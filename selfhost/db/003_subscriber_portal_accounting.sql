-- root/selfhost/db/003_subscriber_portal_accounting.sql
-- Idempotent customer portal schema and accounting deltas for FreeRADIUS SQL updates.
CREATE TABLE IF NOT EXISTS customer_portal_accounts (
  customer_id uuid PRIMARY KEY REFERENCES customers(id) ON DELETE RESTRICT,
  username varchar(80) NOT NULL,
  password_hash text NOT NULL,
  password_version integer NOT NULL DEFAULT 1 CHECK(password_version>0),
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_portal_username_ci
  ON customer_portal_accounts(lower(username));

CREATE TABLE IF NOT EXISTS ticket_messages (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_id uuid NOT NULL REFERENCES tickets(id) ON DELETE RESTRICT,
  visibility varchar(12) NOT NULL CHECK(visibility IN ('public','internal')),
  actor_type varchar(12) NOT NULL CHECK(actor_type IN ('staff','customer')),
  author varchar(100) NOT NULL,
  body text NOT NULL CHECK(length(body) BETWEEN 1 AND 4000),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ticket_messages_ticket_time
  ON ticket_messages(ticket_id,created_at,id);

-- One row per observed change in cumulative accounting totals, not per query.
-- Event timestamps and byte counters are recorded, making history stable even
-- after radacct counters continue to update.
CREATE TABLE IF NOT EXISTS radius_usage_intervals (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  radacctid bigint NOT NULL REFERENCES radacct(radacctid) ON DELETE CASCADE,
  username text NOT NULL,
  nas_ip inet NOT NULL,
  started_at timestamptz NOT NULL,
  ended_at timestamptz NOT NULL,
  input_bytes bigint NOT NULL CHECK(input_bytes>=0),
  output_bytes bigint NOT NULL CHECK(output_bytes>=0),
  source varchar(12) NOT NULL CHECK(source IN ('insert','update')),
  observed_at timestamptz NOT NULL DEFAULT now(),
  CHECK(ended_at>started_at)
);
CREATE INDEX IF NOT EXISTS idx_usage_intervals_period
  ON radius_usage_intervals(started_at,ended_at);
CREATE INDEX IF NOT EXISTS idx_usage_intervals_account
  ON radius_usage_intervals(username,ended_at);

CREATE TABLE IF NOT EXISTS radius_usage_anomalies (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  radacctid bigint NOT NULL REFERENCES radacct(radacctid) ON DELETE CASCADE,
  username text NOT NULL,
  reason varchar(45) NOT NULL,
  old_input_bytes bigint,
  new_input_bytes bigint,
  old_output_bytes bigint,
  new_output_bytes bigint,
  observed_at timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION netforge_capture_radius_usage() RETURNS trigger
LANGUAGE plpgsql AS $netforge_usage$
DECLARE
  old_in bigint := 0;
  old_out bigint := 0;
  new_in bigint := COALESCE(NEW.acctinputoctets,0);
  new_out bigint := COALESCE(NEW.acctoutputoctets,0);
  period_start timestamptz;
  period_end timestamptz;
  reason_text varchar(45) := NULL;
BEGIN
  IF TG_OP = 'UPDATE' THEN
    old_in := COALESCE(OLD.acctinputoctets,0);
    old_out := COALESCE(OLD.acctoutputoctets,0);
    period_start := COALESCE(OLD.acctupdatetime,OLD.acctstarttime);
  ELSE
    period_start := NEW.acctstarttime;
  END IF;
  period_end := COALESCE(NEW.acctupdatetime,NEW.acctstoptime,NEW.acctstarttime);

  -- An explicit duplicate accounting update with unchanged byte totals is
  -- ignored. Do not count it twice, even if its timestamp advanced.
  IF new_in = old_in AND new_out = old_out THEN
    RETURN NEW;
  END IF;

  IF new_in < old_in OR new_out < old_out OR new_in < 0 OR new_out < 0 THEN
    reason_text := 'counter_decreased';
  ELSIF period_start IS NULL OR period_end IS NULL THEN
    reason_text := 'missing_timestamp';
  ELSIF period_end <= period_start THEN
    reason_text := 'non_increasing_timestamp';
  END IF;

  IF reason_text IS NOT NULL THEN
    INSERT INTO radius_usage_anomalies(
      radacctid,username,reason,old_input_bytes,new_input_bytes,
      old_output_bytes,new_output_bytes)
    VALUES(NEW.radacctid,COALESCE(NEW.username,''),
      reason_text,old_in,new_in,old_out,new_out);
    RETURN NEW;
  END IF;

  INSERT INTO radius_usage_intervals(
    radacctid,username,nas_ip,started_at,ended_at,
    input_bytes,output_bytes,source)
  VALUES(NEW.radacctid,COALESCE(NEW.username,''),NEW.nasipaddress,
    period_start,period_end,new_in-old_in,new_out-old_out,
    CASE WHEN TG_OP='INSERT' THEN 'insert' ELSE 'update' END);
  RETURN NEW;
END
$netforge_usage$;

DROP TRIGGER IF EXISTS trg_netforge_usage ON radacct;
CREATE TRIGGER trg_netforge_usage
AFTER INSERT OR UPDATE OF acctinputoctets,acctoutputoctets,acctupdatetime,acctstoptime
ON radacct FOR EACH ROW EXECUTE FUNCTION netforge_capture_radius_usage();

-- Existing rows predate the trigger and cannot be reconstructed into
-- individual interim intervals. Mark the earliest sample by date in the UI.
-- Never silently backfill estimated data as if it were observed deltas.
