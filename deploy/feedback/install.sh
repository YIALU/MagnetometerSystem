#!/usr/bin/env bash
set -euo pipefail
bundle=${1:?publish archive path required}
deployment_dir=$(cd -- "$(dirname -- "$0")" && pwd)
if ! id magnetometer-feedback >/dev/null 2>&1; then
    useradd --system --home-dir /var/lib/magnetometer-feedback --shell /usr/sbin/nologin magnetometer-feedback
fi
install -d -m 0700 -o magnetometer-feedback -g magnetometer-feedback /var/lib/magnetometer-feedback
install -d -m 0750 -o root -g magnetometer-feedback /etc/magnetometer-feedback /etc/magnetometer-feedback/tls
install -d -m 0755 /opt/magnetometer-feedback/releases
release=/opt/magnetometer-feedback/releases/$(date -u +%Y%m%dT%H%M%SZ)
install -d -m 0755 "$release"
tar -xzf "$bundle" -C "$release"
chmod 0755 "$release/MagnetometerSystem.Feedback.Server"
ln -sfn "$release" /opt/magnetometer-feedback/current
install -m 0644 "$deployment_dir/magnetometer-feedback.service" /etc/systemd/system/
install -m 0644 "$deployment_dir/magnetometer-feedback-proxy.service" /etc/systemd/system/
if [ ! -f /etc/magnetometer-feedback/Caddyfile ]; then
    install -d -m 0755 -o magnetometer-feedback -g magnetometer-feedback /var/lib/magnetometer-feedback/acme/.well-known/acme-challenge
    cat > /etc/magnetometer-feedback/Caddyfile <<'CADDY'
{
    admin off
    auto_https off
}
http://:80 {
    root * /var/lib/magnetometer-feedback/acme
    file_server
}
CADDY
    chown root:magnetometer-feedback /etc/magnetometer-feedback/Caddyfile
    chmod 0640 /etc/magnetometer-feedback/Caddyfile
fi
systemctl daemon-reload
systemctl enable magnetometer-feedback.service magnetometer-feedback-proxy.service
systemctl restart magnetometer-feedback.service
systemctl restart magnetometer-feedback-proxy.service
