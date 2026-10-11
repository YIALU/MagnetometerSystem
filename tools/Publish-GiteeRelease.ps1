#Requires -Version 5.1
<#
.SYNOPSIS
    把 artifacts\v<版本>\ 下已构建、已校验的三个发布文件上传到 Gitee Release，并转为正式版。

.DESCRIPTION
    用法（仓库根目录）：
        .\tools\Publish-GiteeRelease.ps1 0.5.9

    依次执行：
      1. 本地按 SHA256SUMS.txt 校验便携版 zip 与安装版 setup.exe；
      2. 核对 Gitee 与 GitHub 上的 v<版本> 标签指向同一提交（Gitee 由仓库镜像从 GitHub 同步）；
      3. 取 GitHub 同版本 Release 的标题和说明，在 Gitee 找到或新建该 Release（新建时先为预发布）；
      4. 上传缺少的附件；同名同大小的跳过，同名不同大小的停止；加 -Replace 则同名附件一律删除重传；
      5. 从 Gitee 重新下载全部附件，按 SHA256SUMS.txt 校验；
      6. 全部通过后转为正式版（加 -NoPublish 则保持预发布）。

    令牌：优先读环境变量 GITEE_TOKEN，否则向 Git 凭据管理器查询 gitee.com 的凭据，
    都没有时提示输入（不回显）。令牌只在本进程内使用，不写入命令行、文件或日志。

    Gitee 仓库附件总额度 1 GB，每版约 130 MB。上传因额度失败时，在 Gitee 网页删除旧版本附件后重跑即可，
    已上传的附件会被跳过。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version,
    [string]$ArtifactsDir,
    [string]$NotesFile,
    [switch]$Replace,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

$GiteeRepo = 'yialu/MagnetometerSystem'
$GitHubRepo = 'YIALU/MagnetometerSystem'
$GiteeApi = "https://gitee.com/api/v5/repos/$GiteeRepo"

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    $Text" -ForegroundColor Green }

$ver = $Version.Trim().TrimStart('v', 'V')
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "版本号应为 X.Y.Z 形式，收到：$Version" }
$tag = "v$ver"
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $repoRoot "artifacts\$tag" }
if (-not (Test-Path -LiteralPath $ArtifactsDir -PathType Container)) {
    throw "找不到发布目录 $ArtifactsDir。先用 build.ps1 在标签 $tag 的干净工作区构建。"
}

$fileNames = @(
    "MagnetometerSystem-$tag-portable-win-x64.zip",
    "MagnetometerSystem-$tag-setup.exe",
    'SHA256SUMS.txt'
)

# ------------------------------------------------------------ 1. 本地校验

Write-Step "本地校验 $ArtifactsDir"
$files = @{}
foreach ($name in $fileNames) {
    $path = Join-Path $ArtifactsDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少文件 $path" }
    $files[$name] = Get-Item -LiteralPath $path
}

$expected = @{}
foreach ($line in [IO.File]::ReadAllLines($files['SHA256SUMS.txt'].FullName)) {
    if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $expected[$Matches[2].Trim()] = $Matches[1].ToLower() }
}
foreach ($name in $fileNames) {
    if ($name -eq 'SHA256SUMS.txt') { continue }
    if (-not $expected.ContainsKey($name)) { throw "SHA256SUMS.txt 中没有 $name" }
    $actual = (Get-FileHash -LiteralPath $files[$name].FullName -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected[$name]) { throw "$name 的 SHA256 与清单不符：$actual" }
    Write-Ok "$name  $actual"
}
$sumsHash = (Get-FileHash -LiteralPath $files['SHA256SUMS.txt'].FullName -Algorithm SHA256).Hash.ToLower()

# ------------------------------------------------------------ 2. 标签

function Get-RemoteTagCommit([string]$Url) {
    $lines = @(git ls-remote $Url "refs/tags/$tag" "refs/tags/$tag^{}" 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "读取 $Url 的标签失败" }
    $peeled = $lines | Where-Object { $_ -match "refs/tags/$([regex]::Escape($tag))\^\{\}$" } | Select-Object -First 1
    if (-not $peeled) { $peeled = $lines | Select-Object -First 1 }
    if (-not $peeled) { return $null }
    return ($peeled -split '\s+')[0]
}

