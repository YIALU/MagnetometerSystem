#Requires -Version 5.1
<#
.SYNOPSIS
    磁力仪系统打包脚本：产出便携版 zip 与安装版 setup.exe。

.DESCRIPTION
    版本号的真源是 git annotated tag（vX.Y.Z）。脚本会校验：
      1. HEAD 上确实打了 tag；
      2. tag 版本与 Directory.Build.props 的 <Version> 一致；
      3. 工作区干净（否则会打出 -dirty 版本）。
    校验通过后把版本号以 -p:Version 传给 dotnet publish。

    产物统一放在 artifacts\v<版本>\，命名规则固定，应用内的检查更新
    功能依赖这套命名从 Gitee release 附件里识别对应的包。

.EXAMPLE
    .\build.ps1
    从 HEAD 的 tag 取版本，同时打便携版和安装版。

.EXAMPLE
    .\build.ps1 -Mode Portable

.EXAMPLE
    .\build.ps1 -Version 0.4.0 -SkipVersionCheck -AllowDirty
    本地试打，不要求打过 tag、不要求工作区干净。
#>
[CmdletBinding()]
param(
    [ValidateSet('Portable', 'Installer', 'All')]
    [string]$Mode = 'All',

    # 手动指定版本号（如 0.4.0）。不传则从 git tag 推导。
    [string]$Version,

    # 允许工作区有未提交/未跟踪的改动
    [switch]$AllowDirty,

    # 允许 git tag 与 Directory.Build.props 的 <Version> 不一致
    [switch]$SkipVersionCheck
)

$ErrorActionPreference = 'Stop'
# Compress-Archive 的进度条会严重拖慢大文件压缩
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\MagnetometerSystem.App\MagnetometerSystem.App.csproj'
$IssPath = Join-Path $RepoRoot 'installer\MagnetometerSystem.iss'

function Write-Step { param([string]$Text) Write-Host ""; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok { param([string]$Text) Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "    警告: $Text" -ForegroundColor Yellow }

function Fail {
    param([string]$Text)
    Write-Host ""
    Write-Host "构建失败: $Text" -ForegroundColor Red
    Write-Host ""
    exit 1
}

# ---------------------------------------------------------------- 版本号推导

function Resolve-BuildVersion {
    if ($Version) {
        $v = $Version.Trim().TrimStart('v')
        Write-Ok "使用命令行指定的版本: $v"
        return $v
    }

    $tag = (& git describe --tags --exact-match HEAD 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tag)) {
        Fail @"
当前 HEAD 上没有 git tag，无法确定发布版本号。

  打标签发布：  git tag -a v0.4.0 -m "发布说明"
  本地试打包：  .\build.ps1 -Version 0.4.0 -SkipVersionCheck -AllowDirty
"@
    }

    $v = $tag.Trim().TrimStart('v')
    Write-Ok "从 git tag 取得版本: $($tag.Trim())"
    return $v
}

function Get-PropsVersion {
    $propsPath = Join-Path $RepoRoot 'Directory.Build.props'
    if (-not (Test-Path $propsPath)) { Fail "找不到 Directory.Build.props" }

    $xml = [xml](Get-Content $propsPath -Raw -Encoding UTF8)
    $node = $xml.SelectSingleNode('//PropertyGroup/Version')
    if ($null -eq $node) { Fail "Directory.Build.props 里找不到 <Version> 节点" }
    return $node.InnerText.Trim()
}

function Resolve-Iscc {
    # 候选路径，按优先级排序：优先大版本（7 带官方简体中文包 ChineseSimplified.isl，
    # 6.7 及以下不带，编译 .iss 会报 "unknown message name"）。
    # winget 默认装到 %LOCALAPPDATA%\Programs，老式安装装到 Program Files。
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    Fail @"
找不到 Inno Setup 的命令行编译器 ISCC.exe，无法打安装版。

  安装方式：  winget install --id JRSoftware.InnoSetup -e
  或手动下载：https://jrsoftware.org/isdl.php

  注意：需要 Inno Setup 6.4 以上，且自带 ChineseSimplified.isl 的版本
  （6.4+ 在源码仓库有，但安装包默认不带；7.0+ 官方自带，推荐装 7）。

  只想打便携版的话：  .\build.ps1 -Mode Portable
"@
}

