<#
.SYNOPSIS
    把便携版简览升级到新包，保留 data\ 里的收藏、工作区、标签与会话。

.DESCRIPTION
    便携版把全部用户数据放在 exe 同级的 data\ 里：
        data\settings.json     设置、收藏（bookmarks 段）、会话（上次的标签页与窗口位置）
        data\workspaces.json   工作区
        data\data\tags.jsonl   彩色标签库
        data\logs\             日志
    新包里不带 data\，所以升级本可以不碰它。但这个脚本按更保险的做法走：
    先把 data\ 备份到安装目录的同级，再把旧安装目录整体改名让位，最后把新载荷落地、
    把 data\ 搬回来。任何一步失败都会回退到操作前的状态，旧目录不会被删除。

.PARAMETER InstallDir
    要升级的那份简览目录（含 Jianlan.exe 与 data\）。

.PARAMETER Package
    新的发行包 zip。

.PARAMETER DataOnly
    只做备份与校验，不替换程序文件。想先看清 data\ 里有什么时用。

.EXAMPLE
    .\upgrade-keep-data.ps1 -InstallDir 'E:\软件\Jianlan-0.2.1-portable-win-x64' -Package 'D:\...\Jianlan-0.2.1-portable-win-x64.zip'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$Package,
    [switch]$DataOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Expand-Archive 的进度条会刷满整屏，遮住真正有用的输出
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }

# 统计一份 data\ 里真正属于"用户设置"的东西，用于前后比对
function Get-DataSummary([string]$dataDir) {
    $summary = [ordered]@{
        SettingsExists  = $false
        SchemaVersion   = $null
        Bookmarks       = 0
        BookmarkGroups  = 0
        Workspaces      = 0
        Tags            = 0
        SessionTabs     = 0
    }

    $settingsPath = Join-Path $dataDir 'settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $summary.SettingsExists = $true
        $json = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $summary.SchemaVersion = $json.schemaVersion
        if ($null -ne $json.bookmarks) {
            if ($null -ne $json.bookmarks.items) { $summary.Bookmarks = @($json.bookmarks.items).Count }
            if ($null -ne $json.bookmarks.groups) { $summary.BookmarkGroups = @($json.bookmarks.groups).Count }
        }
        if ($null -ne $json.session -and $null -ne $json.session.tabs) {
            $summary.SessionTabs = @($json.session.tabs).Count
        }
    }

    $workspacesPath = Join-Path $dataDir 'workspaces.json'
    if (Test-Path -LiteralPath $workspacesPath) {
        $ws = Get-Content -LiteralPath $workspacesPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $ws.workspaces) { $summary.Workspaces = @($ws.workspaces).Count }
    }

    $tagsPath = Join-Path $dataDir (Join-Path 'data' 'tags.jsonl')
    if (Test-Path -LiteralPath $tagsPath) {
        $summary.Tags = @(Get-Content -LiteralPath $tagsPath -Encoding UTF8 | Where-Object { $_.Trim() -ne '' }).Count
    }

    return [pscustomobject]$summary
}

function Show-Summary($summary, [string]$label) {
    Write-Host "    $label  设置文件=$($summary.SettingsExists)  schemaVersion=$($summary.SchemaVersion)" -ForegroundColor Gray
    Write-Host "             收藏 $($summary.Bookmarks) 项 / 分组 $($summary.BookmarkGroups) 个" -ForegroundColor Gray
    Write-Host "             工作区 $($summary.Workspaces) 个 / 标签 $($summary.Tags) 条 / 上次标签页 $($summary.SessionTabs) 个" -ForegroundColor Gray
}

# ---- 0. 前置检查 ------------------------------------------------------------
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$Package = [IO.Path]::GetFullPath($Package)

if (-not (Test-Path -LiteralPath $InstallDir -PathType Container)) { throw "安装目录不存在：$InstallDir" }
if (-not (Test-Path -LiteralPath $Package -PathType Leaf)) { throw "发行包不存在：$Package" }

$exePath = Join-Path $InstallDir 'Jianlan.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw "这个目录里没有 Jianlan.exe，确认是不是简览的安装目录：$InstallDir" }

$dataDir = Join-Path $InstallDir 'data'
if (-not (Test-Path -LiteralPath $dataDir -PathType Container)) {
    Write-Warn "目录里没有 data\，这次升级没有可保留的设置（会变成全新安装）。"
}

# 单实例互斥体是全局的：另一个盘上的简览还开着，新版本也起不来，而且文件会被占用
$running = @(Get-Process -Name Jianlan -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host '简览还在运行，请先完全退出后再跑本脚本：' -ForegroundColor Red
    foreach ($p in $running) { Write-Host ("    进程 {0}  {1}" -f $p.Id, $p.Path) -ForegroundColor Red }
    throw '有 Jianlan 进程在运行。'
}

$before = Get-DataSummary $dataDir
Write-Step '升级前的数据'
Show-Summary $before '现在'

