param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$AutomationId
)

# Invoke a button by AutomationId through UI Automation.
#
# Why not coordinates: clicks aimed at the main window's client area land about
# 8 px off in practice, and the error grows down the list; a 26 px toolbar button
# is easy to miss entirely (a click aimed at the preview toggle landed on the
# search box instead). AutomationId is stable across DPI, placement and scrolling.
#
# WPF maps x:Name to AutomationId, so the caller passes the element name from XAML.
#
# Comments and identifiers here are ASCII on purpose: a UTF-8 file without BOM is
# mis-read by the 5.1 script host, and Chinese text would turn into mojibake.
#
# Prints one JSON line; exits 1 when the window or the element is missing.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)

$win = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
    if (-not $w.Current.IsOffscreen) { $win = $w; break }
}
if ($null -eq $win) { Write-Output '{"ok":false,"error":"window"}'; exit 1 }

$idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if ($null -eq $el) { Write-Output '{"ok":false,"error":"element"}'; exit 1 }

$rect = $el.Current.BoundingRectangle
$enabled = $el.Current.IsEnabled
$rectJson = ',"x":' + [int]$rect.X + ',"y":' + [int]$rect.Y +
            ',"w":' + [int]$rect.Width + ',"h":' + [int]$rect.Height

# A disabled button must report as such instead of throwing: "is it greyed out"
# is exactly what some of these checks are here to verify.
if (-not $enabled) {
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
