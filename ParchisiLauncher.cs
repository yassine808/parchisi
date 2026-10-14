using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace ParchisiLauncher
{
    /// <summary>
    /// One double-click does everything:
    ///   provision (UAC, maybe one reboot) -> boot WSA -> install the game +
    ///   Firefox -> set the default browser -> launch the game -> hold focus.
    ///
    /// On an already-provisioned PC the provisioning phase is a pure file-exists
    /// check, so there is no UAC prompt and the launcher stays fully windowless.
    /// </summary>
    public class Launcher : Form
    {
        private const string Pkg = "com.superking.parchisi.star";
        private const string Activity = "com.superking.parchisi.star/org.cocos2dx.cpp.AppActivity";
        private const string AdbHost = "127.0.0.1";
        private const int AdbPort = 58526;

        private const string WsaInstallDir = @"C:\WSA";
        private const string WsaManifest = @"C:\WSA\AppxManifest.xml";
        private const string WsaPkg = "MicrosoftCorporationII.WindowsSubsystemForAndroid";

        // Provision outcomes.
        private const int ProvOk = 0;
        private const int ProvReboot = 1;
        private const int ProvFail = 2;

        // The game must ship the PNG texture split. The .astc split renders black
        // on WSA, so an install without it is treated as a wrong install.
        private static readonly string[] GameSplits = new string[]
        {
            "base.apk",
            "split_config.arm64_v8a.apk",
            "split_config.etc1_rgb8_group_hd.apk"
        };

        private static readonly string[] FirefoxSplits = new string[]
        {
            "firefox_base.apk",
            "firefox_split_config.mdpi.apk",
            "firefox_split_config.x86_64.apk"
        };

        private readonly TextBox logBox;
        private readonly Button launchBtn;
        private readonly bool doProvision;
        private volatile bool rebootPending;

        public Launcher(bool provision)
        {
            doProvision = provision;

            Text = "Parchisi STAR";
            ClientSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9F);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.WordWrap = false;
            logBox.Location = new Point(12, 12);
            logBox.Size = new Size(536, 318);
            logBox.Font = new Font("Consolas", 9F);
            Controls.Add(logBox);

            launchBtn = new Button();
            launchBtn.Text = "Launch Parchisi STAR";
            launchBtn.Location = new Point(12, 340);
            launchBtn.Size = new Size(536, 46);
            launchBtn.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            launchBtn.Click += delegate { RunAsync(); };
            Controls.Add(launchBtn);

            // Clicking the window during the 60s reboot countdown cancels it, so a
            // first run can never yank the PC out from under the user unnoticed.
            Click += delegate { CancelRebootIfAny(); };

            // Deliberately no Shown handler: the launcher runs windowless. The UI is
            // only revealed by ShowUi() for provisioning progress or on failure.
        }

        private void CancelRebootIfAny()
        {
            if (!rebootPending) return;
            rebootPending = false;
            Log("Reboot cancelled. Run PlayParchisi.exe again when you are ready.");
            Log("After enabling VirtualMachinePlatform a reboot IS still required.");
            Ps("shutdown /a", 20000);
        }

        private string AdbPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"); }
        }

        private static string PayloadDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload"); }
        }

        private static readonly string LogFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");

        private void Log(string msg)
        {
            // Persist exactly once. Do NOT funnel the UI dispatch back through Log(),
            // or BeginInvoke re-enters here and the line gets written to disk twice.
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine); }
            catch { }
            AppendUi(msg);
        }

        private void AppendUi(string msg)
        {
            if (logBox.IsDisposed) return;
            if (logBox.InvokeRequired)
            {
                try { logBox.BeginInvoke(new Action<string>(AppendUi), msg); } catch { }
                return;
            }
            logBox.AppendText(msg + Environment.NewLine);
        }

        // ---------------------------------------------------------------------
        // Provisioning (runs only when WSA is absent; requires elevation)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Cheap, shell-free check that runs on every launch. WSA is provisioned
        /// exactly when both its manifest and its client exist. Never call
        /// Get-WindowsOptionalFeature here: it throws without elevation.
        /// </summary>
        private static bool NeedsProvisioning()
        {
            try
            {
                if (!File.Exists(WsaManifest)) return true;
                if (!File.Exists(WsaClientPath)) return true;
            }
            catch { return true; }
            return false;
        }

        private static int OsBuild()
        {
            // Environment.OSVersion lies (6.2) for csc-built exes with no manifest,
            // so read the registry instead.
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("CurrentBuild");
                        int b;
                        if (v != null && int.TryParse(v.ToString(), out b)) return b;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Runs a PowerShell snippet and returns combined output.</summary>
        private string Ps(string script, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                // Single quotes inside the script avoid escaping the outer double quotes.
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "TIMEOUT";
                    }
                    string o = (so + se).Trim();
                    return o.Length == 0 ? "(ok)" : o;
                }
            }
            catch (Exception ex)
            {
                return "ERR: " + ex.Message;
            }
        }

        private bool IsWsaRegistered()
        {
            string r = Ps("if (Get-AppxPackage " + WsaPkg + ") { 'yes' } else { 'no' }", 90000);
            return r.IndexOf("yes", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private int ProvisionNow()
        {
            Log("== provisioning (administrator) ==");
            try
            {
                int build = OsBuild();
                Log("Windows build: " + build);
                if (build > 0 && build < 22000)
                {
                    Log("FATAL: WSA needs Windows 11 build 22000 or newer.");
                    return ProvFail;
                }

                // --- VirtualMachinePlatform: the one step that needs a reboot ----
                string state = Ps("(Get-WindowsOptionalFeature -Online -FeatureName 'VirtualMachinePlatform').State", 120000);
                Log("VirtualMachinePlatform: " + state.Trim());
                if (state.IndexOf("Enabled", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Log("Enabling VirtualMachinePlatform...");
                    string en = Ps("Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -NoRestart | Out-Null; 'enabled'", 300000);
                    if (en.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Log("FATAL: could not enable VirtualMachinePlatform:");
                        Log("  " + en);
                        return ProvFail;
                    }
                    Log("VirtualMachinePlatform enabled.");
                    return ScheduleResume();
                }

                // --- stage the WSA payload ---------------------------------------
                if (!StageWsa()) return ProvFail;

                // --- register the package ----------------------------------------
                if (IsWsaRegistered())
                {
                    Log("WSA package already registered.");
                }
                else
                {
                    Log("Registering WSA package...");
                    string r = Ps("Add-AppxPackage -DisableDevelopmentMode -Register '" + WsaManifest + "'", 300000);
                    Log(r.Length > 400 ? r.Substring(0, 400) : r);
                    if (!IsWsaRegistered())
                    {
                        Log("FATAL: WSA package did not register.");
                        return ProvFail;
                    }
                    Log("WSA registered.");
                }

                if (!File.Exists(WsaClientPath))
                {
                    // Usually Avast: it quarantines anything that launches WsaClient.exe.
                    Log("WARNING: WsaClient.exe is missing although the package is registered.");
                    Log("  This is almost always Avast quarantining it (IDP.Generic).");
                    Log("  Add C:\\WSA and the launcher folder to Avast exclusions, then restore the file.");
                }

                Log("Provisioning complete.");
                return ProvOk;
            }
            catch (Exception ex)
            {
                Log("FATAL: provisioning: " + ex.Message);
                return ProvFail;
            }
        }

        /// <summary>Extracts payload\wsa.zip -> C:\WSA if the install dir is absent.</summary>
        private bool StageWsa()
        {
            if (File.Exists(WsaManifest))
            {
                Log("C:\\WSA already staged.");
                return true;
            }

            string zip = Path.Combine(PayloadDir, "wsa.zip");
            if (!File.Exists(zip))
            {
                Log("FATAL: C:\\WSA is missing and payload\\wsa.zip was not found.");
                Log("  Place either a ready C:\\WSA folder or payload\\wsa.zip beside the launcher.");
                return false;
            }

            Log("Extracting wsa.zip -> C:\\WSA (about 956 MB, takes a few minutes)...");
            try
            {
                Directory.CreateDirectory(WsaInstallDir);
                int total = 0, n = 0;
                using (ZipArchive za = ZipFile.OpenRead(zip))
                {
                    total = za.Entries.Count;
                    foreach (ZipArchiveEntry e in za.Entries)
                    {
                        string dest = Path.Combine(WsaInstallDir,
                            e.FullName.Replace('/', Path.DirectorySeparatorChar));
                        if (string.IsNullOrEmpty(e.Name))
                        {
                            Directory.CreateDirectory(dest);
                        }
                        else
                        {
                            string dir = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            e.ExtractToFile(dest, true);
                        }
                        n++;
                        if (n % 50 == 0) Log("  " + n + "/" + total + " files");
                    }
                }
                Log("  " + n + "/" + total + " files extracted.");
            }
            catch (Exception ex)
            {
                Log("FATAL: extraction failed: " + ex.Message);
                return false;
            }

            if (!File.Exists(WsaManifest))
            {
                Log("FATAL: AppxManifest.xml still missing after extraction.");
                return false;
            }
            Log("C:\\WSA staged.");
            return true;
        }

        /// <summary>
        /// VirtualMachinePlatform needs one reboot. Write HKLM RunOnce (runs elevated
        /// at next logon) so provisioning resumes by itself, then restart.
        /// </summary>
        private int ScheduleResume()
        {
            string cmd = "\"" + Application.ExecutablePath + "\" --provision";
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"))
                {
                    if (k == null) throw new InvalidOperationException("RunOnce key unavailable");
                    k.SetValue("ParchisiProvision", cmd);
                }
            }
            catch (Exception ex)
            {
                Log("FATAL: could not schedule the resume: " + ex.Message);
                Log("Reboot the PC yourself, then run PlayParchisi.exe again.");
                return ProvFail;
            }

            rebootPending = true;
            Log("");
            Log("VirtualMachinePlatform is enabled. Windows restarts in 60 seconds,");
            Log("then PlayParchisi.exe resumes on its own and finishes the install.");
            Log("");
            Log(">>> CLICK THIS WINDOW to cancel the reboot. <<<");
            Log("");
            Ps("shutdown /r /t 60", 20000);
            return ProvReboot;
        }

        // ---------------------------------------------------------------------
        // App installation (no elevation needed; runs over adb)
        // ---------------------------------------------------------------------

        private bool IsInstalled(string pkg)
        {
            string p = Adb("shell pm path " + pkg, 30000);
            return p.IndexOf("package:", StringComparison.Ordinal) >= 0;
        }

        private bool InstallApks(string label, string[] names)
        {
            string dir = PayloadDir;
            string args = "install-multiple -r -g";
            for (int i = 0; i < names.Length; i++)
            {
                string p = Path.Combine(dir, names[i]);
                if (!File.Exists(p))
                {
                    Log("FATAL: payload file missing: " + p);
                    return false;
                }
                args += " \"" + p + "\"";
            }

            Log("Installing " + label + " (" + names.Length + " splits)...");
            string res = Adb(args, 900000);   // 253 MB + 276 MB over the local bridge
            Log("  " + (res.Length > 500 ? res.Substring(0, 500) : res));
            if (res.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Log("FAILED: " + label + " install did not report Success.");
                return false;
            }
            return true;
        }

        private void EnsureGameInstalled()
        {
            if (IsInstalled(Pkg))
            {
                string paths = Adb("shell pm path " + Pkg, 30000);
                if (paths.IndexOf("etc1_rgb8_group_hd", StringComparison.Ordinal) >= 0)
                {
                    Log("Game installed, PNG texture split present.");
                    return;
                }
                // The .astc split renders black on WSA - reinstall the right ones.
                Log("Game installed but WRONG texture split (etc1_rgb8 missing) -> would render black.");
                Log("Reinstalling the PNG-texture splits...");
            }
            else
            {
                Log("Game not installed.");
            }

            if (InstallApks("game", GameSplits))
            {
                string chk = Adb("shell pm path " + Pkg, 30000);
                Log(chk.IndexOf("etc1_rgb8_group_hd", StringComparison.Ordinal) >= 0
                    ? "  verified: PNG texture split present."
                    : "  WARNING: could not verify the texture split after install.");
            }
            else
            {
                Log("  Game install failed. See the message above.");
            }
        }

        private void EnsureFirefoxInstalled()
        {
            // Firefox is not optional: the game's webview is 118.0.5993.111 and
            // Facebook rejects that UA, so login only works in a current browser.
            if (IsInstalled("org.mozilla.firefox"))
            {
                Log("Firefox installed.");
                return;
            }
            Log("Firefox missing - required for Facebook login.");
            InstallApks("Firefox", FirefoxSplits);
        }

        private void EnsureBrowserRole()
        {
            string d = Adb("shell dumpsys role", 40000);
            int i = d.IndexOf("android.app.role.BROWSER", StringComparison.Ordinal);
            if (i >= 0)
            {
                string tail = d.Substring(i, Math.Min(500, d.Length - i));
                if (tail.IndexOf("org.mozilla.firefox", StringComparison.Ordinal) >= 0)
                {
                    Log("Default browser: Firefox.");
                    return;
                }
            }
            Log("Setting Firefox as the default browser...");
            string r = Adb("shell cmd role add-role-holder android.app.role.BROWSER org.mozilla.firefox", 30000);
            Log("  " + r.Trim());
        }

        // ---------------------------------------------------------------------
        // Boot / launch
        // ---------------------------------------------------------------------

        /// <summary>Boots WSA by invoking WsaClient, then polls the adb port.</summary>
        /// <returns>true if the bridge came up.</returns>
        private bool EnsureWsa()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (PortOpen()) return true;

                Log("WSA bridge down -> cold boot attempt " + attempt + "/3");
                string wsa = WsaClientPath;
                if (!File.Exists(wsa))
                {
                    Log("FATAL: WsaClient.exe not found:");
                    Log("  " + wsa);
                    Log("  (Avast quarantines it as IDP.Generic - restore + add exclusions.)");
                    return false;
                }

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(wsa, "/launch wsa://" + Pkg);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    Log("Boot requested.");
                }
                catch (Exception ex)
                {
                    Log("Could not start WsaClient: " + ex.Message);
                }

                for (int i = 0; i < 120; i++)
                {
                    Thread.Sleep(1000);
                    if (PortOpen())
                    {
                        Log("WSA up after " + i + "s.");
                        return true;
                    }
                }

                Log("Attempt " + attempt + " timed out after 120s. Retrying...");
            }
            return false;
        }

        private string Adb(string args, int timeoutMs)
        {
            try
            {
                if (!File.Exists(AdbPath)) return "ERR: adb.exe not found next to launcher";

                ProcessStartInfo psi = new ProcessStartInfo(AdbPath, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "TIMEOUT";
                    }
                    string outp = (stdout + stderr).Trim();
                    return outp.Length == 0 ? "(ok)" : outp;
                }
            }
            catch (Exception ex)
            {
                return "ERR: " + ex.Message;
            }
        }

        private bool PortOpen()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult r = c.BeginConnect(AdbHost, AdbPort, null, null);
                    bool ok = r.AsyncWaitHandle.WaitOne(800, false);
                    if (!ok || !c.Connected) return false;
                    try { c.EndConnect(r); } catch { return true; }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string WsaClientPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WindowsApps\MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe\WsaClient.exe");
            }
        }

        private void RunAsync()
        {
            launchBtn.Enabled = false;
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Start();
        }

        private void Run()
        {
            // false until the game actually holds focus. Every early "return" below
            // leaves it false, which makes the finally block reveal the log window.
            bool ok = false;
            try
            {
                Log("== Parchisi STAR launcher ==");
                Log("");

                // 0. Provision WSA first when needed (elevated --provision mode only).
                if (doProvision)
                {
                    ShowUi();               // first run: let the user watch the progress
                    int st = ProvisionNow();
                    if (st != ProvOk)
                    {
                        // Reboot scheduled, or failed. Leave the window open either way
                        // so the outcome is visible instead of failing invisibly.
                        Log("");
                        Log(st == ProvReboot ? "Paused: resuming after the reboot." : "Provisioning failed.");
                        return;
                    }
                    Log("");
                }

                // 1. Make sure the adb bridge is up (cold boots WSA if needed, retries).
                if (PortOpen())
                {
                    Log("WSA already running.");
                }
                else if (!EnsureWsa())
                {
                    Log("");
                    Log("FATAL: WSA never started listening on " + AdbPort + ".");
                    Log("Check that Developer mode is ON inside WSA settings.");
                    Log("If it reads On but the port is dead: toggle it Off -> On.");
                    return;
                }

                // 2. adb handshake.
                Log("adb start-server: " + Adb("start-server", 20000));
                Log("adb connect:      " + Adb("connect " + AdbHost + ":" + AdbPort, 20000));

                string dev = Adb("devices", 15000);
                if (dev.IndexOf("device", StringComparison.Ordinal) < 0)
                {
                    Log("");
                    Log("FATAL: adb did not get an authorized device:");
                    Log(dev);
                    return;
                }

                // 3. Keep the display awake so screenshots/backgrounding behave.
                Adb("shell svc power stayon true", 15000);

                // 4. The adb port starts listening before Android's system services are
                //    registered. am start fails with "Can't find service: activity" if we
                //    fire the instant the port opens, so probe for the service first.
                Log("Waiting for Android framework...");
                bool ready = false;
                for (int i = 0; i < 45; i++)
                {
                    string svc = Adb("shell service check activity", 10000);
                    if (svc.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0
                        && svc.IndexOf("ERR", StringComparison.Ordinal) < 0
                        && svc.IndexOf("TIMEOUT", StringComparison.Ordinal) < 0)
                    {
                        ready = true;
                        Log("Framework ready after " + i + "s.");
                        break;
                    }
                    Thread.Sleep(1000);
                }
                if (!ready)
                {
                    Log("FATAL: Android activity service never registered.");
                    return;
                }

                // 5. Install anything missing (no-ops on a machine that is already set up).
                EnsureGameInstalled();
                EnsureFirefoxInstalled();
                EnsureBrowserRole();

                // 6. Drop the progress window so the launch itself is windowless.
                if (doProvision) HideUi();

                // 7. Launch the game activity (retries if the service flaps).
                Log("");
                Log("Launching " + Activity);
                string started = "";
                for (int i = 0; i < 5; i++)
                {
                    started = Adb("shell am start -n " + Activity, 40000);
                    if (started.IndexOf("Can't find service", StringComparison.OrdinalIgnoreCase) < 0
                        && started.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0)
                        break;
                    Log("  not ready, retrying (" + started.Trim() + ")");
                    Thread.Sleep(3000);
                }
                Log(started);

                // 8. Focus guard.
                //    The game auto-fires a Facebook login which backgrounds itself, and WSA
                //    reaps backgrounded apps within ~20s. Press BACK when focus is lost.
                //    Only engage AFTER the game first holds focus - otherwise we sit there
                //    spamming BACK at the WSA home screen while the app cold-starts.
                Log("");
                Log("Watching focus for 2 minutes (BACK only after the game first holds focus)...");
                bool hadFocus = false;
                int backStreak = 0;
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(3000);
                    string f = Adb("shell dumpsys window | grep mCurrentFocus", 10000);

                    if (f.IndexOf(Pkg, StringComparison.Ordinal) >= 0)
                    {
                        if (!hadFocus)
                        {
                            Log("[" + DateTime.Now.ToString("HH:mm:ss") + "] game has focus");
                            hadFocus = true;
                        }
                        backStreak = 0;
                    }
                    else if (hadFocus && backStreak < 3)
                    {
                        Log("[" + DateTime.Now.ToString("HH:mm:ss") + "] focus lost -> BACK   [" + f.Trim() + "]");
                        Adb("shell input keyevent 4", 10000);
                        backStreak++;
                    }
                }

                Log("");
                ok = hadFocus;
                Log(ok ? "== done: game running ==" : "== done: game never held focus ==");
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.Message);
                ok = false;
            }
            finally
            {
                // Reveal the log window ONLY on failure. On success the process just
                // exits silently - the user asked to see nothing but the game.
                if (ok)
                    ExitApp();
                else
                {
                    try { launchBtn.BeginInvoke(new Action(Reenable)); } catch { }
                    ShowUi();
                }
            }
        }

        /// <summary>Kicks off the launch with no visible window.</summary>
        public void StartSilent()
        {
            RunAsync();
        }

        /// <summary>Reveals the log window. Used only when the launch fails.</summary>
        private void ShowUi()
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired)
                {
                    // The form was never shown, so no window handle exists yet and
                    // BeginInvoke would throw. Force handle creation first.
                    IntPtr unused = Handle;
                    BeginInvoke(new Action(ShowUi));
                    return;
                }
                Opacity = 1;
                ShowInTaskbar = true;
                Show();
                BringToFront();
                Focus();
            }
            catch { }
        }

        /// <summary>Hides the progress window so the launch is windowless.</summary>
        private void HideUi()
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired)
                {
                    IntPtr unused = Handle;
                    BeginInvoke(new Action(HideUi));
                    return;
                }
                if (Visible) Hide();
            }
            catch { }
        }

        private void ExitApp()
        {
            try
            {
                Application.Exit();
            }
            catch { }
        }

        private void Reenable()
        {
            launchBtn.Enabled = true;
        }

        private static bool HasArg(string[] args, string want)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], want, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool provision = HasArg(args, "--provision");

            // Not yet provisioned -> relaunch elevated. The child does the UAC work
            // and the whole launch; this instance exits so no window is left behind.
            if (!provision && NeedsProvisioning())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--provision");
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                }
                catch (Win32Exception)
                {
                    // UAC declined - say so instead of silently doing nothing.
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Launcher f = new Launcher(false);
                    f.ShowUiWith("Provisioning needs administrator approval (UAC).",
                        "Run PlayParchisi.exe again and click Yes on the",
                        "Windows prompt to continue installing.");
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Launcher main = new Launcher(provision);
            main.StartSilent();

            // Message loop with no form shown. The process ends when the launch
            // finishes; if it fails, ShowUi() reveals the log window instead.
            Application.Run();
        }

        /// <summary>Shows the log window pre-filled with an explanatory message.</summary>
        public void ShowUiWith(params string[] lines)
        {
            for (int i = 0; i < lines.Length; i++) AppendUi(lines[i]);
            ShowUi();
        }
    }
}
