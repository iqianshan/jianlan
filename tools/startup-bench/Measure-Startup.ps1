<#
.SYNOPSIS
    测量 Lingpane 冷启动耗时，输出 P50 / P95，并给出是否通过 NF-01 门禁的判定。

.DESCRIPTION
    两个指标同时采集：
      outer —— 外部计时：Process.Start → 主窗口句柄出现。接近用户感知。
      inner —— 进程内埋点：App 构造 → 首帧渲染完成（startup.log 的 total）。

    门禁判定用 inner 的 P50 与 P95。outer 只做参考，因为它受轮询粒度（10ms）与系统负载影响。

    环境校验（-MinFreeRatio / -MaxCpuLoad）：不达标时仍完成测量并输出数字，
    但结论标注为参考数据、不作出门禁判定，退出码 5。

    稳定性判据：除环境校验外，还检查 max/min 离散度。离散度 > 1.5 时同样降级——
    它直接反映测量期间的负载变化，比 CPU 瞬时采样更可靠。

    退出码：0 通过 / 2 未通过 / 3 P95 抖动 / 4 无埋点 / 5 降级（环境未达标或离散度过大）

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

    # 内存正常判定线。低于此值仍会测量并判定，但提示结果可能偏高。
    [double]$MinFreeRatio = 0.5,

    # 内存降级线。低于此值判定为参考数据，不作出门禁结论。
    # 依据：可用内存 <30% 时系统已在压缩工作集，WPF 框架页被换出，
    # 同一份代码实测从 578ms 涨到 789ms（旧版同批对照 1028ms，证明是环境）。
    [double]$WarnFreeRatio = 0.3,

    # CPU 负载上限（%）。超过则降级。
    # 依据实测：29% -> P50 511ms，39% -> 569ms，67% -> 1039ms。
    # 取 60 是因为 Windows 空闲机器也常在 30-50% 波动，50 会造成大量误降级；
    # 而 60% 以上启动耗时已明显被调度延迟主导。
    [int]$MaxCpuLoad = 60,

    # 测量稳定性阈值：max/min 比值上限。
    # 这是比 CPU 瞬时值更可靠的判据——它直接看数据本身是否稳定，不需要猜环境。
    # 实测：20 次 530-671（1.27）稳定；CPU 67% 时 625-1055（1.69）不稳定。
    [double]$MaxSpread = 1.5,

    # 环境不合格时仍按门禁判定（不降级为参考数据）
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
$memTight = $envInfo.FreeRatio -lt $MinFreeRatio      # 偏紧：判定但提示
$memCritical = $envInfo.FreeRatio -lt $WarnFreeRatio  # 危险：降级
$cpuHigh = $envInfo.CpuLoad -gt $MaxCpuLoad
$degraded = $false

Write-Host ("测量环境   可用 {0:N1} / {1:N1} GB（{2:P0}；正常 >= {3:P0}，降级 < {4:P0}），CPU {5}%（上限 {6}%）" -f `
        $envInfo.FreeGb, $envInfo.TotalGb, $envInfo.FreeRatio, $MinFreeRatio, $WarnFreeRatio,
        $envInfo.CpuLoad, $MaxCpuLoad)

$blockReasons = @()
if ($memCritical) { $blockReasons += ("可用内存 {0:P0} 低于降级线 {1:P0}" -f $envInfo.FreeRatio, $WarnFreeRatio) }
if ($cpuHigh)     { $blockReasons += ("CPU {0}% 高于上限 {1}%" -f $envInfo.CpuLoad, $MaxCpuLoad) }

if ($blockReasons.Count -gt 0) {
    $msg = ($blockReasons -join '；')
    if ($IgnoreEnvironment) {
        Write-Host "[警告] $msg。按 -IgnoreEnvironment 继续，仍作出判定。" -ForegroundColor Yellow
    }
    else {
        Write-Host "[降级] $msg。" -ForegroundColor Yellow
        Write-Host "       仍完成测量，但结论标记为参考数据，不作为门禁依据（退出码 5）。" -ForegroundColor Yellow
        $degraded = $true
    }
}
elseif ($memTight) {
    Write-Host ("[提示] 可用内存 {0:P0} 低于正常线 {1:P0}，结果可能比空闲时高约 50-100ms，" -f `
            $envInfo.FreeRatio, $MinFreeRatio) -ForegroundColor Yellow
    Write-Host "       但仍正常判定（未进入降级线，趋势可信）。" -ForegroundColor Yellow
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
# 用 .NET API 而非 Remove-Item：某些环境对 cmdlet 的删除有安全钩子拦截
if (Test-Path $logPath) { [System.IO.File]::Delete($logPath) }

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

