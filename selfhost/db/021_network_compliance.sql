-- root/selfhost/db/021_network_compliance.sql
-- Policies are opt-in per enrolled router. Polling is separately opt-in.
-- Baselines are append-only normalized configuration evidence, never secrets.
CREATE TABLE routeros_compliance_policies(
 device_id uuid PRIMARY KEY REFERENCES routeros_devices(id) ON DELETE RESTRICT,
 expected_version varchar(32),
 backup_profile_id uuid REFERENCES backup_profiles(id) ON DELETE RESTRICT,
 backup_max_hours integer NOT NULL CHECK(backup_max_hours BETWEEN 1 AND 168),
 snapshot_max_hours integer NOT NULL CHECK(snapshot_max_hours BETWEEN 1 AND 168),
 require_baseline boolean NOT NULL DEFAULT true,
 poll_interval_minutes integer NOT NULL DEFAULT 0 CHECK(poll_interval_minutes=0 OR poll_interval_minutes BETWEEN 15 AND 1440),
 next_poll_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX routeros_compliance_due ON routeros_compliance_policies(next_poll_at) WHERE poll_interval_minutes>0;
CREATE TABLE routeros_compliance_baselines(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 device_id uuid NOT NULL REFERENCES routeros_devices(id) ON DELETE RESTRICT,
 source_poll_at timestamptz NOT NULL,
 sections jsonb NOT NULL,
 sha256 char(64) NOT NULL CHECK(sha256 ~ '^[0-9A-F]{64}$'),
 reason varchar(240) NOT NULL,
 created_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX routeros_compliance_baseline_recent ON routeros_compliance_baselines(device_id,created_at DESC,id DESC);
CREATE FUNCTION compliance_baseline_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Compliance baseline history is immutable' USING ERRCODE='23514';
END;
$$;
CREATE TRIGGER compliance_baseline_no_edit BEFORE UPDATE OR DELETE ON routeros_compliance_baselines
 FOR EACH ROW EXECUTE FUNCTION compliance_baseline_immutable();
