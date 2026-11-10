# root/CPANEL_DEPLOYMENT.md
# NetForge cPanel deployment contract

Target: https://northfi.co.za/netforge/
Hosting: Afrihost cPanel shared hosting.

## Application
- React + TypeScript static build with base path /netforge/.
- PHP REST API under /netforge/api/; verify installed PHP version and extensions first.
- MySQL/MariaDB provisioned through cPanel.
- Database configuration and secrets outside public_html where permitted.
- Frontend routing via scoped .htaccess rules that never rewrite /netforge/api requests.
- HTTPS required; server-side secure sessions, CSRF defense, authorization, and audit logs.

## Automation agent
- Python agent runs outside cPanel on a trusted always-on machine with network access to ISP equipment.
- Outbound authenticated HTTPS job polling, short-lived job leases, target authorization and idempotency protections.
- No public RouterOS API, SSH, or SNMP exposure required.
- Worker runs scripts in restricted execution environments with bounded concurrency, timeouts and complete audit results.

## Operational validation before release
- Verify PHP version, PDO MySQL, cron, rewrite rules, TLS, outbound HTTPS, file permissions and database privileges.
- Verify /netforge/ routing does not affect existing northfi.co.za content.
- Ensure uploads, backups, configuration, credentials and execution logs are not web accessible.
- Test backup restoration and database migrations before deployment.

Status: Deployment design only; no live cPanel access or deployment performed.
