-- root/selfhost/db/014_staff_session_security.sql
-- Login is case-insensitive. Fail migration rather than silently merge identities.
CREATE UNIQUE INDEX app_users_username_ci_unique ON app_users(lower(username));
ALTER TABLE app_users ADD COLUMN session_generation uuid NOT NULL DEFAULT gen_random_uuid();

-- Any credential, role, identity or enabled-state change invalidates old sessions,
-- including changes made directly by an authorized database administrator.
CREATE FUNCTION netforge_staff_generation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (NEW.username,NEW.password_hash,NEW.role,NEW.enabled)
    IS DISTINCT FROM (OLD.username,OLD.password_hash,OLD.role,OLD.enabled) THEN
   NEW.session_generation := gen_random_uuid();
 END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER netforge_staff_generation BEFORE UPDATE ON app_users
 FOR EACH ROW EXECUTE FUNCTION netforge_staff_generation();

CREATE TABLE staff_sessions(
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 user_id uuid NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
 generation uuid NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL DEFAULT now()+interval '8 hours',
 CHECK(expires_at>created_at)
);
CREATE INDEX staff_sessions_user ON staff_sessions(user_id);
CREATE INDEX staff_sessions_expiry ON staff_sessions(expires_at);
