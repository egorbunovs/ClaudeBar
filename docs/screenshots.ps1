# Takes the screenshots in docs/screenshots/, from `ClaudeBar.exe --demo`: invented accounts,
# invented numbers, over a backdrop of its own, so nothing of the real desktop - or a real
# email address - is ever in frame.
#
#   cd src\ClaudeBar; dotnet build
#   docs\screenshots.ps1 -Out docs\screenshots\pill.png
#   docs\screenshots.ps1 -Out docs\screenshots\all-accounts.png -ShowAll
#   docs\screenshots.ps1 -Out docs\screenshots\mini.png -Mini
#   docs\screenshots.ps1 -Out docs\screenshots\picker.png -Picker
#   docs\screenshots.ps1 -Out docs\screenshots\menu.png -RightClick
#
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [switch]$ShowAll,
    [switch]$Mini,
    [switch]$RightClick,
    # Left-click the switch button in the header, which opens the account picker.
    [switch]$Picker,
    [int]$Pad = 24
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// Per-monitor aware v2, so every coordinate here is physical on every screen - the
    /// screens have different scale factors and a system-aware process gets lied to.
    public static void GoDpiAware() {
        if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware();
    }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// Puts one window at the very top of the always-on-top band.
    public static void Lift(IntPtr h) {
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, no move/size/activate
    }

    /// Lifts the demo's pill back to the top of the topmost band, above the backdrop.
    public static void LiftPill(uint wanted) {
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != wanted || !IsWindowVisible(h)) return true;
            var t = new System.Text.StringBuilder(256);
            GetWindowTextW(h, t, 256);
            if (t.ToString() == "ClaudeBar")
                SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, no move/size/activate
            return true;
        }, IntPtr.Zero);
    }

    // "left,top,right,bottom,title" per visible window. The pill is the one titled ClaudeBar;
    // an open menu is an untitled popup. A third, untitled window sits parked off-screen and
    // is filtered out by the caller on screen bounds.
    public static List<string> VisibleWindows(uint wanted) {
        var found = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            RECT r;
            if (pid == wanted && IsWindowVisible(h) && GetWindowRect(h, out r)
                && r.Right - r.Left > 1 && r.Bottom - r.Top > 1) {
                var t = new System.Text.StringBuilder(256);
                GetWindowTextW(h, t, 256);
                found.Add(string.Format("{0},{1},{2},{3},{4}", r.Left, r.Top, r.Right, r.Bottom, t));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void RightClickAt(int x, int y) {
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
        mouse_event(0x0008, 0, 0, 0, IntPtr.Zero); // right down
        mouse_event(0x0010, 0, 0, 0, IntPtr.Zero); // right up
    }
    public static void LeftClickAt(int x, int y) {
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // left down
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // left up
    }
}
"@ -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Drawing.Primitives, System.Collections, System.Runtime

[Win]::GoDpiAware()

$exeDir   = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ClaudeBar\bin\Debug\net10.0-windows'
$settings = Join-Path $exeDir 'settings.json'

# Second screen if there is one: a real ClaudeBar is probably sitting in the primary screen's
# bottom-right corner, and its pill and its toasts would otherwise wander into frame.
$screen = [System.Windows.Forms.Screen]::AllScreens |
    Sort-Object Primary | Select-Object -First 1

# The demo never writes settings back, so this file only sets the starting state.
@{
    MonitorDeviceName = $screen.DeviceName
    Anchor            = "TopCentre"
    SnapPadding       = 24
    SnapThreshold     = 64
    OffsetX           = 24
    OffsetY           = 24
    PollSeconds       = 60
    WarnAt            = 80
    CriticalAt        = 90
    Opacity           = 1
    Visible           = $true
    ShowAllAccounts   = [bool]$ShowAll
    Mini              = [bool]$Mini
} | ConvertTo-Json | Set-Content -Path $settings -Encoding utf8

$proc = Start-Process -FilePath (Join-Path $exeDir 'ClaudeBar.exe') -ArgumentList '--demo' -PassThru
Start-Sleep -Seconds 3

# A backdrop for the pill to sit on, covering the desktop while the shot is taken.
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen  # whole desktop: DPI scaling makes a single screen rect land short
$form.BackColor = [System.Drawing.Color]::FromArgb(24, 26, 33)
# A soft diagonal wash rather than a flat fill, so the pill has something to sit on.
$form.Add_Paint({
    param($s, $e)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $s.ClientRectangle,
        [System.Drawing.Color]::FromArgb(17, 20, 28),
        [System.Drawing.Color]::FromArgb(45, 53, 72),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    $e.Graphics.FillRectangle($brush, $s.ClientRectangle)
    $brush.Dispose()
})
$form.ShowInTaskbar = $false
$form.TopMost = $true   # some windows here are always-on-top; the backdrop has to beat them
$form.Show()
$form.Activate()
1..25 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }

