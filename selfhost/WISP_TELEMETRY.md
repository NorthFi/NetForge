# root/selfhost/WISP_TELEMETRY.md
# NetForge — SNMPv3 telemetry and highsite incident correlation (Phase 6)

This phase extends the Linux-container/.NET 11 WISP control plane with read-only
SNMPv3 polling, historical interface bitrate, manually assigned highsite
upstream relationships, and contextual operational alerts. The previous
billing, CRM, customer portal, TCP monitoring and FreeRADIUS features remain
unchanged. This is **not** an RF link planner, ICMP/SNMP network discovery
tool, or a replacement for vendor-specific device management.

## 1. Security architecture

- NetForge only accepts **SNMPv3 authPriv** with SHA-256 or SHA-1
  authentication and AES128 privacy. SNMPv1/v2c community strings, MD5,
  unauthenticated and unencrypted SNMPv3 requests are not supported.
  SHA-256 is preferred when supported by the device; some RouterOS
  hardware/versions support only SHA-1 and require that compatibility mode.
- Each SNMPv3 profile references **one already inventoried RADIUS NAS or
  equipment record**. The device must have a literal IPv4 address in
  inventory. Operators cannot submit unbounded hostnames, redirects or
  URLs as polling destinations.
- Credential pairs are encrypted in PostgreSQL with ASP.NET Core Data
  Protection using the application's existing key-ring volume
  `netforge_keys`. **Back up and protect the Data Protection key ring**.
  Losing those keys prevents decrypting the saved SNMPv3 credentials.
- The API runs Net-SNMP's `snmpget` binary in an isolated child process
  without a shell. The SNMPv3 authentication and privacy secrets are **never
  included in command-line arguments or HTTP responses**.
- Each query creates a random 0700 temporary directory and a 0600
  `snmp.conf`, sets `SNMPCONFPATH` to that directory, and removes
  the configuration immediately after polling. A hard crash may leave an
  isolated temporary directory; configure host/container temp cleanup.
- Credentials are never returned through staff APIs. Authenticated
  administrators alone can enroll, enable, update or rotate profiles.
  Dispatchers may view device summaries, counters and alerts.
- Polling is disabled on every new device until explicitly enabled.
  No device configuration is changed by SNMP requests.

Net-SNMP is installed inside the API Docker image. Its binary path defaults
to `/usr/bin/snmpget`. Native Windows deployments require installing and
safely configuring a compatible Net-SNMP executable, then supplying
`NETFORGE_SNMPGET_PATH` in the host environment.

## 2. MikroTik / WISP device setup

Use your RouterOS-supported SNMPv3 read-only user with authentication and
privacy enabled. Ensure the configured SNMPv3 username, hash and AES privacy
secrets on the device match the NetForge profile. The private management
network must route from the API container to the NAS or radio management IP.

- Protocol and destination: **UDP 161** (configurable per device profile).
- Recommended source restrictions: permit only the API monitoring host's
  routed/NAT source IP to the SNMP service. Deny public access and other
  unintended source ranges using RouterOS and upstream firewalls.
- SNMPv3 interface index: choose the actual IF-MIB **ifIndex** for the
  monitored physical or virtual interface. Identify the correct index in
  the device's SNMP tree; do not assume Ethernet 1 is index 1.
- Standard OIDs queried as one SNMPv3 GET:
  - `1.3.6.1.2.1.1.3.0` — sysUpTime, hundredths of seconds.
  - `1.3.6.1.2.1.2.2.1.8.<ifIndex>` — ifOperStatus.
  - `1.3.6.1.2.1.31.1.1.1.6.<ifIndex>` — ifHCInOctets (64-bit).
  - `1.3.6.1.2.1.31.1.1.1.10.<ifIndex>` — ifHCOutOctets (64-bit).

If a device does not implement the IF-MIB 64-bit counters on that
interface, this phase cannot compute a throughput graph for it. Do not
silently substitute 32-bit counters at high data rates.

The `SNMP Telemetry` screen stores profile metadata including device
identity, interface index and optional highsite association. Rotating
credentials in NetForge does **not** update credentials on the device.
The default poll interval is 300 seconds; allowed range is 60–3600 seconds.

## 3. Throughput and history

Each successful SNMP GET creates one timestamped PostgreSQL
`snmp_samples` record with uptime ticks, ifOperStatus and the exact
64-bit counters. The next sample computes:

```text
interface input bps  = (new ifHCInOctets  - previous ifHCInOctets ) × 8 / seconds
interface output bps = (new ifHCOutOctets - previous ifHCOutOctets) × 8 / seconds
```

If the device rebooted, uptime wrapped, counters decreased, elapsed time
is not positive or the measured result is implausibly high, the sample
is **marked as a reset** and no bitrate is reported for that interval.
Data is not fabricated or backfilled for missing samples.

The staff view offers a bitrate trend, up to 1,000 recent samples in an
hour-range filter (1–720 hours). This is per selected device interface:
**input/output directions are relative to that device's interface** and
must not automatically be labeled as subscriber upload/download.

