# root/README.md
# NetForge — .NET 11 self-hosted ISP management

**Development branch:** `netforge-selfhost-dotnet`

NetForge is a self-hosted, independently developed ISP/WISP platform built with C# / ASP.NET Core (.NET 11), PostgreSQL 17 and FreeRADIUS 3.2. The new stack replaces PHP/JSON-file hosting. The earlier cPanel application is preserved separately on branch `netforge-isp-manager` and the live `Published` branch is unchanged.

This branch is an **engineering MVP**, not a claim of feature parity with Splynx. Core CRM data is normalized in PostgreSQL and RADIUS authentication/accounting is handled by an established AAA server.

## Repository layout

```
selfhost/
  src/NetForge.Api/
    NetForge.Api.csproj
    Program.cs
    Core.cs
    Endpoints.cs
    NetworkManagement.cs
    RadiusDynamicAuth.cs
    ServiceLifecycle.cs
    CustomerPortal.cs
    UsageAccounting.cs
    FinancialAccounting.cs
    BillingScheduler.cs
    Notifications.cs
    NetworkMonitoring.cs
    SiteImpact.cs
    SnmpTelemetry.cs
  db/
    001_init.sql
    002_radius_management.sql
    003_subscriber_portal_accounting.sql
    004_financial_ledger_reconciliation.sql
    005_operations_automation.sql
    006_wisp_telemetry.sql
  radius/
    Dockerfile
    entrypoint.sh
  web/
    index.html
    style.css
    app.js
    telemetry.js
    portal/
      index.html
      style.css
      app.js
  Dockerfile
  Caddyfile
  compose.yaml
  .env.example
```

## Host requirements

- **Linux (recommended):** Ubuntu Server 24.04+, Debian stable, or a recent Linux with Docker Engine and Compose v2.
- **Windows:** Windows 11 / Server with Docker Desktop or a supported WSL2/Linux Docker engine. FreeRADIUS runs in a **Linux container**; it is not a native Windows service.
- **Native C# API:** .NET SDK/runtime 11 and a reachable PostgreSQL 17+ instance. The same API builds for Windows and Linux with `dotnet publish`.
- An explicit TLS reverse proxy for production (Caddy, Nginx or equivalent) and a private management LAN/VPN.
- A dedicated private host address reachable by your RADIUS NAS routers over UDP 1812 (authentication) and UDP 1813 (accounting).

## Quick start (Docker Compose)

1. Check out `netforge-selfhost-dotnet`.
2. Run `cd selfhost` and copy `.env.example` to `.env`.
3. Replace `POSTGRES_PASSWORD` and `NETFORGE_ADMIN_PASSWORD` with **distinct**, strong random strings; the database password must contain only letters, numbers, `_`, `.`, or `-` because FreeRADIUS config interpolates it at startup. Placeholder strings beginning `REPLACE_` are rejected by the application.
4. Set `RADIUS_BIND_IP` to the machine's **private** RADIUS listening address, and configure your firewall to allow UDP 1812/1813 only from approved NAS IP addresses. If you cannot bind a private address, restrict public UDP 1812/1813 at your router and host firewall.
5. Run `docker compose --profile https up -d --build` to enable the included private HTTPS reverse proxy. Alternatively, use `docker compose up -d --build` if you already have a trusted external HTTPS reverse proxy.
6. For the included proxy, browse to `https://netforge.localhost:8443/` on the Docker host. Caddy uses an internal CA: you must trust its root certificate on authorized workstations. With an existing reverse proxy, forward authenticated HTTPS traffic to `127.0.0.1:8080`.
7. Create a branch, highsite, sector, customer, package, and pending subscriber service. Use **Set/rotate RADIUS password** to provision the service's FreeRADIUS `radcheck` entry.
8. Under **RADIUS NAS**, add each router with its IPv4 address and long random shared secret. Then run `docker compose restart radius`. SQL NAS records are loaded **at FreeRADIUS startup**, not dynamically.
9. Configure your MikroTik or other NAS to send auth/accounting packets to the host. Verify FreeRADIUS logs and NetForge's RADIUS Sessions screen.
10. Schedule PostgreSQL backups and test restores before loading production data.

