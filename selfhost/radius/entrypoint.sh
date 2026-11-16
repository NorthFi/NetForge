#!/bin/sh
# root/selfhost/radius/entrypoint.sh
set -eu
: "${POSTGRES_USER:?POSTGRES_USER missing}"
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD missing}"
: "${POSTGRES_DB:?POSTGRES_DB missing}"
# Config interpolation is unsafe for arbitrary quotes: require database credentials
# within the documented ASCII subset. The password should be long and random.
for item in "$POSTGRES_USER" "$POSTGRES_PASSWORD" "$POSTGRES_DB"; do
 case "$item" in *[!A-Za-z0-9_.-]*|'') echo "Invalid PostgreSQL credential characters" >&2; exit 1;; esac
done
R=/etc/freeradius/3.0
cat > "$R/mods-available/sql" <<EOF
sql {
 driver = "rlm_sql_postgresql"
 dialect = "postgresql"
 server = "db"
 port = 5432
 login = "$POSTGRES_USER"
 password = "$POSTGRES_PASSWORD"
 radius_db = "$POSTGRES_DB"
 acct_table1 = "radacct"
 acct_table2 = "radacct"
 postauth_table = "radpostauth"
 authcheck_table = "radcheck"
 authreply_table = "radreply"
 groupcheck_table = "radgroupcheck"
 groupreply_table = "radgroupreply"
 usergroup_table = "radusergroup"
 read_groups = yes
 read_clients = yes
 client_table = "nas"
 pool {
   start = 2
   min = 1
   max = 12
   spare = 3
   uses = 0
   lifetime = 0
   idle_timeout = 60
   retry_delay = 5
 }
 \$INCLUDE \${modconfdir}/\${.:name}/main/\${dialect}/queries.conf
}
EOF
ln -sf ../mods-available/sql "$R/mods-enabled/sql"
# Default authorize/accounting/pre-proxy scripts shipped by FreeRADIUS handle PAP,
# CHAP, MS-CHAP and SQL using the stock site. Remove comments only on bare sql lines.
for f in "$R/sites-enabled/default" "$R/sites-enabled/inner-tunnel"; do
 [ -f "$f" ] || continue
 sed -i -E 's@^([[:space:]]*)-[[:space:]]*sql([[:space:]]*(#.*)?)$@\1sql\2@; s@^([[:space:]]*)#[[:space:]]*sql([[:space:]]*(#.*)?)$@\1sql\2@' "$f"
done
# Static client is loopback only, real NAS clients are read from PostgreSQL
# once at server startup. Restart RADIUS after creating or changing NAS records.
exec freeradius -f -l stdout
