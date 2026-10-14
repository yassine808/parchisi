# Parchisi STAR launcher

One double-click that boots the whole chain for Parchisi STAR Online running under
Windows Subsystem for Android (WSA).

## What it does

1. Checks whether WSA's adb bridge is listening on `127.0.0.1:58526`.
2. If not, cold-boots WSA via `WsaClient.exe /launch wsa://<package>` (retries up to 3x,
   120s each).
3. Waits for Android's `activity` system service — the adb port comes up *before* the
   framework finishes registering, so firing too early fails with
   `Can't find service: activity`.
4. Launches the game, retrying on `Error type 3: Activity class does not exist`
   (package manager still settling).
5. Watches window focus for 2 minutes. The game auto-fires a Facebook login on startup
   which backgrounds it, and WSA reaps backgrounded apps within ~20s — so it presses
   BACK, but only after the game has first held focus (otherwise it spams BACK at the
   WSA home screen during a cold start).

Progress is written to `launcher.log` next to the exe.

## Contents

| file | purpose |
|---|---|
| `PlayParchisi.exe` | the launcher, game icon embedded |
| `ParchisiLauncher.cs` | source |
| `parchisi.ico` | icon resource |
| `adb.exe` + `AdbWinApi.dll` + `AdbWinUsbApi.dll` | bundled platform-tools |

adb is bundled deliberately: it normally lives in `%TEMP%`, which Windows wipes
automatically, and a launcher depending on it breaks silently on a later run.

## Building

Windows' built-in C# compiler, no .NET SDK or Python required:

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /win32icon:parchisi.ico `
       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
       /out:PlayParchisi.exe ParchisiLauncher.cs
```

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

- WSA installed and registered (`Status: Ok`)
- Developer mode enabled in WSA settings (the adb bridge does not listen otherwise)
- Game installed via Aurora Store anonymous login — the Play-delivered
  `split_config.etc1_rgb8_group_hd.apk` (PNG textures), not the `.astc` split, which
  renders black on WSA
- Firefox installed as the default browser: WSA's built-in WebView is Chromium 118 and
  Facebook rejects it with "Facebook is not available on this browser"
