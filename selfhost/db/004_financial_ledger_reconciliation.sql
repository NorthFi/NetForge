-- root/selfhost/db/004_financial_ledger_reconciliation.sql
-- Apply once with the versioned migration runner. All amounts are ZAR decimal(12,2).
-- No updates/deletes to posted journal rows: correct via a reversing journal.
CREATE TABLE IF NOT EXISTS ledger_journals (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 source_type varchar(32) NOT NULL,
 source_id uuid NOT NULL,
 memo varchar(300) NOT NULL,
 posted_by varchar(100) NOT NULL,
 posted_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(source_type,source_id)
);
CREATE TABLE IF NOT EXISTS ledger_lines (
 journal_id uuid NOT NULL REFERENCES ledger_journals(id) ON DELETE RESTRICT,
 line_no smallint NOT NULL CHECK(line_no BETWEEN 1 AND 32),
 account_code varchar(24) NOT NULL
   CHECK(account_code IN ('AR','REVENUE','BANK_CLEARING','REVENUE_RETURNS')),
 debit numeric(14,2) NOT NULL DEFAULT 0 CHECK(debit>=0),
 credit numeric(14,2) NOT NULL DEFAULT 0 CHECK(credit>=0),
 PRIMARY KEY(journal_id,line_no),
 CHECK((debit>0 AND credit=0) OR (credit>0 AND debit=0))
);
CREATE INDEX IF NOT EXISTS idx_ledger_lines_account ON ledger_lines(account_code);

CREATE OR REPLACE FUNCTION netforge_validate_journal() RETURNS trigger
LANGUAGE plpgsql AS $journal_check$
DECLARE total_debit numeric(14,2); total_credit numeric(14,2); line_count integer;
BEGIN
 SELECT COALESCE(sum(debit),0),COALESCE(sum(credit),0),count(*)
 INTO total_debit,total_credit,line_count
 FROM ledger_lines WHERE journal_id=NEW.journal_id;
 IF line_count<2 OR total_debit<>total_credit THEN
   RAISE EXCEPTION 'Unbalanced ledger journal % (debit %, credit %, lines %)',
     NEW.journal_id,total_debit,total_credit,line_count;
 END IF;
 RETURN NULL;
END
$journal_check$;
CREATE CONSTRAINT TRIGGER netforge_journal_balanced
AFTER INSERT ON ledger_lines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
EXECUTE FUNCTION netforge_validate_journal();

CREATE OR REPLACE FUNCTION netforge_ledger_immutable() RETURNS trigger
LANGUAGE plpgsql AS $immutable$
BEGIN
 RAISE EXCEPTION 'Posted journal entries are immutable; create a reversal';
END
$immutable$;
CREATE TRIGGER netforge_journal_no_edit
BEFORE UPDATE OR DELETE ON ledger_journals FOR EACH ROW
EXECUTE FUNCTION netforge_ledger_immutable();
CREATE TRIGGER netforge_lines_no_edit
BEFORE UPDATE OR DELETE ON ledger_lines FOR EACH ROW
EXECUTE FUNCTION netforge_ledger_immutable();

