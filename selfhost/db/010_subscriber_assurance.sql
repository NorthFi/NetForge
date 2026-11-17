-- root/selfhost/db/010_subscriber_assurance.sql
-- Evidence-aware service assurance. Historical session accounting does not
-- prove internet reachability; staff-attested interruptions remain separate.
CREATE TABLE IF NOT EXISTS assurance_settings (
    id smallint PRIMARY KEY DEFAULT 1 CHECK(id=1),
    target_percent numeric(6,3) NOT NULL DEFAULT 99.500
        CHECK(target_percent BETWEEN 90.000 AND 99.999),
    accounting_freshness_minutes integer NOT NULL DEFAULT 10
        CHECK(accounting_freshness_minutes BETWEEN 2 AND 120),
    updated_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO assurance_settings(id) VALUES(1) ON CONFLICT(id) DO NOTHING;

-- Immutable association of subscriber services at an incident opening.
-- A later customer site reassignment does not rewrite original evidence.
CREATE TABLE IF NOT EXISTS assurance_incident_services (
    incident_id uuid NOT NULL REFERENCES noc_incidents(id) ON DELETE RESTRICT,
    service_id uuid NOT NULL REFERENCES services(id) ON DELETE RESTRICT,
    customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    username_snapshot varchar(180) NOT NULL,
    service_state_snapshot varchar(20) NOT NULL,
    PRIMARY KEY(incident_id,service_id)
);
CREATE INDEX IF NOT EXISTS idx_assurance_incident_services_service
    ON assurance_incident_services(service_id);

CREATE TABLE IF NOT EXISTS assurance_session_observations (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_id uuid NOT NULL,
    service_id uuid NOT NULL,
    observed_at timestamptz NOT NULL DEFAULT now(),
    state varchar(32) NOT NULL CHECK(state IN (
        'recent_accounting','stale_accounting','recent_stop_observed','no_accounting_data')),
    open_sessions integer NOT NULL DEFAULT 0 CHECK(open_sessions>=0),
    recent_open_sessions integer NOT NULL DEFAULT 0 CHECK(recent_open_sessions>=0),
    last_accounting_at timestamptz,
    last_session_stop_at timestamptz,
    stop_reason varchar(160),
    source varchar(16) NOT NULL DEFAULT 'radacct'
        CHECK(source='radacct'),
    FOREIGN KEY(incident_id,service_id)
        REFERENCES assurance_incident_services(incident_id,service_id)
        ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS idx_assurance_observations_recent
    ON assurance_session_observations(incident_id,service_id,observed_at DESC,id DESC);

-- A verified interruption requires a human statement plus a source reference.
-- This is not automatically calculated from missing interim updates.
CREATE TABLE IF NOT EXISTS assurance_interruptions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    service_id uuid NOT NULL REFERENCES services(id) ON DELETE RESTRICT,
    incident_id uuid REFERENCES noc_incidents(id) ON DELETE RESTRICT,
    ticket_id uuid REFERENCES tickets(id) ON DELETE RESTRICT,
    started_at timestamptz NOT NULL,
    ended_at timestamptz NOT NULL,
    evidence text NOT NULL CHECK(length(trim(evidence)) BETWEEN 20 AND 2000),
    source varchar(20) NOT NULL DEFAULT 'staff_attested'
        CHECK(source='staff_attested'),
    verified_by varchar(100) NOT NULL,
    verified_at timestamptz NOT NULL DEFAULT now(),
    CHECK(ended_at>started_at),
    CHECK(incident_id IS NOT NULL OR ticket_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_assurance_interruptions_service_time
    ON assurance_interruptions(service_id,started_at,ended_at);
CREATE INDEX IF NOT EXISTS idx_assurance_interruptions_incident
    ON assurance_interruptions(incident_id);

-- Exclusion constraint protects non-overlapping verified service intervals
-- even if two API instances receive a concurrent write.
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE assurance_interruptions
    ADD CONSTRAINT assurance_no_overlap_by_service EXCLUDE USING gist (
        service_id WITH =,
        tstzrange(started_at,ended_at,'[)') WITH &&
    );
