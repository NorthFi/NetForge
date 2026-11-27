-- root/selfhost/db/017_backup_vault.sql
-- Binary or textual vendor backups are opaque encrypted objects. Never execute
-- uploaded content. Credential hashes authorize ingestion only, never retrieval.
CREATE TABLE backup_profiles(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 nas_id integer UNIQUE REFERENCES nas(id) ON DELETE RESTRICT,
 equipment_id uuid UNIQUE REFERENCES equipment(id) ON DELETE RESTRICT,
 name varchar(160) NOT NULL,
 vendor varchar(16) NOT NULL CHECK(vendor IN ('routeros','cambium','ubiquiti','mimosa','other')),
 token_hash char(64),
 created_at timestamptz NOT NULL DEFAULT now(),
 last_received_at timestamptz,
 CHECK((nas_id IS NULL)<>(equipment_id IS NULL))
);
CREATE TABLE backup_versions(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 profile_id uuid NOT NULL REFERENCES backup_profiles(id) ON DELETE RESTRICT,
 kind varchar(10) NOT NULL CHECK(kind IN ('text','binary')),
 filename varchar(160) NOT NULL,
 size_bytes integer NOT NULL CHECK(size_bytes BETWEEN 1 AND 4194304),
 sha256 char(64) NOT NULL CHECK(sha256 ~ '^[0-9A-F]{64}$'),
 ciphertext bytea NOT NULL,
 source varchar(12) NOT NULL CHECK(source IN ('manual','collector')),
 actor varchar(80) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX backup_versions_profile_created ON backup_versions(profile_id,created_at DESC,id DESC);
