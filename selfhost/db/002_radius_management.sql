-- root/selfhost/db/002_radius_management.sql
-- Idempotent extension for network/session operations and opt-in billing enforcement.
-- FreeRADIUS loads NAS secrets from the existing nas table at startup.
CREATE TABLE IF NOT EXISTS nas_management (
  nas_id integer PRIMARY KEY REFERENCES nas(id) ON DELETE CASCADE,
  vendor varchar(24) NOT NULL DEFAULT 'mikrotik'
    CHECK (vendor IN ('mikrotik','generic')),
  disconnect_enabled boolean NOT NULL DEFAULT false,
  disconnect_port integer NOT NULL DEFAULT 1700 CHECK (disconnect_port BETWEEN 1 AND 65535),
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS radius_operations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  request_id uuid NOT NULL UNIQUE,
  radacctid bigint NOT NULL REFERENCES radacct(radacctid) ON DELETE RESTRICT,
  nas_id integer NOT NULL REFERENCES nas(id) ON DELETE RESTRICT,
  username text NOT NULL,
  acctsessionid text NOT NULL,
  operation varchar(24) NOT NULL CHECK (operation IN ('disconnect')),
  result varchar(24) NOT NULL DEFAULT 'pending'
    CHECK (result IN ('pending','ack','nak','timeout','rejected','error')),
  actor varchar(80) NOT NULL,
  reason varchar(240) NOT NULL,
  response_note text NOT NULL DEFAULT '',
  created_at timestamptz NOT NULL DEFAULT now(),
  finished_at timestamptz
);
CREATE INDEX IF NOT EXISTS idx_radius_operations_created ON radius_operations(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_radacct_user_started ON radacct(username,acctstarttime DESC);
ALTER TABLE services ADD COLUMN IF NOT EXISTS suspension_reason varchar(24);
ALTER TABLE services ADD COLUMN IF NOT EXISTS suspended_at timestamptz;
ALTER TABLE services ADD COLUMN IF NOT EXISTS resumed_at timestamptz;
DO $
BEGIN
  IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='services_suspension_reason_valid') THEN
    ALTER TABLE services ADD CONSTRAINT services_suspension_reason_valid
      CHECK (suspension_reason IS NULL OR suspension_reason IN ('manual','overdue'));
  END IF;
END
$;
-- This idempotent guard also works when a fresh database runs 001 and 002 together.
-- Install guard for pre-existing tables separately if prior attempts created it.
CREATE TABLE IF NOT EXISTS billing_controls (
  id smallint PRIMARY KEY DEFAULT 1 CHECK (id=1),
  enabled boolean NOT NULL DEFAULT false,
  grace_days integer NOT NULL DEFAULT 7 CHECK (grace_days BETWEEN 0 AND 120),
  restore_when_paid boolean NOT NULL DEFAULT false,
  last_run_at timestamptz,
  updated_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO billing_controls(id) VALUES (1) ON CONFLICT(id) DO NOTHING;
CREATE TABLE IF NOT EXISTS billing_policy_runs (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  started_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz NOT NULL DEFAULT now(),
  suspended integer NOT NULL DEFAULT 0,
  restored integer NOT NULL DEFAULT 0,
  status varchar(20) NOT NULL DEFAULT 'success'
);
