-- Phase 3: cancelling a transaction is a revision too (its own history entry).
-- Replaces fl.guard_transactions(); the trigger itself is unchanged.
CREATE OR REPLACE FUNCTION fl.guard_transactions() RETURNS trigger
  LANGUAGE plpgsql AS
$$
DECLARE v_fund_status fl.fund_status;
BEGIN
  IF TG_OP = 'INSERT' THEN
    SELECT status INTO v_fund_status FROM fl.funds WHERE id = NEW.fund_id;
    IF v_fund_status IS DISTINCT FROM 'ACTIVE' THEN
      RAISE EXCEPTION 'FUND_NOT_ACTIVE' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
  END IF;

  -- UPDATE
  IF OLD.status = 'CANCELLED' THEN
    RAISE EXCEPTION 'TXN_CANCELLED_IMMUTABLE' USING ERRCODE = 'P0001';
  END IF;
  IF NEW.organization_id <> OLD.organization_id OR NEW.fund_id <> OLD.fund_id
     OR NEW.txn_type <> OLD.txn_type OR NEW.txn_number <> OLD.txn_number
     OR NEW.created_by <> OLD.created_by OR NEW.created_at <> OLD.created_at
     OR NEW.client_txn_id IS DISTINCT FROM OLD.client_txn_id THEN
    RAISE EXCEPTION 'TXN_IMMUTABLE_FIELD' USING ERRCODE = 'P0001';
  END IF;
  SELECT status INTO v_fund_status FROM fl.funds WHERE id = NEW.fund_id;
  IF v_fund_status IS DISTINCT FROM 'ACTIVE' THEN
    RAISE EXCEPTION 'FUND_NOT_ACTIVE' USING ERRCODE = 'P0001';
  END IF;
  -- Every change (edit OR cancellation) is a new revision, so each has its own history entry.
  NEW.revision := OLD.revision + 1;
  NEW.updated_at := now();
  RETURN NEW;
END
$$;
