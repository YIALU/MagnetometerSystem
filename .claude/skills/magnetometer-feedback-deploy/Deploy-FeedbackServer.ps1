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
    [string]$Ref = 'origin/master',
    # 已有 server-bundle.tar.gz 时跳过构建（安装脚本和 unit 仍取自 $Ref）。
    [string]$Bundle
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

$repo = (git rev-parse --show-toplevel).Trim()
$work = Join-Path $repo ('.codex_tmp/feedback-deploy/' + (Get-Date -Format 'yyyyMMddHHmmss'))
$keyDir = Join-Path $work 'key'
$upload = Join-Path $work 'upload'
$tree = Join-Path $work 'tree'
[IO.Directory]::CreateDirectory($upload) | Out-Null

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
    if (-not $Bundle) {
        dotnet publish "$tree/src/MagnetometerSystem.Feedback.Server/MagnetometerSystem.Feedback.Server.csproj" `
            -c Release -r linux-x64 --self-contained true -o "$work/publish"
        if ($LASTEXITCODE) { throw 'dotnet publish 失败' }
        tar -czf "$upload/server-bundle.tar.gz" -C "$work/publish" .
        if ($LASTEXITCODE) { throw '打包失败' }
    } else {
        Copy-Item -LiteralPath $Bundle -Destination "$upload/server-bundle.tar.gz"
    }
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
    & ssh @sshOptions -p $Port "$User@$Server" "sed -i 's/\r$//' $remoteDir/*.sh $remoteDir/*.service && bash $remoteDir/remote-deploy.sh && rm -rf $remoteDir"
    if ($LASTEXITCODE) { throw "远程安装失败；上传目录保留在 $remoteDir 供排查" }

    # 6. 公网验证：证书校验保持开启。
    Write-Host '[5/6] 公网 HTTPS 健康检查'
    $health = & curl.exe -sS --fail -m 30 "https://$Server/health"
    if ($LASTEXITCODE) { throw '公网 /health 失败' }
    Write-Host "      $health"
    Write-Host '[6/6] 大请求体验证'
    $after = Test-OversizeRequest '部署后'
    if ($after -ne '400') { throw "预期 400（参数校验拒绝），实际 $after；部署前为 $before" }
    Write-Host "完成：提交 $commit，部署前 $before → 部署后 $after。"
}
finally {
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Force }
    if (Test-Path -LiteralPath $keyDir) { Remove-Item -LiteralPath $keyDir -Force }
    if (Test-Path -LiteralPath $tree) { git worktree remove --force $tree | Out-Null }
}
