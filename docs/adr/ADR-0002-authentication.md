# ADR-0002 — Authentication: managed OTP verification + ASP.NET Core session tokens

- **Status:** **Proposed.** The product owner must choose the OTP provider (TRD Q-01).
- **Date:** 06-Oct-2026

## Context

The PRD asks for:
- Admin-created users only.
- Mobile + OTP login, if a free or practical provider exists (§5.2).
- A configurable fallback such as a PIN (§5.3).

The project's engineering standard says **"never roll your own authentication"** (§2.1). It recommends a managed provider such as Clerk, Supabase Auth or Auth0.

Facts that shape the decision:
- Off-the-shelf managed identity platforms add a second user store, which would have to be kept in sync with FundLedger's admin-provisioned users. Their phone-OTP features are generally on paid tiers. They also don't natively express "only numbers pre-registered by an Admin may sign in", so that rule would need custom hooks anyway.
- In India, application-to-person SMS requires **DLT registration** (entity, sender header, approved template) before an SMS provider will deliver OTPs. Approval takes days to weeks.
- No SMS OTP is permanently free. Provider pricing MUST be verified at decision time (PRD §5.2).

## Decision

FundLedger does **not** implement its own OTP generation, storage or verification, password hashing algorithm, or token cryptography. It composes managed and framework components:

1. **OTP verification is delegated to a managed verification service.** The provider generates, stores, expires and checks the code. FundLedger calls `start(mobile)` and `check(ref, code)` through an `IOtpVerificationProvider` interface.
   - Candidate providers, in no particular order (confirm current pricing, India DLT handling and free tier at decision time):
     - MSG91 OTP
     - Twilio Verify
     - 2Factor.in
     - Firebase Phone Auth, where the client verifies and the API validates the Firebase ID token
   - A `ConsoleOtpProvider` exists for local development only. It is compiled out of Release builds.
2. **Sessions use ASP.NET Core's built-in JWT bearer authentication:**
   - ES256-signed, 15-minute access tokens.
   - Opaque refresh tokens that are stored hashed, rotated on every use, and revoked by family when reuse is detected.
   - Keys come from the secrets store.
3. **The PIN fallback uses ASP.NET Core Identity's `PasswordHasher<T>`** (PBKDF2), plus lockout. No custom hashing.
4. **The Admin-provisioning gate stays in FundLedger:**
   - The API checks that the user exists and is active *before* asking the provider to send an SMS.
   - It responds identically for unknown numbers (no enumeration) and sends nothing to them (no SMS-pumping cost).
5. **Launch default is `auth.method = PIN`** until the chosen OTP provider's DLT approval is complete. Switching to OTP is a settings change, with no schema or user-model change (BR-004).

## Consequences

**Deviation from Standard §2.1, recorded deliberately.**
- What we own: session issuance, rotation and revocation, plus the provisioning gate.
- Mitigations:
  - Only framework primitives are used.
  - Refresh rotation with reuse detection, short-lived access tokens and server-side logout.
  - Integration tests for logout-then-reuse and refresh-token replay.
  - The ZAP scan and the security pass in Phase 5.

Other consequences:
- **MFA (Standard §2.10):** OTP is itself a possession factor. A PIN + OTP combination for Admins is a V1.1 option once OTP is live.
- **Cost exposure:** SMS cost is bounded by the rate limits (TRD TR-013), by sending only to registered numbers, and by billing alerts.

## Alternatives considered

- **Supabase Auth / Clerk / Auth0 phone login.** These satisfy §2.1 literally, but they:
  - duplicate the user store
  - need custom "pre-registered only" hooks
  - add a vendor for 50 users
  - still require DLT for Indian SMS

  This remains a valid alternative if the owner prefers strict §2.1 compliance. The `IOtpVerificationProvider` and session layer would be replaced by validating that provider's JWT.
- **Keycloak (self-hosted).** Strong, but heavy to operate for this scale.
- **WhatsApp OTP.** Out of scope per PRD §3.2 (WhatsApp API automation).
