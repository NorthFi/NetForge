# root/selfhost/OPERATIONS_AUTOMATION.md
# NetForge — recurring invoicing, notification gateways, and TCP checks (Phase 5)

This document applies to the `netforge-selfhost-dotnet` branch. These are
new components in the independent C#/.NET 11, PostgreSQL and FreeRADIUS stack,
not a replacement for formal accounting, tax, ISP network management or
an external message-delivery platform.

## 1. Scheduled monthly billing

The **Invoice Automation** screen configures `billing_schedule`:

| Configuration | Default | Behavior |
| --- | --- | --- |
| Enabled | **false** | No worker-created invoice |
| Auto Issue | **false** | If enabled, newly created drafts are immediately issued with an immutable balanced journal |
| UTC run day | 1 | UTC calendar day at/after which the worker may generate the current month |
| Due days | 7 | Days from the later of the billing period start or actual generation date |

The worker checks once at startup and once per hour. It processes **only the
current UTC month** in automatic mode. A manual administrator run can target a
specified period, but the automation must first be enabled. Each customer/month
has a unique database constraint. Rerunning a period does not duplicate
invoices or reissue old drafts.

Only active customers' active services with positive package monthly prices
are included. A new invoice's item rows capture each service ID, package
description, quantity and price at the point of creation, and its total matches
that captured input. Previously existing customer/month invoices are never
overwritten or reissued by the scheduler.

**Important limitations:** Pricing is not prorated for partial months;
package price is assumed to be an agreed monthly ZAR charge; VAT calculation,
discounts, credit carry-forward, currency conversion, contract terms and
legally compliant tax-invoice PDFs are **not** implemented. Draft creation
does not send emails or post revenue. Issuance posts an immutable receivable
and revenue journal, and queues opt-in invoice emails, if enabled.

Do not turn on automatic issuance for real subscribers until invoice numbering,
amounts, due date and customer obligations have been accepted by finance.
Turning automation on after its run day will generate that month's invoices on
the worker's next run. Overdue-billing suspension is an **independent** opt-in
policy and remains disabled by default.

## 2. Email and Telegram

Credentials exist **only in the API container environment**, not in database
settings, API responses, UI JavaScript, audit logs or Docker image source.

Copy `selfhost/.env.example` to `selfhost/.env` and configure:

```ini
NETFORGE_SMTP_HOST=smtp.example.net
NETFORGE_SMTP_PORT=587
NETFORGE_SMTP_FROM=billing@example.net
NETFORGE_SMTP_USER=your-smtp-user
NETFORGE_SMTP_PASSWORD=your-provider-app-password
NETFORGE_OPERATIONS_EMAIL=noc@example.net

NETFORGE_TELEGRAM_BOT_TOKEN=your-telegram-bot-token
NETFORGE_OPERATIONS_TELEGRAM_CHAT_ID=your-ops-chat-id
```

These are **configuration field descriptions**, not working credentials;
obtain values from your approved provider. Never commit `.env` to Git.
Recreate the API container with the new environment after changing values.

NetForge uses STARTTLS for SMTP transport (typical **TCP 587**); it
does not support implicit TLS on 465. The Telegram bot uses HTTPS to
`api.telegram.org`. Egress DNS and firewall policy must allow the
configured SMTP destination and Telegram HTTPS if enabled.

In **Notifications**, enable the exact channels you want:

- Customer invoice email: global option plus explicit per-customer
  `email_invoice_opt_in`; only when both are enabled will an issued
  invoice queue an email. Staff must obtain and document real customer consent
  before enabling the account preference.
- Operations email: sends TCP monitor DOWN and recovery notifications to
  `NETFORGE_OPERATIONS_EMAIL`.
- Operations Telegram: sends TCP monitor DOWN and recovery notifications to
  `NETFORGE_OPERATIONS_TELEGRAM_CHAT_ID`.

All three switches default to **false**. If a provider isn't correctly
configured, the API rejects enabling its channel. Staff may inspect
recipient, subject, state, attempts and sanitized errors; **API clients do
not receive SMTP passwords or bot tokens**.

### Delivery guarantees

The PostgreSQL `notification_outbox` is transactionally populated during
invoice issuance and monitor state transitions. A worker leases one job at a
time, delivers outside the transaction, and marks completion. Failed
transmissions are retried with backoff and eventually enter `dead` state,
which an administrator can manually retry after checking provider status.

