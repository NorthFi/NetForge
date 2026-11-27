<!-- root/selfhost/ROUTEROS_MANAGEMENT.md -->
# RouterOS v7 management: read-only inspection

The module is manual, administrator-enrolled and disabled by default.
Dispatchers can inspect stored results but cannot initiate connections or
change settings. It never writes to a router, runs scripts or retrieves full
configuration exports. It uses HTTPS REST (normally TCP 443) and Basic auth,
not RouterOS API TCP 8728/8729.

## Deployment

Set NETFORGE_ROUTEROS_ALLOWED_CIDRS to comma-separated canonical IPv4
management subnets in .env (or the native service environment). Empty or
malformed configuration denies all enrollment and inspection. Example:
10.48.0.0/16,100.64.20.0/24. Use only actual management ranges and narrow them
further with egress firewall rules; the application permits explicitly
enrolled HTTPS ports 1–65535. Never use broad public ranges. Restart the API
after changing its environment.

On RouterOS v7, provision a dedicated account whose group has only read and
rest-api policies (verify on the actual firmware), and restrict its address
to the actual NetForge egress source. Enable www-ssl with a valid certificate,
disable plain HTTP if not needed, and restrict the service and input chain
to the trusted NetForge management source. Do not replace existing firewall
rules blindly. Use a VPN or routed management network; allow TCP from the
actual container/host source to the selected router port. No inbound port on
NetForge is required for this feature. Avoid NAT if possible; when Docker
SNAT is present, authorize the observed translated source.

Obtain the leaf certificate SHA-256 fingerprint through an independently
trusted channel, e.g. from the certificate file used on the router:
`openssl x509 -in router.crt -noout -fingerprint -sha256`
Remove colons before enrollment. Do not trust a fingerprint obtained only
from an unauthenticated connection. Exact leaf pin and validity dates replace
CA/hostname validation; there is no insecure TLS option. Rotate the pin
when the certificate changes. Credential rotation disables the profile and
clears its snapshot until re-enabled and inspected.

Enroll a saved NAS or equipment entry in RouterOS management, verify the
target, enable it and select Inspect now. The snapshot contains system
resource, identity, firmware, interface, VLAN, bridge, IPv4 address, route,
PPPoE server and DHCP server fields. The REST request is a fixed GET with
a property list, and the parser independently strips unexpected fields.
The module never fetches PPP secrets, scripts or full export data. Results
may be partial because of device capabilities, permissions, timeouts or
size limits. The last snapshot is replaced per attempt; this is not a
historical backup, monitoring alarm or proof of reachability.

Limits: 4 concurrent polls per API process, 60 seconds between attempts per
device, 25-second total request budget, 5 seconds per section, 512 KiB and
1000 rows per section. Larger lists are marked unavailable rather than
silently truncated. Existing database and DataProtection key backups must
be protected separately; losing keys makes stored credentials unusable.
Database-only encryption does not protect against compromise of both keys
and DB. Keep key storage private; no secret is included in list responses.

Migration 016 is additive. Test in a lab before production use. Native
RouterOS REST reference:
https://help.mikrotik.com/docs/spaces/ROS/pages/47579162/REST%20API
