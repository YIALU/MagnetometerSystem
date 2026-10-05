#!/usr/bin/env bash
set -euo pipefail
ip=${1:?public IPv4 required}
if [[ ! "$ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then echo 'Invalid IP' >&2; exit 1; fi
certbot=/opt/magnetometer-feedback/certbot/bin/certbot
if [ ! -x "$certbot" ]; then
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq python3-venv
    python3 -m venv /opt/magnetometer-feedback/certbot
    /opt/magnetometer-feedback/certbot/bin/pip install --quiet 'certbot>=5.4,<6'
fi
"$certbot" certonly --non-interactive --agree-tos --register-unsafely-without-email \
    --preferred-profile shortlived --ip-address "$ip" --webroot \
    --webroot-path /var/lib/magnetometer-feedback/acme --cert-name magnetometer-feedback
cat > /etc/magnetometer-feedback/renew-hook.sh <<'HOOK'
#!/usr/bin/env bash
set -euo pipefail
install -m 0644 -o root -g magnetometer-feedback "$RENEWED_LINEAGE/fullchain.pem" /etc/magnetometer-feedback/tls/fullchain.pem
install -m 0640 -o root -g magnetometer-feedback "$RENEWED_LINEAGE/privkey.pem" /etc/magnetometer-feedback/tls/privkey.pem
/usr/bin/caddy validate --config /etc/magnetometer-feedback/Caddyfile --adapter caddyfile
systemctl restart magnetometer-feedback-proxy.service
HOOK
chmod 0700 /etc/magnetometer-feedback/renew-hook.sh
install -m 0644 -o root -g magnetometer-feedback /etc/letsencrypt/live/magnetometer-feedback/fullchain.pem /etc/magnetometer-feedback/tls/fullchain.pem
install -m 0640 -o root -g magnetometer-feedback /etc/letsencrypt/live/magnetometer-feedback/privkey.pem /etc/magnetometer-feedback/tls/privkey.pem
cat > /etc/magnetometer-feedback/Caddyfile <<CADDY
{
    admin off
    auto_https off
    default_sni $ip
}
http://:80 {
    root * /var/lib/magnetometer-feedback/acme
    file_server
}
https://$ip {
    tls /etc/magnetometer-feedback/tls/fullchain.pem /etc/magnetometer-feedback/tls/privkey.pem
    reverse_proxy 127.0.0.1:5188
}
CADDY
chown root:magnetometer-feedback /etc/magnetometer-feedback/Caddyfile
chmod 0640 /etc/magnetometer-feedback/Caddyfile
/usr/bin/caddy validate --config /etc/magnetometer-feedback/Caddyfile --adapter caddyfile
cat > /etc/systemd/system/magnetometer-feedback-cert-renew.service <<'SERVICE'
[Unit]
Description=Renew magnetometer feedback IP certificate
[Service]
Type=oneshot
ExecStart=/opt/magnetometer-feedback/certbot/bin/certbot renew --cert-name magnetometer-feedback --quiet --deploy-hook /etc/magnetometer-feedback/renew-hook.sh
SERVICE
cat > /etc/systemd/system/magnetometer-feedback-cert-renew.timer <<'TIMER'
[Unit]
Description=Check feedback IP certificate renewal twice daily
[Timer]
OnCalendar=*-*-* 00,12:00:00
RandomizedDelaySec=1800
Persistent=true
[Install]
WantedBy=timers.target
TIMER
systemctl daemon-reload
systemctl enable --now magnetometer-feedback-cert-renew.timer
systemctl restart magnetometer-feedback-proxy.service
