-- root/selfhost/db/001_init.sql
-- NetForge .NET 11 / PostgreSQL: repeatable baseline for application and FreeRADIUS 3.2.
-- These tables deliberately use foreign keys and transactional data rather than JSON files.
CREATE TABLE IF NOT EXISTS app_users (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  username varchar(80) NOT NULL UNIQUE,
  password_hash text NOT NULL,
  role varchar(20) NOT NULL DEFAULT 'viewer' CHECK (role IN ('admin','dispatcher','technician','viewer')),
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS branches (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name varchar(160) NOT NULL UNIQUE,
  address text NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS highsites (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  number varchar(80) NOT NULL UNIQUE,
  name varchar(160) NOT NULL,
  address text NOT NULL DEFAULT '',
  branch_id uuid REFERENCES branches(id) ON DELETE RESTRICT,
  latitude double precision,
  longitude double precision,
  CONSTRAINT gps_lat CHECK(latitude IS NULL OR latitude BETWEEN -90 AND 90),
  CONSTRAINT gps_lon CHECK(longitude IS NULL OR longitude BETWEEN -180 AND 180)
);
CREATE TABLE IF NOT EXISTS sectors (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  highsite_id uuid NOT NULL REFERENCES highsites(id) ON DELETE RESTRICT,
  name varchar(160) NOT NULL,
  ssid varchar(160) NOT NULL DEFAULT '',
  azimuth numeric(6,2),
  frequency_mhz numeric(9,2),
  max_customers integer CHECK(max_customers IS NULL OR max_customers>=0),
  UNIQUE(highsite_id,name),
  CONSTRAINT sector_azimuth CHECK(azimuth IS NULL OR (azimuth>=0 AND azimuth<360)),
  UNIQUE(id,highsite_id)
);
CREATE TABLE IF NOT EXISTS customers (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  account varchar(100) NOT NULL UNIQUE,
  first_name varchar(120) NOT NULL,
  surname varchar(120) NOT NULL DEFAULT '',
  email varchar(254) NOT NULL DEFAULT '',
  phone varchar(70) NOT NULL DEFAULT '',
  address text NOT NULL DEFAULT '',
  status varchar(20) NOT NULL DEFAULT 'active' CHECK(status IN ('active','suspended','pending','cancelled')),
  branch_id uuid REFERENCES branches(id) ON DELETE RESTRICT,
  highsite_id uuid REFERENCES highsites(id) ON DELETE RESTRICT,
  sector_id uuid,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT customer_site_sector FOREIGN KEY (sector_id,highsite_id) REFERENCES sectors(id,highsite_id) ON DELETE RESTRICT,
  CONSTRAINT customer_sector_needs_site CHECK(sector_id IS NULL OR highsite_id IS NOT NULL)
);
CREATE TABLE IF NOT EXISTS packages (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name varchar(160) NOT NULL UNIQUE,
  download_mbps integer NOT NULL DEFAULT 10 CHECK(download_mbps>0),
  upload_mbps integer NOT NULL DEFAULT 10 CHECK(upload_mbps>0),
  monthly_price numeric(12,2) NOT NULL DEFAULT 0 CHECK(monthly_price>=0),
  mikrotik_rate_limit varchar(64) NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS services (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  package_id uuid NOT NULL REFERENCES packages(id) ON DELETE RESTRICT,
  username varchar(180) NOT NULL UNIQUE,
  state varchar(20) NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','active','suspended','cancelled')),
  static_ipv4 inet UNIQUE,
  created_at timestamptz NOT NULL DEFAULT now(),
  changed_at timestamptz NOT NULL DEFAULT now(),
  CHECK(static_ipv4 IS NULL OR family(static_ipv4)=4)
);
CREATE INDEX IF NOT EXISTS idx_services_customer ON services(customer_id);
CREATE TABLE IF NOT EXISTS tickets (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  number bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
  customer_id uuid REFERENCES customers(id) ON DELETE RESTRICT,
  highsite_id uuid REFERENCES highsites(id) ON DELETE RESTRICT,
  subject varchar(200) NOT NULL,
  description text NOT NULL DEFAULT '',
  type varchar(20) NOT NULL DEFAULT 'customer' CHECK(type IN ('customer','highsite','voip','other')),
  status varchar(20) NOT NULL DEFAULT 'open' CHECK(status IN ('open','in_progress','pending','resolved','closed')),
  priority varchar(20) NOT NULL DEFAULT 'normal' CHECK(priority IN ('low','normal','high','critical')),
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS invoices (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id uuid NOT NULL REFERENCES customers(id) ON DELETE RESTRICT,
  number varchar(120) NOT NULL UNIQUE,
  period date,
  total numeric(12,2) NOT NULL CHECK(total>=0),
  status varchar(20) NOT NULL DEFAULT 'draft' CHECK(status IN ('draft','issued','paid','void')),
  due_date date,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(customer_id,period)
);
CREATE TABLE IF NOT EXISTS payments (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE RESTRICT,
  amount numeric(12,2) NOT NULL CHECK(amount>0),
  reference varchar(160) NOT NULL,
  received_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS audit_log (
  id bigserial PRIMARY KEY,
  username varchar(80) NOT NULL,
  action varchar(80) NOT NULL,
  entity varchar(60) NOT NULL,
  entity_id text NOT NULL,
  occurred_at timestamptz NOT NULL DEFAULT now()
);
-- FreeRADIUS 3.2 PostgreSQL SQL-module schema. Field names/types follow
-- raddb/mods-config/sql/main/postgresql/schema.sql as used by queries.conf.
CREATE TABLE IF NOT EXISTS radcheck (
  id serial PRIMARY KEY,
  username text NOT NULL DEFAULT '',
  attribute text NOT NULL DEFAULT '',
  op varchar(2) NOT NULL DEFAULT '==',
  value text NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS radcheck_username ON radcheck(username,attribute);
CREATE TABLE IF NOT EXISTS radreply (
  id serial PRIMARY KEY,
  username text NOT NULL DEFAULT '',
  attribute text NOT NULL DEFAULT '',
  op varchar(2) NOT NULL DEFAULT '=',
  value text NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS radreply_username ON radreply(username,attribute);
CREATE TABLE IF NOT EXISTS radgroupcheck (
  id serial PRIMARY KEY,groupname text NOT NULL DEFAULT '',attribute text NOT NULL DEFAULT '',
  op varchar(2) NOT NULL DEFAULT '==',value text NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS radgroupcheck_groupname ON radgroupcheck(groupname,attribute);
CREATE TABLE IF NOT EXISTS radgroupreply (
  id serial PRIMARY KEY,groupname text NOT NULL DEFAULT '',attribute text NOT NULL DEFAULT '',
  op varchar(2) NOT NULL DEFAULT '=',value text NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS radgroupreply_groupname ON radgroupreply(groupname,attribute);
CREATE TABLE IF NOT EXISTS radusergroup (
  id serial PRIMARY KEY,username text NOT NULL DEFAULT '',groupname text NOT NULL DEFAULT '',priority integer NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS radusergroup_username ON radusergroup(username);
CREATE TABLE IF NOT EXISTS radacct (
  radacctid bigserial PRIMARY KEY,
  acctsessionid text NOT NULL,
  acctuniqueid text NOT NULL UNIQUE,
  username text,
  groupname text,
  realm text,
  nasipaddress inet NOT NULL,
  nasportid text,
  nasporttype text,
  acctstarttime timestamptz,
  acctupdatetime timestamptz,
  acctstoptime timestamptz,
  acctinterval bigint,
  acctsessiontime bigint,
  acctauthentic text,
  connectinfo_start text,
  connectinfo_stop text,
  acctinputoctets bigint,
  acctoutputoctets bigint,
  calledstationid text,
  callingstationid text,
  acctterminatecause text,
  servicetype text,
  framedprotocol text,
  framedipaddress inet,
  framedipv6address inet,
  framedipv6prefix inet,
  framedinterfaceid text,
  delegatedipv6prefix inet,
  class text
);
CREATE INDEX IF NOT EXISTS radacct_active_session_idx ON radacct(acctuniqueid) WHERE acctstoptime IS NULL;
CREATE INDEX IF NOT EXISTS radacct_bulk_close ON radacct(nasipaddress,acctstarttime) WHERE acctstoptime IS NULL;
CREATE INDEX IF NOT EXISTS radacct_bulk_timeout ON radacct(acctstoptime NULLS FIRST,acctupdatetime);
CREATE INDEX IF NOT EXISTS radacct_start_user_idx ON radacct(acctstarttime,username);
CREATE TABLE IF NOT EXISTS radpostauth (
 id bigserial PRIMARY KEY,username text NOT NULL,pass text,reply text,
 calledstationid text,callingstationid text,authdate timestamptz NOT NULL DEFAULT now(),class text
);
CREATE TABLE IF NOT EXISTS nas (
 id serial PRIMARY KEY,nasname text NOT NULL UNIQUE,shortname text NOT NULL,
 type text NOT NULL DEFAULT 'other',ports integer,secret text NOT NULL,
 server text,community text,description text,
 require_ma text NOT NULL DEFAULT 'auto',limit_proxy_state text NOT NULL DEFAULT 'auto'
);
CREATE TABLE IF NOT EXISTS nasreload(nasipaddress inet PRIMARY KEY,reloadtime timestamptz NOT NULL);

-- Site/customer equipment is deliberately modeled as assets, not management secrets.
CREATE TABLE IF NOT EXISTS equipment (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id uuid REFERENCES customers(id) ON DELETE RESTRICT,
  highsite_id uuid REFERENCES highsites(id) ON DELETE RESTRICT,
  category varchar(20) NOT NULL CHECK(category IN ('distribution','backhaul','access','power','cpe','router','other')),
  name varchar(160) NOT NULL,
  model varchar(160) NOT NULL DEFAULT '',
  management_ip inet,
  serial_number varchar(160) NOT NULL DEFAULT '',
  ownership varchar(40) NOT NULL DEFAULT '',
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK ((customer_id IS NOT NULL)::int + (highsite_id IS NOT NULL)::int = 1)
);
CREATE INDEX IF NOT EXISTS equipment_customer ON equipment(customer_id);
CREATE INDEX IF NOT EXISTS equipment_highsite ON equipment(highsite_id);
