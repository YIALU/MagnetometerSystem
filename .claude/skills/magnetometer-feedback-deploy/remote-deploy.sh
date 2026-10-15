#!/usr/bin/env bash
# 在反馈服务器上以 root 运行：备份数据库 → 校验包 → install.sh → 验证，失败则指回上一版本。
# 由 Deploy-FeedbackServer.ps1 上传到临时目录后调用，不单独提交到服务器其他位置。
# 用法：remote-deploy.sh            部署
#       remote-deploy.sh rollback   公网验证失败时，按本目录保存的状态指回上一版本
set -euo pipefail
dir=$(cd -- "$(dirname -- "$0")" && pwd)
ts=$(date -u +%Y%m%dT%H%M%SZ)
db=/var/lib/magnetometer-feedback/feedback.db
units="magnetometer-feedback.service magnetometer-feedback-proxy.service"

# 最多等 20 秒：两个服务都 active 且本机 /health 正常才算就绪。
wait_healthy() {
    for _ in $(seq 1 20); do
        # systemctl is-active 列多个单元时只要有一个 active 就返回成功，所以逐个检查。
        if systemctl is-active --quiet magnetometer-feedback \
            && systemctl is-active --quiet magnetometer-feedback-proxy \
            && curl -fs -m 3 http://127.0.0.1:5188/health >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    return 1
}

# 回滚退出码（与 set -e 失败时的 1 区分开）：10 已指回上一版本且通过本机健康检查；
# 11 无上一版本可指回；12 回滚步骤失败或服务未恢复，需要人工处理。
rollback() {
    echo "[deploy] $1" >&2
    journalctl -u magnetometer-feedback -u magnetometer-feedback-proxy -n 30 --no-pager >&2 || true
    if [ -n "$previous" ] && [ -d "$previous" ]; then
        # 每一步都尽量执行完，但任何一步失败都记下来，最终不报告为回滚成功。
        restore_failed=0
        for u in $units; do
            if [ -f "$dir/units-previous/$u" ]; then
                install -m 0644 "$dir/units-previous/$u" /etc/systemd/system/ || restore_failed=1
            fi
        done
        systemctl daemon-reload || restore_failed=1
        ln -sfn "$previous" /opt/magnetometer-feedback/current || restore_failed=1
        systemctl restart magnetometer-feedback.service magnetometer-feedback-proxy.service || restore_failed=1
        if [ "$restore_failed" = 0 ] && wait_healthy; then
            echo "[deploy] 已指回上一版本并通过本机健康检查: $previous" >&2
            exit 10
        fi
        echo "[deploy] 回滚到 $previous 未完成（恢复 unit、daemon-reload、切换或重启失败，或服务未恢复），需要人工处理" >&2
        journalctl -u magnetometer-feedback -u magnetometer-feedback-proxy -n 30 --no-pager >&2 || true
        exit 12
    fi
    echo "[deploy] 没有可指回的上一版本" >&2
    exit 11
}

if [ "${1:-}" = rollback ]; then
    [ -f "$dir/previous-dir" ] || { echo "[deploy] 缺少 $dir/previous-dir，无法回滚" >&2; exit 2; }
    previous=$(cat "$dir/previous-dir")
    rollback "公网验证失败，回滚"
fi

previous=$(readlink -f /opt/magnetometer-feedback/current 2>/dev/null || true)
echo "[deploy] 当前版本目录: ${previous:-（无）}"
# 公网验证在本机完成后才删除本目录；验证失败时 rollback 模式从这里读取上一版本。
printf '%s' "$previous" > "$dir/previous-dir"

# install.sh 会覆盖 systemd unit；先保存旧 unit，回滚时一并恢复。
mkdir -p "$dir/units-previous"
for u in $units; do
    if [ -f "/etc/systemd/system/$u" ]; then cp -p "/etc/systemd/system/$u" "$dir/units-previous/"; fi
done
# 每个版本目录下的 systemd/ 保存该版本配套的 unit，供日后手动回滚时一并恢复。
# 上一版本目录若是在本脚本之前部署的（或上次补写中断），用当前（即上一版本）的 unit 逐个补齐。
if [ -n "$previous" ] && [ -d "$previous" ]; then
    install -d -m 0755 "$previous/systemd"
    for u in $units; do
        if [ ! -f "$previous/systemd/$u" ] && [ -f "$dir/units-previous/$u" ]; then
            install -m 0644 "$dir/units-previous/$u" "$previous/systemd/"
        fi
    done
fi

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

cd "$dir"
sha256sum -c bundle.sha256
# install.sh 失败时（例如切换 current 后重启服务失败）也要回滚，不能被 set -e 直接退出。
bash "$dir/install.sh" "$dir/server-bundle.tar.gz" || rollback "install.sh 执行失败"
# 版本目录缺少配套 unit 就无法按 SKILL.md 手动回滚，所以保存失败按部署失败处理。
# 此时 current 已切到新版本，必须走 rollback 而不是让 set -e 直接退出。
current=$(readlink -f /opt/magnetometer-feedback/current)
install -d -m 0755 "$current/systemd" || rollback "保存新版本 unit 失败"
for u in $units; do
    install -m 0644 "/etc/systemd/system/$u" "$current/systemd/" || rollback "保存新版本 unit 失败: $u"
done

wait_healthy || rollback "新版本未通过本机健康检查"

systemctl is-active magnetometer-feedback magnetometer-feedback-proxy
echo "[deploy] 新版本目录: $(readlink -f /opt/magnetometer-feedback/current)"
echo "[deploy] 上一版本目录保留: ${previous:-（无）}"
