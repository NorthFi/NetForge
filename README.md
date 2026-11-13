# root/README.md
# NetForge — ISP / WISP Operations Manager

NetForge is an independently implemented, Splynx-inspired ISP management interface with additional WISP engineering tools. It does not include or redistribute Splynx source code, trademarks, graphics, or private APIs.

## Architecture

A static HTML/CSS/JavaScript frontend connects to a PHP 8.1+ JSON-file API, so the initial installation works on Afrihost cPanel without SQL, Node.js, Docker, Redis, or a persistent daemon.

```
public_html/netforge/
  index.html
  .htaccess
  assets/
    netforge.css
    netforge.js
  api/
    index.php
# Private account directory (automatically created)
netforge-private/
  *.json
  *.lock
```

The private storage directory must be outside `public_html`. The API fails closed if the directory cannot be initialized safely. File records use exclusive locks and temporary-file rename for atomic writes.

## Capabilities

- Dashboard: customer totals, ticket queue, overdue work, site and stock alerts, and planned sector capacity.
- CRM: customer 360° profiles, business/family parent-child account links, leads, communication history, linked services, tickets, assigned follow-up tasks, monthly manually entered usage, and customer equipment.
- Finance: manual invoice/payment registers; idempotent **preview-and-confirm monthly draft invoice generation** for active, priced services; controlled issue/void transitions; debtor ageing and manual reminder drafts; package catalogue; MRR and ARPU estimates; CSV exports. No automated payment capture or invoice delivery.
- Networking: highsite workspaces (Overview / Distribution / Backhaul / Access / Power / Customers / Outages), access sectors, backhaul records, IP address register, incident tracking and infrastructure equipment.
- WISP intelligence: subscriber distribution, manual RF health observations, sector capacity thresholds, frequency reuse warnings, outage impact preview, GPS-based topology drawing, link-budget estimate, midpoint first Fresnel-zone calculator and midpoint Earth-curvature bulge.
- Field operations: branch-aware technician scheduling, drag-and-drop ticket/work-order assignments, ticket worklogs with minutes and helpdesk history, work orders, RF surveys, maintenance and on-call metadata.
- Inventory: stock levels, reorder thresholds, suppliers and deployed customer/site equipment; server-side stock adjustment with optimistic revisions, idempotency IDs, reason logs, and prevention of negative stock.
- Scripts: editable library of script text and approval/risk metadata, **no remote execution**.
- Administration: branch management, staff login accounts, technicians, package plans, sectors and settings.

## Monthly billing and support workflows

