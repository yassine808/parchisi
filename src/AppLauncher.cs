// Parchisi STAR - launcher (v2)
//
// Check-first provisioning. Every dependency is verified before anything is
// installed, every action is written to launcher.log with a timestamp, and the
// failure window only appears if a check fails - naming the check, the cause
// and a suggested fix. The only other UI is (a) the Yes/No questions, asked up
// front before anything is downloaded - Developer Mode, WSA, game files - and
// (b) a small non-blocking notice when the USER must act (consent dialog).
//
// Hard constraints this file encodes (do not break):
//   1. Boot goes through protocol activation (wsa://). WsaClient.exe is never
//      launched directly: without package identity its DLL graph fails.
//   2. There is NO Play Store flow and NO Google sign-in. The game, Firefox
//      and adb ship in one zip on this repo's own releases and are installed
//      with adb install-multiple. Firefox is required for Facebook login
//      (WSA's WebView UA is rejected by Facebook), texture split is MANDATORY
//      (the .astc variant renders black).
//   3. PlayParchisi.exe keeps its name on purpose: AV heuristics key on the
//      filename for the WsaClient spawn.
//
// WSA acquisition: if C:\WSA is missing we DOWNLOAD a community WSA build (Microsoft
// discontinued WSA, so there is no official source), but only after asking the user.
// Source is configurable: launcher.config, env vars, or CLI (see Cfg.Load).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;
// System.Drawing is spelled out in full elsewhere in this file; one alias keeps
// the download dialog's drawing code readable without touching any of that.
using SD = System.Drawing;

namespace Parchisi
{
    // ------------------------------------------------------------- config
    // Precedence (later wins): defaults < launcher.config < environment < CLI.
    // launcher.config is "key=value" lines beside the exe; '#' starts a comment.
    // Keys: WsaRoot, WsaUrl, Repo, AssetRequire, AssetExclude, Sha256,
    //       PayloadRepo, PayloadUrl, PayloadSha256,
    //       BootTimeoutMin, InstallTimeoutMin, Yes, GameWindowTitle
    // Env : PP_WSA_ROOT, PP_WSA_URL, PP_WSA_REPO, PP_WSA_SHA256, GITHUB_TOKEN (API rate limit)
    // CLI : --WsaUrl=... --Repo=owner/name --yes (skip the download question)
    internal static class Cfg
    {
        public static string WsaRoot = @"C:\WSA";
        public static string WsaUrl = "";                       // direct URL; skips release discovery
        public static string Repo = "MustardChef/WSABuilds";    // UNVERIFIED default source - see report
        public static string AssetRequire = "x64";              // regex the asset name must match
        public static string AssetExclude = "arm64|Windows_10|NoGApps|WithoutGApps|Without-GApps";
        public static string Sha256 = "";                       // expected hash for WsaUrl downloads
        // Game payload (APKs + adb) - same discovery/download machinery, own repo.
        public static string PayloadRepo = "yassine808/parchisi";
        public static string PayloadUrl = "";                   // direct URL; skips release discovery
        public static string PayloadSha256 = "";
        public static int BootTimeoutMin = 10;
        public static int InstallTimeoutMin = 30;
        public static bool Yes = false;                         // assume "yes" to the download question
        public static string GameWindowTitle = "Parchisi";

        public static string AppDir;
        public static string CacheDir;

        public static void Load(string appDir, string[] args)
        {
            AppDir = appDir;
            CacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Parchisi", "cache");

            try
            {
                var f = Path.Combine(appDir, "launcher.config");
                if (File.Exists(f))
                    foreach (var raw in File.ReadAllLines(f))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        var i = line.IndexOf('=');
                        if (i > 0) Set(line.Substring(0, i).Trim(), line.Substring(i + 1).Trim());
                    }
            }
            catch { /* the log is not open yet; defaults are fine */ }

            Env("PP_WSA_ROOT", "WsaRoot"); Env("PP_WSA_URL", "WsaUrl");
            Env("PP_WSA_REPO", "Repo"); Env("PP_WSA_SHA256", "Sha256");
            Env("PP_PAYLOAD_URL", "PayloadUrl");

            foreach (var a in args)
            {
                if (!a.StartsWith("--")) continue;
                var s = a.Substring(2); var i = s.IndexOf('=');
                if (i < 0) Set(s, "true"); else Set(s.Substring(0, i), s.Substring(i + 1));
            }
        }

