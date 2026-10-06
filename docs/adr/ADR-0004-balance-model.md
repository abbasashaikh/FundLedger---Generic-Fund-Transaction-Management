# ADR-0004 — Org-level accounts, per-(fund, account) balances, computed not stored

- **Status:** Accepted
- **Date:** 06-Oct-2026

## Context

The PRD defines two balance formulas:
- a fund balance (§12.3), where transfers are excluded
- an account balance (§12.4)

It gives both funds and accounts an opening balance.

The PRD does not say whether accounts belong to one fund. In practice, one physical cash box or bank account often holds money for several funds.

## Decision

1. **Accounts are organization-level.** Each transaction records the fund *and* the account, so balances are always computed per **(fund, account)**.
2. **Opening balances** are stored in `opening_balances(fund_id, account_id, amount)`. The fund opening balance is the **sum** of its rows.
   - This makes the §12.3 and §12.4 formulas consistent by construction: the fund balance is the sum of its account balances.
3. **Balances are computed from ACTIVE transactions via SQL views** (`v_account_movements`, `v_fund_account_balances`, `v_fund_balances`). No running balance is stored.
   - Cancelled transactions are excluded automatically.
   - Transfers produce a − row and a + row for the same fund, so they net to zero at fund level (BR-011).
4. **Transfers are a single row** with `from_account_id` and `to_account_id`, not two linked rows. This matches PRD §24.2 and keeps "one event, one number".
5. **Adjustments** carry an explicit `adjustment_direction` (INCREASE / DECREASE). The amount itself stays positive (BR-005).

## Consequences

- Balances can never drift from the ledger. There is no recalculation job to get wrong.
- Query cost grows with the number of transactions. At V1 scale (thousands to tens of thousands of rows per fund), it is well within targets.
- TRD TR-034 defines the trigger for adding a summary table: if dashboard p95 goes above 500 ms. That table must be maintained transactionally and reconciled nightly.
- Cross-fund transfers ("move ₹X from Medical to Education") are **not** supported in V1. A transfer moves money between accounts within one fund (PRD §10). Two ordinary entries (an expense in one fund and a deposit in the other) remain possible and are clearly labelled. This can be revisited as a future feature.

## Alternatives considered

- **Accounts owned by a fund.** Simpler, but it forces users to create duplicate "Main Cash (Ijtema)" / "Main Cash (Medical)" accounts for the same physical box.
- **Stored running balances.** Faster reads, but there is a risk of drift and race conditions. Not needed at this scale.
- **Full double-entry.** Out of scope (PRD §3.2).
