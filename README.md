# root/README.md
# NetForge — ISP Management Platform

Initial functional release for Afrihost cPanel without SQL. Static browser UI + PHP API + private JSON collections.

## Deploy
1. Upload `index.html`, `.htaccess`, and `api/index.php` to `public_html/netforge/`.
2. Confirm HTTPS and PHP 8.0+ with JSON and sessions enabled.
3. PHP must be permitted to create `netforge-private` in the cPanel account home directory, outside `public_html`. If not, ask hosting support for writable private storage. The application fails closed if private storage cannot be created.
4. Open `https://northfi.co.za/netforge/` and create the first administrator account with a strong unique password.
5. Verify `https://northfi.co.za/netforge/api/index.php?action=status` responds with JSON.
6. Export records regularly and back up the private directory securely.

## Included
- Admin setup, login, logout, secure session cookie and CSRF checks.
- Dashboard with record counts.
- Customers, highsites, sectors, equipment, backhauls, script metadata, and job listings.
- Create/list/delete records; highsite and sector associations; CSV export.
- Private JSON storage with exclusive locks and atomic file replacement.
- Audit records for creates/deletes.

## Limitations and safety
- This is an **initial MVP**, not a production-hardened multi-user ISP management system.
- Single administrator only; no RBAC, login rate limiting, device monitoring, billing, scheduled jobs, script execution, updates, or referential-integrity enforcement yet.
- Deleting a parent record can leave orphaned references; reconcile these before use.
- Script records are metadata only. The web application never executes device commands.
- Do not use on a public site with sensitive real customer information until security review, rate limiting, backups, and access policies are implemented.
- JSON storage is appropriate for small deployments; migrate to PostgreSQL on a self-hosted server as usage grows.

## Repository
The `netforge-isp-manager` branch is the new application. The `Published` branch remains untouched.