This design is **at least once**, not exactly once. A crash after a provider
accepts a message but before SQL marks it sent can produce a duplicate email
or Telegram message. The generated deduplication keys prevent duplicate
outbox rows for the same invoice or monitor event; they cannot guarantee that
external delivery occurs once. Avoid messages that trigger payments or
account changes automatically based only on receipt.

## 3. Network monitoring

**Network Monitoring** checks whether a configured **TCP service port**
on an already inventoried NAS or equipment IPv4 address accepts a
connection from the API container.

Creation requires a saved NAS or equipment record, operator-chosen
TCP port 1–65535, interval 60–3600 seconds, and a failure threshold of 2–10.
Targets default to disabled. Enabling requires explicit confirmation.
It is not possible to submit an arbitrary hostname or URL as a probe
destination through the monitor API.

The worker checks eligible enabled targets every 30 seconds, respecting each
target's configured interval. Individual checks have a two-second connection
timeout. The first successful connection moves the target to UP; the target
moves to DOWN only after the configured number of consecutive TCP failures.
When a previously DOWN service accepts TCP, it becomes UP and produces a
recovery event. Operations alerts are queued on **state transitions**, not
on every failing probe.

**Limitations:** A TCP accept does not prove a site has connectivity for its
subscribers, that PPPoE is working, or that RF SNR, noise and backhaul
throughput are healthy. No ICMP, SNMPv3, NetFlow, telemetry agents, topology
auto-discovery, geospatial alarms, OLT or router configuration automation is
included in Phase 5.

### Network design

The API container needs IP routing to the managed equipment address. A Docker
bridge may NAT the source IP. Allow only the necessary TCP destination/port
pairs between the monitoring worker and the infrastructure network; avoid
allowing arbitrary outbound access to confidential control-plane networks.
A management VRF or VPN is preferred. Prefer dedicated management interfaces
and approved ports, and do not expose RouterOS Winbox/API ports publicly.

Monitor targets must be deliberately selected by an administrator who has
authorization to probe those devices. Failed probes can reflect host firewall,
VPN routing, upstream routing or the API container's source NAT, not hardware
failure. Investigate those before declaring an RF outage.

## 4. APIs

All configuration/mutation operations require authenticated staff CSRF
tokens, and all outbound/dangerous changes require administrator role.

- `GET/PUT /api/billing/schedule` — recurring billing policy
- `GET /api/billing/schedule/preview?period=YYYY-MM` — read-only amounts
- `POST /api/billing/schedule/run` — confirmed manual period run
- `GET /api/billing/schedule/runs` — last 100 runs
- `GET /api/billing/invoices/{id}/items` — itemized snapshots
- `GET/PUT /api/notifications/settings` — enabled channels / readiness
- `PUT /api/customers/{id}/invoice-notifications` — consent preference
- `GET /api/notifications/outbox` — operational outbox status
- `POST /api/notifications/outbox/{id}/retry` — retry exhausted messages
- `GET/POST /api/monitor/targets` — target registry
- `PUT /api/monitor/targets/{id}` — enable/disable / interval
- `POST /api/monitor/targets/{id}/check` — manual bounded TCP probe
- `GET /api/monitor/events` — down / recovery history

## 5. Deployment / rollback

1. Test PostgreSQL backup and restore.
2. Deploy .NET 11 application and apply migration 005 with the existing
   transaction/checksum migration runner.
3. Check **Invoice Automation**, **Notifications**, and **Network Monitoring**
   all show disabled/default state.
4. Create a non-production customer, package and active subscription. Preview
   a billing month without posting anything; review the generated snapshots
   and the journal with a test month.
5. Test SMTP using a disposable internal/test recipient before enabling
   real invoice email, then test Telegram operations chat separately.
6. Create a TCP monitor for a controlled test NAS, verify failed checks,
   threshold transitions, and recovery alerts. Only then monitor production.
7. Avoid using the UI to issue or credit production invoices while testing.
8. Restore PostgreSQL from the verified backup if a full rollback is required.
   **Never downgrade the application to a version that doesn't understand
   new journal or outbox semantics while continuing to accept writes.**

CI uses an unreachable test SMTP port to verify message **enqueueing** without
sending real external email. It exercises billing idempotence and a local TCP
listener for DOWN/recovery state transitions. Live SMTP/Telegram provider
delivery, large-scale NAS monitoring and physical RouterOS acceptance remain
separate production sign-off gates.
