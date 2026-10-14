# PROMPT.md — Reproduce this project from scratch

> **Audience:** an AI coding agent (OpenCode) given this repo with no prior context.
> **Goal:** reach the state where `PlayParchisi.exe` double-clicks into a running,
> focused Parchisi STAR Online game under WSA on Windows.
>
> Read the **Traps** section completely before touching anything. Every trap there
> cost real time to discover and most of them will silently corrupt your work if you
> hit them cold. Several are non-obvious and will look like bugs in your own code
> when they are not.

---

## 1. What exists in this repo

| Path | Purpose |
|---|---|
| `PlayParchisi.exe` | The deliverable. GUI-subsystem launcher, windowless. |
| `ParchisiLauncher.cs` | Its source. Edit this, then rebuild. |
| `parchisi.ico` | Icon resource, embedded via `/win32icon`. |
| `README.md` | Human-facing build + usage notes. |
| `tools/build-wsa-payload.ps1` | Compresses `C:\WSA` into `payload/wsa.zip`. |
| `payload/` | **gitignored.** ~800 MB build output (WSA zip + APKs). Never commit. |

The launcher resolves `adb.exe` and `launcher.log` through
`AppDomain.CurrentDomain.BaseDirectory` — the exe's own folder, never the working
directory. Do not introduce hardcoded paths; the folder must stay portable.

### Build command

Windows ships the compiler. No .NET SDK, no Python, no NuGet.

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tmp = "$env:TEMP\opencode\_build\PlayParchisi.exe"   # MUST build here, see trap A3
New-Item -ItemType Directory -Path (Split-Path $tmp) -Force | Out-Null
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
       /win32icon:parchisi.ico `
       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
       /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
       /out:"$tmp" ParchisiLauncher.cs
Copy-Item $tmp .\PlayParchisi.exe -Force
```

> **`csc` here is C# 5 only.** No `$"..."` interpolation, no `?.`, no `out var`,
> no expression-bodied members. Violating this fails the build, not your logic.

`/target:winexe` sets PE subsystem 2. That is structural proof no console window is
possible. Verify it if ever in doubt:

```powershell
# subsystem offset: PE sig (4) + COFF (20) + 68
$fs=[IO.File]::OpenRead($exe); $b=New-Object byte[] 64; $null=$fs.Read($b,0,64)
$fs.Seek([BitConverter]::ToInt32($b,60),[IO.SeekOrigin]::Begin)|Out-Null
$h=New-Object byte[] 96; $null=$fs.Read($h,0,96); $fs.Close()
[BitConverter]::ToUInt16($h, 4+20+68)   # 2 = GUI, 3 = console
```

### Trap A6 — source/binary desync nearly destroyed reproducibility

The repo once shipped a **windowless binary paired with form-based source**: commit
`0710adc "Run windowless"` contained `1 file changed, 0 insertions(+), 0 deletions(-)`
— only the exe, never the source. The real source sat in `%TEMP%`, which Windows
wipes. Anyone rebuilding would have silently regressed to a visible window.

Recovered and fixed. **Provenance is NOT checkable by hashing** — a control test
building the *same* source twice showed `.text DIFF` and `.rsrc DIFF` between the two
builds (per-build MVID GUID + PE timestamp). Section hashes therefore prove nothing.
What does prove it: inspect the binary's contents with a **byte-pattern search**.

There are two kinds of marker and each needs its own encoding:

| Marker kind | Heap | Encoding | Example |
|---|---|---|---|
| Member/method name | `#Strings` | ASCII, null-terminated | `ShowInTaskbar`, `StartSilent`, `add_Shown` |
| String literal | `#US` | UTF-16LE | `install-multiple`, `VirtualMachinePlatform` |

```powershell
function Test-Bytes([byte[]]$hay,[byte[]]$needle){
  for($i=0;$i -le $hay.Length-$needle.Length;$i++){
    $m=$true
    for($j=0;$j -lt $needle.Length;$j++){ if($hay[$i+$j] -ne $needle[$j]){$m=$false;break} }
    if($m){ return $true }
  }
  return $false
}
$b=[IO.File]::ReadAllBytes($exe)

Test-Bytes $b ([Text.Encoding]::ASCII.GetBytes('add_Shown'))      # true -> STALE form source
Test-Bytes $b ([Text.Encoding]::ASCII.GetBytes('ShowInTaskbar'))  # true -> windowless source
Test-Bytes $b ([Text.Encoding]::Unicode.GetBytes('install-multiple'))  # true -> bootstrapper present
```

