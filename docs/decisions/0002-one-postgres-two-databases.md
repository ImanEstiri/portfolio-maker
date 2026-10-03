# ADR 0002 — One Postgres app, two logical databases

**Status:** Accepted
**Date:** 2026-08-14

## Context

The deployed system needs two databases: one for cv-maker's own data, one for the authservice
instance it runs. They can be two Fly Postgres apps or one app hosting both.

## Options

**A. Two Postgres apps.** Independent failure domains, independent backups, and each service's
data restorable without touching the other. Two machines and two volumes.

**B. One app, two databases.** One machine, one volume, one backup. The two services still own
their schemas — nothing reaches across — but they share an availability and restore unit.

## Decision

**B.**

At this size the isolation A buys is mostly theoretical: both databases serve the same product,
and cv-maker is not useful without its identity service anyway, so an outage of one is an
outage of both regardless of which machine it lands on. What A would genuinely give is
independent *restore* — recovering identity without rolling back CVs, or vice versa — and that
matters at a scale this is not at.

## Consequences

- **`docker/initdb/` creates the second database and a role that owns it**, on first boot
  against an empty volume only. Adding a third database to a running deployment is a manual
  `CREATE DATABASE`, not an edit to that script.
- **Each service still gets its own role and its own credential.** Sharing a machine is not
  sharing an account: `AUTHSERVICE_DB_PASSWORD` exists so the two do not use one login. It
  falls back to the server password for the quick start, which is a demo convenience and
  should not survive into anything real. Separate credentials are not separate data,
  though: CvMaker.Api logs in as `cvmaker`, the server's superuser, which can read the
  `authservice` database too. Giving the API a non-superuser role is what would close that.
- **A restore is all-or-nothing.** Rolling back to last night's snapshot to recover a deleted
  profile also rolls back every account created since. Worth remembering before running one.
- **Splitting later is a migration, not a config change** — a dump, a restore into a new app,
  and a connection-string change per service. Reversible, but not free, which is the honest
  cost of choosing B now.
