#Requires -Version 5.1
<#
重新部署匿名反馈服务（docs/feedback-deployment.md「重新部署」）。
构建指定提交的 linux-x64 自包含服务 → 上传 → 服务器备份数据库并运行 install.sh → 本机经 HTTPS 验证。
私钥只以路径传给 ssh/scp；使用仅当前用户可读的临时副本，结束时删除，原文件不改动。
不运行 configure-https.sh，不改原有 Caddy 服务、反馈数据库内容和 GitHub 令牌文件。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$KeyPath,
    [string]$Server = '59.110.232.32',
    [string]$User = 'root',
    [int]$Port = 22,
    # 服务包、安装脚本和 unit 都从这个提交现场构建/取出，不接受外部服务包，避免混装不同版本。
    [string]$Ref = 'origin/master'
)
$ErrorActionPreference = 'Stop'

function Invoke-Native([scriptblock]$Command) {
    # PS 5.1 下原生程序写 stderr 会在 Stop 模式抛错；这里只按退出码判断。
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $saved }
}

function Test-OversizeRequest([string]$Label) {
    # 全零反馈编号会先被参数校验以 400 拒绝：不写库、不建 Issue。旧版服务（上限 200 KB）返回 413。
    $code = & curl.exe -sS -o NUL -w '%{http_code}' -m 60 -H 'Content-Type: application/json' `
        --data-binary "@$oversize" "https://$Server/api/feedback"
    Write-Host "[$Label] 约 600 KB 请求体返回 HTTP $code"
    return $code
}

function Test-PublicHealth {
    # 经公网 HTTPS（证书校验开启）访问 /health；成功返回响应内容，失败返回 $null。
    $body = Invoke-Native { curl.exe -sS --fail -m 30 "https://$Server/health" }
    if ($LASTEXITCODE) { return $null }
    return ($body | Out-String).Trim()
}

function Get-RollbackSummary([int]$Code) {
    # remote-deploy.sh 回滚退出码：10 已指回并通过本机检查；11 无上一版本；12 已指回但服务未恢复。
    # 其他非零值（例如备份或校验失败）表示没有走回滚，current 未被改动或状态未知。
    # 本机检查只覆盖后端和代理进程，所以 10 还要经公网 HTTPS 再确认一次。
    if ($Code -eq 10) {
        if (Test-PublicHealth) { return '已指回上一版本，并经公网 HTTPS /health 确认可用' }
        return '已指回上一版本，但公网 HTTPS /health 仍失败，请立即登录服务器处理'
    }
    if ($Code -eq 11) { return '服务器上没有可指回的上一版本，请立即登录服务器处理' }
    if ($Code -eq 12) { return '已指回上一版本但服务未恢复，请立即登录服务器处理' }
    return "未确认是否已回滚（退出码 $Code），请登录服务器检查 /opt/magnetometer-feedback/current 和两个服务"
}

$repo = (git rev-parse --show-toplevel).Trim()
$work = Join-Path $repo ('.codex_tmp/feedback-deploy/' + (Get-Date -Format 'yyyyMMddHHmmss'))
$keyDir = Join-Path $work 'key'
$upload = Join-Path $work 'upload'
$tree = Join-Path $work 'tree'
[IO.Directory]::CreateDirectory($upload) | Out-Null
$succeeded = $false

# 1. 连通性：TCP 建立但没有 SSH banner，通常是本机代理 TUN（例如 FlClash 全局模式）接管了 22 端口。
Write-Host '[1/6] 检查 SSH 连通性'
$probe = Invoke-Native { ssh -o ConnectTimeout=20 -o BatchMode=yes -o PreferredAuthentications=none `
        -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -p $Port "$User@$Server" exit } | Out-String
if ($probe -match 'banner exchange|timed out|Connection refused') {
    throw "SSH 无法完成握手：$($probe.Trim())`n请先临时关闭代理软件的 TUN/虚拟网卡（系统代理可保留），或确认安全组放行本机出口 IP。"
}

# 2. 私钥临时副本：仅当前用户可读。
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
[IO.Directory]::CreateDirectory($keyDir) | Out-Null
$acl = Get-Acl -LiteralPath $keyDir
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $keyDir -AclObject $acl
$key = Join-Path $keyDir 'ssh-key'
$sshOptions = @('-i', $key, '-o', 'IdentitiesOnly=yes', '-o', 'StrictHostKeyChecking=ask')

