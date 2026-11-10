# root/NETFORGE_ARCHITECTURE.md
# NetForge — ISP Management Platform

NetForge is a self-hosted ISP operations platform. This repository is being rebuilt as a new application; the legacy implementation is not part of the target architecture.

## Planned modules

- Customer CRM: customer identities, addresses, service subscriptions, CPE assignments and support history.
- Network inventory: highsites, towers, devices, interfaces, sectors, antennas and backhaul links.
- RF associations: assign each customer service/CPE to a serving sector, retaining assignment history.
- Topology: model backhaul dependencies and calculate affected services on outages.
- Monitoring: ICMP/SNMP polling, RouterOS v7 integrations, device status and alarms.
- Automation: versioned script library, target resolution, dry-run target preview, approvals, job scheduling, bounded concurrency, isolated execution workers, immutable audit records.
- Billing: plans, invoices, payments and service lifecycle, integrated after core network inventory.
- GIS: highsite, sector, customer and link mapping with terrain profiles.

## Target stack

- Frontend: React + TypeScript
- Backend: Python FastAPI
- Database: PostgreSQL
- Queue: Redis with dedicated execution workers
- Deployment: Linux + Docker Compose, reverse proxy and HTTPS

## Core domain relationships

Highsite -> devices and sectors
Sector -> serving radio/device and customer CPE associations
Customer -> service subscriptions -> CPE -> serving sector
Backhaul -> endpoint devices and sites -> dependency graph
Script -> immutable version -> execution job -> resolved target devices -> per-target results

## Safety requirements

- Network devices are never contacted directly by the browser.
- Device credentials are encrypted at rest and never returned to frontend clients.
- Role-based authorization is enforced server-side for every target and operation.
- Arbitrary uploaded scripts must not execute in the web API process.
- Batch changes require target previews and risk-based approvals.
- Jobs have timeouts, bounded parallelism, cancellation, and per-device audit trails.
- Destructive operations must not be retried automatically without idempotency guarantees.
- Secrets must never be stored in Git.

## Development status

Architecture established. The new application has not yet been implemented or deployed.

## Branch policy

The `Published` branch remains untouched until the replacement has been built, tested, and explicitly promoted. Development proceeds on `netforge-isp-manager`.
