-- root/selfhost/db/015_ipv4_ipam.sql
-- Static subscriber pools only. The existing services.static_ipv4 remains
-- authoritative, including allocations made before IPAM was introduced.
ALTER TABLE services ADD CONSTRAINT services_static_ipv4_host
 CHECK(static_ipv4 IS NULL OR masklen(static_ipv4)=32);
CREATE TABLE ipam_pools(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 name varchar(100) NOT NULL UNIQUE,
 subnet cidr NOT NULL CHECK(family(subnet)=4 AND masklen(subnet) BETWEEN 20 AND 30),
 gateway inet CHECK(gateway IS NULL OR (family(gateway)=4 AND masklen(gateway)=32
    AND gateway <<= subnet AND gateway<>set_masklen(subnet::inet,32)
    AND gateway<>set_masklen(broadcast(subnet::inet),32))),
 description varchar(240) NOT NULL DEFAULT '',
 created_at timestamptz NOT NULL DEFAULT now(),
 EXCLUDE USING gist (subnet inet_ops WITH &&)
);
CREATE TABLE ipam_reservations(
 address inet PRIMARY KEY CHECK(family(address)=4 AND masklen(address)=32),
 pool_id uuid NOT NULL REFERENCES ipam_pools(id) ON DELETE RESTRICT,
 reason varchar(240) NOT NULL CHECK(length(reason)>=3),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ipam_reservations_pool ON ipam_reservations(pool_id);
-- An unclosed accounting session is evidence of possible use, not proof of
-- current connectivity. Retain it as a block until accounting is reconciled.
CREATE INDEX ipam_unclosed_address ON radacct((host(framedipaddress)))
 WHERE acctstoptime IS NULL AND framedipaddress IS NOT NULL;
CREATE INDEX ipam_radius_reply_address ON radreply(value)
 WHERE attribute='Framed-IP-Address';

CREATE FUNCTION ipam_observed(ip inet) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM radacct a WHERE a.acctstoptime IS NULL
   AND a.framedipaddress IS NOT NULL AND host(a.framedipaddress)=host(ip))
$$;
CREATE FUNCTION ipam_radius_configured(ip inet) RETURNS boolean LANGUAGE sql STABLE AS $$
 SELECT EXISTS(SELECT 1 FROM radreply r WHERE r.attribute='Framed-IP-Address'
   AND r.value IN (host(ip),host(ip)||'/32'))
$$;

-- Protect the legacy service-create API as well as IPAM allocation. The same
-- transaction-scoped lock is taken before pool/reservation changes in the API.
-- The existing unique services.static_ipv4 constraint is the final guard
-- against assigning the same host to two subscribers.
CREATE FUNCTION ipam_guard_static() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p ipam_pools%ROWTYPE;
BEGIN
 IF NEW.static_ipv4 IS NULL THEN RETURN NEW; END IF;
 IF TG_OP='UPDATE' THEN
   IF NEW.static_ipv4 IS NOT DISTINCT FROM OLD.static_ipv4 THEN RETURN NEW; END IF;
 END IF;
 PERFORM pg_advisory_xact_lock(3100315);
 SELECT * INTO p FROM ipam_pools WHERE NEW.static_ipv4 <<= subnet;
 IF FOUND AND (
    NEW.static_ipv4=set_masklen(p.subnet::inet,32)
    OR NEW.static_ipv4=set_masklen(broadcast(p.subnet::inet),32)
    OR NEW.static_ipv4=p.gateway
    OR EXISTS(SELECT 1 FROM ipam_reservations WHERE address=NEW.static_ipv4)
    OR ipam_observed(NEW.static_ipv4)
    OR ipam_radius_configured(NEW.static_ipv4)
 ) THEN
   RAISE EXCEPTION 'Managed address is reserved or in use' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER ipam_guard_static BEFORE INSERT OR UPDATE OF static_ipv4 ON services
 FOR EACH ROW EXECUTE FUNCTION ipam_guard_static();

-- Bounded inventory: /20 is at most 4094 usable hosts. Network and broadcast
-- addresses are never generated, and the gateway is a permanent reservation.
CREATE VIEW ipam_inventory AS
 SELECT p.id AS pool_id,h.address,s.id AS service_id,s.username,c.account,
    s.state AS service_state,r.reason,
    flags.observed,flags.radius_configured,
    CASE WHEN s.id IS NOT NULL THEN 'assigned'
         WHEN h.address=p.gateway THEN 'gateway'
         WHEN r.address IS NOT NULL THEN 'reserved'
         WHEN flags.observed THEN 'accounting_hold'
         WHEN flags.radius_configured THEN 'radius_hold'
         ELSE 'free' END AS state
 FROM ipam_pools p
 CROSS JOIN LATERAL generate_series(1::bigint,
    set_masklen(broadcast(p.subnet::inet),32)-set_masklen(p.subnet::inet,32)-1) AS offsets(n)
 CROSS JOIN LATERAL (SELECT set_masklen(p.subnet::inet,32)+offsets.n AS address) h
 LEFT JOIN services s ON s.static_ipv4=h.address
 LEFT JOIN customers c ON c.id=s.customer_id
 LEFT JOIN ipam_reservations r ON r.address=h.address
 CROSS JOIN LATERAL (SELECT ipam_observed(h.address) AS observed,
    ipam_radius_configured(h.address) AS radius_configured) flags;
