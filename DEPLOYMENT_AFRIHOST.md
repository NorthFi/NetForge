# root/DEPLOYMENT_AFRIHOST.md
# NetForge deployment: northfi.co.za/netforge

## Hosting target
Public URL: https://northfi.co.za/netforge/
Hosting provider: Afrihost.
Hosting capabilities (PHP version, database, cron, SSH, rewrite rules, TLS) must be verified before choosing the production backend.

## Architecture
- Static React/TypeScript frontend compiled with base path /netforge/.
- Shared-hosting-compatible PHP API under /netforge/api/ if PHP and MySQL/MariaDB are available.
- Server-side sessions with secure, HttpOnly, SameSite cookies and CSRF protection.
- Dedicated Python network automation agent hosted on a trusted machine with connectivity to ISP equipment.
- Agent uses outbound authenticated TLS to obtain approved jobs and report results; do not expose RouterOS, SSH, or SNMP ports publicly.
- Strict RBAC, target scoping, audit trails, secrets handling, rate limits and request validation.

## Deployment constraints
- Do not assume Docker, persistent workers, Python runtimes, or root access on shared hosting.
- Configure frontend routing to preserve /netforge/ and avoid interference with the parent site.
- Never store secrets in frontend bundles or the Git repository.
- Avoid hosting job payloads or sensitive logs in publicly served directories.
- Establish encrypted backup and restore procedures for the database.
- Validate cron availability and outbound connectivity before relying on scheduled features.

## Status
Deployment plan only. No changes to the live Afrihost website have been made.
