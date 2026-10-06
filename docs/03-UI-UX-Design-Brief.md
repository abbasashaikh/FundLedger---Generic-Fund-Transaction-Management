# FundLedger — UI/UX Design Brief

| Item | Value |
|---|---|
| Document | Design Brief v1.0 |
| Derived from | PRD v1.1 §15, §26 · [02-App-Flow](02-App-Flow.md) |
| Date | 06-Oct-2026 |
| Audience | UI designer, frontend engineers |

---

## 1. Design goals

| # | Goal | How we'll know |
|---|---|---|
| G1 | **Trustworthy.** It should feel like a careful ledger, not a playful wallet app. | UAT users describe it as "clear", "official" or "safe" |
| G2 | **Fast entry.** Recording a transaction takes 30 seconds or less on a phone. | Timed task, median ≤ 30 s (PRD §15.3) |
| G3 | **Zero ambiguity about money direction.** In, out and transfer can never be confused. | 0 misreads in a 5-user hallway test of the ledger |
| G4 | **Usable by non-technical volunteers** on low-end Android phones, outdoors, under time pressure. | Works one-handed at 360 px width with sunlight-readable contrast |
| G5 | **Accountability is visible.** "Who did it" is shown on every row and every detail screen. | Creator name is visible without tapping |

### 1.1 Users and context

| Persona | Context | Implications |
|---|---|---|
| **Collector volunteer** (Member) | Standing at a collection counter. Phone in one hand, cash in the other. Patchy network. | Large amount field, numeric keypad, chips instead of dropdowns, offline-tolerant, one-handed reach to the primary button |
| **Treasurer** (Member with transfer/report rights) | Evening reconciliation on a phone or laptop | Ledger filters, account balances, transfers, quick reports |
| **Admin** | Desktop, set-up and oversight | Dense tables, bulk views, audit diffs, settings |

---

## 2. Brand & personality

- **Name:** FundLedger. Wordmark: "Fund" in medium weight and "Ledger" in semibold, set in the UI font. No mascot.
- **Tone:** calm, precise, respectful. Neutral language that suits community and religious organizations. Avoid slang and emojis in UI copy.
- **Generic by design:** no Ijtema-specific imagery or wording anywhere in the product UI (PRD §1). Fund names provide the context.
- **Future white-labelling** (PRD §30): every brand colour comes from a token, and the logo is loaded from org settings.

---

## 3. Design tokens

### 3.1 Colour: semantic roles

The primary colour is a deep teal-green, which suggests trust and money without the "alert" connotation of pure green.

Transaction-type colours are **reserved**: they are never used for decoration.

| Token | Light | Dark | Use |
|---|---|---|---|
| `--color-primary` | `#0F766E` (teal-700) | `#2DD4BF` (teal-400) | Primary buttons, active nav, focus ring |
| `--color-primary-fg` | `#FFFFFF` | `#042F2E` | Text on primary |
| `--color-bg` | `#F8FAFC` | `#0B1220` | App background |
| `--color-surface` | `#FFFFFF` | `#111A2E` | Cards, sheets |
| `--color-surface-muted` | `#F1F5F9` | `#1A2440` | Table stripes, input fills |
| `--color-border` | `#E2E8F0` | `#26324D` | Dividers |
| `--color-text` | `#0F172A` | `#E5E7EB` | Body |
| `--color-text-muted` | `#475569` | `#94A3B8` | Secondary text (≥ 4.5:1 on bg) |
| `--color-in` | `#15803D` (green-700) | `#4ADE80` | **Money In** amounts and icons |
| `--color-in-bg` | `#DCFCE7` | `#052E16` | Money In chip/badge bg |
| `--color-out` | `#B91C1C` (red-700) | `#F87171` | **Money Out** amounts and icons |
| `--color-out-bg` | `#FEE2E2` | `#3B0A0A` | Money Out chip/badge bg |
| `--color-transfer` | `#1D4ED8` (blue-700) | `#60A5FA` | **Transfer** |
| `--color-transfer-bg` | `#DBEAFE` | `#0B1E4A` | |
| `--color-adjust` | `#B45309` (amber-700) | `#FBBF24` | **Adjustment** (Admin) |
| `--color-adjust-bg` | `#FEF3C7` | `#3A2604` | |
| `--color-cancelled` | `#64748B` | `#64748B` | Cancelled (with strike-through) |
| `--color-warning` | `#B45309` | `#FBBF24` | Warnings, sync attention |
| `--color-danger` | `#B91C1C` | `#F87171` | Destructive actions |
| `--color-success` | `#15803D` | `#4ADE80` | Success toast |

