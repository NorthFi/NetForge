#!/usr/bin/env bash
# root/selfhost/tests/billing-upgrade-smoke.sh
# Verify one-time backfill of invoices/payments created by prior NetForge releases.
set -Eeuo pipefail
PG="$(docker ps --filter ancestor=postgres:17 --format '{{.ID}}' | head -n1)"
test -n "$PG"
docker exec "$PG" psql -U netforge_ci -d postgres -v ON_ERROR_STOP=1  -c 'CREATE DATABASE netforge_upgrade_ci' >/dev/null
for name in 001_init.sql 002_radius_management.sql 003_subscriber_portal_accounting.sql; do
 docker cp "selfhost/db/$name" "$PG:/tmp/$name" >/dev/null
 docker exec "$PG" psql -U netforge_ci -d netforge_upgrade_ci -v ON_ERROR_STOP=1    -f "/tmp/$name" >/dev/null
done
docker exec -i "$PG" psql -U netforge_ci -d netforge_upgrade_ci -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
INSERT INTO customers(account,first_name) VALUES('UPGRADE-CI','Legacy');
INSERT INTO invoices(customer_id,number,total,status,due_date)
 SELECT id,'UPGRADE-INVOICE-100',100,'paid','2026-01-15'
 FROM customers WHERE account='UPGRADE-CI';
INSERT INTO payments(invoice_id,amount,reference)
 SELECT id,100,'UPGRADE-EFT-001'
 FROM invoices WHERE number='UPGRADE-INVOICE-100';
SQL
docker cp selfhost/db/004_financial_ledger_reconciliation.sql  "$PG:/tmp/004_financial_ledger_reconciliation.sql" >/dev/null
docker exec "$PG" psql -U netforge_ci -d netforge_upgrade_ci -v ON_ERROR_STOP=1  -f /tmp/004_financial_ledger_reconciliation.sql >/dev/null
RESULT="$(docker exec "$PG" psql -U netforge_ci -d netforge_upgrade_ci -Atqc "
 SELECT (SELECT count(*) FROM ledger_journals),
        (SELECT count(*) FROM ledger_lines),
        (SELECT sum(debit) FROM ledger_lines),
        (SELECT sum(credit) FROM ledger_lines),
        (SELECT sum(outstanding) FROM invoice_balances),
        (SELECT count(*) FROM ledger_journals WHERE source_type IN ('legacy_invoice','legacy_payment'));" )"
test "$RESULT" = "2|4|200.00|200.00|0.00|2" || {
 echo "Unexpected legacy journal backfill: $RESULT" >&2;exit 1;
}
echo "Legacy invoice/payment upgrade journal backfill validated."