Write-Step "核对标签 $tag"
$gitHubCommit = Get-RemoteTagCommit "https://github.com/$GitHubRepo.git"
if (-not $gitHubCommit) { throw "GitHub 上没有标签 $tag" }
$giteeCommit = Get-RemoteTagCommit "https://gitee.com/$GiteeRepo.git"
if (-not $giteeCommit) { throw "Gitee 上还没有标签 $tag。等仓库镜像同步，或在 Gitee「仓库镜像管理」里手动更新后重试。" }
if ($giteeCommit -ne $gitHubCommit) { throw "两边的 $tag 指向不同提交：GitHub $gitHubCommit，Gitee $giteeCommit" }
Write-Ok "两边都指向 $gitHubCommit"

# ------------------------------------------------------------ 3. 标题与说明

Write-Step '读取 GitHub Release 的标题和说明'
$gitHubRelease = Invoke-RestMethod "https://api.github.com/repos/$GitHubRepo/releases/tags/$tag" -Headers @{ 'User-Agent' = 'MagnetometerSystem' }
$title = $gitHubRelease.name
if (-not $title) { $title = "V$ver" }
if ($NotesFile) { $body = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path) }
else { $body = $gitHubRelease.body }
if (-not $body) { throw 'GitHub Release 没有说明，请用 -NotesFile 指定。' }
Write-Ok "标题 $title，说明 $($body.Length) 字"

# ------------------------------------------------------------ 令牌

function Get-GiteeToken {
    if ($env:GITEE_TOKEN) { return $env:GITEE_TOKEN }
    try {
        $answer = "protocol=https`nhost=gitee.com`n`n" | git credential fill 2>$null
        foreach ($line in $answer) { if ($line -like 'password=*') { return $line.Substring(9) } }
    } catch { }
    $secure = Read-Host '输入 Gitee 私人令牌（不回显）' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$token = Get-GiteeToken
if (-not $token) { throw '没有拿到 Gitee 令牌。' }

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(30)

function Invoke-Gitee([string]$Method, [string]$Path, [hashtable]$Fields = @{}, [System.Net.Http.HttpContent]$Content) {
    $url = "$GiteeApi$Path"
    if ($Method -in 'GET', 'DELETE') {
        $url += "?access_token=$([Uri]::EscapeDataString($token))"
        foreach ($k in $Fields.Keys) { $url += "&$k=$([Uri]::EscapeDataString([string]$Fields[$k]))" }
    } elseif (-not $Content) {
        $pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
        $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' 'access_token', $token))
        foreach ($k in $Fields.Keys) {
            $pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' $k, ([string]$Fields[$k])))
        }
        $Content = New-Object System.Net.Http.FormUrlEncodedContent (, $pairs)
    }
    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($Method)), $url
    if ($Content) { $request.Content = $Content }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        # 只回报状态码和 Gitee 的说明，不带请求地址（GET 的地址里有令牌）
        if ([int]$response.StatusCode -eq 401) {
            throw "Gitee 拒绝了令牌（HTTP 401）。在 Gitee「设置 → 私人令牌」生成带 projects 权限的令牌，设到环境变量 GITEE_TOKEN 后重跑。"
        }
        throw "Gitee 返回 HTTP $([int]$response.StatusCode) $Method $Path：$text"
    }
    if ([string]::IsNullOrWhiteSpace($text) -or $text.Trim() -eq 'null') { return $null }
    # 不用管道：PowerShell 5.1 会把 JSON 数组当成一个对象输出；return 一个数组变量会逐项输出。
    $parsed = ConvertFrom-Json -InputObject $text
    return $parsed
}

# Gitee 附件列表若不带 size，返回 -1，大小交给下载后的哈希校验。
function Get-RemoteSize($Attachment) {
    if ($Attachment.PSObject.Properties['size'] -and $null -ne $Attachment.size) { return [long]$Attachment.size }
    return -1
}

