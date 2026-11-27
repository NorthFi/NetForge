<!-- root/selfhost/WAREHOUSE.md -->
# Warehouse and installation stock
Migration 023 adds branch warehouses, optional technician custody warehouses,
stock catalogue, on-hand/reserved balances, explicit serials, installation
reservations and immutable movement history. Admin creates catalogue and
warehouses; admin/dispatcher manage stock. Technicians do not directly change
stock in this module. Use a technician's existing branch roster membership
when creating a custody warehouse.

Receive against a document reference. For serialized stock, enter each serial
exactly once (case-insensitive within SKU, normalized to uppercase), up to 100
per movement; bulk items use integer quantity in a declared unit. Transfers
are instantaneous acknowledged physical transfers, including between branches
or technician warehouses. There is no in-transit state. Only unreserved
stock can be transferred. Duplicate request IDs return conflict, not a second
movement. Use the movement log to confirm a lost response before retrying.
Catalogue and warehouse selectors are limited to the first 2000 records;
installation reservation picker uses the first 100 orders; API supports
explicit order IDs. Balances, serials and movement logs are paginated.
Low-stock flags apply to existing warehouse/item balances, not every possible
catalogue/warehouse combination.

Reserve stock for an open installation from its own branch. Technician
warehouse stock requires the assigned technician. Reserved stock is still
on-hand, not available. Release returns availability; issue requires an
in-progress installation and creates customer equipment for each serialized
unit, deducting quantity and clearing reservation. Bulk issue consumes stock
without individual equipment assets. Ownership and management IP are left
unset; complete them separately. Existing serial matches in CRM block issue;
the independent manual equipment API can still create duplicate serials.
Complete/cancel is blocked until every reservation is resolved; reassignment
is blocked when a previous technician's stock is still reserved. Issued stock
does not automatically return if work later fails. Reconcile physical stock
before confirming a movement.

All movement transactions serialize on an advisory lock, with order locks
taken first for installation operations; this favors correctness over high
write throughput. Database checks prevent negative balances and over-reserve.
No changes to prior migrations. No inventory valuation, tax, general ledger
posting, purchase orders, supplier/RMA/return/adjustment workflow, stock counts,
direct highsite deployment, or automatic replenishment. Initial receiving
is manual. A technician warehouse is a custody location, not a staff
authorization grant. Disabling or changing a technician account does not
automatically move physical stock. Existing global branch access rules remain
unchanged. Verify this module in staging before live inventory migration.
