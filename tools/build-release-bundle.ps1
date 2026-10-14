<#
  Builds the self-contained release bundle.

  GitHub release assets are FLAT - they cannot carry directories. The launcher
  resolves adb.exe and payload\ via AppDomain.CurrentDomain.BaseDirectory, so a
  flat upload would break it. Bundling is therefore required for correctness,
  not convenience: extract anywhere, double-click, done.

  Layout:
    PlayParchisi-setup.zip
    `-- PlayParchisi-setup/
        |-- PlayParchisi.exe
        |-- adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll
        |-- README.txt
        `-- payload/
            |-- wsa.zip
            `-- *.apk  (game + Firefox splits)

  Members are streamed, never ReadAllBytes: the bundle is ~1.5 GB.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = 'D:\Projects\parchisi',
    [string]$OutFile  = ''
)

$ErrorActionPreference = 'Stop'

if (-not $OutFile) {
    $OutFile = Join-Path $RepoRoot 'payload\PlayParchisi-setup.zip'
}

# relPath -> source path. relPath uses forward slashes (zip convention).
$members = [ordered]@{
    'PlayParchisi-setup/PlayParchisi.exe'    = Join-Path $RepoRoot 'PlayParchisi.exe'
    'PlayParchisi-setup/adb.exe'             = Join-Path $RepoRoot 'adb.exe'
    'PlayParchisi-setup/AdbWinApi.dll'       = Join-Path $RepoRoot 'AdbWinApi.dll'
    'PlayParchisi-setup/AdbWinUsbApi.dll'    = Join-Path $RepoRoot 'AdbWinUsbApi.dll'
    'PlayParchisi-setup/payload/wsa.zip'                  = Join-Path $RepoRoot 'payload\wsa.zip'
    'PlayParchisi-setup/payload/base.apk'                 = Join-Path $RepoRoot 'payload\base.apk'
    'PlayParchisi-setup/payload/split_config.arm64_v8a.apk'          = Join-Path $RepoRoot 'payload\split_config.arm64_v8a.apk'
    'PlayParchisi-setup/payload/split_config.etc1_rgb8_group_hd.apk' = Join-Path $RepoRoot 'payload\split_config.etc1_rgb8_group_hd.apk'
    'PlayParchisi-setup/payload/firefox_base.apk'         = Join-Path $RepoRoot 'payload\firefox_base.apk'
    'PlayParchisi-setup/payload/firefox_split_config.mdpi.apk'      = Join-Path $RepoRoot 'payload\firefox_split_config.mdpi.apk'
    'PlayParchisi-setup/payload/firefox_split_config.x86_64.apk'    = Join-Path $RepoRoot 'payload\firefox_split_config.x86_64.apk'
}

# --- preflight: fail fast rather than 20 minutes into a 1.5 GB write --------
$missing = @()
foreach ($k in $members.Keys) {
    if (-not (Test-Path $members[$k])) { $missing += $members[$k] }
}
if ($missing) {
    Write-Error ("missing source files:`n  " + ($missing -join "`n  "))
    exit 1
}

# --- README for the bundle --------------------------------------------------
$readme = Join-Path $env:TEMP 'bundle-readme.txt'
@"
Parchisi STAR - one-click launcher (Windows 11)
================================================

1. Extract EVERYTHING into one folder. Keep the folder structure:
       PlayParchisi-setup\
         PlayParchisi.exe
         adb.exe
         payload\
           wsa.zip
           *.apk

2. Double-click PlayParchisi.exe.

First run only:
   - Answer YES to the UAC prompt (installing needs admin once).
   - If Windows says a restart is required, let it restart. The launcher
     resumes on its own and finishes installing.
   - Extraction + install takes a few minutes. Watch the progress window;
     it disappears when the game starts.

Later runs: double-click, and the game opens with no window at all.

If nothing happens or an Avast alert appears:
   - Avast false-positives (IDP.Generic) on anything touching WsaClient.exe.
   - Add this folder and C:\WSA to Avast exclusions, restore the file from
     Avast's Quarantine (Protection > Quarantine), then run again.

If the game opens but stays black: the texture split is wrong. The launcher
detects this and re-installs automatically - check launcher.log.

The Facebook button needs Firefox, which this bundle installs and sets as the
default browser automatically. Log in yourself; the launcher never enters
credentials.

Notes:
   - Windows 11 build 22000+ required. BIOS virtualization must be on.
   - payload\wsa.zip is Microsoft's Windows Subsystem for Android.
   - payload\*.apk are from Google Play, redistributed here.
   - Nothing here is run at startup; no data leaves your machine.
"@ | Set-Content -Path $readme -Encoding UTF8
$members['PlayParchisi-setup/README.txt'] = $readme

# --- build ------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

$totalExpected = 0L
foreach ($k in $members.Keys) { $totalExpected += (Get-Item $members[$k]).Length }

Write-Host "building $OutFile"
Write-Host ("  {0} members, {1:N1} MB expected" -f $members.Count, ($totalExpected / 1MB))

$sw = [Diagnostics.Stopwatch]::StartNew()
$n = 0

$fs = [IO.File]::Open($OutFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
$zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)

try {
    foreach ($k in $members.Keys) {
        $src = $members[$k]
        $len = (Get-Item $src).Length

        # Already-compressed members (zip/apk) would waste CPU to recompress;
        # store them. Small text/exe members compress meaningfully.
        $level = if ($len -gt 10MB) {
            [IO.Compression.CompressionLevel]::NoCompression
        } else {
            [IO.Compression.CompressionLevel]::Optimal
        }

        $entry = $zip.CreateEntry($k, $level)
        $in  = [IO.File]::OpenRead($src)
        $out = $entry.Open()
        try     { $in.CopyTo($out) }
        finally { $out.Dispose(); $in.Dispose() }

        $n++
        Write-Host ("  [{0,2}/{1}] {2,-56} {3,7:N1} MB" -f `
            $n, $members.Count, $k, ($len / 1MB))
    }
}
finally {
    $zip.Dispose()
    $fs.Dispose()
}
$sw.Stop()

