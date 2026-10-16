<#
  CI checks for the Parchisi STAR launcher.

  One script, several checks, so that CI and a local run execute the exact same
  bytes:

      powershell -ExecutionPolicy Bypass -File tools\ci\check.ps1 -Check hygiene

  Checks
    hygiene    the files that ship to users (src + README) carry no personal
               paths, no session temp dirs, no unfinished-work markers
    manifest   the exe still self-elevates (requireAdministrator) and declares
               PerMonitorV2 - losing either silently breaks a clean-machine run
    readme     every relative link / image in README.md resolves to a file that
               exists, so screenshots cannot rot
    dialogs    user-facing questions and failures go through AskPrompt /
               Attention; MessageBox stays allowed only for the one
               single-instance notice
    tray       every literal Tray.SetStatus string still fits the 63-char
               NotifyIcon.Text budget after the 16-char title prefix ("Parchisi
               STAR - "); an overlong status is truncated silently in the tray
    icon       tools\parchisi.ico exists with a valid ICO header (build input)
    source     the constraints this launcher is built on still hold: the exe
               name is preserved, the game package and the mandatory PNG texture
               split are unchanged, and the Play Store / Google sign-in flow
               this port deliberately removed has not crept back in

  Exit 0 = pass, 1 = fail. Every message starts with PASS or FAIL, so the run is
  readable straight from the Actions page.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('hygiene', 'manifest', 'readme', 'dialogs', 'tray', 'icon', 'source')]
    [string]$Check
)

# NB: $ErrorActionPreference stays at Continue on purpose. These checks are
# about reporting a failing fact, not about throwing; an exception in CI shows
# up as a stack trace, which reads like a broken check rather than a broken repo.
# tools\ci\check.ps1 -> tools -> repo root
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Fail([string]$m) { Write-Output "FAIL  $m"; exit 1 }
function Pass([string]$m) { Write-Output "PASS  $m"; exit 0 }

