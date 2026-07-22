-- 00-create-roles-and-database.sql — one-time PostgreSQL provisioning: two roles + the database,
-- deliberately kept separate from 10-grant-app-privileges.sql (see that file for why).
-- docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md, STATE.md hosting-pivot item 6.
--
-- Run once, as the postgres superuser, BEFORE any EF Core migration is ever applied:
--   sudo -u postgres psql -f deploy/postgres/00-create-roles-and-database.sql
--
-- Two roles, not one — this is the load-bearing design decision here, checked against
-- governance/raid.md D-004 and docs/adr/0004-sensitive-data-access-control.md §4 pass 6 rather
-- than assumed: D-004 requires the production application's connecting role to be able to
-- SELECT/INSERT on sensitive_data_access_logs but NEVER the table owner, a superuser, or a
-- BYPASSRLS role, and never explicitly granted UPDATE/DELETE on it. A table's OWNER always has
-- full privileges on it regardless of any GRANT/REVOKE (postgresql.org/docs/current/ddl-priv.html
-- — ownership is itself the broadest privilege, not something REVOKE can touch), so the role that
-- creates the schema (via `dotnet ef database update`) cannot be the same role the running
-- application connects as, or D-004 is violated by construction the moment the first migration
-- runs — no amount of correct GRANT/REVOKE afterwards fixes an ownership problem.
--
--   vlms_owner — LOGIN only; owns the "vlms" database and (by extension, since it creates them)
--                every table `dotnet ef database update` applies. Used ONLY interactively/at
--                deploy time to run migrations — never placed in a running process's
--                ConnectionStrings (not in /etc/vlms/web.env or /etc/vlms/jobs.env).
--   vlms_app   — LOGIN only, NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION, owns nothing.
--                This is the role Vlms.Web/Vlms.Jobs actually connect as in production
--                (ConnectionStrings:VlmsDatabase) — it starts with zero privileges on any table;
--                10-grant-app-privileges.sql (run after migrations create the schema) is what
--                grants it exactly what it needs.
--
-- Neither role is created with BYPASSRLS, confirmed rather than assumed
-- (docs/adr/0004-sensitive-data-access-control.md §6 pass 8, added once the
-- EnforceRowLevelSecurityOnSensitiveDataAccessLog migration made this attribute load-bearing: a
-- role with BYPASSRLS, like a superuser, bypasses Row-Level Security entirely, including the
-- FORCE ROW LEVEL SECURITY that migration relies on to bind sensitive_data_access_logs's owner).
-- postgresql.org/docs/current/sql-createrole.html: "NOBYPASSRLS is the default. Only superuser
-- roles or roles with BYPASSRLS can specify BYPASSRLS" — this script never specifies BYPASSRLS for
-- either role below, both are explicitly NOSUPERUSER, and neither statement below grants BYPASSRLS
-- afterwards anywhere in this repository (grep this repo for "BYPASSRLS" to confirm nothing does),
-- so both roles keep the NOBYPASSRLS default. If this ever changes, re-read adr/0004 §6 pass 8
-- first — granting BYPASSRLS to vlms_app or vlms_owner would silently defeat the RLS tamper
-- protection that migration exists to provide.
--
-- Passwords below are PLACEHOLDER sentinels, same convention as appsettings.json's own checked-in
-- placeholders (never a real credential in this repository) — replace both before running, and
-- never commit the replaced values anywhere; they belong in each role's own use (vlms_owner: a
-- deploy-time secret held by whoever runs migrations; vlms_app: /etc/vlms/web.env and
-- /etc/vlms/jobs.env's ConnectionStrings__VlmsDatabase, per those units' own EnvironmentFile
-- conventions).
--
-- Idempotency: CREATE ROLE/CREATE DATABASE have no IF NOT EXISTS in PostgreSQL (unlike the
-- SQL Server DenyUpdateDeleteOnSensitiveDataAccessLogs migration's own sys.database_principals
-- guard, which this script's two DO blocks below deliberately mirror) — re-running this script
-- against an already-provisioned cluster is safe: each DO block checks pg_roles/pg_database first
-- and skips creation rather than erroring.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vlms_owner') THEN
        CREATE ROLE vlms_owner WITH LOGIN PASSWORD 'PLACEHOLDER_CHANGE_ME_OWNER' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
    END IF;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'vlms_app') THEN
        CREATE ROLE vlms_app WITH LOGIN PASSWORD 'PLACEHOLDER_CHANGE_ME_APP' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
    END IF;
END
$$;

-- CREATE DATABASE cannot run inside a DO block/transaction (postgresql.org/docs/current/sql-createdatabase.html
-- — it must be its own statement) — guarded instead with the standard psql-script idiom of just
-- checking pg_database directly; \gexec is a psql meta-command so this file must be run via psql
-- (as its header instructs), not an arbitrary SQL client.
SELECT 'CREATE DATABASE vlms OWNER vlms_owner'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'vlms')
\gexec

-- vlms_app is not granted anything at the database-object level here — that's
-- 10-grant-app-privileges.sql's job, run after the schema exists. One thing worth stating rather
-- than leaving implicit: PostgreSQL grants CONNECT (and TEMPORARY) on every database to the PUBLIC
-- pseudo-role by default (postgresql.org/docs/current/ddl-priv.html, Table 5.2 — "T" and "c" for
-- DATABASE) — verified rather than assumed, since it's easy to expect CONNECT needs an explicit
-- grant the way table privileges do. So vlms_app can already reach "vlms" the moment both exist,
-- with no grant needed at all; 10-grant-app-privileges.sql still issues an explicit
-- `GRANT CONNECT ... TO vlms_app` anyway, purely for the same self-documenting/greppable-intent
-- reason this codebase already applies elsewhere (e.g. `IgnoreQueryFilters()` must be a reviewable,
-- explicit act rather than a silent default) — not because it changes what vlms_app can do.
