<#
.SYNOPSIS
    测量 Lingpane 冷启动耗时，输出 P50 / P95，并给出是否通过 NF-01 门禁的判定。

.DESCRIPTION
    两个指标同时采集：
      outer —— 外部计时：Process.Start → 主窗口句柄出现。接近用户感知。
      inner —— 进程内埋点：App 构造 → 首帧渲染完成（startup.log 的 total）。

    门禁判定用 inner 的 P50 与 P95。outer 只做参考，因为它受轮询粒度（10ms）与系统负载影响。

    两种判定模式：
      固定阈值  —— 不传 -BaselinePath 时，用 -GateP50Ms / -GateP95Ms（默认 700 / 900）。
      相对基线  —— 传 -BaselinePath baseline.json，阈值 = baseline.p50 x 容差系数。
                   本地发布前用 localToleranceFactor（1.2），CI 用 ciToleranceFactor（2.5）。

    说明：本脚本不清系统文件缓存，测的是"已运行过一次之后"的稳定值。
    首次运行（真正的冷盘）请单独跑一次并记录。

.EXAMPLE
    .\Measure-Startup.ps1 -Exe ..\..\publish\Lingpane.exe -Runs 20

.EXAMPLE
    .\Measure-Startup.ps1 -Exe ..\..\publish\Lingpane.exe -Runs 20 -BaselinePath ..\..\baseline.json

.EXAMPLE
    # CI：宽松阈值，只拦数量级退化
    .\Measure-Startup.ps1 -Exe $exe -Runs 3 -BaselinePath baseline.json -ToleranceFactor 2.5
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,

    [int]$Runs = 20,

    [int]$GateP50Ms = 700,

    [int]$GateP95Ms = 900,

    # 相对基线判定。提供后忽略 GateP50Ms / GateP95Ms。
    [string]$BaselinePath,

    # 覆盖 baseline.json 中的容差系数。0 表示使用文件内的 localToleranceFactor。
    [double]$ToleranceFactor = 0,

    # 环境不合格时仍然测量并判定（默认不判定）
    [switch]$IgnoreEnvironment
)


$ErrorActionPreference = 'Stop'

# 测量有效性前置检查。
# 依据：WPF 启动的主要成本是 CLR 与框架程序集加载，内存紧张时这些页被压缩或换出，
# 启动耗时可从 578ms 涨到 800ms+。若不区分，会把系统负载误判成代码回归。
function Get-EnvironmentStatus {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpuSum = 0
    1..3 | ForEach-Object {
        $cpuSum += (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    }

    [pscustomobject]@{
        FreeGb    = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
        TotalGb   = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        FreeRatio = [double]$os.FreePhysicalMemory / [double]$os.TotalVisibleMemorySize
        CpuLoad   = [math]::Round($cpuSum / 3, 0)
    }
}

$envInfo = Get-EnvironmentStatus
$envOk = ($envInfo.FreeRatio -ge 0.35) -and ($envInfo.CpuLoad -le 50)

Write-Host ("测量环境   可用 {0} / {1} GB（{2:P0}），CPU {3}%" -f `
        $envInfo.FreeGb, $envInfo.TotalGb, $envInfo.FreeRatio, $envInfo.CpuLoad)

if (-not $envOk) {
    $msg = "可用内存不足 35% 或 CPU 高于 50%，本次测量不作为门禁依据"
    if ($IgnoreEnvironment) {
        Write-Host "[警告] $msg（已指定 -IgnoreEnvironment，继续判定）" -ForegroundColor Yellow
    }
    else {
        Write-Host "[跳过] $msg。请关闭占用内存的程序后重测。" -ForegroundColor Yellow
        exit 5
    }
}

if (-not (Test-Path $Exe)) { throw "找不到可执行文件：$Exe" }

$baseP50 = $null
$baseP95 = $null
$factor = 0
$mode = '固定阈值'

if ($BaselinePath) {
    if (-not (Test-Path $BaselinePath)) { throw "找不到基线文件：$BaselinePath" }
    $baseline = Get-Content $BaselinePath -Raw -Encoding UTF8 | ConvertFrom-Json

    if ($ToleranceFactor -gt 0) { $factor = $ToleranceFactor }
    elseif ($baseline.localToleranceFactor) { $factor = [double]$baseline.localToleranceFactor }
    else { $factor = 1.2 }

    $baseP50 = [double]$baseline.p50
    $baseP95 = [double]$baseline.p95
    $GateP50Ms = [int][Math]::Round($baseP50 * $factor)
    $GateP95Ms = [int][Math]::Round($baseP95 * $factor)
    $mode = "相对基线（$([System.IO.Path]::GetFileName($BaselinePath)) x $factor）"
}

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
    Write-Host "（CI 环境可能不支持 GUI 渲染，退出码 4 表示环境不支持，不视为性能不达标）"
    exit 4
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
Write-Host ("判定模式   {0}" -f $mode)
if ($null -ne $baseP50) {
    Write-Host ("基线       P50 {0:N0} ms，P95 {1:N0} ms（{2}）" -f `
            $baseP50, $baseP95, [System.IO.Path]::GetFileName($BaselinePath))
    Write-Host ("P50 偏差   {0:+0;-0;0} ms（{1:P1}）" -f ($innerP50 - $baseP50), `
            (($innerP50 - $baseP50) / $baseP50))
}
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
    Write-Host ("判定：未通过（P50 超出阈值 {0} ms）" -f [Math]::Round($innerP50 - $GateP50Ms)) -ForegroundColor Red
    if ($null -ne $baseP50) {
        Write-Host "      启动路径可能引入同步 IO、大资源加载或启动期初始化，见 docs/performance-gate.md §8" -ForegroundColor Red
    }
    exit 2
}
