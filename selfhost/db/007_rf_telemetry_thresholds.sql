-- root/selfhost/db/007_rf_telemetry_thresholds.sql
-- Verified numeric SNMPv3 OIDs only; no undocumented vendor OIDs are shipped.
-- Retention is disabled by default; data removal requires opt-in.
CREATE TABLE IF NOT EXISTS snmp_metric_profiles (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  device_id uuid NOT NULL REFERENCES snmp_devices(id) ON DELETE RESTRICT,
  metric_key varchar(48) NOT NULL CHECK(metric_key IN (
    'rssi_dbm','rssi_chain0_dbm','rssi_chain1_dbm','snr_db','noise_dbm',
    'ccq_pct','tx_ccq_pct','rx_capacity_mbps','tx_capacity_mbps',
    'cpu_pct','memory_pct','voltage_v','power_w','temperature_c',
    'frequency_mhz')),
  oid varchar(128) NOT NULL CHECK(oid ~ '^\.[0-9]+(\.[0-9]+){4,30}$'),
  scale numeric(12,6) NOT NULL DEFAULT 1 CHECK(scale<>0 AND abs(scale)<=100000),
  value_offset numeric(12,4) NOT NULL DEFAULT 0 CHECK(abs(value_offset)<=100000),
  enabled boolean NOT NULL DEFAULT false,
  direction varchar(6) NOT NULL DEFAULT 'below' CHECK(direction IN ('below','above')),
  warning_threshold numeric(16,4),
  critical_threshold numeric(16,4),
  recovery_margin numeric(10,4) NOT NULL DEFAULT 0 CHECK(recovery_margin>=0 AND recovery_margin<=100000),
  severity varchar(10) NOT NULL DEFAULT 'normal'
    CHECK(severity IN ('normal','warning','critical')),
  pending_severity varchar(10) NOT NULL DEFAULT 'normal'
    CHECK(pending_severity IN ('normal','warning','critical')),
  pending_count smallint NOT NULL DEFAULT 0 CHECK(pending_count BETWEEN 0 AND 2),
  last_value numeric(20,4),
  last_checked_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(device_id,metric_key),
  CHECK((warning_threshold IS NULL)=(critical_threshold IS NULL)),
  CHECK(warning_threshold IS NULL OR (
    (direction='below' AND critical_threshold<=warning_threshold) OR
    (direction='above' AND critical_threshold>=warning_threshold))),
  CHECK(severity='normal' OR warning_threshold IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_snmp_metric_profiles_device ON snmp_metric_profiles(device_id) WHERE enabled=true;

CREATE TABLE IF NOT EXISTS snmp_metric_samples (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  profile_id uuid NOT NULL REFERENCES snmp_metric_profiles(id) ON DELETE RESTRICT,
  observed_at timestamptz NOT NULL DEFAULT now(),
  raw_value numeric(20,4) NOT NULL,
  value numeric(20,4) NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_snmp_metric_samples_time ON snmp_metric_samples(observed_at);
CREATE INDEX IF NOT EXISTS idx_snmp_metric_samples_profile_time
  ON snmp_metric_samples(profile_id,observed_at DESC,id DESC);

CREATE TABLE IF NOT EXISTS snmp_metric_events (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  profile_id uuid NOT NULL REFERENCES snmp_metric_profiles(id) ON DELETE RESTRICT,
  previous_severity varchar(10) NOT NULL,
  severity varchar(10) NOT NULL,
  value numeric(20,4) NOT NULL,
  suppressed boolean NOT NULL DEFAULT false,
  observed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_snmp_metric_events_recent ON snmp_metric_events(observed_at DESC);

CREATE TABLE IF NOT EXISTS snmp_metric_hourly (
  profile_id uuid NOT NULL REFERENCES snmp_metric_profiles(id) ON DELETE RESTRICT,
  hour_start timestamptz NOT NULL,
  samples integer NOT NULL CHECK(samples>0),
  minimum numeric(20,4) NOT NULL,
  maximum numeric(20,4) NOT NULL,
  average numeric(20,4) NOT NULL,
  PRIMARY KEY(profile_id,hour_start)
);
CREATE INDEX IF NOT EXISTS idx_snmp_metric_hourly_time ON snmp_metric_hourly(hour_start);

CREATE TABLE IF NOT EXISTS telemetry_retention (
  id smallint PRIMARY KEY DEFAULT 1 CHECK(id=1),
  enabled boolean NOT NULL DEFAULT false,
  raw_days integer NOT NULL DEFAULT 90 CHECK(raw_days BETWEEN 7 AND 365),
  hourly_days integer NOT NULL DEFAULT 365 CHECK(hourly_days BETWEEN 30 AND 1825),
  last_run_at timestamptz,
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK(hourly_days>=raw_days)
);
INSERT INTO telemetry_retention(id) VALUES (1) ON CONFLICT DO NOTHING;

-- Raw generic telemetry is separate from standard interface counters.
-- Existing SNMP history remains unmodified when introducing this migration.
