# root/README.md
# NetForge — ISP Operations Manager

NetForge is a cPanel-compatible, file-backed ISP management application.

## Current modules
- Customers: account details, names, addresses, packages, branches, sector associations, CPE management details and location links.
- Tickets: customer, highsite, VoIP and other service tickets; priority, status, notes, call logs, assignment and scheduling.
- Planner: monthly day-based calendar, drag-and-drop tickets, branch filters and technician assignments.
- Highsites: site numbers, locations, branch assignment, equipment grouped into distribution, backhaul, access and power.
- Scripts: script library (text storage only; no execution).
- Settings: branches, technicians, login staff, packages and sectors.

## Deploy to Afrihost cPanel
1. Copy `index.html`, `.htaccess`, and `api/index.php` into `public_html/netforge/`.
2. Ensure HTTPS and PHP 8.0+ with sessions and JSON.
3. The PHP API must be able to create `netforge-private` outside `public_html`; if it cannot, hosting permissions must be fixed. It deliberately fails closed instead of storing sensitive data publicly.
4. Browse to `https://northfi.co.za/netforge/` and create the initial administrator.
5. Back up `netforge-private` regularly and test restoration.

## Security and feature limitations
This release is an **MVP and must not be considered production-hardened**.
- No rate limiting or lockout on login; restrict exposure before using real customer data.
- Staff roles: admin, dispatcher, technician, viewer. Only admin can manage staff and delete records; viewer cannot edit. Fine-grained authorization is not yet implemented.
- Equipment access: browser URLs open for HTTP(S). SSH and Winbox show connection instructions only. Browser launching of native tools is not reliable.
- **Device passwords are deliberately not collected or stored.** A credential vault and authenticated agent are required first.
- Scripts are stored as text; there is no execution engine, device connectivity or remote job scheduling yet.
- There is no referential integrity enforcement; deleting a highsite, sector or customer may leave dangling references.
- Staff passwords cannot be reset in the UI yet.
- Existing initial administrator remains in a separate auth JSON file; additional staff are stored in staff.json.
- JSON collections are individually locked and atomically replaced, but multi-collection updates are not transactional.
- No built-in backups, import pipeline, or automated monitoring.
- CSV export from the earlier version is not included in the redesigned interface yet.

## Branches
`netforge-isp-manager` is the development branch. `Published` is unchanged.

## Future architecture
When moving to self-hosted infrastructure, introduce PostgreSQL and a dedicated Python automation worker without changing the frontend's functional modules.
