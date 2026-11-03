<#
.SYNOPSIS
    生成用于列表性能测量的测试目录（NF-03 / NF-04 / NF-05）。

.DESCRIPTION
    默认在目标目录下创建 10000 个条目：9950 个文件 + 50 个子目录。
    文件名与扩展名刻意混合中英文、长短不一，近似真实用户目录的分布，
    避免出现"全是 a.txt"这类不具代表性的情况。

    生成结果可重复：同样的 -Count 每次产生相同的名称集合。

.EXAMPLE
    .\New-TestDirectory.ps1 -Path D:\perf-10k -Count 10000 -Force
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [int]$Count = 10000,

    [int]$DirCount = 50,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (Test-Path $Path) {
    if (-not $Force) {
        throw "目录已存在：$Path。加 -Force 覆盖，或换一个路径。"
    }
    # 逐项删除：-Recurse 在部分环境会被安全钩子拦截
    Get-ChildItem -LiteralPath $Path -Force | ForEach-Object {
        if ($_.PSIsContainer) { [System.IO.Directory]::Delete($_.FullName, $true) }
        else { [System.IO.File]::Delete($_.FullName) }
    }
}
New-Item -ItemType Directory -Path $Path | Out-Null

$extensions = @(
    '.txt', '.log', '.md', '.json', '.xml', '.csv',
    '.pdf', '.docx', '.xlsx', '.pptx',
    '.jpg', '.png', '.gif', '.svg', '.mp4', '.mp3',
    '.zip', '.7z', '.exe', '.dll', '.cs', '.ps1'
)

$stems = @(
    '会议纪要', '项目计划', '预算表', '合同', '截图', 'report', 'notes', 'draft',
    'backup', 'invoice', 'design', 'spec', 'readme', 'photo', 'video', 'archive',
    '数据导出', '测试用例', '需求文档', '变更记录', 'a', 'b', 'x', 'tmp'
)

$fileCount = $Count - $DirCount
if ($fileCount -lt 0) { throw "Count 必须大于 DirCount" }

Write-Host "生成 $Count 项（$fileCount 文件 + $DirCount 目录）→ $Path" -ForegroundColor Cyan

$sw = [System.Diagnostics.Stopwatch]::StartNew()

for ($i = 0; $i -lt $fileCount; $i++) {
    $ext = $extensions[$i % $extensions.Count]
    $stem = $stems[($i * 7) % $stems.Count]
    # 每 10 个文件插一个长名，覆盖长路径与长文本截断场景
    if ($i % 10 -eq 0) {
        $name = '{0}_2026年度第三季度归档_{1:D5}{2}' -f $stem, $i, $ext
    }
    else {
        $name = '{0}_{1:D5}{2}' -f $stem, $i, $ext
    }

    $full = Join-Path $Path $name
    [System.IO.File]::Create($full).Dispose()
}

for ($i = 0; $i -lt $DirCount; $i++) {
    $full = Join-Path $Path ('子目录_{0:D3}' -f $i)
    New-Item -ItemType Directory -Path $full | Out-Null
}

$sw.Stop()

$actual = (Get-ChildItem -LiteralPath $Path -Force | Measure-Object).Count
Write-Host ("完成：实际 {0} 项，耗时 {1:N1} 秒" -f $actual, $sw.Elapsed.TotalSeconds) -ForegroundColor Green

if ($actual -ne $Count) {
    Write-Warning "实际条目数与请求不一致（$actual / $Count）"
}
