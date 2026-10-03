# cv-maker's Fly Postgres app — the same image and init scripts as
# docker-compose.yml's `db` service, so dev and Fly stay in sync: one server,
# two logical databases (`cvmaker`, `authservice`), created by
# docker/initdb/ on first boot against an empty volume.
#
# The second database is the external konradcinkusz/authservice instance's.
# Each service still owns its own schema; they share a machine, and therefore a
# backup and restore unit, which is the deliberate trade for running one Fly
# machine rather than two.

FROM postgres:17-alpine
COPY docker/initdb/ /docker-entrypoint-initdb.d/
