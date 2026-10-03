#!/bin/bash
#
# Creates the second logical database on this Postgres server.
#
# One server, two databases (flyio/postgres.fly.toml): `cvmaker` for CvMaker.Api, created by
# the postgres image itself from POSTGRES_DB, and `authservice` for the external authservice
# instance, created here.
#
# The two services still own their schemas separately — nothing reaches across — they just
# share a machine and therefore a backup and restore unit. That is the deliberate trade for
# running one Fly machine instead of two.
#
# The postgres entrypoint runs everything in /docker-entrypoint-initdb.d/ exactly once, on
# first boot against an empty data directory. It does not re-run on upgrade, so adding a
# database to an existing deployment is a manual CREATE DATABASE, not an edit here.

set -euo pipefail

AUTH_DB="${AUTHSERVICE_DB_NAME:-authservice}"
AUTH_USER="${AUTHSERVICE_DB_USER:-authservice}"

# Falls back to the server's own password so the quick start needs one secret rather than two.
# A real deployment sets AUTHSERVICE_DB_PASSWORD so the two services do not share a credential.
AUTH_PASSWORD="${AUTHSERVICE_DB_PASSWORD:-${POSTGRES_PASSWORD:?POSTGRES_PASSWORD must be set}}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
	SELECT 'CREATE ROLE "$AUTH_USER" LOGIN PASSWORD ' || quote_literal('$AUTH_PASSWORD')
	WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$AUTH_USER')\gexec

	SELECT 'CREATE DATABASE "$AUTH_DB" OWNER "$AUTH_USER"'
	WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$AUTH_DB')\gexec
EOSQL

# Ownership covers the database; the public schema needs saying separately on Postgres 15+,
# where it is no longer writable by every role by default.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$AUTH_DB" <<-EOSQL
	ALTER SCHEMA public OWNER TO "$AUTH_USER";
	GRANT ALL ON SCHEMA public TO "$AUTH_USER";
EOSQL

echo "Created database '$AUTH_DB' owned by '$AUTH_USER'."
