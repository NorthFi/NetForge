<!-- root/selfhost/NETWORK_CHANGE_CONTROL.md -->
# Network change control (manual execution)

Migration 018 adds an encrypted, immutable script library, backup-profile
device groups, and version-pinned maintenance plans. No remote executor,
scheduler, SSH command runner or automatic rollback is included. NetForge
never executes script content. Dispatchers view metadata; administrators
create, review, approve and perform manual handoffs.

## Workflow
1. Create a vendor-specific script and an immutable revision (64 KiB max,
   100 revisions per script). Review the exact SHA-256 and content. Do not
   embed credentials or confidential output. Script encryption uses the
   existing Data Protection key ring; back up the keys separately.
2. Create a group of 1–50 enrolled backup profiles. A plan must contain
   only profiles matching the script vendor. Select an explicit canary.
3. Create a plan with a reason and maintenance window (maximum 24 hours).
   The plan freezes the selected revision and ordered targets. Editing a
   group or creating another revision cannot change a submitted plan.
4. A different administrator reviews the exact script, targets and window
   and approves it. Approval is human attestation, not automated validation.
5. During the window, request handoff of the next pending target. The
   latest backup version created in the preceding 24 hours must decrypt
   and match its original length and SHA-256. A stored backup is not proof
   of restorability or current configuration; verify on the actual device.
   Duplicate uploads do not refresh a version's creation time. If an
   unchanged backup is older than 24 hours the gate conservatively blocks.
6. Execute manually using approved device access, verify the change and
   record a success or failure note (no secrets). A failed or uncertain
   outcome halts the entire plan. Only one target can be outstanding; the
   first is the explicit canary. The operator receiving the handoff must
   record its outcome. Outcome recording is allowed after window expiry.
7. Cancel unused plans if needed. Cancellation is blocked while a handoff
   is outstanding. The system cannot recall previously revealed scripts.
   Review access is admin-only and audited but is not a technical barrier
   to an administrator independently executing a script outside NetForge.

## Operational limitations
No device-specific parser, linting, permission analysis, live execution,
maintenance enforcement outside NetForge, rollback verification, automatic
output collection or execution scheduling. An interrupted HTTP response
after handoff can leave a target outstanding; review the pinned version and
record a conservative failure if execution cannot be verified. If the
operator account is unavailable, a different administrator can mark the
outstanding target uncertain and halt the plan, but cannot claim success.
Migration 019 prevents simultaneous outstanding handoffs for the same
backup profile across plans. It does not detect duplicate inventory profiles
representing one physical device. Resolve uncertain device state before
starting another change. State is deliberately conservative. Run the CI suite and test with lab devices before production.
