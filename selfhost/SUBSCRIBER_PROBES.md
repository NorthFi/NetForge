# root/selfhost/SUBSCRIBER_PROBES.md
# NetForge Phase 11 — Subscriber-side diagnostics

This opt-in module extends service assurance with independent DNS and
HTTPS observations from authorized subscriber LAN hosts. It is not a
remote-access service, network scanner, traffic collector or automatic
SLA credit mechanism.

## Architecture and traffic

The Linux agent is a one-shot Python 3.11+ standard-library program.
A local systemd timer invokes it, default every 300 seconds.

- DNS: the subscriber host's operating-system resolver resolves one
  explicitly selected diagnostic hostname. This may use its configured
  recursive resolver (normally UDP/TCP 53, possibly a local stub or
  encrypted upstream) and local caching. It is not an authoritative DNS
  measurement and does not upload resolved addresses.
- HTTPS: one GET to an operator-authorized HTTPS diagnostic endpoint
  on TCP 443. The agent validates the TLS certificate and hostname,
  requires the configured exact 2xx response status, and does not follow
  redirects, read response bodies, discover proxies or send credentials
  to the diagnostic endpoint.
- Upload: outbound HTTPS to the exact NetForge endpoint
  `/api/probe-report/{agent-id}`, with a 256-bit scoped bearer token.
  TLS verification is mandatory; redirects are refused. No listener,
  inbound NAT, port forward or inbound customer firewall rule is needed.
- NetForge itself never connects to the supplied diagnostic endpoints.
  The agent does not accept commands, scripts or remote configuration.

Select endpoints that the ISP operates or has explicit permission to
check, ideally with stable DNS and a lightweight 204 response. Use a
dedicated low-volume test endpoint rather than real customer workloads.
The reporting endpoint must be reachable from the subscriber network;
a separate authorized management path can improve delivery during
subscriber Internet faults. Do not bypass the subscriber path for the
actual DNS/HTTPS tests if investigating customer connectivity.

## Enrollment and deployment

1. Obtain and record the customer's permission to install monitoring
   on a dedicated Linux host connected to the subscriber LAN. Do not
   install silently or on unrelated networks.
2. Sign into NetForge over HTTPS as an administrator and open
   **Subscriber Probes**. Select the subscriber service, a distinct
   vantage name, authorized DNS hostname, authorized HTTPS endpoint,
   expected HTTP status and reporting interval.
3. Confirm enrollment. Download the one-time `agent.json` configuration.
   Check the report URL points to the externally reachable trusted
   NetForge HTTPS origin. Protect the exported file: it contains the
   bearer credential. Never paste it into a ticket or repository.
4. On the authorized Linux host, from the repository checkout root,
   use the following commands. They create a restricted local service
   user and install only the probe script, configuration and units.
   The exported `agent.json` must be in the current directory.

```sh
# root/selfhost/probe-agent/deployment (operator shell commands)
set -eu
if ! id netforge-probe >/dev/null 2>&1; then
  sudo useradd --system --user-group --no-create-home \
    --shell /usr/sbin/nologin netforge-probe
fi
sudo install -d -o root -g root -m 0755 /opt/netforge-probe
sudo install -d -o root -g netforge-probe -m 0750 /etc/netforge-probe
sudo install -o root -g root -m 0755 \
  selfhost/probe-agent/netforge_probe.py /opt/netforge-probe/netforge_probe.py
sudo install -o netforge-probe -g netforge-probe -m 0600 \
  agent.json /etc/netforge-probe/agent.json
sudo install -o root -g root -m 0644 \
  selfhost/probe-agent/netforge-probe.service /etc/systemd/system/
sudo install -o root -g root -m 0644 \
  selfhost/probe-agent/netforge-probe.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl start netforge-probe.service
sudo systemctl enable --now netforge-probe.timer
sudo journalctl -u netforge-probe.service -n 20 --no-pager
```

For a cadence other than 300 seconds, set `OnUnitActiveSec` in a
systemd timer override (matching the enrolled interval, 60–3600
seconds), reload systemd and restart the timer. The configuration
interval describes the server's freshness expectation; it does not
remotely change the local schedule. Maintain correct UTC clock
synchronization and a trusted CA store. The agent does not provide
an insecure TLS bypass.