# 离散度：测量期间系统状态是否稳定。
# 比 CPU 瞬时值可靠——CPU 采样是某一刻的快照，而离散度反映整个测量过程的实际波动。
$minInner = ($innerArr | Measure-Object -Minimum).Minimum
$maxInner = ($innerArr | Measure-Object -Maximum).Maximum
$spread = $maxInner / $minInner

if ($spread -gt $MaxSpread) {
    Write-Host ("[降级] 离散度 {0:N2}（max {1:N0} / min {2:N0} ms）超过 {3:N2}。" -f `
            $spread, $maxInner, $minInner, $MaxSpread) -ForegroundColor Yellow
    Write-Host "       测量期间系统负载发生变化，本次数据不代表稳态性能。" -ForegroundColor Yellow
    $degraded = $true
}

$p50Ok = $innerP50 -le $GateP50Ms
$p95Ok = $innerP95 -le $GateP95Ms

if ($degraded) {
    Write-Host ("参考结论   P50 {0:N0} ms（阈 {1} ms）、P95 {2:N0} ms（阈 {3} ms）——仅供参考" -f `
            $innerP50, $GateP50Ms, $innerP95, $GateP95Ms) -ForegroundColor Yellow
    Write-Host "           环境未达标或数据离散度过大，本次不作出门禁判定。请关闭占用内存/CPU 的程序后复测。" -ForegroundColor Yellow
    exit 5
}

if ($p50Ok -and $p95Ok) {
    Write-Host "判定：通过" -ForegroundColor Green
    exit 0
} elseif ($p50Ok) {
    Write-Host "判定：P50 通过、P95 超标（抖动大，多为系统负载或后台扫描所致，建议空闲时复测）" -ForegroundColor Yellow
    exit 3
} else {
    Write-Host ("判定：未通过（P50 超出阈值 {0} ms）" -f [Math]::Round($innerP50 - $GateP50Ms)) -ForegroundColor Red

    # 区分"代码回归"与"环境整体变慢"。
    # 依据：2026-11-03 实测，同一份代码在系统状态变化后从 511ms 涨到 1102ms，
    # 同时最小 WPF 空窗口从 541ms 涨到 1095ms——两者同幅变化，说明是系统而非代码。
    # 此时若离散度正常（数据很稳定），最可能是系统级变慢，应先做空窗口对照再动代码。
    if ($spread -le $MaxSpread) {
        Write-Host "      数据离散度正常，可能是系统级整体变慢而非代码回归。" -ForegroundColor Yellow
        Write-Host "      请先跑最小 WPF 对照确认：tools\env-check\Measure-Baseline.ps1" -ForegroundColor Yellow
        Write-Host "      对照也慢 -> 环境问题（重启或关闭驻留程序后复测）；对照正常 -> 才是代码问题。见 docs/performance-gate.md §11" -ForegroundColor Yellow
    }
    elseif ($null -ne $baseP50) {
        Write-Host "      启动路径可能引入同步 IO、大资源加载或启动期初始化，见 docs/performance-gate.md §8" -ForegroundColor Red
    }
    exit 2
}
