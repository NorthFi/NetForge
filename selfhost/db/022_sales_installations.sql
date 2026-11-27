-- root/selfhost/db/022_sales_installations.sql
-- Commercial acceptance is staff-attested evidence, not a digital signature.
-- Prices and customer details are frozen in the quotation, independently of
-- subsequent package updates. No invoice or active service is created.
CREATE TABLE sales_leads(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 number bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
 first_name varchar(120) NOT NULL,
 surname varchar(120) NOT NULL DEFAULT '',
 email varchar(254) NOT NULL DEFAULT '',
 phone varchar(70) NOT NULL DEFAULT '',
 address varchar(500) NOT NULL,
 branch_id uuid NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
 source varchar(80) NOT NULL DEFAULT '',
 notes varchar(2000) NOT NULL DEFAULT '',
 state varchar(16) NOT NULL DEFAULT 'new' CHECK(state IN ('new','qualified','lost','converted')),
 customer_id uuid UNIQUE REFERENCES customers(id) ON DELETE RESTRICT,
 created_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now(),
 CHECK((state='converted')=(customer_id IS NOT NULL))
);
CREATE INDEX sales_leads_state_branch ON sales_leads(state,branch_id,created_at DESC);
CREATE TABLE sales_quotes(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 number bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
 lead_id uuid NOT NULL REFERENCES sales_leads(id) ON DELETE RESTRICT,
 package_id uuid NOT NULL REFERENCES packages(id) ON DELETE RESTRICT,
 package_name varchar(160) NOT NULL,
 download_mbps integer NOT NULL CHECK(download_mbps>0),
 upload_mbps integer NOT NULL CHECK(upload_mbps>0),
 monthly_price numeric(12,2) NOT NULL CHECK(monthly_price>=0),
 installation_price numeric(12,2) NOT NULL CHECK(installation_price>=0),
 currency char(3) NOT NULL DEFAULT 'ZAR' CHECK(currency='ZAR'),
 contact_name varchar(241) NOT NULL,
 service_address varchar(500) NOT NULL,
 terms varchar(2000) NOT NULL,
 valid_until timestamptz NOT NULL,
 state varchar(12) NOT NULL DEFAULT 'issued' CHECK(state IN ('issued','accepted','withdrawn')),
 issued_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 issued_at timestamptz NOT NULL DEFAULT now(),
 CHECK(valid_until>issued_at)
);
CREATE UNIQUE INDEX sales_quote_one_issued ON sales_quotes(lead_id) WHERE state='issued';
CREATE UNIQUE INDEX sales_quote_one_accepted ON sales_quotes(lead_id) WHERE state='accepted';
CREATE FUNCTION sales_quote_frozen() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Quotation history cannot be deleted' USING ERRCODE='23514'; END IF;
 IF OLD.state<>'issued' OR NEW.state NOT IN ('accepted','withdrawn')
    OR (to_jsonb(NEW)-'state') IS DISTINCT FROM (to_jsonb(OLD)-'state') THEN
  RAISE EXCEPTION 'Issued quotation terms are immutable' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER sales_quote_no_edit BEFORE UPDATE OR DELETE ON sales_quotes
 FOR EACH ROW EXECUTE FUNCTION sales_quote_frozen();
CREATE TABLE sales_acceptances(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 quote_id uuid NOT NULL UNIQUE REFERENCES sales_quotes(id) ON DELETE RESTRICT,
 customer_id uuid NOT NULL UNIQUE REFERENCES customers(id) ON DELETE RESTRICT,
 accepted_by_name varchar(160) NOT NULL,
 evidence_reference varchar(240) NOT NULL,
 recorded_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 recorded_at timestamptz NOT NULL DEFAULT now()
);
CREATE FUNCTION sales_acceptance_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Acceptance history is immutable' USING ERRCODE='23514';
END;
$$;
CREATE TRIGGER sales_acceptance_no_edit BEFORE UPDATE OR DELETE ON sales_acceptances
 FOR EACH ROW EXECUTE FUNCTION sales_acceptance_immutable();
-- A technician may serve more than one branch. Daily planning deliberately
-- has no time slots; multiple installations per technician/day are allowed.
CREATE TABLE branch_technicians(
 branch_id uuid NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
 user_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 PRIMARY KEY(branch_id,user_id)
);
CREATE TABLE installation_orders(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 number bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
 acceptance_id uuid NOT NULL UNIQUE REFERENCES sales_acceptances(id) ON DELETE RESTRICT,
 ticket_id uuid NOT NULL UNIQUE REFERENCES tickets(id) ON DELETE RESTRICT,
 branch_id uuid NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
 technician_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
 scheduled_day date,
 state varchar(16) NOT NULL DEFAULT 'unscheduled'
   CHECK(state IN ('unscheduled','scheduled','in_progress','completed','blocked','cancelled')),
 completed_at timestamptz,
 result_note varchar(1000),
 created_at timestamptz NOT NULL DEFAULT now(),
 CHECK((technician_id IS NULL)=(scheduled_day IS NULL)),
 CHECK(state NOT IN ('scheduled','in_progress','completed') OR technician_id IS NOT NULL)
);
CREATE INDEX installation_orders_day ON installation_orders(branch_id,scheduled_day,technician_id);
CREATE TABLE installation_events(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 order_id uuid NOT NULL REFERENCES installation_orders(id) ON DELETE RESTRICT,
 actor_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 action varchar(30) NOT NULL,
 note varchar(1000) NOT NULL DEFAULT '',
 occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX installation_events_order ON installation_events(order_id,id);
