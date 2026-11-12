param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][int]$Index,
    [string]$ListId = "QuickAccessList"
)

# Select a row in a list by ordinal, through UI Automation.
#
# Why not screen coordinates: measured on this machine, a click at a point on the
# sidebar lands dozens of pixels away and can hit a neighbouring row (we aimed at
# the "blue" tag and the app logged NavEmptyRow). The offset also drifts with the
# window position and the scroll offset, so a fixed correction never keeps working.
#
# The ordinal is stable while the data does not change, and is independent of DPI,
# scrolling and window placement. It also verifies that the list really exposes
# every row as a ListItem, which is part of accessibility.
#
# Comments and identifiers here are ASCII on purpose: a UTF-8 file without BOM is
# mis-read by the 5.1 script host, and Chinese text would turn into mojibake.
# Locate by AutomationId, never by Name, for the same reason.
#
# Prints one JSON line; exits 1 when the window, list or index is missing.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) { Write-Output '{"ok":false,"error":"window"}'; exit 1 }

$idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ListId)
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if ($null -eq $list) { Write-Output '{"ok":false,"error":"list"}'; exit 1 }

$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCond)
if ($Index -ge $items.Count) {
    Write-Output ('{"ok":false,"error":"index","count":' + $items.Count + '}')
    exit 1
}

$item = $items.Item($Index)
$pattern = $null
if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
    $pattern.Select()
} else {
    Write-Output '{"ok":false,"error":"noselectionpattern"}'
    exit 1
}

Write-Output ('{"ok":true,"count":' + $items.Count + '}')