# ---- 1. 备份 data\ ---------------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$parent = Split-Path -Parent $InstallDir
if ([string]::IsNullOrWhiteSpace($parent)) { $parent = (Split-Path -Qualifier $InstallDir) + '\' }
$dataBackup = Join-Path $parent ("_jianlan-data-bak-$stamp")

if (Test-Path -LiteralPath $dataDir -PathType Container) {
    Write-Step "备份 data\ 到 $dataBackup"
    Copy-Item -LiteralPath $dataDir -Destination $dataBackup -Recurse -Force
    $files = @(Get-ChildItem -LiteralPath $dataBackup -Recurse -File)
    Write-Ok "已备份 $($files.Count) 个文件"
}

if ($DataOnly) {
    Write-Step '只做备份（-DataOnly），程序文件未改动'
    return
}

# ---- 2. 解压新包到临时目录 --------------------------------------------------
$tempRoot = Join-Path $env:TEMP ("jianlan-upgrade-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Write-Step "解压新包到 $tempRoot"
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    Expand-Archive -LiteralPath $Package -DestinationPath $tempRoot -Force
    $tops = @(Get-ChildItem -LiteralPath $tempRoot -Directory)
    if ($tops.Count -ne 1) { throw "包内应只有一个顶层目录，实际 $($tops.Count) 个。" }
    $payload = $tops[0].FullName
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'Jianlan.exe'))) {
        throw "包内没找到 Jianlan.exe：$payload"
    }
    if (Test-Path -LiteralPath (Join-Path $payload 'data')) {
        throw '包内带了 data\ 目录，这份包装法与预期不符，停止以免覆盖你的设置。'
    }
    $newExeVersion = (Get-Item -LiteralPath (Join-Path $payload 'Jianlan.exe')).VersionInfo.ProductVersion
    Write-Ok "载荷就位（$newExeVersion）"

    # ---- 3. 旧目录让位，新载荷落地 -----------------------------------------
    $oldDir = "$InstallDir.old-$stamp"
    Write-Step "旧目录改名让位：$oldDir"
    Move-Item -LiteralPath $InstallDir -Destination $oldDir

    try {
        Write-Step "新载荷搬到 $InstallDir"
        Move-Item -LiteralPath $payload -Destination $InstallDir
    }
    catch {
        Write-Warn '新载荷落地失败，正在把旧目录改回来'
        if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force -Confirm:$false }
        Move-Item -LiteralPath $oldDir -Destination $InstallDir
        throw
    }

    # ---- 4. 把用户数据搬回新目录 -------------------------------------------
    if (Test-Path -LiteralPath $dataBackup -PathType Container) {
        Write-Step '把备份的 data\ 放回新安装目录'
        $newData = Join-Path $InstallDir 'data'
        # 目标不存在时 Copy-Item 'src\*' 'dst' 会把 dst 当成文件名，必须先建目录
        New-Item -ItemType Directory -Path $newData -Force | Out-Null
        Copy-Item -Path (Join-Path $dataBackup '*') -Destination $newData -Recurse -Force
        Write-Ok '已放回'
    }

    # ---- 5. 校验 -----------------------------------------------------------
    Write-Step '校验'
    $after = Get-DataSummary (Join-Path $InstallDir 'data')
    Show-Summary $after '升级后'

    $problems = @()
    if ($after.SettingsExists -ne $before.SettingsExists) { $problems += 'settings.json 有无发生变化' }
    if ($after.Bookmarks -ne $before.Bookmarks) { $problems += "收藏数量 $($before.Bookmarks) → $($after.Bookmarks)" }
    if ($after.Workspaces -ne $before.Workspaces) { $problems += "工作区数量 $($before.Workspaces) → $($after.Workspaces)" }
    if ($after.Tags -ne $before.Tags) { $problems += "标签数量 $($before.Tags) → $($after.Tags)" }
    if ($after.SessionTabs -ne $before.SessionTabs) { $problems += "上次标签页 $($before.SessionTabs) → $($after.SessionTabs)" }

    $newInfo = (Get-Item -LiteralPath (Join-Path $InstallDir 'Jianlan.exe')).VersionInfo.ProductVersion
    Write-Ok "Jianlan.exe 版本：$newInfo"
    Write-Ok "portable.flag 存在：$(Test-Path -LiteralPath (Join-Path $InstallDir 'portable.flag'))"

    if ($problems.Count -gt 0) {
        Write-Host '用户数据前后不一致：' -ForegroundColor Red
        foreach ($p in $problems) { Write-Host "    - $p" -ForegroundColor Red }
        Write-Host "旧目录仍在：$oldDir" -ForegroundColor Yellow
        throw '校验未通过，请勿删除旧目录。'
    }

    Write-Ok '用户数据一致'

    Write-Host ''
    Write-Host '升级完成。确认新版本能正常打开、收藏与工作区都在之后，可以删除这两个目录：' -ForegroundColor Cyan
    Write-Host "    旧安装目录：$oldDir" -ForegroundColor Gray
    Write-Host "    数据备份  ：$dataBackup" -ForegroundColor Gray
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
}
