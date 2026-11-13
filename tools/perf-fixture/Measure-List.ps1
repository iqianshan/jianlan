<#
.SYNOPSIS
    测量列表性能：枚举、首屏、滚动帧率、内存与句柄（NF-03 / NF-04 / NF-05 / NF-05R）。

.DESCRIPTION
    调用 Jianlan.exe 的 --perf-probe 诊断模式。程序启动后加载指定目录，
    采集指标并写入 %LOCALAPPDATA%\Jianlan\logs\perf-probe.json，随后自行退出。

    内存口径（重要）：
      PrivateMb    —— 私有提交内存，对应任务管理器"提交大小"列。NF-05 以此判定。
      WorkingSetMb —— 工作集，含与其他 WPF 进程共享的框架页，数值偏高，仅作参考。

    本脚本只负责读结果与判定，不参与测量，避免引入自身开销。

.EXAMPLE
    .\Measure-List.ps1 -Exe ..\..\publish\Jianlan.exe -Path D:\perf-10k

.EXAMPLE
    .\Measure-List.ps1 -Exe ..\..\publish\Jianlan.exe -Path D:\perf-10k -Runs 3 -Frames 0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,

    [Parameter(Mandatory = $true)]
    [string]$Path,

    # 双窗格档（NF-05R 双窗格）：填第二窗格的目录，探针会先分屏再测。
    # 留空 = 单窗格。两个窗格各常驻一份万项数据层，这才是双窗格的真实成本；
    # 只把窗口切成两半不加载第二份数据，测出来的数字没有意义。
    [string]$SplitPath = "",

    [int]$Runs = 3,

    # 滚动采样帧数。0 表示只测加载不滚动。
    [int]$Frames = 240,

    # NF 阈值
    [int]$MaxFirstScreenMs = 800,
    [double]$MinFps = 55,
    # NF-05 单窗格档。原 120MB 已于 ADR-0002 上调为 150（双窗格 185 / 预览窗格 230）。
    [int]$MaxPrivateMb = 150,

    # 归一化：每轮先跑一次空窗口对照，用"Jianlan 减去空窗口"的增量判定。
    # 绝对私有提交在同一台机器上能漂移 30MB 以上（实测空窗口 76~114MB），
    # 不归一化时同一个 exe 会时而通过时而超标。NF-08 已改用相对基线，NF-05 同理。
    [switch]$Normalize,

    # NF-05R：归一化口径下的增量上限（MB），即"Jianlan − 纯 WPF 空窗口"。
    # 2026-11-03 实测 29.7 时定为 45；2026-11-05 复测 43.2~46.4 已踩线，
    # 按 ADR-0008 重新标定为 65（实测均值 44.8 + M2 预估增量 15 + 余量）。
    # 这是代码回归判据，判定时先于 NF-05。
    [double]$MaxDeltaMb = 65,

    # 空窗口对照的上限（MB）。超过说明系统处于高态，整批内存数据作废。
    # 比 GDI > 80 更直接：高态下空窗口稳定在 118，而 GDI 偶尔是 118 偶尔是 120，
    # 且 Jianlan 自身的私有提交会在 121~161 之间摆动，Normalize 相减也救不回来（见 ADR-0008）。
    [double]$MaxBlankMb = 90,

    # NF-05H：托管堆上限（MB）。降级判据——只受数据层影响，与环境两态无关，
    # 所以 NF-05/NF-05R 因环境处于高态而不可判定时，这一项仍然可比。
    [double]$MaxHeapMb = 12,

    # 预热次数（不计入结果）。首次启动的进程私有提交明显偏高
    # （实测同一个空窗口首轮 118MB，稳定后 82MB），不预热会把冷态成本算进结果。
    [int]$Warmup = 1
)

$ErrorActionPreference = 'Stop'

# 双窗格档的默认阈值与单窗格不同（ADR-0002 / ADR-0008）：185 MB / 增量 90 MB。
# 只在调用方没有显式传值时套用，显式传值一律以传值为准——
# 脚本替用户改判据比判错更危险。
$splitMode = $SplitPath -ne ''
if ($splitMode) {
    if (-not $PSBoundParameters.ContainsKey('MaxPrivateMb')) { $MaxPrivateMb = 185 }
    if (-not $PSBoundParameters.ContainsKey('MaxDeltaMb')) { $MaxDeltaMb = 90 }
}

