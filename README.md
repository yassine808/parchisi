# Parchisi STAR launcher

One double-click that boots the whole chain for Parchisi STAR Online running under
Windows Subsystem for Android (WSA).

## What it does

**Provisioning phase** (only runs when WSA is absent — triggers a UAC prompt, then
resumes by itself via `RunOnce` if a reboot is needed):

1. Refuses on Windows builds below 22000 (WSA needs Windows 11).
2. Enables `VirtualMachinePlatform` if it is off. That is the one step needing a
   reboot: it writes `HKLM\...\RunOnce` and restarts in 60s (click the window to
   cancel), then resumes on its own at the next logon.
3. Stages `C:\WSA` — uses an existing folder if present, otherwise extracts
   `payload\wsa.zip`.
4. Registers it with
   `Add-AppxPackage -DisableDevelopmentMode -Register C:\WSA\AppxManifest.xml`.

**Launch phase** (always, never elevated):

5. Checks whether WSA's adb bridge is listening on `127.0.0.1:58526`.
6. If not, cold-boots WSA via `WsaClient.exe /launch wsa://<package>` (retries up to
   3x, 120s each).
7. Waits for Android's `activity` system service — the adb port comes up *before* the
   framework finishes registering, so firing too early fails with
   `Can't find service: activity`.
8. Installs the game if missing, **and re-installs it if the PNG texture split is
   absent** (`split_config.etc1_rgb8_group_hd.apk`; the `.astc` split renders black).
9. Installs Firefox if missing and sets it as the default browser
   (`cmd role add-role-holder android.app.role.BROWSER`), which is what makes
   Facebook login work.
10. Launches the game, retrying on `Error type 3: Activity class does not exist`
    (package manager still settling).
11. Watches window focus for 2 minutes. The game auto-fires a Facebook login on
    startup which backgrounds it, and WSA reaps backgrounded apps within ~20s — so it
    presses BACK, but only after the game has first held focus (otherwise it spams
    BACK at the WSA home screen during a cold start).

On success the process exits with **no window ever shown**; the log window appears
only if something fails. Steps 8 and 9 are cheap no-ops on an already-installed
machine, and the whole provisioning phase is a file-exists check there, so a normal
launch never prompts for UAC.

Progress is written to `launcher.log` next to the exe.

## Contents

| file | purpose |
|---|---|
| `PlayParchisi.exe` | the launcher, game icon embedded |
| `ParchisiLauncher.cs` | source |
| `parchisi.ico` | icon resource |
| `adb.exe` + `AdbWinApi.dll` + `AdbWinUsbApi.dll` | bundled platform-tools |
| `payload/` | **gitignored** build artifacts: `wsa.zip` + the game and Firefox APKs |
| `PROMPT.md` | full reproduction brief for an AI agent |
| `tools/build-wsa-payload.ps1` | builds `payload\wsa.zip` from `C:\WSA` |

adb is bundled deliberately: it normally lives in `%TEMP%`, which Windows wipes
automatically, and a launcher depending on it breaks silently on a later run.

## Building

Windows' built-in C# compiler, no .NET SDK or Python required:

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tmp = "$env:TEMP\opencode\_build\PlayParchisi.exe"   # csc cannot write here (see Avast)
New-Item -ItemType Directory -Path (Split-Path $tmp) -Force | Out-Null
& $csc /nologo /target:winexe /optimize+ /win32icon:parchisi.ico `
       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
       /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
       /out:"$tmp" ParchisiLauncher.cs
Copy-Item $tmp .\PlayParchisi.exe -Force
```

`csc` here is **C# 5 only** — no `$"..."` interpolation, no `?.`, no `out var`.

The two `System.IO.Compression*` references are required for the `payload\wsa.zip`
extraction step. `/target:winexe` sets PE subsystem 2, which makes a console window
structurally impossible.

## Avast caveats

Two separate blockers hit during development, both from Avast:

- **Behavioral IDP** flagged the first build as `IDP.Generic` (quarantined as
  `ChestId 0000006E`) roughly 24 seconds after it spawned `WsaClient.exe`.
- **Path-level block** — after quarantining, Avast kept denying creation of that exact
  filename. Reproduced by copying Microsoft-signed `where.exe` to the same name and
  watching it get denied too, which proved the trigger was the *name*, not the content.
  Hence `PlayParchisi.exe`.
- `csc.exe` writing directly into the folder is also intercepted, so the build outputs
  to a temp directory and the finished exe is copied over.

Add the folder to Avast exclusions if you rebuild.

## Prerequisites

The launcher provisions most of this itself on a fresh PC; the rest cannot be done
by software:

| Requirement | Handled how |
|---|---|
| Windows 11 build 22000+ | checked; refuses otherwise |
| BIOS virtualization enabled | **cannot be automated** — firmware setting |
| `VirtualMachinePlatform` feature | launcher enables it (UAC + one reboot) |
| `C:\WSA` / `payload\wsa.zip` | launcher extracts and registers it |
| Admin rights for registration | launcher self-elevates once (UAC) |
| Developer mode in WSA settings | **manual** if the adb port stays dead: toggle it Off → On |
| Game + Firefox + default browser | launcher installs them over adb |

Game install specifics:

- Installed via Aurora Store anonymous login (no Google account) — or from
  `payload\*.apk` as the launcher does.
- Must be the Play-delivered `split_config.etc1_rgb8_group_hd.apk` (PNG textures),
  **not** the `.astc` split, which renders black on WSA. The launcher verifies this
  split is present and re-installs if it is not.
- Firefox must hold `android.app.role.BROWSER`: WSA's built-in WebView is Chromium
  118 and Facebook rejects it with "Facebook is not available on this browser".

A normal launch on an already-provisioned PC never asks for UAC and never shows a
window.
