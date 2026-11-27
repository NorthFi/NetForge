<!-- root/selfhost/IPAM.md -->
# Static subscriber IPv4 IPAM

Use IPAM for a dedicated, operator-controlled static subscriber range, not for
a dynamic RouterOS /ip pool. Pool creation requires explicit confirmation.
Private, CGNAT and operator-owned public IPv4 ranges are supported, sized
/20 through /30; overlapping pools are rejected globally. Network and
broadcast addresses are excluded, and an optional gateway is permanently
reserved. Other infrastructure can be reserved with a reason.

1. Create a pool in **IPv4 IPAM**, after checking other allocators and routing.
2. Create a pending subscriber service without a static address.
3. Inspect the pool, allocate a specific free address or the first free host.
4. Provision PPPoE credentials in Services. NetForge writes the canonical host
   to the RADIUS Framed-IP-Address reply. RouterOS obtains it at authentication.
5. Verify using the assigned address in RouterOS /ppp active print detail.
   No router changes are made by the IPAM module.

Administrators create pools and mutate allocations/reservations; dispatchers
and technicians can inspect. All mutations are audited. Existing service
addresses in a new pool are automatically reflected, without importing or
duplicating ownership. Legacy service creation is guarded against reserved
managed addresses. Only never-provisioned pending services can release their
address. Cancelled and suspended assignments are retained deliberately.

Unclosed RADIUS sessions and exact Framed-IP-Address replies are conservative
holds on otherwise unassigned addresses. They are not live reachability tests.
No automated cleanup, DHCP discovery, IPv6, VRFs, routing, NAT, remote RouterOS
API access or live disconnect is implemented in this phase. External NAS
dynamic allocations may occur outside the database transaction; the operator
must exclude all managed ranges from those allocators before enabling IPAM.

## Upgrade
Back up PostgreSQL. Migration 015 adds a host-mask constraint: review legacy
rows with `SELECT id,static_ipv4 FROM services WHERE masklen(static_ipv4)<>32;`
and explicitly correct any non-host assignments before deployment. Do not
modify previously applied migration files. The API and UI are additive.

Reference: https://help.mikrotik.com/docs/spaces/ROS/pages/328097/RADIUS
