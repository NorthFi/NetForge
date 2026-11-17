-- root/selfhost/db/009_noc_incidents.sql
-- Actionable NOC incidents from explicitly classified upstream TCP transitions.
-- Detection is evidence, not proof of subscriber loss. No automatic billing,
-- RADIUS changes or customer messages are initiated by this migration.
CREATE TABLE IF NOT EXISTS noc_incidents (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    highsite_id uuid NOT NULL REFERENCES highsites(id) ON DELETE RESTRICT,
    source_event_id uuid REFERENCES site_monitor_events(id) ON DELETE RESTRICT,
    origin varchar(12) NOT NULL DEFAULT 'monitor'
        CHECK(origin IN ('monitor','manual')),
    summary varchar(200) NOT NULL CHECK(length(trim(summary))>=5),
    severity varchar(12) NOT NULL DEFAULT 'high'
        CHECK(severity IN ('low','normal','high','critical')),
    status varchar(16) NOT NULL DEFAULT 'open'
        CHECK(status IN ('open','acknowledged','monitoring','resolved')),
    opened_at timestamptz NOT NULL DEFAULT now(),
    recovered_at timestamptz,
    resolved_at timestamptz,
    acknowledged_at timestamptz,
    acknowledged_by varchar(100),
    resolved_by varchar(100),
    assigned_to varchar(100),
    resolution_note varchar(2000),
    potential_customers integer NOT NULL DEFAULT 0 CHECK(potential_customers>=0),
    sectors_snapshot integer NOT NULL DEFAULT 0 CHECK(sectors_snapshot>=0),
    revision integer NOT NULL DEFAULT 1 CHECK(revision>0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(source_event_id),
    CHECK (resolved_at IS NULL OR status='resolved'),
    CHECK (status!='resolved' OR (resolved_at IS NOT NULL AND
          resolution_note IS NOT NULL AND length(trim(resolution_note))>=8))
);
-- Exactly one unresolved highsite incident, even if multiple upstream TCP
-- devices independently fail. A resolved incident is immutable history.
CREATE UNIQUE INDEX IF NOT EXISTS uq_noc_active_highsite
    ON noc_incidents(highsite_id) WHERE status!='resolved';
CREATE INDEX IF NOT EXISTS idx_noc_incidents_opened
    ON noc_incidents(opened_at DESC);

CREATE TABLE IF NOT EXISTS noc_incident_customers (
    incident_id uuid NOT NULL REFERENCES noc_incidents(id) ON DELETE RESTRICT,
    customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
    account_snapshot varchar(100) NOT NULL,
    first_name_snapshot varchar(120) NOT NULL,
    surname_snapshot varchar(120) NOT NULL DEFAULT '',
    active_services integer NOT NULL DEFAULT 0 CHECK(active_services>=0),
    PRIMARY KEY(incident_id,customer_id)
);
CREATE INDEX IF NOT EXISTS idx_noc_customers_customer ON noc_incident_customers(customer_id);

CREATE TABLE IF NOT EXISTS noc_incident_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_id uuid NOT NULL REFERENCES noc_incidents(id) ON DELETE RESTRICT,
    action varchar(24) NOT NULL CHECK(action IN(
        'detected','manual_open','upstream_recovered','upstream_failed_again',
        'acknowledged','note','assigned','resolved','ticket_linked')),
    actor varchar(100) NOT NULL,
    detail varchar(2000) NOT NULL DEFAULT '',
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_noc_history ON noc_incident_history(incident_id,occurred_at,id);

CREATE TABLE IF NOT EXISTS noc_incident_tickets (
    incident_id uuid PRIMARY KEY REFERENCES noc_incidents(id) ON DELETE RESTRICT,
    ticket_id uuid NOT NULL REFERENCES tickets(id) ON DELETE RESTRICT,
    linked_by varchar(100) NOT NULL,
    linked_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_noc_tickets_ticket ON noc_incident_tickets(ticket_id);
