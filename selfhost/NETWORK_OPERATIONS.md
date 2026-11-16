# root/selfhost/NETWORK_OPERATIONS.md
# NetForge network operations — Phase 2

This document describes the **implemented** control plane in `netforge-selfhost-dotnet`. It is an independent RADIUS/CRM stack using ASP.NET Core 11, PostgreSQL and FreeRADIUS. Production cutover requires independent acceptance testing with the actual MikroTik RouterOS version, NAS hardware and firewall topology.

## Supported operations

| Operation | NetForge action | Network effect |
| --- | --- | --- |
| Create RADIUS NAS | RADIUS NAS → Add | Writes NAS IP, shortname and shared secret to SQL; **restart FreeRADIUS** for new authentication clients |
| Enable or configure NAS Disconnect | NAS details → Disconnect settings | Enables per-NAS authorized UDP Disconnect requests at configured port; disabled by default |
| Generate MikroTik setup | NAS details → Generate RouterOS setup instructions | Locally creates RouterOS commands from a supplied server IP and re-entered NAS secret; does not execute commands |
| Rotate NAS secret | NAS details → Rotate secret | Changes SQL secret; **update the NAS and restart FreeRADIUS**; downtime possible |
| Assign service speed | Service details → Change package | Updates subscription and `Mikrotik-Rate-Limit` SQL reply transactionally; takes effect at next login |
| Disable PPPoE login | Service details → Suspend | Writes `Auth-Type := Reject`; retains password, does not disconnect existing session |
| Restore PPPoE login | Service details → Reactivate | Removes only the reject check after validating customer status and stored credential |
| Disconnect live session | RADIUS Sessions → recorded session → Disconnect | Sends RFC 5176 Disconnect-Request to a configured NAS with server-verified response |
| Manual accounting | RADIUS Usage | Aggregates stored `radacct` records by session start month; no changes sent to NAS |
| Opt-in debt controls | Billing Policy | Background policy suspends/restores **new PPPoE authentication only**, no automatic disconnect |

## MikroTik RouterOS and routing

- RADIUS authentication requests use **UDP 1812** to FreeRADIUS.
- Accounting requests and interim updates use **UDP 1813** to FreeRADIUS.
- MikroTik RouterOS supports unsolicited Disconnect Messages through `/radius incoming`. The published RouterOS manual currently documents the default listener on **UDP 1700**. NetForge uses 1700 for `vendor=mikrotik` by default; configure a different port if your device has a different explicit listener.
- The RADIUS host must have a route to the NAS's **source/accounting IP** recorded in `radacct.nasipaddress`, and the NAS's configured `/radius address` must trust that RADIUS server's source address. If the API container has a Docker NAT source address, host/network firewall and source routing must be configured accordingly. An ACK cannot be guaranteed across NAT without proper source/return routing.
- Restrict inbound NAS authentication/accounting traffic at the host firewall to approved NAS source IPs. Restrict each NAS Disconnect listener to the RADIUS management host. Never expose the management API or PostgreSQL directly to the Internet.
- RouterOS local PPP secrets are checked before RADIUS. Avoid duplicate local PPP usernames masking RADIUS provisioning.
- For PPP accounting, enable RADIUS and configure interim updates (e.g. every five minutes) on the NAS. A stale `radacct` row does **not** confirm an active subscriber.
- To avoid mass disconnections after a NAS reboot, NetForge will only send Disconnect-Request if the session has an accounting update less than 30 minutes old; operators should verify on the NAS if necessary.

## Response handling and audit

1. An administrator selects a **specific** `radacctid`, enters a reason, and confirms the request.
2. The API resolves the corresponding `User-Name`, `Acct-Session-Id`, and `NAS-IP-Address` from SQL accounting, then matches an enrolled NAS by actual IP.
3. A unique request ID prevents accidental retries. A short per-session cooldown guards against concurrent repeated clicks.
4. The API sends RADIUS Disconnect-Request (Code 40), verifies its response against the stored secret and Request Authenticator, and records an ACK, NAK, timeout or error without revealing secrets.
5. **ACK means the NAS acknowledged**, not that accounting Stop has arrived. `radius_operations` preserves the request reason, actor, status and timestamp.
6. Only the administrator can initiate disconnects or change NAS secrets/settings.

Because RADIUS Dynamic Authorization uses legacy MD5 authenticators per the protocol, this must run **only on a trusted private/VPN control network**. The implementation does not currently provide RadSec or full RFC 5176 Message-Authenticator policy. Do not use it to disconnect sessions on untrusted public networks.

## Speed profiles

For a package with upload 10 Mbps and download 20 Mbps, NetForge computes `10M/20M` when its optional `mikrotik_rate_limit` field is empty. MikroTik's receive rate corresponds to traffic from the subscriber into the NAS; its transmit rate corresponds to subscriber download. Complex RouterOS burst syntax is deliberately rejected until an explicit, validated rate-plan model is implemented.

Package changes update the RADIUS reply for the subscriber. **They do not modify active sessions immediately**; operators may choose an individual session disconnect to make a new session use the updated reply.

## Overdue billing policy

The default database policy is `enabled=false` and `restore_when_paid=false`, with a seven-day grace period. When enabled:

- An active service belonging to an active customer is suspended when that customer has an **issued** invoice with a positive outstanding amount and `due_date` earlier than the current day minus the grace period.
- Suspensions add `Auth-Type := Reject`, retain PPPoE passwords and record `suspension_reason='overdue'`.
- When `restore_when_paid=true`, only services suspended specifically for overdue billing can be restored after their overdue balance is resolved. Manual suspensions remain untouched.
- The worker runs every ten minutes and uses a PostgreSQL transaction-level advisory lock to prevent simultaneous policy changes across instances.
- A separate **Run policy now** action is available to administrators for controlled testing.

**No automatic disconnect** is sent during enforcement; existing PPPoE sessions remain connected until normal termination or explicit authorized action. A robust production billing system requires credit notes, journals, reconciliation, tax handling, refunds, pro-rating, bad-debt policies and operator notifications before automated debt enforcement is appropriate for paying customers.

## Operational failure modes

| Condition | Behavior |
| --- | --- |
| FreeRADIUS unreachable | New subscriber authentications fail according to NAS fallback; local router PPP records may continue to work |
| PostgreSQL unreachable | API and SQL-backed RADIUS fail; restore connectivity before making changes |
| New/rotated NAS secret not activated | NAS and FreeRADIUS mismatch until configured and container restarted |
| Disconnect-Request timeout | **Unknown** session state; inspect NAS and accounting, do not assume session stayed active |
| Disconnect-NAK | NAS rejected session identifiers or request; investigate router logs and port/firewall configuration |
| Invalid signed response | API returns error and does not record ACK |
| Stale accounting | Disconnect blocked (30-minute freshness guard) |
| Customer reactivated while overdue | Admin override may be automatically suspended on the next enabled billing-policy run |
| App restarted after schema upgrade | Idempotent SQL migrations are rerun; database backups remain essential |

## Pre-production checklist

Run the GitHub Actions integration tests; check the target RouterOS release; test RADIUS authentication, accounting Start/Interim/Stop, rate enforcement, suspend, restore, and Disconnect-ACK against a non-customer NAS; verify UDP source routing and firewall; confirm database backup/restore; verify client-facing maintenance communication and rollback plan.

No changes in this branch affect the historical cPanel/PHP branch or the live Published branch.
