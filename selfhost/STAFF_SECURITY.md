<!-- root/selfhost/STAFF_SECURITY.md -->
# Staff session security

The staff UI now supports role/access changes, password resets and forced
session revocation. In Staff, select another account and confirm the action.
Only administrators can perform these operations. Self-administration is
intentionally prohibited: use a second authorized administrator for changes
to your own account. No subscriber RADIUS or portal credentials are changed.

Staff sessions are server-authoritative: an eight-hour absolute expiry,
maximum ten simultaneous sessions per account (oldest evicted), and
per-request database validation. Logout deletes the current server session;
administrative changes invalidate all sessions for the affected account.
Old cookies from before migration require signing in again. A request already
executing before revocation may finish. Revocation is not a distributed
transaction with RADIUS, nor does it disconnect subscribers.

## Upgrade

Back up PostgreSQL before deploying. Migration 014 is additive but enforces
case-insensitive uniqueness on staff usernames, consistent with login.
Run this read-only preflight on the NetForge database:

    SELECT lower(username), count(*) FROM app_users
    GROUP BY lower(username) HAVING count(*) > 1;

If any rows are returned, resolve duplicate identities deliberately before
deploying; the migration will otherwise fail rather than merge accounts.
Never edit a previously applied migration. Deploy through the existing
selfhost compose workflow and confirm the API becomes available. All staff
must sign in again. Customer portal sessions are unaffected.

## Operations

- Create a separate named administrator for recovery; never share accounts.
- Password reset does not send a password by email. Deliver it via an
  approved secure channel and coordinate the forced sign-out.
- Role/access changes, resets and all-session revocations write the existing
  audit log without recording passwords or session tokens.
- Session lookups require PostgreSQL. Database outages fail closed.
- Expired and superseded rows are cleaned at the next login for that account;
  operators can periodically remove expired rows with a routine SQL job:
  DELETE FROM staff_sessions WHERE expires_at <= now();
- Run the CI integration suite before production rollout; the new smoke test
  exercises access revocation, privilege checks, case-insensitive uniqueness,
  password reset, logout replay and the self-administration guard.
