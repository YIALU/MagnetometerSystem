#!/usr/bin/env bash
# 在反馈服务器上以 root 运行：备份数据库 → 校验包 → install.sh → 验证，失败则指回上一版本。
# 由 Deploy-FeedbackServer.ps1 上传到临时目录后调用，不单独提交到服务器其他位置。
set -euo pipefail
dir=$(cd -- "$(dirname -- "$0")" && pwd)
ts=$(date -u +%Y%m%dT%H%M%SZ)
db=/var/lib/magnetometer-feedback/feedback.db

previous=$(readlink -f /opt/magnetometer-feedback/current 2>/dev/null || true)
echo "[deploy] 当前版本目录: ${previous:-（无）}"

if [ -f "$db" ]; then
    backup=/var/lib/magnetometer-feedback/feedback-backup-$ts.db
    python3 - "$db" "$backup" <<'PY'
import os, sqlite3, sys
os.umask(0o077)
source = sqlite3.connect(sys.argv[1])
target = sqlite3.connect(sys.argv[2])
source.backup(target)
target.close()
source.close()
PY
    echo "[deploy] 数据库已备份: $backup"
else
    echo "[deploy] 未找到 $db，跳过备份（首次部署）"
fi

rollback() {
    echo "[deploy] $1" >&2
    journalctl -u magnetometer-feedback -u magnetometer-feedback-proxy -n 30 --no-pager >&2 || true
    if [ -n "$previous" ] && [ -d "$previous" ]; then
        ln -sfn "$previous" /opt/magnetometer-feedback/current
        systemctl restart magnetometer-feedback.service magnetometer-feedback-proxy.service || true
        echo "[deploy] 已指回上一版本: $previous" >&2
    fi
    exit 1
}

cd "$dir"
sha256sum -c bundle.sha256
# install.sh 失败时（例如切换 current 后重启服务失败）也要回滚，不能被 set -e 直接退出。
bash "$dir/install.sh" "$dir/server-bundle.tar.gz" || rollback "install.sh 执行失败"

ok=0
for _ in $(seq 1 20); do
    # systemctl is-active 列多个单元时只要有一个 active 就返回成功，所以逐个检查。
    if systemctl is-active --quiet magnetometer-feedback \
        && systemctl is-active --quiet magnetometer-feedback-proxy \
        && curl -fs -m 3 http://127.0.0.1:5188/health >/dev/null 2>&1; then ok=1; break; fi
    sleep 1
done

[ "$ok" = 1 ] || rollback "新版本未通过本机健康检查"

systemctl is-active magnetometer-feedback magnetometer-feedback-proxy
echo "[deploy] 新版本目录: $(readlink -f /opt/magnetometer-feedback/current)"
echo "[deploy] 上一版本目录保留: ${previous:-（无）}"