try {
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $KeyPath).Path -Destination $key
    $fileAcl = Get-Acl -LiteralPath $key
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $key -AclObject $fileAcl

    # 3. 检出 $Ref 并构建。
    Write-Host "[2/6] 检出 $Ref 并准备服务包"
    git fetch origin
    if ($LASTEXITCODE) { throw 'git fetch 失败' }
    git worktree add --detach $tree $Ref
    if ($LASTEXITCODE) { throw 'git worktree add 失败' }
    $commit = (git -C $tree rev-parse HEAD).Trim()
    Write-Host "      提交 $commit"
    dotnet publish "$tree/src/MagnetometerSystem.Feedback.Server/MagnetometerSystem.Feedback.Server.csproj" `
        -c Release -r linux-x64 --self-contained true -o "$work/publish"
    if ($LASTEXITCODE) { throw 'dotnet publish 失败' }
    tar -czf "$upload/server-bundle.tar.gz" -C "$work/publish" .
    if ($LASTEXITCODE) { throw '打包失败' }
    foreach ($name in 'install.sh', 'magnetometer-feedback.service', 'magnetometer-feedback-proxy.service') {
        Copy-Item -LiteralPath "$tree/deploy/feedback/$name" -Destination $upload
    }
    Copy-Item -LiteralPath "$PSScriptRoot/remote-deploy.sh" -Destination $upload
    $hash = (Get-FileHash "$upload/server-bundle.tar.gz" -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$upload/bundle.sha256", "$hash  server-bundle.tar.gz`n")
    Write-Host "      server-bundle.tar.gz SHA256 $hash"

    $oversize = Join-Path $work 'oversize.json'
    [IO.File]::WriteAllText($oversize, '{"feedbackId":"00000000-0000-0000-0000-000000000000","scenario":"deploy-check","description":"deploy-check","logs":"' + ('A' * 600000) + '"}')
    $before = Test-OversizeRequest '部署前'

    # 4. 上传。首次连接会提示主机指纹：与服务器控制台/云厂商记录核对后再输入 yes。
    Write-Host '[3/6] 上传到服务器临时目录（首次连接请核对主机指纹）'
    $remoteDir = "/tmp/magnetometer-feedback-deploy-$(Get-Date -Format 'yyyyMMddHHmmss')"
    & ssh @sshOptions -p $Port "$User@$Server" "install -d -m 0700 $remoteDir"
    if ($LASTEXITCODE) { throw '创建远程目录失败' }
    $files = Get-ChildItem -LiteralPath $upload | ForEach-Object { $_.FullName }
    & scp @sshOptions -P $Port @files "${User}@${Server}:$remoteDir/"
    if ($LASTEXITCODE) { throw '上传失败' }

    # 5. 远程备份、安装、本机健康检查（失败自动指回上一版本）。
    Write-Host '[4/6] 备份数据库并安装'
    & ssh @sshOptions -p $Port "$User@$Server" "sed -i 's/\r$//' $remoteDir/*.sh $remoteDir/*.service && bash $remoteDir/remote-deploy.sh"
    if ($LASTEXITCODE -eq 255) {
        # 255 是 ssh 自身的连接错误，拿不到远程脚本的退出码；install.sh 可能已切换 current。
        # 重新连接，按上传目录里保存的状态回滚，避免未经公网验证的版本继续在线。
        Write-Host '[回滚] SSH 连接中断，重新连接并回滚'
        & ssh @sshOptions -p $Port "$User@$Server" "bash $remoteDir/remote-deploy.sh rollback"
        $summary = Get-RollbackSummary $LASTEXITCODE
        throw "远程安装期间 SSH 连接中断：$summary；上传目录保留在 $remoteDir 供排查"
    }
    if ($LASTEXITCODE) {
        $summary = Get-RollbackSummary $LASTEXITCODE
        throw "远程安装失败：$summary；上传目录保留在 $remoteDir 供排查"
    }

    # 6. 公网验证：证书校验保持开启。任一项失败都在服务器上指回上一版本；
    #    回滚所需状态在 $remoteDir 中，验证通过后才删除。
    try {
        Write-Host '[5/6] 公网 HTTPS 健康检查'
        $health = Test-PublicHealth
        if (-not $health) { throw '公网 /health 失败' }
        Write-Host "      $health"
        Write-Host '[6/6] 大请求体验证'
        $after = Test-OversizeRequest '部署后'
        if ($after -ne '400') { throw "预期 400（参数校验拒绝），实际 $after；部署前为 $before" }
    }
    catch {
        $reason = $_.Exception.Message
        Write-Host "[回滚] $reason"
        & ssh @sshOptions -p $Port "$User@$Server" "bash $remoteDir/remote-deploy.sh rollback"
        $summary = Get-RollbackSummary $LASTEXITCODE
        throw "公网验证失败（$reason）：$summary；上传目录保留在 $remoteDir"
    }
    & ssh @sshOptions -p $Port "$User@$Server" "rm -rf $remoteDir"
    if ($LASTEXITCODE) { Write-Warning "部署已通过验证，但删除远程临时目录 $remoteDir 失败，可稍后手动删除" }
    Write-Host "完成：提交 $commit，部署前 $before → 部署后 $after。"
    $succeeded = $true
}
finally {
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Force }
    if (Test-Path -LiteralPath $keyDir) { Remove-Item -LiteralPath $keyDir -Force }
    if (Test-Path -LiteralPath $tree) { git worktree remove --force $tree | Out-Null }
    # 成功时删除本次的构建输出和上传包；失败时保留供排查。
    if ($succeeded) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    elseif (Test-Path -LiteralPath $work) { Write-Host "本地构建目录保留在 $work 供排查，确认后可删除。" }
}
