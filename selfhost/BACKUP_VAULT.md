<!-- root/selfhost/BACKUP_VAULT.md -->
# Multi-vendor configuration backup vault

Migration 017 adds inventory-linked profiles for MikroTik, Cambium, Ubiquiti,
Mimosa and other vendors. Administrators enroll a profile, upload a file,
download a verified original, compare UTF-8 text versions, and issue or revoke
a dedicated upload-only token. Dispatchers see metadata only. Other roles
have no vault access. Files are not executed, unpacked or served inline.
A backup is operator-attested, not verified as restorable.

Each file is authenticated-encrypted using ASP.NET Data Protection with a
profile/version-specific purpose, with a SHA-256 digest and byte length
verified on download. Back up both PostgreSQL and the separate Data
Protection key volume. Database plus key compromise exposes the backups.
Uploads are limited to 4 MiB each and 100 versions per profile, with no
automatic deletion; monitor capacity. Identical latest content of the same
kind is deduplicated. Text comparison shows the order-sensitive contiguous
changed range, not a minimal diff; line endings are normalized for preview
only. Original bytes are preserved. Comparison is capped at 100001 lines.
RouterOS export timestamps can change the byte hash even when the configuration
is unchanged; deduplication does not strip headers or normalize file bytes. No restore is performed by NetForge.

## Collector deployment (Linux)

The standard-library Python collector supports either an existing file
produced by a vendor's approved tooling or a fixed RouterOS SSH command
`/export compact`. This is a text export, not a full binary backup; RouterOS
omits secrets by default and some settings cannot be exported. Do not grant
sensitive, write, policy, reboot or FTP permissions to this SSH account.
Use a separate key-only RouterOS user with read,ssh policies, restrict the
source address and allow TCP 22 (or the enrolled SSH port) through the router
input firewall. Pin the host key in known_hosts using independently verified
key material. No auto-accept. Existing REST credentials are not reused.

Install the collector and service/timer templates under /opt/netforge and
/etc/systemd/system respectively; create the unprivileged netforge-backup
system user and private /etc/netforge/backup directory. Create one private
JSON configuration per profile, owned by that service user, mode 0600.
Required fields: server (HTTPS origin), profile_id (UUID), token (from UI),
mode (routeros-ssh or file). For routeros-ssh also specify router_ip (IPv4),
router_user, identity_file and known_hosts (absolute paths under /etc/netforge
or /opt/netforge, accessible to the service user), optionally router_port.
For file mode specify file (absolute path to completed regular file) and
kind (text or binary). Optional ca_file points to a trusted CA PEM file
for a private NetForge TLS certificate. No TLS bypass is available.
Never put tokens or private keys into Git or command-line arguments.

Run the service manually to validate a particular profile before enabling its
daily timer. For a profile file named tower01.json, the service and timer
instances are netforge-backup@tower01.service and netforge-backup@tower01.timer.
The timer is daily with up to 30 minutes jitter and missed-run catch-up.
Collector-to-NetForge HTTPS is normally TCP 443; router SSH is normally TCP 22.
No inbound access to the collector is required. Check journal exit status and
vault last-received timestamps. Do not assume file mode refreshes the source:
the existing vendor backup job must write a completed file atomically first.
A changed backup upload is limited to once per minute per profile; retry
later if 429. Token rotation invalidates previous tokens.

Cambium and Ubiquiti have manual/external-file ingestion only; Mimosa likewise.
Native vendor extraction, binary RouterOS export automation, retention pruning,
restore drills, central scheduling and script deployment remain future work.
Test actual RouterOS SSH output and host-key handling with the deployed
firmware before production; CI uses disposable fixtures, not a physical router.

References:
https://manual.mikrotik.com/docs/getting-started/configuration-management/
https://manual.mikrotik.com/docs/developer-guides/rest-api/
