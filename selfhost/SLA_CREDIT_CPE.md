# root/selfhost/SLA_CREDIT_CPE.md
# NetForge — Phase 10: versioned SLA terms, approved credits and CPE evidence

Phase 10 extends `netforge-selfhost-dotnet`. The published/cPanel
deployment is not modified. Read this guide **before** enabling any
customer CPE monitoring or writing service credits to the ledger.

## Scope and operational distinction

1. **SLA schedule**: date-effective, immutable *internal* copies of
   subscriber-service terms. An agreement/document reference is
   required, but it is not a digital signature or proof of customer
   acceptance. No past SLA version can be edited in place.
2. **Credit-control workflow**: staff-attested interruptions and a
   referenced versioned agreement support a manually proposed invoice
   credit. A **different administrator** reviews it, and a separate
   explicit operation posts a balanced financial credit note.
3. **CPE TCP reachability**: checks from the NetForge API server to
   an inventoried, customer-owned CPE/router management IPv4 address.
   The result says whether that TCP management port accepted a
   connection from the server, *not* whether the customer could reach
   public Internet services. This is an additional independent
   observation alongside RADIUS accounting, but not an end-to-end
   subscriber-originating probe.

No probe, accounting event or highsite monitor automatically creates
an interruption or a credit request. No service status, bill, portal
message or RADIUS/PPPoE configuration is changed by monitoring.

## Immutable SLA agreement versions

Administrators select a service, UTC effective date (today or up to
one year in the future), internal availability target
(90.000–99.999%), maximum invoice-credit percentage (0–100%) and
agreement/document reference. Versions are numbered sequentially
under a PostgreSQL service row lock.

A new version may only become effective after every previous version.
The database prevents duplicate dates and versions, and a trigger
rejects **UPDATE** and **DELETE**. This means correcting an incorrect
current version requires a newer effective date and an explicit
governance process, not editing the prior evidence.

Versioned terms are recorded for proposal validation; the existing
internal monthly assurance report does **not** automatically
retroactively apply contract exclusions, service-specific targets
or maintenance windows. True SLA adjudication would require signed
agreement verification, activation history, contractual exclusions,
independent customer-path tests and formal credit policy.

## Proposed service credit lifecycle

- **Propose (admin or dispatcher):** choose a previous
  staff-attested interruption, matching service, issued invoice
  from the same customer, and the SLA version effective when the
  interruption began. The amount is entered manually, must be
  positive ZAR cents, may not exceed the outstanding invoice balance
  and is capped at `invoice.total × version.credit_cap_percent / 100`.
  The reason must be 12–300 characters.
- **Review (admin):** an administrator whose account is **different
  from the proposer** explicitly approves or rejects with a reason.
  The reviewer cannot alter the proposed amount, source or invoice.
  The proposer is also prohibited from rejecting their own proposal.
- **Post (admin, after approval):** an administrator confirms a
  separate irreversible financial posting. The invoice is locked and
  current outstanding amount rechecked to prevent over-credit.
  A deterministic note number is created and the existing
  `credit_notes`, `ledger_journals`, `ledger_lines`, and invoice
  balance/status infrastructure is used. The ledger entry debits
  `REVENUE_RETURNS` and credits `AR`.
- **Replay protection:** posting locks the request. A repeat POST
  returns the previously created credit note without writing a
  second journal. The financial ledger's unique source and immutable
  journal constraints provide additional protection. One active
  proposal/approved/posted request is allowed per interruption and
  invoice.

The legacy administrator manual credit-note endpoint still exists
for non-SLA adjustments. It is **not** automatically governed by the
new approval flow; production accounting governance should restrict
who may use that existing endpoint.

A posted credit reduces invoice receivables; it does not refund a
bank payment, initiate an EFT, issue a customer message or post an
automatic cash settlement. An invoice already fully paid has no
outstanding receivable: the posting endpoint rejects credit amounts
greater than the remaining balance. Refunds and positive customer
balances need a separate explicitly reviewed workflow.

### Separation of duties

At least two distinct staff accounts are required for an approved
SLA credit: one proposer and a separate administrator reviewer.
The final posting is another deliberate action, but may be performed
by the reviewer or another administrator. The system does not
currently require a third distinct user for posting, enforce
per-branch financial approval thresholds, or verify document
signatures. Do not represent this as a complete finance approval
hierarchy.

## CPE management TCP checks