**Never rely on colour alone (WCAG 1.4.1).** Every transaction type always carries all three of these:
1. a **sign or glyph**: `+` in, `−` out, `⇄` transfer, `±` adjustment
2. an **icon**: `arrow-down-left`, `arrow-up-right`, `arrows-right-left`, `scale`
3. a **text label** on forms, confirmation sheets and detail screens

Every pair of text colour and background above MUST be verified at ≥ 4.5:1 (body) or ≥ 3:1 (large or UI). This check is automated in CI with a token contrast test.

### 3.2 Typography

| Token | Size / line-height | Weight | Use |
|---|---|---|---|
| `display` | 36/44 | 700 | Balance on dashboard |
| `h1` | 24/32 | 600 | Screen title (desktop) |
| `h2` | 20/28 | 600 | Section titles, mobile screen title |
| `body` | 16/24 | 400 | Default. **Minimum 16 px on inputs**, which also stops iOS from zooming. |
| `body-sm` | 14/20 | 400 | Secondary row text |
| `caption` | 12/16 | 500 | Badges, timestamps |
| `amount-xl` | 40/48 | 700 | Amount input on forms |
| `amount` | 16/24 | 600 | Amounts in lists |

Font rules:
- **Font:** Inter (variable, self-hosted, `font-display: swap`). Fallback stack: `system-ui, "Segoe UI", Roboto, "Noto Sans", sans-serif`.
- **All amounts use `font-variant-numeric: tabular-nums`**, so columns align. Amounts are right-aligned in tables.
- Future languages: add Noto Sans Devanagari (Hindi) and Noto Naskh Arabic (Urdu, RTL) through the same token stack.

### 3.3 Spacing, radius, elevation

- Spacing scale (4 px base): 4, 8, 12, 16, 20, 24, 32, 40, 48.
- Radius: `sm 6` (inputs, chips), `md 10` (cards, buttons), `lg 16` (sheets, dialogs), `full` (FAB, avatars).
- Elevation: cards use a 1 px border and no shadow. Sheets and dialogs use `0 8px 24px rgba(15,23,42,.12)`. Keep it flat and calm.
- Motion: 150–200 ms ease-out for sheets and toasts. Respect `prefers-reduced-motion`. No decorative animation.

### 3.4 Breakpoints

| Name | Width | Layout |
|---|---|---|
| `sm` | < 640 | Single column, bottom nav, full-screen forms |
| `md` | 640–1023 | Single column, wider cards; bottom nav until 768, then collapsed sidebar |
| `lg` | ≥ 1024 | Sidebar + content; forms in a right drawer (480 px) |
| `xl` | ≥ 1440 | Content max-width 1280 px, centred |

The design target is **360 × 740** (common low-end Android). Every screen must work at 320 px without horizontal scroll.

---

## 4. Number, date & text formatting

| Item | Rule | Example |
|---|---|---|
| Currency | Indian digit grouping, `₹` prefix, 2 decimals in detail and tables. Lists drop `.00` when the amount is whole. | `₹1,25,000` · `₹1,25,000.50` |
| Signed amounts | Sign attached, typographic minus `−` | `+ ₹25,000`, `− ₹8,500`, `⇄ ₹20,000` |
| Compact (charts and dashboard tiles only) | Lakh/crore | `₹1.25 L`, `₹2.4 Cr` |
| Amount input | Live grouping as the user types; `inputmode="decimal"`; max 2 decimals; no negatives; paste is sanitized | typing `125000` shows `1,25,000` |
| Date | `dd-MMM-yyyy` by default (org setting) | `05-Oct-2026` |
| Relative | Ledger date headers | `Today`, `Yesterday`, `Mon, 05-Oct` |
| Time | 12-hour with am/pm by default | `10:00 am` |
| Timezone | Always the org timezone (default Asia/Kolkata), never the device's | — |
| Names | Creator's first name in lists, full name in detail | `Ahmed` / `Ahmed Khan` |
| Mobile | Shown masked except to Admins in the user screens | `+91 ••••• •0002` |