The current database has no automated retention/partitioning for
`snmp_samples`. Before large-scale deployment, design retention,
rollups, partitioning and storage-capacity alarms for your site/device
count. The table index on `device_id,observed_at` supports current
short-window queries, but is not a substitute for operational storage
planning.

## 4. Device DOWN / recovery alerts

SNMP device health moves from `unknown` to `up` when the selected
interface reports `ifOperStatus=up(1)`. Three consecutive failed
queries or non-UP statuses mark it `down`; a healthy reading recovers
it. Only transitions create `snmp_events` and optional outbox alerts,
not every failed poll.

A failed SNMP query can mean routing, access-control, authentication
errors or device CPU overload; an interface DOWN result is stronger
but still not proof that every customer loses connectivity. The staff
interface displays these distinctions and preserves failure counts
without leaking SNMP provider stderr.

## 5. Highsite impact correlation

The existing TCP Network Monitoring screen now permits administrators
to assign each TCP monitor to a known highsite and mark it as a
**critical upstream** only after confirmation. Assigning a monitor
to a new site resets its prior TCP state to `unknown` to avoid
attributing stale failures to the wrong tower.

When a tagged upstream TCP monitor goes DOWN:
1. The event is recorded normally in `monitor_events`.
2. If this is the **first DOWN upstream** for that site, a
   `site_monitor_events` event is created, including a snapshot of
   active customers and sectors attached to that highsite.
3. The system queues one **potential highsite upstream outage**
   notification if operations email/Telegram delivery is enabled.
4. Other monitored TCP devices and SNMPv3 interfaces associated with
   the same highsite still record DOWN/RECOVERY events, but their
   notification attempts are suppressed while the upstream failure
   is present.
5. When the last DOWN upstream recovers, a single highsite recovery
   event is generated. Current inventory counts and site state are
   available through the **Highsite Impact** screen.

Correlation uses **explicit highsite tags**, not a network-wide
dependency graph or geographical inference. Multiple independent
upstream monitors at one site are not modeled as failover paths:
the status remains 'potential upstream outage' if at least one tagged
upstream is DOWN. Do not interpret the customer count as proven
subscriber loss; it is a *maximum inventory exposure estimate*.

Alert suppression affects **outbound operations notifications only**.
History is preserved and suppression is visible on individual events.
Customer account balances, invoices, payment processing and subscriber
RADIUS authentication are never affected by monitoring state alone.

## 6. API endpoints

All staff endpoints require authentication; changing configuration,
credentials or triggering polls requires administrator rights and CSRF.

| Endpoint | Description |
| --- | --- |
| `GET/POST /api/snmp/devices` | Profile listing without secrets; add encrypted profile |
| `PUT /api/snmp/devices/{id}` | Enable/disable, interval, ifIndex |
| `PUT /api/snmp/devices/{id}/credentials` | Rotate both auth/privacy passwords |
| `POST /api/snmp/devices/{id}/poll` | Manually poll a saved enabled device |
| `GET /api/snmp/devices/{id}/history?hours=168` | Interface counters, status, bps |
| `GET /api/snmp/events` | UP/DOWN transition history and suppression |
| `PUT /api/monitor/targets/{id}/site` | Associate TCP target to site / tag upstream |
| `GET /api/monitor/sites` | Current per-site exposure / upstream states |
| `GET /api/monitor/site-events` | Potential upstream outage/recovery history |

## 7. Deployment and testing

1. **Back up PostgreSQL and the Data Protection keys** separately.
2. Pull the updated development branch and rebuild the API container:
   `docker compose up -d --build api`.
3. Migration `006_wisp_telemetry.sql` applies once, with checksum
   validation and a PostgreSQL advisory lock.
4. Verify **no SNMP profiles exist and nothing is enabled** by default.
   Previously configured TCP monitors retain their existing settings;
   no site/upstream tags are inferred for NAS devices.
5. Test an isolated lab NAS with a dedicated read-only SNMPv3
   authPriv user, then inspect raw samples and ifIndex.
6. Confirm Docker outbound UDP 161 reachability and RouterOS firewall
   source matching before polling a production device.
7. Explicitly tag a test site's upstream TCP monitor, then test
   DOWN/recovery alerts, current site counts and child suppression.
8. Only enable real operations email/Telegram after testing
   notification providers and the acceptable deduplication policy.
9. Validate backup/restore including Data Protection key ring. Do not
   run the restored database with an unrelated key ring and expect
   encrypted SNMP passwords to decrypt.

The CI suite includes a deterministic response fixture that checks
file permissions, absence of secrets in process args, counter resets,
rate computation and site correlation against PostgreSQL.
A separate **real Net-SNMP agent** test exercises SHA-256/AES authPriv
negotiation with numeric IF-MIB OIDs. Neither test establishes
physical MikroTik compatibility: test on the exact RouterOS release
and RF hardware before using it with customers.