**Do NOT decode the whole file and call `.Contains()`.** Two traps, both hit in practice:

- Decoding as ASCII and searching for a *literal* fails every time — UTF-16 bytes are
  null-interleaved (`i\0n\0s\0t\0...`), so `Contains('install-multiple')` returns false
  on a binary that genuinely contains it.
- Decoding the whole file as UTF-16 works only for literals that happen to land on an
  even file offset. `#US` entries sit at arbitrary offsets, so some literals match and
  others silently do not — e.g. `install-multiple` matched while
  `provisioning (administrator)` did not, from the *same* binary. That partial-match
  pattern is the signature of this bug; when you see it, your method is wrong, not the
  binary.

Before trusting any "this binary matches this source" claim, use that byte search.
**Do not use SHA256 equality** — plain `csc` is nondeterministic, so a rebuilt exe
never matches the shipped one byte for byte (control test: same source, two builds,
`.text` and `.rsrc` both differed).

---

## 2. Target software stack

| Component | Exact state | Why it matters |
|---|---|---|
| WSA | `2407.40000.4.0`, registered-by-manifest at `C:\WSA` | Not a Store install. See trap B1. |
| Game | `com.superking.parchisi.star` vc **531** / `1.329.1` | |
| Game splits | `base.apk` + `split_config.arm64_v8a.apk` + **`split_config.etc1_rgb8_group_hd.apk`** | The `etc1_rgb8` split is the PNG-texture build. See trap B2. |
| Aurora Store | `4.8.4` (`aurora-store_76.apk`) | Anonymous Play login. No Google account. |
| Firefox | `org.mozilla.firefox` `157.0.1`, holds `android.app.role.BROWSER` | Required for Facebook login. See trap B3. |
| adb | Bundled `adb.exe` + `AdbWinApi.dll` + `AdbWinUsbApi.dll` | Port **58526**. |
| Activity | `com.superking.parchisi.star/org.cocos2dx.cpp.AppActivity` | |

---

## 3. Traps — read all of these first

### A. Avast (the dominant hazard)

**A1. Avast false-positives on `IDP.Generic` for anything that spawns `WsaClient.exe`.**
It quarantined the launcher, then kept denying creation of *that exact path* even for
Microsoft-signed content. Proved filename-driven: copying signed `where.exe` to
`ParchisiSTAR.exe` was denied identically.

→ Name the launcher **`PlayParchisi.exe`**. Never rename it back.

**A2. Add the project folder to Avast exclusions before any rebuild.**
Without it the exe disappears ~24s after it spawns `WsaClient.exe`. Chest index:
`C:\ProgramData\Avast Software\Avast\chest\index.xml`.

**A3. `csc.exe` cannot write into the excluded folder at all.**
→ Always build to `%TEMP%`, then `Copy-Item` the result over.

**A4. Avast also eats `7z.exe` from temp.** Observed during payload build: the file
existed, then vanished from `%TEMP%\opencode\7zbundle`. Prefer Deflate — see trap C1.

**A5. Repairing a quarantined `WsaClient.exe`.** Staged-bytes directory swap: Avast
permits metadata operations on the folder, so stage correct bytes under a different
name, then swap. Afterwards `Remove-AppxPackage` and re-register.
Verify: `Get-AppxPackage MicrosoftCorporationII.WindowsSubsystemForAndroid` →
`Status: Ok`, and SHA256 `B6BA78401E16ECE011D78E433C0532B97F00FEC66E58B4F97B61F4CABADC1054`.

### B. Install & login

**B1. WSA here is registered-by-manifest, not Store-installed.**
`C:\WSA\AppxManifest.xml` is the hook:
```powershell
Add-AppxPackage -DisableDevelopmentMode -Register C:\WSA\AppxManifest.xml
```
Microsoft retired WSA and delisted it in March 2025. No `.msixbundle` exists on this
machine. The folder *is* the installer.