CREATE TABLE IF NOT EXISTS bank_import_batches (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 bank_name varchar(100) NOT NULL,
 filename varchar(180) NOT NULL,
 imported_by varchar(100) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS bank_statement_lines (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 batch_id uuid NOT NULL REFERENCES bank_import_batches(id) ON DELETE RESTRICT,
 bank_name varchar(100) NOT NULL,
 transaction_id varchar(120) NOT NULL,
 value_date date NOT NULL,
 amount numeric(12,2) NOT NULL CHECK(amount<>0),
 description varchar(400) NOT NULL DEFAULT '',
 reference varchar(200) NOT NULL DEFAULT '',
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(bank_name,transaction_id)
);
CREATE INDEX IF NOT EXISTS idx_bank_lines_date ON bank_statement_lines(value_date DESC);

ALTER TABLE payments ADD COLUMN IF NOT EXISTS request_id uuid UNIQUE;
ALTER TABLE payments ADD COLUMN IF NOT EXISTS bank_statement_line_id uuid
 REFERENCES bank_statement_lines(id) ON DELETE RESTRICT;

CREATE TABLE IF NOT EXISTS payment_reversals (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 payment_id uuid NOT NULL UNIQUE REFERENCES payments(id) ON DELETE RESTRICT,
 request_id uuid NOT NULL UNIQUE,
 reason varchar(300) NOT NULL,
 reversed_by varchar(100) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS credit_notes (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE RESTRICT,
 number varchar(120) NOT NULL UNIQUE,
 amount numeric(12,2) NOT NULL CHECK(amount>0),
 reason varchar(300) NOT NULL,
 created_by varchar(100) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_credit_notes_invoice ON credit_notes(invoice_id);

-- A paid invoice that is later reversed needs to re-open; invoices that have
-- been satisfied partly via credits use 'settled' instead of claiming all
-- funds arrived through the bank.
ALTER TABLE invoices DROP CONSTRAINT IF EXISTS invoices_status_check;
ALTER TABLE invoices ADD CONSTRAINT invoices_status_check
 CHECK(status IN ('draft','issued','paid','settled','void'));

CREATE OR REPLACE VIEW invoice_balances AS
SELECT i.id AS invoice_id,i.customer_id,i.total,i.status,
  COALESCE(p.paid,0)::numeric(14,2) AS paid,
  COALESCE(c.credited,0)::numeric(14,2) AS credited,
  GREATEST(i.total-COALESCE(p.paid,0)-COALESCE(c.credited,0),0)::numeric(14,2)
    AS outstanding
FROM invoices i
LEFT JOIN LATERAL (
 SELECT sum(x.amount) AS paid FROM payments x
 LEFT JOIN payment_reversals r ON r.payment_id=x.id
 WHERE x.invoice_id=i.id AND r.id IS NULL
) p ON true
LEFT JOIN LATERAL (
 SELECT sum(x.amount) AS credited FROM credit_notes x WHERE x.invoice_id=i.id
) c ON true;

-- One-time legacy postings: preserve prior invoices/payments without inventing
-- date-specific ledger entries for pre-upgrade events.
DO $ledger_backfill$
DECLARE invoice_record record; payment_record record; journal_id uuid;
BEGIN
 FOR invoice_record IN SELECT id,number,total,status FROM invoices
   WHERE status IN ('issued','paid','settled') AND total>0
 LOOP
   journal_id:=NULL;
   INSERT INTO ledger_journals(source_type,source_id,memo,posted_by)
   VALUES('legacy_invoice',invoice_record.id,
          'Opening ledger from existing invoice '||invoice_record.number,'migration')
   ON CONFLICT(source_type,source_id) DO NOTHING RETURNING id INTO journal_id;
   IF journal_id IS NOT NULL THEN
      INSERT INTO ledger_lines(journal_id,line_no,account_code,debit,credit) VALUES
       (journal_id,1,'AR',invoice_record.total,0),
       (journal_id,2,'REVENUE',0,invoice_record.total);
   END IF;
 END LOOP;
 FOR payment_record IN SELECT id,invoice_id,amount FROM payments LOOP
   journal_id:=NULL;
   INSERT INTO ledger_journals(source_type,source_id,memo,posted_by)
   VALUES('legacy_payment',payment_record.id,
          'Opening ledger from existing recorded payment','migration')
   ON CONFLICT(source_type,source_id) DO NOTHING RETURNING id INTO journal_id;
   IF journal_id IS NOT NULL THEN
      INSERT INTO ledger_lines(journal_id,line_no,account_code,debit,credit) VALUES
       (journal_id,1,'BANK_CLEARING',payment_record.amount,0),
       (journal_id,2,'AR',0,payment_record.amount);
   END IF;
 END LOOP;
END
$ledger_backfill$;
