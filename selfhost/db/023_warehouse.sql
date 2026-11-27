-- root/selfhost/db/023_warehouse.sql
-- A quantity is one countable stock unit (e.g. one radio or one metre).
-- Reserve without deducting on-hand; issue deducts both on-hand and reserved.
CREATE TABLE stock_items(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 sku varchar(80) NOT NULL UNIQUE CHECK(sku=upper(sku)),
 name varchar(160) NOT NULL,
 model varchar(160) NOT NULL DEFAULT '',
 category varchar(20) NOT NULL CHECK(category IN ('distribution','backhaul','access','power','cpe','router','other')),
 unit varchar(20) NOT NULL,
 serialized boolean NOT NULL,
 min_available integer NOT NULL DEFAULT 0 CHECK(min_available>=0),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE stock_warehouses(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 branch_id uuid NOT NULL REFERENCES branches(id) ON DELETE RESTRICT,
 name varchar(160) NOT NULL,
 technician_id uuid REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(branch_id,name)
);
CREATE UNIQUE INDEX stock_one_tech_warehouse ON stock_warehouses(branch_id,technician_id) WHERE technician_id IS NOT NULL;
CREATE TABLE stock_balances(
 warehouse_id uuid NOT NULL REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 item_id uuid NOT NULL REFERENCES stock_items(id) ON DELETE RESTRICT,
 on_hand integer NOT NULL DEFAULT 0 CHECK(on_hand BETWEEN 0 AND 100000000),
 reserved integer NOT NULL DEFAULT 0 CHECK(reserved>=0 AND reserved<=on_hand),
 PRIMARY KEY(warehouse_id,item_id)
);
CREATE TABLE stock_reservations(
 id uuid PRIMARY KEY,
 order_id uuid NOT NULL REFERENCES installation_orders(id) ON DELETE RESTRICT,
 warehouse_id uuid NOT NULL REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 item_id uuid NOT NULL REFERENCES stock_items(id) ON DELETE RESTRICT,
 quantity integer NOT NULL CHECK(quantity BETWEEN 1 AND 100000),
 state varchar(12) NOT NULL DEFAULT 'reserved' CHECK(state IN ('reserved','released','issued')),
 created_at timestamptz NOT NULL DEFAULT now(),
 resolved_at timestamptz
);
CREATE INDEX stock_reservations_order ON stock_reservations(order_id,state);
CREATE TABLE stock_serials(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 item_id uuid NOT NULL REFERENCES stock_items(id) ON DELETE RESTRICT,
 serial varchar(160) NOT NULL CHECK(serial=upper(serial)),
 warehouse_id uuid REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 reservation_id uuid REFERENCES stock_reservations(id) ON DELETE RESTRICT,
 equipment_id uuid UNIQUE REFERENCES equipment(id) ON DELETE RESTRICT,
 UNIQUE(item_id,serial),
 CHECK((warehouse_id IS NULL)=(equipment_id IS NOT NULL)),
 CHECK(equipment_id IS NULL OR reservation_id IS NULL)
);
CREATE INDEX stock_serials_available ON stock_serials(warehouse_id,item_id) WHERE reservation_id IS NULL AND equipment_id IS NULL;
CREATE TABLE stock_movements(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 request_id uuid NOT NULL UNIQUE,
 action varchar(12) NOT NULL CHECK(action IN ('receive','transfer','reserve','release','issue')),
 item_id uuid NOT NULL REFERENCES stock_items(id) ON DELETE RESTRICT,
 from_warehouse uuid REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 to_warehouse uuid REFERENCES stock_warehouses(id) ON DELETE RESTRICT,
 reservation_id uuid REFERENCES stock_reservations(id) ON DELETE RESTRICT,
 quantity integer NOT NULL CHECK(quantity>0),
 serials text[] NOT NULL DEFAULT '{}',
 reference varchar(240) NOT NULL,
 actor_id uuid NOT NULL REFERENCES app_users(id) ON DELETE RESTRICT,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE FUNCTION stock_movement_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Stock movement history is immutable' USING ERRCODE='23514';
END;
$$;
CREATE TRIGGER stock_movement_no_edit BEFORE UPDATE OR DELETE ON stock_movements
 FOR EACH ROW EXECUTE FUNCTION stock_movement_immutable();
