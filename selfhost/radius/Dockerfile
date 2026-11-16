# root/selfhost/radius/Dockerfile
# Debian stable FreeRADIUS 3.2 + PostgreSQL adapter.
FROM debian:trixie-slim
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    freeradius freeradius-postgresql freeradius-utils postgresql-client ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY selfhost/radius/entrypoint.sh /usr/local/bin/netforge-radius
RUN chmod 0755 /usr/local/bin/netforge-radius
EXPOSE 1812/udp 1813/udp
ENTRYPOINT ["/usr/local/bin/netforge-radius"]
