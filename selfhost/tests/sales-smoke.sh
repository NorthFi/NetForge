#!/usr/bin/env bash
# root/selfhost/tests/sales-smoke.sh
# Transactional onboarding and branch day planning; no RADIUS or billing side effects.
api POST /sales/leads "{\"firstName\":\"CI Sales\",\"surname\":\"Prospect\",\"phone\":\"0123456789\",\"address\":\"CI test installation\",\"branchId\":\"$BRANCH\",\"source\":\"referral\"}" 201
LEAD="$(id)"
api POST "/sales/leads/$LEAD/quotes" "{\"packageId\":\"$PACKAGE\",\"installationPrice\":500,\"validDays\":14,\"terms\":\"CI commercial terms\",\"confirm\":true}" 409
api POST "/sales/leads/$LEAD/state" '{"state":"qualified","confirm":true}' 200
api POST "/sales/leads/$LEAD/quotes" "{\"packageId\":\"$PACKAGE\",\"installationPrice\":500,\"validDays\":14,\"terms\":\"CI commercial terms\",\"confirm\":true}" 201
QUOTE="$(id)"
api POST "/sales/leads/$LEAD/quotes" "{\"packageId\":\"$PACKAGE\",\"installationPrice\":600,\"validDays\":14,\"terms\":\"CI duplicate\",\"confirm\":true}" 409
# Force an existing-account collision; everything must roll back.
EXISTING_ACCOUNT="$(docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc 'SELECT account FROM customers ORDER BY account LIMIT 1')"
PAYLOAD="$(python3 -c 'import json,sys;print(json.dumps(dict(account=sys.argv[1],acceptedByName="CI customer",evidenceReference="CI signed quote",confirm=True)))' "$EXISTING_ACCOUNT")"
api POST "/sales/leads/$LEAD/quotes/$QUOTE/accept" "$PAYLOAD" 409
api POST "/sales/leads/$LEAD/quotes/$QUOTE/accept" '{"account":"CI-ONBOARD-022","acceptedByName":"CI customer","evidenceReference":"CI signed quote","confirm":true}' 201
INSTALL="$(id)"
NEW_CUSTOMER="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["customerId"])' "$BASE/result.json")"
api POST "/sales/leads/$LEAD/quotes/$QUOTE/accept" '{"account":"CI-ONBOARD-DUP","acceptedByName":"CI customer","evidenceReference":"CI signed quote","confirm":true}' 409
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT status FROM customers WHERE id='$NEW_CUSTOMER'" | grep -qx pending
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT (SELECT count(*) FROM services WHERE customer_id='$NEW_CUSTOMER')+(SELECT count(*) FROM invoices WHERE customer_id='$NEW_CUSTOMER')" | grep -qx 0
api POST /staff '{"username":"ci-install-tech","password":"ci_install_unique_password_2026","role":"technician"}' 201
INSTALL_TECH="$(id)"
DAY="$(date -u +%F)"
api POST "/installations/orders/$INSTALL/schedule" "{\"technicianId\":\"$INSTALL_TECH\",\"day\":\"$DAY\",\"confirm\":true}" 409
api PUT "/installations/roster/$BRANCH" "{\"userId\":\"$INSTALL_TECH\",\"enabled\":true,\"confirm\":true}" 200
api POST "/installations/orders/$INSTALL/schedule" "{\"technicianId\":\"$INSTALL_TECH\",\"day\":\"$DAY\",\"confirm\":true}" 200
api PUT "/installations/roster/$BRANCH" "{\"userId\":\"$INSTALL_TECH\",\"enabled\":false,\"confirm\":true}" 409
api POST "/installations/orders/$INSTALL/state" '{"state":"completed","note":"Invalid transition","confirm":true}' 409
api GET "/installations/orders?branch=$BRANCH&day=$DAY" "" 200
grep -q 'CI-ONBOARD-022' "$BASE/result.json"
api POST "/installations/orders/$INSTALL/state" '{"state":"in_progress","note":"Work started on site","confirm":true}' 200
api POST "/installations/orders/$INSTALL/state" '{"state":"completed","note":"Installation verified manually","confirm":true}' 200
api GET "/installations/orders/$INSTALL/events" "" 200
grep -q '"action":"completed"' "$BASE/result.json"
api PUT "/installations/roster/$BRANCH" "{\"userId\":\"$INSTALL_TECH\",\"enabled\":false,\"confirm\":true}" 200
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT status FROM customers WHERE id='$NEW_CUSTOMER'" | grep -qx pending
echo "Sales and installation workflow smoke passed."