if (Test-Path $readme) { Remove-Item $readme -Force -ErrorAction SilentlyContinue }

$actual = (Get-Item $OutFile).Length
Write-Host ""
Write-Host ("  written: {0:N1} MB in {1:N0}s ({2:N1} MB/s)" -f `
    ($actual / 1MB), $sw.Elapsed.TotalSeconds, (($actual / 1MB) / $sw.Elapsed.TotalSeconds))

# --- verify: reopen and assert every required entry landed -------------------
Write-Host "verifying..."
$verify = [IO.Compression.ZipFile]::OpenRead($OutFile)
try {
    $names = @($verify.Entries | ForEach-Object { $_.FullName })

    $required = @(
        'PlayParchisi-setup/PlayParchisi.exe',
        'PlayParchisi-setup/adb.exe',
        'PlayParchisi-setup/README.txt',
        'PlayParchisi-setup/payload/wsa.zip',
        'PlayParchisi-setup/payload/base.apk',
        'PlayParchisi-setup/payload/split_config.arm64_v8a.apk',
        'PlayParchisi-setup/payload/split_config.etc1_rgb8_group_hd.apk',
        'PlayParchisi-setup/payload/firefox_base.apk',
        'PlayParchisi-setup/payload/firefox_split_config.mdpi.apk',
        'PlayParchisi-setup/payload/firefox_split_config.x86_64.apk'
    )

    $fail = 0
    foreach ($r in $required) {
        if ($names -contains $r) { Write-Host "  [ok] $r" }
        else { Write-Host "  [MISSING] $r"; $fail++ }
    }

    # wrong texture split = black game (the one failure that must never ship)
    if ($names -contains 'PlayParchisi-setup/payload/split_config.etc1_rgb8_group_hd.apk') {
        Write-Host "  [ok] PNG texture split present (not .astc)"
    } else { $fail++ }

    # privacy: userdata.vhdx holds Aurora session, Firefox logins/cookies
    $leak = @($verify.Entries | Where-Object { $_.FullName -like '*userdata.vhdx*' })
    if ($leak.Count -gt 0) { Write-Host "  [LEAK] userdata.vhdx present!"; $fail++ }
    else { Write-Host "  [ok] userdata.vhdx absent (privacy)" }

    $msum = 0L
    foreach ($e in $verify.Entries) { $msum += $e.Length }
    Write-Host ("  entries: {0}, uncompressed total {1:N1} MB" -f $names.Count, ($msum / 1MB))

    if ($fail -gt 0) {
        Write-Host "VERIFICATION FAILED ($fail)"
        exit 2
    }
}
finally { $verify.Dispose() }

$sha = (Get-FileHash $OutFile -Algorithm SHA256).Hash
Write-Host ""
Write-Host "  SHA256: $sha"
Write-Host "  size  : $actual"
Write-Host "VERIFIED"
