// =====================================================================================
//  AX210_WatchClaude — install hooks, single-instance launcher, first-run setup assistant
//  * Single: one copy per session; shortcuts signal the running (elevated) copy to show its window
//  * Setup:  scheduled-task registration for the installer (starts elevated at sign-in, no UAC prompt)
//  * DetectSetup + SetupForm: finds the adapter, home network, extenders and crash-prone channels,
//    and fills in the settings for this PC
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace AX210WatchClaude
{
    // ---------------------------------------------------------------- single instance + "show window" signal
    static class Single
    {
        public const string MutexName = "Local\\AX210_WatchClaude_SingleInstance";
        const string ShowName = "Local\\AX210_WatchClaude_Show", ExitName = "Local\\AX210_WatchClaude_Exit";
        static WaitHandle showEv, exitEv;

        public static bool IsRunning()
        {
            try { Mutex m; if (Mutex.TryOpenExisting(MutexName, out m)) { m.Dispose(); return true; } return false; }
            catch (UnauthorizedAccessException) { return true; }   // an elevated copy owns it
        }

        [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint rev, out IntPtr sd, IntPtr size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateEvent(ref SECURITY_ATTRIBUTES sa, bool manual, bool initial, string name);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        class NativeEvent : WaitHandle { public NativeEvent(IntPtr h) { SafeWaitHandle = new SafeWaitHandle(h, true); } }

        /// An auto-reset event the given SDDL allows to be signalled. The medium-integrity label lets a normal-rights
        /// launcher signal the elevated app (mandatory integrity control would otherwise block the write).
        static WaitHandle Create(string name, string sddl)
        {
            IntPtr sd;
            if (ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out sd, IntPtr.Zero))
                try
                {
                    var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), lpSecurityDescriptor = sd };
                    var h = CreateEvent(ref sa, false, false, name);
                    if (h != IntPtr.Zero) return new NativeEvent(h);
                }
                finally { LocalFree(sd); }
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }

        public static void Listen(Action show, Action exit)
        {
            // show: the signed-in user's normal-rights processes may signal it; exit: administrators only (the installer)
            showEv = Create(ShowName, "D:(A;;0x001F0003;;;BA)(A;;0x001F0003;;;SY)(A;;0x00100002;;;IU)S:(ML;;NW;;;ME)");
            exitEv = Create(ExitName, "D:(A;;0x001F0003;;;BA)(A;;0x001F0003;;;SY)");
            new Thread(() =>
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(new[] { showEv, exitEv });
                    if (i == 0) show(); else if (i == 1) { exit(); return; }
                }
            }) { IsBackground = true, Name = "signals" }.Start();
        }

        static bool Signal(string name)
        {
            try { EventWaitHandle e; if (EventWaitHandle.TryOpenExisting(name, System.Security.AccessControl.EventWaitHandleRights.Modify | System.Security.AccessControl.EventWaitHandleRights.Synchronize, out e)) using (e) { e.Set(); return true; } }
            catch { }
            return false;
        }
        public static void SignalShow() { Signal(ShowName); }
        public static void WaitAndSignalShow(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (Signal(ShowName)) return; Thread.Sleep(300); }
        }

        /// Installer hook: ask the running copy to exit cleanly (restores Wi-Fi Direct), then make sure it's gone.
        public static void StopRunning()
        {
            Signal(ExitName);
            int self = Process.GetCurrentProcess().Id; var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && Process.GetProcessesByName("AX210_WatchClaude").Any(p => p.Id != self)) Thread.Sleep(250);
            foreach (var p in Process.GetProcessesByName("AX210_WatchClaude").Where(p => p.Id != self)) try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
    }

    // ---------------------------------------------------------------- installer hooks
    static class Setup
    {
        public const string TaskName = "AX210_WatchClaude";

        static int Run(string exe, string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return -1; }
                    output = (so.Result + se.Result).Trim(); return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }
        static string Schtasks { get { return Path.Combine(Environment.SystemDirectory, "schtasks.exe"); } }

        /// Starts the app through its task: elevated, with no UAC prompt. False if there's no task.
        public static bool RunTask() { string o; return Run(Schtasks, "/run /tn \"" + TaskName + "\"", out o) == 0; }
        public static bool IsAutostart() { string o; return Run(Schtasks, "/query /tn \"" + TaskName + "\" /xml", out o) == 0 && o.Contains("<LogonTrigger"); }

        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WTSQuerySessionInformation(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        static string SessionString(uint s, int cls)
        {
            IntPtr b; uint n;
            if (!WTSQuerySessionInformation(IntPtr.Zero, s, cls, out b, out n)) return "";
            try { return Marshal.PtrToStringUni(b) ?? ""; } finally { WTSFreeMemory(b); }
        }
        /// The signed-in user the task should belong to (an elevated installer may run under another admin account).
        static string SignedInUser()
        {
            try
            {
                uint s = (uint)Process.GetCurrentProcess().SessionId;
                string u = SessionString(s, 5), d = SessionString(s, 7);
                if (u == "") { s = WTSGetActiveConsoleSessionId(); u = SessionString(s, 5); d = SessionString(s, 7); }
                if (u != "") return (d != "" ? d + "\\" : "") + u;
            }
            catch { }
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }

        /// Data folder: admins and SYSTEM can change it, everyone else can only read (the settings drive an elevated app).
        static void SecureDataFolder()
        {
            Paths.Ensure(); string o;
            Run("icacls.exe", "\"" + Paths.Data + "\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q", out o);
            Run("icacls.exe", "\"" + Path.Combine(Paths.Data, "*") + "\" /reset /T /C /Q", out o);
        }

        public static bool RegisterTask(bool autostart)
        {
            try
            {
                SecureDataFolder();
                string user = SecurityElement.Escape(SignedInUser()), exe = SecurityElement.Escape(Application.ExecutablePath);
                string triggers = !autostart ? "" :
                    "<LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT15S</Delay></LogonTrigger>" +
                    // self-heal: restarts the app within 5 minutes if it ever stops (Exit from the tray is respected until the next sign-in)
                    "<TimeTrigger><Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger>";
                string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                    "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                    "<RegistrationInfo><Author>AX210_WatchClaude</Author><Description>Starts AX210_WatchClaude (Wi-Fi watchdog) in the tray with the rights it needs, without a UAC prompt.</Description></RegistrationInfo>" +
                    "<Triggers>" + triggers + "</Triggers>" +
                    "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                    "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
                    "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>" +
                    "<RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority>" +
                    "<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>" +
                    "<Actions Context=\"Author\"><Exec><Command>\"" + exe + "\"</Command><Arguments>--tray</Arguments></Exec></Actions></Task>";
                var tmp = Path.Combine(Path.GetTempPath(), "AX210_WatchClaude_task_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                string o; int rc = Run(Schtasks, "/create /tn \"" + TaskName + "\" /xml \"" + tmp + "\" /f", out o);
                try { File.Delete(tmp); } catch { }
                Log.Write("START", rc == 0 ? "Scheduled task registered for " + SignedInUser() + (autostart ? " (starts with Windows)" : " (starts on demand only)") : "Couldn't register the scheduled task: " + o);
                return rc == 0;
            }
            catch (Exception ex) { Log.Write("ERROR", "register task: " + ex.Message); return false; }
        }
        public static void UnregisterTask() { string o; Run(Schtasks, "/delete /tn \"" + TaskName + "\" /f", out o); }

        /// Uninstall, "restore my settings": adapter settings + DNS back to how they were before the app, rules removed.
        public static void RestoreOriginal()
        {
            try
            {
                var wd = new Watchdog(); wd.Init();
                var r = wd.RestoreOriginal();
                wd.Cfg.AutoActions = true; wd.Cfg.Save();   // a later reinstall that keeps the data starts in normal mode
                Log.Write("ACTION", "Uninstall: " + r.Replace("\r\n", " "));
            }
            catch (Exception ex) { Log.Write("ERROR", "restore on uninstall: " + ex.Message); }
            Cleanup();
        }
        /// Uninstall, always: game-priority / upload-limit rules removed, Wi-Fi Direct back on.
        public static void Cleanup()
        {
            try { var wd = new Watchdog(); wd.Init(); wd.RestoreWifiDirect("uninstall"); wd.RestoreRandomization("uninstall"); } catch { }
            string o; Ps.Run("Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'AX210_WatchClaude*' } | Remove-NetQosPolicy -Confirm:$false", 60, out o);
        }
    }

    // ---------------------------------------------------------------- auto-detection
    public class SetupPlan
    {
        public string AdapterDesc = "", Driver = "", Ssid = "", Where = "", Nat = "", NatShort = "";
        public bool Intel, CanLock, Has6, Connected;
        public class Ap { public string Bssid, Info; public bool Suggested; }
        public List<Ap> Extenders = new List<Ap>();
        public class Ch { public int Channel, Crashes, Assoc; public bool Suggested; }
        public List<Ch> Channels = new List<Ch>();
        public int TotalCrashes, GameFolders, Routers;
        public List<string> BandOrder = new List<string>();
    }

    partial class Watchdog
    {
        [DllImport("iphlpapi.dll")] static extern int SendARP(uint dest, uint src, byte[] mac, ref int len);
        static string MacOf(string ip)
        {
            try { var mac = new byte[6]; int len = 6; if (SendARP(BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0), 0, mac, ref len) != 0) return null; return string.Join(":", mac.Take(len).Select(b => b.ToString("x2"))); }
            catch { return null; }
        }
        /// Vendor prefix with the "locally administered" bit cleared: a router's radios share it, a different-brand extender doesn't.
        static string Oui(string mac) { try { var p = mac.Split(':'); return (Convert.ToByte(p[0], 16) & 0xFD).ToString("x2") + ":" + p[1] + ":" + p[2]; } catch { return ""; } }

        /// First run: applies what DetectSetup finds without asking, but only what it's sure of: extenders identified
        /// against the gateway's MAC and channels that crashed repeatedly here. Anything uncertain is left for the
        /// setup assistant, which shows the same findings for a manual pass.
        public string AutoSetup()
        {
            var p = DetectSetup();
            var n = Json.S().Deserialize<Config>(Json.S().Serialize(Cfg));
            if (p.Ssid != "") n.HomeSsid = p.Ssid;
            var ext = p.Extenders.Where(a => a.Suggested).Select(a => a.Bssid).Except(n.AvoidBssids, StringComparer.OrdinalIgnoreCase).ToList();
            n.AvoidBssids = n.AvoidBssids.Concat(ext).ToList();
            var ch = p.Channels.Where(c => c.Suggested).Select(c => c.Channel).Except(n.AvoidChannels5).ToList();
            n.AvoidChannels5 = n.AvoidChannels5.Concat(ch).OrderBy(x => x).ToList();
            if (p.CanLock) { var order = new List<string>(p.BandOrder); foreach (var b in new[] { "5", "6", "2.4" }) if (!order.Contains(b)) order.Add(b); n.BandOrder = order; }
            ApplySettings(n);
            lock (StLock) St.SetupDone = true; Save();
            var parts = new List<string> { "Home network: " + (n.HomeSsid == "" ? "learned when you connect" : n.HomeSsid) };
            if (n.AvoidBssids.Count > 0) parts.Add(n.AvoidBssids.Count + " extender address" + (n.AvoidBssids.Count == 1 ? "" : "es") + " avoided");
            if (n.AvoidChannels5.Count > 0) parts.Add("5 GHz channel" + (n.AvoidChannels5.Count == 1 ? " " : "s ") + string.Join(", ", n.AvoidChannels5) + " avoided");
            int unsure = p.Extenders.Count(a => !a.Suggested); if (unsure > 0) parts.Add(unsure + " possible extender" + (unsure == 1 ? "" : "s") + " to check in the setup assistant");
            if (!p.CanLock) parts.Add("band control isn't available on this adapter");
            var summary = string.Join(" · ", parts);
            Log.Write("INFO", "Set up automatically: " + summary);
            return summary;
        }

        public SetupPlan DetectSetup()
        {
            var p = new SetupPlan { AdapterDesc = Wlan.Description, Driver = DriverVersion, Intel = Adapter.IsIntel, CanLock = Adapter.CanLockBands, Has6 = Adapter.Supports6 };
            var c = Wlan.GetConnection(); p.Connected = c.Connected;
            p.Ssid = c.Connected && !string.IsNullOrEmpty(c.Ssid) ? c.Ssid : Cfg.HomeSsid ?? "";
            p.Where = c.Connected ? Describe(c) : "not connected";
            if (!InGame) { Wlan.Scan(); Thread.Sleep(4000); }
            var all = Wlan.GetBssList();
            var home = all.Where(b => b.Ssid == p.Ssid && p.Ssid != "").ToList();
            // the scan knows each BSSID's real band (from its frequency): record it before reading the crash history,
            // whose events only carry channel numbers that 6 GHz shares with 2.4/5 GHz
            lock (StLock) foreach (var b in home.Where(x => x.Band != "?")) St.KnownBands[b.Bssid] = b.Band;

            // Which vendor prefix is the router? Only the gateway's own MAC says so for sure. Without that, the brand with
            // the most radios is the likely router, but nothing is pre-ticked: the PC might be sitting on the extender.
            var groups = home.Where(b => !IsAvoidBssid(b.Bssid)).GroupBy(b => Oui(b.Bssid)).ToDictionary(g => g.Key, g => g.ToList());
            string gw = Adapter.Gateway(), gwMac = gw == null ? null : MacOf(gw), router = null; bool sure = false;
            if (gwMac != null && groups.ContainsKey(Oui(gwMac))) { router = Oui(gwMac); sure = true; }
            else if (groups.Count == 1) router = groups.Keys.First();
            else if (groups.Count > 1) router = groups.OrderByDescending(g => g.Value.Count).ThenByDescending(g => g.Value.Max(b => b.Rssi)).First().Key;
            p.Routers = router != null && groups.ContainsKey(router) ? groups[router].Count : 0;
            foreach (var b in home.Where(b => router != null && Oui(b.Bssid) != router && !IsAvoidBssid(b.Bssid)).OrderBy(b => b.Bssid))
                p.Extenders.Add(new SetupPlan.Ap { Bssid = b.Bssid, Info = b.Band + " GHz · channel " + b.Channel + " · " + b.Rssi + " dBm" + (sure ? "" : " · tick it if this isn't your router"), Suggested = sure });
            foreach (var b in Cfg.AvoidBssids.Where(x => !p.Extenders.Any(e => e.Bssid == x)))
                p.Extenders.Add(new SetupPlan.Ap { Bssid = b, Info = "already on your avoid list" + (home.Any(h => h.Bssid == b) ? "" : " (not visible right now)"), Suggested = true });

            // crash history from Windows' event log (last 180 days), plus what the app has seen itself
            var stats = new Dictionary<int, int[]>();
            try
            {
                string summary; Dictionary<string, string> known; lock (StLock) known = new Dictionary<string, string>(St.KnownBands);
                foreach (var kv in EventMonitor.ImportHistory(new[] { p.Ssid }, known, out summary))
                {
                    p.TotalCrashes += kv.Value[1]; var k = kv.Key.Split('|'); int ch;
                    if (k[0] == "5" && int.TryParse(k[1], out ch)) stats[ch] = new[] { kv.Value[1], kv.Value[0] };
                }
            }
            catch { }
            lock (StLock)
                foreach (var kv in St.Channels.Where(x => x.Key.StartsWith("5|")))
                {
                    int ch; if (!int.TryParse(kv.Key.Substring(2), out ch)) continue;
                    int[] v; if (!stats.TryGetValue(ch, out v)) stats[ch] = v = new int[2];
                    v[0] = Math.Max(v[0], kv.Value.HistCrashes) + kv.Value.Crashes; v[1] = Math.Max(v[1], kv.Value.HistAssociations);
                }
            foreach (var kv in stats.Where(x => x.Value[0] > 0).OrderByDescending(x => x.Value[0]))
            {
                int cr = kv.Value[0], assoc = Math.Max(1, kv.Value[1]);
                bool bad = cr >= 10 && ((double)cr / assoc >= 0.5 || cr >= 0.25 * Math.Max(1, p.TotalCrashes));
                p.Channels.Add(new SetupPlan.Ch { Channel = kv.Key, Crashes = cr, Assoc = kv.Value[1], Suggested = bad || Cfg.AvoidChannels5.Contains(kv.Key) });
            }
            foreach (var ch in Cfg.AvoidChannels5.Where(x => !p.Channels.Any(y => y.Channel == x)))
                p.Channels.Add(new SetupPlan.Ch { Channel = ch, Suggested = true });

            p.BandOrder = p.Has6 ? new List<string> { "5", "6", "2.4" } : new List<string> { "5", "2.4" };
            p.GameFolders = Detector.RootCount;
            try
            {
                string server; var loc = Upnp.Location(out server);
                if (loc == null) { p.NatShort = "UPnP off"; p.Nat = "The router didn't answer UPnP. Turning UPnP on in the router helps peer-to-peer games get an open NAT."; }
                else
                {
                    var ip = Upnp.ExternalIp(loc); bool dbl = !string.IsNullOrEmpty(ip) && Upnp.IsPrivate(ip);
                    p.NatShort = dbl ? "Double NAT" : "Open · UPnP on";
                    p.Nat = dbl ? "UPnP is on, but the router itself is behind another NAT (double NAT / carrier NAT). Some games may report a strict NAT." : "UPnP is on and the router has a public address, so games can open the ports they need.";
                }
            }
            catch { }
            return p;
        }
    }

    // ---------------------------------------------------------------- the setup assistant window
    class SetupForm : Form
    {
        readonly Watchdog wd; readonly Theme T; readonly Action<string> done; readonly FlowLayoutPanel flow; readonly Panel foot; readonly ActionButton bApply, bSkip;
        readonly Label status; SetupPlan plan; bool applied;
        readonly List<Tuple<ToggleSwitch, string>> apToggles = new List<Tuple<ToggleSwitch, string>>();
        readonly List<Tuple<ToggleSwitch, int>> chToggles = new List<Tuple<ToggleSwitch, int>>();
        ToggleSwitch tAuto, tLow, tQos, tWfd, tDns, tNotify; InputFrame fHome;
        readonly int cardW;

        public SetupForm(Watchdog w, Action<string> onDone)
        {
            wd = w; done = onDone; Ui.InitFonts(); T = Theme.Current();
            Text = "AX210_WatchClaude — Setup"; Icon = Brand.AppIcon(); BackColor = T.Bg; ForeColor = T.Text; Font = Ui.Body; AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; cardW = Ui.D(660);
            ClientSize = new Size(cardW + Ui.D(48), Ui.D(560));

            flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = T.Bg, Padding = new Padding(Ui.D(16), Ui.D(14), 0, Ui.D(6)) };
            flow.HandleCreated += (s, e) => Ui.DarkScroll(flow, T.Dark);
            flow.Controls.Add(new WelcomeBanner(T, cardW));
            status = new Label { Text = "Checking your adapter, network and crash history…", Font = Ui.Body, ForeColor = T.Sub, BackColor = T.Bg, AutoSize = true, UseMnemonic = false, Margin = new Padding(Ui.D(4), Ui.D(10), 0, Ui.D(10)) };
            flow.Controls.Add(status);

            foot = new Panel { Dock = DockStyle.Bottom, Height = Ui.D(58), BackColor = T.Bg, Width = ClientSize.Width };
            foot.Paint += (s, e) => { using (var pen = new Pen(T.CardBorder)) e.Graphics.DrawLine(pen, 0, 0, foot.Width, 0); };
            bApply = new ActionButton(T, "", "Apply", true) { Enabled = false };
            bSkip = new ActionButton(T, "", "Skip for now", false);
            int by = (foot.Height - bApply.Height) / 2;
            bApply.Location = new Point(foot.Width - Ui.D(16) - bApply.Width, by); bSkip.Location = new Point(bApply.Left - Ui.D(8) - bSkip.Width, by);
            bApply.Anchor = bSkip.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            foot.Controls.Add(bSkip); foot.Controls.Add(bApply);
            bApply.Click += (s, e) => Apply();
            bSkip.Click += (s, e) => Close();
            FormClosed += (s, e) => { if (!applied) { lock (wd.StLock) wd.St.SetupDone = true; wd.Save(); Log.Write("INFO", "Setup assistant closed without changes"); } };
            Controls.Add(foot); Controls.Add(flow); flow.BringToFront();
            KeyPreview = true; KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += (s, e) => { if (plan == null) ThreadPool.QueueUserWorkItem(_ =>
            {
                SetupPlan p; try { p = wd.DetectSetup(); } catch (Exception ex) { p = null; Log.Write("ERROR", "setup detection: " + ex.Message); }
                try { BeginInvoke(new Action(() => Fill(p))); } catch { }
            }); };
        }

        /// For --render: detect synchronously, then lay out.
        public void FillNow() { Fill(wd.DetectSetup()); }

        class WelcomeBanner : Control
        {
            readonly Theme T; Bitmap mark;
            public WelcomeBanner(Theme t, int w)
            {
                T = t; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                BackColor = t.Bg; Size = new Size(w, Ui.D(84)); Margin = new Padding(0); mark = Brand.Render(Ui.D(56), null);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(T.Bg); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(mark, Ui.D(4), Ui.D(10));
                int x = Ui.D(78);
                TextRenderer.DrawText(g, "Welcome to AX210_WatchClaude", Ui.Title, new Point(x, Ui.D(14)), T.Text, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "It looked at your Wi-Fi card, network and crash history and filled everything in.\nCheck it over, then press Apply. You can change any of it later in Settings.", Ui.Body,
                    new Rectangle(x, Ui.D(42), Width - x, Ui.D(44)), T.Sub, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            }
            protected override void Dispose(bool d) { if (d && mark != null) { mark.Dispose(); mark = null; } base.Dispose(d); }
        }

        Label Value(string text, Color? c = null)
        {
            text = Ui.Rd(text); int w = Ui.D(SectionCard.ColWidth);
            int h = TextRenderer.MeasureText(text, Ui.Body, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            return new Label { Text = text, Font = Ui.Body, ForeColor = c ?? T.Text, BackColor = T.Card, AutoSize = false, UseMnemonic = false, TextAlign = ContentAlignment.TopRight, Padding = new Padding(0, Ui.D(5), 0, 0), Size = new Size(w, h + Ui.D(7)) };
        }
        ToggleSwitch Tog(bool on) { return new ToggleSwitch(T) { Checked = on }; }

        void Fill(SetupPlan p)
        {
            plan = p; flow.SuspendLayout();
            if (p == null) { status.Text = "Couldn't check everything automatically. You can still apply the defaults or set things up in Settings."; status.ForeColor = T.Warn; }
            else
            {
                flow.Controls.Remove(status);
                var hw = new SectionCard(T, "Your hardware", cardW, null);
                hw.AddRow("Wi-Fi adapter", p.Intel ? null : "This isn't an Intel card. Monitoring, low-latency mode, game priority and the tests still work.", Value(p.AdapterDesc == "" ? "not found" : p.AdapterDesc, p.AdapterDesc == "" ? (Color?)T.Bad : null));
                hw.AddRow("Driver", null, Value(p.Driver));
                hw.AddRow("Band control", p.CanLock ? "Can keep the card on one band so the router can't bounce it around." : "Not available with this adapter's driver; band switching stays off.",
                    Value(p.CanLock ? (p.Has6 ? "5, 6 and 2.4 GHz" : "5 and 2.4 GHz (no 6 GHz)") : "not supported", p.CanLock ? (Color?)T.Good : T.Warn));
                hw.AddRow("Driver crashes", "Intel firmware crashes in the Windows event log over the last 180 days, on your home network.",
                    Value(p.TotalCrashes == 0 ? "none found" : p.TotalCrashes.ToString("N0"), p.TotalCrashes == 0 ? T.Good : p.TotalCrashes < 10 ? T.Warn : T.Bad));
                flow.Controls.Add(hw);

                var net = new SectionCard(T, "Your network", cardW, null);
                fHome = new InputFrame(T, false, Ui.D(SectionCard.ColWidth), Ui.D(32)); fHome.Box.Text = Ui.Rd(p.Ssid);
                net.AddRow("Home network", "The Wi-Fi name the watchdog manages. On any other network it only watches.", fHome);
                net.AddRow("Connected to", null, Value(p.Where));
                if (p.Nat != "") net.AddRow("Internet connection", p.Nat, Value(p.NatShort, p.NatShort.StartsWith("Open") ? (Color?)T.Good : T.Warn));
                flow.Controls.Add(net);

                var ext = new SectionCard(T, "Extenders to avoid", cardW, p.Extenders.Count == 0
                    ? "No extender is broadcasting your network name. (Mesh nodes from the same brand as the router aren't flagged.)"
                    : "These broadcast your network name but aren't your router. A desktop next to the router does better staying on the router itself.");
                foreach (var a in p.Extenders) { var t = Tog(a.Suggested); apToggles.Add(Tuple.Create(t, a.Bssid)); ext.AddRow(Ui.Rd(a.Bssid), Ui.Rd(a.Info), t); }                flow.Controls.Add(ext);

                var chs = new SectionCard(T, "Crash-prone channels", cardW, p.Channels.Count == 0
                    ? "No 5 GHz channel has crashed the driver here. The watchdog still quarantines any channel that starts crashing."
                    : "5 GHz channels where the Wi-Fi driver crashed. If your router moves to a checked one, the watchdog switches band until it moves away.");
                foreach (var c in p.Channels.Take(6))
                {
                    var t = Tog(c.Suggested); chToggles.Add(Tuple.Create(t, c.Channel));
                    chs.AddRow("5 GHz channel " + c.Channel, c.Crashes == 0 ? "already on your avoid list" : string.Format("{0:N0} crash{1} over {2:N0} connection{3}{4}", c.Crashes, c.Crashes == 1 ? "" : "es", c.Assoc, c.Assoc == 1 ? "" : "s",
                        p.TotalCrashes > 0 && c.Crashes > 0 ? string.Format(" · {0:0}% of all crashes", 100.0 * c.Crashes / p.TotalCrashes) : ""), t);
                }
                flow.Controls.Add(chs);

                var opt = new SectionCard(T, "What it will do", cardW, "Band order: " + string.Join(" › ", p.BandOrder.Select(b => b + " GHz")) + (p.CanLock ? "" : " (unused: no band control)") + " · " + p.GameFolders + " game folders found.");
                opt.AddRow("Automatic fixes", "Change band, reconnect and re-apply settings by itself. Off = watch and log only.", tAuto = Tog(wd.Cfg.AutoActions));
                opt.AddRow("Low-latency mode", "Keep Windows' background Wi-Fi scans off and streaming mode on while connected.", tLow = Tog(wd.Cfg.LowLatencyMode));
                opt.AddRow("Wi-Fi priority for game traffic", "Tag games' real-time (UDP) traffic so it goes first on your Wi-Fi.", tQos = Tog(wd.Cfg.QosTagGame));
                opt.AddRow("Pause Wi-Fi Direct while gaming", "Stops the card leaving your channel to look for casting devices.", tWfd = Tog(wd.Cfg.SuspendWifiDirectWhileGaming));
                opt.AddRow("Automatic DNS", "Pick the fastest DNS provider, only for a clear win.", tDns = Tog(wd.Cfg.AutoDns));
                opt.AddRow("Notifications", "Pop-ups for crashes, band switches, outages and session reports.", tNotify = Tog(wd.Cfg.Notifications));
                flow.Controls.Add(opt);
            }
            flow.ResumeLayout(); bApply.Enabled = true; bApply.Invalidate(); FitHeight();
        }

        void FitHeight()
        {
            int content = flow.Padding.Vertical;
            foreach (Control c in flow.Controls) content += c.Height + c.Margin.Vertical;
            var wa = Screen.FromControl(this).WorkingArea;
            int h = Math.Min(content + foot.Height, (int)(wa.Height * 0.92)), top = Math.Max(wa.Top, Top - (h - ClientSize.Height) / 2);
            ClientSize = new Size(ClientSize.Width + (content + foot.Height > h ? SystemInformation.VerticalScrollBarWidth : 0), h);
            if (Visible) Location = new Point(Left, Math.Min(top, wa.Bottom - Height));
        }

        void Apply()
        {
            var n = Json.S().Deserialize<Config>(Json.S().Serialize(wd.Cfg));
            if (plan != null)
            {
                var home = fHome.Box.Text.Trim(); if (home != "" && !Ui.Redact) n.HomeSsid = home;
                n.AvoidBssids = apToggles.Where(x => x.Item1.Checked).Select(x => x.Item2).Distinct().ToList();
                n.AvoidChannels5 = chToggles.Where(x => x.Item1.Checked).Select(x => x.Item2).Distinct().OrderBy(x => x).ToList();
                foreach (var keep in wd.Cfg.AvoidChannels5.Where(c => !chToggles.Any(x => x.Item2 == c))) n.AvoidChannels5.Add(keep);
                if (plan.CanLock) { var order = new List<string>(plan.BandOrder); foreach (var b in new[] { "5", "6", "2.4" }) if (!order.Contains(b)) order.Add(b); n.BandOrder = order; }
                n.AutoActions = tAuto.Checked; n.LowLatencyMode = tLow.Checked; n.QosTagGame = tQos.Checked; n.SuspendWifiDirectWhileGaming = tWfd.Checked; n.AutoDns = tDns.Checked; n.Notifications = tNotify.Checked;
            }
            var r = wd.ApplySettings(n);
            lock (wd.StLock) wd.St.SetupDone = true; wd.Save(); applied = true;
            Log.Write("INFO", string.Format("Setup assistant applied: home network set, {0} extender BSSID(s) and {1} channel(s) to avoid", n.AvoidBssids.Count, n.AvoidChannels5.Count));
            Close();
            var cb = done; if (cb != null) cb(n.AutoActions ? "Watching your Wi-Fi now. " + r : "Watching (automatic fixes are off). " + r);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.DarkTitle(Handle, T.Dark); }
    }
}
