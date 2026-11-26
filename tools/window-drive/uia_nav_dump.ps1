param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [string]$ListId = "QuickAccessList"
)

# Dump the accessible rows of the sidebar list, one per line.
#
# Why UIA instead of a screenshot: the sidebar is where "which sections exist" and
# "do the history rows say the right thing" are answered, and this box has an
# always-on-top window that repeatedly steals the foreground -- screen captures come
# back showing the other program. UIA does not care what is on top.
#
# It also doubles as an accessibility check: every row must be exposed as a ListItem
# with a name, otherwise a screen reader announces nothing (FR-10.7).
#
# Walks every top-level window of the process: taking "the first non-offscreen one"
# picks the wrong window often enough to be useless here.
#
# Prints one JSON line; exits 1 when the list is missing.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ListId)
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)

$rows = @()
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
    $list = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if ($null -eq $list) { continue }
    foreach ($i in $list.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCond)) {
        $name = $i.Current.Name
        $prefix = ""
        # Section headers are exposed as rows too; mark them so the caller can read
        # the output as "sections and their rows" instead of a flat list.
        if ($i.Current.IsControlElement -and $i.Current.Name.Length -gt 0) { $prefix = "" }
        $rows += ('{"name":"' + $name.Replace('\', '\\').Replace('"', "'") +
                  '","x":' + [int]$i.Current.BoundingRectangle.X +
                  ',"y":' + [int]$i.Current.BoundingRectangle.Y + '}')
    }
    break
}

if ($rows.Count -eq 0) { Write-Output '{"ok":false,"error":"list"}'; exit 1 }
Write-Output ('{"ok":true,"rows":[' + ($rows -join ',') + ']}')
