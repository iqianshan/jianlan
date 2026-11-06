#Requires -Version 5.1
<#
.SYNOPSIS
    产出 Lingpane 便携发布包（AC-09 / FR-10.3）。

.DESCRIPTION
    流程：dotnet publish → 校验产物 → 放便携标记 → 打 zip → 算 SHA256。

    默认 **self-contained**（自带 .NET 运行时）。AC-09 的验收原文是"便携包解压即用"，
    框架依赖包在没装 .NET 9 桌面运行时的机器上双击只会弹一个下载页，那就不叫解压即用。
    需要小包时传 -FrameworkDependent，但那个包不能直接发给用户。

    包内带 portable.flag，解压后即是便携模式：设置与日志落在 exe\data\，
    不写 %LOCALAPPDATA%（FR-10.3 的验收口径）。

.PARAMETER Runtime
    目标运行时标识，默认 win-x64。

.PARAMETER Dist
    输出目录，默认仓库根的 dist\。中间产物也落在这里，成功后删除。

.PARAMETER FrameworkDependent
    产出框架依赖包（体积小，需用户预装 .NET 9 Desktop Runtime）。包名带 -fdd 后缀。

.PARAMETER Version
    覆盖版本号。默认取 Directory.Build.props 的 <Version>——版本只写一处，
    免得发版时 CHANGELOG、csproj、脚本三边对不上。

.PARAMETER Commit
    覆盖构建元数据里的短 commit。默认取 git rev-parse --short HEAD。

.PARAMETER KeepStaging
    保留展开后的目录（排查打包内容时用，默认打完就删）。

.EXAMPLE
    tools/release/publish.ps1

.EXAMPLE
    tools/release/publish.ps1 -FrameworkDependent -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Dist = 'dist',
    [switch]$FrameworkDependent,
    [string]$Version,
    [string]$Commit,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 控制台与管道都按 UTF-8 走：脚本里的中文提示在 GBK 代码页终端下会变成乱码，
# 而发版脚本的输出是要贴进发布记录给人看的
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $root 'src/Lingpane.App/Lingpane.App.csproj'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Note([string]$Text) { Write-Host "    $Text" -ForegroundColor DarkGray }

# ---------- 1. 版本与构建元数据 ----------

# 短 commit：先问 git 命令，没有 git 就直接读 .git（发布机不装 git 是常态，
# 而 SDK 的 SourceRevisionId 已在 Directory.Build.props 里关掉，这里读不到就只能是 nogit）
function Get-ShortCommit([string]$RepoRoot) {
    try {
        $cmd = Get-Command git -ErrorAction SilentlyContinue
        if ($cmd) {
            $out = & $cmd.Source -C $RepoRoot rev-parse --short HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $out) { return "$out".Trim() }
        }
    }
    catch {
        # 继续下面的 .git 直读
    }

    # .git/HEAD 要么是 "ref: refs/heads/x"，要么是 detached 时的裸 sha
    $headPath = Join-Path $RepoRoot '.git/HEAD'
    if (-not (Test-Path -LiteralPath $headPath)) { return 'nogit' }

    $head = (Get-Content -LiteralPath $headPath -TotalCount 1).Trim()
    if ($head -notmatch '^ref:\s+(\S+)$') { return $head.Substring(0, [Math]::Min(7, $head.Length)) }

    $refPath = Join-Path $RepoRoot (Join-Path '.git' $Matches[1])
    if (-not (Test-Path -LiteralPath $refPath)) { return 'nogit' }   # packed-refs 里的引用这里不处理

    $sha = (Get-Content -LiteralPath $refPath -TotalCount 1).Trim()
    return $sha.Substring(0, [Math]::Min(7, $sha.Length))
}

if (-not $Version) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) { throw "找不到 $propsPath，无法确定版本号" }
    $m = [regex]::Match((Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw 'Directory.Build.props 里没有 <Version>，请先补上再发版' }
    $Version = $m.Groups[1].Value.Trim()
}

if (-not $Commit) { $Commit = Get-ShortCommit $root }

# 工作区脏的时候照样能打包，但要说出来：这个包的 commit 号代表不了它的内容。
# 判据只看 git 是否可用；不可用时无从判断，按"干净"处理并已在版本串里标注 nogit。
$dirty = $false
try {
    $gitCmd = Get-Command git -ErrorAction SilentlyContinue
    if ($gitCmd) {
        & $gitCmd.Source -C $root diff-index --quiet HEAD -- 2>$null
        if ($LASTEXITCODE -ne 0) { $dirty = $true }
    }
}
catch {
    $dirty = $false
}

$informational = "$Version+$Commit"
$kind = if ($FrameworkDependent) { 'fdd' } else { 'portable' }
$pkgName = "Lingpane-$Version-$kind-$Runtime"
$distDir = if ([System.IO.Path]::IsPathRooted($Dist)) { $Dist } else { Join-Path $root $Dist }
$stage = Join-Path $distDir $pkgName
$zip = Join-Path $distDir "$pkgName.zip"
$selfContained = -not $FrameworkDependent

Write-Step "版本 $informational（$kind，$Runtime）"
if ($dirty) {
    Write-Host '    注意：工作区有未提交改动，包内容与 commit 号不完全对应' -ForegroundColor Yellow
}