# ------------------------------------------------------------------ 辅助函数

function Write-Utf8File {
    param([string]$Path, [string[]]$Lines, [bool]$Bom = $true)
    $encoding = New-Object System.Text.UTF8Encoding($Bom)
    [System.IO.File]::WriteAllLines($Path, $Lines, $encoding)
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return '{0:N1} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    return '{0:N1} KB' -f ($Bytes / 1KB)
}

function Invoke-Publish {
    param([string]$OutputDir, [bool]$SingleFile, [string]$BuildVersion, [string]$Label)

    # 注意：不要用 $args，那是 PowerShell 的自动变量
    $publishArgs = @(
        'publish', $ProjectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        "-p:PublishSingleFile=$($SingleFile.ToString().ToLower())",
        "-p:Version=$BuildVersion",
        '-o', $OutputDir,
        '--nologo'
    )
    if ($SingleFile) { $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true' }

    Write-Ok "dotnet publish ($Label) ..."
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish 失败（$Label）" }
}

# ====================================================================== 主流程

Push-Location $RepoRoot
try {
    Write-Step '校验版本号'

    $ver = Resolve-BuildVersion
    if ($ver -notmatch '^\d+\.\d+\.\d+$') {
        Fail "版本号格式非法: '$ver'，必须是 X.Y.Z（tag 形如 v0.4.0）"
    }

    if ($SkipVersionCheck) {
        Write-Warn '已跳过 tag 与 Directory.Build.props 的一致性校验'
    }
    else {
        $propsVer = Get-PropsVersion
        if ($propsVer -ne $ver) {
            Fail @"
git tag 版本 ($ver) 与 Directory.Build.props 的 <Version> ($propsVer) 不一致。

发布前请先 bump 版本号再打 tag：
  1. 修改 Directory.Build.props 的 <Version> 为 $ver
  2. git commit -m "chore: bump version to $ver"
  3. git tag -a v$ver -m "发布说明"
"@
        }
        Write-Ok "Directory.Build.props 版本一致: $propsVer"
    }

    # 与 Directory.Build.props 的 InjectGitSha 用同一条命令判定 dirty，
    # 避免打出 InformationalVersion 带 "-dirty" 后缀的发布包。
    $dirty = & git status --porcelain
    if ($dirty -and -not $AllowDirty) {
        $list = ($dirty | Select-Object -First 10) -join "`n  "
        Fail @"
工作区不干净，构建产物的版本号会被标记为 -dirty：

  $list

请先提交或清理（未跟踪文件也算），或加 -AllowDirty 强制打包。
"@
    }
    if ($dirty) { Write-Warn '工作区不干净，版本号将带 -dirty 后缀' }

    # ------------------------------------------------------------ 准备目录

    $artifactRoot = Join-Path $RepoRoot 'artifacts'
    $outDir = Join-Path $artifactRoot "v$ver"
    $stageRoot = Join-Path $artifactRoot '.stage'
    $portableName = "MagnetometerSystem-v$ver"
    $portableStage = Join-Path $stageRoot $portableName
    $installerStage = Join-Path $stageRoot 'installer-files'

    Write-Step "清理输出目录 artifacts\v$ver"
    Remove-Item -LiteralPath $outDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    $doPortable = $Mode -in @('Portable', 'All')
    $doInstaller = $Mode -in @('Installer', 'All')

    # 安装版依赖 ISCC，先探测再开始漫长的 publish，避免白等
    $iscc = $null
    if ($doInstaller) {
        Write-Step '检测 Inno Setup'
        $iscc = Resolve-Iscc
        Write-Ok "ISCC: $iscc"
        if (-not (Test-Path $IssPath)) { Fail "找不到安装脚本: $IssPath" }
    }

    # -------------------------------------------------------------- 便携版

    if ($doPortable) {
        Write-Step "打包便携版 v$ver"
        Invoke-Publish -OutputDir $portableStage -SingleFile $true -BuildVersion $ver -Label '便携版 / 单文件'

        # 运行时据此判定安装模式，决定检查更新时下载哪个附件
        New-Item -ItemType File -Path (Join-Path $portableStage 'portable.marker') -Force | Out-Null

        Write-Utf8File -Path (Join-Path $portableStage '使用说明.txt') -Lines @(
            "磁力仪数据采集与分析系统  便携版 v$ver",
            '',
            '【运行】',
            '  双击 MagnetometerSystem.App.exe 即可，无需安装。',
            '  首次启动会解压运行时文件，稍慢属正常现象。',
            '',
            '【数据存放位置】',
            '  测量数据库：%LOCALAPPDATA%\MagnetometerSystem\magnetometer.db',
            '  运行日志：  本程序所在目录的 logs 文件夹',
            '  数据库不在本目录内，因此升级时直接覆盖本目录不会丢失数据。',
            '',
            '【升级】',
            '  程序会自动检查更新（可在"设置"里关闭）。',
            '  也可以点右下角版本号打开"关于"窗口，手动点"检查更新"。',
            '  便携版升级方式：关闭程序后，把新版压缩包解压覆盖本目录即可。',
            '',
            '【项目主页】',
            '  https://gitee.com/yialu/MagnetometerSystem'
        )

        $zipPath = Join-Path $outDir "MagnetometerSystem-v$ver-portable-win-x64.zip"
        Write-Ok '压缩中（约需 1-2 分钟）...'
        Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
        Write-Ok "便携版: $(Split-Path $zipPath -Leaf)  ($(Format-Size (Get-Item $zipPath).Length))"
    }

    # -------------------------------------------------------------- 安装版

    if ($doInstaller) {
        Write-Step "打包安装版 v$ver"
        # 安装版发布为目录形式：Inno 的 LZMA2 对散文件压缩率远高于单文件包，
        # 且免去每次启动解压到临时目录的开销。
        Invoke-Publish -OutputDir $installerStage -SingleFile $false -BuildVersion $ver -Label '安装版 / 目录'

        Write-Ok 'ISCC 编译安装包（约需 1-2 分钟）...'
        & $iscc "/DMyAppVersion=$ver" "/DMySourceDir=$installerStage" "/O$outDir" $IssPath | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }

        $setupPath = Join-Path $outDir "MagnetometerSystem-v$ver-setup.exe"
        if (-not (Test-Path $setupPath)) { Fail "ISCC 未生成预期的安装包: $setupPath" }
        Write-Ok "安装版: $(Split-Path $setupPath -Leaf)  ($(Format-Size (Get-Item $setupPath).Length))"
    }

    # ----------------------------------------------------------- 校验文件

    Write-Step '生成 SHA256SUMS.txt'
    $lines = @()
    foreach ($f in (Get-ChildItem -LiteralPath $outDir -File | Sort-Object Name)) {
        if ($f.Name -eq 'SHA256SUMS.txt') { continue }
        $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLower()
        $lines += "$hash  $($f.Name)"
        Write-Ok "$($f.Name)  $hash"
    }
    # 与 sha256sum 格式兼容，不能带 BOM
    Write-Utf8File -Path (Join-Path $outDir 'SHA256SUMS.txt') -Lines $lines -Bom $false

    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue

    Write-Step '打包完成'
    Write-Host "    输出目录: $outDir" -ForegroundColor Green
    Write-Host ''
    Write-Host '    下一步：在 Gitee 新建 release（tag 选 ' -NoNewline -ForegroundColor Gray
    Write-Host "v$ver" -NoNewline -ForegroundColor White
    Write-Host '），上传以上全部文件。' -ForegroundColor Gray
    Write-Host '    https://gitee.com/yialu/MagnetometerSystem/releases/new' -ForegroundColor Gray
    Write-Host ''
}
finally {
    Pop-Location
}
