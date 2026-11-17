# root/selfhost/SERVICE_ASSURANCE.md
# NetForge — Subscriber service assurance (Phase 9)

This runbook covers the development branch `netforge-selfhost-dotnet`.
The feature connects FreeRADIUS session accounting, highsite NOC incidents,
and **staff-attested** service interruptions. Reports are deliberately
conservative: NetForge cannot prove application-layer internet reachability
from RADIUS accounting alone.

## Evidence types and boundaries

**1. RADIUS accounting evidence (automatic).**
An incident snapshots active PPPoE services belonging to the customers
assigned to the highsite when the NOC incident was opened. For each
service, the application records one observation from the PostgreSQL
`radacct` table. Administrators/dispatchers can capture additional
observations without modifying or overwriting the original ones.

Every observation contains the PPPoE username recorded at incident
creation, timestamp, open accounting session count, number of open
sessions with recently observed interim updates, timestamp of the last
accounting update, last session stop, and available termination cause.

Possible states:

| State | Interpretation |
| --- | --- |
| `recent_accounting` | At least one open session with a recent Acct-Start or Acct-Interim-Update |
| `stale_accounting` | Open accounting session row exists, but no fresh interim update was observed |
| `recent_stop_observed` | No open session row, but a recent Acct-Stop was recorded |
| `no_accounting_data` | None of the above were observed |

The freshness threshold defaults to 10 minutes (2–120 permitted).
It should exceed the configured RouterOS accounting interim-update
period plus reasonable delay and packet loss. A stale entry might be
caused by NAS accounting configuration, packet loss to UDP 1813,
RADIUS restart, stale database records, lack of device interim updates,
or a real subscriber session issue. The absence of an open RADIUS
session **does not establish subscriber internet downtime**.
Multiple concurrent sessions are counted separately.

These observations are point-in-time, not a continuous stream of
customer verification. They are not charged, averaged into uptime,
or used to suspend/restore PPPoE. Snapshot membership is stable even
if the customer later changes site, package, username or state.

**2. Staff-attested interruptions (explicit).**
Staff with NOC permissions may record a start and end UTC timestamp
for a *specific* service, but only after selecting a legitimate NOC
incident containing that service in its original exposure snapshot,
or a CRM support ticket linked to the subscriber or its highsite.
A written evidence description (20–2000 characters) and explicit
confirmation are required. The record stores the staff username
and verification timestamp.

Entries are **append-only**, cannot overlap for a service (enforced
with PostgreSQL exclusion constraints), and cannot be backdated
before the service was created or ended in the future. A single
entry can span up to 90 days, but reports clip the interval to
their reporting month.

An operator statement is not independently validated by the software.
Ensure a documented customer-side test, technician observation, RF
fault log or other credible evidence exists before recording time
as attested. Disagreements require separate governance; Phase 9
does not provide an edit/delete or credit adjustment function.
Use database change control to correct erroneous attestations.

## Monthly internal assurance reports

The **Service Assurance** staff screen accepts a `YYYY-MM` UTC month
and optional customer filter. It reports each subscription's
administrative observation window, bounded by the service creation
timestamp and the earlier of UTC now or month end, and shows:

- Staff-attested downtime minutes, clipped to that observation window.
- Number of documented interruption intervals.
- Recorded RADIUS sessions and completed sessions in the window
  (context only, **not** verified uptime/downtime).
- Configurable internal target (default **99.500%**), a notional
  downtime budget and a finding for recorded downtime exceeding
  that budget.
- `maximum_possible_availability_percent`: an **upper bound**
  calculated as `(observation window - attested downtime) /
  observation window`. It assumes zero other interruptions. It is
  *not* measured service availability or proof of an SLA being met.

The report emits `documented_threshold_exceeded` only when
documented minutes exceed the internal budget. Otherwise the finding
is `insufficient_evidence_for_compliance` — **never `SLA met`**.

The built-in CSV export is explicitly labeled internal evidence.
Do not present it to customers as a signed contractual uptime
certificate. CSV text values are escaped to reduce spreadsheet
formula-injection risk.

### Why reports are not contractual SLA determinations

NetForge does not yet track a versioned subscriber contract,
service activation/deactivation history, failover topology,
maintenance exclusions, fault attribution, planned outages,
service-specific SLA exceptions, customer-premises power faults,
application-layer probe results or independent network path tests.
Current service state is not reliable as historical activation state.

A value of 100% in the **maximum possible** column can simply mean
no staff entered verified interruption evidence. It must not
be interpreted as 100% uptime. The internal target is not applied
automatically to billing credits, customer communications, refunds
or accounting journals.

## APIs and permissions

Authenticated **administrator or dispatcher** staff, with CSRF on
mutating requests:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/assurance/policy` | Internal threshold and accounting freshness |
| `PUT /api/assurance/policy` | Admin-only confirmed policy update |
| `GET /api/assurance/incidents/{id}/evidence` | Paginated original incident service evidence |
| `POST /api/assurance/incidents/{id}/refresh` | Confirmed fresh RADIUS accounting snapshot |
| `GET /api/assurance/incidents/{id}/evidence/{serviceId}/history` | Immutable observation history |
| `GET /api/assurance/interruptions?serviceId=...` | Attested interruption ledger |
| `POST /api/assurance/interruptions` | Document and attest one service interval |
| `GET /api/assurance/reports/monthly?period=YYYY-MM&customerId=...` | Internal monthly report |

Customer portal credentials do not grant access to the above routes.
No automatic customer-facing notifications are added in Phase 9.
API diagnostics do not disclose RADIUS shared secrets or PPPoE
passwords.

## Deployment

1. Back up PostgreSQL and Data Protection key material; test restore.
2. Deploy to a **test** stack and verify additive migrations
   `010_subscriber_assurance.sql` and
   `011_assurance_indexes_audit.sql`.
3. Create a test subscriber and highsite; verify the expected
   accounting interim-update interval and NAS reachability over
   UDP 1813.
4. Trigger a test NOC upstream incident, verify its original
   service snapshot and evidence state.
5. Independently test a live RADIUS Acct-Start, Acct-Interim-Update
   and Acct-Stop. Refresh evidence and verify the transition.
6. Attempt an invalid association and an overlapping attestation:
   both should return HTTP 409. Record a legitimate test interval
   and verify monthly clipping and the internal threshold finding.
7. Confirm customer portal users cannot access staff reports or
   incident evidence. Verify a read-only viewer cannot create
   interruption records.
8. Keep automated credits, suspensions and customer SLA claims
   **disabled/not implemented** until separate contractual and
   monitoring acceptance tests pass.

CI exercises the synthetic RADIUS accounting transitions, incident
session snapshots, staff attestations, duplicate prevention,
monthly budget screening, access isolation and existing RADIUS/
SNMP/financial regression tests. Live MikroTik RouterOS integration,
multi-NAS failover, subscriber end-to-end HTTP probes and
contractual SLA adjudication remain unimplemented.