**B2. The `.astc` split renders black on WSA.**
Play serves different splits per device profile. You must get the
`split_config.etc1_rgb8_group_hd.apk` (PNG) variant. If textures are black, suspect
this before anything else — it is not a driver or GPU problem.

**B3. Aurora's home feed crashes.** `IllegalArgumentException: Key
"com.scopely.monopolygo" was already used` — a LazyColumn duplicate-key bug.
→ **Always** reach apps via `market://details?id=<pkg>`. Never browse the feed.

**B4. Facebook login fails in the built-in WebView.** The game SDK logs
`act=NATIVE_WITH_FALLBACK` and falls back to web. The URL is intercepted by
`com.microsoft.windows.userapp/.BrowserIntentHandler`, rendering in WSA's WebView
**118.0.5993.111**, which Facebook's UA gate rejects with *"Facebook is not available
on this browser."*
→ Install a current Firefox. Do not touch the game. Verified: the tap now routes to
`org.mozilla.fenix.customtabs.ExternalAppBrowserActivity` and renders the real login
page.

**B5.** Facebook, `com.google.android.gms`, `com.android.vending` and `com.android.gsf`
are all present in WSA, so a native FB route remains possible if ever wanted.

### C. Payload packaging

**C1. Use Deflate, not LZMA2.** Measured `system.vhdx`: 976 MB → 412 MB (42.2%) in
47.6 s. That puts all of WSA near ~900 MB, under GitHub's 2 GiB asset cap as a single
file. Decisive second benefit: **.NET 4.8 `ZipArchive` extracts Deflate natively**, so
the target PC needs zero external binaries. LZMA2 needs a bundled 7z, and Avast eats
it (trap A4).

**C2. `userdata.vhdx` MUST be excluded.** It is the live Android data disk: Aurora
session, Firefox profile with saved logins and cookies, a saved phone number visible
as an autofill chip at Facebook login, Magisk files, installed app data. Shipping it
leaks personal data. It regenerates on first boot, so omitting it is safe.

**C3. Do not re-zip the APKs.** They are already deflate-compressed; zipping again
wastes time and gains nothing.

**C4. Git cannot hold the big files.** `split_config.etc1_rgb8_group_hd.apk` is
123 MB, over git's 100 MB hard limit. APKs and the WSA zip belong in **release
assets**, never in git history. Committing ~800 MB to git is permanent — a later
delete does not recover it.

**C5. WSA must be shut down before compressing.** The `.vhdx` are exclusively locked
while it runs; compressing a locked, actively-written file yields a silently corrupt
archive. `WsaClient /shutdown`, then wait, then verify by opening each `.vhdx` with
`FileShare.None` before starting.

### D. Cold boot & focus

**D1. The adb port listens before Android's `activity` service registers.**
`am start` fired too early fails. Gate on `service check activity`, then retry.

**D2. Package manager then returns `Error type 3: Activity class does not exist` for
several seconds.** This is a second, separate failure after D1. Retry on it too. Both
appear in the shipped launcher's log as a retry line.

**D3. WSA reaps backgrounded apps ~20s after focus loss.** The game auto-fires a
Facebook login on startup that can background it. Guard by sending BACK — **but only
after the game first holds focus.** BACK-spamming during cold start presses BACK into
the WSA home screen and breaks the boot.

**D4. Check focus before suspecting rendering.** `adb shell dumpsys window | grep
mCurrentFocus`. Black screen usually means the app lost focus and was reaped, not a
texture bug. (The real texture bug is trap B2 — distinguish them by focus state.)

**D5. The adb port may not listen after a WSA restart** even though Developer mode
reads "On". Toggle Developer mode **Off → On** in WSA Settings. The flag lives in the
app-local registry hive `Settings\settings.dat`, which is locked while WSA runs and
cannot be edited live. A plain restart usually recovers the port.

### E. Tooling traps (PowerShell 5.1 + Windows)

**E1. `& $wsa /launch` blocks forever** — PowerShell waits on the process. Use
`Process.Start`, which returns in ~134 ms. The shipped launcher does this correctly.

