param(
    [Parameter(Mandatory = $true)][int]$TargetPid
)

# Probe the main window for the inline-rename smoke test.
#
# Returns one JSON line:
#   ok, hwnd, editing (an Edit control exists inside the file list),
#   text (its current value),
#   list{x,y,w,h} (the file list rectangle, for aiming at empty space),
#   rows[{selected,name,x,y,w,h,nx,ny,nw,nh}]
#
# nx/ny/nw/nh is the rectangle of the row's *name text*, not the whole row.
# The rename gesture only fires when the click lands on the file name itself
# (clicking the empty part of the name column or the icon just selects), so the
# smoke test has to aim at the text; guessing an offset from the row's left edge
# lands on the item border instead and the gesture never arms.
#
# No caret position here on purpose: .NET's TextPatternRange exposes neither
# Start nor Length (only CompareEndpoints against another range), so reading the
# caret through UI Automation is a dead end. Arrow-key behaviour is checked in
# the smoke test by typing a marker character and looking at where it lands.
#
# Comments and identifiers are ASCII on purpose: a UTF-8 file without BOM is
# mis-read by the 5.1 script host and Chinese text turns into mojibake.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)

$win = $null
$best = 0
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
    if ($w.Current.IsOffscreen) { continue }
    $r = $w.Current.BoundingRectangle
    $area = $r.Width * $r.Height
    if ($area -gt $best) { $best = $area; $win = $w }
}

if ($null -eq $win) { Write-Output '{"ok":false,"error":"window"}'; exit 1 }

$idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "FileList")
$list = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)

$rows = @()
$editing = $false
$text = ""
$lbx = 0; $lby = 0; $lbw = 0; $lbh = 0

if ($null -ne $list) {
    $lr = $list.Current.BoundingRectangle
    $lbx = [int]$lr.X; $lby = [int]$lr.Y; $lbw = [int]$lr.Width; $lbh = [int]$lr.Height
    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)

    foreach ($item in $list.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCond)) {
        $r = $item.Current.BoundingRectangle
        $sel = $false
        $sp = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) {
            $sel = $sp.Current.IsSelected
        }

        # Name text rectangle: the TextBlock is what carries the rename gesture.
        # Fall back to the whole row when there is no Text child (icon-only views).
        $tx = [int]$r.X
        $ty = [int]$r.Y
        $tw = 0
        $th = [int]$r.Height
        $textCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $textEl = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $textCond)
        if ($null -ne $textEl) {
            $tr = $textEl.Current.BoundingRectangle
            if ($tr.Width -gt 0) {
                $tx = [int]$tr.X
                $ty = [int]$tr.Y
                $tw = [int]$tr.Width
                $th = [int]$tr.Height
            }
        }

        $rows += [pscustomobject]@{
            selected = $sel
            name = $item.Current.Name
            x    = [int]$r.X
            y    = [int]$r.Y
            w    = [int]$r.Width
            h    = [int]$r.Height
            nx   = $tx
            ny   = $ty
            nw   = $tw
            nh   = $th
        }
    }

    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    if ($null -ne $edit) {
        $editing = $true
        $value = $null
        if ($edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value)) {
            $text = $value.Current.Value
        }
    }
}

$result = [pscustomobject]@{
    ok      = $true
    hwnd    = $win.Current.NativeWindowHandle
    editing = $editing
    text    = $text
    list    = [pscustomobject]@{ x = $lbx; y = $lby; w = $lbw; h = $lbh }
    rows    = $rows
}

Write-Output ($result | ConvertTo-Json -Compress -Depth 4)
