# 匿名反馈服务维护

用户窗口只包含使用场景、问题或需求描述、选填姓名和联系方式，以及「附带最近 3 天的程序日志」选项（默认勾选，可预览）。入口位于主窗口状态栏「反馈与建议」和「关于」。非模态窗口允许继续操作主窗口；草稿独立保存在用户 AppData 的 `MagnetometerSystem/feedback/draft.json`，不占采集数据库写锁。

场景、描述和软件版本用于公开 GitHub Issue，姓名、联系方式和附带日志只留在服务器私有数据库；Issue 里只注明“已附带程序日志”和反馈编号。日志由客户端从最新往前取，最多 512 KB 文本，去掉 Windows 用户名（含用户目录路径）和计算机名，gzip 后以 Base64 放在请求的 `Logs` 字段；局域网 IP 等连接参数保留。公开回执仅包含随机编号、同步状态和已创建的 Issue 链接。

## 当前部署

匿名接收服务及独立代理已经部署在用户提供的公网服务器。公开接口为 `https://59.110.232.32/api/feedback`，健康检查为 `https://59.110.232.32/health`。该公开地址随软件构建发布，覆盖地址可通过 `build.ps1 -FeedbackEndpoint <HTTPS URL>` 或运行时环境变量 `MAGNETOMETER_FEEDBACK_ENDPOINT` 设置。SSH 私钥和 GitHub 写入凭据不会进入程序或发布包。

| 项目 | 路径或名称 |
| --- | --- |
| 后台服务 | `magnetometer-feedback.service`，只监听 `127.0.0.1:5188` |
| 独立反向代理 | `magnetometer-feedback-proxy.service`，使用 80/443；原有 Caddy 配置和服务保留 |
| 程序 | `/opt/magnetometer-feedback/current`，链接到版本目录 |
| 私有数据 | `/var/lib/magnetometer-feedback/feedback.db`，目录仅服务用户及 root 可访问 |
| 代理配置 | `/etc/magnetometer-feedback/Caddyfile` |
| GitHub 凭据 | `/etc/magnetometer-feedback/github-token`，已由维护者配置，root:magnetometer-feedback / 0640 |
| 证书续期 | `magnetometer-feedback-cert-renew.timer`，每天两次检查，续期成功后重启独立代理加载新证书 |

