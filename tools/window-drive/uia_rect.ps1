param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [switch]$Windows,
    [switch]$Buttons
)

# Measure where a popup/menu actually landed.
#
# Two sources, because neither alone is enough:
#   1. EnumWindows for the process: WPF Popup creates its own top-level HWND
#      (WS_EX_TOOLWINDOW), so GetWindowRect gives the menu's real screen rect.
#      Class names of popup windows are HwndWrapper[...] and cannot be told apart
#      from the main window by class alone, so we skip the main window by size.
#   2. The UIA tree: a ContextMenu shows up as ControlType.Menu with a
#      BoundingRectangle. This is the "what the user sees" number.
#
# -Buttons also lists every button of the main window (name + rect): the nav rows
# carry a row-scoped "more actions" button with no AutomationId, so the caller has
# to pick it by name.
#
# Prints one JSON line. ASCII only (see uia_click.ps1 for why).

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Button names are localized (Chinese here); without this the JSON is written in
# the console code page and the caller reads mojibake.
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEnum {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<string> List(uint target) {
        var outp = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != target) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r; GetWindowRect(h, out r);
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            var owner = GetWindow(h, 4); // GW_OWNER
            outp.Add(string.Format(
                "{{\"hwnd\":{0},\"class\":\"{1}\",\"owner\":{2},\"x\":{3},\"y\":{4},\"w\":{5},\"h\":{6}}}",
                h.ToInt64(), sb.ToString().Replace("\\", "\\\\").Replace("\"", "'"),
                owner.ToInt64(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return outp;
    }
}
"@

$wins = [WinEnum]::List([uint32]$TargetPid) -join ','

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)

# Menus live in their own popup window, so they are NOT descendants of the main
# window element. Walk every root-level window of the process instead.
$menus = @()
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
    $menuCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Menu)
    foreach ($m in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)) {
        $rc = $m.Current.BoundingRectangle
        $menus += ('{"name":"' + $m.Current.Name.Replace('\', '\\').Replace('"', "'") +
                   '","x":' + [int]$rc.X + ',"y":' + [int]$rc.Y +
                   ',"w":' + [int]$rc.Width + ',"h":' + [int]$rc.Height + '}')
    }
}

$btnList = @()
if ($Buttons) {
    $win = $null
    $best = 0
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
        if ($w.Current.IsOffscreen) { continue }
        $r0 = $w.Current.BoundingRectangle
        if (($r0.Width * $r0.Height) -gt $best) { $best = $r0.Width * $r0.Height; $win = $w }
    }
    if ($null -ne $win) {
        $bCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bCond)) {
            $rc = $b.Current.BoundingRectangle
            if ($rc.Width -le 0) { continue }
            $btnList += ('{"name":"' + $b.Current.Name.Replace('\', '\\').Replace('"', "'") +
                         '","id":"' + $b.Current.AutomationId.Replace('"', "'") +
                         '","x":' + [int]$rc.X + ',"y":' + [int]$rc.Y +
                         ',"w":' + [int]$rc.Width + ',"h":' + [int]$rc.Height + '}')
        }
    }
}

Write-Output ('{"ok":true,"windows":[' + $wins + '],"menus":[' + ($menus -join ',') +
              '],"buttons":[' + ($btnList -join ',') + ']}')
