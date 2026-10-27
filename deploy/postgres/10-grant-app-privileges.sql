-- 10-grant-app-privileges.sql — grants vlms_app (the runtime application role) exactly the
-- privileges it needs, once the schema actually exists. docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md,
-- governance/raid.md D-004, docs/adr/0004-sensitive-data-access-control.md §4 pass 6.
--
-- Run AFTER `dotnet ef database update` has been applied against "vlms" as vlms_owner (see
-- deploy/README.md for the full ordering) — NOT before. `GRANT ... ON ALL TABLES IN SCHEMA`
-- only ever affects tables that already exist at the moment it runs
-- (postgresql.org/docs/current/sql-grant.html: "GRANT ... ON ALL TABLES IN SCHEMA" applies to
-- existing objects, not future ones) — verified rather than assumed, since it would be a real,
-- silent gap for this script to run first and grant nothing because there was nothing yet to
-- grant on:
--   sudo -u postgres psql -d vlms -f deploy/postgres/10-grant-app-privileges.sql
--
-- Run as the postgres superuser (not vlms_owner) — including for the ALTER DEFAULT PRIVILEGES
-- FOR ROLE vlms_owner statement at the bottom, even though that clause's own doc page phrases the
-- general case as "you can change ... the defaults of roles that you are a member of"
-- (postgresql.org/docs/current/sql-alterdefaultprivileges.html): PostgreSQL's own role-attributes
-- documentation states plainly, without the usual membership caveat, that "a database superuser
-- bypasses all permission checks, except the right to log in"
-- (postgresql.org/docs/current/role-attributes.html) — GRANT/ALTER DEFAULT PRIVILEGES membership
-- requirements are permission checks in exactly that sense. Flagged here as the one point in this
-- file backed by the general superuser rule rather than a command-specific example in the docs, in
-- case a future PostgreSQL version narrows that general rule for this specific command — if this
-- script ever fails on that one statement, connect as vlms_owner directly (or `SET ROLE vlms_owner;`
-- first, which a superuser session can always do) and re-run just that statement.
--
-- Least-privilege design (the load-bearing part of this file, re-check against D-004 before
-- changing anything here): every ordinary table gets SELECT/INSERT/UPDATE/DELETE for vlms_app.
-- ONE table is deliberately excluded from that blanket grant — sensitive_data_access_logs gets
-- SELECT/INSERT only, never UPDATE/DELETE, matching exactly what
-- Vlms.Infrastructure.Auditing.SensitiveDataAuditInterceptor's own writes need (append-only) and
-- exactly what D-004 requires. This is not redundant with the RevokeUpdateDeleteOnSensitiveDataAccessLog
-- migration's `REVOKE UPDATE, DELETE ON sensitive_data_access_logs FROM PUBLIC` — that migration
-- revokes only what PUBLIC-membership would otherwise confer (and PostgreSQL grants nothing to
-- PUBLIC on tables by default in the first place, per that migration's own doc comment), while THIS
-- script is what ensures vlms_app is never explicitly granted UPDATE/DELETE directly — the actually
-- load-bearing half of D-004, per adr/0004 §4 pass 6's own conclusion that the migration's REVOKE
-- has no override effect on a role holding a direct GRANT. Getting this file's grant list wrong is
-- the one way this whole tamper-protection design could be silently defeated.
--
-- __EFMigrationsHistory is excluded from the blanket CRUD grant above — vlms_app never runs
-- migrations at runtime (confirmed: neither Vlms.Web's nor Vlms.Jobs's Program.cs calls
-- Database.Migrate() — see STATE.md's tamper-protection Done entry), so it has no legitimate
-- reason to write it. It DOES get a read-only SELECT grant below: pg_dump (deploy/backup/pg-backup.sh)
-- connects as vlms_app for the nightly logical backup and dumps every table's data via
-- SELECT/COPY, aborting non-zero on the first permission error — without this grant,
-- `pg_dump -Fc` of the whole database fails outright with "permission denied for table
-- __EFMigrationsHistory" the first time it runs. SELECT does not let vlms_app run migrations or
-- write to the table, so this does not weaken the least-privilege/tamper-protection design above.
GRANT SELECT ON public."__EFMigrationsHistory" TO vlms_app;

GRANT CONNECT ON DATABASE vlms TO vlms_app;
GRANT USAGE ON SCHEMA public TO vlms_app;

-- Every existing ordinary table (everything except the two named exclusions) gets full CRUD.
-- Uses information_schema.tables rather than a hand-maintained table list, deliberately — this
-- codebase has 16+ entities today (design/data-design.md) and will gain more; a hardcoded list
-- would silently go stale the next time a migration adds a table, exactly the kind of drift this
-- project's other gate mechanisms (e.g. build/check-access-control.ps1's checklist-currency hash)
-- exist to catch, so this script catches it by construction instead.
DO $$
DECLARE
    tbl RECORD;
BEGIN
    FOR tbl IN
        SELECT table_name
        FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_type = 'BASE TABLE'
          AND table_name NOT IN ('sensitive_data_access_logs', 'deletion_log', '__EFMigrationsHistory')
    LOOP
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO vlms_app', tbl.table_name);
    END LOOP;
END
$$;

-- The one deliberate exception — SELECT/INSERT only, matching D-004 and the audit interceptor's
-- append-only write pattern exactly. If this table does not yet exist when this script runs
-- (e.g. run before migrations by mistake), this statement errors loudly rather than silently
-- granting nothing — a fail-closed outcome, not a fail-open one.
--
-- Since docs/adr/0004-sensitive-data-access-control.md §6 pass 8
-- (EnforceRowLevelSecurityOnSensitiveDataAccessLog), this GRANT is no longer the only thing
-- standing between vlms_app and UPDATE/DELETE on this table — Row-Level Security
-- (ENABLE + FORCE ROW LEVEL SECURITY, no permissive UPDATE/DELETE policy) independently
-- default-denies both commands for every role, including vlms_owner. This GRANT still matters
-- (it governs the separate privilege-grant layer, and matters more for vlms_app's SELECT/INSERT,
-- which the RLS policies permit but still require the underlying GRANT to reach) — it is not made
-- redundant by RLS, the two are deliberately independent layers (see adr/0004 §6(d)).
GRANT SELECT, INSERT ON public.sensitive_data_access_logs TO vlms_app;

-- The retention deletion log (docs/adr/0014-retention-purge.md decision 4): append-only like the
-- audit log. SELECT/INSERT only; the AddDeletionLog migration also revokes UPDATE/DELETE from vlms_app
-- (the default privileges below would otherwise grant them when the table is created) and forces
-- row-level security with no UPDATE/DELETE policy.
GRANT SELECT, INSERT ON public.deletion_log TO vlms_app;
REVOKE UPDATE, DELETE ON public.deletion_log FROM vlms_app;

-- The audit-log purge role (adr/0014 decision 6): may read and delete audit rows and nothing else;
-- the AddDeletionLog migration's DELETE policy limits it to rows older than 6 years. No default
-- privileges are set for it, so future tables give it nothing.
GRANT CONNECT ON DATABASE vlms TO vlms_retention;
GRANT USAGE ON SCHEMA public TO vlms_retention;
GRANT SELECT, DELETE ON public.sensitive_data_access_logs TO vlms_retention;

-- Sequences: NOT granted here, and deliberately so — every primary key in this schema uses a real
-- PostgreSQL identity column (GENERATED BY DEFAULT AS IDENTITY, confirmed by reading
-- src/Vlms.Infrastructure/Migrations/20260720171330_InitialCreate.cs's
-- Npgsql:ValueGenerationStrategy annotations), not the older SERIAL/nextval() pattern. Verified
-- against current PostgreSQL documentation rather than assumed equivalent
-- (postgresql.org/docs/current/ddl-identity-columns.html): identity columns were specifically
-- designed so that table-level INSERT privilege is sufficient on its own — unlike SERIAL, which
-- needs a separate USAGE/SELECT grant on its backing sequence or INSERT fails with "permission
-- denied for sequence". So there is nothing to grant here that the per-table GRANT above doesn't
-- already cover.

-- Future tables: ALTER DEFAULT PRIVILEGES so a brand-new migration's table gets the same broad
-- CRUD grant automatically, without this script needing to be re-run after every single migration.
-- Verified against postgresql.org/docs/current/sql-alterdefaultprivileges.html rather than assumed:
-- "ALTER DEFAULT PRIVILEGES allows you to set the privileges that will be applied to objects
-- created in the future. (It does not affect privileges assigned to already-existing objects.)" —
-- so this cannot retroactively loosen sensitive_data_access_logs's own narrower grant above, and
-- must be run as (or by a role permitted to set defaults for) vlms_owner specifically, since
-- default privileges are scoped `FOR ROLE` — the role that actually creates the future tables via
-- `dotnet ef database update`.
--
-- *** OPERATIONAL WARNING, not a formality: if any future migration introduces another table that
-- needs restricted (non-CRUD) privileges the way sensitive_data_access_logs does today — another
-- audit/tamper-protected table, say — this statement will silently hand vlms_app full CRUD on it
-- the instant that migration runs, with no error and no test failure. Re-review this file whenever
-- such a table is added — do not assume this script still describes the whole picture unchanged;
-- docs/governance/asvs-access-control-checklist.md's V16 Security Logging item already asks
-- whether any new code path skips the audit-log mechanism, and this same re-review discipline
-- extends to "does a new table need the same restricted grant this file gives
-- sensitive_data_access_logs" even though no checklist bullet names this file specifically today. ***
ALTER DEFAULT PRIVILEGES FOR ROLE vlms_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vlms_app;