# TopMost alone only joins the always-on-top band; this puts the backdrop at the top of it,
# above whatever else in there (a pinned terminal) was still showing through.
[Win]::Lift($form.Handle)
1..10 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }

# ...and then the pill goes back above the backdrop.
[Win]::LiftPill($proc.Id)
1..10 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }

function Get-DemoWindows {
    [Win]::VisibleWindows($proc.Id) | ForEach-Object {
        $f = $_ -split ',', 5
        [pscustomobject]@{
            Left = [int]$f[0]; Top = [int]$f[1]; Right = [int]$f[2]; Bottom = [int]$f[3]; Title = $f[4]
        }
    } | Where-Object {
        # On this screen only: one of the app's windows is parked off to the side.
        $_.Left -ge $screen.Bounds.Left -and $_.Right -le $screen.Bounds.Right
    }
}

if ($RightClick -or $Picker) {
    $pill = Get-DemoWindows | Where-Object { $_.Title -eq 'ClaudeBar' } | Select-Object -First 1
    # Bottom-right corner of the pill: the menu then opens down and to the right, and the
    # pill it belongs to stays readable beside it.
    $cx = [int]($pill.Right - 12)
    $cy = [int]($pill.Bottom - 8)
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $cx, $cy
    Start-Sleep -Milliseconds 400
    [Win]::RightClickAt($cx, $cy)
    1..30 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }
}

if ($Picker) {
    $pill = Get-DemoWindows | Where-Object { $_.Title -eq 'ClaudeBar' } | Select-Object -First 1
    # The switch button, third glyph from the right of the header line.
    $cx = [int]($pill.Right - 84)
    $cy = [int]($pill.Top + 21)
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $cx, $cy
    Start-Sleep -Milliseconds 400
    [Win]::LeftClickAt($cx, $cy)
    1..30 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 40 }
}

# The pill, plus - for the menu shot - the popup next to it. Deliberately not everything the
# demo has on screen: its warning toast parks in the corner and would drift into the frame.
$pill = Get-DemoWindows | Where-Object { $_.Title -eq 'ClaudeBar' } | Select-Object -First 1
$rects = @($pill)
if ($RightClick -or $Picker) {
    $rects += Get-DemoWindows | Where-Object {
        $_.Title -ne 'ClaudeBar' -and
        $_.Left -gt $pill.Left - 600 -and $_.Left -lt $pill.Right + 200 -and
        $_.Top -gt $pill.Top - 200 -and $_.Top -lt $pill.Bottom + 400
    }
}
if ($rects.Count -eq 0) { throw "no visible demo window" }
$left   = ($rects | Measure-Object -Property Left   -Minimum).Minimum
$top    = ($rects | Measure-Object -Property Top    -Minimum).Minimum
$right  = ($rects | Measure-Object -Property Right  -Maximum).Maximum
$bottom = ($rects | Measure-Object -Property Bottom -Maximum).Maximum

$x = [Math]::Max($screen.Bounds.Left, $left - $Pad)
$y = [Math]::Max($screen.Bounds.Top, $top - $Pad)
$w = [Math]::Min($screen.Bounds.Right, $right + $Pad) - $x
$h = [Math]::Min($screen.Bounds.Bottom, $bottom + $Pad) - $y

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$form.Close()
$form.Dispose()
Stop-Process -Id $proc.Id -Force
"$Out  ($w x $h)"
$rects | ForEach-Object { "   window '$($_.Title)' $($_.Left),$($_.Top) $($_.Right - $_.Left)x$($_.Bottom - $_.Top)" }
