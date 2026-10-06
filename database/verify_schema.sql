-- =============================================================================
-- FundLedger — schema verification script
-- Seeds the PRD Appendix B example (Ijtema 2026) and asserts the business rules
-- that the database itself is responsible for. Run against a THROWAWAY database:
--
--   docker run --rm -d --name fl-pg -e POSTGRES_PASSWORD=dev -p 55432:5432 postgres:16
--   psql ... -v ON_ERROR_STOP=1 -f database/schema.sql
--   psql ... -v ON_ERROR_STOP=1 -f database/verify_schema.sql
--
-- Every check raises an exception on failure; success prints "ALL CHECKS PASSED".
-- =============================================================================
\set ON_ERROR_STOP 1
SET search_path = fl, public;

-- Login role standing in for the API (member of the runtime role only)
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fl_api_test') THEN
    CREATE ROLE fl_api_test LOGIN PASSWORD 'test' IN ROLE fundledger_app;
  END IF;
END $$;

-- ---- seed as the API role, with tenant context ------------------------------
SET ROLE fl_api_test;
SELECT set_config('app.org_id',  '01900000-0000-7000-8000-000000000001', false),
       set_config('app.user_id', '01900000-0000-7000-8000-0000000000a1', false),
       set_config('app.is_admin','true', false);

INSERT INTO organizations (id, name, short_code) VALUES
  ('01900000-0000-7000-8000-000000000001', 'Al Madad (Example)', 'ALM');

INSERT INTO users (id, organization_id, full_name, mobile_e164, role) VALUES
  ('01900000-0000-7000-8000-0000000000a1', '01900000-0000-7000-8000-000000000001', 'Admin One',  '+919800000001', 'ADMIN'),
  ('01900000-0000-7000-8000-0000000000b1', '01900000-0000-7000-8000-000000000001', 'Ahmed',      '+919800000002', 'MEMBER'),
  ('01900000-0000-7000-8000-0000000000b2', '01900000-0000-7000-8000-000000000001', 'Imran',      '+919800000003', 'MEMBER');

INSERT INTO fund_types (id, organization_id, name) VALUES
  ('01900000-0000-7000-8000-0000000000f0', '01900000-0000-7000-8000-000000000001', 'Event');

