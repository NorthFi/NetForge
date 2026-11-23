-- root/selfhost/db/012_sla_credits_cpe_probes.sql
-- Additive Phase 10: versioned internal SLA schedules, gated invoice credits,
-- and read-only customer-owned CPE management reachability observations.
-- Nothing is enrolled, polled or posted to the ledger automatically.
CREATE TABLE IF NOT EXISTS assurance_sla_versions (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 service_id uuid NOT NULL REFERENCES services(id) ON DELETE RESTRICT,
 version integer NOT NULL CHECK(version BETWEEN 1 AND 10000),
 effective_from date NOT NULL,
 target_percent numeric(6,3) NOT NULL CHECK(target_percent BETWEEN 90 AND 99.999),
 credit_cap_percent numeric(5,2) NOT NULL CHECK(credit_cap_percent BETWEEN 0 AND 100),
 agreement_reference varchar(160) NOT NULL CHECK(length(btrim(agreement_reference))>=5),
 exclusions varchar(2000) NOT NULL DEFAULT '',
 created_by varchar(100) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(service_id,version),
 UNIQUE(service_id,effective_from)
);
CREATE INDEX IF NOT EXISTS idx_assurance_sla_service_effective
 ON assurance_sla_versions(service_id,effective_from DESC);
-- Never mutate a signed-off term snapshot, including after later versions.
CREATE TRIGGER assurance_sla_immutable
 BEFORE UPDATE OR DELETE ON assurance_sla_versions
 FOR EACH ROW EXECUTE FUNCTION netforge_ledger_immutable();

CREATE TABLE IF NOT EXISTS assurance_credit_requests (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 service_id uuid NOT NULL REFERENCES services(id) ON DELETE RESTRICT,
 interruption_id uuid NOT NULL REFERENCES assurance_interruptions(id) ON DELETE RESTRICT,
 sla_version_id uuid NOT NULL REFERENCES assurance_sla_versions(id) ON DELETE RESTRICT,
 invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE RESTRICT,
 amount numeric(12,2) NOT NULL CHECK(amount>0),
 reason varchar(300) NOT NULL CHECK(length(btrim(reason))>=12),
 status varchar(12) NOT NULL DEFAULT 'proposed'
   CHECK(status IN ('proposed','approved','rejected','posted')),
 proposed_by varchar(100) NOT NULL,
 proposed_at timestamptz NOT NULL DEFAULT now(),
 reviewed_by varchar(100),
 reviewed_at timestamptz,
 review_reason varchar(300),
 posted_by varchar(100),
 posted_at timestamptz,
 credit_note_id uuid UNIQUE REFERENCES credit_notes(id) ON DELETE RESTRICT,
 CHECK((status='proposed' AND reviewed_by IS NULL AND posted_at IS NULL)
    OR (status='rejected' AND reviewed_by IS NOT NULL AND
        posted_at IS NULL AND credit_note_id IS NULL)
    OR (status='approved' AND reviewed_by IS NOT NULL AND
        posted_at IS NULL AND credit_note_id IS NULL)
    OR (status='posted' AND reviewed_by IS NOT NULL AND
        posted_at IS NOT NULL AND posted_by IS NOT NULL AND credit_note_id IS NOT NULL)),
 CHECK(reviewed_by IS NULL OR lower(reviewed_by)<>lower(proposed_by))
);
CREATE INDEX IF NOT EXISTS idx_assurance_credit_requests_status
 ON assurance_credit_requests(status,proposed_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS uq_assurance_credit_active_interruption_invoice
 ON assurance_credit_requests(interruption_id,invoice_id)
 WHERE status IN ('proposed','approved','posted');

CREATE TABLE IF NOT EXISTS assurance_cpe_probes (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 service_id uuid NOT NULL UNIQUE REFERENCES services(id) ON DELETE RESTRICT,
 equipment_id uuid NOT NULL REFERENCES equipment(id) ON DELETE RESTRICT,
 tcp_port integer NOT NULL CHECK(tcp_port IN (22,80,443,8291,8729)),
 enabled boolean NOT NULL DEFAULT false,
 interval_seconds integer NOT NULL DEFAULT 300
    CHECK(interval_seconds BETWEEN 60 AND 3600),
 next_probe_at timestamptz NOT NULL DEFAULT now(),
 state varchar(8) NOT NULL DEFAULT 'unknown'
    CHECK(state IN ('unknown','up','down')),
 failure_streak integer NOT NULL DEFAULT 0 CHECK(failure_streak BETWEEN 0 AND 10000),
 last_probed_at timestamptz,
 last_latency_ms integer,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_assurance_cpe_probe_due ON assurance_cpe_probes(next_probe_at)
 WHERE enabled=true;
CREATE TABLE IF NOT EXISTS assurance_cpe_samples (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 probe_id uuid NOT NULL REFERENCES assurance_cpe_probes(id) ON DELETE RESTRICT,
 observed_at timestamptz NOT NULL DEFAULT now(),
 reachable boolean NOT NULL,
 latency_ms integer CHECK(latency_ms>=0),
 state varchar(8) NOT NULL CHECK(state IN ('unknown','up','down')),
 CHECK((reachable AND latency_ms IS NOT NULL) OR
       (NOT reachable AND latency_ms IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_assurance_cpe_samples_recent
 ON assurance_cpe_samples(probe_id,observed_at DESC,id DESC);
