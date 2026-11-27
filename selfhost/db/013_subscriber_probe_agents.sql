-- root/selfhost/db/013_subscriber_probe_agents.sql
-- Opt-in push-only diagnostics. Credentials are high-entropy SHA256 digests,
-- never retrievable through staff APIs. Silence is unknown, not downtime.
CREATE TABLE subscriber_probe_agents (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 service_id uuid NOT NULL REFERENCES services(id) ON DELETE RESTRICT,
 name varchar(100) NOT NULL,
 dns_host varchar(253) NOT NULL,
 https_url varchar(512) NOT NULL,
 expected_status integer NOT NULL CHECK(expected_status BETWEEN 200 AND 299),
 interval_seconds integer NOT NULL CHECK(interval_seconds BETWEEN 60 AND 3600),
 token_hash char(64) NOT NULL,
 generation integer NOT NULL DEFAULT 1 CHECK(generation>0),
 enabled boolean NOT NULL DEFAULT true,
 last_received_at timestamptz,
 last_measured_at timestamptz,
 created_by varchar(100) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(service_id,name)
);
CREATE TABLE subscriber_probe_reports (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 agent_id uuid NOT NULL REFERENCES subscriber_probe_agents(id) ON DELETE RESTRICT,
 generation integer NOT NULL,
 measurement_id uuid NOT NULL,
 measured_at timestamptz NOT NULL,
 received_at timestamptz NOT NULL DEFAULT now(),
 dns_result varchar(8) NOT NULL CHECK(dns_result IN ('ok','failed','timeout')),
 dns_ms integer NOT NULL CHECK(dns_ms BETWEEN 0 AND 30000),
 https_result varchar(8) NOT NULL CHECK(https_result IN ('ok','failed','timeout')),
 https_ms integer NOT NULL CHECK(https_ms BETWEEN 0 AND 30000),
 https_status integer CHECK(https_status BETWEEN 100 AND 599),
 UNIQUE(agent_id,measurement_id)
);
CREATE INDEX idx_subscriber_probe_reports_latest
 ON subscriber_probe_reports(agent_id,generation,measured_at DESC,id DESC);