**Caddy internal TLS:** Run `docker compose exec proxy cat /data/caddy/pki/authorities/local/root.crt`, then save and import that certificate into the approved workstation's trusted root store after verifying its origin. For other workstations, set `NETFORGE_DOMAIN` to a DNS name that resolves to the host and set `NETFORGE_HTTPS_BIND_IP` to its private LAN/VPN address. Never distribute the CA private key. For a public hostname with a trusted certificate, configure your own HTTPS reverse proxy or Caddy ACME separately.

**Important:** The API requires HTTPS for its Secure session cookie. Direct HTTP access on the loopback port is for a proxy connection or health-check only. A bare HTTP browser login may not persist.

## Phase 2: MikroTik / RADIUS lifecycle

Detailed configuration and operational safeguards are in [selfhost/NETWORK_OPERATIONS.md](selfhost/NETWORK_OPERATIONS.md).

The current self-hosted build includes:
- Individual NAS disconnect settings (default **disabled**, MikroTik default UDP **1700** per current RouterOS manual; configurable for NAS running UDP 3799).
- Administrator-confirmed Disconnect-Request (RFC 5176 code 40) directed **only** to a matching session in `radacct` and a registered NAS IP. RADIUS ACK/NAK packet source, identifier and MD5 Request/Response Authenticators are checked; timeout or invalid response is recorded as an unknown/error state.
- Per-subscriber speed profile changes with deterministic `Mikrotik-Rate-Limit` values. The rate field is **router RX/TX**: subscriber upload/downlink expressed as `uploadM/downloadM` by default, unless an explicitly validated override is set.
- Auth-Type Reject suspension and explicit reactivation, retaining PPPoE credentials. Password rotation while suspended does **not** reactivate a service.
- Accounting usage grouped by session-start month, with RouterOS **Input Octets as subscriber upload** and **Output Octets as subscriber download**. Cross-month sessions are not split. Do not use these approximations for metered billing.
- Optional overdue invoice enforcement. `billing_controls.enabled` defaults to `false`; an administrator must explicitly enable suspension, select the grace period, and optionally enable restoration after full payment. A hosted worker checks every 10 minutes, with PostgreSQL advisory locking to avoid concurrent scheduled runs. **The policy does not send automatic Disconnect-Requests.**

When a session disconnect request receives ACK, the NAS has acknowledged the request. Only a later accounting Stop or independent NAS observation confirms that the subscriber actually disconnected. A timeout is ambiguous, not proof of failure.

## Phase 3: Customer self-service and observed usage

- Customer portal: `https://<your-netforge-host>/portal/` (separate `NetForge.Portal` cookie and per-customer accounts; **staff login does not work there**).
- In the staff CRM, select **Customer Portal**, choose a customer, provision a username and a unique password (minimum 14 characters), and securely hand the credentials to that subscriber.
- Customers can view their own contact details, services, **issued/paid invoices only**, recorded payments, per-month usage estimates and their own support tickets. Staff create public responses or private internal notes from each ticket's **Ticket conversation** action.
- Portal account disable/reset revokes earlier sessions by incrementing an account version. Customers can also change their own password; their other sessions are invalidated.
- New RADIUS accounting changes are captured by PostgreSQL trigger `trg_netforge_usage` into `radius_usage_intervals`. The **RADIUS Usage** staff screen and portal Usage tab now display UTC month estimates apportioned by the measured interval duration, and report sample quality/anomalies.
- Historic sessions from before migration 003 do **not** have interim snapshots and are not backfilled as measured data.

Complete operational details, security assumptions and validation constraints: [Customer portal and usage](selfhost/CUSTOMER_PORTAL_ACCOUNTING.md).

## Phase 4: Financial posting and bank reconciliation

This phase introduces transactional, ZAR-denominated double-entry postings and human-approved bank-statement reconciliation. See [Financial operations and safeguards](selfhost/FINANCIAL_OPERATIONS.md).

