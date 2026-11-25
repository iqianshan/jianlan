param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [string]$ListId = ""
)

# Read-only snapshot of the running main window: every List control with its rows
# (name, rect, selected) plus the breadcrumb path. Used to diagnose
# "clicked Downloads but the right-hand list did not change" on a live instance
# without touching it: no clicks, no keys, no activation.
#
# ASCII only on purpose: the 5.1 host mis-reads UTF-8 files without BOM.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Row names are Chinese on a Chinese system; without this the JSON comes out in
# the console code page and the caller (python, utf-8) reads mojibake.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

$root = $AE::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $TargetPid)

$win = $null
$best = 0
foreach ($w in $root.FindAll($TS::Children, $pidCond)) {
    if ($w.Current.IsOffscreen) { continue }
    $r = $w.Current.BoundingRectangle
    $area = $r.Width * $r.Height
    if ($area -gt $best) { $best = $area; $win = $w }
}
if ($null -eq $win) { Write-Output '{"ok":false,"error":"window"}'; exit 1 }

function Sel-Of($el) {
    try {
        $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        return $p.Current.IsSelected
    } catch { return $false }
}

$out = [ordered]@{}
$out.ok = $true
$out.hwnd = $win.Current.NativeWindowHandle
$out.title = $win.Current.Name

$out.lists = @()
$cond = New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)),
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Tree))
)
foreach ($l in $win.FindAll($TS::Descendants, $cond)) {
    if ($ListId -ne "" -and $l.Current.AutomationId -ne $ListId) { continue }
    $lr = $l.Current.BoundingRectangle
    $rows = @()
    foreach ($it in $l.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        $nm = $it.Current.Name
        if ([string]::IsNullOrWhiteSpace($nm)) { continue }
        $r = $it.Current.BoundingRectangle
        $rows += [ordered]@{
            name = $nm
            x    = [int]$r.X
            y    = [int]$r.Y
            w    = [int]$r.Width
            h    = [int]$r.Height
            sel  = (Sel-Of $it)
        }
    }
    $out.lists += [ordered]@{
        id    = $l.Current.AutomationId
        name  = $l.Current.Name
        x     = [int]$lr.X
        y     = [int]$lr.Y
        w     = [int]$lr.Width
        h     = [int]$lr.Height
        count = $rows.Count
        rows  = $rows
    }
}

# breadcrumb: the segment list carries FullPath, which is the current directory
$bc = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "BreadcrumbHost")
$host = $win.FindFirst($TS::Descendants, $bc)
$out.crumbs = @()
if ($null -ne $host) {
    foreach ($b in $host.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $nm = $b.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($nm)) { $out.crumbs += $nm }
    }
}

Write-Output ($out | ConvertTo-Json -Depth 6 -Compress)
