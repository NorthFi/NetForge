<!-- root/selfhost/NETWORK_EXECUTION.md -->
# Opt-in RouterOS execution agent
Migration 020 adds per-backup-profile agent credentials and one-shot jobs.
The existing manual workflow is unchanged. Only independently approved plans
may queue the next target, with the existing fresh backup and integrity gate.
No queue automatically advances. Only RouterOS IPv4 profiles are supported.

Enroll an agent in Network Changes; it binds to the inventoried IP and
generates a separate random token shown once. Token hashes are stored, never
SSH private keys. Rotation/revocation is blocked until outstanding agent
handoffs resolve. On the Linux management host, install Python 3 and OpenSSH,
create a dedicated key-only RouterOS account with the minimum required
policies, restrict its source IP, and pin the host key in a trusted known_hosts
file. Do not reuse REST, backup or general administrator credentials.

Copy execution-agent/netforge_execute.py to a protected directory. Create a
private JSON config (0600, regular file), with fields server (bare HTTPS origin),
profile_id (backup profile UUID), token (execution token), router_ip (saved
IPv4), router_user, identity_file, known_hosts (absolute file paths),
optional router_port (22 default) and ca_file (private trusted CA), and
allowed_sha256 (list of reviewed exact revision SHA-256 hashes from NetForge).
Locally review each permitted command before adding its hash. The local
allowlist is independent of NetForge. Never put secrets in scripts, configuration
repositories or command-line arguments. The script itself is passed as a
remote SSH command and may be visible to privileged local processes.

Queue an approved target using Queue agent, then run the agent with the
private config path as its sole argument. It claims once within five minutes,
requires at least two minutes of remaining maintenance window, verifies
profile, IPv4 address, script hash and local allowlist, and invokes SSH once.
Only single-line printable ASCII RouterOS commands starting with / or :,
at most 4096 characters, are supported for older RouterOS compatibility.
TCP 443 (HTTPS) to NetForge and TCP 22 (or configured SSH port) to the router
are outbound only from the agent. HTTPS uses normal CA/hostname validation;
SSH requires strict host key checking. No local shell is used.

The local command timeout is 60 seconds, output bounded at 64 KiB; only result,
exit code and byte count are sent to NetForge, never raw output. RouterOS may
return error text despite zero SSH exit status; a zero exit code is NOT
verified success. A nonzero, refused, timed-out or late result cannot be
marked successful. Operator verification remains mandatory. A lost response
or report never requeues a claimed job. Another administrator can resolve an
uncertain claim only two minutes after its deadline, after independently
checking the device and confirming it is safe to continue. Killing local SSH
does not guarantee remote execution stopped. No automatic rollback.

The queue uses the latest stored backup at handoff and rechecks its bytes and
age at claim. Automatic immediate pre-execution collection, scheduled
execution, raw output storage, postcondition probes, and actual physical
RouterOS validation are not implemented. Use the backup collector before
queuing changes; deduplicated stale versions may block a queue. Review
network change control documentation for manual alternatives.

Reference: https://help.mikrotik.com/docs/spaces/ROS/pages/132350014/SSH