All formatting lives in `lib/format/`. Components never call `toLocaleString` directly.

---

## 5. Component library

These components are built on shadcn/ui (Radix) and live in `components/ui`. Each one ships with stories and an axe test.

| Component | Notes |
|---|---|
| `Button` | Variants: primary, secondary, ghost, destructive. Sizes: md (44 px tall) and lg (52 px, form primary). A loading state keeps the width and disables the button. |
| `AmountInput` | Large `amount-xl` with a ₹ prefix and live grouping. The type-colour accent underline (in/out/transfer) is **decorative only**; the form title states the type. |
| `ChipGroup` | Single-select chips for category and payment mode. The 6 most-used appear first, then "More…" opens a searchable sheet. Chips are at least 44 px tall. |
| `AccountPicker` | List with account kind icon, name and **current balance** for the selected fund. |
| `DateTimeField` | Collapsed summary ("Today, 10:00 am · Change") that expands to native date and time inputs. |
| `TxnRow` | Icon, sign and amount, category/route, purpose, creator · time, badges. 64 px tall, the whole row is tappable. |
| `TxnTypeBadge` | Icon + label + colour. |
| `StatusBadge` | Cancelled, Edited, Pending sync, Needs attention, Closed fund. |
| `BalanceCard` | Display balance with in/out sub-totals; skeleton state. |
| `StatTile` | Label + amount + optional delta. |
| `ConfirmSheet` | Bottom sheet on mobile, dialog on desktop. Summarizes the transaction exactly as it will be saved. Primary button text names the action ("Save Money In"). |
| `FilterSheet` / `FilterBar` | Ledger and report filters with active chips. |
| `DataTable` | Desktop tables: sticky header, sortable, column visibility, totals footer, keyboard navigable. Becomes stacked cards below `md`. |
| `EmptyState`, `ErrorState`, `Skeleton` | Consistent placeholders. |
| `Toast` | Success, error, info and offline variants; `aria-live="polite"`. |
| `SyncIndicator` | Top-bar icon + count, with states as in App Flow §4.8. |
| `FundSwitcher` | Sheet (mobile) / popover (desktop) with search, status badges, and recent funds first. |
| `DiffView` | Field-by-field old → new for history and audit. |
| `PermissionToggleGrid` | User × fund permission checkboxes with presets. |
| `AttachmentPicker` | Camera (`capture="environment"`) or file; thumbnail list; client compression; upload progress. |

---

## 6. Key screen specifications

### 6.1 S01/S02 Login

```
┌───────────────────────────┐
│                           │
│        [logo]             │
│       FundLedger          │
│  Sign in with your mobile │
│                           │
│  Mobile number            │
│  ┌────┬────────────────┐  │
│  │+91 │ 98765 43210    │  │
│  └────┴────────────────┘  │
│                           │
│  [      Send OTP       ]  │
│                           │
│  Only registered members  │
│  can sign in.             │
└───────────────────────────┘
```

- The org name and logo appear once known. On a fresh install, a neutral FundLedger mark is shown.
- The OTP screen has 6 boxes with `autocomplete="one-time-code"`, a resend countdown, and an "Edit number" link.

### 6.2 S04 Dashboard (mobile)

```
┌─────────────────────────────────┐
│ Ijtema 2026 ▾          ⟳2   (A) │
├─────────────────────────────────┤
│ ┌─────────────────────────────┐ │
│ │ Current balance             │ │
│ │ ₹31,400                     │ │
│ │ ↙ In ₹25,000   ↗ Out ₹8,500 │ │
│ └─────────────────────────────┘ │
│  Today  + ₹25,000   − ₹8,500    │
│                                 │
│ [+ Money In][− Money Out][⇄ Tr] │
│                                 │
│ ⚠ 2 entries waiting to sync  ›  │
│                                 │
│ Accounts                        │
│  💵 Main Cash          ₹1,400   │
│  🏦 Bank              ₹30,000   │
│                                 │
│ Recent                 View all │
│  ↙ + ₹25,000  Ijtema Collection │
│      Ahmed · 10:00 am           │
│  ↗ − ₹8,500   Food              │
│      Ahmed · 12:30 pm           │
│  ⇄ ₹20,000    Cash → Bank       │
│      Imran · 5:00 pm            │
├─────────────────────────────────┤
│ Home  Ledger  (＋)  Reports More│
└─────────────────────────────────┘
```

