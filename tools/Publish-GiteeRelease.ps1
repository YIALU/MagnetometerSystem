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
      3. 取 GitHub 同版本 Release 的标题和说明，并确认本地 SHA256SUMS.txt 与它的一致；
         在 Gitee 找到或新建该 Release（新建时为预发布；要改动正式版的附件时先转回预发布）；
      4. 上传缺少的附件；同名同大小的跳过，同名不同大小的停止；加 -Replace 则同名附件一律删除重传；
      5. 从 Gitee 重新下载全部附件，按 SHA256SUMS.txt 校验（Gitee 下载慢、会断时加 -SizeOnly，
         大文件只核对 Gitee 记录的大小，清单文件仍下载校验，这时保持预发布）；
      6. 全部通过后转为正式版（加 -NoPublish 或 -SizeOnly 则保持预发布）。

    令牌：优先读环境变量 GITEE_TOKEN，否则向 Git 凭据管理器查询 gitee.com 的凭据，
    都没有时在交互终端提示输入（不回显），非交互运行直接报错。令牌不出现在命令行和日志里；
    上传附件用系统自带的 curl.exe，令牌只在上传期间写进临时 curl 配置文件，传完即删。

    Gitee 仓库附件总额度 1 GB，每版约 130 MB。上传因额度失败时，在 Gitee 网页删除旧版本附件后重跑即可，
    已上传的附件会被跳过。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version,
    [string]$ArtifactsDir,
    [string]$NotesFile,
    [switch]$Replace,
    [switch]$NoPublish,
    [switch]$SizeOnly
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
# 匿名接口只能读到已发布的 Release，草稿会返回 404
try { $gitHubRelease = Invoke-RestMethod "https://api.github.com/repos/$GitHubRepo/releases/tags/$tag" -Headers @{ 'User-Agent' = 'MagnetometerSystem' } }
catch { throw "读取 GitHub Release $tag 失败（还是草稿或不存在）：先在 GitHub 发布它。$($_.Exception.Message)" }
if ($gitHubRelease.prerelease) {
    if (-not $NoPublish) { throw "GitHub 上的 $tag 还是预发布：先在 GitHub 转为正式版，或加 -NoPublish 只在 Gitee 建预发布。" }
    Write-Warning "GitHub 上的 $tag 还是预发布，Gitee 将保持预发布。"
}
$title = $gitHubRelease.name
if (-not $title) { $title = "V$ver" }
if ($NotesFile) { $body = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path) }
else { $body = $gitHubRelease.body }
if (-not $body) { throw 'GitHub Release 没有说明，请用 -NotesFile 指定。' }
Write-Ok "标题 $title，说明 $($body.Length) 字"

# 两个平台的包必须字节一致：本地清单要与 GitHub Release 上的 SHA256SUMS.txt 相同
$gitHubSums = @($gitHubRelease.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' }) | Select-Object -First 1
if (-not $gitHubSums) { throw 'GitHub Release 上没有 SHA256SUMS.txt，无法确认两边的包一致。' }
$gitHubSumsPath = Join-Path ([IO.Path]::GetTempPath()) ("github-sums-" + [Guid]::NewGuid() + '.txt')
try {
    Invoke-WebRequest -Uri $gitHubSums.browser_download_url -OutFile $gitHubSumsPath -UseBasicParsing -Headers @{ 'User-Agent' = 'MagnetometerSystem' }
    $gitHubSumsHash = (Get-FileHash -LiteralPath $gitHubSumsPath -Algorithm SHA256).Hash.ToLower()
} finally { Remove-Item -LiteralPath $gitHubSumsPath -Force -ErrorAction SilentlyContinue }
if ($gitHubSumsHash -ne $sumsHash) { throw '本地 SHA256SUMS.txt 与 GitHub Release 上的不同：本地包可能被重新构建过，停止上传。' }
Write-Ok '本地清单与 GitHub Release 一致'

# GitHub 上的包也要与清单一致：优先用 GitHub 记录的 digest，没有时下载回来算
foreach ($name in $fileNames) {
    if ($name -eq 'SHA256SUMS.txt') { continue }
    $asset = @($gitHubRelease.assets | Where-Object { $_.name -eq $name }) | Select-Object -First 1
    if (-not $asset) { throw "GitHub Release 上没有 $name。" }
    if ([long]$asset.size -ne $files[$name].Length) { throw "GitHub 上的 $name 大小与本地不同：GitHub $($asset.size)，本地 $($files[$name].Length)" }
    if ($asset.digest) {
        if ($asset.digest.ToLower() -ne "sha256:$($expected[$name])") { throw "GitHub 上的 $name 与清单不符：$($asset.digest)" }
        Write-Ok "GitHub 上的 $name 与清单一致（digest）"
        continue
    }
    $gitHubAssetPath = Join-Path ([IO.Path]::GetTempPath()) ("github-asset-" + [Guid]::NewGuid())
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $gitHubAssetPath -UseBasicParsing -Headers @{ 'User-Agent' = 'MagnetometerSystem' }
        $gitHubAssetHash = (Get-FileHash -LiteralPath $gitHubAssetPath -Algorithm SHA256).Hash.ToLower()
    } finally { Remove-Item -LiteralPath $gitHubAssetPath -Force -ErrorAction SilentlyContinue }
    if ($gitHubAssetHash -ne $expected[$name]) { throw "GitHub 上的 $name 与清单不符：$gitHubAssetHash" }
    Write-Ok "GitHub 上的 $name 与清单一致（已下载校验）"
}

