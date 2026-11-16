-- root/selfhost/db/005_operations_automation.sql
-- Durable, opt-in invoice automation, notification outbox and TCP service checks.
CREATE TABLE IF NOT EXISTS billing_schedule (
  id smallint PRIMARY KEY DEFAULT 1 CHECK(id=1),
  enabled boolean NOT NULL DEFAULT false,
  auto_issue boolean NOT NULL DEFAULT false,
  run_day smallint NOT NULL DEFAULT 1 CHECK(run_day BETWEEN 1 AND 28),
  due_days smallint NOT NULL DEFAULT 7 CHECK(due_days BETWEEN 1 AND 90),
  last_run_at timestamptz,
  updated_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO billing_schedule(id) VALUES(1) ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS invoice_items (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE RESTRICT,
  service_id uuid NOT NULL REFERENCES services(id) ON DELETE RESTRICT,
  description varchar(220) NOT NULL,
  quantity numeric(10,2) NOT NULL DEFAULT 1 CHECK(quantity>0),
  unit_price numeric(12,2) NOT NULL CHECK(unit_price>=0),
  amount numeric(12,2) NOT NULL CHECK(amount>=0),
  UNIQUE(invoice_id,service_id)
);
CREATE INDEX IF NOT EXISTS idx_invoice_items_invoice ON invoice_items(invoice_id);

CREATE TABLE IF NOT EXISTS billing_schedule_runs (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  period date NOT NULL,
  created_count integer NOT NULL CHECK(created_count>=0),
  issued_count integer NOT NULL CHECK(issued_count>=0),
  actor varchar(100) NOT NULL,
  occurred_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE customers ADD COLUMN IF NOT EXISTS email_invoice_opt_in boolean NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS notification_settings (
  id smallint PRIMARY KEY DEFAULT 1 CHECK(id=1),
  customer_invoice_email boolean NOT NULL DEFAULT false,
  ops_email boolean NOT NULL DEFAULT false,
  ops_telegram boolean NOT NULL DEFAULT false,
  updated_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO notification_settings(id) VALUES(1) ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS notification_outbox (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  dedupe_key varchar(180) NOT NULL UNIQUE,
  channel varchar(12) NOT NULL CHECK(channel IN ('email','telegram')),
  recipient varchar(254) NOT NULL,
  subject varchar(240) NOT NULL,
  body text NOT NULL CHECK(length(body) BETWEEN 1 AND 5000),
  status varchar(12) NOT NULL DEFAULT 'pending'
    CHECK(status IN ('pending','processing','sent','dead')),
  attempts smallint NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 10),
  next_attempt_at timestamptz NOT NULL DEFAULT now(),
  claimed_until timestamptz,
  last_error varchar(300) NOT NULL DEFAULT '',
  created_at timestamptz NOT NULL DEFAULT now(),
  sent_at timestamptz
);
CREATE INDEX IF NOT EXISTS idx_outbox_due ON notification_outbox(next_attempt_at)
  WHERE status IN ('pending','processing');

CREATE TABLE IF NOT EXISTS monitor_targets (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  nas_id integer REFERENCES nas(id) ON DELETE RESTRICT,
  equipment_id uuid REFERENCES equipment(id) ON DELETE RESTRICT,
  name varchar(160) NOT NULL,
  tcp_port integer NOT NULL CHECK(tcp_port BETWEEN 1 AND 65535),
  enabled boolean NOT NULL DEFAULT false,
  interval_seconds integer NOT NULL DEFAULT 300 CHECK(interval_seconds BETWEEN 60 AND 3600),
  failure_threshold integer NOT NULL DEFAULT 2 CHECK(failure_threshold BETWEEN 2 AND 10),
  failure_streak integer NOT NULL DEFAULT 0 CHECK(failure_streak>=0),
  state varchar(12) NOT NULL DEFAULT 'unknown'
    CHECK(state IN ('unknown','up','down')),
  last_checked_at timestamptz,
  last_latency_ms integer CHECK(last_latency_ms IS NULL OR last_latency_ms>=0),
  next_check_at timestamptz NOT NULL DEFAULT now(),
  last_error varchar(160) NOT NULL DEFAULT '',
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((nas_id IS NOT NULL)::int+(equipment_id IS NOT NULL)::int=1)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_monitor_nas ON monitor_targets(nas_id,tcp_port)
  WHERE nas_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_monitor_equipment ON monitor_targets(equipment_id,tcp_port)
  WHERE equipment_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_monitor_due ON monitor_targets(next_check_at)
  WHERE enabled=true;

CREATE TABLE IF NOT EXISTS monitor_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  target_id uuid NOT NULL REFERENCES monitor_targets(id) ON DELETE RESTRICT,
  transition varchar(12) NOT NULL CHECK(transition IN ('down','recovered')),
  occurred_at timestamptz NOT NULL DEFAULT now(),
  detail varchar(240) NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_monitor_events_recent ON monitor_events(occurred_at DESC);