INSERT INTO funds (id, organization_id, fund_type_id, code, name, status, created_by) VALUES
  ('01900000-0000-7000-8000-0000000000f1', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f0', 'IJT26', 'Ijtema 2026', 'ACTIVE', '01900000-0000-7000-8000-0000000000a1'),
  ('01900000-0000-7000-8000-0000000000f2', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f0', 'MED',   'Medical Assistance', 'ACTIVE', '01900000-0000-7000-8000-0000000000a1');

-- Ahmed can see Ijtema only; Imran can see Medical only
INSERT INTO user_fund_access (organization_id, user_id, fund_id, can_transfer, granted_by) VALUES
  ('01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000b1', '01900000-0000-7000-8000-0000000000f1', true, '01900000-0000-7000-8000-0000000000a1'),
  ('01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000b2', '01900000-0000-7000-8000-0000000000f2', true, '01900000-0000-7000-8000-0000000000a1');

INSERT INTO accounts (id, organization_id, name, kind, created_by) VALUES
  ('01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-000000000001', 'Main Cash', 'CASH', '01900000-0000-7000-8000-0000000000a1'),
  ('01900000-0000-7000-8000-0000000000c2', '01900000-0000-7000-8000-000000000001', 'Bank',      'BANK', '01900000-0000-7000-8000-0000000000a1');

INSERT INTO payment_modes (id, organization_id, name) VALUES
  ('01900000-0000-7000-8000-0000000000d1', '01900000-0000-7000-8000-000000000001', 'Cash'),
  ('01900000-0000-7000-8000-0000000000d2', '01900000-0000-7000-8000-000000000001', 'UPI');

INSERT INTO categories (id, organization_id, direction, name, created_by) VALUES
  ('01900000-0000-7000-8000-0000000000e1', '01900000-0000-7000-8000-000000000001', 'MONEY_IN',  'Ijtema Collection', '01900000-0000-7000-8000-0000000000a1'),
  ('01900000-0000-7000-8000-0000000000e2', '01900000-0000-7000-8000-000000000001', 'MONEY_OUT', 'Food',              '01900000-0000-7000-8000-0000000000a1');

INSERT INTO opening_balances (organization_id, fund_id, account_id, amount, as_of_date, set_by) VALUES
  ('01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1', '01900000-0000-7000-8000-0000000000c1', 5000.00, '2026-10-01', '01900000-0000-7000-8000-0000000000a1'),
  ('01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1', '01900000-0000-7000-8000-0000000000c2', 10000.00, '2026-10-01', '01900000-0000-7000-8000-0000000000a1');

-- PRD §13 example ledger for 05-Oct-2026 + one adjustment + one cancelled txn
INSERT INTO transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time,
                          category_id, account_id, from_account_id, to_account_id, adjustment_direction,
                          payment_mode_id, purpose, client_txn_id, created_by) VALUES
  ('01900000-0000-7000-8000-000000001001', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1',
   'IJT26-2026-27-000001', 'DEPOSIT', 25000, '2026-10-05', '10:00', '01900000-0000-7000-8000-0000000000e1',
   '01900000-0000-7000-8000-0000000000c1', NULL, NULL, NULL, '01900000-0000-7000-8000-0000000000d1', 'Ijtema Collection', '01900000-0000-7000-8000-00000000c001', '01900000-0000-7000-8000-0000000000b1'),
  ('01900000-0000-7000-8000-000000001002', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1',
   'IJT26-2026-27-000002', 'EXPENSE', 8500, '2026-10-05', '12:30', '01900000-0000-7000-8000-0000000000e2',
   '01900000-0000-7000-8000-0000000000c1', NULL, NULL, NULL, '01900000-0000-7000-8000-0000000000d1', 'Lunch for volunteers', NULL, '01900000-0000-7000-8000-0000000000b1'),
  ('01900000-0000-7000-8000-000000001003', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1',
   'IJT26-2026-27-000003', 'TRANSFER', 20000, '2026-10-05', '17:00', NULL,
   NULL, '01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-0000000000c2', NULL, NULL, 'Deposit cash to bank', NULL, '01900000-0000-7000-8000-0000000000b1'),
  ('01900000-0000-7000-8000-000000001004', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1',
   'IJT26-2026-27-000004', 'ADJUSTMENT', 100, '2026-10-05', '18:00', NULL,
   '01900000-0000-7000-8000-0000000000c1', NULL, NULL, 'DECREASE', NULL, 'Cash count shortfall after reconciliation', NULL, '01900000-0000-7000-8000-0000000000a1'),
  ('01900000-0000-7000-8000-000000001005', '01900000-0000-7000-8000-000000000001', '01900000-0000-7000-8000-0000000000f1',
   'IJT26-2026-27-000005', 'DEPOSIT', 99999, '2026-10-05', '18:30', '01900000-0000-7000-8000-0000000000e1',
   '01900000-0000-7000-8000-0000000000c1', NULL, NULL, NULL, '01900000-0000-7000-8000-0000000000d1', 'Typo entry', NULL, '01900000-0000-7000-8000-0000000000b1');

UPDATE transactions SET status = 'CANCELLED', cancelled_at = now(),
       cancelled_by = '01900000-0000-7000-8000-0000000000a1', cancellation_reason = 'Wrong amount entered'
 WHERE id = '01900000-0000-7000-8000-000000001005';

-- ---- assertions ---------------------------------------------------------------
DO $$
DECLARE r record; n int; ok boolean;
BEGIN
  -- Balances (PRD §12.3/§12.4)
  -- Cash: 5000 + 25000 - 8500 - 20000 - 100 = 1400 ; Bank: 10000 + 20000 = 30000
  SELECT closing_balance INTO r FROM v_fund_account_balances WHERE account_id = '01900000-0000-7000-8000-0000000000c1';
  IF r.closing_balance <> 1400 THEN RAISE EXCEPTION 'cash balance expected 1400, got %', r.closing_balance; END IF;
  SELECT closing_balance INTO r FROM v_fund_account_balances WHERE account_id = '01900000-0000-7000-8000-0000000000c2';
  IF r.closing_balance <> 30000 THEN RAISE EXCEPTION 'bank balance expected 30000, got %', r.closing_balance; END IF;
  -- Fund: 15000 + 25000 - 8500 - 100 = 31400 (transfer + cancelled excluded)
  SELECT * INTO r FROM v_fund_balances WHERE fund_id = '01900000-0000-7000-8000-0000000000f1';
  IF r.closing_balance <> 31400 OR r.money_in <> 25000 OR r.money_out <> 8500 OR r.adjustments_net <> -100 THEN
    RAISE EXCEPTION 'fund balance wrong: %', row_to_json(r);
  END IF;
  RAISE NOTICE 'ok: balances (fund 31400, cash 1400, bank 30000)';

  -- BR-005 amount > 0
  BEGIN
    INSERT INTO transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time, category_id, account_id, payment_mode_id, purpose, created_by)
    VALUES (gen_random_uuid(), current_org_id(), '01900000-0000-7000-8000-0000000000f1', 'X1', 'DEPOSIT', 0, '2026-10-05', '10:00',
            '01900000-0000-7000-8000-0000000000e1', '01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-0000000000d1', 'x', current_user_id());
    RAISE EXCEPTION 'BR-005 not enforced';
  EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: BR-005 amount > 0';
  END;

  -- BR-010 transfer same account
  BEGIN
    INSERT INTO transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time, from_account_id, to_account_id, purpose, created_by)
    VALUES (gen_random_uuid(), current_org_id(), '01900000-0000-7000-8000-0000000000f1', 'X2', 'TRANSFER', 10, '2026-10-05', '10:00',
            '01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-0000000000c1', 'x', current_user_id());
    RAISE EXCEPTION 'BR-010 not enforced';
  EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: BR-010 transfer source <> destination';
  END;

  -- BR-008 expense purpose
  BEGIN
    INSERT INTO transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time, category_id, account_id, payment_mode_id, purpose, created_by)
    VALUES (gen_random_uuid(), current_org_id(), '01900000-0000-7000-8000-0000000000f1', 'X3', 'EXPENSE', 10, '2026-10-05', '10:00',
            '01900000-0000-7000-8000-0000000000e2', '01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-0000000000d1', '  ', current_user_id());
    RAISE EXCEPTION 'BR-008 not enforced';
  EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: BR-008 expense requires purpose';
  END;

  -- BR-013 cancel requires reason
  BEGIN
    UPDATE transactions SET status = 'CANCELLED', cancelled_at = now(), cancelled_by = current_user_id()
     WHERE id = '01900000-0000-7000-8000-000000001001';
    RAISE EXCEPTION 'BR-013 not enforced';
  EXCEPTION WHEN check_violation THEN RAISE NOTICE 'ok: BR-013 cancellation requires reason';
  END;

  -- Cancelled is terminal
  BEGIN
    UPDATE transactions SET remarks = 'edit after cancel' WHERE id = '01900000-0000-7000-8000-000000001005';
    RAISE EXCEPTION 'cancelled txn was editable';
  EXCEPTION WHEN raise_exception THEN
    IF SQLERRM <> 'TXN_CANCELLED_IMMUTABLE' THEN RAISE; END IF;
    RAISE NOTICE 'ok: cancelled transaction is immutable';
  END;

  -- Edit bumps revision
  UPDATE transactions SET remarks = 'corrected' WHERE id = '01900000-0000-7000-8000-000000001002';
  SELECT revision INTO n FROM transactions WHERE id = '01900000-0000-7000-8000-000000001002';
  IF n <> 2 THEN RAISE EXCEPTION 'revision expected 2, got %', n; END IF;
  RAISE NOTICE 'ok: edit increments revision';

  -- BR-019 duplicate client id
  BEGIN
    INSERT INTO transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time, category_id, account_id, payment_mode_id, purpose, client_txn_id, created_by)
    VALUES (gen_random_uuid(), current_org_id(), '01900000-0000-7000-8000-0000000000f1', 'X4', 'DEPOSIT', 10, '2026-10-05', '10:00',
            '01900000-0000-7000-8000-0000000000e1', '01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-0000000000d1', 'dup', '01900000-0000-7000-8000-00000000c001', current_user_id());
    RAISE EXCEPTION 'BR-019 not enforced';
  EXCEPTION WHEN unique_violation THEN RAISE NOTICE 'ok: BR-019 duplicate client_txn_id rejected';
  END;

  -- BR-012 no hard delete (privilege)
  BEGIN
    DELETE FROM transactions WHERE id = '01900000-0000-7000-8000-000000001001';
    RAISE EXCEPTION 'BR-012 not enforced';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'ok: BR-012 app role cannot DELETE transactions';
  END;

  -- Audit log append-only
  INSERT INTO audit_logs (organization_id, user_id, action, entity_type, entity_id)
  VALUES (current_org_id(), current_user_id(), 'TXN_CREATED', 'Transaction', '1001');
  BEGIN
    UPDATE audit_logs SET action = 'TAMPERED';
    RAISE EXCEPTION 'audit log was updatable';
  EXCEPTION WHEN insufficient_privilege OR raise_exception THEN RAISE NOTICE 'ok: audit log is append-only';
  END;

  -- BR-017 closed fund rejects new transactions
  UPDATE funds SET status = 'CLOSED', closed_at = now(), closed_by = current_user_id()
   WHERE id = '01900000-0000-7000-8000-0000000000f2';
  BEGIN
    INSERT INTO transactions (id, organization_id, fund_id, txn_number, txn_type, amount, txn_date, txn_time, category_id, account_id, payment_mode_id, purpose, created_by)
    VALUES (gen_random_uuid(), current_org_id(), '01900000-0000-7000-8000-0000000000f2', 'X5', 'DEPOSIT', 10, '2026-10-05', '10:00',
            '01900000-0000-7000-8000-0000000000e1', '01900000-0000-7000-8000-0000000000c1', '01900000-0000-7000-8000-0000000000d1', 'x', current_user_id());
    RAISE EXCEPTION 'BR-017 not enforced';
  EXCEPTION WHEN raise_exception THEN
    IF SQLERRM <> 'FUND_NOT_ACTIVE' THEN RAISE; END IF;
    RAISE NOTICE 'ok: BR-017 closed fund rejects transactions';
  END;
  UPDATE funds SET status = 'ACTIVE', closed_at = NULL, closed_by = NULL
   WHERE id = '01900000-0000-7000-8000-0000000000f2';
