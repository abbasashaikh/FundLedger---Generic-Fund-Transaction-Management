# ADR-0002 — Authentication: mobile number + PIN with ASP.NET Core session tokens

- **Status:** Accepted, 07-Oct-2026 (product owner decision: "OTP not required, use PIN")
- **Date:** 06-Oct-2026 (proposed) · 07-Oct-2026 (accepted)

## Context

The PRD asks for:
- Admin-created users only.
- Mobile + OTP login if a free or practical provider exists (§5.2).
- Otherwise, a configurable fallback such as a PIN (§5.3, BR-004).

Facts that shaped the decision:
- In India, SMS OTP requires DLT registration (entity, sender header, approved template), which takes days to weeks.
- No SMS OTP is permanently free.

**The product owner decided that OTP is not required for V1. Login is mobile number + PIN.**

The project's engineering standard says "never roll your own authentication" (§2.1).

## Decision

**1. Login is mobile number + 6-digit PIN.** There is no SMS, no OTP provider and no third-party identity service in V1.

**2. User provisioning:**
- The Admin creates the user and sets a **temporary PIN**, which the Admin shares with the user in person or by phone.
- At first login, the user must choose their own PIN (`pin_must_change`).
- A forgotten PIN is reset by an Admin. There is no self-service reset, because there is no verified second channel.

**3. No custom cryptography:**
- PINs are hashed with ASP.NET Core Identity's `PasswordHasher<T>` (PBKDF2 with a per-hash salt, with versioned rehash on login).
- Sessions use ASP.NET Core's built-in JWT bearer authentication: ES256-signed, 15-minute access tokens.
- Opaque refresh tokens are stored hashed, rotated on every use, and revoked by family when reuse is detected. Keys come from the secrets store.

**4. Brute-force protection.** A 6-digit PIN has only 10⁶ combinations, so this is the main control:

| Control | Rule |
|---|---|
| Per mobile | Lockout after `auth.pin_max_failures` (default 5) failures within `auth.pin_lockout_minutes` (default 15). Counted from `login_attempts`, which records **every** attempt including unknown numbers, so the lockout does not reveal whether a number is registered. |
| Per IP | 20 attempts per 15 min (ASP.NET Core rate limiter + reverse proxy) |
| Weak PINs | Rejected at PIN change: all-same digits, straight sequences (`123456`, `654321`), the last 6 digits of the user's own mobile, and a small denylist of common PINs (`000000`, `111111`, `121212`, `112233`, …) |
| Response | Generic for every failure: "Mobile number or PIN is incorrect." When locked: "Too many attempts. Try again in 15 minutes." |
| Audit | `LOGIN`, `LOGIN_FAILED`, `ACCOUNT_LOCKED`, `PIN_CHANGED`, `PIN_RESET` are audited |

**5. Admins set their own PIN at first login, like everyone else.**

**6. Future option.** The login endpoint is a strategy behind `IAuthMethod`. OTP or a managed identity provider can be added later without changing the user, organization, fund or transaction model (PRD §5.3). The `auth_method` enum gets a new value at that time.

## Consequences

**Deviation from Standard §2.1, recorded deliberately.**
- What we own: the PIN check, lockout and session handling.
- Mitigations:
  - Only framework primitives are used (PasswordHasher, JWT bearer, Data Protection).
  - Lockout and rate limits as above.
  - Integration tests for brute-force lockout, logout-then-reuse and refresh-token replay.
  - The OWASP ZAP scan and the security pass in Phase 5.

**MFA (Standard §2.10).** V1 has a single factor: something you know (the PIN). Possession of the registered phone is not verified.
- Residual risk: a shared or observed PIN lets someone else log in as that user.
- Mitigations:
  - Every action is attributed and audited.
  - Admins can see active sessions and revoke them.
  - Sessions expire after 8 h idle.
  - Deactivation is immediate.
- Revisit by adding OTP or a passkey (WebAuthn) for Admins if the organization handles larger sums or adds members it doesn't know personally.

**Cost.** Zero SMS cost. No vendor dependency for login.

## Alternatives considered

- **SMS OTP via a managed verification service** (MSG91 / Twilio Verify / 2Factor / Firebase). Rejected for V1 by the product owner because of DLT effort and cost. It remains the natural upgrade path.
- **Supabase Auth / Clerk / Auth0.** These would duplicate the user store and need custom "pre-registered only" hooks. Too heavy for 50 users.
- **Passkeys (WebAuthn).** Strong and phishing-resistant, but device-bound credentials make Admin-led recovery harder for volunteers who change phones. A candidate for Admin accounts in V1.1.
