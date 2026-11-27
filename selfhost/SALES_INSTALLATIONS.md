<!-- root/selfhost/SALES_INSTALLATIONS.md -->
# Sales and installations
Migration 022 adds sales leads, immutable issued quotations, recorded acceptance,
branch technician rosters, installation orders and chronological work events.

Admin/dispatcher can qualify or close leads, issue/withdraw quotes and record
customer acceptance. Prices are copied from the selected package at issue; the
installation price is explicitly entered, in ZAR. The terms must state the
applicable tax treatment and scope. This is not a tax invoice or automatic
tax engine. Quotations are valid for 1–90 days; an expired outstanding quote
must be withdrawn before reissue. No message or legal signature is generated.
Acceptance records the named accepting party and an external evidence
reference; retain the actual signed quote/email separately. Acceptance is
staff-attested, not independently authenticated customer consent.

Acceptance checks that the package name, price and speeds have not changed
since issue. In a single transaction it creates a pending CRM customer,
acceptance record, customer ticket and unscheduled installation order.
Duplicate acceptances, duplicate customer accounts and invalid related
records roll back. No invoice, RADIUS credentials, service or contract
billing schedule is generated. Existing package billing remains dynamic;
a quote is not a locked recurring tariff for later billing. Verify package
and price again before separately creating/activating a service.

Administrators assign enabled technician accounts to branches. Dispatchers
and admins assign or reschedule orders by calendar day, with no time slots;
multiple jobs per technician/day are intentional. The planner filters by
branch/day or unscheduled work. Technicians can view and update only their
assigned orders within these endpoints. Existing global CRM access policies
are unchanged, not branch-isolated. Start, block and complete transitions
require notes. Admin/dispatcher can cancel unstarted/blocked orders. Removing
a technician from a branch is blocked while open work is assigned. Staff role
changes are managed separately; reassign disabled technicians via planner.
Order history is independent of the linked support ticket, which must be
reviewed and closed separately. Completion does not activate the customer.

Not included: quotation PDF/email, online acceptance, signed contracts,
inventory allocation, drag-and-drop calendar, recurring tasks, technical
coverage survey or automated provisioning. Run CI and exercise the commercial
workflow with a test customer before production.