END $$;

-- ---- RLS: member sees only assigned fund ------------------------------------
SELECT set_config('app.user_id', '01900000-0000-7000-8000-0000000000b2', false),   -- Imran (Medical only)
       set_config('app.is_admin', 'false', false);
DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM fl.transactions;
  IF n <> 0 THEN RAISE EXCEPTION 'RLS leak: Imran sees % Ijtema transactions', n; END IF;
  SELECT count(*) INTO n FROM fl.funds;
  IF n <> 1 THEN RAISE EXCEPTION 'RLS leak: Imran sees % funds', n; END IF;
  SELECT count(*) INTO n FROM fl.audit_logs;
  IF n <> 0 THEN RAISE EXCEPTION 'RLS leak: member can read audit log'; END IF;
  RAISE NOTICE 'ok: RLS fund-level isolation for members + audit log admin-only';
END $$;

-- ---- RLS: other tenant sees nothing -------------------------------------------
SELECT set_config('app.org_id',  '01900000-0000-7000-8000-000000000999', false),
       set_config('app.is_admin', 'true', false);
DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM fl.transactions;
  IF n <> 0 THEN RAISE EXCEPTION 'RLS leak across tenants: % rows', n; END IF;
  SELECT count(*) INTO n FROM fl.users;
  IF n <> 0 THEN RAISE EXCEPTION 'RLS leak across tenants (users)'; END IF;
  RAISE NOTICE 'ok: RLS tenant isolation';
