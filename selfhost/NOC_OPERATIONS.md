# root/selfhost/NOC_OPERATIONS.md
# NetForge — NOC incident management (Phase 8)

This phase connects operational monitoring to CRM in the self-hosted
C#/.NET 11 and PostgreSQL application. All records are **staff-only**.
No RADIUS/PPPoE access, billing ledger, account balance, or customer
notification is changed by NOC incident workflows.

## Incident origins and evidence

An incident is automatically opened only when an administrator has
explicitly identified a **highsite upstream TCP monitor** and that
monitor produces the first site-level DOWN transition. The parent
monitor uses repeated TCP connection failures; it is not proof that
every subscriber, sector, backhaul or radio is unavailable.

A single unresolved incident is allowed per highsite. Further
correlated upstream failures do not create duplicate incidents.
When all tagged upstream monitors recover, the incident moves to
`monitoring`, not `resolved`: human staff must verify service recovery
and document why it was resolved. If an upstream fails again before
resolution, the incident reopens without losing its audit history,
assignment, original opening time or linked support ticket.

Staff can also report a manual potential incident for a highsite.
A manual incident can be resolved based on verified staff evidence,
but resolution is blocked while a tagged upstream TCP monitor at
that highsite remains DOWN.

## NOC workflows

The **NOC Incidents** screen provides active/resolved/all filters,
open/acknowledged/monitoring counters, incident assignments and
site-level exposure. Open a record to:

1. Acknowledge an open incident as the signed-in staff member.
2. Assign an existing enabled admin, dispatcher or technician.
3. Append an immutable internal investigation note.
4. Review the timestamped detection, recovery and staff-action history.
5. View a paginated snapshot of possibly affected customers.
6. Create a highsite CRM ticket or link an existing highsite ticket
   belonging to exactly the same highsite. Each incident has one
   primary linked ticket.
7. Resolve with a mandatory explanation only after relevant monitoring
   has recovered (or after manual incidents have been independently
   verified).

Linked CRM tickets are **not** automatically resolved when a NOC
incident is resolved. Staff must update the ticket separately.
Incident notes are not posted as customer-facing ticket messages,
and neither portal passwords nor NAS credentials appear in incident
responses.

## Customer exposure: what the counts mean

At incident creation, NetForge snapshots active customers assigned
to the highsite and the count of each customer's active subscriber
services. It also snapshots the count of sectors on that highsite.

**These are potential exposure counts, not confirmed disconnected
accounts.** A customer's actual PPPoE/RADIUS session, routing, RF
backhaul, power state and failover connectivity may remain healthy.
A subscriber may later move to another site or change status without
rewriting historical incident snapshots.

The main incident list contains counts only. The paginated snapshot
endpoint returns accounts/names to NOC-authorized staff. The user-facing
subscriber portal is not granted access to these records.

Do not use incident duration or exposure estimates as contractual SLA
credits, invoices, automatic refunds, or fault cause attribution.
That needs verified accounting/session/service evidence and formally
approved agreement-specific policy.

## Storage and concurrency

Migration `009_noc_incidents.sql` adds:

- `noc_incidents`: immutable origin, site, summary, state, timestamps,
  severity and operator assignment/resolution metadata.
- `noc_incident_customers`: account/name/service-count exposure
  snapshots, captured at incident creation.
- `noc_incident_history`: append-only workflow timeline.
- `noc_incident_tickets`: one CRM site ticket per incident.

New site transitions and incident updates commit in the **same
PostgreSQL transaction**. Site-impact correlation holds the existing
per-highsite advisory lock. Manual incident creation uses that same
lock, and the database enforces a unique unresolved incident per site.
Ack/assignment/resolution use row locks and write audit entries.

Staff HTTP mutations require authenticated NOC roles plus a CSRF token.
No automatic outbound customer messages or network configuration
actions are enabled by migration 009.

## APIs

All routes below are under `/api`.

| Route | Purpose |
| --- | --- |
| `GET /noc/summary` | Counts and potential exposure |
| `GET /noc/incidents?state=active\|resolved\|all` | Staff incident register |
| `GET /noc/incidents/{id}` | One incident, including primary CRM ticket |
| `GET /noc/incidents/{id}/customers?offset=0&limit=100` | Paginated customer snapshots |
| `GET /noc/incidents/{id}/history` | Internal timeline |
| `POST /noc/incidents` | Confirmed manual highsite incident |
| `POST /noc/incidents/{id}/acknowledge` | Record NOC acknowledgement |
| `POST /noc/incidents/{id}/assign` | Assign an enabled staff account |
| `POST /noc/incidents/{id}/notes` | Internal note, no portal publication |
| `POST /noc/incidents/{id}/ticket` | Create or attach same-site highsite CRM ticket |
| `POST /noc/incidents/{id}/resolve` | Confirmed resolution with narrative |

Viewer and customer portal roles have no incident access.
Administrators and dispatchers can manage internal NOC incidents.
Existing ticket permissions and monitoring roles are unchanged.

## Acceptance and deployment

1. Back up PostgreSQL and the application Data Protection keys.
2. Deploy the development branch to an isolated Linux Docker host.
3. Run the additive migration and verify existing customer, RADIUS,
   invoice and telemetry records are unchanged.
4. Use a **test** highsite with a dedicated upstream TCP monitor.
   Trigger two failures to open one incident. Confirm affected
   customer counts against the site's active assignments.
5. Validate acknowledgement, staff assignment, notes, and linked
   CRM ticket. Trigger upstream recovery and manually resolve.
6. Repeat DOWN/recovery before resolving and ensure the same incident
   reopens without duplicate active incidents.
7. Confirm subscriber portal sessions cannot access NOC APIs.
8. Only after successful tests use NOC functionality with live sites.

NOC incidents have no automated customer notification, credit
calculation, escalation paging, correlation against RADIUS session
disconnections, GPS topology inference, cross-highsite dependency
graphs, or contractual SLA reporting. Those remain future phases.
