using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace ParchisiLauncher
{
    public class Launcher : Form
    {
        private const string Pkg = "com.superking.parchisi.star";
        private const string Activity = "com.superking.parchisi.star/org.cocos2dx.cpp.AppActivity";
        private const string AdbHost = "127.0.0.1";
        private const int AdbPort = 58526;

        private readonly TextBox logBox;
        private readonly Button launchBtn;

        public Launcher()
        {
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

            Shown += delegate { RunAsync(); };
        }

        private string AdbPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"); }
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

        private string WsaClientPath
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
            try
            {
                Log("== Parchisi STAR launcher ==");
                Log("");

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

                // 5. Launch the game activity (retries if the service flaps).
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

                // 6. Focus guard.
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
                Log(hadFocus ? "== done: game running ==" : "== done: game never held focus ==");
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.Message);
            }
            finally
            {
                try
                {
                    launchBtn.BeginInvoke(new Action(Reenable));
                }
                catch { }
            }
        }

        private void Reenable()
        {
            launchBtn.Enabled = true;
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Launcher());
        }
    }
}
