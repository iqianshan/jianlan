# 第三方依赖清单校验（AC-10 / docs/about-page.md §五）
#
# 干什么：把 src/Lingpane.App/obj/project.assets.json 里"真的会进产物"的包，
#        逐个到 THIRD-PARTY-NOTICES.md 里找出处；找不到就失败。
#
# 为什么是"校验"而不是"生成"：清单里除了包名与版本，还有"这个包为什么能再分发"
# 这样需要人判断的段落（例如 Windows SDK 投影走的是 SDK 条款而不是 MIT）。
# 让脚本整篇重写，等于把那些判断覆盖成一句机器猜出来的许可证名——比漏登记更糟。
# 脚本负责"漏登记一定失败"，人负责"登记得对不对"。
#
# 用法：
#   pwsh -File tools/licenses/check-third-party.ps1
# 退出码：0 = 一致；1 = 有包没登记或合规文件缺失。

[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot 在 param 默认值里拿不到（-File 调用时还没赋值），必须放到函数体里算
if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$assets = Join-Path $RepoRoot 'src\Lingpane.App\obj\project.assets.json'
$notices = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'

foreach ($required in @($assets, $notices,
        (Join-Path $RepoRoot 'LICENSE'),
        (Join-Path $RepoRoot 'NOTICE'))) {
    if (-not (Test-Path $required)) {
        Write-Host "[FAIL] 缺少文件：$required" -ForegroundColor Red
        exit 1
    }
}

$text = Get-Content -Path $notices -Raw -Encoding UTF8

$json = Get-Content -Path $assets -Raw -Encoding UTF8 | ConvertFrom-Json

# targets 里每个目标框架下是一堆 "包名/版本" 条目，type=package 的才是 NuGet 包，
# type=project 的是本项目引用；只看前者，且要有 runtime 段（真的会进产物）
$packages = @{}

foreach ($framework in $json.targets.PSObject.Properties) {
    foreach ($entry in $framework.Value.PSObject.Properties) {
        if ($entry.Value.type -ne 'package') { continue }
        if (-not $entry.Value.runtime) { continue }

        $slash = $entry.Name.IndexOf('/')
        if ($slash -le 0) { continue }

        $id = $entry.Name.Substring(0, $slash)
        $version = $entry.Name.Substring($slash + 1)

        # 运行时与框架包由用户机器上的 .NET 提供，不随包分发（清单里另有说明）
        if ($id -like 'Microsoft.NETCore.App*' -or $id -like 'Microsoft.WindowsDesktop.App*' -or
            $id -like 'Microsoft.AspNetCore.App*') {
            continue
        }

        $packages["$id/$version"] = $true
    }
}

$missing = @()

foreach ($key in ($packages.Keys | Sort-Object)) {
    $id = $key.Split('/')[0]

    # 判据：清单里出现包名即可。版本号会随升级变化，人写的那一行应该跟着改，
    # 但"包名在不在"才是"有没有登记"的分界线——版本不一致单独提示。
    if ($text -notmatch [regex]::Escape($id)) {
        $missing += $key
        continue
    }

    $version = $key.Split('/', 2)[1]
    if ($text -notmatch [regex]::Escape($version)) {
        Write-Host "[WARN] $id 已登记，但清单里没有版本 $version（升级后忘了改？）" -ForegroundColor Yellow
    }
}

# ── 第二道：发布目录里的程序集 ────────────────────────────────────────────
#
# assets.json 只看得见 PackageReference。本项目真正的第三方程序集来自
# 目标框架的隐式引用（Microsoft.Windows.SDK.NET.dll / WinRT.Runtime.dll），
# 它们在 assets 里没有 package 条目，只出现在 publish 目录里。
# 所以产物存在时，再按"文件名必须在清单里出现"扫一遍。
$publish = Join-Path $RepoRoot 'publish'

if (Test-Path $publish) {
    $foreign = Get-ChildItem -Path $publish -Filter *.dll -File |
        Where-Object { $_.BaseName -notlike 'Lingpane*' } |
        Where-Object { $text -notmatch [regex]::Escape($_.Name) } |
        ForEach-Object { $_.Name }

    foreach ($name in $foreign) {
        $missing += "publish/$name"
    }

    Write-Host "扫描发布目录：$(@(Get-ChildItem -Path $publish -Filter *.dll -File).Count) 个程序集" -ForegroundColor Cyan
}
else {
    Write-Host '未找到 publish 目录，跳过产物扫描（CI 里在 publish 之后运行）' -ForegroundColor Yellow
}

if ($missing.Count -gt 0) {
    Write-Host '[FAIL] 下列内容会进产物，但没有登记到 THIRD-PARTY-NOTICES.md：' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "       $_" -ForegroundColor Red }
    Write-Host '       合规上"忘了写"等同于"没有许可"，补上再提交。' -ForegroundColor Red
    exit 1
}

Write-Host "[OK] NuGet 运行时包 $($packages.Count) 个、发布目录程序集均已登记；LICENSE / NOTICE / 清单三份文件齐备" -ForegroundColor Green
exit 0
