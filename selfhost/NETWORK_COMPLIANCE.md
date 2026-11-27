<!-- root/selfhost/NETWORK_COMPLIANCE.md -->
# RouterOS configuration compliance
Migration 021 adds per-device opt-in policies, append-only baseline history,
exact RouterOS version checks, matching-asset backup freshness checks and
optional scheduled pinned HTTPS inspections. It performs no remote writes.

In Network Compliance, select an enrolled router. Administrators can set the
exact approved RouterOS version (e.g. 7.19.3), optional matching backup profile,
inspection age and backup age limits (1–168 hours), baseline requirement and
read-only polling interval (0 disabled, 15–1440 minutes). Dispatchers can view
policies and evidence. Inspect the current router in RouterOS Management before
approving a baseline with a reason. Approval requires a recent complete
inspection and all required fields. Baselines are immutable, maximum 100.

Checks are evaluated on request, not cached: missing, stale or incomplete
evidence is unknown. A failed check takes precedence over unknown; all
configured checks must pass to be compliant. A firmware version is compared
exactly after removing the channel suffix, not guessed to be latest or secure.
Backup freshness measures newest stored version age, not receipt of duplicate
files or restorability. A matching backup profile is optional.

Baseline comparison uses order-independent multisets of selected static
identity, interface, VLAN, bridge, address, route, PPPoE and DHCP server fields.
Runtime CPU/load/uptime/running state and dynamic address/route rows are not
configuration. Missing required fields cause unknown. Row ordering is not
evaluated; this is not firewall, routing-policy or full export compliance.
A manually approved bad baseline remains bad; approval is human judgment.
The history records who approved and a digest of normalized JSON.

The hosted worker checks due schedules every 30 seconds and inspects at most
10 devices sequentially per cycle. Manual and scheduled requests share the
same four-slot process limiter, pinned TLS, allowlist, 25-second budget and
database device row lock. Due schedules are rechecked under the lock.
A denied/failed poll does not prove downtime. Disabled devices are skipped.
Multiple replicas share the due-time database state, but the four-slot
limit is per process, not cluster-wide. Large fleets may experience queue
delay; choose polling intervals accordingly. There is no automatic NOC
incident creation, exception engine, auto-remediation, additional NTP/DNS or
firewall inspection, or automatic immediate pre-execution backup in this phase.
Physical RouterOS field compatibility must be verified before production.
