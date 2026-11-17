-- root/selfhost/db/011_assurance_indexes_audit.sql
-- Enable audited operator-triggered evidence refreshes and avoid sequential
-- scans of long-term RADIUS session history for incident correlations.
ALTER TABLE noc_incident_history
  DROP CONSTRAINT IF EXISTS noc_incident_history_action_check;
ALTER TABLE noc_incident_history
  ADD CONSTRAINT noc_incident_history_action_check
  CHECK(action IN (
    'detected','manual_open','upstream_recovered','upstream_failed_again',
    'acknowledged','note','assigned','resolved','ticket_linked','radius_evidence'));
CREATE INDEX IF NOT EXISTS idx_radacct_username_session_evidence
  ON radacct(username,acctstoptime DESC,acctupdatetime DESC);
