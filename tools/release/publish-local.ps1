#Requires -Version 5.1
<#
.SYNOPSIS
    刷新仓库根的 publish\ 目录，供本机"双击试一下"用。

.DESCRIPTION
    publish\ 与 tools\release\publish.ps1 是两件事：
      · publish.ps1   产出**便携 zip**（self-contained + 校验 + 版本串），是发版动作；
      · 本脚本        只把当前代码 publish 到 publish\，让你点 exe 看到的就是刚改的东西。

    publish\ 被 .gitignore 排除（它是构建产物，不该进版本库），因此**它不会跟着 git checkuot
    自动更新**。改了代码之后直接双击 publish\Lingpane.exe，跑的是上一次 publish 的旧程序——
    这个坑踩过不止一次（界面上看不到新功能，日志里也没有任何异常）。

    框架依赖而不是 self-contained：本机已有运行时，170MB 的包对"点一下看看"没必要。

    exe 会被写保护挡住的常见原因：上一次的 Lingpane.exe 还开着。脚本先检测再动，不猜。

.PARAMETER Runtime
    目标运行时，默认 win-x64。

.PARAMETER Open
    发布完成后直接启动一次，确认能跑起来。

.EXAMPLE
    tools/release/publish-local.ps1

.EXAMPLE
    tools/release/publish-local.ps1 -Open
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [switch] $Open
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repo 'src\Lingpane.App\Lingpane.App.csproj'
$target = Join-Path $repo 'publish'
$exe = Join-Path $target 'Lingpane.exe'

if (-not (Test-Path $project)) { throw "找不到项目文件：$project" }

# 正在运行的旧进程会锁住 exe，此时 publish 报 "文件被占用"，看着像构建错误
$running = Get-Process -Name 'Lingpane' -ErrorAction SilentlyContinue
if ($running) {
    throw "Lingpane 正在运行（PID $($running.Id -join ', ')），先关掉再发布——否则 exe 写不进去。"
}

Write-Host "→ dotnet publish → publish\" -ForegroundColor Cyan

& dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained false `
    -o $target `
    --nologo `
    -v quiet

if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit $LASTEXITCODE）" }

if (-not (Test-Path $exe)) { throw "publish 报成功但没有 $exe" }

$info = Get-Item $exe
Write-Host ("[OK] publish\Lingpane.exe  {0:N0} 字节  {1:yyyy-MM-dd HH:mm:ss}" -f $info.Length, $info.LastWriteTime) -ForegroundColor Green
Write-Host '  上面这个时间戳就是「这份 exe 是不是刚改的代码」的判据，跟 git log -1 的时刻比一下。'

if ($Open) {
    Write-Host "→ 启动一次" -ForegroundColor Cyan
    Start-Process -FilePath $exe -WorkingDirectory $target
}
