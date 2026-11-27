-- root/selfhost/db/018_network_change_control.sql
-- Approved manual changes, not a remote execution engine. Every plan freezes
-- the script revision and target membership. Credentials never enter targets.
CREATE TABLE network_scripts(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 name varchar(160) NOT NULL UNIQUE,
 vendor varchar(16) NOT NULL CHECK(vendor IN ('routeros','cambium','ubiquiti','mimosa','other')),
 description varchar(500) NOT NULL DEFAULT '',
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE network_script_versions(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 script_id uuid NOT NULL REFERENCES network_scripts(id) ON DELETE RESTRICT,
 revision integer NOT NULL CHECK(revision>0),
 sha256 char(64) NOT NULL CHECK(sha256 ~ '^[0-9A-F]{64}$'),
 ciphertext bytea NOT NULL,
 created_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(script_id,revision)
);
CREATE FUNCTION network_script_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Script revisions are immutable' USING ERRCODE='23514';
END;
$$;
CREATE TRIGGER network_script_no_edit BEFORE UPDATE OR DELETE ON network_script_versions
 FOR EACH ROW EXECUTE FUNCTION network_script_immutable();
CREATE TABLE network_device_groups(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 name varchar(160) NOT NULL UNIQUE,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE network_group_members(
 group_id uuid NOT NULL REFERENCES network_device_groups(id) ON DELETE RESTRICT,
 profile_id uuid NOT NULL REFERENCES backup_profiles(id) ON DELETE RESTRICT,
 PRIMARY KEY(group_id,profile_id)
);
CREATE TABLE network_change_plans(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 version_id uuid NOT NULL REFERENCES network_script_versions(id) ON DELETE RESTRICT,
 group_id uuid NOT NULL REFERENCES network_device_groups(id) ON DELETE RESTRICT,
 reason varchar(500) NOT NULL,
 created_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 approved_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now(),
 approved_at timestamptz,
 window_start timestamptz NOT NULL,
 window_end timestamptz NOT NULL,
 state varchar(16) NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','approved','running','completed','halted','cancelled')),
 CHECK(window_end>window_start),
 CHECK(approved_by IS NULL OR approved_by<>created_by)
);
CREATE TABLE network_change_targets(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 plan_id uuid NOT NULL REFERENCES network_change_plans(id) ON DELETE RESTRICT,
 profile_id uuid NOT NULL REFERENCES backup_profiles(id) ON DELETE RESTRICT,
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 50),
 state varchar(16) NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','handed_off','succeeded','failed')),
 backup_id uuid REFERENCES backup_versions(id) ON DELETE RESTRICT,
 operator_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
 handed_off_at timestamptz,
 completed_at timestamptz,
 outcome_note varchar(500),
 UNIQUE(plan_id,profile_id), UNIQUE(plan_id,ordinal)
);
CREATE INDEX network_change_plans_recent ON network_change_plans(created_at DESC);
