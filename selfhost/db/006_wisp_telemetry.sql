-- root/selfhost/db/006_wisp_telemetry.sql
-- Additive migration: SNMPv3 telemetry, TCP site topology and correlated incidents.
-- Do not modify already applied migrations; preserve installed telemetry history.
ALTER TABLE monitor_targets
  ADD COLUMN IF NOT EXISTS highsite_id uuid REFERENCES highsites(id) ON DELETE RESTRICT;
ALTER TABLE monitor_targets
  ADD COLUMN IF NOT EXISTS is_upstream boolean NOT NULL DEFAULT false;
ALTER TABLE monitor_events
  ADD COLUMN IF NOT EXISTS suppressed boolean NOT NULL DEFAULT false;

-- Associate existing equipment monitors with their known tower. NAS and
-- equipment without a recorded tower remain explicitly unassigned.
UPDATE monitor_targets m SET highsite_id=e.highsite_id
FROM equipment e WHERE m.equipment_id=e.id AND m.highsite_id IS NULL
  AND e.highsite_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_monitor_site_upstream
  ON monitor_targets(highsite_id,state)
  WHERE enabled=true AND is_upstream=true AND highsite_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS site_monitor_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  highsite_id uuid NOT NULL REFERENCES highsites(id) ON DELETE RESTRICT,
  triggering_target_id uuid NOT NULL REFERENCES monitor_targets(id) ON DELETE RESTRICT,
  transition varchar(12) NOT NULL CHECK(transition IN ('down','recovered')),
  impacted_customers integer NOT NULL DEFAULT 0 CHECK(impacted_customers>=0),
  sectors integer NOT NULL DEFAULT 0 CHECK(sectors>=0),
  detail varchar(240) NOT NULL DEFAULT '',
  occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_site_monitor_events_recent
  ON site_monitor_events(occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_site_monitor_events_highsite
  ON site_monitor_events(highsite_id,occurred_at DESC);

-- One SNMPv3 authPriv profile per saved equipment/NAS, optionally assigned
-- to a highsite for capacity/impact reporting. Secrets are encrypted by
-- ASP.NET Core DataProtection before inserting into the database.
CREATE TABLE IF NOT EXISTS snmp_devices (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  equipment_id uuid REFERENCES equipment(id) ON DELETE RESTRICT,
  nas_id integer REFERENCES nas(id) ON DELETE RESTRICT,
  highsite_id uuid REFERENCES highsites(id) ON DELETE RESTRICT,
  name varchar(160) NOT NULL,
  username varchar(80) NOT NULL,
  auth_protocol varchar(8) NOT NULL DEFAULT 'SHA'
    CHECK(auth_protocol IN ('SHA','SHA-256')),
  auth_secret_ciphertext text NOT NULL,
  priv_secret_ciphertext text NOT NULL,
  udp_port integer NOT NULL DEFAULT 161 CHECK(udp_port BETWEEN 1 AND 65535),
  interface_index integer NOT NULL CHECK(interface_index BETWEEN 1 AND 2147483647),
  enabled boolean NOT NULL DEFAULT false,
  interval_seconds integer NOT NULL DEFAULT 300 CHECK(interval_seconds BETWEEN 60 AND 3600),
  next_poll_at timestamptz NOT NULL DEFAULT now(),
  last_polled_at timestamptz,
  state varchar(12) NOT NULL DEFAULT 'unknown'
    CHECK(state IN ('unknown','up','down')),
  failure_streak integer NOT NULL DEFAULT 0 CHECK(failure_streak>=0),
  last_error varchar(160) NOT NULL DEFAULT '',
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((equipment_id IS NOT NULL)::integer+(nas_id IS NOT NULL)::integer=1)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_snmp_equipment ON snmp_devices(equipment_id)
  WHERE equipment_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_snmp_nas ON snmp_devices(nas_id)
  WHERE nas_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_snmp_poll_due ON snmp_devices(next_poll_at)
  WHERE enabled=true;

CREATE TABLE IF NOT EXISTS snmp_samples (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  device_id uuid NOT NULL REFERENCES snmp_devices(id) ON DELETE RESTRICT,
  observed_at timestamptz NOT NULL DEFAULT now(),
  uptime_ticks bigint NOT NULL CHECK(uptime_ticks>=0),
  oper_status integer NOT NULL CHECK(oper_status BETWEEN 1 AND 7),
  input_octets numeric(20,0) NOT NULL CHECK(input_octets>=0),
  output_octets numeric(20,0) NOT NULL CHECK(output_octets>=0),
  input_bps numeric(20,2),
  output_bps numeric(20,2),
  counter_reset boolean NOT NULL DEFAULT false,
  CHECK(input_bps IS NULL OR input_bps>=0),
  CHECK(output_bps IS NULL OR output_bps>=0)
);
CREATE INDEX IF NOT EXISTS idx_snmp_samples_device_time
  ON snmp_samples(device_id,observed_at DESC,id DESC);

CREATE TABLE IF NOT EXISTS snmp_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  device_id uuid NOT NULL REFERENCES snmp_devices(id) ON DELETE RESTRICT,
  transition varchar(20) NOT NULL CHECK(transition IN ('down','recovered')),
  reason varchar(160) NOT NULL,
  suppressed boolean NOT NULL DEFAULT false,
  occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_snmp_events_recent
  ON snmp_events(occurred_at DESC);