**E2. Screenshots: PowerShell's `>` corrupts binary.** Must wrap in cmd:
```powershell
cmd /c "`"$adb`" exec-out screencap -p > `"$out.png`""
```

**E3.** WSA is **1920×1080**; pixel coords map 1:1 to `input tap`. Minimized windows
and no-app-window states produce black captures.

**E4.** `Measure-Object -Property @{e={...}}` is invalid syntax in PS 5.1.

**E5.** `Sort-Object -Unique` on version strings is lexicographic — it put `7z1604`
before `7z2501`. Sort numerically.

**E6.** `reg load` into HKLM needs a privilege not held here; loading into HKCU works.
Copying `settings.dat` fails while WSA runs (file locked).

**E7.** Windows paths passed into `adb shell` lose their backslashes
(`C:\Users\...` → `C:Users...`). Quote or use forward slashes for on-device paths.

**E8. Cross-drive `Move-Item` is copy-then-delete and can fail partway**, leaving an
empty husk at the source. Verify both sides after any move — count files on
destination *and* source before declaring success.

---

## 4. Fresh-PC provisioning — implemented

`PlayParchisi.exe` now does the provisioning itself. It splits into two phases so a
normal launch never prompts for UAC:

**Phase A — provision** (runs only if `C:\WSA\AppxManifest.xml` *or*
`WsaClient.exe` is missing; self-elevates via `Verb = "runas"`, child launched with
`--provision`):

1. Refuses if registry `CurrentBuild` < 22000.
2. Reads `(Get-WindowsOptionalFeature -Online -FeatureName 'VirtualMachinePlatform').State`.
   - **Critical:** this call *throws* without elevation, so it can never be in the
     non-elevated hot path. `NeedsProvisioning()` is deliberately file-exists only.
   - If not `Enabled` → `Enable-WindowsOptionalFeature -NoRestart`, write
     `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce` =
     `"...\PlayParchisi.exe" --provision`, then `shutdown /r /t 60`. Clicking the
     window runs `shutdown /a` (a 60s window, not a silent yank).
3. `StageWsa()` — uses existing `C:\WSA`, else extracts `payload\wsa.zip`
   (312 entries, ~956 MB → 2,135 MB). `ZipArchive` + `ExtractToFile`, per-entry so
   progress can be logged.
4. `Add-AppxPackage -DisableDevelopmentMode -Register 'C:\WSA\AppxManifest.xml'`,
   verified by re-querying `Get-AppxPackage`.
5. If the package is registered but `WsaClient.exe` is missing, warn that Avast
   quarantined it (trap A1).

**Phase B — launch** (never elevated):

6. Boot WSA → adb handshake → framework ready (existing logic).
7. `EnsureGameInstalled()` — `shell pm path`; installs `install-multiple -r -g` of the
   three game splits if absent, and **re-installs if `etc1_rgb8_group_hd` is missing**
   because the `.astc` variant renders black. Verified after install by re-reading
   `pm path`.
8. `EnsureFirefoxInstalled()` — same pattern with the three Firefox splits.
9. `EnsureBrowserRole()` —
   `shell dumpsys role`, find `android.app.role.BROWSER`, confirm `org.mozilla.firefox`
   appears within the next ~500 chars; else
   `shell cmd role add-role-holder android.app.role.BROWSER org.mozilla.firefox`.
   - Note: `cmd role` here has **only** `add-role-holder`, `remove-role-holder`,
     `clear-role-holders`, `set-bypassing-role-qualification`. There is **no**
     `get-role-holders` and **no** `query`, and `pm getDefaultBrowserPackageNameAsUser`
     does not exist either — `dumpsys role` is the only read-back.
   - `add-role-holder` prints nothing on success; `dumpsys role` reports
     `RC=True`. Do not treat empty output as failure.
10. `HideUi()` then launch + focus guard (existing logic).

**Install split sets** (reproduce exactly — both are correct on `abilist =
x86_64,arm64-v8a,x86,armeabi-v7a,armeabi`):

```
game     : base.apk + split_config.arm64_v8a.apk + split_config.etc1_rgb8_group_hd.apk
firefox  : firefox_base.apk + firefox_split_config.mdpi.apk + firefox_split_config.x86_64.apk
```

The apparent mismatch (arm64 game vs x86_64 Firefox on one x86_64 host) is expected:
WSA advertises **both** `x86_64` and `arm64-v8a`. Play serves Firefox's x86_64 build
(first match in `abilist`) and the game's arm64 build (it has no x86_64 build).
`base.apk` carries no native libs; the ABI lives entirely in the split.

### Remaining hard walls

| Requirement | Why the exe cannot do it |
|---|---|
| BIOS virtualization | Firmware setting; no software path. |
| Developer mode toggle | Storage location of the flag still unfound; may need one manual Off→On. |

`HypervisorPresent` is the usable virtualization signal. Do **not** trust
`VirtualizationFirmwareEnabled` or `SecondLevelAddressTranslationExtensions` from
`Win32_Processor` while a hypervisor is already running — they report `False`
incorrectly because the hypervisor owns the extensions.

---

## 5. Verification checklist

Run in order. Each step must pass before the next.

```powershell
$adb = 'D:\Projects\parchisi\adb.exe'
& $adb connect 127.0.0.1:58526