# ------------------------------------------------------------ 令牌

function Get-GiteeToken {
    if ($env:GITEE_TOKEN) { return $env:GITEE_TOKEN }
    # 只查已保存的凭据，不让 Git 或凭据管理器弹出登录提示
    $savedPrompt = $env:GIT_TERMINAL_PROMPT
    $savedGcm = $env:GCM_INTERACTIVE
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'
    # PowerShell 5.1 往原生程序的管道输入不可靠，查询内容经文件交给 cmd 重定向
    $query = Join-Path ([IO.Path]::GetTempPath()) ("gitee-cred-" + [Guid]::NewGuid() + '.txt')
    try {
        [IO.File]::WriteAllText($query, "protocol=https`nhost=gitee.com`n`n", (New-Object System.Text.UTF8Encoding($false)))
        $answer = cmd /c "git credential fill < `"$query`" 2>nul"
        foreach ($line in $answer) { if ($line -like 'password=*') { return $line.Substring(9) } }
    } catch { }
    finally {
        Remove-Item -LiteralPath $query -Force -ErrorAction SilentlyContinue
        $env:GIT_TERMINAL_PROMPT = $savedPrompt
        $env:GCM_INTERACTIVE = $savedGcm
    }
    if ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive) {
        throw '没有找到 Gitee 令牌：设置环境变量 GITEE_TOKEN，或在交互式 PowerShell 里运行以手动输入。'
    }
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
    $isPrerelease = $release.prerelease -ne $false

    # 标题和说明以 GitHub（或 -NotesFile）为准，已存在的 Release 也要跟上
    $normalize = { param($text) ([string]$text).Replace("`r`n", "`n").Trim() }
    if ($release.name -ne $title -or (& $normalize $release.body) -ne (& $normalize $body)) {
        if ($isPrerelease) { $keep = 'true' } else { $keep = 'false' }
        Invoke-Gitee PATCH "/releases/$releaseId" @{
            tag_name = $tag; name = $title; body = $body; prerelease = $keep
        } | Out-Null
        Write-Ok '已更新标题和说明'
    }

    # -------------------------------------------------------- 4. 上传附件

    Write-Step '上传附件'
    $existing = @(Invoke-Gitee GET "/releases/$releaseId/attach_files" @{ per_page = 100 })
    $plan = @()
    foreach ($name in $fileNames) {
        $local = $files[$name]
        $remote = $existing | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if ($remote -and -not $Replace) {
            $remoteSize = Get-RemoteSize $remote
            if ($remoteSize -eq $local.Length -or $remoteSize -lt 0) { Write-Ok "$name 已存在，跳过"; continue }
            throw "Gitee 上的 $name 大小为 $remoteSize，本地为 $($local.Length)。确认要替换时加 -Replace 重跑。"
        }
        $plan += [pscustomobject]@{ Name = $name; Remote = $remote }
    }

    # 改动正式版的附件前先转回预发布，中途失败也不会让用户看到缺附件的正式版；
    # 加了 -NoPublish 或 -SizeOnly（不做完整哈希校验）时，已是正式版的也转回预发布
    if (($plan.Count -gt 0 -or $NoPublish -or $SizeOnly) -and -not $isPrerelease) {
        Invoke-Gitee PATCH "/releases/$releaseId" @{
            tag_name = $tag; name = $title; body = $body; prerelease = 'true'
        } | Out-Null
        $isPrerelease = $true
        Write-Ok '已先转为预发布，校验通过后再转回正式版'
    }

    foreach ($item in $plan) {
        $name = $item.Name
        $local = $files[$name]
        if ($item.Remote) {
            # -Replace 时无论大小都重传：同大小但内容损坏的附件也要能修好
            Invoke-Gitee DELETE "/releases/$releaseId/attach_files/$($item.Remote.id)" | Out-Null
            Write-Ok "已删除旧的 $name"
        }
        Write-Host "    上传 $name（$([math]::Round($local.Length / 1MB, 1)) MB）…"
        # Gitee 拒收 .NET Framework HttpClient 的 multipart（HTTP 406），附件用 curl 上传。
        # 令牌写进只在本次上传期间存在的 curl 配置文件，不出现在命令行里。
        $curlConfig = Join-Path ([IO.Path]::GetTempPath()) ("gitee-upload-" + [Guid]::NewGuid() + '.cfg')
        $curlOutput = "$curlConfig.json"
        try {
            $filePath = $local.FullName.Replace('\', '/')
            $lines = @(
                "form = `"access_token=$token`"",
                "form = `"file=@$filePath;type=application/octet-stream`""
            )
            [IO.File]::WriteAllLines($curlConfig, $lines, (New-Object System.Text.UTF8Encoding($false)))
            $status = & curl.exe -q -sS --max-time 3600 -K $curlConfig -o $curlOutput -w '%{http_code}' "$GiteeApi/releases/$releaseId/attach_files"
            if ($LASTEXITCODE -ne 0) { throw "curl 上传 $name 失败（退出码 $LASTEXITCODE）" }
            $responseText = ''
            if (Test-Path -LiteralPath $curlOutput) { $responseText = [IO.File]::ReadAllText($curlOutput) }
            if ($status -notmatch '^2\d\d$') {
                $message = "Gitee 返回 HTTP $status 上传 $name：$responseText"
                if ($responseText -match '1\s*G|超出|exceed') {
                    $message += "`n仓库附件已达 1 GB 上限：在 Gitee 网页删除旧版本的附件后重跑本脚本，已传的会跳过。"
                }
                throw $message
            }
        } finally {
            Remove-Item -LiteralPath $curlConfig, $curlOutput -Force -ErrorAction SilentlyContinue
        }
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
            if ($SizeOnly -and $name -ne 'SHA256SUMS.txt') {
                if ($remoteSize -lt 0) { throw "Gitee 没有返回 $name 的大小，无法只核对大小，请去掉 -SizeOnly 重跑。" }
                Write-Ok "$name  大小一致（$remoteSize 字节，按 -SizeOnly 未下载）"
                continue
            }
            $target = Join-Path $tempDir $name
            try { Invoke-WebRequest -Uri $remote.browser_download_url -OutFile $target -UseBasicParsing }
            catch { throw "从 Gitee 下载 $name 失败：$($_.Exception.Message)`nGitee 下载慢或中断时，可加 -SizeOnly 重跑（已传的附件会跳过）。" }
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLower()
            if ($name -eq 'SHA256SUMS.txt') { $want = $sumsHash } else { $want = $expected[$name] }
            if ($hash -ne $want) { throw "下载回来的 $name SHA256 不符：$hash" }
            Write-Ok "$name  $hash"
        }
    } finally { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue }

    # -------------------------------------------------------- 6. 转正式

    if (-not $isPrerelease) {
        Write-Step '已是正式版，附件未改动'
    } elseif ($NoPublish) {
        Write-Step '已按 -NoPublish 保持预发布'
    } elseif ($SizeOnly) {
        # 只核对了大小，没有完整哈希校验，不能当作与 GitHub 逐字节一致
        Write-Step '已按 -SizeOnly 保持预发布'
        Write-Warning '大文件未下载校验 SHA256。去掉 -SizeOnly 重跑可校验并转为正式版；或自行从 Gitee 下载核对后，在 Gitee 网页上转为正式版。'
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