Administrators must choose a `cpe` or `router` equipment record with
a valid IPv4 management address and the **same customer owner** as
the subscriber PPPoE service. Arbitrary URL/hostname/IP input and
other customers' or highsite-owned equipment are rejected.
Ownership and address are checked again before every network probe;
if the asset ownership no longer matches, polling is disabled.

Allowed TCP ports: **22, 80, 443, 8291, 8729**. These reflect
common SSH, HTTP(S), MikroTik Winbox and API-TLS management
services, not a network-wide port scanner. Default interval is
300 seconds (60–3600 accepted), and every new probe is disabled
until specifically enabled. Each connection attempt times out
after 2 seconds. Three consecutive failures mark the target DOWN;
a successful connection recovers it immediately. Each result is
recorded with UTC timestamp, reachability, connection latency and
debounced state.

The poll runs from the **API server**; when Docker/NAT/VPN is
involved, the source address is the actual routed/NAT egress
address. Restrict TCP management ingress to trusted server/VPN
sources in MikroTik firewall rules. Do **not** expose CPE
management ports publicly to support monitoring.

Example diagnostic interpretation:

- CPE TCP reachable + fresh RADIUS: both signals observed, but
  does not prove customer DNS/browsing works.
- CPE TCP unreachable + RADIUS fresh: management service may be
  filtered/unavailable while subscriber forwarding remains healthy.
- CPE TCP unreachable + RADIUS stale: investigate first. Do not
  turn either result into contractual downtime without evidence.

This phase does not deploy a customer-side agent, authenticated
HTTP/DNS probes from the customer's network, or independent
multi-vantage availability tests. Those require managed probes
and explicit customer consent.

### Data lifecycle

`assurance_cpe_samples` is a separate raw history table. The
Phase 7 SNMP retention policy does **not** delete these new
samples. Before monitoring many thousands of CPEs, introduce
partitioning, rollup and a separate opt-in retention plan; monitor
PostgreSQL growth and ensure backups.

## APIs

All endpoints use staff authentication; state mutations require
CSRF and role checks.

| Endpoint | Role | Purpose |
| --- | --- | --- |
| `GET /api/assurance/sla-versions` | NOC staff | Read version history |
| `POST /api/assurance/sla-versions` | Admin | Append immutable future/current terms |
| `GET /api/assurance/credit-requests` | NOC staff | Review proposal status |
| `POST /api/assurance/credit-requests` | Admin/dispatcher | Propose unposted credit |
| `POST /api/assurance/credit-requests/{id}/review` | Different admin | Approve/reject |
| `POST /api/assurance/credit-requests/{id}/post` | Admin | Idempotent credit note + journal |
| `GET /api/assurance/cpe-probes` | NOC staff | List CPE checks |
| `POST /api/assurance/cpe-probes` | Admin | Register disabled/enabled CPE probe |
| `PUT /api/assurance/cpe-probes/{id}` | Admin | Confirm port/interval/enabled state |
| `POST /api/assurance/cpe-probes/{id}/check` | Admin | Manual TCP check |
| `GET /api/assurance/cpe-probes/{id}/history` | NOC staff | Review raw samples |

## Acceptance checklist

1. Back up PostgreSQL and the Data Protection keyring; test restore.
2. Deploy to a test Docker stack; apply
   `012_sla_credits_cpe_probes.sql` through the existing immutable
   migration runner.
3. Record a test SLA v1 and v2. Verify the database refuses to update
   v1 and refuses retroactive or duplicate effective dates.
4. Use an existing staff-attested subscriber interruption from a
   known NOC incident; create a test invoice. Confirm credit
   proposal cap, same-customer and outstanding-balance limits.
5. Try to approve as the proposer (must fail). Sign in as a different
   administrator, approve with a reason, and confirm no credit note
   or ledger entry exists **until explicit posting**.
6. Post once, replay the same request and verify exactly one
   balanced two-line journal and one credit note.
7. Register a CPE with a private management IPv4 address and enable
   only an authorized management TCP port. Verify UP, repeated DOWN
   and recovery; verify no extra service interruption appears.
8. Confirm a subscriber portal session cannot view or modify SLA
   terms, credit requests or saved CPE IP addresses.
9. Test against actual RouterOS/CPE firewall/NAT routing and user
   accounts, and obtain local finance approval before production
   credit posting.

The CI fixture uses localhost TCP reachability, separate staff
credentials, PostgreSQL ledger assertions, legacy RADIUS and
SNMPv3 regression checks. It is not evidence of live MikroTik
interoperability or contractual credit entitlement.
