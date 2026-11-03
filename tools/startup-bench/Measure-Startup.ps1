<#
.SYNOPSIS
    测量 Lingpane 冷启动耗时，输出 P50 / P95，并给出是否通过 NF-01 门禁的判定。

.DESCRIPTION
    两个指标同时采集：
      outer —— 外部计时：Process.Start → 主窗口句柄出现。接近用户感知。
      inner —— 进程内埋点：App 构造 → 首帧渲染完成（startup.log 的 total）。

    门禁判定用 inner 的 P50（≤700ms）与 P95（≤900ms）。outer 只做参考，因为它受轮询粒度
    （10ms）与系统负载影响，不适合做硬性阈值。

    说明：本脚本不清系统文件缓存，测的是"已运行过一次之后"的稳定值。
    首次运行（真正的冷盘）请单独跑一次并记录。

.EXAMPLE
    .\Measure-Startup.ps1 -Exe ..\..\publish\Lingpane.exe -Runs 20
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,

    [int]$Runs = 20,

    [int]$GateP50Ms = 700,

    [int]$GateP95Ms = 900
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { throw "找不到可执行文件：$Exe" }

$logPath = Join-Path $env:LOCALAPPDATA 'Lingpane\logs\startup.log'
if (Test-Path $logPath) { Remove-Item $logPath -Force }

function Get-Percentile {
    param([double[]]$Values, [double]$P)
    $sorted = $Values | Sort-Object
    $index = [Math]::Ceiling($sorted.Count * $P) - 1
    if ($index -lt 0) { $index = 0 }
    return $sorted[$index]
}

$outer = New-Object System.Collections.Generic.List[double]
$inner = New-Object System.Collections.Generic.List[double]

Write-Host "测量 $Runs 次：$Exe" -ForegroundColor Cyan

for ($i = 1; $i -le $Runs; $i++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $Exe -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)

    while (-not $proc.HasExited -and $proc.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 10
        $proc.Refresh()
    }

    $sw.Stop()
    $outer.Add($sw.Elapsed.TotalMilliseconds)

    # 等埋点落盘
    Start-Sleep -Milliseconds 250
    if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }

    $innerMs = $null
    if (Test-Path $logPath) {
        $lastLine = Get-Content $logPath -Tail 1
        if ($lastLine -match 'total=(\d+)') { $innerMs = [double]$Matches[1] }
    }
    if ($null -ne $innerMs) { $inner.Add($innerMs) }

    $displayInner = if ($null -ne $innerMs) { "$innerMs ms" } else { "n/a" }
    Write-Host ("  第 {0,2} 次：外部 {1,6:N0} ms | 进程内 {2}" -f $i, $sw.Elapsed.TotalMilliseconds, $displayInner)
}

if ($inner.Count -eq 0) {
    Write-Host "未采集到进程内埋点，请确认日志路径：$logPath" -ForegroundColor Yellow
    exit 1
}

$innerArr = $inner.ToArray()
$outerArr = $outer.ToArray()

$innerP50 = Get-Percentile $innerArr 0.50
$innerP95 = Get-Percentile $innerArr 0.95
$outerP50 = Get-Percentile $outerArr 0.50
$outerP95 = Get-Percentile $outerArr 0.95

Write-Host ""
Write-Host "================ 结果 ================" -ForegroundColor Cyan
Write-Host ("进程内 P50 = {0,6:N0} ms   P95 = {1,6:N0} ms   min = {2,6:N0} ms   max = {3,6:N0} ms" -f `
        $innerP50, $innerP95, ($innerArr | Measure-Object -Minimum).Minimum, ($innerArr | Measure-Object -Maximum).Maximum)
Write-Host ("外部   P50 = {0,6:N0} ms   P95 = {1,6:N0} ms" -f $outerP50, $outerP95)
Write-Host ("门禁阈值   P50 <= {0} ms，P95 <= {1} ms" -f $GateP50Ms, $GateP95Ms)

$p50Ok = $innerP50 -le $GateP50Ms
$p95Ok = $innerP95 -le $GateP95Ms

if ($p50Ok -and $p95Ok) {
    Write-Host "判定：通过" -ForegroundColor Green
    exit 0
} elseif ($p50Ok) {
    Write-Host "判定：P50 通过、P95 超标（抖动大，多为系统负载或后台扫描所致，建议空闲时复测）" -ForegroundColor Yellow
    exit 3
} else {
    Write-Host ("判定：未通过（P50 超出 {0} ms）" -f [Math]::Round($innerP50 - $GateP50Ms)) -ForegroundColor Red
    exit 2
}
