#!/bin/sh
# The agent container runs two processes: nginx (serves OpenSpeedTest + the
# throughput-critical transfer legs on port 3000) and the .NET agent (the tunnel
# plus the loopback results relay on 3001). nginx runs in the background; the
# agent is the main process, so `docker stop` (SIGTERM to it) tears the container
# down and nginx goes with it.
set -e

# Generate a persisted self-signed cert for the LAN speed test's TLS listener if it
# doesn't exist yet. TLS makes the OpenSpeedTest page a secure context so the browser
# Geolocation API works (GPS-tagged results) without a per-site reverse proxy. Persisted
# under /data so a client's one-time browser trust exception survives restarts; SANs
# cover the host's LAN IPs + hostname so the cert matches the address clients use.
# Non-fatal: if generation fails, nginx just won't serve (the agent still runs).
CERTDIR=/data/speedtest-tls
if [ ! -f "$CERTDIR/cert.pem" ]; then
    mkdir -p "$CERTDIR"
    IPS=$(hostname -I 2>/dev/null || echo)
    SAN="DNS:$(hostname),DNS:localhost"
    for ip in $IPS; do SAN="$SAN,IP:$ip"; done
    CN=$(echo "$IPS" | awk '{print $1}')
    openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
        -keyout "$CERTDIR/key.pem" -out "$CERTDIR/cert.pem" \
        -subj "/CN=${CN:-agent}" -addext "subjectAltName=$SAN" >/dev/null 2>&1 \
        && chmod 600 "$CERTDIR/key.pem" \
        || echo "entrypoint: self-signed cert generation failed; LAN speed test TLS unavailable"
fi

nginx -c /etc/nginx/netopt-speedtest.conf -g 'daemon off;' &
exec /app/NetworkOptimizer.Agent
