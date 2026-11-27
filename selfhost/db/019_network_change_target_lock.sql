-- root/selfhost/db/019_network_change_target_lock.sql
-- Cross-plan guard: never allow two unresolved handoffs to the same enrolled
-- backup profile. A second inventory record for the same physical device
-- remains an operator data-quality problem.
CREATE UNIQUE INDEX network_change_one_handoff_per_profile
 ON network_change_targets(profile_id) WHERE state='handed_off';
