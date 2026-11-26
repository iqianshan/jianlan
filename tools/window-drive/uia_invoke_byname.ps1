param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$Name,
    [switch]$All,
    [switch]$Any
)

# Invoke a button by its accessible Name, across every top-level window of the process.
#
# Why this exists next to uia_click.ps1 (which goes by AutomationId):
#   1. The shortcut editor builds its rows in code, so the "change"/"reset" links
#      carry an AutomationProperties.Name ("change: rename") but no AutomationId.
#      A Name is what a screen reader announces, so it is the stable handle there.
#   2. uia_click.ps1 takes the FIRST non-offscreen window of the process. With the
#      settings dialog open, that is a coin toss between the main window and the
#      dialog; here we walk all of them and only accept an exact Name match.
#
# -All lists every matching button instead of invoking the first one (name + rect),
# which is what a layout check needs: the point is to see that the column is aligned.
#
# Comments and identifiers are ASCII on purpose: a UTF-8 file without BOM is
# mis-read by the 5.1 script host, and Chinese text in here would become mojibake.
# The Name itself is passed in by the caller and is safe to carry.
#
# Prints one JSON line; exits 1 when nothing matched.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

# -Any drops the ControlType filter: the shortcut filter box is a TextBox, and the
# only useful handle it has is its accessible Name (used here just to get its rect).
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)

$found = @()
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
    $scope = [System.Windows.Automation.TreeScope]::Descendants
    if ($Any) {
        foreach ($b in $w.FindAll($scope, $nameCond)) {
            $rc = $b.Current.BoundingRectangle
            $found += ,$b
            if ($All) {
                Write-Output ('{"ok":true,"name":"' + $Name.Replace('"', "'") +
                              '","type":"' + $b.Current.ControlType.ProgrammaticName +
                              '","x":' + [int]$rc.X + ',"y":' + [int]$rc.Y +
                              ',"w":' + [int]$rc.Width + ',"h":' + [int]$rc.Height + '}')
            }
        }
        continue
    }
    foreach ($b in $w.FindAll($scope, $btnCond)) {
        if ($b.Current.Name -ne $Name) { continue }
        $rc = $b.Current.BoundingRectangle
        $found += ,$b
        if ($All) {
            Write-Output ('{"ok":true,"name":"' + $Name.Replace('"', "'") +
                          '","x":' + [int]$rc.X + ',"y":' + [int]$rc.Y +
                          ',"w":' + [int]$rc.Width + ',"h":' + [int]$rc.Height + '}')
        }
    }
}

if ($All) {
    if ($found.Count -eq 0) { Write-Output '{"ok":false,"error":"element"}'; exit 1 }
    exit 0
}

if ($found.Count -eq 0) { Write-Output '{"ok":false,"error":"element"}'; exit 1 }

$el = $found[0]
$rect = $el.Current.BoundingRectangle
$rectJson = ',"x":' + [int]$rect.X + ',"y":' + [int]$rect.Y +
            ',"w":' + [int]$rect.Width + ',"h":' + [int]$rect.Height

if (-not $el.Current.IsEnabled) {
    Write-Output ('{"ok":false,"error":"disabled"' + $rectJson + '}')
    exit 2
}

$pattern = $null
if (-not $el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
    Write-Output ('{"ok":false,"error":"no-invoke"' + $rectJson + '}')
    exit 1
}

$pattern.Invoke()
Write-Output ('{"ok":true,"enabled":true' + $rectJson + '}')
