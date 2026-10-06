# ADR-0005 — Offline outbox with client UUIDs and server idempotency

- **Status:** Accepted
- **Date:** 06-Oct-2026

## Context

PRD §19 requires:
- a basic offline transaction queue
- a unique client ID per transaction (BR-018)
- no duplicate sync (BR-019)
- no silent overwrites
- the server as source of truth

## Decision

**Client side:**
- Each new transaction gets a UUIDv7 `clientTxnId` on the device.
- It is stored in an IndexedDB `outbox` (Dexie), bound to the `user_id` and `org_id` that created it.

**Scope:**
- Only *creates* of Money In, Money Out and Transfer are allowed offline.
- Edit, cancel, adjustment and admin actions require a connection. This removes the hard conflict cases (edit-vs-edit, edit-vs-cancel) entirely.

**Server side:**
- Online creates and offline sync share the same idempotency path.
- Uniqueness is enforced by a DB constraint, `UNIQUE (organization_id, client_txn_id)`, not by an application check. A race therefore resolves to `DUPLICATE` and returns the existing row.
- Sync is a batch endpoint, but each item is processed in its own DB transaction and gets its own result: `CREATED`, `DUPLICATE`, `REJECTED` (business rule, never auto-retried) or `RETRY` (transient).
- The server re-validates every rule at sync time against current state: fund status, permissions, category and account active, amount limits. Rejected items go to `NEEDS_ATTENTION` for the user to fix or discard. Nothing is forced in.
- Transaction numbers are assigned by the server at sync time.

**Service worker:**
- The service worker never serves API responses from cache. Financial reads are network-only.
- The app keeps its own small read cache (the last 200 transactions per fund) for offline viewing, clearly labelled as possibly out of date.

## Consequences

- Duplicate prevention is guaranteed at the database level and tested with parallel syncs.
- Users may occasionally need to resolve a rejected offline entry. This is a deliberate trade-off against silently accepting data that breaks the rules.
- Offline creation is limited by the device's IndexedDB quota. Attachments are capped at 3 per transaction offline, after client compression.
