<#
.SYNOPSIS
    测量最小 WPF 空窗口的启动耗时，用于判断"启动变慢"是代码问题还是环境问题。

.DESCRIPTION
    这个程序只有空窗口，不含任何业务代码。它的启动耗时代表**当前这台机器跑 WPF 的
    最低成本**。判据很直接：

      空窗口 P50 <= 800ms  -> 环境正常，Jianlan 的启动数字可信
      空窗口 P50 >  800ms  -> 环境异常，任何 WPF 程序的启动数字都不可信

    为什么这个判据有效：2026-11-03 同一台机器实测，Jianlan 从 569ms 涨到 1102ms 时，
    空窗口同步从 541ms 涨到 1095ms——两者同幅变化。若只看 Jianlan 的数字，会误判成
    代码回归；有了这个对照，可以立刻排除。

.EXAMPLE
    .\Measure-Baseline.ps1 -Runs 6

.EXAMPLE
    # 已 publish 过，只测不编译
    .\Measure-Baseline.ps1 -Runs 6 -SkipBuild
#>
param(
    [int]$Runs = 6,

    # 空窗口 P50 超过此值即判定环境异常
    [int]$GateMs = 800,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'WpfBaseline'
$exe = Join-Path $proj 'out\WpfBaseline.exe'
$log = Join-Path $env:TEMP 'wpf-baseline.log'

if (-not $SkipBuild) {
    Write-Host "编译最小对照程序..." -ForegroundColor Cyan
    Push-Location $proj
    try {
        & dotnet publish -c Release -r win-x64 --self-contained false -o out --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）。确认已安装 .NET 9 SDK。" }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $exe)) { throw "找不到 $exe。先不加 -SkipBuild 跑一次。" }

if (Test-Path $log) { [System.IO.File]::Delete($log) }

$os = Get-CimInstance Win32_OperatingSystem
Write-Host ("环境       可用 {0:N1} / {1:N1} GB（{2:P0}），CPU {3}%" -f `
        ($os.FreePhysicalMemory / 1MB), ($os.TotalVisibleMemorySize / 1MB),
        ($os.FreePhysicalMemory / $os.TotalVisibleMemorySize),
        (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average)

Write-Host "测量 $Runs 次：$exe" -ForegroundColor Cyan

for ($i = 1; $i -le $Runs; $i++) {
    $p = Start-Process -FilePath $exe -PassThru
    $p.WaitForExit(30000) | Out-Null
    if (-not $p.HasExited) { $p.Kill() }
}

if (-not (Test-Path $log)) { throw "未采集到结果，检查 $log" }

$values = @(Get-Content $log | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [double]$_ })
if ($values.Count -eq 0) { throw '结果文件为空' }

$sorted = $values | Sort-Object
$p50 = $sorted[[Math]::Ceiling($sorted.Count * 0.5) - 1]
$p95 = $sorted[[Math]::Ceiling($sorted.Count * 0.95) - 1]
$min = ($values | Measure-Object -Minimum).Minimum
$max = ($values | Measure-Object -Maximum).Maximum

Write-Host ''
Write-Host '========== 最小 WPF 空窗口 ==========' -ForegroundColor Cyan
Write-Host ("次数 {0}   P50 = {1,6:N0} ms   P95 = {2,6:N0} ms   min = {3,6:N0}   max = {4,6:N0}" -f `
        $values.Count, $p50, $p95, $min, $max)
Write-Host ("本机历史正常值   541-665 ms（2026-11-03 上午实测）")
Write-Host ("判定阈值         P50 <= {0} ms" -f $GateMs)
Write-Host ''

if ($p50 -le $GateMs) {
    Write-Host '结论：环境正常。此时 Jianlan 的启动数字可信，超标即为代码问题。' -ForegroundColor Green
    exit 0
}
else {
    Write-Host '结论：环境异常。这台机器现在跑任何 WPF 程序都慢，Jianlan 的启动数字不可信。' -ForegroundColor Red
    Write-Host '      先排除：重启系统 / 关闭驻留的安全与监控软件 / 关闭浏览器与办公软件。' -ForegroundColor Yellow
    Write-Host '      排除后再测一次本脚本；回到 800ms 以内再测 Jianlan。' -ForegroundColor Yellow
    exit 2
}