END $$;

-- ---- pre-auth lookup works without tenant context -----------------------------
SELECT set_config('app.org_id', '', false), set_config('app.user_id', '', false), set_config('app.is_admin', '', false);
DO $$
DECLARE n int;
BEGIN
  SELECT count(*) INTO n FROM fl.users;
  IF n <> 0 THEN RAISE EXCEPTION 'users visible without tenant context'; END IF;
  SELECT count(*) INTO n FROM fl.auth_find_user_by_mobile('+919800000002') WHERE pin_must_change;
  IF n <> 1 THEN RAISE EXCEPTION 'auth lookup failed: % rows', n; END IF;
  RAISE NOTICE 'ok: pre-auth lookup via definer function only (new user must change PIN)';

  -- PIN login attempts: recorded for unknown numbers too; append-only for the app role
  INSERT INTO fl.login_attempts (mobile_e164, succeeded, failure_code)
  VALUES ('+919999999999', false, 'UNKNOWN'), ('+919800000002', false, 'BAD_PIN');
  SELECT count(*) INTO n FROM fl.login_attempts WHERE NOT succeeded;
  IF n <> 2 THEN RAISE EXCEPTION 'login_attempts insert failed'; END IF;
  BEGIN
    UPDATE fl.login_attempts SET succeeded = true;
    RAISE EXCEPTION 'login_attempts was updatable by app role';
  EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE 'ok: login_attempts insert-only for app role';
  END;
END $$;

-- ---- BR-001 active user limit ---------------------------------------------------
RESET ROLE;
UPDATE fl.organizations SET max_active_users = 3 WHERE id = '01900000-0000-7000-8000-000000000001';
SET ROLE fl_api_test;
SELECT set_config('app.org_id',  '01900000-0000-7000-8000-000000000001', false),
       set_config('app.user_id', '01900000-0000-7000-8000-0000000000a1', false),
       set_config('app.is_admin','true', false);
DO $$
BEGIN
  BEGIN
    INSERT INTO fl.users (id, organization_id, full_name, mobile_e164)
    VALUES (gen_random_uuid(), current_org_id(), 'Fourth', '+919800000004');
    RAISE EXCEPTION 'BR-001 not enforced';
  EXCEPTION WHEN raise_exception THEN
    IF SQLERRM <> 'ACTIVE_USER_LIMIT_REACHED' THEN RAISE; END IF;
  END;
  -- inactive user may still be created
  INSERT INTO fl.users (id, organization_id, full_name, mobile_e164, status)
  VALUES (gen_random_uuid(), current_org_id(), 'Fourth', '+919800000004', 'INACTIVE');
  RAISE NOTICE 'ok: BR-001 active user limit (inactive users allowed)';
END $$;

RESET ROLE;
\echo 'ALL CHECKS PASSED'
