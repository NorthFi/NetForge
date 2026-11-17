-- root/selfhost/db/008_snmp_interface_hourly.sql
-- Non-destructive long term rollups for standard IF-MIB bitrate and status.
CREATE TABLE IF NOT EXISTS snmp_interface_hourly (
  device_id uuid NOT NULL REFERENCES snmp_devices(id) ON DELETE RESTRICT,
  hour_start timestamptz NOT NULL,
  samples integer NOT NULL CHECK(samples>0),
  operational_samples integer NOT NULL CHECK(operational_samples>=0),
  input_average_bps numeric(20,2),
  input_peak_bps numeric(20,2),
  output_average_bps numeric(20,2),
  output_peak_bps numeric(20,2),
  PRIMARY KEY(device_id,hour_start)
);
CREATE INDEX IF NOT EXISTS idx_snmp_interface_hourly_time
  ON snmp_interface_hourly(hour_start);