switch ($Check) {

    'hygiene' {
        # Scoped deliberately: this scans the exe's own source and the README -
        # the two files that actually reach users. tools\lab\* documents real
        # local paths because it is the experiment log behind this repo, and
        # scanning it would only teach contributors to work around the check.
        $files = @('src\AppLauncher.cs', 'README.md')
        $patterns = [ordered]@{
            'personal path'          = 'C:\\Users\\'
            'local user name'        = 'ysmag'
            'session temp dir'       = 'Temp\\opencode'
            'local backup reference' = 'WSA-backup'
            'hold marker'            = '\.hold'
            'unfinished work'        = 'TODO|FIXME'
        }
        $bad = 0
        foreach ($f in $files) {
            $p = Join-Path $root $f
            if (-not (Test-Path -LiteralPath $p)) { Fail "missing tracked file: $f" }
            $lines = Get-Content -LiteralPath $p
            foreach ($k in $patterns.Keys) {
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($lines[$i] -match $patterns[$k]) {
                        Write-Output ("FAIL  {0}:{1} matched '{2}': {3}" -f $f, ($i + 1), $k, $lines[$i].Trim())
                        $bad++
                    }
                }
            }
        }
        if ($bad -gt 0) { exit 1 }
        Pass ("hygiene: {0} shipped files clean ({1} patterns)" -f $files.Count, $patterns.Count)
    }

    'manifest' {
        $m = Join-Path $root 'tools\app.manifest'
        if (-not (Test-Path -LiteralPath $m)) { Fail 'tools\app.manifest is missing' }
        $raw = [IO.File]::ReadAllText($m)
        # Without requireAdministrator: Add-AppxPackage -Register fails 0x80073D28
        # on a clean machine (packaged service), and the Hyper-V cmdlets fail too.
        if ($raw -notmatch 'requestedExecutionLevel[^>]*level="requireAdministrator"') {
            Fail 'app.manifest no longer requests requireAdministrator - registration would fail 0x80073D28'
        }
        # Without PerMonitorV2 the hard-coded prompt layout renders wrong at
        # non-100% display scaling (measured: the download prompt has a 6 px gap).
        if ($raw -notmatch 'PerMonitorV2') {
            Fail 'app.manifest no longer declares PerMonitorV2 DPI awareness'
        }
        Pass 'manifest: requireAdministrator + PerMonitorV2 present'
    }

    'readme' {
        $rp = Join-Path $root 'README.md'
        if (-not (Test-Path -LiteralPath $rp)) { Fail 'README.md is missing' }
        $raw = [IO.File]::ReadAllText($rp)
        $missing = 0
        $checked = 0
        foreach ($m in [regex]::Matches($raw, '\]\(([^)]+)\)')) {
            $link = $m.Groups[1].Value
            if ($link -match '^(https?:|mailto:|#)') { continue }   # external / in-page anchor
            $clean = ($link -split '#')[0].Trim()
            if (-not $clean) { continue }                            # pure anchor
            $checked++
            if (-not (Test-Path -LiteralPath (Join-Path $root $clean))) {
                Write-Output "FAIL  README.md links to a file that does not exist: $clean"
                $missing++
            }
        }
        if ($missing -gt 0) { exit 1 }
        Pass ("readme: all {0} relative links resolve" -f $checked)
    }

    'dialogs' {
        $sp = Join-Path $root 'src\AppLauncher.cs'
        if (-not (Test-Path -LiteralPath $sp)) { Fail 'src\AppLauncher.cs is missing' }
        $src = Get-Content -LiteralPath $sp
        # MessageBox cannot take focus reliably from an elevated background
        # process (observed opening behind the user's windows), which reads as
        # "the exe did nothing". Every question and failure therefore goes
        # through AskPrompt/Attention, which flash the taskbar when they cannot
        # take focus. The single-instance notice is the one accepted MessageBox.
        $code = @($src | Where-Object { $_ -notmatch '^\s*//' })
        $mb = @($code | Where-Object { $_ -match 'MessageBox\.Show' })
        $stray = @($mb | Where-Object { $_ -notmatch 'already running' })
        if ($stray.Count -gt 0) {
            foreach ($s in $stray) { Write-Output ("FAIL  MessageBox.Show outside the allowlist: {0}" -f $s.Trim()) }
            Fail 'use Attention()/AskPrompt() for user-facing windows (MessageBox opens behind other windows when elevated)'
        }
        if ($mb.Count -eq 0) { Fail 'the single-instance MessageBox notice disappeared; if that was on purpose, drop this assertion too' }
        $ask = @($src | Where-Object { $_ -match 'new AskPrompt\(' }).Count
        $att = @($src | Where-Object { $_ -match 'Attention\(' }).Count
        if ($ask -eq 0) { Fail 'AskPrompt is no longer used - questions would fall back to MessageBox' }
        if ($att -eq 0) { Fail 'Attention() is no longer called - prompts could open behind other windows' }
        Pass ("dialogs: AskPrompt x{0}, Attention x{1}, MessageBox allowlisted x{2}" -f $ask, $att, $mb.Count)
    }

    'tray' {
        $sp = Join-Path $root 'src\AppLauncher.cs'
        if (-not (Test-Path -LiteralPath $sp)) { Fail 'src\AppLauncher.cs is missing' }
        $src = Get-Content -LiteralPath $sp -Raw
        # NotifyIcon.Text throws past 63 characters on the .NET Framework, and
        # SetText() prefixes "Parchisi STAR - " (16 chars), leaving 47 for the
        # status itself. Two GPGP-era statuses overran their budget once and
        # were silently cut in the tray, so the literals are measured here.
        # Only pure literals: the concatenated statuses end in a small variable
        # part (MB counts, minutes) that is bounded by construction.
        $bad = 0
        $checked = 0
        foreach ($m in [regex]::Matches($src, 'Tray\.SetStatus\("([^"]*)"\)')) {
            $s = $m.Groups[1].Value
            $checked++
            if ((16 + $s.Length) -gt 63) {
                Write-Output ("FAIL  tray status is {0} chars, {1} with the title prefix (limit 63): {2}" -f `
                              $s.Length, (16 + $s.Length), $s)
                $bad++
            }
        }
        if ($checked -eq 0) { Fail 'no literal Tray.SetStatus call found - the status line changed shape and this check rotted' }
        if ($bad -gt 0) { exit 1 }
        Pass ("tray: {0} literal statuses fit the 63-char tray budget" -f $checked)
    }

    'icon' {
        $ip = Join-Path $root 'tools\parchisi.ico'
        if (-not (Test-Path -LiteralPath $ip)) { Fail 'tools\parchisi.ico is missing - the build needs it for /win32icon' }
        $b = [IO.File]::ReadAllBytes($ip)
        if ($b.Length -lt 76) { Fail ("ico is only {0} bytes" -f $b.Length) }
        if (-not ($b[0] -eq 0 -and $b[1] -eq 0 -and $b[2] -eq 1 -and $b[3] -eq 0)) {
            Fail ("ico header is {0:X2} {1:X2} {2:X2} {3:X2}, expected 00 00 01 00" -f $b[0], $b[1], $b[2], $b[3])
        }
        Pass ("icon: {0} bytes, valid ICO header" -f $b.Length)
    }

    'source' {
        $sp = Join-Path $root 'src\AppLauncher.cs'
        if (-not (Test-Path -LiteralPath $sp)) { Fail 'src\AppLauncher.cs is missing' }
        $src = Get-Content -LiteralPath $sp
        $raw = [IO.File]::ReadAllText($sp)

        # The build hardcodes this name on purpose: an AV-heuristic-sensitive
        # filename that a well-meaning rename would silently break. Assert the
        # source and the build script agree on it.
        $bp = Join-Path $root 'tools\build.ps1'
        if (-not (Test-Path -LiteralPath $bp)) { Fail 'tools\build.ps1 is missing' }
        $buildRaw = [IO.File]::ReadAllText($bp)
        if ($buildRaw -notmatch "PlayParchisi\.exe") {
            Fail 'tools\build.ps1 no longer emits PlayParchisi.exe - the exe name is load-bearing (see README "Name is load-bearing")'
        }
        if ($raw -notmatch 'PlayParchisi\.exe') {
            Fail 'src\AppLauncher.cs no longer references PlayParchisi.exe'
        }

        # The game is only distinguishable from any other WSA app by its package
        # id, and the launcher depends on it for install, verify and launch.
        if ($raw -notmatch 'com\.superking\.parchisi\.star') {
            Fail 'the game package id (com.superking.parchisi.star) disappeared from the source'
        }

        # Without the PNG texture split the game renders black. The split must be
        # both shipped in the payload list and verified after install, so a change
        # to either half has to be deliberate.
        if ($raw -notmatch 'split_config\.etc1_rgb8_group_hd\.apk') {
            Fail 'the mandatory PNG texture split is no longer in the payload APK list'
        }
        if ($raw -notmatch 'etc1_rgb8_group_hd') {
            Fail 'the post-install texture-split verification disappeared'
        }

        # Firefox is the Facebook login path: the game's WebView UA is rejected
        # by Facebook, so the browser role holder must stay in the payload.
        if ($raw -notmatch 'firefox_base\.apk') {
            Fail 'the Firefox APK list disappeared - Facebook login would break again'
        }
        if ($raw -notmatch 'org\.mozilla\.firefox') {
            Fail 'the Firefox package id disappeared from the source'
        }
        if ($raw -notmatch 'android\.app\.role\.BROWSER') {
            Fail 'the browser-role handoff to Firefox disappeared'
        }

        # Four backslashes in a row in the SOURCE is a C# string holding TWO, and
        # in this codebase that is always a mistranscription of an escape: it turned
        # the single-instance mutex into "Global\\PlayParchisiLauncher", a name no
        # kernel object will accept, so the constructor threw and every launch died
        # on exit code 2 with no window - the exe looked like it did nothing. A
        # clean-machine run is what caught it; this check is what stops it coming
        # back. Comments are excluded because the comment above that fix quotes the
        # bad form on purpose.
        foreach ($bad in @($src | Where-Object { $_ -match '\\\\\\\\' -and $_ -notmatch '^\s*//' })) {
            Fail ("doubled backslash in a string literal: {0}" -f $bad.Trim())
        }

        # This port deliberately ships no Play Store flow and no Google sign-in:
        # the game, Firefox and adb arrive as one zip and are installed over adb.
        # What was removed is the store/sign-in machinery - opening the store UI,
        # waiting on a sign-in, and reading installer identity to police where an
        # APK came from. (com.android.vending survives on purpose as the BOOT
        # protocol-activation target: it exists in every GApps build, so it is a
        # reliable way to wake the subsystem - the store window is closed again
        # right after boot. It is never queried for, never opened as the store,
        # and never used to install anything.)
        $code = @($src | Where-Object { $_ -notmatch '^\s*//' })
        foreach ($pat in @('market://', 'installerPackageName', 'google.*sign.?in', 'sign.?in.*google', 'vending/details')) {
            $hit = @($code | Where-Object { $_ -match $pat })
            if ($hit.Count -gt 0) {
                foreach ($h in $hit) { Write-Output ("FAIL  forbidden pattern '{0}': {1}" -f $pat, $h.Trim()) }
                Fail 'the Play Store / Google sign-in flow was removed from this port; do not reintroduce it'
            }
        }

        Pass "source: exe name, package id, texture split present; Play Store / sign-in flow absent"
    }
}
