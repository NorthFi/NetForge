# root/SPLYNX_PARITY.md
# Splynx functionality benchmark — NetForge status

NetForge is an original ISP/WISP application. It does not contain Splynx proprietary implementation, source, artwork or trademarks. This is a feature parity tracker, not a claim of complete equivalence.

Reference: publicly described capabilities on https://splynx.com/ , https://splynx.com/customer-management/ and https://splynx.com/network-management/ (reviewed November 2026).

| Domain | NetForge today | Gaps to close |
| --- | --- | --- |
| Subscriber CRM and account relationships | Functional baseline | Custom fields, change histories, GDPR/POPIA workflow, batch onboarding |
| Customer self-service portal | Functional baseline | MFA, credential recovery, contract downloads, service plan changes, payment gateway |
| Tickets and communication | Functional baseline | Multiple inboxes, mail ingestion, notification delivery, SLA escalation and canned-message analytics |
| Field scheduling and work orders | Functional baseline | Job templates, geolocation, technician mobile app and signatures |
| Recurring billing | Manual draft-generation engine | Taxes, proration, recurring scheduler, credits, reversals, true accounting ledger |
| Payments and debtors | Manual ledger and reminders | Bank statement reconciliation, debit orders, gateways, refunds and prepaid balances |
| Prepaid billing and top-ups | Not implemented | Vouchers, cutoff rules, customer balance and recharge controls |
| VoIP/CDR rating and invoices | Not implemented | Carrier CDR import, rate plans, billed minutes and voice statistics |
| Internet service and package inventory | Functional baseline | Rate changes with effective dates, authorization and downstream provisioning |
| RADIUS AAA / session accounting | Not implemented | Dedicated RADIUS service, NAS authorization, Accounting-Start/Interim/Stop, CoA |
| MikroTik/PPP/RADIUS provisioning | Not implemented | Secure self-hosted network agent, credential vault, rollback and idempotent jobs |
| ACS TR-069 / CPE management | Not implemented | Separate ACS infrastructure, device enrollment, profiles and execution policy |
| SNMP/PING monitoring and telemetry | Not implemented | Read-only polling agent, normalized metrics, alert engine and network topology |
| Network backups and configuration | Not implemented | Dedicated agent, encrypted storage, approved restore workflow |
| IP address management | Manual register | CIDR containment validation, subnet allocation, IPv6 delegation and overlap prevention |
| Highsite, sector and RF link planning | Functional WISP baseline | Terrain elevation integration, LOS verification, interference measurements and azimuth optimization |
| Warehouse inventory | Functional stock ledger | Reservations, transfers, suppliers, purchase orders, serial-level stock lifecycle |
| Sales funnel and lead management | Functional baseline | Quotations, contracts, signup widget, referral flows and lead conversion automations |
| Communications and knowledge base | Published announcements and articles | SMTP/SMS/WhatsApp gateways, schedules, templates and delivery logs |
| API and webhooks | Session-authenticated internal JSON API | Public versioned API, scoped tokens, webhooks, rate limits and third-party integrations |
| Reporting | Manual-derived summaries | Scheduled reports, materialized rollups, audited financial dashboards |
| Staff security | Role checks and CSRF | Granular permission matrix, per-branch restrictions, MFA, audited impersonation |
| High-availability and scaling | File-backed cPanel MVP | PostgreSQL, migrations, queues, archival, background tasks and tested restores |
| Mobile apps | Mobile-responsive web | Native or PWA offline technician app, field photography and notifications |

## Architecture required for full parity

1. **Current cPanel phase:** keep the static frontend, PHP APIs, small JSON collections, customer portal and safe manual business workflows.
2. **Dedicated management agent:** install an outbound-only agent on a trusted network host; expose no device-management ports on the Internet. Restrict job types, require approvals and log all device actions. Separate read-only monitoring from configuration changes.
3. **AAA and ACS integration:** deploy FreeRADIUS (or equivalent) and an ACS service on appropriate self-hosted infrastructure, with dedicated secrets, authentication policy and test NAS devices.
4. **Transaction-grade backend:** migrate billing and accounting to PostgreSQL with constraints, ledgers and immutable adjustments. Add a job queue for notifications, monitoring and recurring billing.
5. **Security and deployment:** enforce TLS, MFA, RBAC, scoped API keys, backups, data retention, POPIA controls, audit trails and disaster-recovery exercises before real customer credentials/payment information are used.

## Not supported on basic Afrihost cPanel alone

Network-wide SNMP scanning, continuous monitoring, RADIUS sessions, CoA, TR-069 ACS, bulk device provisioning, reliable scheduled network commands, transactional billing, and large-scale message delivery. These require services outside the shared host.

## Product position

NetForge's differentiator is WISP operations: site-to-sector-to-subscriber lineage, RF diagnostics, capacity and airtime planning, outage blast-radius estimation, technician dispatch, secure device automation, and engineering material lists. These should exceed general ISP CRM depth without misrepresenting the application's readiness.
