// =====================================================================================
//  AX210_WatchClaude — v3 features (Claude, 2026-11-24)
//   * Wi-Fi Direct paused while a game runs (stops off-channel discovery excursions)
//   * Game session reports + a pre-game check that only speaks up when something's wrong
//   * Download/upload warning while gaming, naming the busiest programs
//   * Driver scorecard (crashes/hour per Intel driver version) + saved copy + rollback
//   * Network checkup (one click, pass/warn/fail)
//   * Upload limiter (optional): caps this PC's non-game uploads below line rate
//   * Fast downloads on 6 GHz (optional): big download + no game -> 6 GHz, then back
//   * Exit flag so the 5-minute self-restart respects "Exit" until the next boot
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AX210WatchClaude
{
    public class SessionRec
    {
        public string Game { get; set; } public DateTime Start { get; set; } public double Minutes { get; set; }
        public double GwMed { get; set; } public double GwP95 { get; set; } public double GwMax { get; set; } public double GwLossPct { get; set; }
        public double NetMed { get; set; } public double NetP95 { get; set; } public double NetLossPct { get; set; }
        public int Stalls { get; set; } public int Crashes { get; set; } public string Where { get; set; }
        public bool Streaming { get; set; } public string Bluetooth { get; set; }   // 1.0.3: a streaming/VR session; Bluetooth devices connected during it
    }
    public class DriverStat { public double Hours { get; set; } public int Crashes { get; set; } public DateTime FirstSeen { get; set; } public bool BackedUp { get; set; } public bool ViaWindowsUpdate { get; set; } }

    public class GameSession
    {
        public string Game; public DateTime Start = DateTime.Now, LastSample = DateTime.Now; public bool PreChecked, Streaming; public string Bluetooth = "";
        public readonly List<double> Gw = new List<double>(), Net = new List<double>(); public int GwN, GwLost, NetN, NetLost, Stalls;
        public readonly Dictionary<string, double> Where = new Dictionary<string, double>();
    }

    static class Native2
    {
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] public struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [DllImport("kernel32.dll")] public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool reboot);
        public static DateTime BootTime() { return DateTime.Now.AddMilliseconds(-(double)GetTickCount64()); }
    }

    static class ExitFlag
    {
        static string FlagFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Paths.App, "exited.flag"); } }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WTSINFO
        {
            public int State, SessionId, IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
            public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
        }
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);

        /// Identifies this sign-in: the session's logon time. It changes on sign-out/sign-in and on every start-up (Fast
        /// Startup signs you out first), and is the same for the elevated and normal processes of one sign-in.
        static string SessionKey()
        {
            try
            {
                IntPtr b; int n;
                if (WTSQuerySessionInformation(IntPtr.Zero, Process.GetCurrentProcess().SessionId, 24 /* WTSSessionInfo */, out b, out n))
                    try { var i = (WTSINFO)Marshal.PtrToStructure(b, typeof(WTSINFO)); if (i.LogonTime != 0) return "logon:" + i.LogonTime; }
                    finally { WTSFreeMemory(b); }
            }
            catch { }
            return "boot:" + DateTime.UtcNow.AddMilliseconds(-(double)Native2.GetTickCount64()).Ticks;   // UTC: immune to DST
        }
        /// Same sign-in? Logon keys match exactly; the boot-time fallback is recomputed from the clock, so it gets a tolerance.
        static bool SameSession(string a, string b)
        {
            if (a == b) return true;
            long x, y;
            return a.StartsWith("boot:") && b.StartsWith("boot:") && long.TryParse(a.Substring(5), out x) && long.TryParse(b.Substring(5), out y) && Math.Abs(x - y) < TimeSpan.FromMinutes(3).Ticks;
        }
        public static void Set() { try { Directory.CreateDirectory(Path.GetDirectoryName(FlagFile)); File.WriteAllText(FlagFile, SessionKey()); } catch { } }
        public static void Clear() { try { if (File.Exists(FlagFile)) File.Delete(FlagFile); } catch { } }
        /// True when the user chose Exit earlier in this sign-in, so the self-restart and logon start leave it alone.
        public static bool ExitedThisSession()
        {
            try
            {
                if (!File.Exists(FlagFile)) return false;
                if (SameSession(File.ReadAllText(FlagFile).Trim(), SessionKey())) return true;
                File.Delete(FlagFile);
            }
            catch { }
            return false;
        }
    }

    static class Upnp
    {
        public static string Location(out string server)
        {
            server = "";
            try
            {
                using (var u = new UdpClient())
                {
                    u.Client.ReceiveTimeout = 2500;
                    var m = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
                    u.Send(m, m.Length, "239.255.255.250", 1900);
                    var ep = new IPEndPoint(IPAddress.Any, 0); var r = Encoding.ASCII.GetString(u.Receive(ref ep));
                    server = Regex.Match(r, @"(?im)^SERVER:\s*(.+)$").Groups[1].Value.Trim();
                    var loc = Regex.Match(r, @"(?im)^LOCATION:\s*(\S+)").Groups[1].Value.Trim();
                    return loc == "" ? null : loc;
                }
            }
            catch { return null; }
        }
        public static string ExternalIp(string location)
        {
            try
            {
                string xml; using (var wc = new WebClient { Proxy = null }) xml = wc.DownloadString(location);
                var m = Regex.Match(xml, @"<serviceType>(urn:schemas-upnp-org:service:WAN(?:IP|PPP)Connection:\d)</serviceType>.*?<controlURL>(.+?)</controlURL>", RegexOptions.Singleline);
                if (!m.Success) return null;
                string st = m.Groups[1].Value; var ctrl = new Uri(new Uri(location), m.Groups[2].Value.Trim());
                var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:GetExternalIPAddress xmlns:u=\"" + st + "\"></u:GetExternalIPAddress></s:Body></s:Envelope>";
                var req = (HttpWebRequest)WebRequest.Create(ctrl); req.Proxy = null; req.Method = "POST"; req.Timeout = 4000;
                req.ContentType = "text/xml; charset=\"utf-8\""; req.Headers.Add("SOAPACTION", "\"" + st + "#GetExternalIPAddress\"");
                var bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream()))
                    return Regex.Match(sr.ReadToEnd(), "<NewExternalIPAddress>(.*?)</NewExternalIPAddress>").Groups[1].Value.Trim();
            }
            catch { return null; }
        }
        public static bool IsPrivate(string ip)
        {
            IPAddress a; if (!IPAddress.TryParse(ip ?? "", out a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
    }

    partial class Watchdog
    {
        public double RxMbps, TxMbps; long lastRxBytes = -1, lastTxBytes = -1; DateTime lastBytesT, lastFeatTick, lastScoreCheck;
        DateTime? heavySince, quietSince, turboSince;
        volatile GameSession session; public GameSession Session { get { return session; } }
        static string DriverDir { get { return Path.Combine(Paths.Data, "driver-backup"); } }

        void FeaturesStart()
        {
            lock (StLock) { if (DriverVersion != "?" && Adapter.IsIntel && !St.DriverStats.ContainsKey(DriverVersion)) St.DriverStats[DriverVersion] = new DriverStat { FirstSeen = DateTime.UtcNow }; }
            if (CanAct)
            {
                if (St.WifiDirectDisabled.Count > 0) ThreadPool.QueueUserWorkItem(_ => RestoreWifiDirect("left paused by the previous run"));
                ThreadPool.QueueUserWorkItem(_ => { ApplyUploadLimiter(false); BackupDriver(); });
            }
        }

        void FeaturesTick(ConnInfo c)
        {
            var now = DateTime.Now; double dt = lastFeatTick == default(DateTime) ? 0 : (now - lastFeatTick).TotalSeconds; lastFeatTick = now;
            try   // throughput on the Wi-Fi adapter
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, Adapter.Guid.ToString("B"), StringComparison.OrdinalIgnoreCase));
                if (ni != null)
                {
                    var s = ni.GetIPStatistics(); long rx = s.BytesReceived, tx = s.BytesSent; double bdt = (now - lastBytesT).TotalSeconds;
                    if (lastRxBytes >= 0 && rx >= lastRxBytes && tx >= lastTxBytes && bdt > 0.5) { RxMbps = (rx - lastRxBytes) * 8 / bdt / 1e6; TxMbps = (tx - lastTxBytes) * 8 / bdt / 1e6; }
                    lastRxBytes = rx; lastTxBytes = tx; lastBytesT = now;
                }
            }
            catch { }
            if (c.Connected && dt > 0 && dt < 60) lock (StLock) { DriverStat d; if (St.DriverStats.TryGetValue(DriverVersion, out d)) d.Hours += dt / 3600.0; }
            var ses = session;
            if (ses != null)
            {
                lock (ses)
                {
                    foreach (var x in Pinger.Recent(30).Where(x => x.T > ses.LastSample))
                    {
                        if (x.Gw != -2) { ses.GwN++; if (x.Gw < 0) ses.GwLost++; else ses.Gw.Add(x.Gw); if (x.Gw < 0 || x.Gw >= Cfg.StallMs) ses.Stalls++; }
                        if (x.Net != -2) { ses.NetN++; if (x.Net < 0) ses.NetLost++; else ses.Net.Add(x.Net); }
                        ses.LastSample = x.T;
                    }
                    if (c.Connected && dt > 0 && dt < 60) { var k = ApName(c.Bssid) + " " + KeyLabel(c.Key); double v; ses.Where.TryGetValue(k, out v); ses.Where[k] = v + dt; }
                }
                if (!ses.PreChecked && (now - ses.Start).TotalSeconds >= 15) { ses.PreChecked = true; PreGameCheck(ses); }
            }
            BandwidthGuard(now);
            if ((now - lastScoreCheck).TotalHours >= 1) { lastScoreCheck = now; DriverScorecardCheck(); }
        }

        // ---------- games: start / end
        void OnGameStarted(string game, bool streaming)
        {
            session = new GameSession { Game = game, Streaming = streaming };
            int gen = Interlocked.Increment(ref wfdGen);
            // Streaming/VR: no Wi-Fi Direct pause. Quest users often stream through Windows' Mobile Hotspot, and wireless
            // displays use Wi-Fi Direct too; pausing it would cut them off.
            if (Cfg.SuspendWifiDirectWhileGaming && MayChange && !streaming && WiredName == null) ThreadPool.QueueUserWorkItem(_ => SuspendWifiDirect(gen));
            ThreadPool.QueueUserWorkItem(_ => BatteryGameBoost(true, gen));
            MeshHoldStart();
        }

        void OnGameEnded(string game)
        {
            var ses = session; session = null;
            int gen = Interlocked.Increment(ref wfdGen);
            if (CanAct) ThreadPool.QueueUserWorkItem(_ => RestoreWifiDirect(game + " closed", gen));   // always queued: a pause may still be in flight
            ThreadPool.QueueUserWorkItem(_ => BatteryGameBoost(false, gen));
            MeshHoldEnd();
            if (ses == null) return;
            double mins = (DateTime.Now - ses.Start).TotalMinutes; if (mins < 2) return;
            SessionRec rec; string where;
            lock (ses)
            {
                ses.Gw.Sort(); ses.Net.Sort();
                where = string.Join(", ", ses.Where.OrderByDescending(x => x.Value).Take(2).Select(x => x.Key));
                rec = new SessionRec
                {
                    Game = ses.Game, Start = ses.Start.ToUniversalTime(), Minutes = Math.Round(mins, 1),
                    GwMed = PingMonitor.Pct(ses.Gw, 50), GwP95 = PingMonitor.Pct(ses.Gw, 95), GwMax = ses.Gw.Count > 0 ? ses.Gw[ses.Gw.Count - 1] : 0,
                    GwLossPct = ses.GwN == 0 ? 0 : Math.Round(100.0 * ses.GwLost / ses.GwN, 2),
                    NetMed = PingMonitor.Pct(ses.Net, 50), NetP95 = PingMonitor.Pct(ses.Net, 95), NetLossPct = ses.NetN == 0 ? 0 : Math.Round(100.0 * ses.NetLost / ses.NetN, 2),
                    Stalls = ses.Stalls, Where = where, Streaming = ses.Streaming, Bluetooth = ses.Bluetooth ?? ""
                };
            }
            lock (StLock)
            {
                rec.Crashes = St.Incidents.Count(i => i.Kind == "crash" && i.Time >= rec.Start);
                St.Sessions.Add(rec); if (St.Sessions.Count > 150) St.Sessions.RemoveRange(0, St.Sessions.Count - 120);
            }
            Save();
            bool clean = rec.Crashes == 0 && rec.GwLossPct < 0.5 && rec.NetLossPct < 1 && rec.GwP95 < 20;
            var text = string.Format("{0:0} min · router {1:0}/{2:0}/{3:0} ms (median/p95/max) · internet {4:0}/{5:0} ms · loss {6:0.#}% · {7} stall{8} · {9} driver crash{10} · {11}",
                rec.Minutes, rec.GwMed, rec.GwP95, rec.GwMax, rec.NetMed, rec.NetP95, Math.Max(rec.GwLossPct, rec.NetLossPct), rec.Stalls, rec.Stalls == 1 ? "" : "s", rec.Crashes, rec.Crashes == 1 ? "" : "es", where);
            if (!clean && rec.Bluetooth != "") text += " · Bluetooth was connected (" + rec.Bluetooth + "): it shares the Wi-Fi card's antenna, so it can cause lag";
            Log.Write("SESSION", ses.Game + ": " + text);
            try
            {
                var f = Path.Combine(Paths.Logs, "sessions.csv");
                if (!File.Exists(f)) File.WriteAllText(f, "start,game,minutes,gw_med,gw_p95,gw_max,gw_loss_pct,net_med,net_p95,net_loss_pct,stalls,crashes,where\r\n");
                File.AppendAllText(f, string.Join(",", ses.Start.ToString("yyyy-MM-dd HH:mm"), "\"" + ses.Game + "\"", rec.Minutes, rec.GwMed, rec.GwP95, rec.GwMax, rec.GwLossPct, rec.NetMed, rec.NetP95, rec.NetLossPct, rec.Stalls, rec.Crashes, "\"" + where + "\"") + "\r\n");
            }
            catch { }
            if (Cfg.SessionReports) Note("session", ses.Game + " session: " + (clean ? "clean ✔" : "had problems"), text, clean ? ToolTipIcon.Info : ToolTipIcon.Warning, 0);
        }

        void PreGameCheck(GameSession ses)
        {
            var issues = new List<string>(); var c = Conn; var p = Pinger.Stats(15);
            if (!c.Connected) issues.Add("Wi-Fi isn't connected");
            else
            {
                if (IsAvoidBssid(c.Bssid)) issues.Add("you're on an extender");
                else if (c.Band == "5" && Cfg.AvoidChannels5.Contains(c.Channel)) issues.Add("Wi-Fi is on channel " + c.Channel);
                else if (c.Band != PreferredBand() && !St.TurboActive) issues.Add("Wi-Fi is on the " + c.Band + " GHz fallback");
            }
            if (p.GwP95 > 30 || p.GwLossPct > 1) issues.Add(string.Format("router ping is unstable ({0:0} ms p95, {1:0.#}% loss)", p.GwP95, p.GwLossPct));
            if (!ses.Streaming && RxMbps > Cfg.GuardRxMbps) issues.Add(string.Format("something is downloading at ~{0:0} Mbps", RxMbps));   // a stream is that traffic itself
            if (!ses.Streaming && TxMbps > Cfg.GuardTxMbps) issues.Add(string.Format("something is uploading at ~{0:0} Mbps", TxMbps));
            var bt = Bt.Connected(); lock (ses) ses.Bluetooth = bt;
            if (bt != "" && c.Connected && c.Band == "2.4") issues.Add("Bluetooth (" + bt + ") is connected while Wi-Fi is on 2.4 GHz, the band Bluetooth uses too");
            MeshPreGame(c);   // moves to a clearly stronger node on its own, with its own notice
            if (CrashCount(1) > 0) issues.Add("the Wi-Fi driver crashed in the last hour");
            if (UdpInUse >= Cfg.UdpWarnThreshold) issues.Add("network ports are running low");
            if (issues.Count == 0) { Log.Write("GAME", string.Format("Pre-game check for {0}: all clear ({1}, router {2:0}/{3:0} ms)", ses.Game, Describe(c), p.GwP50, p.GwP95)); return; }
            var s = string.Join("; ", issues);
            Log.Write("GAME", "Pre-game check for " + ses.Game + ": " + s);
            Note("pregame", "Heads-up for " + ses.Game, char.ToUpper(s[0]) + s.Substring(1) + ".", ToolTipIcon.Warning, 5);
        }

        // ---------- Wi-Fi Direct
        // Pause and restore run one at a time, and each game start/end bumps wfdGen. A queued pause or restore whose game
        // event has since been superseded does nothing, so a game that closes while its pause is still running gets
        // restored, and a quick game switch doesn't restore in the middle of the next game.
        readonly object wfdLock = new object(); int wfdGen;
        void SuspendWifiDirect(int gen = -1)
        {
            lock (wfdLock)
            {
                if (gen >= 0 && gen != Volatile.Read(ref wfdGen)) return;
                string so, se;
                Ps.Run("$a = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.InterfaceDescription -like 'Microsoft Wi-Fi Direct Virtual Adapter*' -and $_.AdminStatus -eq 'Up' -and $_.Status -ne 'Up' }); " +
                       "$a | Disable-NetAdapter -Confirm:$false; 'WFD:' + (($a | ForEach-Object { $_.InterfaceDescription }) -join '|')", 60, out so, out se);
                var line = (so ?? "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("WFD:")) ?? "WFD:";
                var names = line.Substring(4).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                if (names.Count > 0)
                {
                    lock (StLock) St.WifiDirectDisabled = St.WifiDirectDisabled.Union(names).ToList();   // add to, never replace, what's already paused
                    Save(); Log.Write("GAME", "Wi-Fi Direct paused for the game (" + names.Count + " virtual adapters)");
                }
                else if (!string.IsNullOrWhiteSpace(se)) Log.Write("ERROR", "Couldn't pause Wi-Fi Direct: " + se.Trim());
            }
        }
        public void RestoreWifiDirect(string why) { RestoreWifiDirect(why, -1); }
        void RestoreWifiDirect(string why, int gen)
        {
            lock (wfdLock)
            {
                if (gen >= 0 && gen != Volatile.Read(ref wfdGen)) return;   // another game started since
                List<string> names; lock (StLock) names = St.WifiDirectDisabled.ToList(); if (names.Count == 0) return;
                string so, se;
                bool ok = Ps.Run("Get-NetAdapter -IncludeHidden | Where-Object { @(" + string.Join(",", names.Select(Ps.Q)) + ") -contains $_.InterfaceDescription } | Enable-NetAdapter -Confirm:$false", 60, out so, out se);
                if (ok && string.IsNullOrWhiteSpace(se)) { lock (StLock) St.WifiDirectDisabled.Clear(); Save(); Log.Write("GAME", "Wi-Fi Direct restored (" + why + ")"); }
                else Log.Write("ERROR", "Couldn't restore Wi-Fi Direct: " + se);
            }
        }

        // ---------- bandwidth guard
        void BandwidthGuard(DateTime now)
        {
            var ses0 = session;
            if (!InGame || !Cfg.BandwidthGuard || Busy != "" || St.TurboActive || (ses0 != null && ses0.Streaming)) { if (heavySince != null && heavySince < now) heavySince = null; return; }
            bool heavy = RxMbps > Cfg.GuardRxMbps || TxMbps > Cfg.GuardTxMbps;
            if (!heavy) { if (heavySince != null && heavySince < now) heavySince = null; return; }
            if (heavySince == null) { heavySince = now; return; }
            if ((now - heavySince.Value).TotalSeconds < 20) return;
            heavySince = now.AddMinutes(10);   // check again in 10 min at the earliest
            double rx = RxMbps, tx = TxMbps; string game = CurrentGame;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var top = TopIo(2000, game);
                string dir = rx > Cfg.GuardRxMbps ? string.Format("downloading at ~{0:0} Mbps", rx) : string.Format("uploading at ~{0:0} Mbps", tx);
                Log.Write("GAME", "While " + game + " runs, this PC is " + dir + ". Busiest programs: " + top);
                Note("bwguard", "Something is using your connection", "This PC is " + dir + " while you play " + game + ". Busiest: " + top + ". Pausing it will cut lag.", ToolTipIcon.Warning, 15);
            });
        }
        static string TopIo(int ms, string excludeGame)
        {
            Func<Dictionary<int, Tuple<string, ulong>>> snap = () =>
            {
                var d = new Dictionary<int, Tuple<string, ulong>>();
                foreach (var p in Process.GetProcesses())
                    using (p)
                    {
                        if (p.Id <= 4) continue;
                        IntPtr h = Native2.OpenProcess(0x1000, false, p.Id); if (h == IntPtr.Zero) continue;
                        try { Native2.IO_COUNTERS c; if (Native2.GetProcessIoCounters(h, out c)) d[p.Id] = Tuple.Create(p.ProcessName, c.ReadBytes + c.WriteBytes + c.OtherBytes); }
                        catch { }
                        finally { Native2.CloseHandle(h); }
                    }
                return d;
            };
            var a = snap(); Thread.Sleep(ms); var b = snap(); int me = Process.GetCurrentProcess().Id;
            var rates = b.Where(kv => kv.Key != me && a.ContainsKey(kv.Key) && kv.Value.Item2 >= a[kv.Key].Item2)
                         .Select(kv => Tuple.Create(kv.Value.Item1, (kv.Value.Item2 - a[kv.Key].Item2) / (ms / 1000.0) / 1e6))
                         .Where(t => !string.Equals(GameList.DisplayName(t.Item1), excludeGame, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(t => t.Item1).Select(g => Tuple.Create(g.Key, g.Sum(x => x.Item2)))
                         .Where(t => t.Item2 > 0.5).OrderByDescending(t => t.Item2).Take(3).ToList();
            return rates.Count == 0 ? "couldn't tell which program" : string.Join(", ", rates.Select(t => string.Format("{0} ({1:0} MB/s)", t.Item1, t.Item2)));
        }

        // ---------- driver scorecard, backup, rollback
        void DriverCrashCounted() { lock (StLock) { DriverStat d; if (St.DriverStats.TryGetValue(DriverVersion, out d)) d.Crashes++; } }
        /// Driver scorecard, backup and rollback are for Intel cards: another adapter (a USB one in use for a while) never
        /// gets into them, so a rollback can't aim at the wrong device.
        void OnDriverChanged()
        {
            if (!Adapter.IsIntel) return;
            lock (StLock) { if (!St.DriverStats.ContainsKey(DriverVersion)) St.DriverStats[DriverVersion] = new DriverStat { FirstSeen = DateTime.UtcNow }; }
            if (CanAct) ThreadPool.QueueUserWorkItem(_ => BackupDriver());
        }
        void BackupDriver()
        {
            try
            {
                var ver = DriverVersion; if (string.IsNullOrEmpty(ver) || ver == "?" || !Adapter.Found || StandIn || !Adapter.IsIntel) return;   // card missing: backed up once it's back
                var hwid = Adapter.HardwareId; if (hwid != "") lock (StLock) St.DriverHardwareId = hwid;   // always this card's, even when the copy exists
                var dir = Path.Combine(DriverDir, ver);
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.inf").Length > 0) { MarkBacked(ver); return; }
                string so, se;
                var script = "$id=(Get-NetAdapter -Name " + Ps.Q(Adapter.Name) + ").PnPDeviceID; $inf=(Get-PnpDeviceProperty -InstanceId $id -KeyName DEVPKEY_Device_DriverInfPath).Data; " +
                             "$hw=@((Get-PnpDeviceProperty -InstanceId $id -KeyName DEVPKEY_Device_HardwareIds).Data)[0]; New-Item -ItemType Directory -Force -Path " + Ps.Q(dir) + " | Out-Null; " +
                             "pnputil /export-driver $inf " + Ps.Q(dir) + " | Out-Null; 'HWID=' + $hw";
                Ps.Run(script, 300, out so, out se);
                var hw = Regex.Match(so ?? "", @"HWID=(\S+)").Groups[1].Value;
                if (hw != "") lock (StLock) St.DriverHardwareId = hw;

                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.inf").Length > 0) { MarkBacked(ver); Log.Write("DRIVER", "Saved a copy of Intel driver " + ver + " for rollback"); PruneDriverBackups(); }
                else Log.Write("ERROR", "Driver backup failed: " + so + " " + se);
            }
            catch (Exception ex) { Log.Write("ERROR", "Driver backup: " + ex.Message); }
        }
        void MarkBacked(string ver) { lock (StLock) { DriverStat d; if (St.DriverStats.TryGetValue(ver, out d)) d.BackedUp = true; } Save(); }
        void PruneDriverBackups()
        {
            try
            {
                foreach (var d in new DirectoryInfo(DriverDir).GetDirectories().Where(d => d.Name != DriverVersion).OrderByDescending(d => d.CreationTimeUtc).Skip(1))
                { d.Delete(true); Log.Write("DRIVER", "Removed older driver copy " + d.Name); lock (StLock) { DriverStat s; if (St.DriverStats.TryGetValue(d.Name, out s)) s.BackedUp = false; } }
            }
            catch { }
        }
        public List<string> DriverBackups()
        {
            try { return new DirectoryInfo(DriverDir).GetDirectories().Where(d => d.Name != DriverVersion && d.GetFiles("*.inf").Length > 0).OrderByDescending(d => d.CreationTimeUtc).Select(d => d.Name).ToList(); }
            catch { return new List<string>(); }
        }
        public string RollbackDriver(string version)
        {
            var dir = Path.Combine(DriverDir, version); var inf = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.inf").FirstOrDefault() : null;
            if (inf == null) return "No saved copy of driver " + version + ".";
            if (StandIn || CardMissing || !Adapter.Found) return "This PC's Wi-Fi card isn't available right now, so there's nothing to roll back.";
            if (!Adapter.IsIntel || !OwnCardInUse()) return "Driver rollback is for this PC's Intel card, and another adapter is in use right now.";
            var hwid = Adapter.HardwareId; if (hwid == "") lock (StLock) hwid = St.DriverHardwareId;
            if (string.IsNullOrEmpty(hwid)) return "The card's hardware ID isn't known yet, so I can't roll back automatically.";
            var from = DriverVersion; Log.Write("DRIVER", "Rolling back Intel driver " + from + " → " + version);
            bool reboot, ok; int err; string why;
            using (Adapter.Work())   // the card goes away while Windows installs the driver: not a crash
            {
                ok = Native2.UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hwid, inf, 1 /* INSTALLFLAG_FORCE */, out reboot);
                err = Marshal.GetLastWin32Error();
                if (ok) WaitForBand(Profiles.BandOf(St.CurrentMode), 120, out why);
            }
            if (!ok) { Log.Write("ERROR", "Driver rollback failed (Windows error " + err + ")"); return "Rollback failed (Windows error " + err + "). Your current driver " + from + " is unchanged."; }
            DriverVersion = Adapter.DriverVersion(); St.LastDriverVersion = DriverVersion; lastProfileCheck = DateTime.MinValue; OnDriverChanged(); Save();
            return "Installed Intel driver " + DriverVersion + (reboot ? ". Windows wants a restart to finish." : ".") + " Now on " + Describe(Wlan.GetConnection()) + ".";
        }
        void DriverScorecardCheck()
        {
            Dictionary<string, DriverStat> all; lock (StLock) all = St.DriverStats.ToDictionary(kv => kv.Key, kv => kv.Value);
            DriverStat cur; if (!all.TryGetValue(DriverVersion, out cur) || cur.Crashes < 3 || cur.Hours < 2) return;
            double rc = cur.Crashes / cur.Hours;
            var better = all.Where(kv => kv.Key != DriverVersion && kv.Value.Hours >= 5).Select(kv => new { V = kv.Key, R = kv.Value.Crashes / kv.Value.Hours })
                            .Where(x => rc > Math.Max(2 * x.R, 0.25)).OrderBy(x => x.R).FirstOrDefault();
            if (better == null) return;
            bool saved = DriverBackups().Contains(better.V);
            Log.Write("DRIVER", string.Format("Driver {0} crashes {1:0.00}/h vs {2:0.00}/h on {3}", DriverVersion, rc, better.R, better.V));
            Note("driverworse", "This Intel driver is crashing more", string.Format("{0}: {1:0.0} crashes/hour vs {2:0.0} on {3}. {4}", DriverVersion, rc, better.R, better.V,
                saved ? "Right-click the tray icon → Roll back Wi-Fi driver." : "There's no saved copy of the older driver."), ToolTipIcon.Warning, 24 * 60);
        }

        // ---------- upload limiter (optional)
        public string ApplyUploadLimiter(bool report)
        {
            if (!IsAdmin) return "Needs administrator rights.";
            string o;
            if (!Cfg.UploadLimiter)
            {
                Ps.Run("Remove-NetQosPolicy -Name 'AX210_WatchClaude_UploadCap' -Confirm:$false -ErrorAction SilentlyContinue", 60, out o);
                if (report) Log.Write("INFO", "Upload limiter off");
                return "Upload limiter is off.";
            }
            double cap = Cfg.UploadCapMbps > 0 ? Cfg.UploadCapMbps : St.LastUploadMbps * 0.9;
            if (cap < 5) return "Run a speed test first so the limiter knows your upload speed (or set UploadCapMbps in settings).";
            var script = "Remove-NetQosPolicy -Name 'AX210_WatchClaude_UploadCap' -Confirm:$false -ErrorAction SilentlyContinue; " +
                         "New-NetQosPolicy -Name 'AX210_WatchClaude_UploadCap' -Default -ThrottleRateActionBitsPerSecond " + (ulong)(cap * 1e6) + " -NetworkProfile All | Out-Null";
            bool ok = Ps.Run(script, 60, out o);
            var msg = ok ? string.Format("Upload limiter on: this PC's uploads are capped at {0:0} Mbps; game traffic is exempt.", cap) : "Couldn't set the upload limiter: " + o;
            Log.Write(ok ? "INFO" : "ERROR", msg); return msg;
        }

        public string SpeedTest()
        {
            string o; bool limiter = Cfg.UploadLimiter && CanAct;
            if (limiter) Ps.Run("Remove-NetQosPolicy -Name 'AX210_WatchClaude_UploadCap' -Confirm:$false -ErrorAction SilentlyContinue", 60, out o);   // measure the real line rate
            var r = Tests.Speed();
            lock (StLock) { if (Tests.LastDown > 1) St.LastDownloadMbps = Tests.LastDown; if (Tests.LastUp > 1) St.LastUploadMbps = Tests.LastUp; }
            Save();
            if (limiter) r += "\r\n" + ApplyUploadLimiter(true);
            return r;
        }

        // ---------- fast downloads on 6 GHz (optional)
        bool TurboCheck()
        {
            var now = DateTime.Now;
            if (St.TurboActive)
            {
                if (RxMbps >= 50) quietSince = null; else if (quietSince == null) quietSince = now;
                bool end = InGame || !Cfg.TurboDownloads || (quietSince != null && (now - quietSince.Value).TotalSeconds >= 60);
                if (!end) return false;
                var reason = InGame ? CurrentGame + " started" : !Cfg.TurboDownloads ? "fast downloads turned off" : "the download finished";
                bool game = InGame;
                return RunAction("Leaving fast-download mode", () => { lock (StLock) St.TurboActive = false; Save(); ApplyMode(PreferredBand(), reason + " — back to the gaming band", true); });
            }
            if (!Cfg.TurboDownloads || InGame || !BandAvailable("6") || Profiles.BandOf(St.CurrentMode) == "6" || ModeBlocked("6") || RxMbps < Cfg.TurboRxMbps) { turboSince = null; return false; }
            if (turboSince == null) { turboSince = now; return false; }
            if ((now - turboSince.Value).TotalSeconds < 30) return false;
            turboSince = null;
            return RunAction("Fast-download mode", () => { if (ApplyMode("6", string.Format("big download at ~{0:0} Mbps — using 6 GHz's faster link until it finishes", RxMbps), true)) { lock (StLock) St.TurboActive = true; Save(); } });
        }

        // ---------- options from the tray
        public string SetOption(string name, bool on)
        {
            switch (name)
            {
                case "wfd": Cfg.SuspendWifiDirectWhileGaming = on; Cfg.Save(); if (!on) RestoreWifiDirect("option turned off"); else if (InGame) SuspendWifiDirect(); return "Pause Wi-Fi Direct while gaming: " + (on ? "on" : "off");
                case "reports": Cfg.SessionReports = on; Cfg.Save(); return "Game session reports: " + (on ? "on" : "off");
                case "guard": Cfg.BandwidthGuard = on; Cfg.Save(); return "Download warnings while gaming: " + (on ? "on" : "off");
                case "upload": Cfg.UploadLimiter = on; Cfg.Save(); return ApplyUploadLimiter(true);
                case "turbo": Cfg.TurboDownloads = on; Cfg.Save(); nudge.Set(); return "Fast downloads on 6 GHz: " + (on ? "on (kicks in when a big download runs and no game is open)" : "off");
            }
            return "";
        }

        /// Self-test entry: a bare watchdog (no loops, no writes) that runs the checkup read-only.
        public static string SelfCheckup()
        {
            var w = new Watchdog { Cfg = new Config(), St = new State(), Wlan = new WlanClient() };
            Adapter.Init(w.Wlan.Iface); w.DriverVersion = Adapter.DriverVersion();
            w.Cfg.HomeSsid = w.Wlan.GetConnection().Ssid; w.St.CurrentMode = Profiles.InferMode(Adapter.Read(Profiles.ModeKeys));
            w.St.DriverStats[w.DriverVersion] = new DriverStat { FirstSeen = DateTime.UtcNow };
            return w.Checkup() + "\r\n\r\nBusiest programs right now (I/O): " + TopIo(1500, "");
        }

        // ---------- one-click network checkup
        static int PingTo(string host, int size, bool df) { try { using (var p = new Ping()) { var r = p.Send(host, 1500, new byte[size], new PingOptions(64, df)); return r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1; } } catch { return -1; } }

        public string Checkup()
        {
            var lines = new List<string>(); int ok = 0, warn = 0, fail = 0;
            Action<string, string, string> R = (lvl, what, detail) => { if (lvl == "OK") ok++; else if (lvl == "WARN") warn++; else if (lvl == "FAIL") fail++; lines.Add(string.Format("[{0,-4}] {1,-24} {2}", lvl, what, detail)); };
            var c = Wlan.GetConnection();
            if (!c.Connected) R("FAIL", "Wi-Fi link", "not connected");
            else R(IsAvoidBssid(c.Bssid) || !ChannelOk(c.Band, c.Channel) ? "FAIL" : c.Band != PreferredBand() && !St.TurboActive ? "WARN" : "OK", "Wi-Fi link", Describe(c) + string.Format(", link {0}/{1} Mbps", c.RxMbps, c.TxMbps));
            if (c.Connected) R(c.Rssi >= -60 ? "OK" : c.Rssi >= -70 ? "WARN" : "FAIL", "Signal", c.Rssi + " dBm");

            var gw = Adapter.Gateway(); var lat = Tests.MeasureMany(new[] { gw, "1.1.1.1", "8.8.8.8" }, 20, 250, Cfg.StallMs);
            R(lat[0].P95 < 20 && lat[0].LossPct < 1 ? "OK" : lat[0].P95 < 50 && lat[0].LossPct < 3 ? "WARN" : "FAIL", "Router ping (20 s)",
              string.Format("median {0:0} · p95 {1:0} · max {2:0} ms · loss {3:0.#}% · stalls {4}", lat[0].P50, lat[0].P95, lat[0].Max, lat[0].LossPct, lat[0].Stalls));
            for (int i = 1; i < 3; i++)
                R(lat[i].LossPct < 1 && lat[i].P95 < lat[i].P50 + 40 ? "OK" : "WARN", "Internet ping " + lat[i].Host, string.Format("median {0:0} · p95 {1:0} · max {2:0} ms · loss {3:0.#}%", lat[i].P50, lat[i].P95, lat[i].Max, lat[i].LossPct));
            int v6 = PingTo("2606:4700:4700::1111", 32, false); R(v6 >= 0 ? "OK" : "WARN", "IPv6", v6 >= 0 ? v6 + " ms to Cloudflare" : "no IPv6 reply");
            int mtu = PingTo("1.1.1.1", 1472, true); R(mtu >= 0 ? "OK" : "WARN", "Full-size packets", mtu >= 0 ? "1500-byte packets pass without fragmenting" : "1500-byte packets don't get through (possible MTU problem)");

            if (!InGame) { Wlan.Scan(); Thread.Sleep(4000); }
            var bss = Wlan.GetBssList(); var ext = bss.Where(b => IsAvoidBssid(b.Bssid)).ToList();
            var home = CurrentHome;
            if (Cfg.AvoidBssids.Count == 0) R("INFO", "Extender", "none set up (Setup assistant finds them)");
            else if (ext.Any(b => b.Ssid == home)) R("WARN", "Extender", "broadcasts \"" + home + "\" too, on " + string.Join(", ", ext.Where(b => b.Ssid == home).Select(b => b.Band + " GHz ch " + b.Channel)) + ". Avoided, but renaming its network is the sure fix");
            else if (ext.Count > 0) R("OK", "Extender", "uses its own network name; this PC won't join it");
            else R("INFO", "Extender", "not visible right now" + (InGame ? " (no scans during games)" : ""));
            if (c.Connected) { int same = bss.Count(b => b.Band == c.Band && b.Channel == c.Channel && b.Ssid != c.Ssid); R(same <= 3 ? "OK" : "WARN", "Channel crowding", same + (same == 1 ? " other network on " : " other networks on ") + c.Band + " GHz ch " + c.Channel); }
            if (c.Connected && c.Band == "5")
            {
                int dfs; lock (StLock) dfs = St.Incidents.Count(i => i.Kind == "dfs" && i.Time > DateTime.UtcNow.AddDays(-30));
                if (IsDfs(c.Channel)) R(dfs > 0 ? "WARN" : "INFO", "Radar channels (DFS)", "channel " + c.Channel + " is a radar channel: if the router detects radar it must leave, which can drop you for up to a minute" + (dfs > 0 ? " (" + dfs + " time" + (dfs == 1 ? "" : "s") + " in 30 days)" : "") + ". Channels 36–48 or 149–165 avoid that");
                else R("OK", "Radar channels (DFS)", "channel " + c.Channel + " isn't one" + (dfs > 0 ? " (" + dfs + " radar channel change" + (dfs == 1 ? "" : "s") + " in 30 days)" : ""));
            }
            var btNow = Bt.Connected();
            R(btNow == "" ? "OK" : c.Connected && c.Band == "2.4" ? "WARN" : "INFO", "Bluetooth", btNow == "" ? "nothing connected" : btNow + " connected. Bluetooth shares the Wi-Fi card's antenna" + (c.Connected && c.Band == "2.4" ? " and the 2.4 GHz band you're on: expect lag spikes" : "; if games stall with it on, a wired or 2.4 GHz-dongle headset helps") + BtStallNote());
            R(WiredName != null ? "INFO" : "OK", "Connection", (WiredName != null ? "Ethernet in use (" + WiredName + "); Wi-Fi is watched only" : "Wi-Fi carries the internet traffic") + (Metered ? " · metered: data-heavy tests ask first" : ""));

            string server; var loc = Upnp.Location(out server);
            if (loc == null) R("WARN", "UPnP", "router didn't answer; peer-to-peer co-op games may get a strict NAT");
            else
            {
                R("OK", "UPnP", "on" + (server != "" ? " (" + server + ")" : ""));
                var ip = Upnp.ExternalIp(loc);
                if (!string.IsNullOrEmpty(ip)) R(Upnp.IsPrivate(ip) ? "WARN" : "OK", "NAT", Upnp.IsPrivate(ip) ? "the router's internet address is private (" + ip + "): double NAT / carrier NAT" : "single NAT; the router has a public internet address");
            }

            string so, se;
            Ps.Run("$x=[ordered]@{}; " +
                   "$na=Get-NetAdapter -Name " + Ps.Q(Adapter.Name) + " -ErrorAction SilentlyContinue; $x.mac=[string]$na.MacAddress + '|' + [string]$na.PermanentAddress; " +
                   "$x.wifipower=((powercfg /query SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a | Select-String 'Current AC Power Setting Index') -replace '.*:\\s*','').Trim(); " +
                   "$x.aspm=((powercfg /query SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 | Select-String 'Current AC Power Setting Index') -replace '.*:\\s*','').Trim(); " +
                   "$x.autotune=((netsh int tcp show global | Select-String 'Auto-Tuning') -replace '.*:\\s*','').Trim(); " +
                   "$x.wfd=((Get-NetAdapter -IncludeHidden | Where-Object { $_.InterfaceDescription -like 'Microsoft Wi-Fi Direct Virtual Adapter*' } | ForEach-Object { [string]$_.AdminStatus }) -join ','); " +
                   "$x.qos=@(Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'AX210_WatchClaude_*' -and $_.Name -ne 'AX210_WatchClaude_UploadCap' }).Count; " +
                   "$x.Keys | ForEach-Object { $_ + '=' + $x[$_] }", 60, out so, out se);
            var kv = (so ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("=")).ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
            Func<string, string> K = k => { string v; return kv.TryGetValue(k, out v) ? v : ""; };
            var macs = K("mac").Split('|'); bool? random = macs.Length == 2 ? MacDiffers(macs[0], macs[1]) : null;
            R(random == false ? "OK" : random == true ? "WARN" : "INFO", "Hardware address", random == null ? "unknown" : random == false ? "this PC's own (the router always recognizes it, so per-device priority and reservations stick)"
                : "random on this network: router per-device priority/reservations may not recognize this PC" + (Cfg.FixedHardwareAddress && IsHome(c.Ssid) ? " (the watchdog turns it off when no game is running)" : ""));
            R(K("wifipower") == "0x00000000" ? "OK" : "WARN", "Wi-Fi power saving", K("wifipower") == "0x00000000" ? "maximum performance" : "not at maximum performance (" + K("wifipower") + ")");
            R(K("aspm") == "0x00000000" ? "OK" : "WARN", "PCIe power saving", K("aspm") == "0x00000000" ? "off" : "on (" + K("aspm") + ")");
            R(K("autotune").StartsWith("normal", StringComparison.OrdinalIgnoreCase) ? "OK" : "WARN", "TCP auto-tuning", K("autotune") == "" ? "unknown" : K("autotune"));
            R("INFO", "Wi-Fi Direct", K("wfd") == "" ? "not present" : K("wfd").Replace("Up", "on").Replace("Down", "paused") + (Cfg.SuspendWifiDirectWhileGaming ? " (paused automatically during games)" : ""));
            R(LowLatencyState.Contains("background scan off") && LowLatencyState.Contains("streaming mode on") ? "OK" : "WARN", "Low-latency mode", LowLatencyState == "" ? "not set" : LowLatencyState);
            var drift = ProfileDrift(St.CurrentMode); R(drift.Count == 0 ? "OK" : "WARN", "Adapter settings", drift.Count == 0 ? "optimized profile in place (" + Profiles.Name(St.CurrentMode) + ")" : "drifted: " + string.Join(", ", drift));
            int q; int.TryParse(K("qos"), out q); R(!Cfg.QosTagGame || q > 0 ? "OK" : "WARN", "Game traffic priority", Cfg.QosTagGame ? q + " rules (UDP only)" : "off");

            DriverStat ds; lock (StLock) St.DriverStats.TryGetValue(DriverVersion, out ds);
            R(CrashCount(24) == 0 ? "OK" : CrashCount(24) < 3 ? "WARN" : "FAIL", "Intel driver " + DriverVersion, string.Format("{0} crashes in 24 h, {1} in 7 days{2}; {3} saved for rollback", CrashCount(24), CrashCount(168),
                ds != null && ds.Hours > 1 ? string.Format(", {0:0.00}/hour on this version", ds.Crashes / ds.Hours) : "", DriverBackups().Count == 0 ? (ds != null && ds.BackedUp ? "this version" : "no copy") : DriverBackups().Count + " older version(s)"));
            var dnsCur = DnsTools.Current(); double dq = dnsCur.Count > 0 ? DnsTools.QueryMs(dnsCur[0], "www.google.com", 1500) : -1;
            R(dq >= 0 && dq < 60 ? "OK" : "WARN", "DNS", (dnsCur.Count == 0 ? "none" : string.Join(", ", dnsCur.Take(2))) + (dq >= 0 ? string.Format(" — {0:0} ms lookup", dq) : " — not answering"));
            CheckUdp(false); R(UdpInUse < Cfg.UdpWarnThreshold ? "OK" : "WARN", "Network ports (UDP)", UdpInUse + " in use" + (UdpTop != "" ? " · " + UdpTop : ""));
            int nrpt; lock (StLock) nrpt = St.Incidents.Count(i => i.Kind == "nrpt" && i.Time > DateTime.UtcNow.AddDays(-7));
            bool ts = Process.GetProcessesByName("tailscaled").Length > 0;
            R(nrpt == 0 ? (ts ? "INFO" : "OK") : "WARN", "DNS rules / Tailscale", (nrpt == 0 ? "no DNS-rules errors this week" : nrpt + " DNS-rules errors this week") + (ts ? "; Tailscale service is running" : ""));
            var q8 = AvoidSummary(); R("INFO", "Avoiding", q8 == "" ? "nothing" : q8);
            R("INFO", "Options", string.Format("upload limiter {0} · fast downloads on 6 GHz {1} · session reports {2} · download warnings {3} · streaming/VR as games {4} · mesh node hold {5}",
                Cfg.UploadLimiter ? "on" : "off", Cfg.TurboDownloads ? "on" : "off", Cfg.SessionReports ? "on" : "off", Cfg.BandwidthGuard ? "on" : "off", Cfg.StreamingAsGames ? "on" : "off", Cfg.MeshHold ? "on" : "off"));
            if (HasBattery) R("INFO", "Battery", Cfg.BatteryAware ? "laptop: Wi-Fi power saving stays normal on battery and is lifted while a game runs" : "laptop: Wi-Fi power saving off on battery too (battery-aware mode is off)");
            if (HomeNetworks.Count > 1) R("INFO", "Home networks", string.Join(", ", HomeNetworks));
            var head = string.Format("Network checkup — {0} OK, {1} warning{2}, {3} problem{4}", ok, warn, warn == 1 ? "" : "s", fail, fail == 1 ? "" : "s");
            Log.Write("TEST", head);
            return head + "\r\n\r\n" + string.Join("\r\n", lines);
        }
    }
}