### 6.3 S08 Add Money In (mobile)

```
┌─────────────────────────────────┐
│ ✕  Money In · Ijtema 2026       │
├─────────────────────────────────┤
│ Amount                          │
│ ₹ 25,000                        │  ← autofocus, amount-xl, green underline
│                                 │
│ Category                        │
│ (Ijtema Collection)(Donation)   │
│ (Contribution)(Sponsorship) More│
│                                 │
│ Account                         │
│ (💵 Main Cash ₹1,400)(🏦 Bank)  │
│                                 │
│ Payment mode                    │
│ (Cash)(UPI)(Bank)(Cheque)       │
│                                 │
│ Purpose *                       │
│ [Ijtema Collection          ]   │
│ Received from                   │
│ [                           ]   │
│ Today, 10:00 am · Change        │
│ ▸ More details (reference,      │
│   remarks, attachment)          │
├─────────────────────────────────┤
│ [        Review & Save        ] │  ← sticky, thumb zone
└─────────────────────────────────┘
```

The confirmation sheet layout:

```
┌─────────────────────────────────┐
│ Confirm Money In                │
│                                 │
│   + ₹25,000.00                  │
│   Ijtema Collection             │
│ ─────────────────────────────── │
│ Fund       Ijtema 2026          │
│ Account    Main Cash            │
│ Mode       Cash                 │
│ Date       05-Oct-2026 10:00 am │
│ Purpose    Ijtema Collection    │
│ Recorded by  Ahmed (you)        │
│                                 │
│ [ Edit ]   [  Save Money In  ]  │
└─────────────────────────────────┘
```

### 6.4 S06 Ledger (desktop)

| Date ▾ | No. | Type | Category / Route | Purpose | Account | Mode | By | Amount |
|---|---|---|---|---|---|---|---|---|
| 05-Oct 5:00 pm | …000003 | ⇄ Transfer | Main Cash → Bank | Deposit cash | — | — | Imran | ₹20,000 |
| 05-Oct 12:30 pm | …000002 | ↗ Out | Food | Lunch for volunteers | Main Cash | Cash | Ahmed | − ₹8,500 |
| 05-Oct 10:00 am | …000001 | ↙ In | Ijtema Collection | Ijtema Collection | Main Cash | Cash | Ahmed | + ₹25,000 |

- The footer shows filtered totals: In · Out · Net.
- Cancelled rows show a struck-through amount, a "Cancelled" badge and 60 % opacity.

### 6.5 S07 Transaction detail

Sections, top to bottom:
1. Hero: amount and type
2. Details list (2-column on desktop)
3. Attachments strip
4. **Accountability card**, with a distinct surface:
   - "Recorded by Ahmed Khan on 05-Oct-2026 at 10:00 am (synced 10:02 am)"
   - Edits and the cancellation, each with its reason
5. History accordion

### 6.6 Admin screens

The admin screens use the desktop-first `DataTable` with a drawer for forms.

On S19, the user form puts the **Fund access grid** directly below the identity fields. It is the most important admin decision, so it must not be hidden behind a tab.

---

## 7. Interaction principles

1. **Amount first.** Every money form opens with the amount field focused and the keypad up.
2. **Choose, don't type.** Use chips and pickers for category, account and mode. Remember the user's last choices per fund. Suggest recent "Received from" and "Paid to" values.
3. **Confirm money, not navigation.** A confirmation is required for financial saves, cancellations, fund closing, deactivation and settings changes. Never require one for navigation or filters.
4. **Undo is cancellation, not deletion.** The UI never shows a "Delete" control for financial data. The destructive verb is "Cancel transaction", and it requires a reason.
5. **Primary action in the thumb zone.** On mobile, the save button is sticky at the bottom, full width, and at least 52 px tall.
6. **Immediate, honest feedback.** Say "Saved" only after the server confirms. Otherwise say "Saved on this device · will sync". Never show a success state that isn't true.
7. **Show who and when.** The creator and time appear in every list row. Edited and cancelled states are always badged.
8. **Progressive disclosure.** Reference, remarks and attachments sit under "More details". Exception: if the payment mode requires a reference, the field is shown expanded.
9. **Forgiving inputs.** Accept pasted amounts with commas or ₹. Trim whitespace. Accept mobile numbers with spaces.
10. **No dead ends.** Every empty, error or forbidden state names the next step, such as "Contact your Admin" or "Tap ＋ to add".