- Invoice issuance creates **Dr Accounts Receivable / Cr Revenue** with an immutable journal.
- Payments create **Dr Bank Clearing / Cr Accounts Receivable**, and reversals post a new, opposite journal rather than editing or deleting the original receipt.
- Credit notes create **Dr Revenue Returns / Cr Accounts Receivable** and reduce the invoice outstanding balance. Credit notes cannot currently be reversed through the application; review them carefully before posting.
- Invoices now distinguish `paid` (cash settlement) from `settled` (settlement involving an applied credit). Outstanding balances use payments **excluding reversals**, plus posted credits.
- The **Bank Reconciliation** section supports a fixed, bank-neutral CSV: `Date,Amount,Description,Reference,TransactionId`. Upload for preview first, then explicitly import. The imported rows remain unmatched until an administrator allocates an incoming transaction to an issued invoice.
- Each bank transaction is deduplicated using a bank label and its stable transaction ID. One incoming bank line may be allocated across several invoices but never beyond its available balance. Outgoing debits are imported for reference and cannot be allocated as customer payments.
- Manual payment posting accepts an optional unique request ID; the UI supplies it, and repeating the same request does not create another payment. Manual postings also guard against duplicate references and amounts on the same invoice.
- The **Financial Ledger** section displays the trial balance and source-tagged journal lines. Historical invoices/payments are imported once as clearly marked opening postings in migration `004`.
- A posted credit or reversed payment affects the overdue-invoice policy through the same database `invoice_balances` view. **Automatic suspension remains opt-in and disabled by default.**

**Scope limit:** This is an auditable foundation, **not a certified accounting or taxation system**. There is no VAT determination, valid South African tax-invoice PDF, bank API feed, gateway, refunds/chargebacks, multi-currency support, formal bank settlement or automatic matching. Validate controls with an accountant and test full backup/restore before using customer financial data.

## Phase 5: Monthly billing, messaging and network checks

The new **Invoice Automation**, **Notifications**, and **Network Monitoring**
screens are administrator-controlled. Everything that can create invoices,
send messages or probe live devices is **disabled by default**.
See [Operations automation and runbook](selfhost/OPERATIONS_AUTOMATION.md)
for deployment, provider configuration, routing/firewall requirements and
risk controls.

- UTC monthly invoice generation is idempotent per customer/month and
  freezes active subscription prices in `invoice_items`.
- Issuance is independently opt-in; it posts balanced journals and may
  enqueue customer emails only with global enablement plus per-customer
  consent. Due dates are not backdated into immediate delinquency when
  a period is generated late.
- A durable SMTP STARTTLS or Telegram notification outbox supports
  retry/backoff, status visibility and sanitized errors. Delivery is
  **at least once**, so external duplicates remain possible.
- A conservative TCP service monitor probes only inventoried NAS or
  equipment IPv4 addresses, tracks sustained failures and recovery,
  and optionally queues operations notifications.
- No SNMPv3, ICMP, RF quality statistics, VAT/proration, PDF invoice
  delivery, or automatic bank payment matching is included yet.

## Phase 6: WISP telemetry and upstream impact

Two new staff views, **SNMP Telemetry** and **Highsite Impact**, extend the
existing TCP Network Monitoring module. See
[WISP telemetry and highsite correlation runbook](selfhost/WISP_TELEMETRY.md).

- Inventoried SNMPv3 authPriv devices only, SHA-256/SHA with AES128,
  encrypted USM credentials using the persisted ASP.NET Core key ring;
  Docker installs a real Net-SNMP client.
- Read-only IF-MIB 64-bit interface counters, timestamped samples,
  calculated bps and detection of device reboots/counter resets.
- Historical interface bitrate charts and interface-status event history.
- Explicit highsite/upstream tags for TCP monitors and potential
  highsite-impact alerts with snapshot customer/sector counts.
- Child TCP/SNMP notification suppression during tagged upstream outages,
  while preserving full individual event history.
- **New SNMP polling is disabled by default.** This is not ICMP,
  SNMPv2c, traffic flow analysis, a full RF graph or an OLT/ACS controller.

## Current capabilities