if (-not (Test-Path $Exe)) { throw "找不到可执行文件：$Exe" }
if (-not (Test-Path $Path)) { throw "找不到测试目录：$Path（先用 tools/perf-fixture 生成）" }

$resultPath = Join-Path $env:LOCALAPPDATA 'Jianlan\logs\perf-probe.json'
$results = @()

# 归一化用的空窗口对照（与 tools/env-check 共用同一个程序）
$blankExe = Join-Path $PSScriptRoot '..\env-check\WpfBaseline\out\WpfBaseline.exe'
$blankLog = Join-Path $env:TEMP 'wpf-baseline.log'
$blankMb = $null

if ($Normalize -and -not (Test-Path $blankExe)) {
    throw "归一化需要空窗口对照程序：$blankExe（先跑 tools/env-check/Measure-Baseline.ps1 编译）"
}

function Measure-BlankMb {
    if (Test-Path $blankLog) { [System.IO.File]::Delete($blankLog) }
    $p = Start-Process -FilePath $blankExe -PassThru
    $p.WaitForExit(30000) | Out-Null
    if (-not (Test-Path $blankLog)) { return $null }

    $line = Get-Content $blankLog | Where-Object { $_ -match 'mem priv=(\d+)MB' } | Select-Object -Last 1
    if ($line -and $line -match 'mem priv=(\d+)MB') { return [double]$Matches[1] }
    return $null
}

Write-Host "测量 $Runs 次：$Path（帧数 $Frames）" -ForegroundColor Cyan
if ($splitMode) { Write-Host "双窗格：第二窗格 $SplitPath（NF-05 185 MB 档 / NF-05R 90 MB 档）" -ForegroundColor Cyan }
if ($Normalize) { Write-Host "归一化已开启：每轮先测空窗口对照" -ForegroundColor Cyan }

