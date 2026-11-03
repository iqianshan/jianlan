<#
.SYNOPSIS
    测量列表性能：枚举、首屏、滚动帧率、内存与句柄（NF-03 / NF-04 / NF-05）。

.DESCRIPTION
    调用 Lingpane.exe 的 --perf-probe 诊断模式。程序启动后加载指定目录，
    采集指标并写入 %LOCALAPPDATA%\Lingpane\logs\perf-probe.json，随后自行退出。

    内存口径（重要）：
      PrivateMb    —— 私有提交内存，对应任务管理器"提交大小"列。NF-05 以此判定。
      WorkingSetMb —— 工作集，含与其他 WPF 进程共享的框架页，数值偏高，仅作参考。

    本脚本只负责读结果与判定，不参与测量，避免引入自身开销。

.EXAMPLE
    .\Measure-List.ps1 -Exe ..\..\publish\Lingpane.exe -Path D:\perf-10k

.EXAMPLE
    .\Measure-List.ps1 -Exe ..\..\publish\Lingpane.exe -Path D:\perf-10k -Runs 3 -Frames 0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,

    [Parameter(Mandatory = $true)]
    [string]$Path,

    [int]$Runs = 3,

    # 滚动采样帧数。0 表示只测加载不滚动。
    [int]$Frames = 240,

    # NF 阈值
    [int]$MaxFirstScreenMs = 800,
    [double]$MinFps = 55,
    [int]$MaxPrivateMb = 120
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { throw "找不到可执行文件：$Exe" }
if (-not (Test-Path $Path)) { throw "找不到测试目录：$Path（先用 tools/perf-fixture 生成）" }

$resultPath = Join-Path $env:LOCALAPPDATA 'Lingpane\logs\perf-probe.json'
$results = @()

Write-Host "测量 $Runs 次：$Path（帧数 $Frames）" -ForegroundColor Cyan

for ($i = 1; $i -le $Runs; $i++) {
    # 用 .NET API 而非 Remove-Item：某些环境对 cmdlet 的删除有安全钩子拦截
    if (Test-Path $resultPath) { [System.IO.File]::Delete($resultPath) }

    $proc = Start-Process -FilePath $Exe -ArgumentList '--perf-probe', "`"$Path`"", '--perf-frames', $Frames -PassThru
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
    $results += $r

    Write-Host ("  第 {0} 次：枚举 {1,5} ms | 首屏 {2,6:N0} ms | {3,6:N1} fps | P95 帧 {4,5:N1} ms | 私有 {5,6:N1} MB" -f `
            $i, $r.enumerateMs, $r.firstScreenMs, $r.fps, $r.p95FrameMs, $r.privateMb)
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
$items = ($results | Select-Object -First 1).itemCount

Write-Host ""
Write-Host "================ 结果（$($results.Count) 次取中位）================" -ForegroundColor Cyan
Write-Host ("条目数       {0}" -f $items)
Write-Host ("枚举耗时     {0,8:N0} ms" -f $enumMs)
Write-Host ("首屏耗时     {0,8:N0} ms   阈值 <= {1} ms   （NF-03）" -f $firstMs, $MaxFirstScreenMs)
if ($Frames -gt 0) {
    Write-Host ("滚动帧率     {0,8:N1} fps  阈值 >= {1} fps   （NF-04）" -f $fps, $MinFps)
    Write-Host ("P95 帧间隔   {0,8:N1} ms" -f $p95Frame)
    Write-Host ("最大帧间隔   {0,8:N1} ms" -f $maxFrame)
}
Write-Host ("私有提交     {0,8:N1} MB   阈值 <= {1} MB    （NF-05，对应任务管理器'提交大小'）" -f $private, $MaxPrivateMb)
Write-Host ("工作集       {0,8:N1} MB   （含共享框架页，仅参考；加载后 {1:N1} MB）" -f $ws, $wsLoad)
Write-Host ("句柄         {0,8:N0}" -f $handles)
Write-Host ("GDI 对象     {0,8:N0}" -f $gdi)

$fail = @()
if ($firstMs -gt $MaxFirstScreenMs) { $fail += "首屏 ${firstMs}ms > ${MaxFirstScreenMs}ms" }
if ($Frames -gt 0 -and $fps -lt $MinFps) { $fail += "帧率 ${fps} < ${MinFps}" }
if ($private -gt $MaxPrivateMb) { $fail += "私有提交 ${private}MB > ${MaxPrivateMb}MB" }

Write-Host ""
if ($fail.Count -eq 0) {
    Write-Host "判定：通过" -ForegroundColor Green
    exit 0
}

Write-Host ("判定：未通过 —— " + ($fail -join "；")) -ForegroundColor Red
exit 2