# 1. package registered
Get-AppxPackage MicrosoftCorporationII.WindowsSubsystemForAndroid | Select Status,Version
#    expect Status: Ok, 2407.40000.4.0

# 2. bridge up
& $adb devices          # expect ... device

# 3. framework booted
& $adb shell getprop sys.boot_completed   # expect 1

# 4. PNG split, not ASTC
& $adb shell pm path com.superking.parchisi.star
#    must include split_config.etc1_rgb8_group_hd.apk, and no .astc split

# 5. browser role (Facebook fix)
& $adb shell dumpsys role   # expect org.mozilla.firefox holds BROWSER

# 6. launch, then confirm focus
& $adb shell am start -n com.superking.parchisi.star/org.cocos2dx.cpp.AppActivity
& $adb shell dumpsys window | Select-String mCurrentFocus
#    expect ... com.superking.parchisi.star/org.cocos2dx.cpp.AppActivity
```

### Cold-boot end-to-end test

The only test that catches traps D1/D2/D3:

```powershell
& "$env:LOCALAPPDATA\Microsoft\WindowsApps\MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe\WsaClient.exe" /shutdown
Start-Sleep 20
Remove-Item D:\Projects\parchisi\launcher.log -ErrorAction SilentlyContinue
Start-Process D:\Projects\parchisi\PlayParchisi.exe -WorkingDirectory D:\Projects\parchisi
# then read launcher.log:
#   WSA up after ~7s  ->  Framework ready  ->  retry line (expected, D1+D2)
#   -> game has focus   ->  == done: game running ==
```

### Windowless invariant

The launcher must never own a visible window on success. Enumerate to prove it:

```powershell
# MUST be in the SAME shell invocation as the launch: Add-Type state does not
# survive between `shell` calls, and a type-not-found error leaves $v as $null,
# which reports a FALSE PASS of 0 violations.
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Text;
public class W{
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h,out int pid);
 [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h,StringBuilder s,int n);
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 public static System.Collections.Generic.List<string> Vis(int me){
  var r=new System.Collections.Generic.List<string>();
  EnumWindows(delegate(IntPtr h,IntPtr p){
   int pid;GetWindowThreadProcessId(h,out pid);
   if(pid==me&&IsWindowVisible(h)){var sb=new StringBuilder(256);GetWindowTextW(h,sb,256);r.Add(h+" '"+sb+"'");}
   return true;},IntPtr.Zero);
  return r;}
}
"@ -ReferencedAssemblies System.Windows.Forms