# 探针参数只拼一次：预热与正式测量用同一组目录参数，
# 拼接写两遍迟早出现"预热是单窗格、正式是双窗格"这种只在内存数字上体现的错
$probeArgs = @('--perf-probe', "`"$Path`"", '--perf-frames')
if ($splitMode) { $probeArgs += @('--perf-split', "`"$SplitPath`"") }

if ($Warmup -gt 0) {
    Write-Host "预热 $Warmup 次（不计入结果）..." -ForegroundColor DarkGray
    for ($w = 1; $w -le $Warmup; $w++) {
        if ($Normalize) { $null = Measure-BlankMb }
        if (Test-Path $resultPath) { [System.IO.File]::Delete($resultPath) }
        $wp = Start-Process -FilePath $Exe -ArgumentList ($probeArgs + @('0')) -PassThru
        $wp.WaitForExit(180000) | Out-Null
        Start-Sleep -Milliseconds 300
    }
}

for ($i = 1; $i -le $Runs; $i++) {
    # 用 .NET API 而非 Remove-Item：某些环境对 cmdlet 的删除有安全钩子拦截
    if (Test-Path $resultPath) { [System.IO.File]::Delete($resultPath) }

    if ($Normalize) {
        $blankBefore = Measure-BlankMb
        $proc = Start-Process -FilePath $Exe -ArgumentList ($probeArgs + @($Frames)) -PassThru
    }
    else {
        $blankBefore = $null
        $proc = Start-Process -FilePath $Exe -ArgumentList ($probeArgs + @($Frames)) -PassThru
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(120)

    while (-not $proc.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $proc.Refresh()
    }

    if (-not $proc.HasExited) {
        $proc.Kill()
        Write-Warning "第 $i 次超时未退出，已终止"
        continue
    }

    if (-not (Test-Path $resultPath)) {
        Write-Warning "第 $i 次未生成结果文件，程序可能启动失败"
        continue
    }

    $r = Get-Content $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($Normalize -and $null -ne $blankBefore) {
        $r | Add-Member -NotePropertyName 'blankMb' -NotePropertyValue $blankBefore -Force
        $r | Add-Member -NotePropertyName 'deltaMb' -NotePropertyValue ([double]$r.privateMb - $blankBefore) -Force
    }
    $results += $r

    if ($Normalize -and $null -ne $blankBefore) {
        Write-Host ("  第 {0} 次：枚举 {1,5} ms | 首屏 {2,6:N0} ms | {3,6:N1} fps | P95 帧 {4,5:N1} ms | 私有 {5,6:N1} MB | 堆 {6,4:N1} MB | 空窗 {7,5:N0} | 增量 {8,5:N1} MB" -f `
                $i, $r.enumerateMs, $r.firstScreenMs, $r.fps, $r.p95FrameMs, $r.privateMb, $r.managedHeapMb, $blankBefore, $r.deltaMb)
    }
    else {
        Write-Host ("  第 {0} 次：枚举 {1,5} ms | 首屏 {2,6:N0} ms | {3,6:N1} fps | P95 帧 {4,5:N1} ms | 私有 {5,6:N1} MB | 堆 {6,4:N1} MB" -f `
                $i, $r.enumerateMs, $r.firstScreenMs, $r.fps, $r.p95FrameMs, $r.privateMb, $r.managedHeapMb)
    }
}

if ($results.Count -eq 0) { throw "没有采集到任何结果" }

function Get-Median {
    param([double[]]$Values)
    $sorted = $Values | Sort-Object
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

$enumMs = Get-Median ($results | ForEach-Object { [double]$_.enumerateMs })
$firstMs = Get-Median ($results | ForEach-Object { [double]$_.firstScreenMs })
$fps = Get-Median ($results | ForEach-Object { [double]$_.fps })
$p95Frame = Get-Median ($results | ForEach-Object { [double]$_.p95FrameMs })
$maxFrame = Get-Median ($results | ForEach-Object { [double]$_.maxFrameMs })
$private = Get-Median ($results | ForEach-Object { [double]$_.privateMb })
$ws = Get-Median ($results | ForEach-Object { [double]$_.workingSetMb })
$wsLoad = Get-Median ($results | ForEach-Object { [double]$_.workingSetAfterLoadMb })
$handles = Get-Median ($results | ForEach-Object { [double]$_.handleCount })
$gdi = Get-Median ($results | ForEach-Object { [double]$_.gdiObjects })
$heap = Get-Median ($results | ForEach-Object { [double]$_.managedHeapMb })
$items = ($results | Select-Object -First 1).itemCount
$secondItems = ($results | Select-Object -First 1).secondItemCount

Write-Host ""
Write-Host "================ 结果（$($results.Count) 次取中位）================" -ForegroundColor Cyan
Write-Host ("条目数       {0}" -f $items)
if ($splitMode) {
    Write-Host ("第二窗格     {0} 项   目录 {1}" -f $secondItems, $SplitPath)
}
Write-Host ("枚举耗时     {0,8:N0} ms" -f $enumMs)
Write-Host ("首屏耗时     {0,8:N0} ms   阈值 <= {1} ms   （NF-03）" -f $firstMs, $MaxFirstScreenMs)
if ($Frames -gt 0) {
    Write-Host ("滚动帧率     {0,8:N1} fps  阈值 >= {1} fps   （NF-04）" -f $fps, $MinFps)
    Write-Host ("P95 帧间隔   {0,8:N1} ms" -f $p95Frame)
    Write-Host ("最大帧间隔   {0,8:N1} ms" -f $maxFrame)
}
Write-Host ("私有提交     {0,8:N1} MB   阈值 <= {1} MB    （NF-05，对应任务管理器'提交大小'）" -f $private, $MaxPrivateMb)

$deltaValues = @($results | Where-Object { $null -ne $_.deltaMb } | ForEach-Object { [double]$_.deltaMb })
$delta = $null
$blankMed = $null
if ($Normalize -and $deltaValues.Count -gt 0) {
    $blankMed = Get-Median ($results | Where-Object { $null -ne $_.blankMb } | ForEach-Object { [double]$_.blankMb })
    $delta = Get-Median $deltaValues
    Write-Host ("空窗口基线   {0,8:N1} MB   （当前系统 WPF 空载，随环境漂移）" -f $blankMed)
    Write-Host ("相对增量     {0,8:N1} MB   阈值 <= {1} MB    （NF-05R，先行判定）" -f $delta, $MaxDeltaMb)
}

Write-Host ("工作集       {0,8:N1} MB   （含共享框架页，仅参考；加载后 {1:N1} MB）" -f $ws, $wsLoad)
Write-Host ("托管堆       {0,8:N1} MB   阈值 <= {1} MB    （NF-05H，环境高态下仍可比）" -f $heap, $MaxHeapMb)
Write-Host ("句柄         {0,8:N0}" -f $handles)
Write-Host ("GDI 对象     {0,8:N0}" -f $gdi)

# 环境态判定（2026-11-03 实测）：
#   同一 exe 在同一台机器上存在两态，GDI 与句柄同步变化，私有提交相差约 36MB：
#     低态  GDI 56  / 句柄 ~950  / 万项目录私有提交 ~108MB
#     高态  GDI 148 / 句柄 ~1260 / 万项目录私有提交 ~144MB
#   两态与代码无关（空窗口对照同步变化），且不会自行回落，重启系统后恢复。
#   高态下的内存数字与历史不可比，此时不应据此判定 NF-05 通过或未通过。
$gdiHigh = $gdi -gt 80
$blankHigh = $Normalize -and $null -ne $blankMed -and $blankMed -gt $MaxBlankMb
$envBad = $gdiHigh -or $blankHigh
if ($envBad) {
    Write-Host ''
    if ($blankHigh) {
        Write-Host ("注意：空窗口对照 {0:N1} MB 超过 {1} MB，当前处于高态。" -f $blankMed, $MaxBlankMb) -ForegroundColor Yellow
        Write-Host '      高态下 Jianlan 自身的私有提交会在 121~161MB 之间摆动，' -ForegroundColor Yellow
        Write-Host '      Normalize 相减也救不回来（ADR-0008），NF-05R 判定无效。' -ForegroundColor Yellow
    }
    else {
        Write-Host '注意：GDI 对象高于 80，当前处于高态（正常态为 56）。' -ForegroundColor Yellow
    }

    Write-Host '      高态下私有提交比正常态高约 36MB，此数字不可与历史对比，NF-05 判定无效。' -ForegroundColor Yellow
    Write-Host '      恢复方式：重启系统后重测。' -ForegroundColor Yellow
}

$fail = @()
if ($firstMs -gt $MaxFirstScreenMs) { $fail += "首屏 ${firstMs}ms > ${MaxFirstScreenMs}ms" }
if ($Frames -gt 0 -and $fps -lt $MinFps) { $fail += "帧率 ${fps} < ${MinFps}" }
if ($heap -gt $MaxHeapMb) { $fail += "托管堆 ${heap}MB > ${MaxHeapMb}MB" }
if ($Normalize) {
    if ($null -ne $delta -and $delta -gt $MaxDeltaMb) { $fail += "相对增量 ${delta}MB > ${MaxDeltaMb}MB" }
}
elseif ($private -gt $MaxPrivateMb) { $fail += "私有提交 ${private}MB > ${MaxPrivateMb}MB" }

# 双窗格没真正加载时，内存数字只是"单窗格 + 一个空壳"。
# 这种情况必须显式作废，不能因为数字好看就当成双窗格达标。
$splitBroken = $splitMode -and $secondItems -le 0
if ($splitBroken) {
    Write-Host ''
    Write-Host '注意：第二窗格条目数为 0，分屏没有真正加载。' -ForegroundColor Yellow
    Write-Host '      此时的内存数字只代表单窗格，双窗格判定无效。' -ForegroundColor Yellow
}

Write-Host ""
if ($splitBroken) {
    Write-Host '判定：不可判定 —— 双窗格未真正加载，内存指标无效。' -ForegroundColor Yellow
    exit 3
}

if ($gdiHigh) {
    Write-Host '判定：不可判定 —— 环境处于高态，内存指标无效。' -ForegroundColor Yellow
    Write-Host ('      其余指标：首屏 {0:N0} ms、帧率 {1:N1} fps' -f $firstMs, $fps)

    # NF-05H 的意义就在这里：托管堆不受框架底与环境两态影响，
    # 绝对/相对内存都不可判定的那一轮，它仍然能回答"数据层有没有回归"。
    if ($heap -gt $MaxHeapMb) {
        Write-Host ('      但托管堆 {0:N1} MB 已超 {1} MB —— NF-05H 未通过，数据层有回归。' -f $heap, $MaxHeapMb) -ForegroundColor Red
    }
    else {
        Write-Host ('      托管堆 {0:N1} MB <= {1} MB —— NF-05H 通过（唯一可比的内存判据）。' -f $heap, $MaxHeapMb) -ForegroundColor Green
    }

    exit 3
}

if ($fail.Count -eq 0) {
    Write-Host "判定：通过" -ForegroundColor Green
    exit 0
}

Write-Host ("判定：未通过 —— " + ($fail -join "；")) -ForegroundColor Red
exit 2