IP 地址证书来自 Let’s Encrypt，约六天有效。使用 Certbot 自动续期，保持公网 80 验证路径可达；禁止通过关闭证书校验解决连接问题。Caddy `default_sni` 指向公开 IP，使 Windows 不发送 SNI 的 IP 连接也能选择正确证书。[IP 证书说明](https://letsencrypt.org/2026/03/11/shorter-certs-certbot)、[Caddy default_sni](https://caddyserver.com/docs/caddyfile/options#default-sni)。

## 配置 GitHub 自动建单

无需在服务器登录 GitHub 网页。由维护者创建仅能写本仓库 Issue 的细粒度令牌，保存在服务器文件中。2026-10-05 已完成配置并验证真实建单。未配置或授权不可用时，用户提交仍获得服务端持久回执，反馈保持 pending；配置后后台自动继续同步。

1. 在 GitHub 的 [细粒度令牌设置](https://github.com/settings/personal-access-tokens/new)创建令牌，设置名称和适当到期日。
2. Resource owner 选择 `YIALU`，Repository access 选择 Only select repositories，并仅选择 `MagnetometerSystem`。
3. Repository permissions 仅增加 **Issues: Read and write**；Metadata 的默认只读权限保留。不需要 Contents、Actions、Administration 或全账户权限。
4. 令牌只在隐藏的终端提示中粘贴，不发在聊天、Issue、代码或日志里。Windows 可运行仓库的 [配置脚本](../tools/Set-FeedbackGitHubToken.ps1)：

```powershell
.\tools\Set-FeedbackGitHubToken.ps1 -KeyPath '<本机 SSH pem 密钥绝对路径>'
```

脚本通过交互式 SSH 隐藏输入，在服务器原子替换令牌文件，不在本机保存令牌。首次 SSH 连接需要核对服务器主机指纹；只有私钥会产生仅当前用户可读的临时副本，用后删除，原私钥不改动。脚本已进行语法检查，真正写入令牌需要维护者交互操作。已经登录服务器时，也可直接运行以下命令：

```bash
/etc/magnetometer-feedback/configure-github-token.sh
```

该配置工具已安装在当前服务器，需要 root 运行。输入通过隐藏提示完成，令牌不作为命令字面量进入 shell 历史；临时文件写入完成后原子替换，取消或空输入保持旧配置。重新部署到其他服务器时，可使用上面的 Windows 配置脚本。后台每 15 秒读取一次文件，无需重启采集程序或服务器原有服务。服务会确认/创建 `feedback` 和 `needs-triage` 标签；新 Issue 均待维护者审核。令牌到期前由维护者轮换，应用无法自行延长令牌有效期。

维护者按是否采纳、修复或重复决定后续工作。创建成功才将反馈状态记为 synced；401/403/限流保持 pending；POST 超时或结果未知记为 unknown，先核对已有 Issue，无法确认时留待人工处理，不自动重复创建。

令牌配置前的部署测试记录已清除。维护者授权后使用单条明确标注的反馈完成真实建单，生成 [联调 Issue #5](https://github.com/YIALU/MagnetometerSystem/issues/5)，验证后已关闭；不要把其他部署测试数据批量同步到正式仓库。[细粒度令牌官方说明](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token)

## 日常查看

```bash
systemctl status magnetometer-feedback magnetometer-feedback-proxy --no-pager
systemctl list-timers magnetometer-feedback-cert-renew.timer
journalctl -u magnetometer-feedback -n 50 --no-pager
```

通过 SSH 查看姓名和联系方式：

```bash
sudo python3 - <<'PY'
import sqlite3, json
c = sqlite3.connect('file:/var/lib/magnetometer-feedback/feedback.db?mode=ro', uri=True)
for ident, state, payload, url in c.execute('SELECT id,state,payload,issue_url FROM feedback ORDER BY created_utc DESC LIMIT 30'):
    p = json.loads(payload)
    print(ident, state, url or '')
    print('场景:', p['Scenario'])
    print('描述:', p['Description'])
    print('姓名:', p.get('Name') or '')
    print('联系方式:', p.get('Contact') or '')
PY
```

按反馈编号导出附带日志（Issue 中注明了编号）。先用 `install` 建好仅自己可读写（0600）的空文件，已存在的文件也会被清空并收紧权限：

```bash
install -m 600 /dev/null feedback-logs.txt
sudo python3 - '<反馈编号>' > feedback-logs.txt <<'PY'
import sqlite3, json, sys, base64, gzip
c = sqlite3.connect('file:/var/lib/magnetometer-feedback/feedback.db?mode=ro', uri=True)
row = c.execute('SELECT payload FROM feedback WHERE id=?', (sys.argv[1],)).fetchone()
logs = json.loads(row[0]).get('Logs') if row else None
sys.stdout.write(gzip.decompress(base64.b64decode(logs)).decode('utf-8') if logs else '（未附带日志）\n')
PY
```

该终端结果和导出的日志包含用户个人信息或现场细节，不能复制到公开 Issue 或共享日志。备份使用 SQLite backup API，不只复制正在写入的单个 `.db` 文件：

```bash
sudo python3 - <<'PY'
import sqlite3, os
os.umask(0o077)
source = sqlite3.connect('/var/lib/magnetometer-feedback/feedback.db')
target = sqlite3.connect('/var/lib/magnetometer-feedback/feedback-backup.db')
source.backup(target)
target.close()
source.close()
PY
```

备份、令牌、私钥和反馈数据库不进入 Git。删除反馈或解决 unknown 记录前，按编号核对原 Issue，保留维护者处理决定。

## 重新部署

在 Windows 仓库根发布独立 Linux 自包含服务，然后上传到服务器临时目录，运行 [安装脚本](../deploy/feedback/install.sh)：

```powershell
dotnet publish src/MagnetometerSystem.Feedback.Server/MagnetometerSystem.Feedback.Server.csproj -c Release -r linux-x64 --self-contained true -o .codex_tmp/feedback/server-publish
tar -czf .codex_tmp/feedback/server-bundle.tar.gz -C .codex_tmp/feedback/server-publish .
```

请求体上限为 1 MB（含附带日志）。**带日志的客户端发布前必须先部署新版服务**：旧服务的上限是 200 KB，带日志的请求会被拒收（用户看到“反馈服务暂时无法接收”）。

服务器部署文件包括两个 systemd unit、安装脚本及首次 HTTPS 配置脚本。安装脚本更新新服务的 current 链接并重启它们，保留反馈数据库和现有代理配置；不要对服务器已有服务目录运行该脚本。首次 HTTPS 配置用 `configure-https.sh <public IPv4>`。服务单实例运行；不能启动第二个进程共享同一反馈数据库。

## 验证边界

测试涵盖匿名实际 HTTP 接口、桌面客户端调用链、真实临时 SQLite、丢失响应重试、服务重启、选填信息隐私、GitHub 未授权/网络故障和未知结果核对，以及 WPF 填写与草稿恢复。初次公网部署测试记录在授权前清除；授权后使用一个专用编号验证真实建单，测试单已关闭，回执保留。

实际 GitHub 建单已验证：桌面 ViewModel 经可信 HTTPS 提交，服务器 SQLite 保存后生成 Issue 并返回 synced；场景和描述一致，姓名和联系方式未公开。相同编号重试及反馈后台重启后重试均只有一个 Issue。后续是否采纳由维护者决定，本次不代表实际串口采集期间的端到端验收或新版发布。
