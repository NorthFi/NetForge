-- root/selfhost/db/024_procurement.sql
-- Purchase costs are commercial commitments, not posted ledger entries.
-- Receipt quantity is derived from immutable stock movements, not a counter.
CREATE TABLE procurement_suppliers(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 name varchar(160) NOT NULL UNIQUE,
 account_reference varchar(100) NOT NULL DEFAULT '',
 email varchar(254) NOT NULL DEFAULT '',
 phone varchar(70) NOT NULL DEFAULT '',
 enabled boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE procurement_orders(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 number bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
 supplier_id uuid NOT NULL REFERENCES procurement_suppliers(id) ON DELETE RESTRICT,
 supplier_name varchar(160) NOT NULL,
 warehouse_id uuid NOT NULL REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 currency char(3) NOT NULL DEFAULT 'ZAR' CHECK(currency='ZAR'),
 reference varchar(240) NOT NULL,
 expected_day date,
 state varchar(16) NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','approved','partial','received','cancelled')),
 created_by uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 approved_by uuid REFERENCES app_users(id) ON DELETE RESTRICT,
 approved_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 CHECK(approved_by IS NULL OR approved_by<>created_by),
 CHECK((approved_by IS NULL)=(approved_at IS NULL))
);
CREATE INDEX procurement_orders_state ON procurement_orders(state,created_at DESC);
CREATE TABLE procurement_lines(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 order_id uuid NOT NULL REFERENCES procurement_orders(id) ON DELETE RESTRICT,
 item_id uuid NOT NULL REFERENCES stock_items(id) ON DELETE RESTRICT,
 sku varchar(80) NOT NULL,
 item_name varchar(160) NOT NULL,
 quantity integer NOT NULL CHECK(quantity BETWEEN 1 AND 100000),
 unit_cost numeric(12,2) NOT NULL CHECK(unit_cost>=0),
 UNIQUE(order_id,item_id)
);
CREATE TABLE procurement_receipts(
 request_id uuid PRIMARY KEY REFERENCES stock_movements(request_id) ON DELETE RESTRICT,
 line_id uuid NOT NULL REFERENCES procurement_lines(id) ON DELETE RESTRICT,
 delivery_reference varchar(180) NOT NULL,
 received_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX procurement_receipts_line ON procurement_receipts(line_id);
CREATE TABLE stock_highsite_deployments(
 request_id uuid PRIMARY KEY REFERENCES stock_movements(request_id) ON DELETE RESTRICT,
 highsite_id uuid NOT NULL REFERENCES highsites(id) ON DELETE RESTRICT,
 deployed_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE stock_highsite_assets(
 request_id uuid NOT NULL REFERENCES stock_highsite_deployments(request_id) ON DELETE RESTRICT,
 equipment_id uuid NOT NULL UNIQUE REFERENCES equipment(id) ON DELETE RESTRICT,
 PRIMARY KEY(request_id,equipment_id)
);
-- These records are append-only. Corrections need a future reversal workflow.
CREATE TRIGGER procurement_lines_immutable BEFORE UPDATE OR DELETE ON procurement_lines
 FOR EACH ROW EXECUTE FUNCTION stock_movement_immutable();
CREATE TRIGGER procurement_receipts_immutable BEFORE UPDATE OR DELETE ON procurement_receipts
 FOR EACH ROW EXECUTE FUNCTION stock_movement_immutable();
CREATE TRIGGER stock_highsite_deployments_immutable BEFORE UPDATE OR DELETE ON stock_highsite_deployments
 FOR EACH ROW EXECUTE FUNCTION stock_movement_immutable();
CREATE TRIGGER stock_highsite_assets_immutable BEFORE UPDATE OR DELETE ON stock_highsite_assets
 FOR EACH ROW EXECUTE FUNCTION stock_movement_immutable();
CREATE FUNCTION procurement_order_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Purchase order history is immutable' USING ERRCODE='23514'; END IF;
 IF (to_jsonb(NEW)-ARRAY['state','approved_by','approved_at']) IS DISTINCT FROM
    (to_jsonb(OLD)-ARRAY['state','approved_by','approved_at']) THEN
  RAISE EXCEPTION 'Purchase order terms are immutable' USING ERRCODE='23514';
 END IF;
 IF OLD.state='pending' AND NEW.state='approved' AND NEW.approved_by IS NOT NULL THEN RETURN NEW; END IF;
 IF NEW.approved_by IS DISTINCT FROM OLD.approved_by OR NEW.approved_at IS DISTINCT FROM OLD.approved_at THEN
  RAISE EXCEPTION 'Purchase order approval is immutable' USING ERRCODE='23514';
 END IF;
 IF (OLD.state='pending' AND NEW.state='cancelled') OR
    (OLD.state IN ('approved','partial') AND NEW.state IN ('partial','received','cancelled')) THEN RETURN NEW; END IF;
 RAISE EXCEPTION 'Invalid purchase order transition' USING ERRCODE='23514';
END;
$$;
CREATE TRIGGER procurement_order_guard BEFORE UPDATE OR DELETE ON procurement_orders
 FOR EACH ROW EXECUTE FUNCTION procurement_order_guard();
CREATE VIEW procurement_line_status AS
 SELECT l.*,i.serialized,COALESCE(r.received,0)::integer AS received,
   l.quantity-COALESCE(r.received,0)::integer AS outstanding,
   l.quantity*l.unit_cost AS ordered_value,
   COALESCE(r.received,0)*l.unit_cost AS received_value
 FROM procurement_lines l JOIN stock_items i ON i.id=l.item_id
 LEFT JOIN LATERAL(
    SELECT sum(m.quantity) AS received FROM procurement_receipts r
    JOIN stock_movements m ON m.request_id=r.request_id WHERE r.line_id=l.id
 ) r ON true;