try {
    # -------------------------------------------------------- 找到或新建 Release

    Write-Step "查找 Gitee Release $tag"
    $release = $null
    try { $release = Invoke-Gitee GET "/releases/tags/$tag" } catch { if ($_.Exception.Message -notmatch 'HTTP 404') { throw } }
    if ($release -and $release.id) {
        Write-Ok "已存在（id $($release.id)，预发布：$($release.prerelease)）"
    } else {
        $release = Invoke-Gitee POST '/releases' @{
            tag_name = $tag; name = $title; body = $body; prerelease = 'true'; target_commitish = $gitHubCommit
        }
        Write-Ok "已新建预发布（id $($release.id)）"
    }
    $releaseId = $release.id

    # -------------------------------------------------------- 4. 上传附件

    Write-Step '上传附件'
    $existing = @(Invoke-Gitee GET "/releases/$releaseId/attach_files" @{ per_page = 100 })
    foreach ($name in $fileNames) {
        $local = $files[$name]
        $remote = $existing | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if ($remote) {
            $remoteSize = Get-RemoteSize $remote
            if (-not $Replace) {
                if ($remoteSize -eq $local.Length -or $remoteSize -lt 0) { Write-Ok "$name 已存在，跳过"; continue }
                throw "Gitee 上的 $name 大小为 $remoteSize，本地为 $($local.Length)。确认要替换时加 -Replace 重跑。"
            }
            # -Replace 时无论大小都重传：同大小但内容损坏的附件也要能修好
            Invoke-Gitee DELETE "/releases/$releaseId/attach_files/$($remote.id)" | Out-Null
            Write-Ok "已删除旧的 $name"
        }
        Write-Host "    上传 $name（$([math]::Round($local.Length / 1MB, 1)) MB）…"
        $stream = [IO.File]::OpenRead($local.FullName)
        try {
            $form = New-Object System.Net.Http.MultipartFormDataContent
            $form.Add((New-Object System.Net.Http.StringContent $token), 'access_token')
            $fileContent = New-Object System.Net.Http.StreamContent $stream
            $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
            $form.Add($fileContent, 'file', $name)
            try { Invoke-Gitee POST "/releases/$releaseId/attach_files" -Content $form | Out-Null }
            catch {
                if ($_.Exception.Message -match '1\s*G|超出|exceed') {
                    throw "$($_.Exception.Message)`n仓库附件已达 1 GB 上限：在 Gitee 网页删除旧版本的附件后重跑本脚本，已传的会跳过。"
                }
                throw
            }
        } finally { $stream.Dispose() }
        Write-Ok "$name 已上传"
    }

    # -------------------------------------------------------- 5. 下载回来校验

    Write-Step '从 Gitee 下载附件并校验'
    $attached = @(Invoke-Gitee GET "/releases/$releaseId/attach_files" @{ per_page = 100 })
    $extra = @($attached | Where-Object { $fileNames -notcontains $_.name })
    if ($extra.Count -gt 0) { Write-Warning "Release 上另有附件（未改动）：$(($extra | ForEach-Object { $_.name }) -join ', ')" }
    $tempDir = Join-Path ([IO.Path]::GetTempPath()) ("gitee-verify-" + [Guid]::NewGuid())
    [IO.Directory]::CreateDirectory($tempDir) | Out-Null
    try {
        foreach ($name in $fileNames) {
            $remote = $attached | Where-Object { $_.name -eq $name } | Select-Object -First 1
            if (-not $remote) { throw "Gitee 上缺少 $name" }
            $remoteSize = Get-RemoteSize $remote
            if ($remoteSize -ge 0 -and $remoteSize -ne $files[$name].Length) { throw "$name 大小不符：Gitee $remoteSize，本地 $($files[$name].Length)" }
            $target = Join-Path $tempDir $name
            Invoke-WebRequest -Uri $remote.browser_download_url -OutFile $target -UseBasicParsing
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLower()
            if ($name -eq 'SHA256SUMS.txt') { $want = $sumsHash } else { $want = $expected[$name] }
            if ($hash -ne $want) { throw "下载回来的 $name SHA256 不符：$hash" }
            Write-Ok "$name  $hash"
        }
    } finally { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue }

    # -------------------------------------------------------- 6. 转正式

    if ($NoPublish) {
        Write-Step '已按 -NoPublish 保持预发布'
    } elseif ($release.prerelease -eq $false) {
        Write-Step '已是正式版'
    } else {
        Write-Step '转为正式版'
        Invoke-Gitee PATCH "/releases/$releaseId" @{
            tag_name = $tag; name = $title; body = $body; prerelease = 'false'
        } | Out-Null
        Write-Ok '完成'
    }
    Write-Host "https://gitee.com/$GiteeRepo/releases/tag/$tag"
} finally {
    $client.Dispose()
    $token = $null
}
