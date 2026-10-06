# ADR-0007 — Money In receipts (PDF + share) in V1

- **Status:** Accepted, 07-Oct-2026 (product owner decision on TRD Q-09: "yes")
- **Date:** 07-Oct-2026

## Context

The product owner wants a receipt for Money In transactions in V1: a PDF that can be shared with the person who gave the money.

PRD §30 lists "WhatsApp receipt/report sharing" as a future enhancement, and §3.2 excludes WhatsApp API automation. Two things need deciding:
- how to provide receipts without pulling those out-of-scope items in
- how receipts stay consistent with the edit and cancel rules

## Decision

**1. One receipt per DEPOSIT transaction.**
- The **receipt number is the transaction number**. There is no second numbering sequence, so a receipt can always be traced to exactly one ledger entry.
- Receipts are not available for expenses, transfers or adjustments.

**2. Generated server-side, on demand.**
- Endpoint: `GET /api/v1/transactions/{id}/receipt.pdf`, generated with QuestPDF (already used for exports).
- The receipt is never stored, so it always reflects the transaction's **current** revision and status.
- This is a deliberate, bounded exception to Standard §9.1 (no synchronous file generation): the receipt is one A5 page, rendered in roughly 100 ms. Exports remain asynchronous. If p95 receipt latency goes above 1 s, move receipts to the export job pipeline.

**3. Receipt content:**

| Section | Content |
|---|---|
| Header | Organization name, address and registration number (if set); the title "Receipt" |
| Identity | Receipt no. (= transaction number); receipt date (the transaction date) |
| Body | Received from; amount in figures (`₹25,000.00`) and **in words, Indian system** ("Rupees Twenty-Five Thousand Only"); fund; category; purpose; payment mode and reference |
| Footer | "Recorded by {name}" (configurable); "Computer-generated receipt. No signature required." (configurable footer text); generated-at timestamp; a short verification code (first 8 characters of an HMAC of transaction ID + revision) |
| States | Edited transaction: a "Revised (rev n)" note. **Cancelled transaction: a large diagonal "CANCELLED" watermark** plus the cancellation date. The receipt is still downloadable for the record. |

**4. Sharing:**
- The PWA uses the **Web Share API with a file** (`navigator.share({ files: [pdf] })`). This opens the phone's share sheet, where the user chooses WhatsApp, SMS, email and so on.
- Desktop browsers, and phones without file sharing, fall back to download.
- This is user-initiated manual sharing, **not** WhatsApp API automation, so PRD §3.2 still holds.

**5. Access:**
- Anyone who can view the transaction can generate its receipt, with the same RLS and permission checks as the transaction detail.
- A Member without `can_view_all_txns` can only get receipts for their own entries.

**6. Offline.** A queued entry has no transaction number yet, so "Receipt" is disabled with the label "Available after sync".

**7. Audit.** Each generation writes `RECEIPT_GENERATED` with the transaction ID and revision.

**8. Settings:**
- `receipt.enabled` (default true)
- `receipt.footer_text`
- `receipt.show_recorded_by` (default true)
- `organizations.registration_number` (new column) is printed when set.

**9. Not a tax receipt.** The receipt is not an 80G or other tax-exemption certificate, because tax accounting is out of scope (PRD §3.2). The footer must not claim tax benefits.

## Consequences

- One new endpoint, one new UI action on S07 and the post-save success screen, and Phase 4 gains task P4-07.
- The amount-in-words function needs Indian-system unit tests: lakh, crore, paise, and edge cases like ₹1,00,000.50.
- Because receipts are never stored, there is no stale or deleted-receipt problem, but a receipt someone shared earlier may differ from a later one after an Admin edit. The "Revised (rev n)" marker and the verification code make this visible.

## Alternatives considered

- **Client-side PDF (pdf-lib).** Would work offline, but there is no transaction number offline, and layout and fonts (₹ glyph, future Hindi/Urdu) are harder to keep consistent. Rejected.
- **Separate receipt numbering.** Adds a sequence to manage and a mapping to audit, with no benefit. Rejected.
- **Stored receipt files.** Would need invalidation on every edit or cancel. Rejected.
