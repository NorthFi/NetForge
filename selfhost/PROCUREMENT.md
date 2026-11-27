<!-- root/selfhost/PROCUREMENT.md -->
# Procurement and highsite deployment
Migration 024 adds supplier records, immutable purchase lines and receipt
history, two-person purchase approval, partial goods receiving, reorder
suggestions and direct highsite stock deployment.

Admin manages suppliers. Admin/dispatcher creates an immutable pending PO
with 1–50 unique catalogue SKUs, receiving branch warehouse, quantities,
agreed unit costs in ZAR and a reference. Another administrator must approve
it. Prices are procurement amounts only; no VAT calculation, supplier invoice,
accounts payable, GL journal, weighted-average valuation or payment occurs.
Freight and tax are not separately modelled. To amend a pending order cancel
and create a new one. An approved order may be partially received line by line.
Receipt quantity cannot exceed outstanding quantity. Serialized receipts require
one explicit serial per unit (maximum 100 per receipt); bulk receipts allow
up to 100,000 countable units. A receipt uses the same transaction, stock writer
and advisory lock as manual warehouse receiving, and links the movement to
the purchase line. Duplicate request IDs or serials roll back all changes.
Do not record the same physical delivery through manual warehouse receiving
as well. Cancelling a partially received PO cancels remaining quantities only;
already received stock and its history remain. Disabling a supplier blocks
new orders and approvals but not existing approved receipts.

Reorder recommendations consider existing branch warehouse/item balances,
minimum available quantity and outstanding approved/partial orders. No automatic
PO, demand forecasting, lead time or safety-stock calculation. A zero-stock item
with no balance row is not included. Receipt and deployment histories are paginated.

Direct highsite deployment confirms actual physical issue of unreserved stock
from the same branch as the highsite. A highsite without branch assignment is
ineligible. For serialized equipment the transaction creates highsite equipment,
links the stock serial to the new asset, and records a deployment history.
Bulk stock is consumed without individual equipment records. The existing
warehouse reconciliation includes these issue movements. Management IP and
ownership are deliberately not inferred. No remote network configuration is
changed. This module does not recover issued stock or automatically reverse
a deployment. Existing independent equipment API can still create duplicate
serials; resolve inventory discrepancies before deployment.

Global staff branch access policies remain unchanged. Warehouse and procurement
writes use conservative locks; all quantity changes and audit records commit
together. No return/quarantine/RMA/stocktake, approval thresholds, document PDF,
supplier email, invoice matching or accounting valuation in this phase.
