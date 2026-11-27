-- root/selfhost/db/025_equipment_lifecycle.sql
-- A deployment asset is historical once recovered. The physical stock serial
-- persists across deployments; old customer/site and monitoring history cannot
-- be silently reassigned to a new customer or highsite.
ALTER TABLE equipment ADD COLUMN recovered_at timestamptz;
CREATE INDEX equipment_current_serial ON equipment(upper(serial_number)) WHERE recovered_at IS NULL;
ALTER TABLE stock_serials ADD COLUMN condition varchar(16) NOT NULL DEFAULT 'unclassified'
 CHECK(condition IN ('unclassified','tested_used','refurbished'));
CREATE TABLE equipment_recoveries(
 id uuid PRIMARY KEY,
 equipment_id uuid NOT NULL UNIQUE REFERENCES equipment(id) ON DELETE RESTRICT,
 serial_id uuid NOT NULL REFERENCES stock_serials(id) ON DELETE RESTRICT,
 warehouse_id uuid NOT NULL REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 state varchar(16) NOT NULL DEFAULT 'quarantine'
   CHECK(state IN ('quarantine','passed','failed','rma','released','scrapped')),
 warranty_until date,
 supplier_id uuid REFERENCES procurement_suppliers(id) ON DELETE RESTRICT,
 rma_reference varchar(160),
 last_test_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
 condition varchar(16) CHECK(condition IN ('tested_used','refurbished')),
 created_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now(),
 resolved_at timestamptz,
 CHECK((state IN ('released','scrapped'))=(resolved_at IS NOT NULL))
);
CREATE UNIQUE INDEX equipment_recovery_open_serial ON equipment_recoveries(serial_id)
 WHERE state NOT IN ('released','scrapped');
CREATE TABLE equipment_lifecycle_events(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 request_id uuid NOT NULL UNIQUE,
 recovery_id uuid NOT NULL REFERENCES equipment_recoveries(id) ON DELETE RESTRICT,
 action varchar(24) NOT NULL,
 note varchar(1000) NOT NULL,
 supplier_id uuid REFERENCES procurement_suppliers(id) ON DELETE RESTRICT,
 rma_reference varchar(160),
 actor_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX equipment_lifecycle_events_case ON equipment_lifecycle_events(recovery_id,id);
CREATE TRIGGER equipment_lifecycle_events_immutable BEFORE UPDATE OR DELETE ON equipment_lifecycle_events
 FOR EACH ROW EXECUTE FUNCTION stock_movement_immutable();
-- Prevent new operational enrollment against retired equipment. The row lock
-- serializes enrollment against the recovery's FOR UPDATE lock.
CREATE FUNCTION equipment_require_current() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE retired timestamptz;
BEGIN
 IF NEW.equipment_id IS NOT NULL THEN
   SELECT recovered_at INTO retired FROM equipment WHERE id=NEW.equipment_id FOR SHARE;
   IF retired IS NOT NULL THEN RAISE EXCEPTION 'Recovered equipment is historical' USING ERRCODE='23514'; END IF;
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER monitor_equipment_current BEFORE INSERT OR UPDATE OF equipment_id ON monitor_targets
 FOR EACH ROW EXECUTE FUNCTION equipment_require_current();
CREATE TRIGGER snmp_equipment_current BEFORE INSERT OR UPDATE OF equipment_id ON snmp_devices
 FOR EACH ROW EXECUTE FUNCTION equipment_require_current();
CREATE TRIGGER probe_equipment_current BEFORE INSERT OR UPDATE OF equipment_id ON assurance_cpe_probes
 FOR EACH ROW EXECUTE FUNCTION equipment_require_current();
CREATE TRIGGER routeros_equipment_current BEFORE INSERT OR UPDATE OF equipment_id ON routeros_devices
 FOR EACH ROW EXECUTE FUNCTION equipment_require_current();
CREATE TRIGGER backup_equipment_current BEFORE INSERT OR UPDATE OF equipment_id ON backup_profiles
 FOR EACH ROW EXECUTE FUNCTION equipment_require_current();
