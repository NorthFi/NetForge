-- root/selfhost/db/016_routeros_inventory.sql
-- One administrator-enrolled HTTPS identity per inventory asset. No arbitrary
-- URL, remote shell, script execution, or readable credential column in API output.
CREATE TABLE routeros_devices(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 nas_id integer UNIQUE REFERENCES nas(id) ON DELETE RESTRICT,
 equipment_id uuid UNIQUE REFERENCES equipment(id) ON DELETE RESTRICT,
 name varchar(160) NOT NULL,
 address inet NOT NULL CHECK(family(address)=4 AND masklen(address)=32),
 port integer NOT NULL CHECK(port BETWEEN 1 AND 65535),
 username varchar(80) NOT NULL,
 password_ciphertext text NOT NULL,
 certificate_sha256 char(64) NOT NULL CHECK(certificate_sha256 ~ '^[0-9A-F]{64}$'),
 enabled boolean NOT NULL DEFAULT false,
 created_at timestamptz NOT NULL DEFAULT now(),
 last_poll_at timestamptz,
 last_status varchar(12) NOT NULL DEFAULT 'unknown'
   CHECK(last_status IN ('unknown','ok','partial','failed')),
 snapshot jsonb,
 CHECK((nas_id IS NULL)<>(equipment_id IS NULL)),
 UNIQUE(address,port)
);
