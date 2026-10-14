# build-wsa-payload.ps1
# Compresses C:\WSA into payload\wsa.zip, excluding userdata.vhdx.
#
# WHY: userdata.vhdx is the live Android data disk. It holds the Aurora Store
# session, the Firefox profile (saved logins, cookies, autofill including a
# saved phone number seen at Facebook login), Magisk files, and installed app
# data. Shipping it would leak Yassine's personal data. It also regenerates
# automatically on first boot, so omitting it is safe.
#
# WSA MUST be shut down first: the .vhdx files are exclusively locked while the
# subsystem runs, and compressing a locked, actively-written file produces a
# silently corrupt archive.

param(
    [string]$Source = 'C:\WSA',
    [string]$Out    = 'D:\Projects\parchisi\payload\wsa.zip',
    [string]$Log    = 'D:\Projects\parchisi\payload\build-wsa.log'
)

$ErrorActionPreference = 'Stop'

function Log([string]$m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m
    Add-Content -Path $Log -Value $line -Encoding UTF8
    Write-Output $line
}

New-Item -ItemType Directory -Path (Split-Path $Out -Parent) -Force | Out-Null
Set-Content -Path $Log -Value "" -Encoding UTF8

Log "=== build-wsa-payload start ==="
Log "source: $Source"
Log "output: $Out"

# --- 1. stop WSA -------------------------------------------------------------
Log "step 1: ensure WSA stopped"
$wsa = "$env:LOCALAPPDATA\Microsoft\WindowsApps\MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe\WsaClient.exe"
for ($i = 1; $i -le 3; $i++) {
    if (-not (Get-Process -Name WsaClient, WsaService -ErrorAction SilentlyContinue)) { break }
    Log "  WSA running, shutdown attempt $i"
    if (Test-Path $wsa) { Start-Process -FilePath $wsa -ArgumentList '/shutdown' -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 12
}
if (Get-Process -Name WsaClient, WsaService -ErrorAction SilentlyContinue) {
    Log "  still up, forced kill"
    Get-Process -Name WsaClient, WsaService -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 15
}
if (Get-Process -Name WsaClient, WsaService -ErrorAction SilentlyContinue) {
    Log "  FATAL: WSA will not stop. Aborting rather than zipping locked files."
    exit 1
}
Log "  WSA stopped"

# --- 2. wait for the .vhdx to unlock ----------------------------------------
Log "step 2: wait for .vhdx unlock"
$deadline = (Get-Date).AddSeconds(120)
$ok = $false
while ((Get-Date) -lt $deadline) {
    $locked = 0
    Get-ChildItem $Source -Recurse -Filter '*.vhdx' -Force -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $fs = [IO.File]::Open($_.FullName, 'Open', 'Read', 'None')
            $fs.Close()
        } catch { $locked++ }
    }
    if ($locked -eq 0) { $ok = $true; break }
    Log "  $locked still locked, waiting..."
    Start-Sleep -Seconds 5
}
if (-not $ok) { Log "  FATAL: .vhdx never unlocked. Aborting."; exit 1 }
Log "  all unlocked"

# --- 3. enumerate ------------------------------------------------------------
Log "step 3: enumerate"
$files = Get-ChildItem $Source -Recurse -File -Force -ErrorAction SilentlyContinue |
         Where-Object { $_.Name -ne 'userdata.vhdx' }
$raw = ($files | Measure-Object Length -Sum).Sum
Log ("  {0} files, {1:N1} MB raw (userdata.vhdx excluded)" -f $files.Count, ($raw / 1MB))

# --- 4. compress -------------------------------------------------------------
# Deflate, not LZMA2. LZMA2 gets a better ratio but needs a 7z binary bundled
# and re-fetched on the target PC -- and Avast ate 7z.exe during this build.
# Deflate is extracted natively by .NET 4.8 ZipArchive, so the target needs
# zero external tools. Measured 42% on system.vhdx, which fits under the
# 2 GiB GitHub asset cap as a single file.
Log "step 4: compress (Deflate via .NET ZipArchive, Optimal)"
$sw = [Diagnostics.Stopwatch]::StartNew()
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$ofs = [IO.File]::Open($Out, 'Create')
$zip = New-Object System.IO.Compression.ZipArchive($ofs, [System.IO.Compression.ZipArchiveMode]::Create)
$n = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Source.Length + 1)
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    $n++
    if ($n % 25 -eq 0) { Log ("  {0}/{1} files" -f $n, $files.Count) }
}
$zip.Dispose()
$ofs.Dispose()
$sw.Stop()

$z = (Get-Item $Out).Length
Log ("  done: {0:N1} MB -> {1:N1} MB  ratio {2:P1}  in {3:N1}s" -f ($raw/1MB), ($z/1MB), ($z/$raw), $sw.Elapsed.TotalSeconds)
Log ("  under GitHub 2 GiB cap: {0}" -f ($z -lt 2147483648))
Log "=== build-wsa-payload complete ==="