        static void Env(string env, string key)
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(v)) Set(key, v);
        }

        static void Set(string k, string v)
        {
            int n;
            switch (k.ToLowerInvariant())
            {
                case "wsaroot": WsaRoot = v.TrimEnd('\\'); break;
                case "wsaurl": WsaUrl = v; break;
                case "repo": Repo = v; break;
                case "assetrequire": AssetRequire = v; break;
                case "assetexclude": AssetExclude = v; break;
                case "sha256": Sha256 = v; break;
                case "payloadrepo": PayloadRepo = v; break;
                case "payloadurl": PayloadUrl = v; break;
                case "payloadsha256": PayloadSha256 = v; break;
                case "boottimeoutmin": if (int.TryParse(v, out n) && n > 0) BootTimeoutMin = n; break;
                case "installtimeoutmin": if (int.TryParse(v, out n) && n > 0) InstallTimeoutMin = n; break;
                case "yes": Yes = !v.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                case "gamewindowtitle": GameWindowTitle = v; break;
            }
        }
    }

    // ------------------------------------------------------------- logger
    internal static class Log
    {
        static readonly object Sync = new object();
        static readonly List<string> Lines = new List<string>();
        static StreamWriter _w;
        static string _path;

        public static void Init(string preferredDir)
        {
            // The exe folder may be read-only (Program Files), so fall back to LocalAppData.
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parchisi");
            foreach (var dir in new[] { preferredDir, fallback })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, "launcher.log");
                    if (File.Exists(p) && new FileInfo(p).Length > 5 * 1048576)
                    {
                        File.Delete(p + ".old");
                        File.Move(p, p + ".old");           // simple one-generation rotation
                    }
                    // FileShare.ReadWrite so the user can open the log while we run.
                    var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    _path = p;
                    break;
                }
                catch { /* try the next location */ }
            }
            if (_w == null) Warn("file logging unavailable; log kept in memory only");

            Info("======================================================");
            Info("Parchisi STAR launcher");
            Info("exe      : " + Assembly.GetExecutingAssembly().Location);
            Info("version  : " + Assembly.GetExecutingAssembly().GetName().Version);
            Info("started  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Info("os       : " + Environment.OSVersion.VersionString);
            Info("64bit    : os=" + Environment.Is64BitOperatingSystem + " process=" + Environment.Is64BitProcess);
            Info("user     : " + Environment.UserDomainName + "\\" + Environment.UserName);
            Info("elevated : " + Program.IsElevated());
            Info("WsaRoot  : " + Cfg.WsaRoot);
            Info("log file : " + (_path ?? "(none)"));
            Info("------------------------------------------------------");
        }

        public static void Close()
        {
            lock (Sync)
            {
                if (_w != null) { try { _w.Flush(); _w.Dispose(); } catch { } _w = null; }
            }
        }

        public static void Step(string m) { Write("STEP", m); }
        public static void Info(string m) { Write("INFO", m); }
        public static void Ok(string m) { Write("OK  ", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Err(string m) { Write("ERR ", m); }

        static void Write(string lvl, string msg)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + lvl + "  " + msg;
            lock (Sync)
            {
                Lines.Add(line);
                if (_w != null) { try { _w.WriteLine(line); } catch { } }
            }
        }

        public static string[] Snapshot() { lock (Sync) { return Lines.ToArray(); } }
        public static string FilePath { get { return _path; } }
    }

    // ------------------------------------------------ non-blocking user notice
    // The ONLY UI besides the download questions and the failure window. Shown on
    // its own STA thread, never takes focus (it must not steal it from the Android
    // windows it is pointing at - the WSA first-run consent dialog, for instance).
    internal static class Notice
    {
        static readonly object L = new object();
        static Form _form;
        static bool _closing;

        sealed class NoticeForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        public static void Show(string title, string text)
        {
            Close();
            lock (L) { _closing = false; }
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var f = new NoticeForm
                    {
                        Text = title,
                        FormBorderStyle = FormBorderStyle.FixedToolWindow,
                        TopMost = true,
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Size = new System.Drawing.Size(420, 140)
                    };
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    f.Location = new System.Drawing.Point(wa.Right - f.Width - 12, wa.Bottom - f.Height - 12);
                    f.Controls.Add(new Label
                    {
                        Dock = DockStyle.Fill,
                        Text = text,
                        Padding = new Padding(10),
                        Font = new System.Drawing.Font("Segoe UI", 10)
                    });
                    f.FormClosed += delegate(object s, FormClosedEventArgs e)
                    {
                        lock (L) { if (ReferenceEquals(_form, f)) _form = null; }
                    };
                    f.Shown += (s, e) =>
                    {
                        // Close() can land before the handle exists - its BeginInvoke
                        // then fails silently and the reference is already gone, which
                        // used to leave an uncloseable TopMost window on screen.
                        bool c; lock (L) { c = _closing; }
                        if (c) { try { f.Close(); } catch { } }
                        ready.Set();
                    };
                    lock (L)
                    {
                        if (_closing) { ready.Set(); return; }   // closed while starting
                        _form = f;
                    }
                    Application.Run(f);
                }
                catch (Exception ex) { Log.Warn("notice window: " + ex.Message); }
                finally { ready.Set(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            ready.WaitOne(3000);
        }

        public static void Close()
        {
            Form f;
            lock (L) { _closing = true; f = _form; _form = null; }
            if (f == null) return;
            try { f.BeginInvoke(new Action(() => f.Close())); } catch { }
        }
    }

    // ------------------------------------------------- transient status bubble
    // Between "we activated the app" and "its window is on screen" there is no UI
    // at all - on a virgin first boot that stretch measured 16 minutes, so the
    // launcher looked idle while it was actually waiting. This bubble says which
    // app is being launched and closes itself the moment that app shows up
    // (or the step ends), so the user always knows what is happening.
    // Like Notice it never takes focus: it must not steal it from the app itself.
    internal static class Status
    {
        static readonly object L = new object();
        static Form _form;
        static string _text;
        static bool _closing;

        sealed class BubbleForm : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        public static void Show(string text)
        {
            lock (L) { if (_form != null && _text == text) return; }   // already up: no flicker
            Close();
            lock (L) { _closing = false; }
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var f = new BubbleForm
                    {
                        Text = "Launching",
                        FormBorderStyle = FormBorderStyle.FixedToolWindow,
                        TopMost = true,
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Size = new System.Drawing.Size(420, 90)
                    };
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    f.Location = new System.Drawing.Point(wa.Right - f.Width - 12, wa.Top + 12);
                    f.Controls.Add(new Label
                    {
                        Dock = DockStyle.Fill,
                        Text = text,
                        Padding = new Padding(10),
                        Font = new System.Drawing.Font("Segoe UI", 10)
                    });
                    // The user may close the bubble with its own X: forget it, or the
                    // dedupe guard above would suppress every later Show of that text
                    // and the launcher would look idle again for the rest of the run.
                    f.FormClosed += delegate(object s, FormClosedEventArgs e)
                    {
                        lock (L)
                        {
                            if (ReferenceEquals(_form, f)) { _form = null; _text = null; }
                        }
                    };
                    f.Shown += (s, e) =>
                    {
                        bool c; lock (L) { c = _closing; }
                        if (c) { try { f.Close(); } catch { } }
                        ready.Set();
                    };
                    lock (L)
                    {
                        if (_closing) { ready.Set(); return; }   // closed while starting
                        _form = f; _text = text;
                    }
                    Application.Run(f);
                }
                catch (Exception ex) { Log.Warn("status bubble: " + ex.Message); }
                finally { ready.Set(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            ready.WaitOne(3000);
        }

        public static void Close()
        {
            Form f;
            lock (L) { _closing = true; f = _form; _form = null; _text = null; }
            if (f == null) return;
            try { f.BeginInvoke(new Action(() => f.Close())); } catch { }
        }
    }

    // ------------------------------------------------------------ tray icon
    // This exe opens no window, so during the long stretches where it is
    // legitimately doing nothing the user can see - a 700 MB WSA download,
    // a 560 MB game-file download, the first-run consent click - it would
    // otherwise look like nothing is running at all. The tray icon makes
    // the wait visible: the tooltip tracks the current step, and the menu
    // gives the user the two things they might want while waiting - read
    // the log, or cancel.
    // It runs on its own STA thread with its own message loop, because a
    // NotifyIcon needs one and the main thread is busy running checks.
    internal static class Tray
    {
        static readonly object L = new object();
        static volatile string _status = "starting";
        static NotifyIcon _icon;
        static ContextMenuStrip _menu;
        static ToolStripMenuItem _line;
        static string _icoPath;

        // Program installs this so the menu's "Exit launcher" can cancel the run
        // the same way Ctrl+C does.
        public static Action OnExit;

        public static void Start()
        {
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    _menu = new ContextMenuStrip();
                    _line = new ToolStripMenuItem("starting") { Enabled = false };
                    _menu.Items.Add(_line);
                    _menu.Items.Add(new ToolStripSeparator());

                    var open = new ToolStripMenuItem("Open log");
                    open.Click += (s, e) =>
                    {
                        try { if (Log.FilePath != null) Process.Start(Log.FilePath); }
                        catch (Exception ex) { Log.Warn("open log: " + ex.Message); }
                    };
                    _menu.Items.Add(open);

                    var exit = new ToolStripMenuItem("Exit launcher");
                    exit.Click += (s, e) =>
                    {
                        Log.Warn("Exit chosen in the tray");
                        var h = OnExit; if (h != null) h();     // Program cancels the run
                        Notice.Close();
                        Status.Close();
                        try { Application.Exit(); } catch { }
                    };
                    _menu.Items.Add(exit);

                    _menu.Opening += (s, e) => { _line.Text = _status; };

                    System.Drawing.Icon ico = null;
                    try { ico = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch { }
                    _icon = new NotifyIcon { Icon = ico, ContextMenuStrip = _menu, Visible = true };
                    _icoPath = Path.Combine(Cfg.AppDir ?? "", "parchisi.ico");
                    SetText("Parchisi STAR");

                    // The tooltip follows the current step without every log line
                    // having to marshal onto this thread.
                    var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                    timer.Tick += (s, e) =>
                    {
                        try
                        {
                            SetText(_status);
                            // the .ico can be dropped in beside the exe to re-skin
                            // the icon live; if it vanishes, fall back to the exe's
                            if (_icoPath != null && File.Exists(_icoPath))
                            {
                                var mt = File.GetLastWriteTimeUtc(_icoPath);
                                if (mt != _lastIcoUtc)
                                {
                                    _lastIcoUtc = mt;
                                    _icon.Icon = new System.Drawing.Icon(_icoPath);   // replaces; Dispose is by NotifyIcon
                                }
                            }
                        }
                        catch { }
                    };
                    timer.Start();

                    ready.Set();
                    Application.Run();
                }
                catch (Exception ex) { Log.Warn("tray icon: " + ex.Message); }
                finally { ready.Set(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;          // never keeps the process alive on its own
            t.Start();
            ready.WaitOne(3000);
        }

        static DateTime _lastIcoUtc;

        // NotifyIcon.Text throws past 63 characters on the .NET Framework, so
        // the status is truncated here rather than taking the tray down.
        static void SetText(string s)
        {
            var full = "Parchisi STAR - " + (s ?? "");
            _icon.Text = full.Length <= 63 ? full : full.Substring(0, 60) + "...";
        }

        public static void SetStatus(string s) { _status = s; }

        public static void Stop()
        {
            ContextMenuStrip m;
            NotifyIcon ic;
            lock (L) { ic = _icon; m = _menu; _icon = null; _menu = null; }
            if (ic == null) return;
            // Application.ExitThread() must run on the tray's own thread - calling
            // it here would target the main thread's (nonexistent) message loop.
            try
            {
                if (m != null && m.IsHandleCreated)
                    m.BeginInvoke(new Action(() => { ic.Visible = false; ic.Dispose(); Application.ExitThread(); }));
                else { ic.Visible = false; ic.Dispose(); }
            }
            catch { try { ic.Visible = false; ic.Dispose(); } catch { } }
        }
    }

    // ------------------------------------------------------ progress window
    // A ~700 MB download must not be buried in a log file nobody opens. This is
    // a small always-on-top window with a bar, the exact MB counts and the
    // measured transfer rate, updated from the download thread. It is only used
    // for the WSA archive: a 600 KB tool download does not deserve a window.
    internal static class ProgressWin
    {
        static readonly object L = new object();
        static Form _form;
        static Label _detail, _rate;
        static ProgressBar _bar;
        static bool _closing;

        public static void Show(string title)
        {
            lock (L) { _closing = false; }
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var f = new Form
                    {
                        Text = title,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        MaximizeBox = false,
                        MinimizeBox = false,
                        ShowInTaskbar = false,
                        TopMost = true,
                        StartPosition = FormStartPosition.CenterScreen,
                        Size = new System.Drawing.Size(430, 128)
                    };
                    _detail = new Label { Left = 14, Top = 12, Width = 400, Height = 20, Text = "starting..." };
                    _bar = new ProgressBar
                    {
                        Left = 14, Top = 36, Width = 400, Height = 24,
                        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30
                    };
                    _rate = new Label { Left = 14, Top = 68, Width = 400, Height = 20, Text = "" };
                    f.Controls.Add(_detail); f.Controls.Add(_bar); f.Controls.Add(_rate);
                    f.FormClosed += delegate(object s, FormClosedEventArgs e)
                    {
                        lock (L) { if (ReferenceEquals(_form, f)) _form = null; }
                    };
                    f.Shown += (s, e) =>
                    {
                        bool c; lock (L) { c = _closing; }
                        if (c) { try { f.Close(); } catch { } }
                        ready.Set();
                    };
                    lock (L)
                    {
                        if (_closing) { ready.Set(); return; }   // finished while starting
                        _form = f;
                    }
                    Application.Run(f);
                }
                catch (Exception ex) { Log.Warn("progress window: " + ex.Message); }
                finally { ready.Set(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            ready.WaitOne(3000);
        }

        // called from the download thread at the throttled progress ticks
        public static void Set(long done, long total, double mbPerSec)
        {
            Form f; lock (L) { f = _form; }
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                f.BeginInvoke(new Action(() =>
                {
                    if (_bar.Style != ProgressBarStyle.Blocks)
                    {
                        _bar.Style = ProgressBarStyle.Blocks;
                        _bar.Maximum = 100;
                    }
                    var pct = total > 0 ? (int)Math.Min(100L, done * 100L / total) : 0;
                    _bar.Value = Math.Max(0, Math.Min(100, pct));
                    _detail.Text = total > 0
                        ? (done / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " of " +
                          (total / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB   (" + pct + "%)"
                        : (done / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB downloaded";

                    var left = -1.0;
                    if (mbPerSec > 0.05 && total > done) left = (total - done) / (mbPerSec * 1048576.0);
                    _rate.Text = mbPerSec.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s" +
                        (left > 0
                            // "about 87000 s left" was unreadable on a slow link
                            ? "   -   about " + TimeSpan.FromSeconds(left).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + " left"
                            : "");
                }));
            }
            catch { }
        }

        // called while the file is hashing / installing: same window, new text
        public static void Status(string s)
        {
            Form f; lock (L) { f = _form; }
            if (f == null || !f.IsHandleCreated) return;
            try { f.BeginInvoke(new Action(() => { _detail.Text = s; _rate.Text = ""; })); }
            catch { }
        }

        public static void Finish()
        {
            Form f; lock (L) { _closing = true; f = _form; _form = null; }
            if (f == null) return;
            try { f.BeginInvoke(new Action(() => { try { f.Close(); } catch { } })); }
            catch { }
        }
    }

    // ------------------------------------------------------- logcat watcher
    // Reads WSA's own logcat straight off LocalState - no adb, no developer mode.
    // Incremental and line-based: it remembers a byte offset per file, so a 14 MB
    // boot log is read once, "Boot is finished" can never scroll out of a tail window,
    // and every regex runs on ONE line (a negated class in .NET matches newlines, which
    // once produced false positives across 517 unrelated lines).
    // Only files written since `floorUtc` count, so a previous session's log cannot
    // produce a false "boot finished".
    internal sealed class LogWatcher
    {
        readonly DateTime _floorUtc;
        readonly Dictionary<string, long> _offsets = new Dictionary<string, long>();
        readonly Dictionary<string, string> _carry = new Dictionary<string, string>();

        public bool BootFinished;

        static readonly TimeSpan Rx = TimeSpan.FromSeconds(2);

        public LogWatcher(DateTime floorUtc) { _floorUtc = floorUtc; }

        public static string LogcatDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", Program.PackageName, "LocalState", "diagnostics", "logcat");
            }
        }

        public void Poll()
        {
            try
            {
                if (!Directory.Exists(LogcatDir)) return;
                var files = new DirectoryInfo(LogcatDir).GetFiles("*.log")
                    .Where(f => f.LastWriteTimeUtc >= _floorUtc)
                    .OrderBy(f => f.LastWriteTimeUtc).ToArray();
                foreach (var f in files)
                {
                    // Per file, not per poll: one log WSA still holds open used to abort
                    // the whole scan, so newer files' "Boot is finished" and install
                    // evidence were never read at all.
                    try { ReadNew(f); }
                    catch (Exception ex) { Log.Warn("  logcat watcher: " + f.Name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Warn("  logcat watcher: " + ex.Message); }
        }

        void ReadNew(FileInfo f)
        {
            long off; _offsets.TryGetValue(f.FullName, out off);
            string carry; _carry.TryGetValue(f.FullName, out carry);
            if (f.Length < off) { off = 0; carry = null; }      // rotated/truncated
            if (f.Length == off) return;

            using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                                           FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Position = off;
                var buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    off += n;
                    var text = (carry ?? "") + Encoding.UTF8.GetString(buf, 0, n);
                    var cut = text.LastIndexOf('\n');
                    if (cut < 0) { carry = text.Length > 65536 ? "" : text; continue; }
                    carry = text.Substring(cut + 1);
                    foreach (var line in text.Substring(0, cut).Split('\n')) Process(line);
                }
            }
            _offsets[f.FullName] = off;
            _carry[f.FullName] = carry ?? "";
        }

        void Process(string line)
        {
            if (!BootFinished && line.IndexOf("Boot is finished", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                BootFinished = true;
                Log.Info("  logcat: \"Boot is finished\"");
            }
        }
    }

    // -------------------------------------------------------------- program
    internal static class Program
    {
        // Tunables ------------------------------------------------------
        const int AdbPort = 58526;
        const int PollMs = 2000;
        const int LaunchConfirmMs = 90 * 1000;

        internal const string PackageName = "MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe";
        internal const string PackageFamily = "MicrosoftCorporationII.WindowsSubsystemForAndroid";
        internal const string GamePackage = "com.superking.parchisi.star";
        internal const string FirefoxPackage = "org.mozilla.firefox";
        // WSA boot goes through protocol activation of a package that is always
        // present in the GApps build; the store window is closed again after boot.
        internal const string BootPackage = "com.android.vending";
        const string UserAgent = "ParchisiLauncher/2.0 (+WSA provisioning)";

        static volatile bool Cancelled;
        static string _cause;               // human-readable reason for the last failure
        static string _tmpDir;              // extraction temp dir, cleaned on exit if unfinished
        static LogWatcher _watcher;
        static DateTime _bootStartUtc = DateTime.MinValue;
        static bool _adbConnected;

        internal static string MarkerPath { get { return Path.Combine(Cfg.WsaRoot, ".pp-complete"); } }

        sealed class StepDef
        {
            public string Name; public Func<bool> Body; public string Hint;
            public StepDef(string n, Func<bool> b, string h) { Name = n; Body = b; Hint = h; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }  // TLS 1.3 if supported

            var dir = AppDomain.CurrentDomain.BaseDirectory;
            Cfg.Load(dir, args);
            Log.Init(dir);

            bool created;
            Mutex mtx = null;
            // NB: "Global\\Name" - exactly two backslashes in the C# source, one in the
            // string. Four ("Global\\\\Name") builds the name "Global\\Name", which is
            // not a legal kernel-object name: the constructor throws, every launch dies
            // on exit code 2, and the catch below logs it and returns with no window at
            // all - the exe appears to do nothing. Caught on the first clean-machine run.
            try { mtx = new Mutex(true, "Global\\PlayParchisiLauncher", out created); }
            catch (Exception ex)
            {
                // Assuming exclusivity after a FAILURE was backwards: if the mutex cannot
                // be created a second instance may well own it, and two writers on one
                // .part file corrupt the download.
                Log.Err("single-instance mutex: " + ex.Message);
                Log.Close();
                return 2;
            }
            if (!created)
            {
                Log.Warn("another launcher instance is already running; exiting");
                MessageBox.Show("The launcher is already running.", "Parchisi STAR",                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Log.Close();
                return 2;
            }

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Cancelled = true; };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { Cancelled = true; Notice.Close(); Status.Close(); Tray.Stop(); CleanupTemp(); Log.Close(); };

            Tray.OnExit = () => { Cancelled = true; };   // menu "Exit launcher" == Ctrl+C
            Tray.Start();

            int exit;
            try { exit = Run(); }
            catch (OperationCanceledException) { Log.Warn("cancelled"); exit = 130; }
            catch (Exception ex) { Log.Err("unhandled exception: " + ex); exit = 1; ShowFailureWindow("unexpected error", ex.Message, ""); }

            Tray.Stop();
            try { mtx.ReleaseMutex(); } catch { }
            Log.Close();
            return exit;
        }

        static int Run()
        {
            var steps = new[]
            {
                new StepDef("Administrator rights", CheckAdmin, "Right-click the launcher and choose 'Run as administrator'."),
                new StepDef("Windows 11", CheckWindows11, "WSA needs Windows 11 (build 22000 or newer). Update Windows."),
                new StepDef("virtualization", CheckVirtualization, "Enable virtualization (VT-x/AMD-V/SVM) in BIOS, enable 'Virtual Machine Platform', then reboot."),
                new StepDef("Developer Mode", EnsureDeveloperMode, "Turn on Settings > System > For developers > Developer Mode, then re-run."),
                new StepDef("WSA payload", EnsurePayload, "Check your internet connection and free disk space, or set WsaUrl in launcher.config."),
                new StepDef("WSA dependencies", EnsureDependencies, "Re-run; if it persists, delete C:\\WSA so it is downloaded again."),
                new StepDef("WSA registration", EnsureRegistered, "Make sure no other WSA copy is registered; run 'Get-AppxPackage *WindowsSubsystemForAndroid* | Remove-AppxPackage' and retry."),
                new StepDef("userdata template", EnsureUserdataTemplate, "Enable the Hyper-V PowerShell module, or ensure diskpart works and C: has 1 GB free."),
                new StepDef("WsaService", EnsureService, "Reboot, then re-run. WSA may not be registered correctly."),
                new StepDef("WSA boot", BootWsa, "Check virtualization, free RAM (4 GB+), and the log for a WsaClient crash."),
                new StepDef("game files + install", EnsureGameInstalled, "Re-run; the game files download automatically. Check your internet connection and free disk space."),
                new StepDef("launch", LaunchGame, "Start the game manually from the Start menu; see the log for details.")
            };

            for (int i = 0; i < steps.Length; i++)
            {
                var s = steps[i];
                var label = "check " + (i + 1) + "/" + steps.Length + " - " + s.Name;
                _cause = null;
                Log.Step(label + " ...");
                Tray.SetStatus(s.Name + "  (" + (i + 1) + "/" + steps.Length + ")");
                var sw = Stopwatch.StartNew();
                bool ok;
                try { ok = s.Body(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ok = Fail(Describe(ex)); }
                sw.Stop();
                var secs = N(sw.Elapsed.TotalSeconds);
                if (!ok)
                {
                    Log.Err(label + " FAILED (" + secs + "s): " + (_cause ?? "see log"));
                    Log.Err("full log: " + Log.FilePath);
                    ShowFailureWindow(label, _cause ?? "(no detail)", s.Hint);
                    return 10 + i;      // distinct exit code per failed step
                }
                Log.Ok(label + " passed (" + secs + "s)");
            }
            // LaunchGame only returns once the game window is gone, so by here
            // the run is genuinely over rather than merely submitted.
            Log.Ok("all checks passed; game closed - exiting");
            Tray.SetStatus("done");
            return 0;
        }

        // -------------------------------------------------------- helpers
        static string N(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string MB(long bytes) { return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture); }
        static string Size(long b)
        {
            return b >= 1073741824L ? (b / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                                    : MB(b) + " MB";
        }

        // The download question is phrased in GB, always. Size() would say
        // "713 MB", which reads as a small number right up until it is 0.7 GB
        // of someone's bandwidth; the ask needs the bigger unit to be honest.
        static string Gb(long b)
        {
            return (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        static bool Fail(string cause) { _cause = cause; Log.Err(cause); return false; }

        static void Wait(int ms)
        {
            var sw = Stopwatch.StartNew();
            do
            {
                if (Cancelled) throw new OperationCanceledException();
                if (ms <= 0) return;
                Thread.Sleep((int)Math.Min(200, Math.Max(1, ms - sw.ElapsedMilliseconds)));
            } while (sw.ElapsedMilliseconds < ms);
        }

        internal static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static string Describe(Exception ex)
        {
            var wex = ex as WebException;
            if (wex != null)
            {
                var hr = wex.Response as HttpWebResponse;
                if (hr != null) return "HTTP " + (int)hr.StatusCode + " " + hr.StatusDescription;
                switch (wex.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure: return "DNS lookup failed (offline?)";
                    case WebExceptionStatus.ConnectFailure: return "could not connect (offline or blocked)";
                    case WebExceptionStatus.Timeout: return "request timed out";
                    default: return "network error: " + wex.Status + " - " + wex.Message;
                }
            }
            return ex.Message;
        }

        static bool IsDiskFull(Exception ex)
        {
            var io = ex as IOException;
            if (io == null) return false;
            var code = io.HResult & 0xFFFF;
            return code == 112 || code == 39;       // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL
        }

        static string PsQuote(string s) { return "'" + s.Replace("'", "''") + "'"; }

        static int CountProcs(string name)
        {
            var p = Process.GetProcessesByName(name);
            try { return p.Length; } finally { foreach (var x in p) x.Dispose(); }
        }

        static bool Open(string uri)
        {
            // Shell activation is unbounded by nature: a stalled Explorer or hung
            // protocol handler used to block the main flow here indefinitely.
            bool ok = false;
            string err = null;
            if (!_open.Run(delegate
            {
                try { using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { } ok = true; }
                catch (Exception ex) { err = ex.Message; }
            }, 5000, "shell open"))
            {
                Log.Warn("  could not open " + uri + ": the shell did not answer within 5 s");
                return false;
            }
            if (!ok) Log.Warn("  could not open " + uri + ": " + err);
            return ok;
        }

        static readonly Bounded _open = new Bounded();

        // PowerShell must be 64-bit: under WOW64 the Hyper-V module, optional-feature and
        // package cmdlets misbehave. Sysnative reaches the 64-bit one from a 32-bit exe.
        static string PsExe()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var sys = (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem) ? "Sysnative" : "System32";
            return Path.Combine(win, sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        // -EncodedCommand removes every quoting problem (apostrophes in paths, etc.), and the
        // try/catch + 'Stop' turns cmdlet failures into a non-zero exit code we can trust.
        static bool RunPs(string script, string tag, int timeoutMs, out string output)
        {
            var wrapped = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                          "try{" + script + "}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}";
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            return Run(PsExe(), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc,
                       tag, timeoutMs, out output);
        }

        static bool Run(string exe, string args, string tag, int timeoutMs, out string output)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var sb = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        if (p.WaitForExit(200)) break;
                        // Cancel must be observed HERE: tray "Exit" during a long 7-Zip
                        // extraction or package registration used to set Cancelled while
                        // nothing in this wait ever read it - the launcher kept running.
                        if (Cancelled)
                        {
                            try { p.Kill(); } catch { }
                            Log.Warn("cancelled while running [" + tag + "]");
                            throw new OperationCanceledException();
                        }
                        if (sw.ElapsedMilliseconds > timeoutMs)
                        {
                            try { p.Kill(); } catch { }
                            Log.Warn("timeout on [" + tag + "] after " + timeoutMs + " ms");
                            lock (sb) output = sb.ToString();
                            LogOutputTail(tag, output);
                            return false;
                        }
                    }
                    // Drain the async readers, bounded. The parameterless WaitForExit
                    // waits for the pipes to close, and a grandchild that inherited the
                    // handles keeps them open forever - no failure window, no tray stop,
                    // only Task Manager. Quiet for 300 ms (2 s cap) is close enough.
                    var drainUntil = Environment.TickCount + 2000;
                    int lastLen = -1, quiet = 0;
                    while (Environment.TickCount < drainUntil)
                    {
                        int len; lock (sb) len = sb.Length;
                        if (len == lastLen) { if (++quiet >= 6) break; }
                        else { quiet = 0; lastLen = len; }
                        Thread.Sleep(50);
                    }
                    lock (sb) output = sb.ToString();
                    if (p.ExitCode != 0)
                    {
                        Log.Info("[" + tag + "] exit=" + p.ExitCode);
                        LogOutputTail(tag, output);
                    }
                    return p.ExitCode == 0;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("[" + tag + "] could not run " + exe + ": " + ex.Message);
                output = ex.Message;
                return false;
            }
        }

        // Child output used to reach the user ONLY through a failure cause string, so
        // shortening those strings would have cost diagnosis. Failures now log their
        // own tail: the failure window embeds the full log, the cause stays one plain
        // sentence.
        static void LogOutputTail(string tag, string output)
        {
            if (string.IsNullOrEmpty(output)) return;
            var lines = output.TrimEnd().Split('\n');
            var n = Math.Min(lines.Length, 15);
            Log.Warn("[" + tag + "] output (last " + n + " of " + lines.Length + " lines):");
            for (int i = lines.Length - n; i < lines.Length; i++) Log.Warn("  " + lines[i].TrimEnd('\r'));
        }

        static bool PortOpen(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var r = c.BeginConnect("127.0.0.1", port, null, null);
                    try
                    {
                        if (!r.AsyncWaitHandle.WaitOne(700, false)) return false;   // Dispose cancels the connect
                        try { c.EndConnect(r); return true; } catch { return false; }
                    }
                    finally { try { r.AsyncWaitHandle.Close(); } catch { } }   // polled in tight loops: no leak
                }
            }
            catch { return false; }
        }

        static void CleanupTemp()
        {
            try { if (_tmpDir != null && Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true); } catch { }
        }

        // ------------------------------------------------------ 1. admin
        static bool CheckAdmin()
        {
            Log.Info("process 64-bit: " + Environment.Is64BitProcess);
            if (!IsElevated()) return Fail("the launcher is not running as administrator");
            return true;
        }

        // --------------------------------------------------- 2. windows 11
        static bool CheckWindows11()
        {
            Log.Info("reported version: " + Environment.OSVersion.Version);
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key != null)
                {
                    var id = key.GetValue("CurrentBuildNumber") ?? key.GetValue("CurrentBuild");
                    Log.Info("CurrentBuild=" + id + "  DisplayVersion=" + key.GetValue("DisplayVersion"));
                    int build;
                    if (id != null && int.TryParse(id.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out build))
                    {
                        Log.Info("build " + build + "; WSA needs 22000+");
                        return build >= 22000 || Fail("Windows build " + build + " is older than 22000 (Windows 11)");
                    }
                }
            }
            Log.Warn("could not read build number; trusting OSVersion");
            var v = Environment.OSVersion.Version;
            return (v.Major >= 10 && v.Build >= 22000) || Fail("Windows build " + v.Build + " is older than 22000");
        }

        // ------------------------------------------------ 3. virtualization
        // HypervisorPresent=True is authoritative: it means the hypervisor is actually
        // running (feature enabled, rebooted, and enabled in firmware). Only when it is
        // False do we ask the slower feature query, purely to give an accurate message.
        static bool CheckVirtualization()
        {
            string o;
            bool hv = false;
            if (RunPs("(Get-CimInstance Win32_ComputerSystem).HypervisorPresent", "hypervisor", 30000, out o))
            {
                Log.Info("HypervisorPresent: " + o.Trim());
                hv = o.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            }
            if (hv) return true;

            var vmp = "unknown";
            if (RunPs("(Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State.ToString()",
                      "vmp", 120000, out o)) vmp = o.Trim();
            Log.Warn("VirtualMachinePlatform: " + vmp);

            if (vmp.IndexOf("Enabled", StringComparison.OrdinalIgnoreCase) >= 0)
                return Fail("Virtual Machine Platform is enabled but no hypervisor is running: reboot, or turn on virtualization (VT-x / AMD-V / SVM) in the BIOS");
            if (vmp.IndexOf("Pending", StringComparison.OrdinalIgnoreCase) >= 0)
                return Fail("Virtual Machine Platform was just enabled; reboot Windows and re-run");
            return Fail("Virtual Machine Platform is not enabled (state: " + vmp + "). Enable it in 'Windows Features' and reboot");
        }

        // -------------------------------------------------------- 4. payload
        static bool PayloadComplete()
        {
            var manifest = Path.Combine(Cfg.WsaRoot, "AppxManifest.xml");
            if (!File.Exists(manifest)) return false;
            if (File.Exists(MarkerPath)) return true;                 // our own completed install
            if (File.Exists(Path.Combine(Cfg.WsaRoot, "WsaClient.exe")))
            {
                Log.Warn("no completion marker, but manifest + WsaClient.exe exist; accepting a manual install");
                return true;
            }
            Log.Warn("AppxManifest.xml present but WsaClient.exe missing - install looks incomplete");
            return false;
        }

        sealed class Asset
        {
            public string Name, Url, Sha256, Source;
            // Where the release came from, so the prompt can offer it as a link: the
            // user is about to spend three quarters of a gigabyte on a third-party
            // build, and should be able to look at it first.
            public string ReleaseUrl, ReleaseTag;
            public long Size = -1;
        }

        static bool EnsurePayload()
        {
            if (PayloadComplete())
            {
                var di = new DirectoryInfo(Cfg.WsaRoot);
                var files = di.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
                Log.Info("WSA present: " + MB(files.Sum(f => f.Length)) + " MB, " + files.Length + " files");
                return true;
            }

            Log.Info("WSA is not installed at " + Cfg.WsaRoot + "; it must be downloaded");
            try { Directory.CreateDirectory(Cfg.CacheDir); }
            catch (Exception ex) { return Fail("cannot create cache dir " + Cfg.CacheDir + ": " + Describe(ex)); }

            string archive;
            if (!ObtainArchive(out archive)) return false;
            if (!ExtractAndInstall(archive)) return false;
            return true;
        }

        static bool ObtainArchive(out string archive)
        {
            archive = null;
            Asset a = null; string discoverErr = null;
            try { a = Cfg.WsaUrl.Length > 0 ? FromUrl(Cfg.WsaUrl) : Discover(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { discoverErr = Describe(ex); Log.Warn("download source lookup failed: " + discoverErr); }

            if (a == null)
            {
                // Offline? A previously downloaded archive can still be used.
                var cached = FindCachedArchive();
                if (cached != null && !SidecarOk(cached))
                {
                    Log.Warn("cached archive failed its recorded fingerprint; discarding " + cached);
                    try { File.Delete(cached); } catch { }
                    try { File.Delete(cached + ".sha256"); } catch { }
                    cached = null;
                }
                if (cached != null)
                {
                    Log.Warn("using cached archive without verification: " + cached);
                    if (!NeedSpace(new FileInfo(cached).Length)) return false;
                    archive = cached; return true;
                }
                return Fail("could not find a download (" + discoverErr + "). Check your internet connection");
            }

            var target = Path.Combine(Cfg.CacheDir, a.Name);
            bool okCached = false;
            // Hashing can throw if AV/indexer holds the file - that must degrade to
            // "re-download", not kill the run with "file is being used by another process".
            try { okCached = File.Exists(target) && CacheValid(target, a); }
            catch (Exception ex) { Log.Warn("cached archive unreadable: " + Describe(ex)); }
            if (okCached)
            {
                Log.Ok("verified cached archive: " + target);
                if (!NeedSpace(new FileInfo(target).Length)) return false;   // cached path: disk check used to be skipped
                archive = target; return true;
            }

            // Saying "No" is a decision, not a fault. It takes the same quiet
            // path as the tray's "Exit" during a download - exit 130, nothing
            // changed - rather than the "setup problem" window, which is for
            // things the user did not choose and cannot get past by re-running.
            if (!Confirm(a, "Windows Subsystem for Android"))
            {
                Log.Warn("WSA download declined by the user; nothing was changed");
                throw new OperationCanceledException();
            }
            if (!NeedSpace(a.Size > 0 ? a.Size : 3L * 1073741824)) return false;
            // The archive is by far the largest transfer in the whole run, so it
            // gets the progress window; the small tool downloads stay log-only.
            if (!DownloadFile(a.Url, target, a.Size, a.Sha256, a.Name, true)) return false;
            archive = target;
            return true;
        }

        static Asset FromUrl(string url)
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrEmpty(name)) name = "wsa-download.zip";
            return new Asset { Name = name, Url = url, Sha256 = Cfg.Sha256, Source = "configured URL" };
        }

        static string FindCachedArchive()
        {
            try
            {
                return new DirectoryInfo(Cfg.CacheDir).GetFiles()
                    .Where(f => (f.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                                 f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
                                // Not "any archive in the cache": the game payload zip lives
                                // here too and is normally the NEWEST file, so offline it used
                                // to be selected as the WSA build, extracted to a tree with no
                                // manifest, and never deleted.
                                f.Name != "ParchisiPayload.zip" &&
                                f.Length > 100L * 1048576)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
            }
            catch { return null; }
        }

        static bool CacheValid(string path, Asset a)
        {
            if (!string.IsNullOrEmpty(a.Sha256))
                return Sha256Of(path).Equals(a.Sha256, StringComparison.OrdinalIgnoreCase);
            if (a.Size > 0) return new FileInfo(path).Length == a.Size;
            // No published identity: fall back to the fingerprint WE recorded when this
            // file was downloaded, so a later run still notices a truncated or swapped
            // cache. Returning true here (the old behaviour) trusted whatever sat at
            // this path forever, and there is no size gate to catch it either.
            var side = path + ".sha256";
            if (File.Exists(side))
            {
                var rec = SafeReadAllText(side).Trim();
                if (rec.Length != 64) return false;
                return Sha256Of(path).Equals(rec, StringComparison.OrdinalIgnoreCase);
            }
            Log.Warn("cached archive has no published hash, no size, and no recorded fingerprint - not reusing it");
            return false;
        }

        // Offline companion to CacheValid: no Asset exists at all, so the recorded
        // fingerprint is the only identity available. No sidecar (a cache written by an
        // older version) is accepted - refusing it would make the offline path dead.
        static bool SidecarOk(string path)
        {
            try
            {
                var side = path + ".sha256";
                if (!File.Exists(side)) return true;
                var rec = SafeReadAllText(side).Trim();
                if (rec.Length != 64) return false;
                return Sha256Of(path).Equals(rec, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        static string SafeReadAllText(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // Ask before downloading several GB (and, for .7z, a small extractor).
        // kind: which download this is, so the question can tell the truth about
        // what it is for. The same DownloadPrompt serves two different assets (the
        // WSA archive and the game payload), and a hardcoded "the game runs inside
        // it" would be nonsense next to an APK bundle.
        static bool Confirm(Asset a, string kind)
        {
            if (Cfg.Yes) { Log.Info("download pre-approved (--yes)"); return true; }

            // Everything that is useful but not decision-critical goes to the log
            // instead of the question, which stays short on purpose: the user is
            // being asked one thing - spend the bandwidth or not.
            Log.Info("asset: " + a.Name + "  from " + a.Source);
            if (string.IsNullOrEmpty(a.Sha256))
                Log.Warn("no checksum is published for this asset; integrity will be checked by size only");
            if (a.Size > 0) Log.Info("download size: " + Gb(a.Size) + " (" + Size(a.Size) + ")");
            Log.Info("destination: " + Cfg.CacheDir + Path.DirectorySeparatorChar + a.Name);

            // Unpacking a .7z needs a tool most PCs do not have. Instead of
            // fetching it quietly halfway through, the question says so up front -
            // the user approved a game, and this is a separate download that gets
            // run as administrator with no published checksum to check it against.
            var unpacker = a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && Find7z() == null
                         ? "It also downloads a 1 MB tool to unpack it (7zr.exe)."
                         : null;

            Log.Info("asking user to approve download of " + a.Name + " (" + kind + ")");
            Tray.SetStatus("question: download?");
            using (var d = new DownloadPrompt(a, unpacker, kind))
            {
                var r = d.ShowDialog();
                Log.Info("user answered: " + r);
                return r == DialogResult.Yes;
            }
        }

        // ---- the prompts ---------------------------------------------------------
        // Both questions used to be MessageBoxes, and both had the same two problems.
        // A MessageBox cannot show a clickable source, and - because the launcher runs
        // elevated - it opened *behind* whatever the user already had open, so a run
        // could look like it had done nothing at all (observed here: a prompt hidden
        // behind a maximised browser, while the launcher logged its hwnd). Every
        // question in this file is therefore a real dialog, and Attention() gives it
        // the chance a background window is allowed to ask for: it takes focus, and
        // if something else holds focus it flashes its taskbar button.
        //
        // TopMost = true was not enough on its own. Measured, not assumed: the
        // WS_EX_TOPMOST bit stayed clear, and it stayed clear for a plain test form
        // too - on this box, with NVIDIA Overlay / Broadcast and Discord's overlay
        // running, an ordinary app cannot enter the topmost band (SetWindowPos returns
        // True and the bit never lands; the taskbar still manages it, so the mechanism
        // itself works).
        static void Attention(Form f)
        {
            SetWindowPos(f.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            f.Activate();
            // Form.Activate() returns void, so "did we get focus?" is answered by
            // asking the OS who the foreground window actually is.
            var activated = GetForegroundWindow() == f.Handle;
            if (!activated)
            {
                var fi = new Flashwinfo();
                fi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Flashwinfo));
                fi.hwnd = f.Handle;
                fi.dwFlags = FlashAll;
                fi.uCount = 6;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            Log.Info("prompt shown: hwnd=" + f.Handle + " activated=" + activated +
                     " exstyle=0x" + GetWindowLong(f.Handle, GwlExstyle).ToString("x8"));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool FlashWindowEx(ref Flashwinfo pfwi);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Flashwinfo
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        const int GwlExstyle = -20;
        const uint FlashAll = 3;
        static readonly IntPtr HwndTopmost = new IntPtr(-1);
        const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;

        // ---- the Developer Mode question ------------------------------------------
        // Asked before anything is downloaded, so a PC that has never been used for
        // development gets both of its questions at the start instead of one at the
        // start and one in the middle of the run.
        sealed class AskPrompt : Form
        {
            public AskPrompt(string heading, string body)
            {
                Text = "Parchisi STAR";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = true;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                Font = new SD.Font("Segoe UI", 9F);
                ClientSize = new SD.Size(430, 210);

                try
                {
                    var ico = SD.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (ico != null)
                    {
                        Icon = ico;
                        Controls.Add(new PictureBox
                        {
                            Left = 18, Top = 16, Size = new SD.Size(34, 34),
                            SizeMode = PictureBoxSizeMode.Zoom, Image = ico.ToBitmap()
                        });
                    }
                }
                catch { /* an icon is decoration; never let it stop the question */ }

                var head = new Label
                {
                    Left = 62, Top = 16, Width = 352, AutoSize = true,
                    Font = new SD.Font(Font, SD.FontStyle.Bold),
                    ForeColor = SD.Color.FromArgb(32, 32, 32),
                    Text = heading
                };
                var text = new Label
                {
                    Left = 62, Top = 58, Width = 352, AutoSize = true,
                    ForeColor = SD.Color.FromArgb(88, 88, 88),
                    Text = body
                };

                // The rule is placed under the whole paragraph, not at the height
                // DownloadPrompt uses for its single-line body. This question runs
                // to three lines (58..~105) and at y=94 the divider came down
                // between lines two and three, which reads as a strikethrough.
                Controls.Add(new Panel { Left = 18, Top = 132, Size = new SD.Size(394, 1), BackColor = SD.Color.FromArgb(228, 228, 228) });

                var no = new Button { Text = "No", DialogResult = DialogResult.No, Size = new SD.Size(82, 28), Location = new SD.Point(330, 166) };
                var yes = new Button
                {
                    Text = "Yes", DialogResult = DialogResult.Yes, Size = new SD.Size(82, 28),
                    Location = new SD.Point(240, 166),
                    Font = new SD.Font(Font, SD.FontStyle.Bold)
                };
                AcceptButton = yes;
                CancelButton = no;

                Controls.AddRange(new Control[] { head, text, no, yes });

                // Same reason as DownloadPrompt: the primary display is not
                // necessarily the one the user is looking at.
                var wa = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
                Location = new SD.Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Attention(this);
            }
        }

        // ---- the download prompt -------------------------------------------------
        // The room to say what WSA is before asking for three quarters of a gigabyte,
        // with the source the user can check and the size in GB.
        sealed class DownloadPrompt : Form
        {
            // extraNote is for the things that will also be downloaded. The user
            // approved a game, not an unpacker, so when no 7-Zip is present the
            // question says so before the run reaches Extract7z.
            // kind names the download ("Windows Subsystem for Android" / "the game
            // files") - the heading, the "why" body and the "Installs to" row are
            // all derived from it, because one prompt serves both downloads.
            public DownloadPrompt(Asset a, string extraNote, string kind)
            {
                bool wsa = kind.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0;
                Text = "Parchisi STAR";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                // In the taskbar on purpose: if this ever lands on a screen the user
                // is not looking at, there has to be a button to find it by.
                ShowInTaskbar = true;
                TopMost = true;
                // Not CenterScreen: that centres on the primary display regardless of
                // where the user is, and on a two-monitor PC the question can then
                // appear on the other screen, where it reads as "nothing happened".
                StartPosition = FormStartPosition.Manual;
                Font = new SD.Font("Segoe UI", 9F);
                ClientSize = new SD.Size(430, 256);

                try
                {
                    // The game's own icon, so the prompt is recognisably part of the
                    // thing the user double-clicked rather than a system box.
                    var ico = SD.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (ico != null)
                    {
                        Icon = ico;
                        Controls.Add(new PictureBox
                        {
                            Left = 18, Top = 16, Size = new SD.Size(34, 34),
                            SizeMode = PictureBoxSizeMode.Zoom, Image = ico.ToBitmap()
                        });
                    }
                }
                catch { /* an icon is decoration; never let it stop the question */ }

                // "WSA" means nothing to someone who double-clicked a game exe, so the
                // heading spells it out and the body says why it is being asked for.
                // For the game files the same rows tell the other half of the truth:
                // these are the APKs, and they land in the launcher's own cache.
                var head = new Label
                {
                    Left = 62, Top = 16, Width = 352, AutoSize = true,
                    Font = new SD.Font(Font, SD.FontStyle.Bold),
                    ForeColor = SD.Color.FromArgb(32, 32, 32),
                    Text = wsa ? "Windows Subsystem for Android" + Environment.NewLine + "is not installed on this PC"
                               : "The game files" + Environment.NewLine + "are not on this PC yet"
                };
                var body = new Label
                {
                    Left = 62, Top = 58, Width = 352, AutoSize = true,
                    ForeColor = SD.Color.FromArgb(88, 88, 88),
                    Text = (wsa ? "The game runs inside it. Download it now?"
                                : "The game, its browser and the installer tool. Download them now?") +
                           (string.IsNullOrEmpty(extraNote) ? "" : Environment.NewLine + extraNote)
                };

                Controls.Add(new Panel { Left = 18, Top = 94, Size = new SD.Size(394, 1), BackColor = SD.Color.FromArgb(228, 228, 228) });

                Controls.Add(Caption("Download", 106));
                Controls.Add(Value(a.Size > 0 ? Gb(a.Size) + "   (" + Size(a.Size) + ")" : "several GB", 106));
                Controls.Add(Caption("Source", 130));
                Controls.Add(SourceRow(a, 130));
                if (!string.IsNullOrEmpty(a.ReleaseTag))
                    Controls.Add(new Label
                    {
                        Left = 154, Top = 150, Width = 258, AutoSize = true,
                        Font = new SD.Font(Font.FontFamily, 8F),
                        ForeColor = SD.Color.FromArgb(146, 146, 146),
                        Text = "release " + a.ReleaseTag
                    });
                Controls.Add(Caption("Installs to", 176));
                Controls.Add(Value(wsa ? Cfg.WsaRoot : Cfg.CacheDir, 176));

                Controls.Add(new Panel { Left = 18, Top = 202, Size = new SD.Size(394, 1), BackColor = SD.Color.FromArgb(228, 228, 228) });

                var no = new Button { Text = "No", DialogResult = DialogResult.No, Size = new SD.Size(82, 28), Location = new SD.Point(330, 212) };
                var yes = new Button
                {
                    Text = "Yes", DialogResult = DialogResult.Yes, Size = new SD.Size(82, 28),
                    Location = new SD.Point(240, 212),
                    Font = new SD.Font(Font, SD.FontStyle.Bold)
                };
                AcceptButton = yes;
                CancelButton = no;

                Controls.AddRange(new Control[] { head, body, no, yes });

                // Centre on the screen the user is actually on - the one under the
                // cursor - rather than on the primary display.
                var wa = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
                Location = new SD.Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Attention(this);
            }

            static Label Caption(string text, int top)
            {
                return new Label { Left = 18, Top = top, Width = 130, AutoSize = true, ForeColor = SD.Color.FromArgb(128, 128, 128), Text = text };
            }

            static Label Value(string text, int top)
            {
                return new Label { Left = 154, Top = top, Width = 258, AutoSize = true, ForeColor = SD.Color.FromArgb(32, 32, 32), Text = text };
            }

            // The build comes from a third-party repository, so the user should be able
            // to look at it before spending the bandwidth on it.
            static Control SourceRow(Asset a, int top)
            {
                var url = a.ReleaseUrl;
                if (string.IsNullOrEmpty(url))
                    return Value(a.Source, top);

                var text = url;
                var cut = text.IndexOf("/releases", StringComparison.OrdinalIgnoreCase);
                if (cut > 0) text = text.Substring(0, cut);
                text = text.Replace("https://", "").Replace("http://", "").TrimEnd('/');

                var link = new LinkLabel { Left = 154, Top = top, Width = 258, AutoSize = true, Text = text };
                link.LinkColor = SD.Color.FromArgb(0, 102, 204);
                link.ActiveLinkColor = SD.Color.FromArgb(0, 76, 153);
                link.VisitedLinkColor = SD.Color.FromArgb(122, 48, 160);
                link.Links.Add(0, text.Length, url);
                link.LinkClicked += (s, e) =>
                {
                    var target = e.Link.LinkData as string;
                    try { Process.Start(target); }
                    catch (Exception ex) { Log.Warn("could not open " + target + ": " + ex.Message); }
                };
                return link;
            }
        }

        // ---- release discovery (GitHub Releases API) ------------------------------
        // Nothing here hardcodes a file URL: the asset is found by name pattern, newest
        // release first. The SOURCE REPO is an assumption (configurable) - see the report.
        static string HttpGetString(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            var tok = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(tok)) req.Headers["Authorization"] = "Bearer " + tok;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        // The OS lives in the release TAG ("Windows_10_2407.40000.4.0_LTS_8"),
        // never in the asset name ("WSA_2407.40000.4.0_x64_Release-Nightly-
        // GApps-13.0-NoAmazon.7z"). So AssetExclude cannot exclude Windows 10
        // builds - its "Windows_10" pattern matches no asset - and the +30
        // Win11 bonus applied to names never fires either. Today the Windows_11
        // tags happen to sort above the Windows_10 ones, but that is ordering
        // luck: this repo does publish Windows_10 releases, and a Win10-only WSA
        // would not register on a Windows 11 PC.
        static readonly Regex Win10Tag = new Regex(
            @"(^|[^A-Za-z0-9])windows[\s_\-]*10([^0-9]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        static readonly Regex Win11Tag = new Regex(
            @"(^|[^A-Za-z0-9])windows[\s_\-]*11([^0-9]|$)|(^|[^A-Za-z0-9])win[\s_\-]*11([^0-9]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

        static Asset Discover()
        {
            var url = "https://api.github.com/repos/" + Cfg.Repo + "/releases?per_page=20";
            Log.Info("looking for a WSA build: " + url);
            string json = null;
            for (int i = 1; i <= 3 && json == null; i++)
            {
                try { json = HttpGetString(url); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var hr = (ex as WebException) != null ? ((WebException)ex).Response as HttpWebResponse : null;
                    if (hr != null)
                    {
                        var code = (int)hr.StatusCode;
                        var left = hr.Headers["X-RateLimit-Remaining"];
                        var reset = hr.Headers["X-RateLimit-Reset"];
                        try { hr.Close(); } catch { }
                        if (code == 404) throw new InvalidOperationException("repository " + Cfg.Repo + " not found (HTTP 404)");
                        // A rate-limited shared IP used to be retried blindly, then reported
                        // as an internet problem - and it pushes the run into the unverified
                        // cache path.
                        if (code == 403 || code == 429)
                            throw new InvalidOperationException("GitHub said no (HTTP " + code + ", " +
                                (left == "0" ? "rate limit reached, reset at epoch " + (reset ?? "?")
                                             : "access denied") + "); retry later or set GITHUB_TOKEN");
                    }
                    Log.Warn("  attempt " + i + ": " + Describe(ex));
                    if (i == 3) throw;
                    Wait(2000 * i);
                }
            }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var rels = ser.DeserializeObject(json) as object[];
            if (rels == null) throw new InvalidOperationException("unexpected response from the GitHub API");

            var require = new Regex(Cfg.AssetRequire, RegexOptions.IgnoreCase);
            var exclude = Cfg.AssetExclude.Length > 0 ? new Regex(Cfg.AssetExclude, RegexOptions.IgnoreCase) : null;
            var seen = new List<string>();

            foreach (var r in rels)
            {
                var rd = r as Dictionary<string, object>;
                if (rd == null) continue;
                object draft; if (rd.TryGetValue("draft", out draft) && draft is bool && (bool)draft) continue;

                object tagObj;
                var tag = rd.TryGetValue("tag_name", out tagObj) && tagObj != null ? tagObj.ToString() : "";
                if (tag.Length > 0 && Win10Tag.IsMatch(tag))
                {
                    Log.Info("skipping Windows 10 release " + tag + " - this launcher targets Windows 11");
                    continue;
                }

                object av; if (!rd.TryGetValue("assets", out av)) continue;
                var assets = av as object[]; if (assets == null) continue;

                Asset best = null; int bestScore = -1;
                foreach (var x in assets)
                {
                    var ad = x as Dictionary<string, object>; if (ad == null) continue;
                    var name = Str(ad, "name"); seen.Add(name);
                    var isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    if (!isZip && !name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!require.IsMatch(name)) continue;
                    if (exclude != null && exclude.IsMatch(name)) continue;
                    // Only GApps builds qualify - this exact build is the one verified
                    // bootable end-to-end on this machine; the game itself does not
                    // need the store, but an unverified variant is not worth the risk.
                    if (!Regex.IsMatch(name, "MindTheGapps|OpenGApps|GApps", RegexOptions.IgnoreCase)) continue;

                    var score = 50;
                    // The tag is the authoritative OS signal; the name check is
                    // kept for repos that do encode the OS in the filename.
                    if (Win11Tag.IsMatch(tag) ||
                        Regex.IsMatch(name, "Windows_?11|Win_?11", RegexOptions.IgnoreCase)) score += 30;
                    if (isZip) score += 20;                        // zip needs no extra tool
                    if (score <= bestScore) continue;

                    var digest = Str(ad, "digest");               // "sha256:<hex>" when GitHub provides it
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = digest.Substring(7); else digest = "";
                    long size = -1; long.TryParse(Str(ad, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    bestScore = score;
                    best = new Asset
                    {
                        Name = name, Url = Str(ad, "browser_download_url"), Sha256 = digest, Size = size,
                        Source = "github.com/" + Cfg.Repo + " release " + Str(rd, "tag_name"),
                        ReleaseUrl = Str(rd, "html_url"), ReleaseTag = Str(rd, "tag_name")
                    };
                }
                if (best != null && best.Url.Length > 0)
                {
                    Log.Info("selected " + best.Name + " (" + Size(best.Size) + ") from " + best.Source +
                             (best.Sha256.Length > 0 ? ", sha256 published" : ", no sha256 published"));
                    return best;
                }
            }
            throw new InvalidOperationException("no matching .zip/.7z GApps x64 asset in the latest releases of " + Cfg.Repo +
                " (assets seen: " + string.Join(", ", seen.Take(8).ToArray()) + ")");
        }

        // ---- disk space -------------------------------------------------------------
        static long Free(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
            catch (Exception ex)
            {
                // Returning "infinite" silently disables the pre-check; say so, so a later
                // "disk full" is not a mystery.
                Log.Warn("free space unknown for " + path + ": " + ex.Message);
                return long.MaxValue;
            }
        }

        // archive on the cache drive; extraction (~3x, refined exactly for .zip later) on the WSA drive
        static bool NeedSpace(long archiveSize)
        {
            var cacheRoot = Path.GetPathRoot(Path.GetFullPath(Cfg.CacheDir));
            var wsaRoot = Path.GetPathRoot(Path.GetFullPath(Cfg.WsaRoot));
            long needCache = archiveSize + 256L * 1048576;
            long needWsa = archiveSize * 3 + 512L * 1048576;
            bool same = string.Equals(cacheRoot, wsaRoot, StringComparison.OrdinalIgnoreCase);
            var needA = same ? needCache + needWsa : needCache;
            Log.Info("free space: cache drive " + Size(Free(Cfg.CacheDir)) + " (need " + Size(needA) + "), WSA drive " +
                     Size(Free(Cfg.WsaRoot)) + " (need " + Size(needWsa) + ")");
            if (Free(Cfg.CacheDir) < needA) return Fail("not enough free disk space on " + cacheRoot + " (need about " + Size(needA) + ")");
            if (!same && Free(Cfg.WsaRoot) < needWsa) return Fail("not enough free disk space on " + wsaRoot + " (need about " + Size(needWsa) + ")");
            return true;
        }

        // ---- download with resume, retries, progress, hash --------------------------
        // `ui` wraps the transfer in the progress window (used for the WSA
        // archive and the game payload); the core loop itself is unchanged
        // and shared. The label names the asset, so the title stays generic.
        static bool DownloadFile(string url, string dest, long expectedSize, string expectedSha, string label, bool ui = false)
        {
            if (ui)
            {
                ProgressWin.Show("Downloading - " + label);
                try { return DownloadFileCore(url, dest, expectedSize, expectedSha, label); }
                finally { ProgressWin.Finish(); }
            }
            return DownloadFileCore(url, dest, expectedSize, expectedSha, label);
        }

        static bool DownloadFileCore(string url, string dest, long expectedSize, string expectedSha, string label)
        {
            var part = dest + ".part";
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Wait(0);
                try
                {
                    Log.Info("downloading " + label + " (attempt " + attempt + "/" + maxAttempts + ")");
                    DownloadOnce(url, part, expectedSize);

                    var len = new FileInfo(part).Length;
                    if (expectedSize > 0 && len != expectedSize)
                    {
                        if (len > expectedSize) File.Delete(part);
                        throw new IOException("size mismatch: have " + len + ", expected " + expectedSize);
                    }
                    if (!string.IsNullOrEmpty(expectedSha))
                    {
                        Log.Info("verifying SHA-256 ...");
                        ProgressWin.Status("verifying SHA-256 ...");
                        var got = Sha256Of(part);
                        if (!got.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(part);
                            Log.Err("SHA-256 got " + got + ", expected " + expectedSha);
                            return Fail("the download was corrupted; it was discarded. Re-run to download it again");
                        }
                        Log.Ok("SHA-256 verified");
                    }
                    else Log.Warn("no SHA-256 available for " + label + "; integrity not verified beyond size");

                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(part, dest);
                    // Record our own fingerprint: with no published hash this is the only
                    // identity a later run will have to detect a corrupted cache.
                    try
                    {
                        var fp = !string.IsNullOrEmpty(expectedSha) ? expectedSha : Sha256Of(dest);
                        File.WriteAllText(dest + ".sha256", fp);
                    }
                    catch { }
                    Log.Ok("downloaded " + label + " -> " + dest);
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (IsDiskFull(ex)) return Fail("disk full while downloading " + label);
                    // Permanent I/O problems are not transient: retrying a denied ACL or a
                    // cache path that exists as a FILE just burned 4+8+16+32 s of backoff
                    // and reported it as a network failure.
                    if (ex is UnauthorizedAccessException || ex is DirectoryNotFoundException ||
                        ex is System.Security.SecurityException)
                        return Fail("cannot write " + Cfg.CacheDir + ": " + ex.Message);
                    var hr = (ex as WebException) != null ? ((WebException)ex).Response as HttpWebResponse : null;
                    if (hr != null)
                    {
                        var code = (int)hr.StatusCode;
                        try { hr.Close(); } catch { }        // pool holds 2 connections per host
                        if (code == 404 || code == 410)
                            return Fail("download URL is gone (HTTP " + code + "): " + url);
                    }
                    Log.Warn("  download problem: " + Describe(ex));
                    if (attempt == maxAttempts) return Fail("download failed after " + maxAttempts + " attempts: " + Describe(ex));
                    Wait(Math.Min(60000, 2000 << attempt));     // 4s, 8s, 16s, 32s
                }
            }
            return false;
        }

        static void DownloadOnce(string url, string part, long expectedSize)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (expectedSize > 0 && have > expectedSize) { File.Delete(part); have = 0; }
            if (expectedSize > 0 && have == expectedSize) { Log.Info("  .part file is already complete"); return; }

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            // Timeout covers only getting the response, not the transfer - that is
            // ReadWriteTimeout. On the fresh PC that reported this, three attempts
            // to fetch a 1 MB tool each burned about twenty seconds before failing,
            // which is most of a minute and a half of doing nothing. Half the
            // figure still leaves a slow connection room, and the big archive
            // barely notices: it spends its time in the transfer, not the handshake.
            req.Timeout = 15000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            if (have > 0) req.AddRange(have);

            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException wex)
            {
                // 416 = "range not satisfiable": the .part file no longer
                // matches what the server has (re-uploaded or truncated
                // remotely), so resume is impossible and we restart cleanly.
                // Written as a runtime branch rather than `catch ... when`
                // because that is a C# 6 exception filter and this file
                // compiles with the .NET Framework csc (C# 5).
                var bad = wex.Response as HttpWebResponse;
                if (bad == null || (int)bad.StatusCode != 416) throw;
                try { bad.Close(); } catch { }
                File.Delete(part);
                throw new IOException("server rejected the resume range; restarting from zero");
            }

            using (resp)
            {
                var resumed = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                if (resumed)
                {
                    // Status 206 alone does not prove OUR range came back: a proxy can
                    // answer with a different one, and appending at the wrong offset
                    // yields a file of exactly the expected length that is pure garbage.
                    var cr = resp.Headers["Content-Range"];
                    var dash = cr == null ? -1 : cr.IndexOf('-');
                    long start;
                    if (cr == null || dash < 6 || !long.TryParse(cr.Substring(6, dash - 6), out start) || start != have)
                    {
                        Log.Warn("  server answered with a different byte range - restarting from zero");
                        try { File.Delete(part); } catch { }
                        throw new IOException("resume range not honoured; restarting from zero");
                    }
                }
                if (!resumed) have = 0;
                long total = resp.ContentLength >= 0 ? have + resp.ContentLength : expectedSize;
                Log.Info(resumed ? "  resuming at " + Size(have) : "  starting; size " + (total > 0 ? Size(total) : "unknown"));

                using (var src = resp.GetResponseStream())
                using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create,
                                                FileAccess.Write, FileShare.Read))
                {
                    var buf = new byte[81920];
                    long done = have, startBytes = have, lastLog = 0, lastUi = 0;
                    var sw = Stopwatch.StartNew();
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (Cancelled) throw new OperationCanceledException();
                        dst.Write(buf, 0, n);
                        done += n;
                        var rate = (done - startBytes) / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        if (sw.ElapsedMilliseconds - lastUi >= 250)           // window + tray: keep it alive
                        {
                            lastUi = sw.ElapsedMilliseconds;
                            ProgressWin.Set(done, total, rate);
                            // Tray budget is 38 chars (63 minus the title prefix);
                            // the old wording over it and was silently cut off.
                            Tray.SetStatus("downloading - " + MB(done) + "/" +
                                           (total > 0 ? MB(total) : "?") + " MB - " + N(rate) + " MB/s");
                        }
                        if (sw.ElapsedMilliseconds - lastLog >= 5000)         // log: readable, every 5s
                        {
                            lastLog = sw.ElapsedMilliseconds;
                            Log.Info("  " + (total > 0 ? N(done * 100.0 / total) + "%  " : "") +
                                     MB(done) + " MB  " + N(rate) + " MB/s");
                        }
                    }
                    if (total > 0 && done != total) throw new IOException("connection closed early at " + MB(done) + " of " + MB(total) + " MB");
                }
            }
        }

        static string Sha256Of(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        // ---- extraction (temp dir -> atomic move, zip-slip guarded) ----------------
        static bool ExtractAndInstall(string archive)
        {
            var root = Cfg.WsaRoot;
            var tmp = root + ".extracting";
            _tmpDir = tmp;
            try
            {
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);

                bool ok;
                try
                {
                    ok = archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                        ? Extract7z(archive, tmp) : ExtractZip(archive, tmp);
                }
                catch (InvalidDataException ex)
                {
                    try { File.Delete(archive); } catch { }
                    return Fail("downloaded archive is corrupt (" + ex.Message + "); it was deleted - re-run to download again");
                }
                if (!ok) return false;

                var payload = FindManifestRoot(tmp);
                if (payload == null)
                {
                    // Wrong content can never succeed - and CacheValid would otherwise
                    // keep it on disk and hand it back on the next offline run.
                    try { File.Delete(archive); } catch { }
                    return Fail("the downloaded files are not an Android build (no AppxManifest.xml); it was deleted - re-run to download again");
                }

                // A half-installed or foreign C:\WSA is moved aside, never deleted.
                if (Directory.Exists(root))
                {
                    if (Directory.EnumerateFileSystemEntries(root).Any())
                    {
                        var aside = root + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        Log.Warn("existing " + root + " is not a complete install; moving it to " + aside);
                        Directory.Move(root, aside);
                    }
                    else Directory.Delete(root);
                }
                Directory.Move(payload, root);          // same volume: atomic rename
                File.WriteAllText(MarkerPath,
                    "archive=" + Path.GetFileName(archive) + "\r\ncompleted=" +
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\r\n");
                Log.Ok("WSA installed to " + root);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Path exists as a FILE, or a deny ACL from an older install under
                // another user: not an IO problem, and it needs its own message.
                return Fail("no permission to write " + root + ": " + ex.Message);
            }
            catch (IOException ex)
            {
                // One catch with a branch, not two catch clauses: without C# 6
                // exception filters two `catch (IOException ...)` handlers would
                // be an unreachable-block error. IsDiskFull decides which of
                // the two distinct causes we report.
                if (!IsDiskFull(ex))
                    return Fail("could not place WSA files in " + root + ": " + ex.Message +
                                " (is WSA running or a file locked?)");
                return Fail("disk full while extracting: " + ex.Message);
            }
            finally { CleanupTemp(); _tmpDir = null; }
        }

        static string FindManifestRoot(string dir)
        {
            if (File.Exists(Path.Combine(dir, "AppxManifest.xml"))) return dir;
            var hit = Directory.GetFiles(dir, "AppxManifest.xml", SearchOption.AllDirectories)
                .OrderBy(p => p.Length).FirstOrDefault();
            return hit == null ? null : Path.GetDirectoryName(hit);
        }

        static bool ExtractZip(string zip, string dest)
        {
            Log.Info("extracting " + zip + " -> " + dest);
            using (var za = ZipFile.OpenRead(zip))
            {
                long total = za.Entries.Sum(e => e.Length);
                var need = total + 512L * 1048576;
                if (Free(dest) < need) return Fail("not enough free space to extract: need " + Size(need) + ", have " + Size(Free(dest)));

                var destFull = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
                long done = 0; var sw = Stopwatch.StartNew(); long lastLog = 0;
                foreach (var e in za.Entries)
                {
                    if (Cancelled) throw new OperationCanceledException();
                    var full = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', '\\')));
                    // zip-slip: an entry like ..\..\Windows\x must never leave the destination.
                    if (!full.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                        return Fail("unsafe path in archive (zip-slip), refusing to extract: " + e.FullName);
                    if (e.FullName.EndsWith("/") || e.Name.Length == 0) { Directory.CreateDirectory(full); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    e.ExtractToFile(full, true);
                    done += e.Length;
                    if (sw.ElapsedMilliseconds - lastLog >= 5000)
                    {
                        lastLog = sw.ElapsedMilliseconds;
                        Log.Info("  extracted " + MB(done) + " / " + MB(total) + " MB");
                    }
                }
            }
            Log.Ok("extraction finished");
            return true;
        }

        static string Find7z()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var c in new[]
            {
                Path.Combine(pf, "7-Zip", "7z.exe"), Path.Combine(pf86, "7-Zip", "7z.exe"),
                Path.Combine(Cfg.CacheDir, "7zr.exe"), Path.Combine(Cfg.AppDir, "7zr.exe")
            }) if (File.Exists(c)) return c;
            return null;
        }

        static bool Extract7z(string archive, string dest)
        {
            var exe = Find7z();
            if (exe == null)
            {
                // The download question names this tool when it is going to be
                // needed (see Confirm): it is a second download the user did not ask
                // for, it has no published checksum, and it is then run as admin.
                var target = Path.Combine(Cfg.CacheDir, "7zr.exe");
                Log.Warn("no 7-Zip found; downloading 7zr.exe from 7-zip.org (no published checksum to verify against)");
                if (!DownloadFile("https://www.7-zip.org/a/7zr.exe", target, -1, "", "7zr.exe")) return false;
                exe = target;
            }
            // No published checksum for 7zr.exe, so at least prove it is a plausible
            // program file before running it - a captive portal's HTML page would
            // otherwise sit in the cache and fail identically forever.
            if (string.Equals(Path.GetFileName(exe), "7zr.exe", StringComparison.OrdinalIgnoreCase))
            {
                string why;
                if (!PlausiblePe(exe, out why))
                {
                    try { File.Delete(exe); } catch { }
                    return Fail("the unpacker (7zr.exe) is unusable (" + why + "); it was deleted - re-run to download it again");
                }
            }
            Log.Info("extracting with " + exe);
            string o;
            if (!Run(exe, "x \"" + archive + "\" -o\"" + dest + "\" -y", "7z extract", 60 * 60 * 1000, out o))
            {
                // A damaged archive fails this way on EVERY run while the cache still
                // validates on size alone (offline path) - discard it so the next run
                // downloads a fresh copy instead of looping forever.
                var msg = o ?? "";
                if (msg.IndexOf("Cannot open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("CRC Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("Headers Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("Unexpected end of", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { File.Delete(archive); } catch { }
                    return Fail("the cached archive is damaged; it was deleted - re-run to download it again");
                }
                // The unpacker's tail is in the log now (LogOutputTail), not here.
                return Fail("the files could not be unpacked (see the unpacker's message in the log)");
            }
            return true;
        }

        static bool PlausiblePe(string path, out string why)
        {
            why = null;
            try
            {
                using (var fs = File.OpenRead(path))
                    if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') { why = "not a program file"; return false; }
                var len = new FileInfo(path).Length;
                if (len < 100000) { why = "only " + len + " bytes"; return false; }
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        static string Tail(string s) { s = (s ?? "").Trim(); return s.Length > 300 ? s.Substring(s.Length - 300) : s; }

        // Optional, off by default (it is a second download the user did not approve).
        // --------------------------------------------------- 5. dependencies
        sealed class Dep
        {
            public string Name, Min, FileRegex;
            public Dep(string n, string m, string r) { Name = n; Min = m; FileRegex = r; }
        }

        // Explicit file patterns: the plain VCLibs pattern must NOT match the UWPDesktop package.
        static readonly Dep[] Deps =
        {
            new Dep("Microsoft.UI.Xaml.2.8", "8.2208.12001.0", @"^Microsoft\.UI\.Xaml\.2\.8.*x64.*\.appx$"),
            new Dep("Microsoft.VCLibs.140.00.UWPDesktop", "14.0.33728.0", @"^Microsoft\.VCLibs\.140\.00\.UWPDesktop.*x64.*\.appx$"),
            new Dep("Microsoft.VCLibs.140.00", "14.0.33519.0", @"^Microsoft\.VCLibs\.140\.00(?!\.UWPDesktop).*x64.*\.appx$")
        };

        static bool EnsureDependencies()
        {
            foreach (var d in Deps)
            {
                var have = InstalledVersion(d.Name);
                Log.Info(d.Name + ": installed=" + (have ?? "none") + "  minimum=" + d.Min);
                if (have != null && CompareVersions(have, d.Min) >= 0) { Log.Info("  satisfied"); continue; }

                var rx = new Regex(d.FileRegex, RegexOptions.IgnoreCase);
                var appx = Directory.GetFiles(Cfg.WsaRoot, "*.appx").FirstOrDefault(f => rx.IsMatch(Path.GetFileName(f)));
                if (appx == null) return Fail("the Android files are missing a required part (" + d.Name + ")");

                Log.Info("  installing " + appx);
                string o;
                if (!RunPs("Add-AppxPackage -Path " + PsQuote(appx), "add " + d.Name, 10 * 60 * 1000, out o))
                    return Fail("installing " + d.Name + " failed (see the unpacker/installer output in the log)");

                var after = InstalledVersion(d.Name);
                Log.Info("  now installed: " + (after ?? "none"));
                if (after == null || CompareVersions(after, d.Min) < 0)
                    return Fail(d.Name + " is still below the required " + d.Min + " after install (found " + (after ?? "none") + ")");
            }
            return true;
        }

        static string InstalledVersion(string name)
        {
            string o;
            if (!RunPs("(Get-AppxPackage -AllUsers -Name " + PsQuote(name) +
                       " | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1).Version",
                       "query " + name, 90000, out o)) return null;
            var v = o.Trim();
            return v.Length == 0 ? null : v;
        }

        static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (Version.TryParse(a, out va) && Version.TryParse(b, out vb)) return va.CompareTo(vb);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // --------------------------------------------------- 6. registration
        sealed class PkgInfo { public string Status, Version, Location; }

        static PkgInfo GetWsaPackage()
        {
            string o;
            if (!RunPs("$p = Get-AppxPackage -Name " + PsQuote(PackageFamily) + " | Select-Object -First 1; " +
                       "if ($p) { $p.Status.ToString() + '|' + $p.Version + '|' + $p.InstallLocation }",
                       "get package", 90000, out o)) return null;
            var s = o.Trim();
            if (s.Length == 0) return null;
            var parts = s.Split('|');
            return new PkgInfo
            {
                Status = parts[0],
                Version = parts.Length > 1 ? parts[1] : "?",
                Location = parts.Length > 2 ? parts[2] : ""
            };
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // --------------------------------------- 6. sideloading + WSA registration
        // Windows refuses Add-AppxPackage -Register of an unpacked app (0x80073CFF)
        // unless sideloading is allowed, which on a PC that has never been used for
        // development it is not - exactly the machine this exe is aimed at. That is
        // why the setting gets its own early step, before anything is downloaded.

        // -1 = absent or unreadable. The value lives in the native hive, so the view
        // is asked for explicitly rather than inherited from the process.
        static int ReadHklmDword(string subKey, string name)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(subKey))
                {
                    if (k == null) return -1;
                    var v = k.GetValue(name);
                    return v is int ? (int)v : -1;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("  registry read " + subKey + "\\" + name + ": " + ex.Message);
                return -1;
            }
        }

        // Settings writes two values for one switch: "Sideload apps" sets one and
        // Developer Mode implies both. 1 = allowed, 0 = forbidden, -1 = never
        // touched, which on Home and Pro means off.
        static int ReadSideloadState(string subKey)
        {
            var side = ReadHklmDword(subKey, "AllowAllTrustedApps");
            var dev = ReadHklmDword(subKey, "AllowDevelopmentWithoutDevLicense");
            if (side == 1 || dev == 1) return 1;
            if (side == 0 || dev == 0) return 0;
            return -1;
        }

        static bool EnsureDeveloperMode()
        {
            const string appModel = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
            const string policy = @"SOFTWARE\Policies\Microsoft\Windows\Appx";

            // A policy of 0 wins over the setting, and this process cannot change it.
            if (ReadSideloadState(policy) == 0)
            {
                // Registry path and editor path belong in the log, not in the window.
                Log.Err("policy in force: HKLM\\" + policy + " = 0 - allowed in Local Group Policy " +
                        "Editor > Computer Configuration > Administrative Templates > Windows Components > " +
                        "App Package Deployment (this process cannot change it)");
                return Fail("Developer Mode is switched off by this PC's administrator. Ask whoever manages this PC to allow it.");
            }

            if (ReadSideloadState(appModel) == 1)
            {
                Log.Info("Developer Mode is already on (sideloading allowed)");
                return true;
            }

            Log.Warn("Developer Mode is off; Windows will not register WSA without it (0x80073CFF)");
            if (Cfg.Yes) Log.Info("turning it on without asking (--yes)");
            else
            {
                Log.Info("asking user to approve turning on Developer Mode");
                Tray.SetStatus("question: turn on Developer Mode?");
                using (var d = new AskPrompt(
                    "Windows needs Developer Mode" + Environment.NewLine + "to install the game",
                    "Windows will not install the game's Android subsystem" + Environment.NewLine +
                    "without it. It is one setting, it stays on, and you" + Environment.NewLine +
                    "can turn it off again later. Turn it on now?"))
                {
                    var r = d.ShowDialog();
                    Log.Info("user answered: " + r);
                    if (r != DialogResult.Yes)
                    {
                        // The same quiet path as declining the download: a
                        // decision the user is entitled to, not a fault.
                        Log.Warn("Developer Mode declined by the user; nothing was changed");
                        throw new OperationCanceledException();
                    }
                }
            }

            try
            {
                // Both values: Settings writes one for "Sideload apps" and Developer
                // Mode implies the other, and registering WSA wants the pair.
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.CreateSubKey(appModel))
                {
                    k.SetValue("AllowAllTrustedApps", 1, RegistryValueKind.DWord);
                    k.SetValue("AllowDevelopmentWithoutDevLicense", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { return Fail("could not turn on Developer Mode: " + ex.Message); }

            if (ReadSideloadState(appModel) != 1)
                return Fail("Developer Mode did not turn on. Turn it on in Settings > System > " +
                            "For developers, then re-run.");
            Log.Ok("Developer Mode turned on (HKLM\\" + appModel + ")");
            return true;
        }

        static bool EnsureRegistered()
        {
            var pkg = GetWsaPackage();
            if (pkg != null)
            {
                Log.Info("registered: status=" + pkg.Status + " version=" + pkg.Version + " at " + pkg.Location);
                if (pkg.Status == "Ok" && SamePath(pkg.Location, Cfg.WsaRoot)) return true;
                Log.Warn("status is not Ok or it points at a different folder; re-registering from " + Cfg.WsaRoot);
            }
            else Log.Info("not registered yet");

            var manifest = Path.Combine(Cfg.WsaRoot, "AppxManifest.xml");
            string o;
            if (!RunPs("Add-AppxPackage -ForceApplicationShutdown -ForceUpdateFromAnyVersion -Register " + PsQuote(manifest),
                       "register", 10 * 60 * 1000, out o))
            {
                // The whole output goes to the log, and the window gets the
                // diagnosis. Never match on the wording - Windows is localised, and
                // the PC that reported this was running French - only on the code
                // and the link Microsoft puts in the message.
                Log.Warn("Add-AppxPackage said:" + Environment.NewLine + (o ?? "").Trim());
                var code = Regex.Match(o ?? "", "0x[0-9A-Fa-f]{8}");
                var sideload = (code.Success && code.Value.Equals("0x80073CFF", StringComparison.OrdinalIgnoreCase))
                               || (o ?? "").IndexOf("LinkId=231020", StringComparison.OrdinalIgnoreCase) >= 0;
                if (sideload)
                    return Fail("Windows refused to install the Android files. Turn on Developer Mode " +
                                "(Settings > System > For developers), then re-run");
                // The full Add-AppxPackage output is already logged above (localised text
                // and error code); the window only needs the headline.
                return Fail("Windows would not register the Android files" +
                            (code.Success ? " (" + code.Value + ")" : ""));
            }

            pkg = GetWsaPackage();
            if (pkg == null) return Fail("WSA is still not registered after Add-AppxPackage");
            Log.Info("registered: status=" + pkg.Status + " version=" + pkg.Version);
            return pkg.Status == "Ok" || Fail("WSA package status is '" + pkg.Status + "', expected 'Ok'");
        }

        // ----------------------------------------------- 7. userdata template
        // WSA copies <package root>\userdata.vhdx on first boot. Without it WsaClient aborts
        // with 0xc0000409 in ucrtbase.dll. A blank 8 GB dynamic VHDX is accepted; WSA
        // formats it and grows it itself. New-VHD needs the Hyper-V module (absent on
        // Windows Home), so diskpart is the fallback.
        static bool EnsureUserdataTemplate()
        {
            var tpl = Path.Combine(Cfg.WsaRoot, "userdata.vhdx");
            if (File.Exists(tpl))
            {
                Log.Info("template present: " + tpl + "  size=" + MB(new FileInfo(tpl).Length) + " MB (kept; WSA formats blank ones)");
                return true;
            }

            Log.Info("no template; creating a blank 8 GB dynamic VHDX");
            string o;
            bool made = RunPs("New-VHD -Path " + PsQuote(tpl) + " -SizeBytes 8GB -Dynamic | Out-Null",
                              "New-VHD", 5 * 60 * 1000, out o) && File.Exists(tpl);
            if (!made)
            {
                Log.Warn("New-VHD unavailable or failed (" + Tail(o) + "); trying diskpart");
                made = DiskpartCreate(tpl);
            }
            if (!made) return Fail("could not create the Android disk file (New-VHD / diskpart failed)");
            Log.Info("created " + tpl + " (" + MB(new FileInfo(tpl).Length) + " MB on disk)");
            return true;
        }

        static bool DiskpartCreate(string vhdx)
        {
            var script = Path.Combine(Path.GetTempPath(), "pp-diskpart-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(script, "create vdisk file=\"" + vhdx + "\" maximum=8192 type=expandable\r\n");
                string o;
                Run("diskpart.exe", "/s \"" + script + "\"", "diskpart", 3 * 60 * 1000, out o);
                return File.Exists(vhdx);
            }
            finally { try { File.Delete(script); } catch { } }
        }

        // ------------------------------------------------------- 8. service
        static bool EnsureService()
        {
            using (var sc = new ServiceController("WsaService"))
            {
                try { var st = sc.Status; }
                catch (InvalidOperationException) { return Fail("the Android service is missing - the install did not complete"); }

                Log.Info("WsaService: " + sc.Status);
                if (sc.Status == ServiceControllerStatus.Running) return true;
                try
                {
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(90));
                }
                catch (System.ServiceProcess.TimeoutException) { return Fail("the Android service did not start within 90 s"); }
                catch (InvalidOperationException ex) { return Fail("could not start the Android service: " + ex.Message); }
                sc.Refresh();
                Log.Info("WsaService: " + sc.Status);
                return sc.Status == ServiceControllerStatus.Running || Fail("the Android service is " + sc.Status);
            }
        }

        // ------------------------------------------------------- UIA helpers
        // A UIA read blocks for as long as the target app refuses to pump messages:
        // Current.Name on another process's window is a WM_GETTEXT sent to that
        // process's thread. Measured on a virgin WSA first boot, one such read sat
        // in the wait loop for 16 minutes (06:35:21 -> 06:51:57, resuming 98 ms
        // after "Boot is finished"), so the user saw nothing while the clock ran.
        // Nothing here can be cancelled, so such work runs on a helper thread with
        // a budget: if it overruns, the caller keeps its last known state instead
        // of freezing. One attempt per slot at a time - a stuck probe is never
        // restarted, it just finishes in the background.
        sealed class Bounded
        {
            readonly object _l = new object();
            bool _busy;
            int _slowAt = int.MinValue;

            public bool Run(Action work, int timeoutMs, string tag)
            {
                Thread t = null;
                lock (_l)
                {
                    if (!_busy)
                    {
                        _busy = true;
                        t = new Thread(() =>
                        {
                            try { work(); }
                            catch (Exception ex) { Log.Warn(tag + ": " + ex.Message); }
                            finally { lock (_l) { _busy = false; } }
                        });
                        try
                        {
                            t.IsBackground = true;
                            t.SetApartmentState(ApartmentState.STA);
                            t.Start();
                        }
                        // A failed Start used to leave _busy latched forever, wedging
                        // this slot so every later probe timed out on stale data.
                        catch (Exception ex)
                        {
                            t = null; _busy = false;
                            Log.Warn(tag + ": could not start helper thread: " + ex.Message);
                        }
                    }
                }
                bool ok;
                if (t != null) ok = t.Join(timeoutMs);
                else
                {
                    var sw = Stopwatch.StartNew();
                    ok = false;
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        lock (_l) if (!_busy) { ok = true; break; }
                        Thread.Sleep(50);
                    }
                }
                if (ok) return true;
                var now = Environment.TickCount;
                lock (_l)
                {
                    if (_slowAt != int.MinValue && now - _slowAt < 30000) return false;
                    _slowAt = now;
                }
                Log.Warn(tag + " did not answer within " + timeoutMs + " ms (an app window is not pumping messages) - not waiting");
                return false;
            }
        }

        static readonly Bounded _probe = new Bounded();
        static readonly object _probeData = new object();
        static List<string[]> _probeLast = new List<string[]>();

        // Bounded probe: at most `timeoutMs` per call, result may be the last
        // completed one (stale-but-safe: every caller treats "no window" as
        // "keep waiting / try again later", never as a failure).
        static List<string[]> WsaWindows(int timeoutMs)
        {
            _probe.Run(() =>
            {
                var r = WsaWindowsCore();
                lock (_probeData) { _probeLast = r; }
            }, timeoutMs, "window probe");
            lock (_probeData) { return _probeLast; }
        }

        static List<string[]> WsaWindows() { return WsaWindows(5000); }

        // Top-level windows owned by WsaClient: [name, class]. Every property read is guarded
        // because UIA elements vanish between enumeration and read.
        static List<string[]> WsaWindowsCore()
        {
            var res = new List<string[]>();
            var procs = Process.GetProcessesByName("WsaClient");
            try
            {
                if (procs.Length == 0) return res;
                var root = AutomationElement.RootElement;
                foreach (var p in procs)
                {
                    AutomationElementCollection wins;
                    try { wins = root.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id)); }
                    catch { continue; }
                    foreach (AutomationElement w in wins)
                    {
                        try { res.Add(new[] { w.Current.Name ?? "", w.Current.ClassName ?? "" }); }
                        catch (ElementNotAvailableException) { }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("  window probe: " + ex.Message); }
            finally { foreach (var p in procs) p.Dispose(); }
            return res;
        }

        // An Android app surface = a WsaClient window that is NOT the #32770 consent dialog.
        // (The WSA Settings app is a different process, so it can no longer false-positive.)
        static bool AndroidWindowPresent() { return WsaWindows().Any(w => w[1] != "#32770"); }

        static bool GameWindowPresent()
        {
            return WsaWindows().Any(w => w[1] != "#32770" &&
                w[0].IndexOf(Cfg.GameWindowTitle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        enum Click { NoDialog, Clicked, Failed }

        static readonly Bounded _consent = new Bounded();

        // First-run consent dialog: class #32770, button "Continue". Nothing boots until answered.
        // It reads the same non-pumping process as the window probe, so it carries the same
        // freeze risk: bounded, one attempt at a time, and an overrun simply skips this tick -
        // the dialog is retried every 10 s anyway.
        static Click TryClickContinue()
        {
            var res = Click.NoDialog;
            if (_consent.Run(() => { res = TryClickContinueCore(); }, 5000, "consent dialog probe"))
                return res;
            // Overrun: "no dialog" could not be PROVEN while the probe was stuck, and
            // reporting it as such silenced the "click Continue" notice - the run then
            // ended at the 30-minute timeout with the wrong error.
            return Click.Failed;
        }

        static Click TryClickContinueCore()
        {
            var procs = Process.GetProcessesByName("WsaClient");
            try
            {
                if (procs.Length == 0) return Click.NoDialog;
                var root = AutomationElement.RootElement;
                var seen = false;
                foreach (var p in procs)
                {
                    AutomationElementCollection wins;
                    try { wins = root.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id)); }
                    catch { continue; }
                    foreach (AutomationElement w in wins)
                    {
                        try
                        {
                            if (w.Current.ClassName != "#32770") continue;
                            seen = true;
                            var title = w.Current.Name ?? "";
                            Log.Info("  found dialog '" + title + "' (pid " + p.Id + ")");
                            // Only the first-run consent dialog may be confirmed without
                            // the user seeing it: any other #32770 dialog with a button
                            // named "Continue" used to be silently approved. `seen` still
                            // counts so an unmatched (e.g. localised) title still raises
                            // the notice instead of failing silently.
                            if (title.IndexOf("Optional diagnostic data", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                Log.Warn("  not the first-run consent dialog - leaving it alone");
                                continue;
                            }
                            var btn = w.FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.NameProperty, "Continue"));
                            object inv;
                            if (btn != null && btn.TryGetCurrentPattern(InvokePattern.Pattern, out inv))
                            {
                                ((InvokePattern)inv).Invoke();
                                Log.Ok("  clicked 'Continue' on the first-run consent dialog");
                                Wait(1500);
                                return Click.Clicked;
                            }
                        }
                        catch (ElementNotAvailableException) { }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Log.Warn("  consent click: " + ex.Message); }
                    }
                }
                return seen ? Click.Failed : Click.NoDialog;
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }

        // ---------------------------------------------------------- 9. boot
        static LogWatcher Watcher()
        {
            if (_watcher == null)
                _watcher = new LogWatcher(_bootStartUtc == DateTime.MinValue
                    ? DateTime.UtcNow.AddMinutes(-30) : _bootStartUtc.AddSeconds(-5));
            return _watcher;
        }

        static bool BootWsa()
        {
            if (PortOpen(AdbPort)) { Log.Info("port " + AdbPort + " already open - WSA is running"); return true; }

            // If the VM is already alive its current log belongs to this session, so older log
            // data is legitimate; otherwise only data written after we start counts.
            var vmWasUp = CountProcs("vmmemWSA") > 0;
            _bootStartUtc = vmWasUp ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow;
            _watcher = null;
            var watcher = Watcher();

            var lc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "Packages", PackageName, "LocalCache");
            var ud = Path.Combine(lc, "userdata.2.vhdx");
            Log.Info("VM already running: " + vmWasUp + "; userdata.2.vhdx: " +
                     (File.Exists(ud) ? MB(new FileInfo(ud).Length) + " MB" : "absent (first boot)"));

            Log.Info("launching wsa://" + BootPackage + " (protocol activation is mandatory)");
            Status.Show("Starting Android...");
            if (!Open("wsa://" + BootPackage)) { Status.Close(); return Fail("Windows could not open the Android part; re-run the launcher"); }

            var sw = Stopwatch.StartNew();
            long lastClick = -10000, lastCrash = 0, lastLog = 0;
            var noticed = false;
            var timeoutMs = Cfg.BootTimeoutMin * 60 * 1000;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Wait(PollMs);

                if (sw.ElapsedMilliseconds - lastClick > 10000)
                {
                    lastClick = sw.ElapsedMilliseconds;
                    var c = TryClickContinue();
                    if (c == Click.Failed)
                    {
                        if (!noticed)
                        {
                            noticed = true;
                            Notice.Show("Action needed",
                                "The Android setup needs one click.\r\nPlease click 'Continue'.");
                        }
                    }
                    // Closed on Clicked AND on a clean "no dialog": the user may have
                    // clicked Continue by hand, in which case the probe returns
                    // NoDialog and the notice used to stay up for the rest of boot.
                    else if (noticed) { noticed = false; Notice.Close(); }
                }

                watcher.Poll();
                var vm = CountProcs("vmmemWSA") > 0;

                if (sw.ElapsedMilliseconds - lastLog >= 30000)
                {
                    lastLog = sw.ElapsedMilliseconds;
                    Log.Info("  t+" + (sw.ElapsedMilliseconds / 1000) + "s  VM " + (vm ? "up" : "not yet") +
                             "  userdata=" + (File.Exists(ud) ? MB(new FileInfo(ud).Length) + " MB" : "absent"));
                }

                // Crash probe: throttled - each query spawns PowerShell.
                if (!vm && sw.ElapsedMilliseconds > 60000 && sw.ElapsedMilliseconds - lastCrash > 30000)
                {
                    lastCrash = sw.ElapsedMilliseconds;
                    var crash = LastWsaCrash();
                    if (crash != null)
                    {
                        Log.Err("WsaClient crash record: " + crash);   // path + exception code, log only
                        Notice.Close(); Status.Close();
                        return Fail("the Android part crashed while starting");
                    }
                }

                if (watcher.BootFinished) { Notice.Close(); Status.Close(); Log.Ok("Android boot complete after " + (sw.ElapsedMilliseconds / 1000) + "s (logcat)"); CloseBootWindow(); return true; }
                if (PortOpen(AdbPort)) { Notice.Close(); Status.Close(); Log.Ok("adb bridge open after " + (sw.ElapsedMilliseconds / 1000) + "s"); CloseBootWindow(); return true; }
                if (AndroidWindowPresent()) { Notice.Close(); Status.Close(); Log.Ok("Android window visible after " + (sw.ElapsedMilliseconds / 1000) + "s"); CloseBootWindow(); return true; }
            }
            Notice.Close();
            Status.Close();
            return Fail("Android did not finish booting within " + Cfg.BootTimeoutMin + " minutes");
        }

        // Boot goes through protocol activation of the always-present store
        // package; this launcher never uses the store, so close its window
        // again before anything else happens. Best-effort: a leftover window
        // is cosmetic, not a fault.
        static void CloseBootWindow()
        {
            var o = Adb("shell am force-stop " + BootPackage, 10000);
            if (o != null) Log.Info("closed the boot window (" + BootPackage + ")");
        }

        static string LastWsaCrash()
        {
            string o;
            if (!RunPs("Get-WinEvent -FilterHashtable @{LogName='Application';Id=1000;StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | " +
                       "Where-Object { $_.Message -match 'WsaClient' } | Select-Object -First 1 | ForEach-Object { $_.Message }",
                       "crash query", 60000, out o)) return null;
            if (o.IndexOf("WsaClient", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var m = Regex.Match(o, @"Faulting application path:\s*(.+)");
            var code = Regex.Match(o, @"Exception code:\s*(0x[0-9a-fA-F]+)");
            return (m.Success ? m.Groups[1].Value.Trim() : "WsaClient") + (code.Success ? "  " + code.Groups[1].Value : "");
        }

        // ------------------------------------------------------------ adb
        // adb is REQUIRED for the sideload install; it ships inside the game
        // payload zip (extracted to CacheDir\payload) and falls back to an
        // adb that happens to be on the machine.
        static string AdbPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var c in new[]
                {
                    Path.Combine(Cfg.CacheDir, "payload", "adb.exe"),
                    Path.Combine(Cfg.AppDir, "adb.exe"),
                    Path.Combine(home, "platform-tools", "adb.exe")
                }) if (File.Exists(c)) return c;
                return null;
            }
        }

        static string Serial { get { return "127.0.0.1:" + AdbPort; } }

        static bool AdbReady()
        {
            var adb = AdbPath;
            if (adb == null || !PortOpen(AdbPort)) { _adbConnected = false; return false; }
            if (_adbConnected) return true;
            string o;
            if (!Run(adb, "connect " + Serial, "adb connect", 15000, out o))
            { _adbConnected = false; return false; }
            o = (o ?? "").Trim();
            Log.Info("  adb connect: " + o);
            // "connected" on its own also matches failure text ("the connected party did
            // not properly respond"), which used to latch the cache on a dead bridge.
            _adbConnected = o.StartsWith("connected to", StringComparison.OrdinalIgnoreCase)
                         || o.StartsWith("already connected", StringComparison.OrdinalIgnoreCase);
            return _adbConnected;
        }

        // null = the bridge is unavailable or the command failed; "" = it ran and printed
        // nothing. Callers must not read error text as if it were command output - an
        // "error: device offline" line used to count as a set getprop and skipped the
        // ASTC fix, and as evidence in GameInstalled.
        static string Adb(string args, int timeoutMs)
        {
            if (!AdbReady()) return null;
            string o;
            if (!Run(AdbPath, "-s " + Serial + " " + args, "adb " + args, timeoutMs, out o))
            { _adbConnected = false; return null; }   // a failing command drops the cache
            o = (o ?? "").Trim();
            if (o.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            { _adbConnected = false; return null; }   // "device not found" / "offline"
            return o;
        }

        // ----------------------------------------- 10. game files + sideload install
        // No Play Store, no sign-in: the game, Firefox and adb ship as one zip on this
        // repo's own releases, downloaded with the same ask-first machinery as the WSA
        // archive. The PNG texture split is MANDATORY (.astc renders black); Firefox is
        // required for Facebook login but best-effort - it never blocks the launch.
        static string InstallMarker { get { return Path.Combine(Cfg.WsaRoot, ".pp-game"); } }
        static string PayloadDir { get { return Path.Combine(Cfg.CacheDir, "payload"); } }

        static readonly string[] GameApks =
        {
            "base.apk", "split_config.arm64_v8a.apk", "split_config.etc1_rgb8_group_hd.apk"
        };
        static readonly string[] FirefoxApks =
        {
            "firefox_base.apk", "firefox_split_config.mdpi.apk", "firefox_split_config.x86_64.apk"
        };

        // null = adb could not answer; otherwise the pm path output (base + splits).
        static string AdbGamePmPath()
        {
            var outp = Adb("shell pm path " + GamePackage, 10000);
            if (outp == null) return null;
            foreach (var l in outp.Split('\n'))
                if (l.Trim().StartsWith("package:", StringComparison.Ordinal)) return outp;
            return null;                        // ran, printed no package path
        }

        static bool RememberInstalled()
        {
            try
            {
                if (!File.Exists(InstallMarker)) File.WriteAllText(InstallMarker, GamePackage);
            }
            catch (Exception ex) { Log.Warn("  could not write install marker: " + ex.Message); }
            return true;
        }

        static bool EnsureGameInstalled()
        {
            if (!EnsurePayloadFiles()) return false;    // brings adb + APKs when missing

            var paths = AdbGamePmPath();
            if (paths != null)
            {
                if (paths.IndexOf("split_config.etc1_rgb8_group_hd", StringComparison.Ordinal) >= 0)
                {
                    Log.Ok("game already installed, texture split verified");
                    RememberInstalled();
                    BestEffortFirefox();
                    return true;
                }
                Log.Warn("game installed but WRONG texture split (etc1_rgb8 missing) -> would render black; reinstalling");
            }
            else if (File.Exists(InstallMarker))
                Log.Warn("install marker present but Android does not see the game; installing again");

            if (!InstallGame()) return false;
            BestEffortFirefox();
            return true;
        }

        static bool EnsurePayloadFiles()
        {
            if (File.Exists(Path.Combine(PayloadDir, "adb.exe")) &&
                File.Exists(Path.Combine(PayloadDir, "base.apk")))
            { Log.Info("game files present: " + PayloadDir); return true; }

            Log.Info("game files are not in the cache; they must be downloaded");
            try { Directory.CreateDirectory(Cfg.CacheDir); }
            catch (Exception ex) { return Fail("cannot create cache dir " + Cfg.CacheDir + ": " + Describe(ex)); }

            Asset a = null; string discoverErr = null;
            try { a = Cfg.PayloadUrl.Length > 0 ? FromPayloadUrl(Cfg.PayloadUrl) : DiscoverPayload(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { discoverErr = Describe(ex); Log.Warn("game file lookup failed: " + discoverErr); }

            var target = Path.Combine(Cfg.CacheDir, a != null ? a.Name : "ParchisiPayload.zip");
            bool okCached = false;
            try { okCached = a != null && File.Exists(target) && CacheValid(target, a); }
            catch (Exception ex) { Log.Warn("cached game files unreadable: " + Describe(ex)); }

            if (!okCached)
            {
                if (a == null)
                    return Fail("could not find the game files (" + discoverErr + "). Check your internet connection");
                // Saying "No" is a decision, not a fault - same quiet path as the
                // tray's Exit: exit 130, nothing changed.
                if (!Confirm(a, "the game files"))
                {
                    Log.Warn("game file download declined by the user; nothing was changed");
                    throw new OperationCanceledException();
                }
                if (!NeedSpace(a.Size > 0 ? a.Size : 600L * 1048576)) return false;
                if (!DownloadFile(a.Url, target, a.Size, a.Sha256, a.Name, true)) return false;
            }
            else Log.Ok("verified cached game files: " + target);

            return ExtractPayload(target);
        }

        static Asset FromPayloadUrl(string url)
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrEmpty(name)) name = "ParchisiPayload.zip";
            return new Asset { Name = name, Url = url, Sha256 = Cfg.PayloadSha256, Source = "configured URL" };
        }

        // Newest release first; the payload lives on this repo's own releases.
        static Asset DiscoverPayload()
        {
            var url = "https://api.github.com/repos/" + Cfg.PayloadRepo + "/releases?per_page=20";
            Log.Info("looking for the game files: " + url);
            string json = null;
            for (int i = 1; i <= 3 && json == null; i++)
            {
                try { json = HttpGetString(url); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("  attempt " + i + ": " + Describe(ex));
                    if (i == 3) throw;
                    Wait(2000 * i);
                }
            }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var rels = ser.DeserializeObject(json) as object[];
            if (rels == null) throw new InvalidOperationException("unexpected response from the GitHub API");

            var require = new Regex(@"^ParchisiPayload.*\.zip$", RegexOptions.IgnoreCase);
            foreach (var r in rels)
            {
                var rd = r as Dictionary<string, object>;
                if (rd == null) continue;
                object draft; if (rd.TryGetValue("draft", out draft) && draft is bool && (bool)draft) continue;
                object av; if (!rd.TryGetValue("assets", out av)) continue;
                var assets = av as object[]; if (assets == null) continue;

                foreach (var x in assets)
                {
                    var ad = x as Dictionary<string, object>; if (ad == null) continue;
                    var name = Str(ad, "name");
                    if (!require.IsMatch(name)) continue;

                    var digest = Str(ad, "digest");               // "sha256:<hex>" when GitHub provides it
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = digest.Substring(7);
                    else digest = Cfg.PayloadSha256;              // configured pin as fallback
                    long size = -1; long.TryParse(Str(ad, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    var a = new Asset
                    {
                        Name = name, Url = Str(ad, "browser_download_url"), Sha256 = digest, Size = size,
                        Source = "github.com/" + Cfg.PayloadRepo + " release " + Str(rd, "tag_name"),
                        ReleaseUrl = Str(rd, "html_url"), ReleaseTag = Str(rd, "tag_name")
                    };
                    Log.Info("selected " + a.Name + " (" + Size(a.Size) + ") from " + a.Source +
                             (a.Sha256.Length > 0 ? ", sha256 published" : ", no sha256 published"));
                    return a;
                }
            }
            throw new InvalidOperationException("no ParchisiPayload zip in the latest releases of " + Cfg.PayloadRepo);
        }

        static bool ExtractPayload(string zip)
        {
            try
            {
                // ExtractToDirectory refuses an existing directory: a second run threw,
                // the blanket catch swallowed it, and a corrupt cached tree never healed.
                if (Directory.Exists(PayloadDir)) Directory.Delete(PayloadDir, true);
                ZipFile.ExtractToDirectory(zip, PayloadDir);
                if (!File.Exists(Path.Combine(PayloadDir, "adb.exe")) ||
                    !File.Exists(Path.Combine(PayloadDir, "base.apk")))
                    return Fail("the game files zip did not contain what it should (adb + APKs)");
                Log.Ok("game files extracted: " + PayloadDir);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Fail("cannot extract the game files: " + Describe(ex)); }
        }

        static bool InstallGame()
        {
            var missing = GameApks.Where(f => !File.Exists(Path.Combine(PayloadDir, f))).ToArray();
            if (missing.Length > 0)
                return Fail("game files missing from " + PayloadDir + ": " + string.Join(", ", missing) +
                            "; delete that folder and re-run");

            var paths = string.Join(" ", GameApks.Select(f => "\"" + Path.Combine(PayloadDir, f) + "\"").ToArray());
            Log.Info("installing " + GamePackage + " (adb install-multiple)");
            Tray.SetStatus("installing the game");
            Status.Show("Installing the game...");
            var o = Adb("install-multiple -r -g " + paths, Cfg.InstallTimeoutMin * 60 * 1000);
            Status.Close();
            if (o == null) return Fail("adb could not install the game (bridge dropped - see log)");
            var brief = o.Replace("\n", " ").Trim();
            if (brief.Length > 0) Log.Info("  " + brief.Substring(0, Math.Min(140, brief.Length)));

            var chk = AdbGamePmPath();
            if (chk == null) return Fail("the install finished but Android does not see the game: " + brief.Substring(0, Math.Min(120, brief.Length)));
            if (chk.IndexOf("split_config.etc1_rgb8_group_hd", StringComparison.Ordinal) < 0)
                return Fail("installed WITHOUT the PNG texture split - it would render black (see README); re-run");
            Log.Ok("game installed with the PNG texture split");
            RememberInstalled();
            return true;
        }

        // Firefox is required for FACEBOOK LOGIN (WSA's WebView UA is rejected), but the
        // game itself runs without it - so every failure here is logged, never fatal.
        static void BestEffortFirefox()
        {
            try
            {
                var p = Adb("shell pm path " + FirefoxPackage, 10000);
                var have = p != null && p.IndexOf("package:", StringComparison.Ordinal) >= 0;
                if (!have)
                {
                    var missing = FirefoxApks.Where(f => !File.Exists(Path.Combine(PayloadDir, f))).ToArray();
                    if (missing.Length > 0)
                    {
                        Log.Warn("Firefox APKs missing (" + string.Join(", ", missing) +
                                 ") - Facebook login will fail in the built-in WebView");
                        return;
                    }
                    var paths = string.Join(" ", FirefoxApks.Select(f => "\"" + Path.Combine(PayloadDir, f) + "\"").ToArray());
                    Log.Info("installing Firefox (best-effort, for Facebook login)");
                    var o = Adb("install-multiple -r -g " + paths, 15 * 60 * 1000);
                    if (o == null) Log.Warn("Firefox install failed (adb) - Facebook login will fail in the built-in WebView");
                    else Log.Ok("Firefox installed");
                }

                // The game opens login links through the Android browser role; without
                // the role the link lands in the rejected WebView again.
                var d = Adb("shell dumpsys role", 40000);
                if (d == null) { Log.Warn("  browser role not checked (adb)"); return; }
                var i = d.IndexOf("android.app.role.BROWSER", StringComparison.Ordinal);
                var tail = i >= 0 ? d.Substring(i, Math.Min(400, d.Length - i)) : "";
                if (tail.IndexOf(FirefoxPackage, StringComparison.Ordinal) >= 0) { Log.Info("  default browser: Firefox"); return; }
                Log.Info("setting Firefox as the default browser...");
                var r = Adb("shell cmd role add-role-holder android.app.role.BROWSER " + FirefoxPackage, 30000);
                if (r != null) Log.Ok("  browser role -> Firefox");
                else Log.Warn("  browser role not set (best-effort)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Firefox best-effort step failed: " + ex.Message); }
        }

        // ------------------------------------------------------- 11. launch
        static bool LaunchGame()
        {
            // (The ASTC fix of the GPGP launcher does not apply here: this game's
            // texture fix is the PNG split, which is verified at install time.)

            Log.Info("launching wsa://" + GamePackage);
            Status.Show("Launching the game...");
            if (!Open("wsa://" + GamePackage)) { Status.Close(); return Fail("Windows could not start the game; re-run the launcher"); }
            Log.Info("launch submitted; waiting for the game window");

            var sw = Stopwatch.StartNew();
            var seen = false;
            while (sw.ElapsedMilliseconds < LaunchConfirmMs && !Cancelled)
            {
                Wait(2000);
                if (GameWindowPresent()) { seen = true; break; }
            }
            if (seen) Log.Ok("game window visible after " + (sw.ElapsedMilliseconds / 1000) + "s");
            // The launch was submitted; a title mismatch must not strand a working install.
            else Log.Warn("could not confirm a window titled '*" + Cfg.GameWindowTitle + "*' within " +
                          (LaunchConfirmMs / 1000) + "s (set GameWindowTitle if the title differs)");
            Status.Close();        // the bubble retires either way
            if (!seen) return true;     // nothing to follow

            // This launcher is a wrapper, not a resident program: it stays around
            // only while the game runs and leaves with it. Three consecutive
            // misses (~12s) rather than one, because a single UIA scan can miss
            // a window that is really there (a resize, a fullscreen toggle, a
            // momentary animation).
            Tray.SetStatus("playing - closes with the game");
            var miss = 0;
            while (true)                            // Wait() throws if the user picks Exit
            {
                Wait(3000);
                if (GameWindowPresent()) { miss = 0; continue; }
                if (++miss >= 3) { Log.Ok("game closed - exiting with the game"); break; }
            }
            return true;
        }

        // ------------------------------------------------------ fail window
        static void ShowFailureWindow(string check, string cause, string hint)
        {
            // This is the funnel every failure path comes through (and it blocks in
            // Application.Run below) - nothing may outlive it, above all the TopMost
            // "Launching..." bubble, which would then sit over the failure window.
            Notice.Close();
            Status.Close();
            try
            {
                var form = new Form
                {
                    Text = "Parchisi STAR - setup problem",
                    Size = new System.Drawing.Size(900, 600),
                    StartPosition = FormStartPosition.CenterScreen
                };
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                grid.Controls.Add(new Label
                {
                    AutoSize = true,
                    MaximumSize = new System.Drawing.Size(860, 0),
                    Padding = new Padding(10),
                    Font = new System.Drawing.Font("Segoe UI", 10),
                    Text = "A check failed: " + check + "\r\nCause: " + cause +
                           (string.IsNullOrEmpty(hint) ? "" : "\r\nSuggested fix: " + hint) +
                           "\r\nFull log: " + (Log.FilePath ?? "(not written)")
                }, 0, 0);

                var tb = new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
                    ScrollBars = ScrollBars.Both, Font = new System.Drawing.Font("Consolas", 9),
                    Text = string.Join(Environment.NewLine, Log.Snapshot())
                };
                grid.Controls.Add(tb, 0, 1);

                var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var close = new Button { Text = "Close", AutoSize = true };
                close.Click += (s, e) => form.Close();
                var copy = new Button { Text = "Copy log", AutoSize = true };
                copy.Click += (s, e) => { try { Clipboard.SetText(tb.Text); } catch { } };
                var open = new Button { Text = "Open log", AutoSize = true, Enabled = Log.FilePath != null };
                open.Click += (s, e) => Open(Log.FilePath);
                bar.Controls.Add(close); bar.Controls.Add(copy); bar.Controls.Add(open);
                grid.Controls.Add(bar, 0, 2);

                form.Controls.Add(grid);
                form.Shown += (s, e) => { tb.SelectionStart = tb.TextLength; tb.ScrollToCaret(); };
                Application.Run(form);
            }
            catch { /* windowless is still better than crashing */ }
        }
    }
}