1. In **Administration → Packages**, create a package with a positive `price` (monthly ZAR).
2. Under **Finance → Services** (or a customer's **Services** tab), assign an **active** service to an **active** customer and set its package to the package name. Optionally enter `monthly_fee` to override package price for that service.
3. Give the customer a `billing_day` between 1 and 28 in the customer editor. Missing/invalid values default to day 1 of the selected billing month.
4. Open **Recurring billing**, select a month, and run **Preview**. Confirm eligible, already billed and skipped services. The same service is charged only once for a given month.
5. An **administrator** may generate draft invoices after explicitly confirming the preview. Repeated calls cannot silently create duplicates. Drafts are not payable or emailed.
6. Issue individual drafts when reviewed. Only issued invoices can receive allocated payments. An invoice with payments cannot be voided. Generated invoice snapshots cannot be directly edited or deleted.
7. Use **Debtors** for ageing and copyable reminder text. Sending reminders is entirely manual; no messaging integration is active.
8. Use **Reports** for estimated MRR, ARPU, support hours and usage summaries. These values are derived from manually stored source data, not live equipment telemetry.

**Linked customer accounts:** Assign a parent account from the customer editor or create child accounts from Customer 360°. Cycles are rejected and parents with linked children cannot be deleted. This is for support and navigation only; automatic combined invoicing/payment consolidation is **not** enabled.

**Billing restrictions:** Prices must be numeric, positive, and have at most two decimal places; there is no VAT calculation, proration, prepaid balance, credit-note/refund ledger, advance credit, payment gateway, or compliant tax invoice/PDF generation. Active services with missing prices are skipped and reported. Billing creation is a manual operator action, not a scheduled background process. Never use this for legal/tax invoices before implementing the jurisdiction-specific requirements.

**Ticketing:** Open a ticket for its customer, highsite, VoIP or other service, log technician minutes in its worklog, add calls and linked work orders, and track customer follow-up tasks. Time logging is manual; SLA targets are not automatically enforced.

**Stock:** Create a stock item, then use its **Adjust stock** action with a signed quantity change and mandatory reason. Ordinary inventory edits cannot modify existing stock quantities; the adjustment action writes an audit movement entry under the item's JSON record. Initial inventory quantities can be entered when creating a new item, so confirm opening stock during initial setup.

**Data usage:** Monthly upload/download GB records are manually entered; no RADIUS accounting import, data cap enforcement, throttling or automated collection occurs.

## Deployment (Afrihost cPanel)

1. Upload `index.html`, `.htaccess`, `assets/`, and `api/` into `public_html/netforge/` while retaining the paths above.
2. Use PHP **8.1 or newer**, with sessions and JSON enabled. Enable HTTPS for the hostname in cPanel.
3. Confirm PHP can create a writable `netforge-private` directory in the account home, outside `public_html`. Never relocate this directory into the web root.
4. Open `https://northfi.co.za/netforge/` and create the initial administrator using a unique password at least 14 characters long.
5. Configure branches, highsites and sectors first; then assign each customer to their highsite and a sector belonging to that site.
6. Store regular encrypted backups of `netforge-private`; test restoration, and restrict account directory access to the PHP process and authorized server administrators.

The development branch `netforge-isp-manager` must be tested on a staging path before publishing. The `Published` branch remains unaffected.

## Automated checks

GitHub Actions runs PHP syntax, JavaScript syntax/screen verification and authenticated API integration smoke tests covering administrator bootstrap; site/sector/customer assignment; equipment; communications; service records; recurring billing preview, draft issuance and duplicate prevention; payment safeguards; RF observations, surveys and work orders; helpdesk time logs, customer tasks and manual usage; inventory stock movements, stale edits, duplicate IPAM, deletion restrictions, and optimistic concurrency.

Workflow: `.github/workflows/netforge-ci.yml`.

## Security and operational limitations

This remains a staged MVP, not a claim of feature parity with Splynx and not an audited production billing or monitoring system.

- No live SNMP, RouterOS API, RADIUS, PPPoE provisioning, TR-069 ACS, WhatsApp messaging, payment gateway, automated payment reconciliation, tax invoice generation, remote script execution, or network alarms yet.
- RF calculations assume free-space propagation and configurable effective Earth radius. They do not model terrain, trees, buildings, multipath, or interference. All RF snapshots and site statuses are manually entered.
- The GPS topology connects configured highsites visually; it is **not** a live bandwidth or availability map.
- Session cookies are Secure/HttpOnly/SameSite. CSRF checks, initial RBAC and per-IP-and-user login throttling exist. Granular per-branch permissions, MFA and comprehensive penetration testing are still required.
- Device management passwords are deliberately excluded from file storage; browser access only opens HTTP(S) links. Winbox and SSH require a trusted workstation or future secure agent.
- Records have optimistic revisions, but inter-collection updates are not transactional. Branch and service references need more comprehensive integrity checks before larger deployments.
- Never store payment card details, API secrets, or passwords in free-text notes, scripts or equipment records.
- PHP is file-backed and intended for small deployments. Migrating later to PostgreSQL and a dedicated automation worker is strongly recommended.

## Licensing and independence

NetForge uses an original layout and implementation inspired by common ISP CRM conventions. Splynx is a separate product and trademark.
