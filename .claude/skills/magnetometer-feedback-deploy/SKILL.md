---
name: magnetometer-feedback-deploy
description: "仅用于 YIALU/MagnetometerSystem 的匿名反馈服务重新部署：从 GitHub master 构建 linux-x64 自包含反馈服务，上传到反馈服务器（默认 59.110.232.32），备份反馈数据库后运行 deploy/feedback/install.sh，并经可信 HTTPS 验证。不用于首次 HTTPS 配置、GitHub 令牌配置或桌面程序发布。"
---

# 磁力仪反馈服务重新部署

依据 `docs/feedback-deployment.md`「重新部署」。一键脚本：`Deploy-FeedbackServer.ps1`（同目录），服务器端步骤在 `remote-deploy.sh`。

## 前提

- Windows、.NET 8 SDK、Windows 自带 OpenSSH（ssh/scp）和 curl.exe。
- SSH 私钥（`.pem`）在仓库根或其上级，已被 `.gitignore` 忽略。只把路径传给脚本；不读取、打印、复制到别处或提交。
- **代理**：本机若开着 FlClash/Clash 的 TUN（虚拟网卡），尤其是全局模式，22 端口会被接管，表现为「Connection timed out during banner exchange」。部署期间临时关闭 TUN（系统代理可保留），完成后再打开。全局模式下加 DIRECT 规则不生效。
- 首次连接会提示服务器主机指纹：与服务器控制台或云厂商记录核对后再输入 `yes`，并把指纹记在汇报里。

## 运行

在仓库根（用户工作区未提交改动不受影响，构建在 `.codex_tmp/feedback-deploy/` 下的独立 worktree 中进行）：

```powershell
powershell -ExecutionPolicy Bypass -File .claude\skills\magnetometer-feedback-deploy\Deploy-FeedbackServer.ps1 -KeyPath '<pem 绝对路径>'
```

可选参数：`-Server`、`-User`（默认 root）、`-Port`（默认 22）、`-Ref`（默认 `origin/master`）。服务包总是从 `-Ref` 现场构建，与安装脚本和 unit 出自同一提交。

脚本依次：

1. 检查 SSH 握手，失败时提示关闭 TUN 或检查安全组。
2. 生成仅当前用户可读的私钥临时副本（结束时删除）。
3. `git fetch origin`，在独立 worktree 检出 `-Ref`，`dotnet publish -r linux-x64 --self-contained` 并打包，计算 SHA256。
4. 部署前先发一个约 600 KB、反馈编号为全零 GUID 的请求作基线（旧版 200 KB 上限返回 413）。
5. 上传包、`install.sh`、两个 unit、`remote-deploy.sh` 到服务器 `/tmp` 下的 0700 临时目录。
6. 服务器上：记录当前版本目录 → 用 SQLite backup API 备份为 `/var/lib/magnetometer-feedback/feedback-backup-<UTC时间>.db` → 校验包哈希 → 运行 `install.sh` → 等待两个服务 active 且 `127.0.0.1:5188/health` 正常；失败时恢复旧 unit、把 `/opt/magnetometer-feedback/current` 指回上一版本并重启。
7. 本机经 HTTPS 检查 `https://<Server>/health`（不关闭证书校验），再发同样的大请求，预期 **400**（参数校验拒绝，不写库、不建 Issue）。任一项失败时运行 `remote-deploy.sh rollback` 按同样方式指回上一版本；两项都通过后才删除服务器临时目录。回滚后同样等待两个服务 active 且本机 `/health` 正常；仍未恢复时脚本明确提示需要立即登录服务器人工处理。

## 边界

- 不运行 `configure-https.sh`；不改原有 Caddy 服务、`/etc/magnetometer-feedback/Caddyfile`、GitHub 令牌文件和反馈数据库内容。
- 不提交真实反馈来验证（会在公开仓库建 Issue）。大请求测试计入每 IP 每小时 10 次的提交限流。
- 旧版本目录保留在 `/opt/magnetometer-feedback/releases/`；手动回滚：

```bash
ln -sfn /opt/magnetometer-feedback/releases/<旧版本> /opt/magnetometer-feedback/current
systemctl restart magnetometer-feedback magnetometer-feedback-proxy
```

- 备份文件、私钥、令牌和数据库内容不进入 Git、聊天或日志。

## 汇报

写明：部署的提交 SHA、包 SHA256、主机指纹、备份文件名、新旧版本目录、两个服务状态、`/health` 输出、大请求部署前后返回码。没做的写「未做」。部署反馈服务不等于桌面程序发布，也不代表真实设备/串口验证。