---

## 8. Accessibility (WCAG 2.2 AA)

- Touch targets are at least **44 × 44 px**, with 8 px spacing between adjacent targets.
- Every input has a visible `<label>`; placeholder text is never used as a label. Required fields are marked with `*` and `aria-required`.
- Errors are linked with `aria-describedby`. On submit, focus moves to the first error.
- Focus rings are always visible: 2 px `--color-primary` with a 2 px offset. Never remove the outline.
- The whole app works by keyboard on desktop. In tables, arrow keys move between rows and Enter opens one. Dialogs trap focus and return it to the trigger when they close.
- Screen-reader text for amounts gives the type in words, e.g. "Money in, 25,000 rupees". The `₹` symbol and sign glyphs are `aria-hidden`, and a visually hidden text equivalent is provided.
- Charts include a data-table alternative ("View as table").
- Respect `prefers-reduced-motion` and `prefers-color-scheme`. Text can be zoomed to 200 % without content being clipped.
- Sunlight readability: minimum body contrast is 7:1 in the light theme for the amount and primary text tokens.

---

## 9. Microcopy

| Context | Copy |
|---|---|
| Form titles | "Money In", "Money Out", "Transfer between accounts", "Adjustment (Admin)" |
| Primary buttons | "Review & Save" → "Save Money In" / "Save Money Out" / "Save Transfer" |
| Offline save | "Saved on this device. It will sync when you're online." |
| Synced | "All entries synced." |
| Needs attention | "This entry couldn't be added: {reason}. Edit or discard it." |
| Closed fund | "{Fund} is closed. New entries can't be added." |
| Edit window | "You can edit this for {n} more minutes." / "Editing window has ended. Ask an Admin to correct it." |
| Cancel dialog | Title "Cancel this transaction?" · Body "It will stay in history and won't count in balances. This can't be undone." · Field "Reason (required)" · Buttons "Keep it" / "Cancel transaction" |
| User limit | "You've reached the limit of 50 active users. Deactivate a user to add another." |
| Empty ledger | "No transactions yet." + "Tap ＋ to record the first one." |
| No funds assigned | "You haven't been added to any fund yet. Please contact your Admin." |

Copy rules:
- Use "Money In" and "Money Out" in the UI, matching the PRD's terminology. Avoid "credit" and "debit", because volunteers misread them.
- Use sentence case everywhere.
- Error messages never blame the user and never expose codes, except a short reference ID on 5xx errors ("Ref: 7F3A…").

---

## 10. PWA specifics

- **Manifest:**
  - `name` "FundLedger", `short_name` "FundLedger"
  - `display: standalone`, `orientation: portrait` (mobile), `theme_color` = primary, `background_color` = bg
  - Maskable icons at 192 and 512
  - Shortcuts: "Money In" → `/new/in`, "Money Out" → `/new/out`
- **Splash/loading:** logo on `--color-bg`, with no spinner for under 300 ms.
- **Update UX:** a non-blocking banner reading "New version available · Refresh". Never auto-reload while a form is dirty.
- **Install:** custom install banner (App Flow §7). On iOS, show a short "Add to Home Screen" guide on request from the More screen.
- **Offline indicator:** the top-bar cloud-off icon plus a one-line banner. The rest of the UI keeps working.

---

## 11. Dark mode

Dark mode is fully supported via tokens (§3.1). It follows the system setting by default and can be overridden in More → Theme.

- Type colours shift to their 400 shades so they keep contrast on dark surfaces.
- Charts read their colours from tokens.

---

## 12. Deliverables expected from design

1. Figma (or Penpot) library mirroring §3 tokens and §5 components, with light and dark modes.
2. High-fidelity mobile (360 px) and desktop (1440 px) frames for S01, S02, S04, S05, S06, S07, S08, S09, S10, S13, S14, S15, S18, S19, S21 and S25.
3. A clickable prototype for the critical path: login, then Money In, confirm and success, then the ledger, then detail.
4. Redlines only for non-standard components (`AmountInput`, `ConfirmSheet`, `TxnRow`, `PermissionToggleGrid`).
5. Token export as CSS variables / Tailwind theme (`tokens.css`).
