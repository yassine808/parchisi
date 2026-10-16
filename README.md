# Parchisi STAR — one-exe launcher for Windows

A single double-clickable `PlayParchisi.exe` that takes a plain Windows 11 PC from
"nothing installed" to "Parchisi STAR is running". It checks the machine, fetches
what it is missing, installs the game, and leaves when the game closes.

There is no installer, no driver, no service, no registry editing and no folder to
clean up. It is one file, it tells you what it is doing, and it logs everything.

---

## Download

Grab `PlayParchisi.exe` from the
[latest release](https://github.com/yassine808/parchisi/releases/latest)
and double-click it.

The exe is self-elevating: Windows will show a UAC prompt, because registering the
Android subsystem needs administrator rights. That is the only thing the launcher
asks of you up front.

> **Name is load-bearing.** The file must stay `PlayParchisi.exe`. Antivirus
> heuristics key on the filename when deciding that a process is spawning the
> Windows Subsystem for Android's client, and a generic name makes that verdict
> more likely. If you rename it and it stops working, rename it back.

---

## What it does

The launcher runs **12 checks in order**. Every check either passes, or fails with
a window naming the check, the cause and what to do about it. Nothing is guessed.

| # | Check | What it verifies |
|---|-------|------------------|
| 1 | Administrator rights | Running elevated (UAC) |
| 2 | Windows 11 | Build 22000 or newer — WSA does not exist on Windows 10 |
| 3 | virtualization | VT-x / AMD-V on, Virtual Machine Platform enabled |
| 4 | Developer Mode | Turned on so the Android bridge can be used |
| 5 | WSA payload | The Android subsystem archive is present, or is downloaded |
| 6 | WSA dependencies | The runtime packages WSA needs |
| 7 | WSA registration | The subsystem is registered with Windows |
| 8 | userdata template | The virtual disk template the subsystem boots from |
| 9 | WsaService | The background service that runs the subsystem |
| 10 | WSA boot | The subsystem actually boots to a usable state |
| 11 | game files + install | The game APKs are fetched and installed |
| 12 | launch | The game starts and its window appears |

Once the game window is gone, the launcher exits — it stays alive only as long as
the game does, so it never sits in the background holding anything.

Every run writes `launcher.log` beside the exe, with a timestamp on each line. If
a check fails, the log has the detail and the failure window has a button to open
it.

---

## What it downloads, and when it asks

The launcher will not download anything without asking you first. Two questions,
both showing the exact size and the source before you agree:

1. **The Windows Subsystem for Android** — roughly **0.70 GB**. Microsoft
   discontinued WSA, so there is no official source; this comes from the community
   build at [`MustardChef/WSABuilds`](https://github.com/MustardChef/WSABuilds).
   The launcher picks the GApps x64 build from the newest release.

2. **The game files** — the game APKs, Firefox and the `adb` installer tool,
   bundled as one `ParchisiPayload.zip` from this repository's own
   [releases](https://github.com/yassine808/parchisi/releases).

Both questions name the size in GB — not MB, because "713 MB" reads as small right
up until it is 0.7 GB of your bandwidth — and both offer the source as a link so
you can look before you commit.

If you decline, the launcher stops having changed nothing. If a download is
interrupted, it resumes rather than starting over, and when it finishes it is
checked against the size and the SHA-256 published on the release.

Already-downloaded files are reused, so a second run costs nothing.

---

## Why Firefox is in there

The game's own built-in browser is too old for Facebook: the WebView reports a
user agent Facebook rejects, so **logging in through the game fails**.

The launcher therefore installs Firefox as well and makes it the default browser
inside Android, so a login tap opens in a browser that Facebook accepts. This step
is best-effort — if it fails, the game still runs and you just cannot log in with
Facebook.

---

## The texture split, and why the game would be black without it

Android games ship their graphics in two texture formats. The game's `.astc`
textures render **black** inside WSA, and the fix is to install the PNG-textured
split: `split_config.etc1_rgb8_group_hd.apk`.

That file is mandatory. The launcher checks for it after installing (and re-installs
if it is missing), so a run either has it or tells you why it could not.

---

## Configuration

Everything has a sensible default, so most people never touch this. Settings are
read in this order, each overriding the last:

```
defaults  <  launcher.config  <  environment  <  command line
```

Create `launcher.config` beside the exe with one `key=value` per line (a `#` starts
a comment):

| Key | Default | Meaning |
|-----|---------|---------|
| `WsaRoot` | `C:\WSA` | Where the subsystem is installed |
| `WsaUrl` | *(empty)* | Direct URL for the WSA archive, skipping release lookup |
| `Repo` | `MustardChef/WSABuilds` | Where to find the WSA archive |
| `Sha256` | *(empty)* | Expected hash when using `WsaUrl` |
| `PayloadRepo` | `yassine808/parchisi` | Where to find the game files |
| `PayloadUrl` | *(empty)* | Direct URL for the game files, skipping release lookup |
| `BootTimeoutMin` | `10` | Minutes to wait for the subsystem to boot |
| `InstallTimeoutMin` | `30` | Minutes to wait for the install |
| `GameWindowTitle` | `Parchisi` | Substring of the game window title, used to confirm launch |

Environment variables use the `PP_` prefix — `PP_WSA_ROOT`, `PP_WSA_URL`,
`PP_WSA_REPO`, `PP_WSA_SHA256`, `PP_PAYLOAD_URL` — and command-line flags use
`--Key=value`, for example `--yes` to pre-approve the download questions or
`--WsaUrl=...` to point at a local copy.

Setting `GITHUB_TOKEN` in the environment raises the rate limit when the launcher
asks GitHub which release to pull from.

---

## Building from source

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

This produces `dist\PlayParchisi.exe`. There are no dependencies to restore and no
.NET SDK to install: it compiles with the C# compiler that ships inside the
Windows .NET Framework, and the resulting exe runs on a stock Windows 11 machine.

The build embeds the game's own icon and a manifest that makes the exe self-elevate
and declare per-monitor DPI awareness.

---

## Checks

The same checks CI runs are in one script, so a local run and a pull request
exercise the same bytes:

```powershell
powershell -ExecutionPolicy Bypass -File tools\ci\check.ps1 -Check source
```

| Check | Guards against |
|-------|----------------|
| `hygiene` | Personal paths, session temp dirs and unfinished-work markers in the files that ship |
| `manifest` | Losing `requireAdministrator` (registration fails 0x80073D28) or PerMonitorV2 (prompts draw wrong at non-100% scaling) |
| `readme` | A relative link or screenshot in this file pointing at something that no longer exists |
| `dialogs` | A `MessageBox.Show` outside the single-instance notice — an elevated `MessageBox` opens behind your other windows and reads as "the exe did nothing" |
| `tray` | A tray status string too long for the 63-character budget, which gets cut off silently |
| `icon` | A missing or malformed `tools\parchisi.ico`, which the build needs |
| `source` | The exe name, game package or texture split changing, or the Play Store / Google sign-in flow creeping back in |

---

## Troubleshooting

**The failure window says virtualization is off.** Enable virtualization
(VT-x / AMD-V / SVM) in your BIOS, turn on *Virtual Machine Platform* in Windows,
reboot, and run it again.

**It says Windows 11 is required.** WSA genuinely does not exist on Windows 10 —
there is nothing the launcher can do about that.

**A check keeps failing with the same message.** Delete `C:\WSA` and re-run; the
launcher will download a fresh copy. Read `launcher.log` for the detail behind any
failure window.

**The game launches but is black.** That is the missing PNG texture split. Delete
`C:\WSA` and the cache folder (`%LOCALAPPDATA%\Parchisi\cache`) and re-run, so the
game is installed again with the split present.

**GitHub rate-limits the launcher.** Set `GITHUB_TOKEN` in your environment.

---

## Licence

[MIT](LICENSE).
