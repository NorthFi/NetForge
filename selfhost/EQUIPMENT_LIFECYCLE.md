<!-- root/selfhost/EQUIPMENT_LIFECYCLE.md -->
# Equipment recovery and RMA
Migration 025 records physical recovery from customers/highsites, quarantine,
manual inspection, same-serial supplier RMA, independent release/disposal and
immutable lifecycle events. Admin/dispatcher operate recovery and testing;
a different administrator must approve release or disposal after the last
test. Staff must verify physical recovery and legal ownership/authorization.
A recovered deployment retains its historical customer/site, serial and asset
ID, but management IP is cleared and active equipment listing excludes it.
Subsequent deployment creates a new deployment asset, reusing the same physical
stock serial. History is not reassigned. Conditions tested_used/refurbished are
staff attestations, not automated hardware diagnostics.

Only serialized assets with a known serial and assigned branch can be
recovered. Existing stock serial must match the selected SKU; a legacy asset
without a stock serial must match catalogue category and model exactly.
Recover into a non-technician warehouse of the same branch. Existing
TCP monitor, SNMP, CPE probe, RouterOS or backup profiles block recovery even
when disabled. This conservative restriction avoids incorrectly associating
operational history or credentials with a redeployed device. There is no
profile archival/reassignment workflow yet; do not manually delete history
to bypass this rule. Database triggers reject new profile enrollment on
historical equipment. Other independent CRM views may retain historic assets.

Quarantine and RMA stock are outside warehouse balances. A passed inspection
with a condition classification is required before independent release.
Failed inspection permits supplier dispatch, same-serial return to quarantine
for retest, or independent disposal. Warranty end is manually supplied;
RMA reference and supplier are recorded; a supplier need not accept warranty.
No supplier message is sent automatically. Replacements with different
serials are not supported by this workflow. Disposal is irreversible in
this module and is not a financial write-off. Release credits one unit with
an immutable receive movement and links the existing stock serial to the
warehouse; it never inserts a duplicate serial or posts a procurement receipt.
New equipment purchases are not classified as new by default, since manual
receipts may include used equipment. Condition is recorded on the physical
serial, visible in the warehouse serial view, not an automatic valuation
adjustment. Scrapped serials remain in history and cannot be received as
new stock for the same SKU.

No stocktake, bulk returns, replacement serial mapping, warranty claims
automation, inventory valuation, full accounting, active service cancellation
or network commands. Recovery does not change RADIUS service state, remove
NAS configuration, or terminate customer billing. Verify these separately.
Existing independent equipment creation API may still create duplicate serials;
recovery/issue reject known conflicting active assets. Test in staging.
