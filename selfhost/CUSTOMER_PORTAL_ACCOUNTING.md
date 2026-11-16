# root/selfhost/CUSTOMER_PORTAL_ACCOUNTING.md
# NetForge — subscriber portal and RADIUS accounting (Phase 3)

## Architecture

**Backend:** .NET 11 ASP.NET Core Minimal APIs, PostgreSQL 17, FreeRADIUS 3.x.

**Portal:** static frontend under `/portal/`, independent cookie authentication scheme `NetForge.Portal`, cookie path `/api/portal`, strict SameSite, Secure and HttpOnly. The staff login uses `NetForge.Auth` and cannot authenticate a customer API route. Portal endpoints select the customer ID only from the authenticated cookie claim—not from query strings or JSON input.

**Passwords:** account secrets are PBKDF2-SHA256 hashes with unique salts and 310,000 iterations. Passwords are never returned in staff API lists. Staff can create accounts, revoke sessions, reset passwords, and disable/enable access. A password reset, enable/disable operation or subscriber password change increments the per-account password version. The portal cookie validates the version and active status against PostgreSQL each request.

**CSRF:** authenticated portal state changes require a random CSRF token in `X-CSRF-Token`. Ticket creation, ticket messages, password changes and logout are protected. The independent portal cookie does not reuse the staff CSRF token. Login is rate-limited per source IP.

**Exposed data:** account contact information, service status, package/speed, issued or paid invoices, recorded payments, submitted tickets, public correspondence and usage summaries. Staff internal notes, management addresses, device secrets, NAS secrets, PPPoE passwords, payment details and other customer records are not exposed.

## Administrator setup

1. Deploy the branch via Docker with an HTTPS reverse proxy and a trusted TLS certificate, following `README.md`. Do not allow customer access over untrusted HTTP.
2. Sign in as an administrator at the main NetForge application and create or locate a customer.
3. Open **Customer Portal** in the staff navigation. Enroll the customer with a dedicated username and a strong, unique password (14–1024 characters).
4. Share credentials through a separately verified private channel. The application deliberately does not email credentials.
5. Have the subscriber open `https://your-host/portal/`, sign in, and verify visibility of their own subscriptions, invoices and support tickets.
6. Open a ticket in the staff console. The **Ticket conversation** action allows public customer replies and internal-only staff notes.
7. When disabling a customer's portal login or resetting a password, all previously issued sessions are invalidated immediately on their next API request.

**Not implemented:** MFA, account recovery via SMS/email, consumer payment gateways, payment card storage, PDF invoices and contract signing. These need further hardening and billing requirements.

## RADIUS accounting design

FreeRADIUS writes cumulative `acctinputoctets` and `acctoutputoctets` to the existing PostgreSQL `radacct` rows at Start/Interim/Stop. PostgreSQL migration `003_subscriber_portal_accounting.sql` adds trigger `trg_netforge_usage`, which records *increments* between old and new samples in `radius_usage_intervals`.

- Exact unchanged counters do not create an additional usage interval.
- Negative counters, decreasing counters, missing timestamps and non-increasing event timestamps are recorded in `radius_usage_anomalies`, not subtracted from usage.
- For a 20-minute interval spanning a midnight boundary, measured bytes are apportioned across the two UTC calendar days by elapsed seconds, then aggregated per month. This is a deterministic estimate assuming uniform traffic across the interval.
- `acctinputoctets` = traffic entering the NAS from the customer (subscriber upload); `acctoutputoctets` = subscriber download.
- Repeated reports query the persisted immutable interval rows and do not double-count repeated SQL reads.
- The endpoint `GET /api/radius/usage-reconciled?period=YYYY-MM` provides staff estimates. `GET /api/portal/usage?period=YYYY-MM` is customer-scoped.
- The `observed_intervals` and anomaly counts help staff identify confidence/quality issues.

### Data limitations

1. **No historical backfill.** Older `radacct` session counter totals cannot recreate historical interims; migration does not invent prior samples.
2. **Uniform distribution assumption.** Session byte volumes within an accounting interval cannot be placed at exact times without actual traffic samples. The month-boundary split is a time-weighted approximation.
3. **Late accounting records.** Missing or delayed Start/Interim/Stop updates, NAS counter resets and missing gigawords can cause incomplete usage. They are recorded or flagged where observable.
4. **Counter anomalies.** Negative or decreasing totals are excluded and must be investigated. This system does not silently charge anomaly bytes.
5. **No payment or cap enforcement.** These reports are operational estimates, *not* a rated/settled usage ledger. Do not bill metered traffic from them without a separate reconciliation and adjudication workflow.

To improve confidence, configure RouterOS PPP RADIUS accounting and interim updates, ensure reliable UDP 1813 routing, and monitor FreeRADIUS accounting logs alongside the `radacct` timestamps.

## Database migrations and backups

Migrations `001`, `002` and `003` are applied in lexical order inside a PostgreSQL transaction, protected by `pg_advisory_xact_lock`. Each successful script is recorded with a SHA-256 checksum in `netforge_schema_migrations`.

- Already applied scripts are not rerun on application restarts, preventing repeated trigger recreation and unnecessary table locks.
- Editing an applied migration is a startup error. Add a new numbered migration rather than modifying existing migration files.
- Before deployment, take a consistent PostgreSQL backup and test restore.
- Treat RADIUS database contents and backups as secrets because PPPoE verification items and NAS shared secrets may be present in existing tables.
- Database and API failures must not be treated as successful payment processing or subscriber disconnection.

## Automated integration coverage

The GitHub Actions `.NET 11 self-host validation` workflow exercises staff/portal cookie separation, private note filtering, CSRF checks, account revocation, ticket creation, monthly RADIUS byte distribution, duplicate interim updates and anomaly exclusion; existing tests also exercise real FreeRADIUS PAP accept/reject and HTTPS proxy serving.

Production sign-off remains separate: test against a physical MikroTik NAS, verify PPPoE Start/Interim/Stop updates, security controls, backup recovery, firewall and data-retention policies before migrating real subscribers.
