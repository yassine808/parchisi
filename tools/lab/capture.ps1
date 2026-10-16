<#
  capture.ps1 - capture one specific top-level window of a process as a PNG.

      powershell -NoProfile -ExecutionPolicy Bypass -File tools\lab\capture.ps1 `
          -ProcId 1234 -TitlePattern 'setup problem' -OutPng 'C:\temp\x.png'

  How it captures, and why in this order (measured, not assumed):

    1. PrintWindow, preferred. It reads the window from the window itself and
       sends the target no input at all, so capturing can never change what the
       program under test does next. That matters more here than any picture:
       the harness exists to observe the launcher, and an observation tool that
       can dismiss a dialog turns a pass into a mystery failure.
       PrintWindow only works at the same integrity level as the target. The
       harness runs elevated, exactly like the launcher, so it does work - and
       when it does, the result is indistinguishable from the screen: measured
       436x239 at avg-brightness 208 via PrintWindow vs 207.3 via the screen.
       flags=2 (PW_RENDERFULLCONTENT) is required; plain PrintWindow renders
       184.3 and misses content that is drawn after the first paint.

    2. CopyFromScreen, only if PrintWindow fails or comes back blank. This is
       the fallback for capturing a window at a different integrity level, where
       PrintWindow is blocked. It needs the window to genuinely own the
       foreground, which means forcing it there - see the -NoFocusRequired
       description below for why that is a last resort rather than the plan.

  * Graphics.CopyFromScreen copies whatever is on top of the screen, so in the
    fallback path the target must genuinely own the foreground or the capture
    shows a different window entirely. A Medium-integrity process cannot make an
    elevated window take foreground. => To capture an elevated window by the
    screen route, run THIS script elevated.

  * The status bubble deliberately uses ShowWithoutActivation (it must never
    steal focus from the app it is launching). PrintWindow suits it best of all,
    since it needs no activation; -NoFocusRequired therefore now only changes
    the SCREEN fallback, where the check becomes "the window owns its own centre
    pixel" (WindowFromPoint) rather than "the window is foreground". Either way
    the window is never focused in this mode.

  Exit 0 = captured and verified, 1 = failed.
#>
param(
    [Parameter(Mandatory = $true)][int]$ProcId,
    [Parameter(Mandatory = $true)][string]$TitlePattern,
    [Parameter(Mandatory = $true)][string]$OutPng,
    [int]$SettleMs = 600,
    [int]$Retries = 4,
    [switch]$NoFocusRequired
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;using System.Runtime.InteropServices;
public class Cap {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

function Find-Target {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcId)
    foreach ($top in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        $n = $top.Current.Name
        if ($n -and $n -match $TitlePattern) { return $top }
    }
    return $null
}

# brightness snapshot, to tell a real light dialog from a dark window on top
function Stats([System.Drawing.Bitmap]$bmp) {
    $sum = 0; $min = 255; $max = 0; $n = 0
    $w = [math]::Min(80, $bmp.Width); $h = [math]::Min(60, $bmp.Height)
    for ($y = 0; $y -lt $h; $y += 2) {
        for ($x = 0; $x -lt $w; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            $b = [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
            $sum += $b; $n++
            if ($b -lt $min) { $min = $b }
            if ($b -gt $max) { $max = $b }
        }
    }
    return @{ avg = [math]::Round($sum / [math]::Max(1, $n), 1); min = $min; max = $max }
}

function Save-Verified([System.Drawing.Bitmap]$bmp, [string]$how, [int]$w, [int]$hh) {
    $s = Stats $bmp
    # a launcher dialog is light (avg well above mid) with a dark title bar;
    # Discord sits far darker. Refuse a capture that cannot be ours.
    if ($s.avg -lt 90) { $bmp.Dispose(); return $false }
    $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("CAPTURED by {0}  {1}x{2}  ->  {3}   avg-brightness={4} min={5} max={6}" -f $how, $w, $hh, $OutPng, $s.avg, $s.min, $s.max)
    exit 0
}

for ($try = 1; $try -le $Retries; $try++) {
    $el = Find-Target
    if (-not $el) { Write-Output "attempt $try : no window matching '$TitlePattern' (pid $ProcId)"; Start-Sleep -Milliseconds 800; continue }

    $hwnd = [IntPtr]$el.Current.NativeWindowHandle
    if (-not [Cap]::IsWindow($hwnd)) { Write-Output "attempt $try : stale handle"; Start-Sleep -Milliseconds 800; continue }

    $r = New-Object Cap+RECT
    [Cap]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $W = $r.R - $r.L; $H = $r.B - $r.T
    if ($W -le 0 -or $H -le 0) { Write-Output "attempt $try : bad rect ${W}x${H}"; Start-Sleep -Milliseconds 800; continue }

    # --- preferred route: ask the window for its own pixels, touch nothing
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $pok = [Cap]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if ($pok) {
        $s = Stats $bmp
        # A cross-integrity PrintWindow reports success but hands back solid
        # white, so brightness alone cannot separate it from a real dialog.
        # A genuine launcher window always has dark pixels (title bar, text);
        # a blank render is uniformly white, so min is the honest test.
        if ($s.avg -ge 90 -and $s.min -lt 250) { Save-Verified $bmp 'PrintWindow' $W $H }
        else { Write-Output ("attempt $try : PrintWindow came back blank (avg={0} min={1}) - falling back to the screen" -f $s.avg, $s.min); $bmp.Dispose() }
    } else {
        $bmp.Dispose()
        Write-Output "attempt $try : PrintWindow returned false (a different integrity level) - falling back to the screen"
    }

    if ($NoFocusRequired) {
        # Never force focus (the window must not steal it). Instead prove the
        # window is genuinely what is drawn at its own centre pixel.
        Start-Sleep -Milliseconds $SettleMs
        $mid = New-Object Cap+POINT
        $mid.X = $r.L + [int]($W / 2); $mid.Y = $r.T + [int]($H / 2)
        # WindowFromPoint returns the DEEPEST window at that pixel - for a form
        # whose area is covered by a Label that is the STATIC child, not the
        # form, so a raw comparison never matches. Walk up to the top-level
        # window (GA_ROOT) before comparing.
        $at = [Cap]::GetAncestor([Cap]::WindowFromPoint($mid), 2)
        if ($at -ne $hwnd) {
            $cname = 'unknown'
            try { $cname = ([System.Windows.Automation.AutomationElement]::FromHandle($at).Current.Name) } catch { }
            Write-Output "attempt $try : centre pixel belongs to '$cname', not to this window (something is on top) - not capturing"
            Start-Sleep -Milliseconds 800
            continue
        }
    }
    else {
        [Cap]::ShowWindow($hwnd, 9) | Out-Null                      # SW_RESTORE, never minimise it away
        $fg = [Cap]::GetForegroundWindow()
        $tId = [Cap]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
        $cId = [Cap]::GetCurrentThreadId()
        if ($tId -ne $cId) { [Cap]::AttachThreadInput($tId, $cId, $true) | Out-Null }
        [Cap]::BringWindowToTop($hwnd) | Out-Null
        [Cap]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds $SettleMs
        if ($tId -ne $cId) { [Cap]::AttachThreadInput($tId, $cId, $false) | Out-Null }

        $owns = ([Cap]::GetForegroundWindow() -eq $hwnd)
        if (-not $owns) {
            $cur = [Cap]::GetForegroundWindow()
            $cname = 'unknown'
            try { $cname = ([System.Windows.Automation.AutomationElement]::FromHandle($cur).Current.Name) } catch { }
            Write-Output "attempt $try : window is NOT foreground (foreground is '$cname') - not capturing, would show the wrong window"
            Start-Sleep -Milliseconds 800
            continue
        }
    }

    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($W, $H)))
    $g.Dispose()
    Save-Verified $bmp 'the screen' $W $H
    Start-Sleep -Milliseconds 800
}
Write-Output "FAILED  could not capture '$TitlePattern' (pid $ProcId) after $Retries attempts"
exit 1
