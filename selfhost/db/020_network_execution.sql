-- root/selfhost/db/020_network_execution.sql
-- Separate upload-only backup tokens from execution credentials. Agents receive
-- exactly one claim for a queued target; uncertain claims are never requeued.
CREATE TABLE network_execution_agents(
 profile_id uuid PRIMARY KEY REFERENCES backup_profiles(id) ON DELETE RESTRICT,
 address inet NOT NULL CHECK(family(address)=4 AND masklen(address)=32),
 token_hash char(64) NOT NULL CHECK(token_hash ~ '^[0-9A-F]{64}$'),
 enabled boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE network_execution_jobs(
 target_id uuid PRIMARY KEY REFERENCES network_change_targets(id) ON DELETE RESTRICT,
 state varchar(12) NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','claimed','reported','cancelled')),
 queued_at timestamptz NOT NULL DEFAULT now(),
 claimed_at timestamptz,
 deadline timestamptz,
 claim_hash char(64),
 reported_at timestamptz,
 result varchar(20) CHECK(result IN ('exit_zero','nonzero','timeout','refused','output_limit','transport_error','uncertain')),
 output_bytes integer CHECK(output_bytes BETWEEN 0 AND 65536),
 exit_code integer
);
