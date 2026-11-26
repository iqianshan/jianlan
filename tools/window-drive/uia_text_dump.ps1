param(
    [Parameter(Mandatory = $true)][int]$TargetPid
)

# Dump every text-bearing element of the process's top-level windows.
#
# Why: this machine has an always-on-top window that keeps stealing the foreground,
# and the app window sometimes lands outside the 1920-wide primary screen -- screenshots
# come back showing someone else's window. Reading the text through UI Automation is
# the only reliable way to see "what does the recording chip say right now" and
# "what did the status bar answer".
#
# Why EnumWindows instead of RootElement.FindAll(Children): the settings dialog is an
# owned window with ShowInTaskbar=False; the UIA top-level tree sometimes lists only
# the main window for this process, so the dialog--and therefore the status bar we
# want to read--is simply absent. Enumerating HWNDs and going in through FromHandle
# sees every window the process owns.
#
# Output: "win=<title>" per window, then one indented line per text element.
# ASCII in comments only: the 5.1 host reads BOM-less files as ANSI.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class WinEnum
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static IntPtr[] ForProcess(uint target)
    {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == target) list.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
}
"@

[Console]::OutputEncoding = [Text.Encoding]::UTF8

$textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)

$handles = [WinEnum]::ForProcess([uint32]$TargetPid)
if ($handles.Count -eq 0) { Write-Output '{"ok":false,"error":"window"}'; exit 1 }

foreach ($h in $handles) {
    try {
        $w = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    } catch {
        continue
    }
    if ($null -eq $w) { continue }

    $title = $w.Current.Name
    if ([string]::IsNullOrEmpty($title)) { $title = '(untitled)' }
    Write-Output ('win=' + $title + ' hwnd=' + [int64]$h)

    $texts = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)
    foreach ($t in $texts) {
        $name = $t.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        # Collapse newlines so one element stays on one line.
        $flat = $name -replace "\r?\n", ' / '
        Write-Output ('  ' + $flat)
    }
}
