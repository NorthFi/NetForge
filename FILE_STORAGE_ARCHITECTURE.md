# root/FILE_STORAGE_ARCHITECTURE.md
# NetForge initial deployment: JSON file storage

## Constraints
- Host under https://northfi.co.za/netforge/ on basic Afrihost cPanel.
- No SQL database, Docker, persistent process, or root access assumed.
- PHP support and private writable filesystem must be verified.
- Migration to self-hosted PostgreSQL planned later.

## Data model
JSON is canonical storage; CSV is import/export only.
Use stable UUID references and explicit schema versions.

Suggested collections:
- customers.json
- services.json
- highsites.json
- sectors.json
- backhauls.json
- equipment.json
- customer_assignments.json
- scripts.json
- script_jobs.json
- users.json
- audit_logs.json
- settings.json

## Storage safety
- Store outside public_html whenever possible; otherwise enforce web-server deny rules and verify they work.
- No plaintext passwords, device credentials, API tokens, or executable scripts in public-accessible paths.
- Acquire exclusive locks using a separate persistent lock file; write to a temporary file on the same filesystem, fsync when supported, and rename atomically.
- Validate schemas, enforce referential integrity and optimistic revision checks.
- For multi-file operations use a write-ahead journal or a single transactionally updated aggregate until a transaction layer is implemented.
- Rotate backups, maintain restore tests, and bound audit-log size.
- Use server-side authentication, CSRF protection, RBAC, and rate limiting.
- Expose no generic file write/read endpoints.

## Automation
A separately hosted Python agent polls approved jobs over outbound authenticated HTTPS. The web app never executes arbitrary uploaded scripts. Script execution must be isolated and auditable.

## Future migration
Implement a storage interface so a PostgreSQL backend can replace JSON without changing the API or UI.

Status: design only; not yet deployed.