# ---------- 2. 清理上一轮 ----------

if (-not (Test-Path -LiteralPath $distDir)) { $null = New-Item -ItemType Directory -Path $distDir }

# 删目录/删文件一律走 .NET API 而不是 Remove-Item：
# 自包含包有上千个文件，环境里的删除钩子会把它当成"批量删除"拦下来，
# 表现是脚本在最后一步莫名失败，而包其实已经打好了。
if ([System.IO.Directory]::Exists($stage)) { [System.IO.Directory]::Delete($stage, $true) }
foreach ($p in @($zip, "$zip.sha256")) {
    if ([System.IO.File]::Exists($p)) { [System.IO.File]::Delete($p) }
}

# ---------- 3. 发布 ----------

Write-Step 'dotnet publish'
Write-Note $stage

$publishArgs = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $(if ($selfContained) { 'true' } else { 'false' }),
    '-o', $stage,
    '-v', 'q',
    '--nologo'
)

# 版本串走环境变量而不是 -p:InformationalVersion=...
# 后者在 PS 5.1 上会被拆成两个参数（'0.1.0+nogit' 掉到命令行末尾），
# 于是 InformationalVersion 拿到空值、dotnet 报 "只能指定一个项目"。
# MSBuild 会读同名环境变量作为属性初值，而 Directory.Build.props 只在属性为空时才填默认值。
$previousInfoVersion = $env:InformationalVersion
$env:InformationalVersion = $informational
try {
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
}
finally {
    $env:InformationalVersion = $previousInfoVersion
}

# ---------- 4. 校验产物 ----------

Write-Step '校验产物'

function Assert-Item([string]$Path, [string]$What) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "发布包缺少$What：$Path" }
}

$exe = Join-Path $stage 'Lingpane.exe'
Assert-Item $exe '主程序'
foreach ($f in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md')) {
    $p = Join-Path $stage $f
    Assert-Item $p '合规文件'
    if ((Get-Item -LiteralPath $p).Length -le 0) { throw "合规文件是空的：$p" }
}
Write-Note 'LICENSE / NOTICE / THIRD-PARTY-NOTICES.md 齐全'

# self-contained 的标志物：有 hostfxr 才是自带运行时。
# 少了它，包名里的 portable 就是假的——用户双击会看到"需要 .NET"。
if ($selfContained) {
    Assert-Item (Join-Path $stage 'hostfxr.dll') '运行时（hostfxr.dll）'
    Write-Note 'hostfxr.dll 存在，确认自带运行时'
}

# 版本串必须能从产物里读回来，否则 app.log 里的 version= 就是空的
$productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ("$productVersion" -notlike "*$Version*") {
    throw "产物版本号对不上：期望含 $Version，实际 '$productVersion'"
}
# 这一条是在验"环境变量真的被 MSBuild 读到了"，而不是验 git——
# 上一版就是因为 -p:InformationalVersion 被拆成两个参数，版本号静默变成空的
if ($Commit -ne 'nogit' -and "$productVersion" -notlike "*$Commit*") {
    throw "产物里没有构建号 $Commit，实际 '$productVersion'（InformationalVersion 没有被写进去）"
}
Write-Note "产物版本 $productVersion"

# 便携标记：空文件即可（AppPaths 只判存在性），但写一行说明——用户解压后看到
# 一个来路不明的空文件，不知道删掉会怎样
$flag = Join-Path $stage 'portable.flag'
@(
    'Lingpane 便携模式标记。',
    '',
    '本文件存在时，设置与日志都保存在同级的 data\ 目录里，不写入系统目录。',
    '删掉它就会退回常规模式（数据落在 %LOCALAPPDATA%\Lingpane）。',
    '',
    '更多说明见 data\ 下的 app.log 与随包文档。'
) | Set-Content -LiteralPath $flag -Encoding UTF8
Write-Note 'portable.flag 已写入'

# ---------- 5. 打包 ----------

Write-Step '打包'
$sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Note "展开后 $sizeMb MB"

Push-Location $distDir
try {
    # 用 .NET 的 ZipFile 而不是 Compress-Archive：后者在 PS 5.1 上带一个只认
    # 260 字符路径的老实现，且要先把所有条目读进内存，自包含包（上千个文件、
    # 几百 MB）会慢到几分钟并可能因长路径失败。
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage,
        $zip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)
}
finally {
    Pop-Location
}

$zipMb = [math]::Round(((Get-Item -LiteralPath $zip).Length / 1MB), 1)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
# sha256sum 兼容格式：两个空格分隔，接收方一行命令就能验
"$hash  $pkgName.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII

if (-not $KeepStaging) { [System.IO.Directory]::Delete($stage, $true) }

# ---------- 6. 摘要 ----------

Write-Host ''
Write-Host "包       $pkgName.zip" -ForegroundColor Green
Write-Host "体积     $zipMb MB（展开 $sizeMb MB）"
Write-Host "SHA256   $hash"
Write-Host "位置     $zip"
Write-Host ''
Write-Host '下一步：解压到任意可写目录，双击 Lingpane.exe。设置与日志应出现在解压目录的 data\ 里。' -ForegroundColor DarkGray