The process runs as an unprivileged local account, with systemd
filesystem and capability restrictions and a 40-second service
deadline. Each DNS and diagnostic HTTPS check has an eight-second
wall-clock deadline; upload has twelve seconds. A failed upload
exits nonzero. The next timer invocation makes a fresh measurement.
The agent never claims a measurement it could not deliver.

Delete or securely retain the original exported secret after
installation in accordance with local credential-handling policy.

## Reading the evidence

Each service can have up to four distinct enrolled vantage records.
NetForge stores server receipt time, agent-reported measurement time,
DNS result and duration, HTTPS result and duration, response status,
and credential generation. Staff can review history and the latest
evidence side by side with the service's existing CPE and RADIUS
signals.

| Signal | Interpretation |
|---|---|
| checks_passed | Latest timely report succeeded against both configured targets. |
| check_failed | At least one check in a timely report failed or timed out. |
| unknown | No current-generation report, or measurement/receipt too old. |
| disabled | The agent credential has been revoked. |
| CPE up/down/unknown | Recent management-plane evidence, not subscriber forwarding. |
| RADIUS recent_accounting/unknown | Recent open accounting activity, not application reachability. |

Freshness is three times the enrolled interval, based on both
measurement and receipt timestamps. CPE state is independently aged
using its own interval. RADIUS activity uses the configured accounting
freshness policy. These are independent, timestamped signals, not a
proven causal inference. RADIUS correlation uses the current service
username; it is not a historical identity ledger.

An unreachable reporting endpoint may prevent delivery exactly when
subscriber Internet connectivity fails. Missing reports therefore remain
**unknown**, not down. No offline queue or retrospective outage
reconstruction is implemented. A successful HTTPS response verifies
that particular endpoint, not the whole Internet; the DNS check can
be influenced by caching and the hosts file. A compromised agent can
fabricate results. Authentication proves possession of the agent
credential, not hardware identity, physical location or truthfulness.

## Credentials and data controls

- Separate enrollment token per agent, generated with 32 random bytes.
  Only its SHA-256 digest is persisted; staff list/history never
  return it. Enrollment and rotation responses are marked no-store.
- The upload endpoint accepts bounded JSON (maximum 4096 bytes,
  explicit Content-Length), validates configured targets and result
  combinations, and enforces a five-second read deadline.
- Reports must be no more than ten minutes old or one minute in the
  future. New measurements must advance in time. A unique measurement
  UUID and transactional row lock make repeated submissions
  idempotent. New reports are limited to at most one per 30 seconds
  per agent. A per-process concurrency limit provides additional
  protection; configure deployment-side network/edge rate limits for
  public exposure.
- Administrators can revoke or rotate a credential; rotation
  invalidates the old token, starts a new evidence generation and
  resets the current evidence to unknown. History is retained.
  Update the local file manually and verify permissions. Stop the
  local timer if the subscriber withdraws consent.
- Staff endpoints require authenticated admin or dispatcher access,
  with admin-only enrollment/revocation/rotation and CSRF protection.
  Do not publish the raw API container port: use the TLS reverse proxy.
  Do not log Authorization headers or response bodies containing
  enrollment credentials.

No IP resolution records, customer passwords, browsing histories,
payload captures, or diagnostic response contents are collected.
Probe reports are retained as raw history; the existing SNMP retention
policy does not prune them. Plan disk capacity, a dedicated retention
policy and backup/restore tests before large deployments.

## Acceptance boundary

The CI tests use a temporary local TLS endpoint with a trusted test
CA, plus authenticated HTTP API fixtures and PostgreSQL. They cover
TLS validation, response-status validation, redirect refusal, deadline
behavior, secret-file permissions, replay, timestamp validation, rate
limits, generation rotation, revocation, evidence freshness and
subscriber portal isolation. They do not certify connectivity at any
actual customer site, physical CPE behavior, or contractual SLA
availability. Test the agent behind the actual MikroTik subscriber
routing/NAT/firewall topology and validate its reporting path before
widespread rollout.

No agent result creates an attested interruption, modifies billing,
posts a credit, changes RADIUS access, or sends customer notifications.