- Staff login with PBKDF2 password hashes, cookie authentication, role checks and request CSRF tokens.
- Admin creation of staff, branches, service packages and NAS devices.
- Customers, highsites, access sectors and customer/site equipment records.
- Foreign-key validation for customer → site → sector relationships.
- PPPoE service creation, password rotation, RADIUS check/reply provisioning, bandwidth replies and optional static IPv4.
- A separate FreeRADIUS 3.2 container using SQL tables `radcheck`, `radreply`, `nas`, `radacct`, and `radpostauth`.
- Accounting history with active/unclosed session visibility, recorded usage intervals, UTC month estimates, and counter-reset anomaly detection.
- Tickets with customer/site associations, type, priority and status, including public customer replies and internal-only support notes.
- Invoices and manual monthly drafts, balanced financial postings, bank statement import/preview, manual reconciliation, credit notes and auditable payment reversals.
- Auditing for key administrative changes.

## Critical RADIUS and billing semantics

- The service **must be created pending** and then activated with a password. No RADIUS password is returned by API reads.
- FreeRADIUS uses the `Cleartext-Password` check item for PAP/CHAP/MSCHAP compatibility. This is **sensitive plaintext in PostgreSQL**. Encrypt database volumes/backups, restrict database access, use network segmentation, and plan a dedicated RADIUS database account with least-privilege access.
- Suspending a customer or service adds a FreeRADIUS `Auth-Type := Reject` check item and retains the PPPoE password for later reactivation. It prevents **new authentication** but does **not** disconnect an existing PPPoE session. An administrator can disconnect an individual live session via an explicitly enabled NAS.
- The session list shows unclosed accounting records, which may include stale sessions. The `recently_seen` indicator is based on the last 30 minutes.
- MikroTik `Mikrotik-Rate-Limit` values default to subscriber upload/download (RouterOS receive/transmit); verify directionality with your service profiles and RouterOS version before onboarding.
- Recurring invoice draft creation is **manual** and idempotent per customer and billing month. Ledger postings, payments, credits and reversals are implemented, but tax calculations, proration, payment gateways, SMS/email delivery, refunds and legally compliant invoice PDFs are not.
- Do not expose the PostgreSQL port or the application management port directly to the public Internet.
- Do not place RADIUS device shared secrets in tickets or notes. The NAS secret is displayed only at creation time and is stored in PostgreSQL for FreeRADIUS to use.

## Native Windows or Linux API (without Docker)

```powershell
dotnet restore selfhost/src/NetForge.Api/NetForge.Api.csproj
dotnet publish selfhost/src/NetForge.Api/NetForge.Api.csproj -c Release
```

Set `Database__ConnectionString`, `NETFORGE_ADMIN_USER`, `NETFORGE_ADMIN_PASSWORD`, and `NETFORGE_KEY_DIRECTORY` in the process environment, then run the published `NetForge.Api.dll` or native executable. The app applies numbered PostgreSQL migrations transactionally with checksums and an advisory lock. Existing migration files must never be edited after being applied to a live database. You still need to provide an independently installed FreeRADIUS PostgreSQL integration and a production TLS reverse proxy. Native Windows API hosting does **not** remove the requirement for a separate FreeRADIUS service.

## Current gaps versus full Splynx functionality

Do **not** assume this is complete Splynx functionality. Missing major components include multi-factor authentication, payment gateways and automatic bank feeds, VAT-compliant invoicing and proration, refunds and prepaid vouchers, usage-based rating, true live-session CoA speed changes, NAS configuration automation, TR-069/USP ACS, ICMP, vendor-specific SNMP CPU/memory/RF telemetry, telemetry retention/rollups, notification templates and confirmed external delivery, scoped API tokens, backup/restore UI, VoIP CDR billing, technician dispatch, procurement, contracts, granular RBAC, multi-tenant isolation, and high availability.

This branch **intentionally excludes the RF link planner**. It retains basic WISP highsite/sector/subscriber relationships and equipment records.

## Status

Automated CI validates .NET 11 compilation, PostgreSQL API workflows, Docker images, and real FreeRADIUS PAP accept/reject tests including suspension. This does not replace RouterOS NAS acceptance tests, disaster recovery verification or a security assessment. Require TLS, backups, NAS isolation, auditing, security tests and vendor-specific RADIUS acceptance tests prior to a live cutover.