$viol=0
while(-not $p.HasExited){ Start-Sleep 5; $viol += [W]::Vis($p.Id).Count }
"violations: $viol"    # expect 0
```

The log window appears **only on failure**, so a silent no-op is never possible.

### Measurement traps — three ways verification lies to you

**M1. `mCurrentFocus=null` does not mean the game lost focus.** WSA runs **two
displays** (a home display and an app display). `dumpsys window` prints one
`mCurrentFocus` per display, so `null` (home) appears alongside a real
`mCurrentFocus=Window{... com.superking.parchisi.star/...AppActivity}`. Always read
*all* matches plus `topResumedActivity`:

```powershell
& $adb shell dumpsys window | Select-String 'mCurrentFocus'      # expect >= 2 lines
& $adb shell dumpsys activity activities | Select-String topResumedActivity
& $adb shell "ps -A | grep parchisi"                              # expect 1 process
```

**M2. Mean pixel brightness is misleading for a portrait game.** This game renders
portrait (~460px wide) centred in a 1920x1080 landscape frame, so black letterbox
bars dominate the frame and yield a mean of ~11/255 — **not** a black screen. The
correct check is to look at the frame, not average it:

```powershell
cmd /c "`"$adb`" exec-out screencap -p > `"$out.png`""   # cmd wrapper, trap E2
```

A `.astc` texture failure (trap B2) shows an all-black *game area*; correct rendering
shows full art and readable text. Average brightness cannot tell those apart.

**M3. `Dispose()` before sampling returns NaN.** Read `$bm.Height`/sample the bytes,
*then* dispose. Disposing first zeroes the dimensions, the sample loop never runs,
`$n=0`, and `$sum/(3.0*$n)` gives `NaN` — which a careless `if ($avg -lt 8)` treats
as a pass in some code paths.

**M4. A running PowerShell terminal steals Windows focus**, so a screenshot taken
immediately after a command may show the wrong state. Set focus back first
(`SetForegroundWindow` on the `WsaClient` window) and re-capture.

---

## 6. State history (why things look the way they do)

- WSA was broken by Avast quarantining `WsaClient.exe`. Restored via staged swap.
- The game was reinstalled through Aurora rather than APKPure/Uptodown/XAPK mirrors,
  specifically to get the PNG texture split (trap B2).
- Launcher was renamed `ParchisiSTAR.exe` → `PlayParchisi.exe` because of the Avast
  filename trigger (trap A1).
- Three launcher bugs found by live cold-boot testing and fixed: duplicate log lines,
  `am start` before framework readiness, and BACK-spam at WSA home.
- Facebook login fixed by installing Firefox, not by modifying the game.
- Repo moved `C:\Users\ysmag\Desktop\game` → `D:\Projects\parchisi`.

## 7. State, verification status, open work

### Built and verified on this machine

| Claim | Evidence |
|---|---|
| Payload sound | `payload\wsa.zip`: 312 entries, 2,135.2 MB → 956.0 MB (44.8%), `AppxManifest.xml` present, `userdata.vhdx` **absent**, `metadata.vhdx` extracted **SHA256-identical** to source |
| Warm launch | `130s`, **0 visible-window violations**, self-exited, `== done: game running ==` |
| Cold launch (WSA down) | port closed → `WSA up after 7s` → framework ready → installs self-skip → `game has focus` → self-exited, `145s` |
| Game renders | fresh `screencap` shows full Parchisi STAR art, title, and all three buttons (PNG split confirmed working) |
| New phases are true no-ops when satisfied | log shows `Game installed, PNG texture split present` / `Firefox installed` / `Default browser: Firefox` with no reinstall |
| Source matches binary | shipped exe has `ShowInTaskbar`+`StartSilent`, lacks `add_Shown` (trap A6) |

### NOT yet verified — do not assume these work

- **The provisioning phase (Phase A) has never actually run.** This machine already
  has WSA, so `NeedsProvisioning()` returns false and every branch in `ProvisionNow()`,
  `StageWsa()`, `ScheduleResume()` and the UAC relaunch is **untested code**. It
  compiles and the branches are reasoned, but that is all.
- Untested specifically: `runas` relaunch, UAC-decline path, `Enable-WindowsOptionalFeature`,
  `RunOnce` write + reboot resume, `payload\wsa.zip` → `C:\WSA` extraction end-to-end,
  `Add-AppxPackage -Register`, and both `install-multiple` paths (they always skip here).
- The only honest full test is a real fresh Windows 11 VM: no `C:\WSA`, no WSA package,
  Developer mode off. Until that passes, treat section 4 as unproven.

### Open work

- **Upload deferred by request.** Publishing `payload\wsa.zip` + APKs as GitHub release
  assets waits until Yassine confirms the build works. APKs exceed git's 100 MB limit
  (`split_config.etc1_rgb8_group_hd.apk` 123 MB, `firefox_split_config.x86_64.apk`
  209 MB) so they are release-asset-only, never committed.
- `C:\Users\ysmag\Desktop\parchisi-star-wsa-setup.md` may still cite the old
  `C:\Users\ysmag\Desktop\game` path, and still needs the Avast-exclusion and
  "Developer mode Off→On" notes.
