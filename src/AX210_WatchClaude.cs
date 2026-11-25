// =====================================================================================
//  AX210_WatchClaude — Wi-Fi watchdog for Intel AX210-family cards on Windows 11, for online gaming
//  Written by Claude (Anthropic). MIT licensed.
//
//  Build:    build.ps1  (app + installer)          Install: AX210_WatchClaude-Setup.exe
//  Data:     C:\ProgramData\AX210_WatchClaude   (config.json, state.json, status.json,
//            logs\watch.log, logs\benchmarks.log, samples\YYYY-MM-DD.csv)
//
//  What it does (see README.md for the why):
//   * Pins the card to one band at a time (driver settings) so router band-steering
//     can't bounce it around, and steers to the router's own access point (not an
//     extender) with WlanConnect + a desired-BSSID list.
//   * Avoids crash-prone 5 GHz channels (set up from your own crash history, plus any
//     channel that crashes repeatedly) by changing band; probes back to the preferred
//     band later. Every change is transactional: if the new setup doesn't connect, it rolls back.
//   * Keeps Windows' background Wi-Fi scans off and "media streaming mode" on while
//     connected (both only last while an app holds them).
//   * Watches the Intel driver's crash events live, decodes them, labels them, and
//     quarantines channels that crash repeatedly.
//   * Pings the router every second and the internet every 2 s; runs a recovery
//     ladder when the link drops; tells Wi-Fi problems apart from ISP outages.
//   * Re-applies the optimized adapter profile after driver updates.
//   * Benchmarks DNS weekly and on demand; on-demand latency/speed/band/stability tests.
//   * Watches UDP ephemeral port usage (Tcpip 4266 = Windows ran out of UDP ports).
//   * Recognizes games (any game folder you play from + ~40 popular online titles), defers
//     non-urgent changes while one runs, and gives each game's UDP traffic Wi-Fi priority.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("AX210_WatchClaude")]
[assembly: System.Reflection.AssemblyProduct("AX210_WatchClaude")]
[assembly: System.Reflection.AssemblyDescription("AX210_WatchClaude — Wi-Fi watchdog for gaming")]
[assembly: System.Reflection.AssemblyCompany("minimalice92")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.0.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.2")]

namespace AX210WatchClaude
{
    // ---------------------------------------------------------------- paths & json
    static class Paths
    {
        public const string App = "AX210_WatchClaude";
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), App);
        public static string Logs { get { return Path.Combine(Data, "logs"); } }
        public static string Samples { get { return Path.Combine(Data, "samples"); } }
        public static string ConfigFile { get { return Path.Combine(Data, "config.json"); } }
        public static string StateFile { get { return Path.Combine(Data, "state.json"); } }
        public static string StatusFile { get { return Path.Combine(Data, "status.json"); } }
        public static void Ensure() { Directory.CreateDirectory(Data); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Samples); }
    }

    static class Json
    {
        public static JavaScriptSerializer S() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        public static string Pretty(string json)
        {
            var sb = new StringBuilder(); int ind = 0; bool str = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (str) { sb.Append(c); if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); } else if (c == '"') str = false; continue; }
                switch (c)
                {
                    case '"': str = true; sb.Append(c); break;
                    case '{': case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        sb.Append(c).Append("\r\n").Append(' ', ++ind * 2); break;
                    case '}': case ']': sb.Append("\r\n").Append(' ', Math.Max(0, --ind) * 2).Append(c); break;
                    case ',': sb.Append(c).Append("\r\n").Append(' ', ind * 2); break;
                    case ':': sb.Append(": "); break;
                    default: if (!char.IsWhiteSpace(c)) sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
        public static bool ReadOnly;
        public static void WriteAtomic(string path, string text)
        {
            if (ReadOnly) return;
            var tmp = path + ".tmp"; File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }

    // ---------------------------------------------------------------- config (user-editable)
    public class Config
    {
        public string HomeSsid { get; set; } = "";
        // Access points that broadcast the home SSID but shouldn't be used (extenders/mesh nodes). Filled in by setup.
        public List<string> AvoidBssids { get; set; } = new List<string>();
        // 5 GHz channels never to sit on. Setup suggests the ones that crashed the driver repeatedly on this PC.
        public List<int> AvoidChannels5 { get; set; } = new List<int>();
        // Band preference. 5 GHz first: 6 GHz has more airtime but showed silent stalls on AX210 + current drivers.
        public List<string> BandOrder { get; set; } = new List<string> { "5", "6", "2.4" };
        public bool AutoActions { get; set; } = true;            // false = monitor only
        public bool DeferWhileGaming { get; set; } = true;
        public int ConfigVersion { get; set; }
        // Process names (no .exe) that always count as a game. Games in Steam/Battle.net/Epic/etc. folders are detected without being listed.
        public List<string> GameProcesses { get; set; } = new List<string> { "Overwatch" };
        public List<string> ExtraGameFolders { get; set; } = new List<string>();
        // Folders inside game libraries that are tools, not games.
        public List<string> IgnoreGameFolders { get; set; } = new List<string> { "wallpaper_engine", "Steamworks Shared", "Steam Controller Configs", "SteamVR", "Riot Client", "Launcher" };
        // Game executables that get Wi-Fi traffic priority from startup (others get it the first time they're played).
        public List<string> QosAlwaysExes { get; set; } = new List<string> { "Overwatch.exe" };
        // v3 extras
        public bool SuspendWifiDirectWhileGaming { get; set; } = true;   // stops off-channel discovery excursions during games
        public bool SessionReports { get; set; } = true;
        public bool BandwidthGuard { get; set; } = true;                 // warn when downloads/uploads run during a game
        public double GuardRxMbps { get; set; } = 40;
        public double GuardTxMbps { get; set; } = 15;
        public bool UploadLimiter { get; set; } = false;                 // cap this PC's non-game uploads below line rate
        public double UploadCapMbps { get; set; } = 0;                   // 0 = 90% of the last measured upload speed
        public bool TurboDownloads { get; set; } = false;                // big download + no game -> 6 GHz until it ends
        public double TurboRxMbps { get; set; } = 400;
        public bool LowLatencyMode { get; set; } = true;         // background scan off + streaming mode on
        public bool EnforceAdapterProfile { get; set; } = true;
        public bool AutoDns { get; set; } = true;
        public int DnsBenchmarkDays { get; set; } = 7;
        public bool QosTagGame { get; set; } = true;             // DSCP 46 / WMM voice for the game's uplink
        public string QosGameExe { get; set; } = "Overwatch.exe";
        public bool Notifications { get; set; } = true;
        public int SwitchDwellMinutes { get; set; } = 30;
        public int ProbePreferredHours { get; set; } = 3;
        public int QuarantineHours { get; set; } = 24;
        public int StallMs { get; set; } = 100;
        public string InternetTarget { get; set; } = "1.1.1.1";
        public int SampleSeconds { get; set; } = 5;
        public int KeepSampleDays { get; set; } = 14;
        public int UdpWarnThreshold { get; set; } = 8000;

        /// True when config.json couldn't be read. The app then runs on defaults and never writes over the file
        /// (a copy is kept as config.json.broken) until the user saves settings or reloads a fixed file.
        public static bool Broken;
        const int CurrentVersion = 3;
        public static Config Load()
        {
            Config c = null; bool save = false;
            try
            {
                if (File.Exists(Paths.ConfigFile)) c = Parse(File.ReadAllText(Paths.ConfigFile), out save);
                else { c = Parse(null, out save); save = true; }
                Broken = false;
            }
            catch (Exception ex)
            {
                Broken = true;
                try { File.Copy(Paths.ConfigFile, Paths.ConfigFile + ".broken", true); } catch { }
                Log.Write("ERROR", "config.json has a mistake (" + ex.Message + "). Running on default settings until it's fixed; the file is left as it is (copy: config.json.broken)");
                bool unused; return Parse(null, out unused);
            }
            if (save) c.Save();
            return c;
        }
        /// JSON (null = defaults) -> a usable config: repaired, then migrated to the current version. Throws on bad JSON.
        public static Config Parse(string json, out bool changed)
        {
            var c = json == null ? null : Json.S().Deserialize<Config>(json);
            if (c == null) { c = new Config(); foreach (var n in GameList.Popular) c.GameProcesses.Add(n); c.ConfigVersion = CurrentVersion; }
            changed = c.Sanitize();
            if (c.ConfigVersion < 2)   // v2: multi-game support
            {
                foreach (var n in GameList.Popular) if (!c.GameProcesses.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) c.GameProcesses.Add(n);
                c.ConfigVersion = 2; changed = true;
            }
            if (c.ConfigVersion < CurrentVersion) { c.ConfigVersion = CurrentVersion; changed = true; }   // v3: new options get their defaults written out
            return c;
        }
        /// Repairs what a hand edit can break: null lists/strings, unknown bands, and program names that can't be
        /// program names (quotes from "Copy as path", folders, illegal characters).
        public bool Sanitize()
        {
            bool changed = false;
            Func<List<string>, bool, List<string>> clean = (list, exe) =>
            {
                var r = new List<string>(); if (list == null) changed = true;
                foreach (var raw in list ?? new List<string>())
                {
                    var n = Names.CleanProgram(raw, exe); if (n != raw) changed = true;
                    if (n != null && !r.Contains(n, StringComparer.OrdinalIgnoreCase)) r.Add(n);
                }
                return r;
            };
            Func<List<string>, List<string>> plain = list => { if (list == null) changed = true; var r = (list ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); if (list != null && r.Count != list.Count) changed = true; return r; };
            QosAlwaysExes = clean(QosAlwaysExes, true); GameProcesses = clean(GameProcesses, false);
            ExtraGameFolders = plain(ExtraGameFolders); IgnoreGameFolders = plain(IgnoreGameFolders);
            AvoidBssids = plain(AvoidBssids).Select(x => x.ToLowerInvariant().Replace('-', ':')).Where(x => Regex.IsMatch(x, "^([0-9a-f]{2}:){5}[0-9a-f]{2}$")).ToList();
            if (AvoidChannels5 == null) { AvoidChannels5 = new List<int>(); changed = true; }
            var bands = (BandOrder ?? new List<string>()).Where(b => b == "5" || b == "6" || b == "2.4").Distinct().ToList();
            if (bands.Count == 0) bands = new List<string> { "5", "6", "2.4" };
            if (BandOrder == null || !bands.SequenceEqual(BandOrder)) { BandOrder = bands; changed = true; }
            if (HomeSsid == null) { HomeSsid = ""; changed = true; }
            if (string.IsNullOrWhiteSpace(InternetTarget)) { InternetTarget = "1.1.1.1"; changed = true; }
            return changed;
        }
        /// Writes config.json. Only explicit saves (Settings, Setup, Reload) may replace a broken file; everything
        /// else the app saves by itself is kept in memory until the file is fixed.
        public void Save(bool explicitSave = false)
        {
            if (Broken && !explicitSave) return;
            try { Json.WriteAtomic(Paths.ConfigFile, Json.Pretty(Json.S().Serialize(this))); Broken = false; } catch (Exception ex) { Log.Write("ERROR", "save config: " + ex.Message); }
        }
    }

    static class Names
    {
        static readonly char[] Bad = Path.GetInvalidFileNameChars();
        /// "C:\Games\Foo.exe" (with or without quotes) -> Foo.exe (exe=true) or Foo (exe=false); null if nothing usable is left.
        public static string CleanProgram(string raw, bool exe)
        {
            var s = (raw ?? "").Trim().Trim('"', '\'').Trim();
            int i = s.LastIndexOfAny(new[] { '\\', '/' }); if (i >= 0) s = s.Substring(i + 1);
            s = new string(s.Where(ch => Array.IndexOf(Bad, ch) < 0).ToArray()).Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4).TrimEnd();
            if (s.Length == 0) return null;
            return exe ? s + ".exe" : s;
        }
    }

    // ---------------------------------------------------------------- persisted state (learned)
    public class ChannelStat
    {
        public double Seconds { get; set; }
        public int Crashes { get; set; }
        public int Stalls { get; set; }
        public int HistCrashes { get; set; }
        public int HistAssociations { get; set; }
    }
    public class IncidentRec { public DateTime Time { get; set; } public string Kind { get; set; } public string Title { get; set; } public string Key { get; set; } public string Detail { get; set; } }

    public class State
    {
        public Dictionary<string, string> OriginalProps { get; set; } = new Dictionary<string, string>();
        public List<string> OriginalDns { get; set; } = new List<string>();
        public bool OriginalDnsSaved { get; set; }
        public Dictionary<string, string> LastKnownGood { get; set; } = new Dictionary<string, string>();
        public string CurrentMode { get; set; } = "";
        public Dictionary<string, DateTime> Quarantine { get; set; } = new Dictionary<string, DateTime>();
        public Dictionary<string, DateTime> ModeFailedUntil { get; set; } = new Dictionary<string, DateTime>();
        public Dictionary<string, ChannelStat> Channels { get; set; } = new Dictionary<string, ChannelStat>();
        public List<IncidentRec> Incidents { get; set; } = new List<IncidentRec>();
        public DateTime LastDnsBench { get; set; }
        public string DnsChoice { get; set; } = "";
        public string LastDriverVersion { get; set; } = "";
        public bool HistoryImported { get; set; }
        public DateTime LastSwitch { get; set; }
        public DateTime NextProbe { get; set; }
        public string HomeProfile { get; set; } = "";
        public string Router6Bssid { get; set; } = "";
        // bssid -> band, learned live (used to attribute old crash events to a band)
        public Dictionary<string, string> KnownBands { get; set; } = new Dictionary<string, string>();
        public bool SetupDone { get; set; }
        public Dictionary<string, string> GameQos { get; set; } = new Dictionary<string, string>();   // exe path or name -> QoS policy name
        public Dictionary<string, GameRec> Games { get; set; } = new Dictionary<string, GameRec>();
        public List<SessionRec> Sessions { get; set; } = new List<SessionRec>();
        public Dictionary<string, DriverStat> DriverStats { get; set; } = new Dictionary<string, DriverStat>();
        public List<string> WifiDirectDisabled { get; set; } = new List<string>();
        public bool TurboActive { get; set; }
        public double LastDownloadMbps { get; set; }
        public double LastUploadMbps { get; set; }
        public string DriverHardwareId { get; set; } = "";

        /// The pre-app adapter/DNS/power values were lost (state.json was damaged). The current values are probably the app's
        /// own, so they're never recorded as "original"; a restore resets to driver/Windows defaults instead.
        public bool OriginalsUnknown { get; set; }
        public Dictionary<string, string> OriginalSystem { get; set; } = new Dictionary<string, string>();   // power/QoS values from before Harden

        public static State Load()
        {
            try { if (File.Exists(Paths.StateFile)) return Json.S().Deserialize<State>(File.ReadAllText(Paths.StateFile)) ?? new State(); }
            catch (Exception ex)
            {
                try { File.Copy(Paths.StateFile, Paths.StateFile + ".broken", true); } catch { }
                Log.Write("ERROR", "state.json was damaged (" + ex.Message + "); starting fresh (copy: state.json.broken). Original settings are unknown, so a restore will use driver defaults");
                return new State { OriginalsUnknown = true, OriginalDnsSaved = true, SetupDone = true };
            }
            return new State();
        }
        public void Save() { try { Json.WriteAtomic(Paths.StateFile, Json.Pretty(Json.S().Serialize(this))); } catch (Exception ex) { Log.Write("ERROR", "save state: " + ex.Message); } }
    }

    // ---------------------------------------------------------------- logging
    static class Log
    {
        static readonly object L = new object();
        static readonly List<string[]> recent = new List<string[]>();
        public static int Version;
        public static bool Silent;
        /// Shows the previous run's events after a restart (the last ~300 lines of watch.log).
        public static void LoadRecent()
        {
            try
            {
                var f = Path.Combine(Paths.Logs, "watch.log"); if (!File.Exists(f)) return; string text;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { if (fs.Length > 120000) fs.Seek(-120000, SeekOrigin.End); using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd(); }
                var items = new List<string[]>();
                foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Reverse().Take(300).Reverse())
                {
                    var m = Regex.Match(line, @"^\d{4}-(\d\d-\d\d \d\d:\d\d:\d\d) \[([A-Z]+)\] (.*)$");
                    if (m.Success && m.Groups[2].Value != "DEBUG") items.Add(new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value });
                }
                lock (L) { recent.InsertRange(0, items); if (recent.Count > 400) recent.RemoveRange(0, recent.Count - 400); Version++; }
            }
            catch { }
        }
        public static void Write(string type, string msg, bool show = true)
        {
            if (Silent) return;
            var now = DateTime.Now;
            lock (L)
            {
                try
                {
                    var f = Path.Combine(Paths.Logs, "watch.log");
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 5000000) { var old = f + ".1"; if (File.Exists(old)) File.Delete(old); File.Move(f, old); }
                    File.AppendAllText(f, now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + type + "] " + msg + Environment.NewLine);
                }
                catch { }
                if (show && type != "DEBUG") { recent.Add(new[] { now.ToString("MM-dd HH:mm:ss"), type, msg }); if (recent.Count > 400) recent.RemoveAt(0); Version++; }
            }
        }
        public static string[][] Recent() { lock (L) { var a = recent.ToArray(); Array.Reverse(a); return a; } }
        public static void Bench(string title, string text)
        {
            lock (L) { try { File.AppendAllText(Path.Combine(Paths.Logs, "benchmarks.log"), "===== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + title + " =====\r\n" + text + "\r\n\r\n"); } catch { } }
        }
    }

    // ---------------------------------------------------------------- bands
    static class Bands
    {
        public static string FromFreq(int mhz) { return mhz >= 5925 ? "6" : mhz >= 4900 ? "5" : mhz > 0 ? "2.4" : "?"; }
        public static int ChannelFromFreq(int mhz)
        {
            if (mhz >= 5925) return (mhz - 5950) / 5;
            if (mhz >= 4900) return (mhz - 5000) / 5;
            if (mhz == 2484) return 14;
            if (mhz >= 2412) return (mhz - 2407) / 5;
            return 0;
        }
        static readonly HashSet<int> Ch5 = new HashSet<int>(new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165, 169, 173, 177 });
        static bool Is6Channel(int ch) { return ch >= 1 && ch <= 233 && ch % 4 == 1; }   // 6 GHz 20 MHz channels: 1, 5, 9 … 233
        /// Band from a bare channel number, or "?" when the number exists in two bands (6 GHz reuses 1/5/9/13 and 149–177).
        public static HashSet<string> Candidates(int ch)
        {
            var r = new HashSet<string>(); if (ch >= 1 && ch <= 14) r.Add("2.4"); if (Ch5.Contains(ch)) r.Add("5"); if (Is6Channel(ch)) r.Add("6"); return r;
        }
        public static string FromChannel(int ch)
        {
            bool b24 = ch >= 1 && ch <= 14, b5 = Ch5.Contains(ch), b6 = Is6Channel(ch);
            int n = (b24 ? 1 : 0) + (b5 ? 1 : 0) + (b6 ? 1 : 0);
            if (n != 1) return "?";
            return b24 ? "2.4" : b5 ? "5" : "6";
        }
        public static string Guess(string bssid, int ch, Dictionary<string, string> known)
        {
            string b; if (bssid != null && known != null && known.TryGetValue(bssid, out b) && b != "?") return b;
            return FromChannel(ch);
        }
    }

    // ---------------------------------------------------------------- Native Wi-Fi API
    static class WlanNative
    {
        [DllImport("wlanapi.dll")] public static extern uint WlanOpenHandle(uint v, IntPtr r, out uint nv, out IntPtr h);
        [DllImport("wlanapi.dll")] public static extern uint WlanCloseHandle(IntPtr h, IntPtr r);
        [DllImport("wlanapi.dll")] public static extern uint WlanEnumInterfaces(IntPtr h, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern void WlanFreeMemory(IntPtr p);
        [DllImport("wlanapi.dll")] public static extern uint WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr r, out int size, out IntPtr data, IntPtr vt);
        [DllImport("wlanapi.dll")] public static extern uint WlanSetInterface(IntPtr h, ref Guid g, int op, int size, IntPtr data, IntPtr r);
        [DllImport("wlanapi.dll")] public static extern uint WlanScan(IntPtr h, ref Guid g, IntPtr ssid, IntPtr ie, IntPtr r);
        [DllImport("wlanapi.dll")] public static extern uint WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr ssid, int bssType, bool sec, IntPtr r, out IntPtr list);
        [DllImport("wlanapi.dll")] public static extern uint WlanConnect(IntPtr h, ref Guid g, ref WLAN_CONNECTION_PARAMETERS p, IntPtr r);
        [DllImport("wlanapi.dll")] public static extern uint WlanDisconnect(IntPtr h, ref Guid g, IntPtr r);
        public delegate void NotificationCallback(IntPtr data, IntPtr context);
        [DllImport("wlanapi.dll")] public static extern uint WlanRegisterNotification(IntPtr h, uint source, bool ignoreDup, NotificationCallback cb, IntPtr ctx, IntPtr r, out uint prev);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_CONNECTION_PARAMETERS
        {
            public int wlanConnectionMode;
            [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public int dot11BssType;
            public uint dwFlags;
        }
        public const int OpInterfaceState = 6, OpCurrentConnection = 7, OpChannel = 8, OpBackgroundScan = 2, OpStreamingMode = 3, OpRssi = 0x10000102;
    }

    public class ConnInfo
    {
        public bool Connected; public int IfState; public string Profile = "", Ssid = "", Bssid = "", Band = "";
        public int Phy, Quality, RxMbps, TxMbps, Channel, Rssi, FreqMHz;
        public string Key { get { return Band + "|" + Channel; } }
    }
    public class Bss { public string Ssid, Bssid, Band; public int Rssi, FreqMHz, Channel, Phy; }

    class WlanClient : IDisposable
    {
        IntPtr h; public Guid Iface; public bool Ok; public string Description = "";
        /// Band for a BSSID the app has seen before (the watchdog plugs in its learned map).
        public Func<string, string> BandHint;
        /// Bands the adapter's current band lock allows (null = any). In 5 GHz-only mode a channel-149 link must be 5 GHz.
        public Func<HashSet<string>> AllowedBands;
        readonly Dictionary<string, DateTime> netshMiss = new Dictionary<string, DateTime>();
        WlanNative.NotificationCallback cb;      // kept alive for the native side
        public event Action<uint, uint> Notification;

        public WlanClient()
        {
            uint nv;
            if (WlanNative.WlanOpenHandle(2, IntPtr.Zero, out nv, out h) != 0) return;
            IntPtr list;
            if (WlanNative.WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return;
            try
            {
                int n = Marshal.ReadInt32(list, 0), best = -1;
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = list + 8 + i * 532;
                    var g = (Guid)Marshal.PtrToStructure(e, typeof(Guid));
                    string d = Marshal.PtrToStringUni(e + 16) ?? "";
                    int score = Score(d) * 2 + (Marshal.ReadInt32(e, 528) == 1 ? 1 : 0);   // prefer an Intel AX2xx card, then a connected one
                    if (score > best) { best = score; Iface = g; Description = d; }
                }
                Ok = n > 0;
            }
            finally { WlanNative.WlanFreeMemory(list); }
            cb = (data, ctx) =>
            {
                try { var src = (uint)Marshal.ReadInt32(data, 0); var code = (uint)Marshal.ReadInt32(data, 4); var ev = Notification; if (ev != null) ev(src, code); } catch { }
            };
            uint prev; WlanNative.WlanRegisterNotification(h, 0x8 | 0x10, true, cb, IntPtr.Zero, IntPtr.Zero, out prev);
        }

        static int Score(string d)
        {
            if (Regex.IsMatch(d, @"\bAX21[01]\b|\bAX411\b", RegexOptions.IgnoreCase)) return 4;
            if (Regex.IsMatch(d, @"Intel.*\b(AX\d{3}|BE\d{3})\b", RegexOptions.IgnoreCase)) return 3;
            if (d.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return d.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf("Direct", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
        }
        static string Mac(IntPtr p, int off) { var sb = new StringBuilder(17); for (int i = 0; i < 6; i++) { if (i > 0) sb.Append(':'); sb.Append(Marshal.ReadByte(p, off + i).ToString("x2")); } return sb.ToString(); }
        static string Ssid(IntPtr p, int off) { int len = Math.Min(Math.Max(Marshal.ReadInt32(p, off), 0), 32); var b = new byte[len]; Marshal.Copy(p + off + 4, b, 0, len); return Encoding.UTF8.GetString(b); }
        public static byte[] ParseMac(string s) { return s.Split(':', '-').Select(x => Convert.ToByte(x, 16)).ToArray(); }

        public bool Query(int op, out int val)
        {
            val = 0; if (!Ok) return false; int sz; IntPtr d;
            if (WlanNative.WlanQueryInterface(h, ref Iface, op, IntPtr.Zero, out sz, out d, IntPtr.Zero) != 0) return false;
            try { val = Marshal.ReadInt32(d, 0); } finally { WlanNative.WlanFreeMemory(d); }
            return true;
        }
        public uint SetBool(int op, bool v)
        {
            IntPtr p = Marshal.AllocHGlobal(4);
            try { Marshal.WriteInt32(p, v ? 1 : 0); return WlanNative.WlanSetInterface(h, ref Iface, op, 4, p, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(p); }
        }

        public ConnInfo GetConnection()
        {
            var c = new ConnInfo(); if (!Ok) return c;
            int st; if (Query(WlanNative.OpInterfaceState, out st)) c.IfState = st;
            int sz; IntPtr d;
            if (WlanNative.WlanQueryInterface(h, ref Iface, WlanNative.OpCurrentConnection, IntPtr.Zero, out sz, out d, IntPtr.Zero) == 0)
            {
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES offsets (verified on this PC): state 0, profile 8, ssid 520, bssid 560, phy 568, quality 576, rx 580, tx 584 (kbps)
                    c.IfState = Marshal.ReadInt32(d, 0); c.Profile = Marshal.PtrToStringUni(d + 8); c.Ssid = Ssid(d, 520); c.Bssid = Mac(d, 560);
                    c.Phy = Marshal.ReadInt32(d, 568); c.Quality = Marshal.ReadInt32(d, 576); c.RxMbps = Marshal.ReadInt32(d, 580) / 1000; c.TxMbps = Marshal.ReadInt32(d, 584) / 1000;
                }
                finally { WlanNative.WlanFreeMemory(d); }
                c.Connected = c.IfState == 1;
            }
            if (c.Connected)
            {
                int ch, rssi;
                if (Query(WlanNative.OpChannel, out ch)) c.Channel = ch;
                if (Query(WlanNative.OpRssi, out rssi)) c.Rssi = rssi;
                var b = GetBssList().FirstOrDefault(x => x.Bssid == c.Bssid);
                if (b != null) { c.FreqMHz = b.FreqMHz; c.Band = b.Band; if (c.Channel == 0) c.Channel = b.Channel; }
                else
                {
                    string hint = BandHint == null ? null : BandHint(c.Bssid);
                    if (!string.IsNullOrEmpty(hint) && hint != "?") c.Band = hint;
                    else
                    {
                        var cands = Bands.Candidates(c.Channel); var allowed = AllowedBands == null ? null : AllowedBands();
                        if (allowed != null && allowed.Count > 0) cands.IntersectWith(allowed);
                        c.Band = cands.Count == 1 ? cands.First() : NetshBandCached(c.Bssid) ?? "?";
                    }
                }
            }
            return c;
        }

        public List<Bss> GetBssList()
        {
            var r = new List<Bss>(); if (!Ok) return r; IntPtr bl;
            if (WlanNative.WlanGetNetworkBssList(h, ref Iface, IntPtr.Zero, 3, false, IntPtr.Zero, out bl) != 0) return r;
            try
            {
                int n = Marshal.ReadInt32(bl, 4);
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = bl + 8 + i * 360;   // WLAN_BSS_ENTRY is 360 bytes (verified)
                    var b = new Bss { Ssid = Ssid(e, 0), Bssid = Mac(e, 40), Phy = Marshal.ReadInt32(e, 52), Rssi = Marshal.ReadInt32(e, 56), FreqMHz = (int)((uint)Marshal.ReadInt32(e, 92) / 1000) };
                    b.Band = Bands.FromFreq(b.FreqMHz); b.Channel = Bands.ChannelFromFreq(b.FreqMHz); r.Add(b);
                }
            }
            finally { WlanNative.WlanFreeMemory(bl); }
            return r;
        }

        /// "Band : 6 GHz" from netsh (Windows 11, English UI); null if it can't be read.
        string NetshBandCached(string bssid)
        {
            DateTime until;
            lock (netshMiss) if (bssid != null && netshMiss.TryGetValue(bssid, out until) && until > DateTime.UtcNow) return null;
            var r = NetshBand();
            if (r == null && bssid != null) lock (netshMiss) netshMiss[bssid] = DateTime.UtcNow.AddMinutes(10);   // don't spawn netsh every tick
            return r;
        }
        static string NetshBand()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                string text;
                using (var p = Process.Start(psi))
                {
                    var read = p.StandardOutput.ReadToEndAsync();   // a stalled WLAN service can hang netsh: give up after 3 s
                    if (!read.Wait(3000)) { try { p.Kill(); } catch { } return null; }
                    text = read.Result;
                }
                var m = Regex.Match(text, @"^\s*Band\s*:\s*([\d.]+)\s*GHz", RegexOptions.Multiline);
                return m.Success ? (m.Groups[1].Value.StartsWith("2") ? "2.4" : m.Groups[1].Value.StartsWith("5") ? "5" : m.Groups[1].Value.StartsWith("6") ? "6" : null) : null;
            }
            catch { return null; }
        }

        public uint Scan() { return Ok ? WlanNative.WlanScan(h, ref Iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) : 1; }
        public uint Disconnect() { return Ok ? WlanNative.WlanDisconnect(h, ref Iface, IntPtr.Zero) : 1; }

        /// Connect using a saved profile, optionally restricted to specific BSSIDs.
        public uint Connect(string profile, IList<string> bssids)
        {
            if (!Ok || string.IsNullOrEmpty(profile)) return 1;
            var p = new WlanNative.WLAN_CONNECTION_PARAMETERS { wlanConnectionMode = 0, strProfile = profile, dot11BssType = 1 };
            IntPtr list = IntPtr.Zero;
            try
            {
                if (bssids != null && bssids.Count > 0)
                {
                    int n = bssids.Count, size = Math.Max(12 + 6 * n, 20);
                    list = Marshal.AllocHGlobal(size);
                    for (int i = 0; i < size; i++) Marshal.WriteByte(list, i, 0);
                    Marshal.WriteByte(list, 0, 0x80); Marshal.WriteByte(list, 1, 1); Marshal.WriteInt16(list, 2, 20); // NDIS header: default type, rev 1, sizeof(DOT11_BSSID_LIST)
                    Marshal.WriteInt32(list, 4, n); Marshal.WriteInt32(list, 8, n);
                    for (int i = 0; i < n; i++) { var mac = ParseMac(bssids[i]); for (int k = 0; k < 6; k++) Marshal.WriteByte(list, 12 + i * 6 + k, mac[k]); }
                    p.pDesiredBssidList = list;
                }
                uint rc = WlanNative.WlanConnect(h, ref Iface, ref p, IntPtr.Zero);
                if (rc == 87 && list != IntPtr.Zero) { Marshal.WriteInt16(list, 2, 18); rc = WlanNative.WlanConnect(h, ref Iface, ref p, IntPtr.Zero); }
                return rc;
            }
            finally { if (list != IntPtr.Zero) Marshal.FreeHGlobal(list); }
        }

        public void Dispose() { if (h != IntPtr.Zero) { WlanNative.WlanCloseHandle(h, IntPtr.Zero); h = IntPtr.Zero; } }
    }

    // ---------------------------------------------------------------- PowerShell helper (admin actions)
    static class Ps
    {
        public static bool Run(string script, int timeoutSec, out string output)
        {
            string so, se; bool ok = Run(script, timeoutSec, out so, out se); output = ((so ?? "") + "\n" + (se ?? "")).Trim(); return ok;
        }
        public static bool Run(string script, int timeoutSec, out string stdout, out string stderr)
        {
            stdout = ""; stderr = "";
            try
            {
                script = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Continue';" + script;
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(); } catch { } stderr = "timeout"; return false; }
                    stdout = so.Result; stderr = se.Result;
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { stderr = ex.Message; return false; }
        }
        public static string Q(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }
    }

    // ---------------------------------------------------------------- adapter settings
    static class Profiles
    {
        // Applied in every mode. Values verified against this driver's valid registry values.
        public static readonly Dictionary<string, string> Base = new Dictionary<string, string> {
            {"RoamAggressiveness","0"},        // 1. Lowest — stationary desktop, don't go looking
            {"ThroughputBoosterEnabled","0"},  // Intel default; bursting hogs airtime
            {"CtsToItself","1"},               // CTS-to-self (Intel default) instead of full RTS/CTS
            {"*PacketCoalescing","0"},         // deliver packets immediately
            {"MIMOPowerSaveMode","3"},         // No SMPS
            {"uAPSDSupport","0"},              // no U-APSD power save
            {"IEEE11nMode","3"},               // 802.11ax
            {"IbssTxPower","100"},             // highest transmit power
        };
        public static readonly string[] ModeKeys = { "WirelessMode", "Is6GhzBandSupported", "RoamingPreferredBandType" };
        public static IEnumerable<string> AllKeys { get { return Base.Keys.Concat(ModeKeys); } }
        static Dictionary<string, string> D(string wm, string s6, string pref) { return new Dictionary<string, string> { { "WirelessMode", wm }, { "Is6GhzBandSupported", s6 }, { "RoamingPreferredBandType", pref } }; }
        // WirelessMode: 17 = 5 GHz 802.11a only, 32 = 2.4 GHz b/g only. Pref: 1=2.4, 2=5, 3=6.
        public static Dictionary<string, string> ModeProps(string mode)
        {
            switch (mode)
            {
                case "6": return D("32", "1", "3");    // 6 GHz with 5 GHz locked out (2.4 kept as the driver's own fallback)
                case "6b": return D("17", "1", "3");   // 6 GHz preferred, 5 GHz allowed (used only if the strict form fails)
                case "2.4": return D("32", "0", "1");
                default: return D("17", "0", "2");     // "5": 5 GHz only
            }
        }
        public static Dictionary<string, string> Target(string mode)
        {
            var d = new Dictionary<string, string>(Base); foreach (var kv in ModeProps(mode)) d[kv.Key] = kv.Value; return d;
        }
        public static string BandOf(string mode) { return mode == "6b" ? "6" : string.IsNullOrEmpty(mode) ? "5" : mode; }
        public static string InferMode(Dictionary<string, string> p)
        {
            string wm, s6; p.TryGetValue("WirelessMode", out wm); p.TryGetValue("Is6GhzBandSupported", out s6);
            if (s6 == "1") return wm == "32" ? "6" : "6b";
            if (wm == "32" || wm == "16" || wm == "0") return "2.4";
            return "5";
        }
        public static string Name(string mode) { return BandOf(mode) + " GHz"; }
    }

    static class Adapter
    {
        public static string Name = "Wi-Fi"; public static Guid Guid; static string classKey;
        const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        public static void Init(Guid g)
        {
            Guid = g; var id = g.ToString("B");
            try { var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)); if (ni != null) Name = ni.Name; } catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(ClassRoot))
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        try { using (var s = k.OpenSubKey(sub)) { var v = s == null ? null : s.GetValue("NetCfgInstanceId") as string; if (v != null && string.Equals(v, id, StringComparison.OrdinalIgnoreCase)) { classKey = ClassRoot + "\\" + sub; break; } } }
                        catch { }
                    }
            }
            catch { }
        }
        public static bool Found { get { return classKey != null; } }
        public static string Description { get { try { using (var k = Registry.LocalMachine.OpenSubKey(classKey)) return k.GetValue("DriverDesc") as string ?? ""; } catch { return ""; } } }
        public static bool IsIntel
        {
            get
            {
                try { using (var k = Registry.LocalMachine.OpenSubKey(classKey)) { var p = (k.GetValue("ProviderName") as string ?? "") + " " + (k.GetValue("DriverDesc") as string ?? ""); return p.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0; } }
                catch { return false; }
            }
        }
        /// True if the driver exposes this advanced property (Ndi\Params\<keyword>).
        public static bool Has(string keyword)
        {
            if (classKey == null) return false;
            try { using (var k = Registry.LocalMachine.OpenSubKey(classKey + @"\Ndi\Params\" + keyword)) return k != null; } catch { return false; }
        }
        /// Allowed values of an enum advanced property (empty if not an enum / not present).
        public static HashSet<string> EnumValues(string keyword)
        {
            var r = new HashSet<string>(); if (classKey == null) return r;
            try { using (var k = Registry.LocalMachine.OpenSubKey(classKey + @"\Ndi\Params\" + keyword + @"\enum")) if (k != null) foreach (var v in k.GetValueNames()) r.Add(v); } catch { }
            return r;
        }
        /// Band locking needs the Intel "Wireless Mode" property with 5 GHz-only (17) and 2.4 GHz-only (32) values.
        public static bool CanLockBands { get { if (!IsIntel) return false; var v = EnumValues("WirelessMode"); return v.Contains("17") && v.Contains("32"); } }
        public static bool Supports6 { get { return Has("Is6GhzBandSupported"); } }
        /// The keywords this driver has, with values it accepts (enum keywords: the value must be one of its choices).
        public static Dictionary<string, string> Supported(Dictionary<string, string> props)
        {
            if (classKey == null) return props;
            return props.Where(kv => Has(kv.Key) && (kv.Value == null || EnumValues(kv.Key).Count == 0 || EnumValues(kv.Key).Contains(kv.Value))).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        /// The driver's default for a keyword (what Reset-NetAdapterAdvancedProperty writes), or null.
        public static string DefaultValue(string keyword)
        {
            if (classKey == null) return null;
            try { using (var k = Registry.LocalMachine.OpenSubKey(classKey + @"\Ndi\Params\" + keyword)) { var v = k == null ? null : k.GetValue("default"); return v == null ? null : v.ToString(); } } catch { return null; }
        }
        public static Dictionary<string, string> Read(IEnumerable<string> keys)
        {
            var d = new Dictionary<string, string>(); if (classKey == null) return d;
            try { using (var k = Registry.LocalMachine.OpenSubKey(classKey)) foreach (var key in keys) { var v = k == null ? null : k.GetValue(key); d[key] = v == null ? null : v.ToString(); } } catch { }
            return d;
        }
        public static string DriverVersion() { try { using (var k = Registry.LocalMachine.OpenSubKey(classKey)) return k.GetValue("DriverVersion") as string ?? "?"; } catch { return "?"; } }
        public static bool Apply(Dictionary<string, string> props, bool restart, out string output)
        {
            var sb = new StringBuilder();
            foreach (var kv in Supported(props).Where(x => x.Value != null))
                sb.Append("Set-NetAdapterAdvancedProperty -Name " + Ps.Q(Name) + " -RegistryKeyword " + Ps.Q(kv.Key) + " -RegistryValue " + Ps.Q(kv.Value) + " -NoRestart -ErrorAction Continue;");
            if (restart) sb.Append("Restart-NetAdapter -Name " + Ps.Q(Name) + " -Confirm:$false;");
            return Ps.Run(sb.ToString(), 120, out output);
        }
        /// Puts saved values back. A null value means the keyword had no value of its own before, so it's reset to the driver default
        /// (skipping it would leave the app's value in place).
        public static bool Restore(Dictionary<string, string> props, out string output)
        {
            var sb = new StringBuilder();
            foreach (var kv in props)
            {
                if (classKey != null && !Has(kv.Key)) continue;
                if (kv.Value != null) sb.Append("Set-NetAdapterAdvancedProperty -Name " + Ps.Q(Name) + " -RegistryKeyword " + Ps.Q(kv.Key) + " -RegistryValue " + Ps.Q(kv.Value) + " -NoRestart -ErrorAction Continue;");
                else sb.Append("Reset-NetAdapterAdvancedProperty -Name " + Ps.Q(Name) + " -RegistryKeyword " + Ps.Q(kv.Key) + " -NoRestart -ErrorAction Continue;");
            }
            output = ""; if (sb.Length == 0) return true;
            sb.Append("Restart-NetAdapter -Name " + Ps.Q(Name) + " -Confirm:$false;");
            return Ps.Run(sb.ToString(), 120, out output);
        }
        public static bool Restart() { string o; return Ps.Run("Restart-NetAdapter -Name " + Ps.Q(Name) + " -Confirm:$false", 90, out o); }
        public static string Gateway()
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, Guid.ToString("B"), StringComparison.OrdinalIgnoreCase));
                if (ni == null) return null;
                var gw = ni.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                return gw == null ? null : gw.ToString();
            }
            catch { return null; }
        }
        public static int IfIndex()
        {
            try { var ni = NetworkInterface.GetAllNetworkInterfaces().First(n => string.Equals(n.Id, Guid.ToString("B"), StringComparison.OrdinalIgnoreCase)); return ni.GetIPProperties().GetIPv4Properties().Index; } catch { return -1; }
        }
    }

    // ---------------------------------------------------------------- pings
    public class PingSample { public DateTime T; public int Gw; public int Net; }   // ms; -1 = lost, -2 = not sent
    public class PingStats
    {
        public int GwN, GwLost, NetN, NetLost, Stalls; public double GwP50, GwP95, GwMax, NetP50, NetP95, NetMax;
        public double GwLossPct { get { return GwN == 0 ? 0 : 100.0 * GwLost / GwN; } }
        public double NetLossPct { get { return NetN == 0 ? 0 : 100.0 * NetLost / NetN; } }
    }

    class PingMonitor
    {
        readonly object L = new object(); readonly List<PingSample> buf = new List<PingSample>();
        public volatile string Gateway; public string Internet = "1.1.1.1"; public int StallMs = 100;
        public DateTime LastGwOk = DateTime.MinValue, LastNetOk = DateTime.MinValue, Started = DateTime.Now;
        public void Start() { new Thread(Run) { IsBackground = true, Name = "pinger", Priority = ThreadPriority.BelowNormal }.Start(); }
        void Run()
        {
            Ping pg = new Ping(), pn = new Ping(); var data = new byte[32]; var opt = new PingOptions(64, true); long tick = 0;
            while (true)
            {
                var t0 = DateTime.Now; var s = new PingSample { T = t0, Gw = -2, Net = -2 };
                Task<PingReply> tg = null, tn = null;
                try { var g = Gateway; if (!string.IsNullOrEmpty(g)) tg = pg.SendPingAsync(g, 1000, data, opt); } catch { pg = new Ping(); }
                try { if (tick % 2 == 0) tn = pn.SendPingAsync(Internet, 1000, data, opt); } catch { pn = new Ping(); }
                s.Gw = Result(tg, ref pg); s.Net = Result(tn, ref pn);
                if (s.Gw >= 0) LastGwOk = DateTime.Now; if (s.Net >= 0) LastNetOk = DateTime.Now;
                if (tick > 0) lock (L) { buf.Add(s); if (buf.Count > 3700) buf.RemoveRange(0, buf.Count - 3600); }
                tick++;
                int wait = 1000 - (int)(DateTime.Now - t0).TotalMilliseconds; if (wait > 0) Thread.Sleep(wait);
            }
        }
        static int Result(Task<PingReply> t, ref Ping p)
        {
            if (t == null) return -2;
            try { if (!t.Wait(1500)) { p = new Ping(); return -1; } var r = t.Result; return r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1; }
            catch { p = new Ping(); return -1; }
        }
        public List<PingSample> Recent(int seconds) { var since = DateTime.Now.AddSeconds(-seconds); lock (L) return buf.Where(x => x.T >= since).ToList(); }
        public PingStats Stats(int seconds) { return Compute(Recent(seconds), StallMs); }
        public int StallsSince(DateTime t) { lock (L) return buf.Count(x => x.T > t && x.Gw != -2 && (x.Gw < 0 || x.Gw >= StallMs)); }
        public static double Pct(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0; int i = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1; return sorted[Math.Max(0, Math.Min(sorted.Count - 1, i))];
        }
        public static PingStats Compute(List<PingSample> s, int stallMs)
        {
            var st = new PingStats(); var g = new List<double>(); var n = new List<double>();
            foreach (var x in s)
            {
                if (x.Gw != -2) { st.GwN++; if (x.Gw < 0) st.GwLost++; else g.Add(x.Gw); if (x.Gw < 0 || x.Gw >= stallMs) st.Stalls++; }
                if (x.Net != -2) { st.NetN++; if (x.Net < 0) st.NetLost++; else n.Add(x.Net); }
            }
            g.Sort(); n.Sort();
            st.GwP50 = Pct(g, 50); st.GwP95 = Pct(g, 95); st.GwMax = g.Count > 0 ? g[g.Count - 1] : 0;
            st.NetP50 = Pct(n, 50); st.NetP95 = Pct(n, 95); st.NetMax = n.Count > 0 ? n[n.Count - 1] : 0;
            return st;
        }
    }

    // ---------------------------------------------------------------- crash decoding
    public class Decoded { public uint Id, Address; public string Name = "", Title = "", Plain = "", Detail = "", Known = ""; }

    static class Decoder
    {
        // Linux iwlwifi fw/img.c advanced_lookup[]; lookup masks the top nibble (FW_SYSASSERT_CPU_MASK).
        static readonly Dictionary<uint, string> Adv = new Dictionary<uint, string> {
            {0x34,"NMI_INTERRUPT_WDG"},{0x35,"SYSASSERT"},{0x37,"UCODE_VERSION_MISMATCH"},{0x38,"BAD_COMMAND"},{0x39,"BAD_COMMAND"},
            {0x3C,"NMI_INTERRUPT_DATA_ACTION_PT"},{0x3D,"FATAL_ERROR"},{0x46,"NMI_TRM_HW_ERR"},{0x4C,"NMI_INTERRUPT_TRM"},
            {0x54,"NMI_INTERRUPT_BREAK_POINT"},{0x5C,"NMI_INTERRUPT_WDG_RXF_FULL"},{0x64,"NMI_INTERRUPT_WDG_NO_RBD_RXF_FULL"},
            {0x66,"NMI_INTERRUPT_HOST"},{0x70,"NMI_INTERRUPT_LMAC_FATAL"},{0x71,"NMI_INTERRUPT_UMAC_FATAL"},
            {0x73,"NMI_INTERRUPT_OTHER_LMAC_FATAL"},{0x7C,"NMI_INTERRUPT_ACTION_PT"},{0x84,"NMI_INTERRUPT_UNKNOWN"},
            {0x86,"NMI_INTERRUPT_INST_ACTION_PT"},{0x88,"NMI_INTERRUPT_PREG"},{0x0010070D,"PNVM_MISSING"} };
        static readonly HashSet<uint> KnownHangAddrs = new HashSet<uint> { 0x804A612A, 0x804A6042, 0x804A6002, 0x804A6026 };

        public static string ErrorName(uint id) { string n; return Adv.TryGetValue(id & 0x0FFFFFFF, out n) ? n : "ADVANCED_SYSASSERT"; }
        public static string Plain(string name)
        {
            if (name == "NMI_INTERRUPT_HOST") return "The Wi-Fi chip stopped responding and the driver forced a reset.";
            if (name == "ADVANCED_SYSASSERT" || name == "SYSASSERT") return "The Wi-Fi chip's firmware detected an internal error and reset.";
            if (name.Contains("WDG")) return "The Wi-Fi chip's watchdog fired and it reset.";
            if (name.Contains("FATAL")) return "The Wi-Fi chip hit a fatal error and reset.";
            if (name == "PNVM_MISSING") return "Firmware data (PNVM) is missing; reinstall the Intel driver.";
            return "The Wi-Fi chip's firmware failed and reset.";
        }
        public static byte[] HexFromXml(string xml)
        {
            var m = Regex.Match(xml ?? "", "<Binary>([0-9A-Fa-f]+)</Binary>"); if (!m.Success) return null;
            var h = m.Groups[1].Value; var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }
        public static Decoded Decode5002(byte[] b)
        {
            var d = new Decoded();
            if (b == null || b.Length < 44)
            {
                d.Name = "UNKNOWN"; d.Title = "Driver/firmware crash"; d.Plain = "The Wi-Fi adapter failed and the driver reset it."; d.Detail = "(no dump data)"; return d;
            }
            int size = BitConverter.ToUInt16(b, 2); var w = new List<uint>();
            for (int o = 40; o + 4 <= b.Length && o < 40 + size; o += 4) w.Add(BitConverter.ToUInt32(b, o));
            d.Id = w.Count > 0 ? w[0] : 0; d.Address = w.Count > 4 ? w[4] : 0; d.Name = ErrorName(d.Id);
            d.Title = d.Name == "NMI_INTERRUPT_HOST" ? "Firmware hang" : d.Name == "ADVANCED_SYSASSERT" ? "Firmware assert" : "Firmware error";
            d.Plain = Plain(d.Name);
            d.Detail = string.Format("{0} 0x{1:X8} @0x{2:X8}", d.Name, d.Id, d.Address);
            if (KnownHangAddrs.Contains(d.Address)) d.Known = "known AX210 firmware hang (the long-standing NMI_INTERRUPT_HOST bug, linked to congested 5 GHz channels)";
            else if ((d.Id & 0x0FFFFFFF) == 0x443D || (d.Id & 0x0FFFFFFF) == 0x91) d.Known = "known AX210 firmware assert";
            return d;
        }
        // Netwtw event 7021 (logged at every association): BSSID sits 8 bytes before the SSID, channel 84 bytes after it.
        public static bool Parse7021(byte[] b, string ssid, out string bssid, out int channel)
        {
            bssid = null; channel = 0; if (b == null || b.Length < 48 || string.IsNullOrEmpty(ssid)) return false;
            int size = BitConverter.ToUInt16(b, 2); int end = Math.Min(b.Length, 40 + size);
            var s = Encoding.UTF8.GetBytes(ssid);
            for (int i = 48; i + s.Length <= end; i++)
            {
                bool m = true; for (int k = 0; k < s.Length && m; k++) m = b[i + k] == s[k];
                if (!m) continue;
                bssid = string.Join(":", Enumerable.Range(i - 8, 6).Select(x => b[x].ToString("x2")));
                if (i + 84 < end) channel = b[i + 84];
                return true;
            }
            return false;
        }
    }

    // ---------------------------------------------------------------- live event watching
    public class Incident { public DateTime Time; public string Kind = "", Title = "", Plain = "", Detail = "", Known = ""; }

    class EventMonitor
    {
        public event Action<Incident> Incident;
        EventLogWatcher sys, wlan;
        readonly object L = new object(); readonly List<Tuple<int, DateTime, byte[]>> pending = new List<Tuple<int, DateTime, byte[]>>(); System.Threading.Timer flush;

        public void Start()
        {
            try
            {
                sys = new EventLogWatcher(new EventLogQuery("System", PathType.LogName,
                    "*[System[(EventID=5002 or EventID=5005 or EventID=5007 or EventID=5010 or EventID=7029 or EventID=4266 or EventID=1023)]]"));
                sys.EventRecordWritten += OnRec; sys.Enabled = true;
            }
            catch (Exception ex) { Log.Write("ERROR", "Can't watch the System log: " + ex.Message); }
            try
            {
                wlan = new EventLogWatcher(new EventLogQuery("Microsoft-Windows-WLAN-AutoConfig/Operational", PathType.LogName, "*[System[(EventID=8003)]]"));
                wlan.EventRecordWritten += OnRec; wlan.Enabled = true;
            }
            catch (Exception ex) { Log.Write("ERROR", "Can't watch the WLAN log: " + ex.Message); }
        }
        void OnRec(object s, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;
            try { using (var r = e.EventRecord) Handle(r); } catch (Exception ex) { Log.Write("ERROR", "event handler: " + ex.Message); }
        }
        void Raise(Incident i) { var ev = Incident; if (ev != null) ev(i); }
        void Handle(EventRecord r)
        {
            string prov = r.ProviderName ?? ""; int id = r.Id; DateTime t = r.TimeCreated ?? DateTime.Now;
            if (prov.StartsWith("Netwtw", StringComparison.OrdinalIgnoreCase))
            {
                if (id == 5002 || id == 5005 || id == 5007)
                {
                    // A crash writes 5005 x2 + 5002 + 5007 within a second; collect them, then report once.
                    lock (L) { pending.Add(Tuple.Create(id, t, id == 5002 ? Decoder.HexFromXml(r.ToXml()) : null)); if (flush == null) flush = new System.Threading.Timer(_ => FlushCrash(), null, 3000, Timeout.Infinite); }
                    return;
                }
                if (id == 5010) { Raise(new Incident { Time = t, Kind = "warning", Title = "Driver warning (5010)", Plain = "The adapter returned an invalid value to the driver. Not a crash; 65% of past ones were on 6 GHz.", Detail = "WDRV 0x0000F07F" }); return; }
                if (id == 7029) { Raise(new Incident { Time = t, Kind = "driverinit", Title = "Wi-Fi driver initialized", Plain = "The Intel driver started or restarted." }); return; }
                return;
            }
            if (prov == "Tcpip" && id == 4266) { Raise(new Incident { Time = t, Kind = "udp", Title = "UDP ports exhausted (Tcpip 4266)", Plain = "Windows ran out of ephemeral UDP ports. New game, voice and DNS connections can fail until ports free up." }); return; }
            if (prov.EndsWith("DNS-Client") && id == 1023) { Raise(new Incident { Time = t, Kind = "nrpt", Title = "DNS rules table corrupted (1023)", Plain = "Windows reported its DNS policy table (NRPT) as corrupted; name lookups can fail. Tailscale edits this table." }); return; }
            if (prov.EndsWith("WLAN-AutoConfig") && id == 8003)
            {
                string reason = ""; try { reason = Regex.Match(r.FormatDescription() ?? "", @"Reason:\s*(.+)").Groups[1].Value.Trim(); } catch { }
                Raise(new Incident { Time = t, Kind = "disconnect", Title = "Wi-Fi disconnected", Plain = reason, Detail = reason });
            }
        }
        void FlushCrash()
        {
            List<Tuple<int, DateTime, byte[]>> items;
            lock (L) { items = pending.ToList(); pending.Clear(); if (flush != null) { flush.Dispose(); flush = null; } }
            if (items.Count == 0) return;
            var bin = items.Where(x => x.Item3 != null).Select(x => x.Item3).FirstOrDefault();
            var d = Decoder.Decode5002(bin);
            Raise(new Incident { Time = items.Min(x => x.Item2), Kind = "crash", Title = d.Title, Plain = d.Plain, Detail = d.Detail, Known = d.Known });
        }

        /// One-time import: attribute past crashes (5002) to the access point/channel of the preceding association (7021).
        public static Dictionary<string, int[]> ImportHistory(string ssid, Dictionary<string, string> knownBands, out string summary)
        {
            var res = new Dictionary<string, int[]>(); int assoc = 0, crashes = 0; summary = "";
            var q = new EventLogQuery("System", PathType.LogName, "*[System[(EventID=5002 or EventID=7021) and TimeCreated[timediff(@SystemTime) <= 15552000000]]]");
            string cur = null;
            using (var rdr = new EventLogReader(q))
            {
                EventRecord r;
                while ((r = rdr.ReadEvent()) != null)
                    using (r)
                    {
                        if (!(r.ProviderName ?? "").StartsWith("Netwtw", StringComparison.OrdinalIgnoreCase)) continue;
                        var bin = Decoder.HexFromXml(r.ToXml());
                        if (r.Id == 7021)
                        {
                            string b; int ch;
                            if (Decoder.Parse7021(bin, ssid, out b, out ch) && ch > 0)
                            {
                                cur = Bands.Guess(b, ch, knownBands) + "|" + ch;
                                int[] v; if (!res.TryGetValue(cur, out v)) res[cur] = v = new int[2]; v[0]++; assoc++;
                            }
                            else cur = null;   // joined some other network: its crashes aren't the home channel's
                        }
                        else if (r.Id == 5002 && cur != null) { res[cur][1]++; crashes++; }
                    }
            }
            summary = string.Format("Imported {0} associations and {1} attributed crashes from the last 180 days", assoc, crashes);
            return res;
        }
    }

    // ---------------------------------------------------------------- games
    public class GameRec { public string Name { get; set; } public string Exe { get; set; } public DateTime LastSeen { get; set; } public bool Prioritized { get; set; } }

    static class GameList
    {
        // Process names (no .exe): popular online games, plus the ones installed on this PC.
        public static readonly string[] Popular = {
            "Overwatch", "RobloxPlayerBeta", "Minecraft.Windows", "VALORANT-Win64-Shipping", "League of Legends", "cs2", "dota2",
            "FortniteClient-Win64-Shipping", "r5apex", "r5apex_dx12", "RocketLeague", "cod", "Marvel-Win64-Shipping", "destiny2", "TslGame",
            "RustClient", "RainbowSix", "RainbowSix_Vulkan", "HaloInfinite", "GTA5", "GTA5_Enhanced", "Discovery", "helldivers2", "Warframe.x64",
            "PathOfExile", "PathOfExileSteam", "Diablo IV", "Wow", "Hearthstone", "SoTGame", "DeadByDaylight-Win64-Shipping", "EscapeFromTarkov",
            "deadlock", "eldenring", "valheim", "Terraria", "Risk of Rain 2", "PEAK", "Borderlands4", "Ravenswatch" };
        static readonly Dictionary<string, string> Display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"RobloxPlayerBeta","Roblox"}, {"Minecraft.Windows","Minecraft"}, {"VALORANT-Win64-Shipping","VALORANT"}, {"cs2","Counter-Strike 2"}, {"dota2","Dota 2"},
            {"FortniteClient-Win64-Shipping","Fortnite"}, {"r5apex","Apex Legends"}, {"r5apex_dx12","Apex Legends"}, {"RocketLeague","Rocket League"}, {"cod","Call of Duty"},
            {"Marvel-Win64-Shipping","Marvel Rivals"}, {"destiny2","Destiny 2"}, {"TslGame","PUBG"}, {"RustClient","Rust"}, {"RainbowSix","Rainbow Six Siege"},
            {"RainbowSix_Vulkan","Rainbow Six Siege"}, {"HaloInfinite","Halo Infinite"}, {"GTA5","GTA V"}, {"GTA5_Enhanced","GTA V"}, {"Discovery","THE FINALS"},
            {"helldivers2","Helldivers 2"}, {"Warframe.x64","Warframe"}, {"PathOfExile","Path of Exile"}, {"PathOfExileSteam","Path of Exile"}, {"Wow","World of Warcraft"},
            {"SoTGame","Sea of Thieves"}, {"DeadByDaylight-Win64-Shipping","Dead by Daylight"}, {"EscapeFromTarkov","Escape from Tarkov"}, {"deadlock","Deadlock"},
            {"eldenring","ELDEN RING"}, {"valheim","Valheim"}, {"Borderlands4","Borderlands 4"} };
        public static string DisplayName(string n) { string d; return n != null && Display.TryGetValue(n, out d) ? d : n; }
        public static readonly string[] Launchers = { "Battle.net", "Steam", "Ubisoft Connect", "Overwolf", "REDlauncher", "EA app", "Epic Games Launcher", "Riot Client", "GOG GALAXY", "Playnite", "Wallpaper Engine" };
        public static readonly Regex Publisher = new Regex("Blizzard|Riot|Electronic Arts|Ubisoft|Epic Games|GOG|Valve|Activision|Bungie|Rockstar|Mojang|Roblox|miHoYo|HoYoverse|Grinding Gear|Digital Extremes|Bethesda|CD PROJEKT|Krafton|NetEase|Embark|Respawn|Wargaming|Hi-Rez|Psyonix|Gearbox|FromSoftware|Iron Gate|Re-Logic|Hopoo", RegexOptions.IgnoreCase);
        public static readonly Regex Helper = new Regex("crash|errorreport|bugreport|setup|redist|prereq|unins|easyanticheat|beservice|battleye|cefprocess|webhelper|createdump|dxsetup|vconsole|modupload|roomeditor", RegexOptions.IgnoreCase);
    }

    /// A game = a process named in the config list, or a process from a game-library folder that has been in the
    /// foreground at least once (so background tools such as Wallpaper Engine never count).
    class GameDetector
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        Dictionary<string, string> roots = new Dictionary<string, string>();   // lower-case root + '\' -> fixed game name, or null = one folder per game
        public DateTime LastDiscover = DateTime.MinValue;
        public int RootCount { get { return roots.Count; } }
        public IEnumerable<string> RootList { get { return roots.Select(kv => kv.Key + (kv.Value != null ? "  = " + kv.Value : "  (one folder per game)")); } }
        readonly Dictionary<int, string> paths = new Dictionary<int, string>();
        readonly HashSet<int> foreground = new HashSet<int>();

        static string Norm(string p) { try { p = Path.GetFullPath(p.Trim().Trim('"')); } catch { } return p.TrimEnd('\\').ToLowerInvariant() + "\\"; }
        static void Add(Dictionary<string, string> r, string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (!Directory.Exists(path.Trim().Trim('"'))) return; } catch { return; }
            var k = Norm(path); if (!r.ContainsKey(k)) r[k] = name;
        }

        public void Discover(Config cfg)
        {
            var r = new Dictionary<string, string>();
            try   // Steam libraries
            {
                string steam = null;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) steam = k == null ? null : k.GetValue("SteamPath") as string;
                if (steam == null) using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")) steam = k == null ? null : k.GetValue("InstallPath") as string;
                if (steam != null)
                {
                    steam = steam.Replace('/', '\\'); var libs = new List<string> { steam };
                    var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf)) foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\"")) libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                    foreach (var l in libs) Add(r, Path.Combine(l, @"steamapps\common"), null);
                }
            }
            catch { }
            // Installed-programs list: Battle.net, Riot, EA, Ubisoft, GOG, Steam games, Roblox, ...
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var up in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    try
                    {
                        using (var k = hive.OpenSubKey(up))
                            if (k != null)
                                foreach (var sub in k.GetSubKeyNames())
                                    try
                                    {
                                        using (var s = k.OpenSubKey(sub))
                                        {
                                            var name = s.GetValue("DisplayName") as string; var loc = s.GetValue("InstallLocation") as string; var pub = s.GetValue("Publisher") as string ?? "";
                                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(loc)) continue;
                                            if (GameList.Launchers.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                                            if (!(sub.StartsWith("Steam App", StringComparison.OrdinalIgnoreCase) || GameList.Publisher.IsMatch(pub))) continue;
                                            int rb = loc.IndexOf(@"\Roblox\Versions\", StringComparison.OrdinalIgnoreCase);
                                            if (rb >= 0) { Add(r, loc.Substring(0, rb + @"\Roblox\Versions".Length), "Roblox"); continue; }
                                            Add(r, loc, Regex.Replace(name, @"[®™]|\s+for\s+\S+$", "").Trim());
                                        }
                                    }
                                    catch { }
                    }
                    catch { }
            try   // Epic
            {
                var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.item"))
                    {
                        var t = File.ReadAllText(f);
                        Add(r, Regex.Match(t, "\"InstallLocation\"\\s*:\\s*\"(.+?)\"").Groups[1].Value.Replace(@"\\", @"\"), Regex.Match(t, "\"DisplayName\"\\s*:\\s*\"(.+?)\"").Groups[1].Value);
                    }
            }
            catch { }
            try   // library folders on every fixed drive
            {
                foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                    foreach (var sub in new[] { "XboxGames", "Riot Games", @"Program Files\EA Games", @"Program Files (x86)\Origin Games", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games", @"Program Files (x86)\GOG Galaxy\Games", "GOG Games", @"Program Files\Epic Games", "Epic Games" })
                        Add(r, Path.Combine(d.RootDirectory.FullName, sub), null);
            }
            catch { }
            foreach (var x in cfg.ExtraGameFolders) Add(r, x, null);
            roots = r; LastDiscover = DateTime.Now;
        }

        public bool IsGamePath(string path, Config cfg, out string game)
        {
            game = null; if (string.IsNullOrEmpty(path)) return false; var p = path.ToLowerInvariant();
            foreach (var kv in roots.OrderByDescending(x => x.Key.Length))   // most specific folder wins
            {
                if (!p.StartsWith(kv.Key)) continue;
                if (cfg.IgnoreGameFolders.Any(x => p.Contains("\\" + x.ToLowerInvariant() + "\\"))) return false;
                if (GameList.Helper.IsMatch(Path.GetFileNameWithoutExtension(path))) return false;
                var rest = path.Substring(kv.Key.Length);
                game = kv.Value ?? (rest.Contains("\\") ? rest.Split('\\')[0] : Path.GetFileNameWithoutExtension(path));
                return true;
            }
            return false;
        }

        string PathOf(int pid)
        {
            string s; if (paths.TryGetValue(pid, out s)) return s;
            s = null; IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != IntPtr.Zero) { try { var sb = new StringBuilder(1024); int n = sb.Capacity; if (QueryFullProcessImageName(h, 0, sb, ref n)) s = sb.ToString(); } finally { CloseHandle(h); } }
            paths[pid] = s; return s;
        }

        public bool Detect(Config cfg, out string game, out string path, out bool named)
        {
            game = null; path = null; named = false;
            uint fg = 0; try { GetWindowThreadProcessId(GetForegroundWindow(), out fg); } catch { }
            var alive = new HashSet<int>(); var names = new HashSet<string>(cfg.GameProcesses, StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    int pid = p.Id; alive.Add(pid);
                    if (pid == fg) foreground.Add(pid);
                    if (game != null) continue;
                    string pn; try { pn = p.ProcessName; } catch { continue; }
                    if (names.Contains(pn)) { game = pn; named = true; continue; }   // named games: no handle is opened to them
                    if (!foreground.Contains(pid)) continue;
                    var pth = PathOf(pid); string g;
                    if (IsGamePath(pth, cfg, out g)) { game = g; path = pth; }
                }
            foreground.RemoveWhere(x => !alive.Contains(x));
            foreach (var dead in paths.Keys.Where(x => !alive.Contains(x)).ToList()) paths.Remove(dead);
            return game != null;
        }
    }

    // ---------------------------------------------------------------- UDP port usage
    static class UdpTable
    {
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
        public static Dictionary<int, int> EphemeralByPid(out int total)
        {
            var d = new Dictionary<int, int>(); total = 0;
            foreach (var af in new[] { 2, 23 })
            {
                int size = 0; GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, 1, 0); if (size <= 0) continue;
                size += 4096; IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedUdpTable(buf, ref size, false, af, 1, 0) != 0) continue;
                    int n = Marshal.ReadInt32(buf), row = af == 2 ? 12 : 28, portOff = af == 2 ? 4 : 20, pidOff = af == 2 ? 8 : 24;
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr p = buf + 4 + i * row; int raw = Marshal.ReadInt32(p, portOff); int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        if (port < 49152) continue;
                        int pid = Marshal.ReadInt32(p, pidOff); total++; int c; d[pid] = d.TryGetValue(pid, out c) ? c + 1 : 1;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return d;
        }
        public static string Top(Dictionary<int, int> d, int n)
        {
            return string.Join(", ", d.OrderByDescending(x => x.Value).Take(n).Select(x => { string name = "?"; try { using (var p = Process.GetProcessById(x.Key)) name = p.ProcessName; } catch { } return name + " " + x.Value; }));
        }
    }

    // ---------------------------------------------------------------- DNS
    static class DnsTools
    {
        public class Provider { public string Name; public string[] V4, V6; }
        public static readonly Provider[] Providers = {
            new Provider { Name = "Cloudflare", V4 = new[] { "1.1.1.1", "1.0.0.1" }, V6 = new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" } },
            new Provider { Name = "Google", V4 = new[] { "8.8.8.8", "8.8.4.4" }, V6 = new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" } },
            new Provider { Name = "Quad9", V4 = new[] { "9.9.9.9", "149.112.112.112" }, V6 = new[] { "2620:fe::fe", "2620:fe::9" } },
            new Provider { Name = "OpenDNS", V4 = new[] { "208.67.222.222", "208.67.220.220" }, V6 = new[] { "2620:119:35::35", "2620:119:53::53" } },
            new Provider { Name = "AdGuard (unfiltered)", V4 = new[] { "94.140.14.140", "94.140.14.141" }, V6 = new[] { "2a10:50c0::1:ff", "2a10:50c0::2:ff" } },
        };
        public static bool IsKnownResolver(string ip) { return Providers.Any(p => p.V6.Contains(ip, StringComparer.OrdinalIgnoreCase) || p.V4.Contains(ip)); }
        static readonly string[] Names = { "www.google.com", "www.cloudflare.com", "overwatch.blizzard.com", "us.actual.battle.net", "www.microsoft.com", "discord.com", "www.youtube.com", "www.amazon.com" };
        static readonly Random rnd = new Random();

        public static List<string> Current()
        {
            try { var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, Adapter.Guid.ToString("B"), StringComparison.OrdinalIgnoreCase)); return ni == null ? new List<string>() : ni.GetIPProperties().DnsAddresses.Select(a => a.ToString().Split('%')[0]).ToList(); }
            catch { return new List<string>(); }
        }
        public static List<string> ReadStatic()
        {
            var r = new List<string>(); var g = Adapter.Guid.ToString("B");
            foreach (var root in new[] { @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" })
                try { using (var k = Registry.LocalMachine.OpenSubKey(root + g)) { var v = k == null ? null : k.GetValue("NameServer") as string; if (!string.IsNullOrWhiteSpace(v)) r.AddRange(v.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)); } } catch { }
            return r;
        }
        static byte[] Build(string name, out ushort id)
        {
            lock (rnd) id = (ushort)rnd.Next(1, 65535);
            var ms = new MemoryStream();
            ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id); ms.WriteByte(1); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1);
            for (int i = 0; i < 6; i++) ms.WriteByte(0);
            foreach (var label in name.Split('.')) { var b = Encoding.ASCII.GetBytes(label); ms.WriteByte((byte)b.Length); ms.Write(b, 0, b.Length); }
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(1); ms.WriteByte(0); ms.WriteByte(1);
            return ms.ToArray();
        }
        public static double QueryMs(string server, string name, int timeoutMs)
        {
            try
            {
                var ip = IPAddress.Parse(server);
                using (var udp = new UdpClient(ip.AddressFamily))
                {
                    udp.Client.ReceiveTimeout = timeoutMs; ushort id; var q = Build(name, out id);
                    var sw = Stopwatch.StartNew(); udp.Send(q, q.Length, new IPEndPoint(ip, 53));
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        IPEndPoint from = null; var resp = udp.Receive(ref from);
                        if (resp.Length >= 12 && resp[0] == (byte)(id >> 8) && resp[1] == (byte)id && (resp[2] & 0x80) != 0)
                        {
                            int rcode = resp[3] & 0x0F; return rcode == 0 || rcode == 3 ? sw.Elapsed.TotalMilliseconds : -1;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }
        public class Score { public string Provider, Family; public string[] Servers; public double Median; public int Fail; }
        static Score Measure(string provider, string family, string[] servers)
        {
            var s = new Score { Provider = provider, Family = family, Servers = servers }; var ms = new List<double>();
            foreach (var n in Names) QueryMs(servers[0], n, 1000);                 // warm the resolver cache
            for (int round = 0; round < 2; round++) foreach (var n in Names) { var t = QueryMs(servers[0], n, 1000); if (t < 0) s.Fail++; else ms.Add(t); }
            if (servers.Length > 1 && QueryMs(servers[1], Names[0], 1000) < 0 && QueryMs(servers[1], Names[1], 1000) < 0) s.Fail += 3;
            ms.Sort(); s.Median = ms.Count == 0 ? 9999 : PingMonitor.Pct(ms, 50); return s;
        }
        /// Benchmarks every provider over IPv4 and IPv6; applies the fastest if it clearly beats the current one.
        public static string BenchmarkAndMaybeApply(bool apply, out string choice)
        {
            choice = ""; var sb = new StringBuilder(); var cur = Current();
            sb.AppendLine("Current DNS: " + (cur.Count == 0 ? "(none)" : string.Join(", ", cur)));
            var scores = new List<Score>();
            foreach (var p in Providers) { scores.Add(Measure(p.Name, "IPv4", p.V4)); scores.Add(Measure(p.Name, "IPv6", p.V6)); }
            // Anything currently configured that isn't one of the providers gets measured too.
            foreach (var fam in new[] { "IPv4", "IPv6" })
            {
                var others = cur.Where(c => (fam == "IPv4") == !c.Contains(":") && !Providers.Any(p => p.V4.Contains(c) || p.V6.Contains(c))).ToArray();
                if (others.Length > 0) scores.Add(Measure("Current (" + others[0] + ")", fam, others));
            }
            sb.AppendLine(); sb.AppendLine(string.Format("{0,-24} {1,-5} {2,9} {3,6}", "Provider", "IP", "median", "fails"));
            foreach (var s in scores.OrderBy(x => x.Family).ThenBy(x => x.Median))
                sb.AppendLine(string.Format("{0,-24} {1,-5} {2,7:0.0} ms {3,6}", s.Provider, s.Family == "IPv4" ? "v4" : "v6", s.Median, s.Fail));
            var chosen = new List<string>(); var names = new List<string>(); bool change = false;
            foreach (var fam in new[] { "IPv4", "IPv6" })
            {
                var ok = scores.Where(s => s.Family == fam && s.Fail <= 1).OrderBy(s => s.Median).ToList();
                if (ok.Count == 0) { sb.AppendLine(fam + ": nothing reachable; leaving it alone"); continue; }
                var best = ok[0];
                var current = scores.Where(s => s.Family == fam && s.Servers.Any(x => cur.Contains(x))).OrderBy(s => s.Median).FirstOrDefault();
                bool better = current == null || current.Fail > 1 || (best.Median + 2 <= current.Median && best.Median <= 0.9 * current.Median);
                var pick = better ? best : current;
                chosen.AddRange(pick.Servers.Take(2)); names.Add(fam + " " + pick.Provider);
                if (better && (current == null || current.Provider != best.Provider)) change = true;
                sb.AppendLine(fam + ": " + (better ? "best is " + best.Provider + string.Format(" ({0:0.0} ms)", best.Median) : "keeping " + pick.Provider + " (within 2 ms of the fastest)"));
            }
            choice = string.Join(", ", names);
            if (apply && change && chosen.Count > 0)
            {
                int idx = Adapter.IfIndex(); string o;
                var script = "Set-DnsClientServerAddress -InterfaceIndex " + idx + " -ServerAddresses @(" + string.Join(",", chosen.Select(Ps.Q)) + "); Clear-DnsClientCache";
                bool ok = Ps.Run(script, 60, out o);
                sb.AppendLine(ok ? "Applied: " + string.Join(", ", chosen) : "Couldn't apply DNS: " + o);
            }
            else if (apply) sb.AppendLine("No change needed.");
            sb.AppendLine("Note: DNS only affects how fast names are looked up, not in-game ping.");
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- on-demand tests
    public class LatStats
    {
        public string Host = ""; public int Sent, Lost, Stalls; public double Min, P50, P95, Max, Jitter;
        public double LossPct { get { return Sent == 0 ? 0 : 100.0 * Lost / Sent; } }
        public override string ToString() { return string.Format("{0,-16} sent {1,4}  loss {2,4:0.0}%  min {3,4:0}  median {4,4:0}  p95 {5,4:0}  max {6,4:0}  jitter {7,4:0.0}  stalls {8}", Host, Sent, LossPct, Min, P50, P95, Max, Jitter, Stalls); }
    }

    static class Tests
    {
        public static double LastDown, LastUp;
        public static LatStats Measure(string host, int seconds, int intervalMs, int stallMs)
        {
            var st = new LatStats { Host = host }; if (string.IsNullOrEmpty(host)) return st;
            var rtts = new List<double>(); var order = new List<double>(); var sw = Stopwatch.StartNew();
            using (var p = new Ping())
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    long t0 = sw.ElapsedMilliseconds; st.Sent++;
                    try { var r = p.Send(host, 1000); if (r.Status == IPStatus.Success) { rtts.Add(r.RoundtripTime); order.Add(r.RoundtripTime); if (r.RoundtripTime >= stallMs) st.Stalls++; } else { st.Lost++; st.Stalls++; } }
                    catch { st.Lost++; st.Stalls++; }
                    int wait = intervalMs - (int)(sw.ElapsedMilliseconds - t0); if (wait > 0) Thread.Sleep(wait);
                }
            for (int i = 1; i < order.Count; i++) st.Jitter += Math.Abs(order[i] - order[i - 1]);
            if (order.Count > 1) st.Jitter /= order.Count - 1;
            rtts.Sort(); if (rtts.Count > 0) { st.Min = rtts[0]; st.Max = rtts[rtts.Count - 1]; st.P50 = PingMonitor.Pct(rtts, 50); st.P95 = PingMonitor.Pct(rtts, 95); }
            return st;
        }
        public static List<LatStats> MeasureMany(string[] hosts, int seconds, int intervalMs, int stallMs)
        {
            var res = new LatStats[hosts.Length];
            var th = hosts.Select((h, i) => new Thread(() => res[i] = Measure(h, seconds, intervalMs, stallMs)) { IsBackground = true }).ToList();
            th.ForEach(t => t.Start()); th.ForEach(t => t.Join());
            return res.ToList();
        }
        public static string Latency(string gw, int seconds)
        {
            var r = MeasureMany(new[] { gw, "1.1.1.1", "8.8.8.8" }, seconds, 250, 100);
            var sb = new StringBuilder(); sb.AppendLine(string.Format("Latency test, {0} s at 4 pings/s (gateway = router):", seconds));
            r.ForEach(x => sb.AppendLine("  " + x)); return sb.ToString();
        }

        static void Prep() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; ServicePointManager.DefaultConnectionLimit = 32; ServicePointManager.Expect100Continue = false; }
        public static double Download(int seconds, int streams)
        {
            Prep(); long total = 0; var deadline = DateTime.UtcNow.AddSeconds(seconds); var sw = Stopwatch.StartNew();
            var th = Enumerable.Range(0, streams).Select(i => new Thread(() =>
            {
                var buf = new byte[65536];
                while (DateTime.UtcNow < deadline)
                {
                    HttpWebRequest req = null;
                    try
                    {
                        req = (HttpWebRequest)WebRequest.Create("https://speed.cloudflare.com/__down?bytes=50000000");
                        req.UserAgent = "AX210_WatchClaude"; req.Timeout = 10000; req.ReadWriteTimeout = 10000;
                        using (var resp = req.GetResponse()) using (var s = resp.GetResponseStream())
                        { int n; while (DateTime.UtcNow < deadline && (n = s.Read(buf, 0, buf.Length)) > 0) Interlocked.Add(ref total, n); }
                    }
                    catch { if (DateTime.UtcNow < deadline) Thread.Sleep(200); }
                    finally { try { if (req != null) req.Abort(); } catch { } }
                }
            }) { IsBackground = true }).ToList();
            th.ForEach(t => t.Start()); th.ForEach(t => t.Join());
            return Interlocked.Read(ref total) * 8.0 / sw.Elapsed.TotalSeconds / 1e6;
        }
        public static double Upload(int seconds, int streams)
        {
            Prep(); long total = 0; var deadline = DateTime.UtcNow.AddSeconds(seconds); var sw = Stopwatch.StartNew();
            var body = new byte[4 * 1024 * 1024]; new Random(7).NextBytes(body);
            var th = Enumerable.Range(0, streams).Select(i => new Thread(() =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    HttpWebRequest req = null;
                    try
                    {
                        req = (HttpWebRequest)WebRequest.Create("https://speed.cloudflare.com/__up");
                        req.Method = "POST"; req.ContentType = "application/octet-stream"; req.UserAgent = "AX210_WatchClaude";
                        req.AllowWriteStreamBuffering = false; req.ContentLength = body.Length; req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                        using (var s = req.GetRequestStream())
                            for (int off = 0; off < body.Length && DateTime.UtcNow < deadline; off += 65536) { int n = Math.Min(65536, body.Length - off); s.Write(body, off, n); Interlocked.Add(ref total, n); }
                        if (DateTime.UtcNow < deadline) using (req.GetResponse()) { }
                    }
                    catch { }
                    finally { try { if (req != null) req.Abort(); } catch { } }
                }
            }) { IsBackground = true }).ToList();
            th.ForEach(t => t.Start()); th.ForEach(t => t.Join());
            return Interlocked.Read(ref total) * 8.0 / sw.Elapsed.TotalSeconds / 1e6;
        }
        public static string Speed()
        {
            var sb = new StringBuilder();
            var idle = Measure("1.1.1.1", 6, 250, 100);
            LatStats dl = null; double down = 0;
            var t1 = new Thread(() => dl = Measure("1.1.1.1", 10, 250, 100)); t1.Start(); down = Download(10, 6); t1.Join();
            LatStats ul = null; double up = 0;
            var t2 = new Thread(() => ul = Measure("1.1.1.1", 10, 250, 100)); t2.Start(); up = Upload(10, 4); t2.Join();
            LastDown = down; LastUp = up;
            sb.AppendLine(string.Format("Download {0,6:0} Mbps    Upload {1,6:0} Mbps   (Cloudflare, 10 s each)", down, up));
            sb.AppendLine(string.Format("Ping to 1.1.1.1 — idle {0:0} ms · while downloading {1:0} ms · while uploading {2:0} ms (median)", idle.P50, dl.P50, ul.P50));
            double bloat = Math.Max(dl.P50, ul.P50) - idle.P50;
            sb.AppendLine("Bufferbloat: " + (bloat < 5 ? "A (none)" : bloat < 30 ? "B (mild)" : bloat < 60 ? "C (noticeable)" : "D (bad)") + string.Format(" — +{0:0} ms under load", bloat));
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- the watchdog
    partial class Watchdog
    {
        public Config Cfg; public State St; public WlanClient Wlan; public readonly PingMonitor Pinger = new PingMonitor(); readonly EventMonitor events = new EventMonitor();
        public bool IsAdmin; public volatile bool Paused; public DateTime PausedUntil;
        /// --render previews: shows the UI as the running app would, but never changes or writes anything.
        public bool ReadOnly; public bool CanAct { get { return IsAdmin && !ReadOnly; } }
        /// Allowed to change the system: elevated, not a preview, and "Automatic fixes" on (off = watch and log only).
        bool MayChange { get { return CanAct && Cfg.AutoActions; } }
        public volatile ConnInfo Conn = new ConnInfo();
        public volatile PingStats P60 = new PingStats();
        public volatile bool InGame; public volatile string Health = "Starting…"; public volatile int Level = 3; public volatile string Busy = "";
        public int UdpInUse; public string UdpTop = ""; public string DriverVersion = ""; public string LowLatencyState = "";
        public readonly GameDetector Detector = new GameDetector(); public volatile string CurrentGame = ""; readonly HashSet<string> qosPending = new HashSet<string>();
        public event Action<string, string, ToolTipIcon> Notify;
        public event Action Updated;
        readonly SemaphoreSlim actionLock = new SemaphoreSlim(1, 1);
        readonly AutoResetEvent nudge = new AutoResetEvent(false);
        public readonly object StLock = new object();
        readonly DateTime started = DateTime.Now;
        DateTime lastBandLearn, lastSample, lastProfileCheck = DateTime.MinValue, lastUdp, lastSave, lastTick, lastPrune, lastProbeCheck, resumeGrace;
        DateTime? downSince; int ladder; bool deferNotified, ispNoted;
        string lastConnDesc = "", lastKey = "";
        readonly Dictionary<string, DateTime> lastNote = new Dictionary<string, DateTime>();

        public string HomeProfile { get { return !string.IsNullOrEmpty(St.HomeProfile) ? St.HomeProfile : Cfg.HomeSsid; } }
        public bool ActionBusy { get { return actionLock.CurrentCount == 0; } }

        /// Loads settings and finds the adapter, without starting anything (used by Start and the installer hooks).
        public void Init()
        {
            Cfg = Config.Load(); St = State.Load();
            IsAdmin = ReadOnly || new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Wlan = new WlanClient();
            Wlan.BandHint = b => { string v; lock (StLock) return b != null && St.KnownBands.TryGetValue(b, out v) ? v : null; };
            Wlan.AllowedBands = () =>
            {
                if (!Adapter.CanLockBands) return null;
                switch (Profiles.InferMode(Adapter.Read(Profiles.ModeKeys)))   // what the driver is set to right now
                {
                    case "5": return new HashSet<string> { "5" };
                    case "2.4": return new HashSet<string> { "2.4" };
                    case "6": return new HashSet<string> { "6", "2.4" };
                    default: return new HashSet<string> { "6", "5" };
                }
            };
            Adapter.Init(Wlan.Iface);
            DriverVersion = Adapter.DriverVersion();
        }

        public void Start()
        {
            Init(); Log.LoadRecent();
            if (!Wlan.Ok) Log.Write("ERROR", "No Wi-Fi interface found through the Windows WLAN API");
            Log.Write("START", string.Format("AX210_WatchClaude {0} started — admin={1}, adapter '{2}' ({3}), driver {4}, auto actions {5}", Program.Version, IsAdmin, Adapter.Name, Wlan.Description, DriverVersion, Cfg.AutoActions ? "on" : "off"));
            if (Adapter.Found && !Adapter.CanLockBands) Log.Write("INFO", "This adapter/driver doesn't support Intel band locking; band switching is off (monitoring, steering, low-latency mode and the rest still work)");
            else if (Adapter.Found && !Adapter.Supports6) Log.Write("INFO", "This adapter has no 6 GHz; using 5 and 2.4 GHz only");
            Detector.Discover(Cfg);
            Log.Write("INFO", "Game detection: watching " + Detector.RootCount + " game folders and " + Cfg.GameProcesses.Count + " known game names");
            if (St.OriginalProps.Count == 0 && Adapter.Found && !St.OriginalsUnknown) { St.OriginalProps = Adapter.Read(Profiles.AllKeys); Log.Write("INFO", "Backed up the original adapter settings"); }
            if (!St.OriginalDnsSaved) { St.OriginalDns = DnsTools.ReadStatic(); St.OriginalDnsSaved = true; }
            if (!string.IsNullOrEmpty(St.LastDriverVersion) && St.LastDriverVersion != DriverVersion)
            {
                Log.Write("DRIVER", "Intel driver changed " + St.LastDriverVersion + " → " + DriverVersion + "; settings will be re-checked");
                Note("driver", "Intel Wi-Fi driver updated", St.LastDriverVersion + " → " + DriverVersion + ". Re-checking settings.", ToolTipIcon.Info, 0);
            }
            St.LastDriverVersion = DriverVersion;
            if (string.IsNullOrEmpty(St.CurrentMode)) St.CurrentMode = Profiles.InferMode(Adapter.Read(Profiles.ModeKeys));
            Save();
            Wlan.Notification += (src, code) => { if ((src == 0x8 && (code == 10 || code == 21)) || (src == 0x10 && (code == 6 || code == 8 || code == 10))) nudge.Set(); };
            events.Incident += OnIncident; events.Start();
            if (!St.HistoryImported) ThreadPool.QueueUserWorkItem(_ => ImportHistory());
            Pinger.Internet = Cfg.InternetTarget; Pinger.StallMs = Cfg.StallMs; Pinger.Gateway = Adapter.Gateway(); Pinger.Start();
            CheckPowerBackup();
            if (MayChange) ThreadPool.QueueUserWorkItem(_ => Harden());
            FeaturesStart();
            SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) { downSince = null; resumeGrace = DateTime.Now.AddSeconds(60); Log.Write("INFO", "Resumed from sleep"); } };
            new Thread(Loop) { IsBackground = true, Name = "watchdog" }.Start();
        }

        void Loop() { Thread.Sleep(3000); while (true) { Tick(); nudge.WaitOne(5000); } }

        public void Save() { if (!ReadOnly) lock (StLock) St.Save(); }

        public void Note(string key, string title, string text, ToolTipIcon icon, int minMinutes)
        {
            lock (lastNote)
            {
                DateTime last; if (minMinutes > 0 && lastNote.TryGetValue(key, out last) && (DateTime.Now - last).TotalMinutes < minMinutes) return;
                lastNote[key] = DateTime.Now;
            }
            var n = Notify; if (n != null) n(title, text, icon);
        }

        // ---------- helpers
        public bool IsAvoidBssid(string b) { return b != null && Cfg.AvoidBssids.Any(x => string.Equals(x, b, StringComparison.OrdinalIgnoreCase)); }
        public bool IsQuarantined(string key) { lock (StLock) { DateTime u; return St.Quarantine.TryGetValue(key, out u) && u > DateTime.UtcNow; } }
        public bool ChannelOk(string band, int ch) { return !(band == "5" && Cfg.AvoidChannels5.Contains(ch)) && !IsQuarantined(band + "|" + ch); }
        public bool ModeBlocked(string band) { lock (StLock) { DateTime u; return St.ModeFailedUntil.TryGetValue(band, out u) && u > DateTime.UtcNow; } }
        public static bool BandAvailable(string b) { return Adapter.CanLockBands && (b != "6" || Adapter.Supports6); }
        public IEnumerable<string> UsableBands { get { return Cfg.BandOrder.Where(BandAvailable); } }
        public string PreferredBand() { return UsableBands.FirstOrDefault(b => !ModeBlocked(b)) ?? UsableBands.FirstOrDefault() ?? "5"; }
        public string ApName(string bssid) { return string.IsNullOrEmpty(bssid) ? "—" : IsAvoidBssid(bssid) ? "Extender" : "Router"; }
        public string Describe(ConnInfo c) { return c == null || !c.Connected ? "disconnected" : string.Format("{0} {1} GHz ch {2} ({3} dBm)", ApName(c.Bssid), c.Band, c.Channel, c.Rssi); }
        public string Describe(Bss b) { return string.Format("{0} {1} GHz ch {2} ({3} dBm, {4})", ApName(b.Bssid), b.Band, b.Channel, b.Rssi, b.Bssid); }
        public static string KeyLabel(string key) { var p = key.Split('|'); return p.Length != 2 ? key : p[0] == "?" ? "ch " + p[1] + " (band unknown)" : p[0] + " GHz ch " + p[1]; }
        public int CrashCount(int hours) { lock (StLock) return St.Incidents.Count(i => i.Kind == "crash" && i.Time > DateTime.UtcNow.AddHours(-hours)); }
        ChannelStat Stat(string key) { ChannelStat s; if (!St.Channels.TryGetValue(key, out s)) St.Channels[key] = s = new ChannelStat(); return s; }
        void UpdateGame()
        {
            string gname, gpath; bool named;
            bool ing = Detector.Detect(Cfg, out gname, out gpath, out named);
            if (ing)
            {
                var disp = GameList.DisplayName(gname);
                if (disp != CurrentGame)
                {
                    Log.Write("GAME", disp + " is running — non-urgent changes will wait");
                    if (CurrentGame != "") OnGameEnded(CurrentGame);
                    CurrentGame = disp; OnGameStarted(disp);
                }
                RecordGame(disp, gname, gpath, named);
            }
            else if (CurrentGame != "") { var was = CurrentGame; Log.Write("GAME", was + " closed"); CurrentGame = ""; OnGameEnded(was); }
            InGame = ing;
            if ((DateTime.Now - Detector.LastDiscover).TotalHours >= 6) Detector.Discover(Cfg);
        }

        void RecordGame(string display, string procName, string path, bool named)
        {
            string exe = named || string.IsNullOrEmpty(path) ? procName + ".exe" : Path.GetFileName(path);
            string key = named || string.IsNullOrEmpty(path) ? exe.ToLowerInvariant() : path.ToLowerInvariant();
            GameRec g; bool isNew;
            lock (StLock) { isNew = !St.Games.TryGetValue(key, out g); if (isNew) St.Games[key] = g = new GameRec { Name = display, Exe = exe }; g.LastSeen = DateTime.UtcNow; }
            if (isNew) Log.Write("GAME", "New game seen: " + display + " (" + exe + ")");
            if (!Cfg.QosTagGame || !MayChange || g.Prioritized) return;
            lock (qosPending) { if (qosPending.Contains(key)) return; qosPending.Add(key); }
            string cond = named || string.IsNullOrEmpty(path) ? exe : path; string policy = PolicyName(exe, key);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string o; bool ok = Ps.Run(QosScript(policy, cond), 60, out o);
                lock (StLock) { g.Prioritized = ok; if (ok) St.GameQos[key] = policy; }
                Save(); lock (qosPending) qosPending.Remove(key);
                Log.Write("GAME", ok ? "Wi-Fi priority on for " + display + "'s game traffic (UDP). Fully applies from its next launch." : "Couldn't add traffic priority for " + display + ": " + o);
            });
        }

        static string QosScript(string policy, string cond)
        {
            return "if (-not (Get-NetQosPolicy -Name " + Ps.Q(policy) + " -ErrorAction SilentlyContinue)) { New-NetQosPolicy -Name " + Ps.Q(policy) +
                   " -AppPathNameMatchCondition " + Ps.Q(cond) + " -IPProtocolMatchCondition UDP -DSCPAction 46 -PriorityValue8021Action 6 -NetworkProfile All | Out-Null }";
        }
        string PolicyName(string exe, string key)
        {
            var name = "AX210_WatchClaude_" + Regex.Replace(Names.CleanProgram(exe, false) ?? "game", "[^A-Za-z0-9_.-]", "");
            lock (StLock) if (St.GameQos.Any(kv => kv.Value == name && kv.Key != key)) name += "_" + (key.GetHashCode() & 0xFFFF).ToString("x4");
            return name;
        }
        string QosSection()
        {
            var q = new StringBuilder("Remove-NetQosPolicy -Name 'AX210_WatchClaude_Game' -Confirm:$false -ErrorAction SilentlyContinue\r\n");   // v1 policy (all protocols)
            if (!Cfg.QosTagGame)
            {
                q.Append("Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'AX210_WatchClaude_*' -and $_.Name -ne 'AX210_WatchClaude_UploadCap' } | Remove-NetQosPolicy -Confirm:$false\r\n$r+='game traffic priority: off'");
                lock (StLock) { St.GameQos.Clear(); foreach (var g in St.Games.Values) g.Prioritized = false; }
                return q.ToString();
            }
            q.Append("$k='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\QoS'\r\nif (-not (Test-Path $k)) { New-Item -Path $k | Out-Null }\r\nNew-ItemProperty -Path $k -Name 'Do not use NLA' -Value '1' -PropertyType String -Force | Out-Null\r\n");
            foreach (var exe in Cfg.QosAlwaysExes) q.Append(QosScript(PolicyName(exe, exe.ToLowerInvariant()), exe)).Append("\r\n");
            lock (StLock)
                foreach (var kv in St.GameQos.Where(x => x.Key.Contains("\\") && !File.Exists(x.Key)).ToList())
                {   // game uninstalled or moved: drop its policy
                    q.Append("Remove-NetQosPolicy -Name " + Ps.Q(kv.Value) + " -Confirm:$false -ErrorAction SilentlyContinue\r\n");
                    St.GameQos.Remove(kv.Key); GameRec g; if (St.Games.TryGetValue(kv.Key, out g)) g.Prioritized = false;
                }
            q.Append("$r+=('game traffic priority: ' + @(Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'AX210_WatchClaude_*' -and $_.Name -ne 'AX210_WatchClaude_UploadCap' }).Count + ' policies (UDP only)')");
            return q.ToString();
        }
        static int PingOnce(string host, int timeout) { try { using (var p = new Ping()) { var r = p.Send(host, timeout); return r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1; } } catch { return -1; } }
        public string AvoidSummary()
        {
            var parts = new List<string>();
            if (Cfg.AvoidChannels5.Count > 0) parts.Add("5 GHz ch " + string.Join("/", Cfg.AvoidChannels5));
            lock (StLock) foreach (var q in St.Quarantine.Where(x => x.Value > DateTime.UtcNow)) parts.Add(KeyLabel(q.Key) + " until " + q.Value.ToLocalTime().ToString("MM-dd HH:mm"));
            lock (StLock) foreach (var q in St.ModeFailedUntil.Where(x => x.Value > DateTime.UtcNow)) parts.Add(q.Key + " GHz unusable until " + q.Value.ToLocalTime().ToString("HH:mm"));
            if (Cfg.AvoidBssids.Count > 0) parts.Add("extender (" + Cfg.AvoidBssids.Count + " BSSIDs)");
            return string.Join(" · ", parts);
        }

        // ---------- main tick (every 5 s, sooner on Wi-Fi notifications)
        void Tick()
        {
            try
            {
                var now = DateTime.Now;
                if (Paused && now > PausedUntil) { Paused = false; Log.Write("INFO", "Automatic actions resumed"); }
                var c = Wlan.GetConnection(); Conn = c;
                Pinger.Gateway = Adapter.Gateway();
                P60 = Pinger.Stats(60);
                UpdateGame();
                if (c.Connected)
                {
                    if (string.IsNullOrEmpty(Cfg.HomeSsid) && !string.IsNullOrEmpty(c.Ssid)) { Cfg.HomeSsid = c.Ssid; Cfg.Save(false); Log.Write("INFO", "Home network learned: " + c.Ssid); }
                    if (c.Ssid == Cfg.HomeSsid && !string.IsNullOrEmpty(c.Profile)) St.HomeProfile = c.Profile;
                    if ((now - lastBandLearn).TotalSeconds >= 60) { lastBandLearn = now; foreach (var b in Wlan.GetBssList().Where(x => x.Ssid == c.Ssid && x.Band != "?")) lock (StLock) St.KnownBands[b.Bssid] = b.Band; }   // exact, from frequency
                    lock (StLock) { if (!string.IsNullOrEmpty(c.Bssid) && c.Band != "" && c.Band != "?") St.KnownBands[c.Bssid] = c.Band; if (c.Band == "6" && !IsAvoidBssid(c.Bssid)) St.Router6Bssid = c.Bssid; }
                    var desc = Describe(c);
                    if (desc != lastConnDesc && c.Key != lastKey) { Log.Write("CONN", "Connected: " + desc + string.Format(", link {0}/{1} Mbps", c.RxMbps, c.TxMbps)); lastKey = c.Key; }
                    lastConnDesc = desc;
                }
                // stats for the band/channel we're on
                double dt = lastTick == default(DateTime) ? 0 : (now - lastTick).TotalSeconds;
                if (c.Connected && dt > 0 && dt < 30) lock (StLock) { var s = Stat(c.Key); s.Seconds += dt; s.Stalls += Pinger.StallsSince(lastTick); }
                lastTick = now;
                FeaturesTick(c);

                bool warm = (now - Pinger.Started).TotalSeconds > 12;
                bool linkUp = c.Connected && ((now - Pinger.LastGwOk).TotalSeconds < 6 || (now - Pinger.LastNetOk).TotalSeconds < 6);
                if (warm)
                {
                    if (!linkUp) { if (downSince == null) { downSince = now; ladder = 0; Log.Write("CONN", "Connection lost (" + (c.Connected ? "router not answering" : "Wi-Fi disconnected") + ")"); } }
                    else if (downSince != null) { Log.Write("CONN", string.Format("Connection back after {0:0.0} s", (now - downSince.Value).TotalSeconds)); downSince = null; ladder = 0; }
                }
                if (c.Connected && Cfg.LowLatencyMode && MayChange) EnsureLowLatency();
                else if (c.Connected && ReadOnly) { int bg0, st0; Wlan.Query(WlanNative.OpBackgroundScan, out bg0); Wlan.Query(WlanNative.OpStreamingMode, out st0); LowLatencyState = (bg0 == 0 ? "background scan off" : "background scan ON") + ", " + (st0 != 0 ? "streaming mode on" : "streaming mode OFF"); }
                EvaluateHealth(linkUp, warm);
                if (warm && !Paused && Cfg.AutoActions && CanAct) { if (downSince != null) RecoveryLadder(); else Decide(c); }
                if ((now - lastSample).TotalSeconds >= Cfg.SampleSeconds) { lastSample = now; WriteSample(c); }
                if ((now - lastUdp).TotalSeconds >= 60) { lastUdp = now; CheckUdp(false); }
                if ((now - lastSave).TotalSeconds >= 60) { lastSave = now; Save(); }
                if ((now - lastPrune).TotalHours >= 12) { lastPrune = now; Prune(); }
                WriteStatus();
                var u = Updated; if (u != null) u();
            }
            catch (Exception ex) { Log.Write("ERROR", "tick: " + ex); }
        }

        void EnsureLowLatency()
        {
            int bg, stream; var parts = new List<string>();
            if (Wlan.Query(WlanNative.OpBackgroundScan, out bg) && bg != 0) { var rc = Wlan.SetBool(WlanNative.OpBackgroundScan, false); parts.Add("background scan off rc=" + rc); }
            if (Wlan.Query(WlanNative.OpStreamingMode, out stream) && stream == 0) { var rc = Wlan.SetBool(WlanNative.OpStreamingMode, true); parts.Add("streaming mode on rc=" + rc); }
            int bg2, st2; Wlan.Query(WlanNative.OpBackgroundScan, out bg2); Wlan.Query(WlanNative.OpStreamingMode, out st2);
            var state = (bg2 == 0 ? "background scan off" : "background scan ON") + ", " + (st2 != 0 ? "streaming mode on" : "streaming mode OFF");
            if (state != LowLatencyState) { Log.Write("INFO", "Low-latency mode: " + state + (parts.Count > 0 ? " (" + string.Join("; ", parts) + ")" : "")); LowLatencyState = state; }
        }

        void EvaluateHealth(bool linkUp, bool warm)
        {
            var c = Conn;
            if (!Wlan.Ok) { Level = 2; Health = "No Wi-Fi adapter found"; return; }
            if (!c.Connected) { Level = 2; Health = "Wi-Fi disconnected"; return; }
            if (warm && !linkUp) { Level = 2; Health = "Connected, but the router isn't answering"; return; }
            var issues = new List<string>();
            if (IsAvoidBssid(c.Bssid)) issues.Add("on the extender");
            if (c.Band == "5" && Cfg.AvoidChannels5.Contains(c.Channel)) issues.Add("on channel " + c.Channel);
            if (IsQuarantined(c.Key)) issues.Add("on a quarantined channel");
            bool gwOk = (DateTime.Now - Pinger.LastGwOk).TotalSeconds < 5, netDown = (DateTime.Now - Pinger.LastNetOk).TotalSeconds > 15;
            if (warm && gwOk && netDown)
            {
                issues.Add("internet down (Wi-Fi fine)");
                if (!ispNoted) { ispNoted = true; Log.Write("ISP", "Internet not answering while the router is fine — an ISP-side outage, not Wi-Fi"); Note("isp", "Internet outage", "Your Wi-Fi link is fine; the internet (your ISP) isn't answering.", ToolTipIcon.Warning, 30); }
            }
            else if (!netDown) ispNoted = false;
            if (P60.GwP95 > 60 || P60.NetLossPct > 3) issues.Add("unstable ping");
            if (c.Band == "2.4") issues.Add("2.4 GHz fallback");
            if (Paused) issues.Add("actions paused");
            Level = issues.Count == 0 ? 0 : 1;
            Health = issues.Count == 0 ? "Healthy — " + Describe(c) : string.Join(", ", issues) + " — " + Describe(c);
        }

        // ---------- decisions
        bool RunAction(string what, Action a)
        {
            if (!actionLock.Wait(0)) return false;
            Busy = what;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { a(); } catch (Exception ex) { Log.Write("ERROR", what + ": " + ex.Message); }
                finally { Busy = ""; actionLock.Release(); nudge.Set(); }
            });
            return true;
        }
        public bool TryBeginTest(string name) { if (!actionLock.Wait(0)) return false; Busy = name; return true; }
        public void EndTest() { Busy = ""; actionLock.Release(); }

        void Decide(ConnInfo c)
        {
            if (!c.Connected || string.IsNullOrEmpty(Cfg.HomeSsid) || c.Ssid != Cfg.HomeSsid || ActionBusy) return;  // foreign network: monitor only
            if (c.Band == "?") return;   // band not known yet (fresh BSSID, no scan): decide on the next tick, not on a guess
            if (TurboCheck()) return;   // fast-download mode enters/leaves first (leaving happens even when a game starts)
            string problem = null;
            if (IsAvoidBssid(c.Bssid)) problem = "connected to an extender instead of the router";
            else if (c.Band == "5" && Cfg.AvoidChannels5.Contains(c.Channel)) problem = "on 5 GHz channel " + c.Channel + " (crash-prone)";
            else if (IsQuarantined(c.Key)) problem = "on " + KeyLabel(c.Key) + ", quarantined after repeated crashes";
            if (problem != null)
            {
                bool urgent; lock (StLock) urgent = St.Incidents.Any(i => i.Kind == "crash" && i.Time > DateTime.UtcNow.AddMinutes(-15));
                if (InGame && Cfg.DeferWhileGaming && !urgent)
                {
                    if (!deferNotified) { deferNotified = true; Log.Write("ACTION", "Problem found (" + problem + ") — waiting until " + CurrentGame + " closes"); Note("defer", "Will fix after your game", "Wi-Fi is " + problem + ". I'll move it when " + CurrentGame + " closes, or right away if the driver crashes.", ToolTipIcon.Warning, 0); }
                    return;
                }
                RunAction("Fixing: " + problem, () => FixPlacement(problem));
                return;
            }
            deferNotified = false;
            if (InGame) return;   // everything below is optional housekeeping
            var now = DateTime.Now;
            if (Cfg.EnforceAdapterProfile && (now - lastProfileCheck).TotalMinutes >= 10)
            {
                lastProfileCheck = now;
                var drift = ProfileDrift(St.CurrentMode);
                if (drift.Count > 0 && (Adapter.CanLockBands || now >= baseBackoffUntil))   // backing off: carry on with the rest
                {
                    var why = "settings drifted: " + string.Join(", ", drift);
                    if (Adapter.CanLockBands) RunAction("Re-applying settings", () => ApplyMode(St.CurrentMode, why, drift.Any(d => Profiles.ModeKeys.Contains(d))));
                    else RunAction("Re-applying settings", () => ApplyBaseProfile(why));
                    return;
                }
            }
            var pref = PreferredBand();
            if (Adapter.CanLockBands && Profiles.BandOf(St.CurrentMode) != pref && !St.TurboActive && (now - lastProbeCheck).TotalMinutes >= 5)
            {
                lastProbeCheck = now;
                bool dwellOk = (DateTime.UtcNow - St.LastSwitch).TotalMinutes >= Cfg.SwitchDwellMinutes;
                if (dwellOk && DateTime.UtcNow > St.NextProbe)
                {
                    // Free look at the router's other radios via the Reduced Neighbor Report (netsh "Colocated APs").
                    var colo = ColocatedChannels();
                    List<int> chs;
                    if (pref == "5" && colo.TryGetValue("5", out chs) && chs.Any(ch => !ChannelOk("5", ch)) && !chs.Any(ch => ChannelOk("5", ch)))
                    { St.NextProbe = DateTime.UtcNow.AddMinutes(30); Log.Write("INFO", "Router's 5 GHz is still on channel " + string.Join("/", chs) + "; staying on " + Profiles.Name(St.CurrentMode)); }
                    else { St.NextProbe = DateTime.UtcNow.AddHours(Cfg.ProbePreferredHours); RunAction("Trying " + pref + " GHz again", () => ApplyMode(pref, "returning to the preferred band", true)); return; }
                }
            }
            if (Cfg.AutoDns && (DateTime.UtcNow - St.LastDnsBench).TotalDays >= Cfg.DnsBenchmarkDays && (now - started).TotalMinutes > 3)
                RunAction("DNS benchmark", () => RunDns(true));
        }

        List<string> ProfileDrift(string mode)
        {
            // only what this driver has and accepts, so an unsupported keyword or value can never count as "drift"
            var target = Adapter.CanLockBands ? Adapter.Supported(Profiles.Target(mode)) : BaseTarget();
            var cur = Adapter.Read(target.Keys);
            return target.Where(kv => cur.ContainsKey(kv.Key) && cur[kv.Key] != null && cur[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
        }

        Dictionary<string, List<int>> ColocatedChannels()
        {
            var r = new Dictionary<string, List<int>>();
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                string text; using (var p = Process.Start(psi)) { text = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
                foreach (Match m in Regex.Matches(text, @"BSSID:\s*([0-9a-fA-F:]{17}),\s*Band:\s*([\d.]+)\s*GHz,\s*Channel:\s*(\d+)"))
                {
                    var band = m.Groups[2].Value; int ch = int.Parse(m.Groups[3].Value);
                    List<int> l; if (!r.TryGetValue(band, out l)) r[band] = l = new List<int>(); if (!l.Contains(ch)) l.Add(ch);
                }
            }
            catch { }
            return r;
        }

        bool TryConnectBest(string band)
        {
            var list = Wlan.GetBssList().Where(b => b.Ssid == Cfg.HomeSsid && b.Band == band && !IsAvoidBssid(b.Bssid) && ChannelOk(b.Band, b.Channel)).OrderByDescending(b => b.Rssi).ToList();
            if (list.Count == 0 && band == "6" && !string.IsNullOrEmpty(St.Router6Bssid))
            {
                var rc0 = Wlan.Connect(HomeProfile, new[] { St.Router6Bssid }); Log.Write("ACTION", "Steering to the router's 6 GHz radio " + St.Router6Bssid + " (rc=" + rc0 + ")"); return rc0 == 0;
            }
            if (list.Count == 0) return false;
            var rc = Wlan.Connect(HomeProfile, new[] { list[0].Bssid });
            Log.Write("ACTION", "Steering to " + Describe(list[0]) + " (rc=" + rc + ")"); return rc == 0;
        }

        bool WaitForBand(string band, int timeoutSec, out string reason)
        {
            reason = "no connection"; var sw = Stopwatch.StartNew(); bool steered = false, steeredBad = false;
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                Thread.Sleep(2000);
                var c = Wlan.GetConnection();
                if (!c.Connected) { reason = "couldn't connect"; if (sw.Elapsed.TotalSeconds > 20 && !steered) { steered = true; TryConnectBest(band); } continue; }
                if (c.Band == "?") { reason = "band not confirmed yet"; Wlan.Scan(); continue; }   // rescan so the BSS list learns its frequency
                if (c.Band != band) { reason = "the card connected on " + c.Band + " GHz instead"; if (sw.Elapsed.TotalSeconds > 8 && !steered) { steered = true; TryConnectBest(band); } continue; }
                if (IsAvoidBssid(c.Bssid) || !ChannelOk(c.Band, c.Channel))
                {
                    reason = IsAvoidBssid(c.Bssid) ? "it landed on the extender" : "the router's " + band + " GHz radio is on channel " + c.Channel;
                    if (!steeredBad) { steeredBad = true; if (TryConnectBest(band)) continue; }
                    if (!IsAvoidBssid(c.Bssid)) return false;
                    continue;
                }
                var gw = Adapter.Gateway(); if (gw == null) { reason = "no IP address yet"; continue; }
                if (PingOnce(gw, 1000) < 0 && PingOnce(gw, 1000) < 0) { reason = "the router isn't answering"; continue; }
                Conn = c; return true;
            }
            return false;
        }

        /// Transactional band change: apply, verify (right band, OK channel, router answering), else roll back.
        public bool ApplyMode(string mode, string reason, bool notify)
        {
            if (!IsAdmin || !Adapter.Found || !BandAvailable(Profiles.BandOf(mode))) return false;
            var band = Profiles.BandOf(mode); var target = Profiles.Target(mode); var before = Adapter.Read(target.Keys);
            Log.Write("ACTION", "Applying " + Profiles.Name(mode) + " profile: " + reason);
            if (notify) Note("switch", "Adjusting Wi-Fi", Profiles.Name(mode) + ": " + reason + ". You may drop for a few seconds.", ToolTipIcon.Info, 0);
            string o; Adapter.Apply(target, true, out o); if (!string.IsNullOrWhiteSpace(o)) Log.Write("DEBUG", o);
            string why; bool ok = WaitForBand(band, 60, out why);
            if (!ok && mode == "6")
            {
                Log.Write("ACTION", "Strict 6 GHz didn't work (" + why + "); trying 6 GHz with 5 GHz still allowed");
                Adapter.Apply(Profiles.Target("6b"), true, out o); ok = WaitForBand("6", 60, out why); if (ok) mode = "6b";
            }
            if (ok)
            {
                lock (StLock) { St.CurrentMode = mode; St.LastSwitch = DateTime.UtcNow; St.LastKnownGood = Adapter.Read(Profiles.AllKeys); St.ModeFailedUntil.Remove(band); }
                Save(); var c = Wlan.GetConnection(); Conn = c;
                Log.Write("ACTION", "Done — now on " + Describe(c));
                if (notify) Note("switched", "Wi-Fi adjusted", "Now on " + Describe(c) + ".", ToolTipIcon.Info, 0);
                return true;
            }
            Log.Write("ACTION", Profiles.Name(mode) + " didn't work out (" + why + "); rolling back");
            lock (StLock) St.ModeFailedUntil[band] = DateTime.UtcNow.AddHours(Cfg.ProbePreferredHours);
            Adapter.Restore(before, out o);
            string w2; WaitForBand(Profiles.BandOf(St.CurrentMode), 60, out w2);
            Save();
            if (notify) Note("switchfail", "Stayed on " + Profiles.Name(St.CurrentMode), band + " GHz wasn't usable: " + why + ".", ToolTipIcon.Warning, 0);
            return false;
        }

        void FixPlacement(string problem)
        {
            var c = Wlan.GetConnection();
            Log.Write("ACTION", "Fixing: " + problem);
            if (!InGame) { Wlan.Scan(); Thread.Sleep(4000); }   // scans cause small lag spikes, so not mid-game
            if (TryConnectBest(c.Band))
            {
                string why; if (WaitForBand(c.Band, 25, out why)) { var n = Wlan.GetConnection(); Log.Write("ACTION", "Fixed — now on " + Describe(n)); Note("fixed", "Wi-Fi fixed", "Moved to " + Describe(n) + ".", ToolTipIcon.Info, 0); return; }
            }
            foreach (var b in UsableBands) { if (b == c.Band || ModeBlocked(b)) continue; if (ApplyMode(b, problem, true)) return; }
            Log.Write("ACTION", "Couldn't find a better option; staying put");
            Note("stuck", "Couldn't avoid the problem", "Wi-Fi is " + problem + " and no other band was usable right now.", ToolTipIcon.Warning, 30);
        }

        void RecoveryLadder()
        {
            if (ActionBusy || DateTime.Now < resumeGrace || string.IsNullOrEmpty(Cfg.HomeSsid)) return;
            double down = (DateTime.Now - downSince.Value).TotalSeconds;
            bool homeVisible = Wlan.GetBssList().Any(b => b.Ssid == Cfg.HomeSsid);
            if (down >= 8 && ladder < 1)
            {
                ladder = 1;
                RunAction("Reconnecting", () =>
                {
                    var best = Wlan.GetBssList().Where(b => b.Ssid == Cfg.HomeSsid && !IsAvoidBssid(b.Bssid) && ChannelOk(b.Band, b.Channel) && b.Band == Profiles.BandOf(St.CurrentMode)).OrderByDescending(b => b.Rssi).FirstOrDefault();
                    var rc = Wlan.Connect(HomeProfile, best == null ? null : new[] { best.Bssid });
                    Log.Write("ACTION", "Recovery 1/4: reconnect" + (best == null ? "" : " to " + Describe(best)) + " (rc=" + rc + ")");
                });
            }
            else if (down >= 30 && ladder < 2)
            {
                ladder = 2;
                RunAction("Reconnecting (full)", () => { Wlan.Disconnect(); Thread.Sleep(1500); var rc = Wlan.Connect(HomeProfile, null); Log.Write("ACTION", "Recovery 2/4: disconnect + reconnect (rc=" + rc + ")"); });
            }
            else if (down >= 60 && ladder < 3)
            {
                ladder = 3;
                if (!homeVisible) { Log.Write("CONN", "Router's network isn't visible at all — the router is probably restarting; not touching the adapter"); Note("routerdown", "Router not visible", "Your network isn't showing up. The router may be restarting.", ToolTipIcon.Warning, 30); return; }
                RunAction("Restarting adapter", () => { Log.Write("ACTION", "Recovery 3/4: restarting the Wi-Fi adapter"); Adapter.Restart(); });
            }
            else if (down >= 120 && ladder < 4 && homeVisible)
            {
                ladder = 4;
                RunAction("Rolling back", RevertToLastKnownGood);
            }
            else if (down >= 300 && ladder < 5)
            {
                ladder = 5; Log.Write("CONN", "Still offline after 5 minutes; automatic fixes exhausted");
                Note("offline", "Still offline", "Automatic fixes didn't bring the connection back. Check the router.", ToolTipIcon.Error, 30);
            }
        }

        public void RevertToLastKnownGood()
        {
            var lkg = St.LastKnownGood.Count > 0 ? St.LastKnownGood : St.OriginalProps;
            var cur = Adapter.Read(lkg.Keys);
            var diff = lkg.Where(kv => cur.ContainsKey(kv.Key) && cur[kv.Key] != kv.Value && !(kv.Value == null && cur[kv.Key] == Adapter.DefaultValue(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value);
            string o;
            if (diff.Count == 0) { Log.Write("ACTION", "Recovery 4/4: settings already last-known-good; restarting the adapter once more"); Adapter.Restart(); return; }
            Log.Write("ACTION", "Recovery 4/4: rolling back to last-known-good settings (" + string.Join(", ", diff.Keys) + ")");
            Adapter.Restore(diff, out o);
            St.CurrentMode = Profiles.InferMode(Adapter.Read(Profiles.ModeKeys)); Save();
            Note("rollback", "Rolled back Wi-Fi settings", "Restored the last setup that worked.", ToolTipIcon.Warning, 0);
        }

        // ---------- incidents
        void OnIncident(Incident inc)
        {
            var c = Conn; string key = c.Connected ? c.Key : lastKey;
            lock (StLock)
            {
                St.Incidents.Add(new IncidentRec { Time = inc.Time.ToUniversalTime(), Kind = inc.Kind, Title = inc.Title, Key = key, Detail = inc.Detail });
                if (St.Incidents.Count > 600) St.Incidents.RemoveRange(0, St.Incidents.Count - 500);
            }
            switch (inc.Kind)
            {
                case "crash":
                    {
                        int last30, last24;
                        lock (StLock)
                        {
                            Stat(key).Crashes++;
                            DriverStat dsx; if (St.DriverStats.TryGetValue(DriverVersion, out dsx)) dsx.Crashes++;
                            last30 = St.Incidents.Count(i => i.Kind == "crash" && i.Key == key && i.Time > DateTime.UtcNow.AddMinutes(-30));
                            last24 = St.Incidents.Count(i => i.Kind == "crash" && i.Key == key && i.Time > DateTime.UtcNow.AddHours(-24));
                        }
                        Log.Write("INCIDENT", inc.Title + " on " + KeyLabel(key) + " — " + inc.Plain + " [" + inc.Detail + "]" + (inc.Known != "" ? " (" + inc.Known + ")" : ""));
                        Note("crash", "Wi-Fi driver crashed and recovered", inc.Plain + " On " + KeyLabel(key) + ".", ToolTipIcon.Warning, 10);
                        if (last30 >= 2 || last24 >= 5)
                        {
                            int hours = last24 >= 5 ? Cfg.QuarantineHours * 3 : Cfg.QuarantineHours;
                            lock (StLock) St.Quarantine[key] = DateTime.UtcNow.AddHours(hours);
                            Log.Write("ACTION", "Quarantined " + KeyLabel(key) + " for " + hours + " h (" + last30 + " crashes in 30 min, " + last24 + " in 24 h)");
                            Note("quarantine", "Avoiding " + KeyLabel(key), "It crashed " + last30 + " times in 30 minutes. Moving off it.", ToolTipIcon.Warning, 0);
                        }
                        Save(); nudge.Set(); break;
                    }
                case "warning": Log.Write("DRIVER", inc.Title + " on " + KeyLabel(key) + " — " + inc.Plain, false); break;
                case "driverinit":
                    {
                        var v = Adapter.DriverVersion();
                        if (v != DriverVersion) { Log.Write("DRIVER", "Intel driver changed " + DriverVersion + " → " + v); Note("driver", "Intel Wi-Fi driver updated", DriverVersion + " → " + v + ". Re-checking settings.", ToolTipIcon.Info, 0); DriverVersion = v; St.LastDriverVersion = v; lastProfileCheck = DateTime.MinValue; if (MayChange) ThreadPool.QueueUserWorkItem(_ => Harden()); OnDriverChanged(); }
                        else Log.Write("DRIVER", "Wi-Fi driver (re)initialized", false);
                        break;
                    }
                case "udp": Log.Write("UDP", inc.Title); CheckUdp(true); break;
                case "nrpt": Log.Write("DNS", inc.Title + " — " + inc.Plain); Note("nrpt", "DNS problem", inc.Plain, ToolTipIcon.Warning, 30); break;
                case "disconnect": Log.Write("CONN", "Windows: Wi-Fi disconnected — " + inc.Detail); break;
            }
        }

        void CheckUdp(bool alert)
        {
            try
            {
                int total; var d = UdpTable.EphemeralByPid(out total); UdpInUse = total; UdpTop = UdpTable.Top(d, 3);
                if (alert || total >= Cfg.UdpWarnThreshold)
                {
                    Log.Write("UDP", string.Format("{0} ephemeral UDP ports in use; top: {1}", total, UdpTop));
                    Note("udp", "Network ports running out", string.Format("{0} UDP ports in use. Biggest user: {1}. Restarting that app frees them.", total, UdpTop), ToolTipIcon.Warning, 30);
                }
            }
            catch (Exception ex) { Log.Write("ERROR", "UDP check: " + ex.Message, false); }
        }

        void ImportHistory()
        {
            try
            {
                if (string.IsNullOrEmpty(Cfg.HomeSsid)) { Thread.Sleep(15000); if (string.IsNullOrEmpty(Cfg.HomeSsid)) return; }
                string summary; Dictionary<string, string> known; lock (StLock) known = new Dictionary<string, string>(St.KnownBands);
                var h = EventMonitor.ImportHistory(Cfg.HomeSsid, known, out summary);
                lock (StLock) { foreach (var kv in h) { var s = Stat(kv.Key); s.HistAssociations = kv.Value[0]; s.HistCrashes = kv.Value[1]; } St.HistoryImported = true; }
                Save(); Log.Write("INFO", summary);
            }
            catch (Exception ex) { Log.Write("ERROR", "history import: " + ex.Message); }
        }

        /// Power settings + device power management + optional QoS tag for the game. Idempotent.
        readonly object hardenLock = new object();
        public void Harden()
        {
            lock (hardenLock)   // one at a time: the first run's backup must be taken before any run changes anything
                try { HardenCore(); } catch (Exception ex) { Log.Write("ERROR", "system settings: " + ex.Message); }
        }
        const string SubWifi = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", SetWifi = "12bbebe6-58d6-4636-95bb-3217ef867c1a", SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20", SetAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
        const string PowerBackupVersion = "2";
        static Dictionary<string, string> UnknownPower() { return new Dictionary<string, string> { { "unknown", "1" }, { "v", PowerBackupVersion } }; }

        /// Called at start, before anything is changed. A backup can only be trusted if it was taken before the app first
        /// hardened this PC: states from 1.0.0 (adapter backup but no power backup), backups taken by 1.0.1 (after 1.0.0 had
        /// already hardened), and states rebuilt after damage are marked "unknown" -> a restore uses Windows' defaults.
        void CheckPowerBackup()
        {
            lock (StLock)
            {
                if (St.OriginalsUnknown && St.OriginalSystem.Count == 0) St.OriginalSystem = UnknownPower();
                else if (St.OriginalSystem.Count == 0 && St.OriginalProps.Count > 0) { St.OriginalSystem = UnknownPower(); Log.Write("INFO", "Power settings were changed by an earlier version before backups existed; a restore will use Windows' defaults"); }
                else if (St.OriginalSystem.Count > 0 && !St.OriginalSystem.ContainsKey("v")) St.OriginalSystem = UnknownPower();
            }
        }

        /// Once, before the first change: the power plan values, the adapter's "allow turning off" and the QoS NLA value.
        void CaptureSystemOriginals()
        {
            lock (StLock) if (St.OriginalSystem.Count > 0 || St.OriginalsUnknown) return;
            string so, se;
            Ps.Run((@"
$o=[ordered]@{}
$o.scheme=((powercfg /getactivescheme) -replace '.*:\s*([0-9a-fA-F-]{36}).*','$1').Trim()
function Q($sub,$set){ $t = powercfg /query $o.scheme $sub $set; @(((($t | Select-String 'Current AC Power Setting Index') -replace '.*:\s*','') -join '').Trim(), ((($t | Select-String 'Current DC Power Setting Index') -replace '.*:\s*','') -join '').Trim()) }
$w = Q '%SW%' '%TW%'; $o.wifiAC=$w[0]; $o.wifiDC=$w[1]
$p = Q '%SP%' '%TP%'; $o.aspmAC=$p[0]
$pnp=(Get-NetAdapter -Name %NAME%).PnPDeviceID
$o.devpower=((@(Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | Where-Object { $_.InstanceName.StartsWith($pnp,[StringComparison]::OrdinalIgnoreCase) }) | ForEach-Object { [string]$_.Enable }) -join ',')
$v=(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\QoS' -Name 'Do not use NLA' -ErrorAction SilentlyContinue).'Do not use NLA'; $o.nla=if ($v -eq $null) { '(none)' } else { [string]$v }
$o.Keys | ForEach-Object { $_ + '=' + $o[$_] }").Replace("%SW%", SubWifi).Replace("%TW%", SetWifi).Replace("%SP%", SubPcie).Replace("%TP%", SetAspm).Replace("%NAME%", Ps.Q(Adapter.Name)), 60, out so, out se);
            var d = new Dictionary<string, string>();
            foreach (var l in (so ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Contains("="))) d[l.Substring(0, l.IndexOf('='))] = l.Substring(l.IndexOf('=') + 1);
            string ac; if (!d.TryGetValue("wifiAC", out ac) || !Regex.IsMatch(ac, "^0x[0-9a-fA-F]+$")) d = UnknownPower();   // couldn't read it (non-English powercfg): restore will use defaults
            else d["v"] = PowerBackupVersion;
            lock (StLock) { if (St.OriginalSystem.Count > 0) return; St.OriginalSystem = d; }
            Save(); Log.Write("INFO", d.ContainsKey("unknown") ? "Couldn't read the power settings; a restore will use Windows' defaults" : "Backed up the original power settings");
        }

        /// Windows' own default for a power setting in a plan (registry DefaultPowerSchemeValues), as "0x.." like powercfg prints.
        static string PlanDefault(string scheme, string sub, string setting, bool ac)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" + sub + "\\" + setting + @"\DefaultPowerSchemeValues\" + scheme))
                { var v = k == null ? null : k.GetValue(ac ? "ACSettingIndex" : "DCSettingIndex"); return v == null ? null : "0x" + Convert.ToInt64(v).ToString("x8"); }
            }
            catch { return null; }
        }

        /// Undoes Harden: power plan values, "allow the computer to turn off this device", and the QoS NLA value.
        /// Without a trustworthy backup it puts back Windows' defaults (plan defaults, turn-off allowed, no NLA override).
        string RestoreSystemOriginals()
        {
            Dictionary<string, string> d; lock (StLock) d = new Dictionary<string, string>(St.OriginalSystem); if (d.Count == 0) return "";
            if (d.ContainsKey("unknown"))
            {
                var cur = ((Func<string>)(() => { string so; Ps.Run("((powercfg /getactivescheme) -replace '.*:\\s*([0-9a-fA-F-]{36}).*','$1').Trim()", 30, out so); return (so ?? "").Trim(); }))();
                d = new Dictionary<string, string> { { "scheme", cur }, { "devpower", "True" }, { "nla", "(none)" } };
                if (Regex.IsMatch(cur, "^[0-9a-fA-F-]{36}$"))
                {
                    d["wifiAC"] = PlanDefault(cur, SubWifi, SetWifi, true); d["wifiDC"] = PlanDefault(cur, SubWifi, SetWifi, false); d["aspmAC"] = PlanDefault(cur, SubPcie, SetAspm, true);
                }
            }
            Func<string, string> Num = k => { string v; if (!d.TryGetValue(k, out v) || !Regex.IsMatch(v ?? "", "^0x[0-9a-fA-F]+$")) return null; return Convert.ToInt64(v.Substring(2), 16).ToString(); };
            string scheme; d.TryGetValue("scheme", out scheme); if (!Regex.IsMatch(scheme ?? "", "^[0-9a-fA-F-]{36}$")) scheme = "SCHEME_CURRENT";
            var sb = new StringBuilder();
            if (Num("wifiAC") != null) sb.AppendLine("powercfg /setacvalueindex " + scheme + " " + SubWifi + " " + SetWifi + " " + Num("wifiAC") + " | Out-Null");
            if (Num("wifiDC") != null) sb.AppendLine("powercfg /setdcvalueindex " + scheme + " " + SubWifi + " " + SetWifi + " " + Num("wifiDC") + " | Out-Null");
            if (Num("aspmAC") != null) sb.AppendLine("powercfg /setacvalueindex " + scheme + " " + SubPcie + " " + SetAspm + " " + Num("aspmAC") + " | Out-Null");
            sb.AppendLine("powercfg /setactive SCHEME_CURRENT | Out-Null");
            string dp; if (d.TryGetValue("devpower", out dp) && dp.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0)
                sb.AppendLine("$pnp=(Get-NetAdapter -Name " + Ps.Q(Adapter.Name) + ").PnPDeviceID; Get-CimInstance -Namespace root\\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | Where-Object { $_.InstanceName.StartsWith($pnp,[StringComparison]::OrdinalIgnoreCase) -and -not $_.Enable } | ForEach-Object { $_ | Set-CimInstance -Property @{Enable=$true} }");
            string nla; if (d.TryGetValue("nla", out nla) && nla == "(none)") sb.AppendLine("Remove-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\QoS' -Name 'Do not use NLA' -ErrorAction SilentlyContinue");
            string o; bool ok = Ps.Run(sb.ToString(), 60, out o);
            Log.Write("ACTION", ok ? "Restored the original power settings" : "Couldn't fully restore the power settings: " + o);
            return ok ? "Power settings restored." : "Power settings partly restored.";
        }

        void HardenCore()
        {
            CaptureSystemOriginals();
            var s = @"
$r=@()
powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null
powercfg /setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null
powercfg /setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0 | Out-Null
powercfg /setactive SCHEME_CURRENT | Out-Null
$r+='Wi-Fi power saving: max performance; PCIe link power saving: off'
$pnp=(Get-NetAdapter -Name %NAME%).PnPDeviceID
Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue | Where-Object { $_.InstanceName.StartsWith($pnp,[StringComparison]::OrdinalIgnoreCase) -and $_.Enable } | ForEach-Object { $_ | Set-CimInstance -Property @{Enable=$false}; $r+='device power-off: disabled' }
%QOS%
$r -join '; '";
            s = s.Replace("%NAME%", Ps.Q(Adapter.Name)).Replace("%QOS%", QosSection());
            string o; bool ok = Ps.Run(s, 120, out o);
            Log.Write("INFO", "System settings: " + (ok ? o : "partly failed: " + o));
            if (ok && Cfg.QosTagGame) lock (StLock) foreach (var exe in Cfg.QosAlwaysExes) St.GameQos[exe.ToLowerInvariant()] = PolicyName(exe, exe.ToLowerInvariant());
            Save();
        }

        public string RunDns(bool apply)
        {
            string choice; var r = DnsTools.BenchmarkAndMaybeApply(apply && IsAdmin, out choice);
            lock (StLock) { St.LastDnsBench = DateTime.UtcNow; St.DnsChoice = choice; }
            Save(); Log.Bench("DNS benchmark", r); Log.Write("DNS", "Benchmark done: " + choice);
            return r;
        }

        public string RestoreOriginal()
        {
            Cfg.AutoActions = false; Cfg.UploadLimiter = false; Cfg.TurboDownloads = false; Cfg.Save();
            lock (StLock) St.TurboActive = false;
            RestoreWifiDirect("restoring original settings");
            if (Wlan.Ok) { Wlan.SetBool(WlanNative.OpBackgroundScan, true); Wlan.SetBool(WlanNative.OpStreamingMode, false); LowLatencyState = "off (original settings restored)"; }
            var sb = new StringBuilder(); string o;
            if (St.OriginalProps.Count > 0) { Adapter.Restore(St.OriginalProps, out o); sb.AppendLine("Adapter settings restored."); }
            else if (St.OriginalsUnknown) { Adapter.Restore(Profiles.AllKeys.ToDictionary(k => k, k => (string)null), out o); sb.AppendLine("Adapter settings reset to the driver's defaults (the originals were lost)."); }
            var ps = RestoreSystemOriginals(); if (ps != "") sb.AppendLine(ps);
            var dns = St.OriginalDns.Count > 0 ? "Set-DnsClientServerAddress -InterfaceIndex " + Adapter.IfIndex() + " -ServerAddresses @(" + string.Join(",", St.OriginalDns.Select(Ps.Q)) + ")" : "Set-DnsClientServerAddress -InterfaceIndex " + Adapter.IfIndex() + " -ResetServerAddresses";
            Ps.Run(dns + "; Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'AX210_WatchClaude*' } | Remove-NetQosPolicy -Confirm:$false", 60, out o);
            lock (StLock) { St.GameQos.Clear(); foreach (var g in St.Games.Values) g.Prioritized = false; }
            sb.AppendLine("DNS restored; game traffic-priority policies removed.");
            sb.AppendLine("Automatic actions are now OFF (monitoring continues). Turn them back on with 'Re-apply optimized settings'.");
            St.CurrentMode = Profiles.InferMode(Adapter.Read(Profiles.ModeKeys)); Save();
            Log.Write("ACTION", "Restored original settings; automatic actions off");
            return sb.ToString();
        }

        /// Tuned base profile without band keys (adapters that can't lock bands).
        /// The tuned base profile only makes sense with Intel's driver (other drivers name or mean these keywords differently).
        static Dictionary<string, string> BaseTarget() { return Adapter.IsIntel ? Adapter.Supported(Profiles.Base) : new Dictionary<string, string>(); }
        DateTime baseBackoffUntil;

        /// Applies the base profile (adapters that can't lock bands), checks Wi-Fi comes back, and undoes it if not.
        /// If the driver keeps reporting the same "drift" afterwards, it stops retrying for a day instead of restarting
        /// the adapter every 10 minutes.
        public bool ApplyBaseProfile(string reason)
        {
            if (!CanAct || !Adapter.Found) return false;
            var target = BaseTarget(); if (target.Count == 0) return false;
            var before = Adapter.Read(target.Keys);
            Log.Write("ACTION", "Applying the tuned adapter settings: " + reason);
            string o; bool ok = Adapter.Apply(target, true, out o); if (!string.IsNullOrWhiteSpace(o)) Log.Write("DEBUG", o);
            bool back = false; var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 60 && !back)
            {
                Thread.Sleep(2000);
                var c = Wlan.GetConnection(); var gw = Adapter.Gateway();
                back = c.Connected && gw != null && (PingOnce(gw, 1000) >= 0 || PingOnce(gw, 1000) >= 0);
            }
            if (!back)
            {
                Log.Write("ACTION", "Wi-Fi didn't come back after the tuned settings; putting the previous ones back");
                Adapter.Restore(before, out o); baseBackoffUntil = DateTime.Now.AddHours(24);
                Note("switchfail", "Kept your adapter settings", "The tuned settings didn't work with this adapter, so the previous ones are back.", ToolTipIcon.Warning, 0);
                return false;
            }
            lock (StLock) St.LastKnownGood = Adapter.Read(Profiles.AllKeys); Save();
            var still = ProfileDrift(St.CurrentMode);
            if (still.Count > 0) { baseBackoffUntil = DateTime.Now.AddHours(24); Log.Write("ACTION", "The driver didn't keep " + string.Join(", ", still) + "; not retrying for 24 h"); }
            Log.Write("ACTION", ok ? "Tuned adapter settings applied" : "Some adapter settings couldn't be applied: " + o);
            return ok;
        }

        public string Reapply()
        {
            Cfg.AutoActions = true; Cfg.Save(); Harden();
            if (!Adapter.CanLockBands) return ApplyBaseProfile("re-applying optimized settings") ? "Optimized adapter settings applied (this adapter doesn't support band locking)." : "Couldn't apply the adapter settings. See the event list.";
            var mode = string.IsNullOrEmpty(St.CurrentMode) ? PreferredBand() : St.CurrentMode;
            bool ok = ApplyMode(mode, "re-applying optimized settings", true);
            return ok ? "Optimized settings applied; now on " + Describe(Wlan.GetConnection()) + "." : "Couldn't apply " + Profiles.Name(mode) + "; rolled back. See the event list.";
        }

        public string BandTest()
        {
            if (InGame) return "Close " + CurrentGame + " first — the band test drops the connection a few times.";
            var gw = Adapter.Gateway(); var sb = new StringBuilder(); var start = St.CurrentMode;
            var results = new List<Tuple<string, LatStats, LatStats, string, double>>();
            if (!Adapter.CanLockBands) return "This adapter's driver doesn't support Intel band locking, so the band test can't switch bands.";
            foreach (var b in new[] { "5", "6", "2.4" }.Where(BandAvailable))
            {
                if (!ApplyMode(b, "band test", false)) { results.Add(Tuple.Create(b, (LatStats)null, (LatStats)null, "unusable: " + AvoidSummary(), 1e9)); continue; }
                Thread.Sleep(8000);
                var c = Wlan.GetConnection(); gw = Adapter.Gateway();
                var r = Tests.MeasureMany(new[] { gw, "1.1.1.1" }, 40, 250, Cfg.StallMs);
                double score = r[0].P95 + 0.5 * r[1].P95 + 50 * (r[0].LossPct + r[1].LossPct) + 15 * r[0].Stalls;
                results.Add(Tuple.Create(b, r[0], r[1], Describe(c) + string.Format(", link {0}/{1} Mbps", c.RxMbps, c.TxMbps), score));
            }
            sb.AppendLine("Band test (40 s of pings per band; lower score is better):");
            foreach (var t in results)
            {
                sb.AppendLine(string.Format("  {0,-3} GHz  {1}", t.Item1, t.Item2 == null ? t.Item4 : string.Format("score {0,6:0}  —  {1}", t.Item5, t.Item4)));
                if (t.Item2 != null) { sb.AppendLine("           router:   " + t.Item2); sb.AppendLine("           internet: " + t.Item3); }
            }
            var best = results.Where(t => t.Item2 != null).OrderBy(t => t.Item5).FirstOrDefault();
            if (best == null) { ApplyMode(start, "band test finished", false); sb.AppendLine("No band was usable; restored the starting setup."); return sb.ToString(); }
            var order = new List<string> { best.Item1 }; order.AddRange(results.Where(t => t.Item2 != null && t.Item1 != best.Item1).OrderBy(t => t.Item5).Select(t => t.Item1)); order.AddRange(new[] { "5", "6", "2.4" }.Where(b => !order.Contains(b)));
            Cfg.BandOrder = order; Cfg.Save();
            ApplyMode(best.Item1, "band test winner", false);
            sb.AppendLine("Winner: " + best.Item1 + " GHz. New band order: " + string.Join(" > ", order));
            return sb.ToString();
        }

        public string StabilityTest(int minutes)
        {
            var startUtc = DateTime.UtcNow; var gw = Adapter.Gateway(); var c = Wlan.GetConnection();
            bool stop = false; long dl = 0;
            var loader = new Thread(() => { while (!stop) { try { dl += (long)(Tests.Download(3, 2) * 3 / 8); } catch { } for (int i = 0; i < 27 && !stop; i++) Thread.Sleep(1000); } }) { IsBackground = true };
            loader.Start();
            var r = Tests.MeasureMany(new[] { gw, "1.1.1.1" }, minutes * 60, 500, Cfg.StallMs);
            stop = true; loader.Join(5000);
            int crashes, warnings; lock (StLock) { crashes = St.Incidents.Count(i => i.Kind == "crash" && i.Time >= startUtc); warnings = St.Incidents.Count(i => i.Kind == "warning" && i.Time >= startUtc); }
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("Stability test: {0} min on {1}, with a short download burst every 30 s", minutes, Describe(c)));
            sb.AppendLine("  router:   " + r[0]); sb.AppendLine("  internet: " + r[1]);
            sb.AppendLine(string.Format("  driver crashes during test: {0}   driver warnings: {1}", crashes, warnings));
            sb.AppendLine(crashes == 0 && r[0].LossPct < 1 && r[0].Stalls <= minutes ? "  Verdict: solid." : "  Verdict: not clean — see the numbers above.");
            return sb.ToString();
        }

        // ---------- files
        void WriteSample(ConnInfo c)
        {
            try
            {
                var p10 = Pinger.Stats(Cfg.SampleSeconds);
                var f = Path.Combine(Paths.Samples, DateTime.Now.ToString("yyyy-MM-dd") + ".csv");
                if (!File.Exists(f)) File.WriteAllText(f, "time,state,ssid,bssid,ap,band,channel,rssi,rx_mbps,tx_mbps,gw_med,gw_max,gw_loss,net_med,net_max,net_loss,stalls,in_game,mode,busy\r\n");
                if (!ReadOnly) File.AppendAllText(f, string.Join(",", DateTime.Now.ToString("HH:mm:ss"), c.Connected ? "up" : "down", c.Ssid, c.Bssid, ApName(c.Bssid).Replace(' ', '_'), c.Band, c.Channel, c.Rssi, c.RxMbps, c.TxMbps,
                    p10.GwP50, p10.GwMax, p10.GwLost, p10.NetP50, p10.NetMax, p10.NetLost, p10.Stalls, InGame ? 1 : 0, St.CurrentMode, (Busy ?? "").Replace(',', ' ')) + "\r\n");
            }
            catch { }
        }
        void WriteStatus()
        {
            try
            {
                var c = Conn; var p = P60;
                var o = new
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), health = Health, level = Level, busy = Busy, paused = Paused, inGame = InGame, game = CurrentGame, gameFolders = Detector.RootCount, admin = IsAdmin, autoActions = Cfg.AutoActions,
                    rxMbps = Math.Round(RxMbps, 1), txMbps = Math.Round(TxMbps, 1), fastDownloadMode = St.TurboActive, wifiDirectPaused = St.WifiDirectDisabled.Count > 0,
                    session = session == null ? "" : session.Game + ", " + (int)(DateTime.Now - session.Start).TotalMinutes + " min",
                    connection = new { c.Connected, c.Ssid, c.Bssid, ap = ApName(c.Bssid), c.Band, c.Channel, c.Rssi, c.RxMbps, c.TxMbps },
                    ping60s = new { gwMedian = p.GwP50, gwP95 = p.GwP95, gwMax = p.GwMax, gwLossPct = p.GwLossPct, netMedian = p.NetP50, netP95 = p.NetP95, netMax = p.NetMax, netLossPct = p.NetLossPct, stalls = p.Stalls },
                    mode = St.CurrentMode, preferred = PreferredBand(), avoiding = AvoidSummary(), lowLatency = LowLatencyState,
                    driver = DriverVersion, crashes24h = CrashCount(24), crashes7d = CrashCount(168), udpEphemeral = UdpInUse, udpTop = UdpTop, dns = DnsTools.Current(), dnsChoice = St.DnsChoice
                };
                Json.WriteAtomic(Paths.StatusFile, Json.Pretty(Json.S().Serialize(o)));
            }
            catch { }
        }
        void Prune()
        {
            try { foreach (var f in Directory.GetFiles(Paths.Samples, "*.csv")) if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-Cfg.KeepSampleDays)) File.Delete(f); } catch { }
            lock (StLock) foreach (var k in St.Quarantine.Where(x => x.Value < DateTime.UtcNow).Select(x => x.Key).ToList()) St.Quarantine.Remove(k);
        }
    }

    // ---------------------------------------------------------------- UI: tray + status window
    class TrayApp : ApplicationContext
    {
        readonly Watchdog wd; readonly NotifyIcon ni; readonly Control ui; StatusForm form; public readonly Icon[] Icons;
        public static TrayApp Current;
        public TrayApp(Watchdog w, bool showWindow)
        {
            Current = this; wd = w; ui = new Control(); var unused = ui.Handle;
            int sz = SystemInformation.SmallIconSize.Width;
            Icons = new[] { Brand.StatusIcon(sz, null), MakeIcon(Color.FromArgb(214, 138, 0), sz), MakeIcon(Color.FromArgb(207, 34, 46), sz), MakeIcon(Color.FromArgb(128, 132, 140), sz) };
            ni = new NotifyIcon { Icon = Icons[3], Visible = true, Text = "AX210_WatchClaude — starting" };
            var m = new ContextMenuStrip();
            AX210WatchClaude.Ui.InitFonts(); m.Renderer = new MenuRenderer(Theme.Current()); m.Font = AX210WatchClaude.Ui.Body;
            m.Items.Add("Open AX210_WatchClaude", null, (s, e) => ShowStatus()); m.Items[0].Font = AX210WatchClaude.Ui.BodyBold; m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Network checkup", null, (s, e) => Checkup());
            m.Items.Add("Latency test (30 s)", null, (s, e) => Latency()); m.Items.Add("Speed test", null, (s, e) => Speed());
            m.Items.Add("Band test (~4 min)", null, (s, e) => BandTest()); m.Items.Add("Stability test (10 min)", null, (s, e) => Stability());
            m.Items.Add("DNS benchmark", null, (s, e) => Dns()); m.Items.Add(new ToolStripSeparator());
            var opts = new ToolStripMenuItem("Options");
            Func<string, string, Func<bool>, ToolStripMenuItem> Opt = (label, key, get) =>
            {
                var it = new ToolStripMenuItem(label);
                it.Click += (s, e) => { bool on = !get(); ThreadPool.QueueUserWorkItem(_ => { var r = wd.SetOption(key, on); OnUi(() => ni.ShowBalloonTip(4000, "AX210_WatchClaude", r, ToolTipIcon.Info)); }); };
                opts.DropDownItems.Add(it); return it;
            };
            var oWfd = Opt("Pause Wi-Fi Direct while gaming", "wfd", () => wd.Cfg.SuspendWifiDirectWhileGaming);
            var oRep = Opt("Game session reports", "reports", () => wd.Cfg.SessionReports);
            var oGrd = Opt("Warn about downloads while gaming", "guard", () => wd.Cfg.BandwidthGuard);
            var oUp = Opt("Upload limiter (cap non-game uploads)", "upload", () => wd.Cfg.UploadLimiter);
            var oTur = Opt("Fast downloads on 6 GHz", "turbo", () => wd.Cfg.TurboDownloads);
            opts.DropDownOpening += (s, e) => { oWfd.Checked = wd.Cfg.SuspendWifiDirectWhileGaming; oRep.Checked = wd.Cfg.SessionReports; oGrd.Checked = wd.Cfg.BandwidthGuard; oUp.Checked = wd.Cfg.UploadLimiter; oTur.Checked = wd.Cfg.TurboDownloads; };
            m.Items.Add(opts); opts.DropDown.Renderer = m.Renderer; opts.DropDown.Font = AX210WatchClaude.Ui.Body;
            m.Items.Add("Pause / resume actions", null, (s, e) => TogglePause());
            m.Items.Add("Re-apply optimized settings", null, (s, e) => Run("Re-apply optimized settings", () => wd.Reapply()));
            m.Items.Add("Restore original Wi-Fi settings…", null, (s, e) => { if (MessageBox.Show("Put the Wi-Fi adapter and DNS back the way they were before AX210_WatchClaude, and switch automatic actions off?", "AX210_WatchClaude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Run("Restore original settings", () => wd.RestoreOriginal()); });
            m.Items.Add("Roll back Wi-Fi driver…", null, (s, e) => RollbackDriver());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Open logs folder", null, (s, e) => OpenLogs());
            m.Items.Add("Settings…", null, (s, e) => ShowSettings());
            m.Items.Add("Setup assistant…", null, (s, e) => ShowSetup());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit (until next sign-in)", null, (s, e) => ExitApp("Exited from the tray menu", true));
            ni.ContextMenuStrip = m;
            ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
            wd.Notify += (t, msg, icon) => OnUi(() => { if (wd.Cfg.Notifications) ni.ShowBalloonTip(6000, t, msg, icon); });
            wd.Updated += () => OnUi(UpdateIcon);
            if (!wd.IsAdmin) ni.ShowBalloonTip(8000, "AX210_WatchClaude: monitor only", "Running without administrator rights, so it watches and tests but can't change settings. On a standard Windows account that's expected; on an administrator account, exit it and start it again, then allow the prompt.", ToolTipIcon.Warning);
            Single.Listen(() => OnUi(ShowStatus), () => OnUi(() => ExitApp("Stopped by the installer", false)));
            if (Config.Broken) ni.ShowBalloonTip(15000, "config.json has a mistake", "Running on default settings until it's fixed. Your file wasn't changed (a copy is in config.json.broken). Fix it and use Settings → Reload from file, or save new settings.", ToolTipIcon.Warning);
            var t0 = new System.Windows.Forms.Timer { Interval = 2500 };
            t0.Tick += (s, e) =>
            {
                t0.Stop(); t0.Dispose();
                if (!wd.St.SetupDone && wd.Wlan.Ok && wd.CanAct)   // first run: set up automatically, no dialog
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string r = null; try { r = wd.AutoSetup(); } catch (Exception ex) { Log.Write("ERROR", "automatic setup: " + ex.Message); }
                        if (r != null) OnUi(() => ni.ShowBalloonTip(12000, "Set up for this PC", r + ". Review or change it any time: Settings → Setup assistant.", ToolTipIcon.Info));
                    });
                if (showWindow) ShowStatus();
            };
            t0.Start();
        }
        /// userExit: the tray's Exit, which keeps it off until the next sign-in. The installer's stop isn't one: the new
        /// version must be able to start right away.
        void ExitApp(string why, bool userExit)
        {
            ni.Visible = false; if (userExit) ExitFlag.Set(); wd.RestoreWifiDirect("app exited"); wd.Save(); Log.Write("START", why); ExitThread();
        }
        void OnUi(Action a) { try { if (ui.IsHandleCreated) ui.BeginInvoke(a); } catch { } }
        internal static Icon MakeIcon(Color c, int size = 32) { return Brand.StatusIcon(size, c); }
        SetupForm setup;
        public void ShowSetup()
        {
            if (setup == null || setup.IsDisposed) setup = new SetupForm(wd, msg => { OnUi(() => { ni.ShowBalloonTip(5000, "AX210_WatchClaude is set up", msg, ToolTipIcon.Info); ShowStatus(); }); });
            setup.Show(); if (setup.WindowState == FormWindowState.Minimized) setup.WindowState = FormWindowState.Normal; setup.Activate();
        }
        void UpdateIcon()
        {
            ni.Icon = Icons[Math.Max(0, Math.Min(3, wd.Level))];
            var c = wd.Conn; var p = wd.P60;
            string t = c.Connected ? string.Format("AX210 Watch: {0}GHz ch{1} {2}dBm {3:0}/{4:0}ms", c.Band, c.Channel, c.Rssi, p.GwP50, p.NetP50) : "AX210 Watch: " + wd.Health;
            if (!string.IsNullOrEmpty(wd.Busy)) t = "AX210 Watch: " + wd.Busy;
            ni.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }
        public void ShowStatus()
        {
            if (form == null || form.IsDisposed) form = new StatusForm(wd, this);
            form.Show(); if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate(); form.RefreshView();
        }
        void Run(string name, Func<string> f)
        {
            if (!wd.TryBeginTest(name)) { ni.ShowBalloonTip(3000, "Busy", "Something else is running: " + wd.Busy, ToolTipIcon.Info); return; }
            ShowStatus(); form.ShowResult(name, "Running…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string r; try { r = f(); } catch (Exception ex) { r = "Error: " + ex.Message; } finally { wd.EndTest(); }
                Log.Bench(name, r); Log.Write("TEST", name + " finished");
                OnUi(() => { if (form != null && !form.IsDisposed) form.ShowResult(name, r); var first = r.Split('\n').FirstOrDefault(x => x.Trim() != "") ?? ""; ni.ShowBalloonTip(5000, name + " finished", first.Trim().Length > 200 ? first.Trim().Substring(0, 200) : first.Trim(), ToolTipIcon.Info); });
            });
        }
        public void Latency() { Run("Latency test", () => Tests.Latency(Adapter.Gateway(), 30)); }
        public void Speed() { Run("Speed test", wd.SpeedTest); }
        public void Checkup() { Run("Network checkup", wd.Checkup); }
        SettingsForm settings;
        public void ShowSettings()
        {
            if (settings == null || settings.IsDisposed) settings = new SettingsForm(wd, msg => OnUi(() => ni.ShowBalloonTip(5000, "Settings saved", msg, ToolTipIcon.Info)));
            settings.Show(); if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal; settings.Activate();
        }
        public void RollbackDriver()
        {
            var list = wd.DriverBackups();
            if (list.Count == 0) { MessageBox.Show("There's no saved copy of an older Intel driver yet.\n\nThe app saves the driver you're using now, so if a future update turns out worse, you'll be able to come back to this one.", "AX210_WatchClaude"); return; }
            var v = list[0];
            if (MessageBox.Show("Replace Intel driver " + wd.DriverVersion + " with the saved " + v + "?\n\nWi-Fi will drop for up to a minute while Windows installs it.", "AX210_WatchClaude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (wd.InGame && MessageBox.Show(wd.CurrentGame + " is running. Roll back anyway?", "AX210_WatchClaude", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Run("Driver rollback", () => wd.RollbackDriver(v));
        }
        public void Dns() { Run("DNS benchmark", () => wd.RunDns(true)); }
        public void Stability() { Run("Stability test", () => wd.StabilityTest(10)); }
        public void BandTest()
        {
            if (!wd.IsAdmin) { MessageBox.Show("The band test needs administrator rights.", "AX210_WatchClaude"); return; }
            if (MessageBox.Show("The band test tries 5, 6 and 2.4 GHz for about a minute each. Your connection will drop a few times. Start now?", "AX210_WatchClaude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Run("Band test", wd.BandTest);
        }
        public void OpenLogs() { try { Process.Start("explorer.exe", Paths.Data); } catch { } }
        public void TogglePause()
        {
            if (wd.Paused) { wd.Paused = false; Log.Write("INFO", "Automatic actions resumed"); }
            else { wd.Paused = true; wd.PausedUntil = DateTime.Now.AddHours(1); Log.Write("INFO", "Automatic actions paused for 1 hour"); }
            if (form != null && !form.IsDisposed) form.RefreshView();
        }
    }

    // ---------------------------------------------------------------- entry point
    static class Program
    {
        public static readonly string Version = typeof(Program).Assembly.GetName().Version.ToString(3);

        //  (no args)          what the shortcuts run: show the window, starting the app through its scheduled task if needed (no UAC prompt)
        //  --tray             what the scheduled task runs: start quietly in the tray, in place (respects "Exit until next sign-in")
        //  --background       like no args, but doesn't open a window (the installer's silent mode)
        //  --register-task [--autostart] | --unregister-task | --stop | --restore-original | --cleanup    installer hooks
        //  --selftest <file> [--dns] [--checkup] | --render <png> [view] [--redact]                          diagnostics
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--selftest") { SelfTest.Run(args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "AX210_WatchClaude_selftest.txt"), args.Contains("--dns"), args.Contains("--checkup")); return; }
            if (args.Length >= 2 && args[0] == "--render") { Ui.Redact = args.Contains("--redact"); Preview.Render(args[1], args.Length >= 3 && !args[2].StartsWith("--") ? int.Parse(args[2]) : 0); return; }
            if (args.Contains("--register-task")) { Environment.ExitCode = Setup.RegisterTask(args.Contains("--autostart")) ? 0 : 1; return; }
            if (args.Contains("--unregister-task")) { Setup.UnregisterTask(); return; }
            if (args.Contains("--stop")) { Single.StopRunning(); return; }
            if (args.Contains("--restore-original")) { Setup.RestoreOriginal(); return; }
            if (args.Contains("--cleanup")) { Setup.Cleanup(); return; }

            bool tray = args.Contains("--tray"), background = args.Contains("--background");
            if (tray && ExitFlag.ExitedThisSession()) return;   // user chose Exit this sign-in; the 5-minute self-restart respects that
            if (Single.IsRunning()) { if (!tray && !background) Single.SignalShow(); return; }
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (!tray) ExitFlag.Clear();
            // --tray is the task itself: it runs in place, never relaunches (on a standard account the task's token isn't
            // elevated, and relaunching through the already-running task would just exit) -> monitor-only there.
            if (!admin && !tray && !args.Contains("--monitor-only"))
            {
                if (Setup.RunTask()) { if (!background) Single.WaitAndSignalShow(25000); return; }   // starts elevated with no prompt
                if (background) return;
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "") { UseShellExecute = true, Verb = "runas" }); return; }
                catch { /* UAC declined: fall through to monitor-only */ }
            }
            bool created;
            using (var mutex = new Mutex(true, Single.MutexName, out created))
            {
                if (!created) { if (!tray) Single.SignalShow(); return; }
                Paths.Ensure();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("ERROR", "Unhandled: " + e.ExceptionObject);
                Application.ThreadException += (s, e) => Log.Write("ERROR", "UI: " + e.Exception);
                var wd = new Watchdog(); wd.Start();
                Application.Run(new TrayApp(wd, !tray && !background));
                wd.Save();
            }
        }
    }

    static class Preview
    {
        /// Dev aid: renders the status window to a PNG (read-only monitor instance, no tray icon, nothing written).
        public static void Render(string png, int tab)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Log.Silent = true; Json.ReadOnly = true;
            var wd = new Watchdog { ReadOnly = true }; wd.Start();
            if (Ui.Redact) { Ui.RedactWords[wd.Cfg.HomeSsid ?? ""] = "HomeWiFi"; Ui.RedactWords[Environment.MachineName] = "GAMING-PC"; }
            var until = DateTime.Now.AddSeconds(14); while (DateTime.Now < until) { Application.DoEvents(); Thread.Sleep(100); }
            if (tab == 12)
            {
                var su = new SetupForm(wd, null) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
                su.FillNow(); su.Show(); for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
                Capture(su, png); Environment.Exit(0);
            }
            if (tab >= 9)
            {
                var sf = new SettingsForm(wd, null) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
                sf.Show(); for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
                if (tab == 10) sf.ScrollForPreview(1.0); else if (tab == 11) sf.ScrollForPreview(0.42);
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
                Capture(sf, png); Environment.Exit(0);
            }
            var f = new StatusForm(wd, null) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
            f.Show(); for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
            if (tab == 5) f.ShowResult("Network checkup", wd.Checkup());
            f.RefreshView(); for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
            if (tab > 0 && tab < 5) f.SelectTabForPreview(tab);
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
            Capture(f, png);
            Environment.Exit(0);
        }
        static void Capture(Form f, string png)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(f.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); } finally { g.ReleaseHdc(hdc); } }
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    }

    // ---------------------------------------------------------------- self-test (read-only; no admin needed)
    static class SelfTest
    {
        public static void Run(string outFile, bool dns, bool checkup)
        {
            var sb = new StringBuilder(); string ssidForHistory = "";
            try
            {
                if (checkup) { sb.AppendLine(Watchdog.SelfCheckup()); File.WriteAllText(outFile, sb.ToString()); return; }
                sb.AppendLine("AX210_WatchClaude self-test " + DateTime.Now);
                sb.AppendLine("Admin: " + new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
                using (var w = new WlanClient())
                {
                    sb.AppendLine("WLAN ok=" + w.Ok + " iface=" + w.Iface + " desc=" + w.Description);
                    Adapter.Init(w.Iface);
                    sb.AppendLine("Adapter name='" + Adapter.Name + "' classKeyFound=" + Adapter.Found + " driver=" + Adapter.DriverVersion());
                    var c = w.GetConnection();
                    sb.AppendLine(string.Format("Conn: connected={0} profile={1} ssid={2} bssid={3} band={4} ch={5} freq={6} rssi={7} q={8} rx={9} tx={10} phy={11}", c.Connected, c.Profile, c.Ssid, c.Bssid, c.Band, c.Channel, c.FreqMHz, c.Rssi, c.Quality, c.RxMbps, c.TxMbps, c.Phy));
                    int bg, st; w.Query(WlanNative.OpBackgroundScan, out bg); w.Query(WlanNative.OpStreamingMode, out st);
                    sb.AppendLine("Background scan=" + bg + " streaming mode=" + st);
                    var list = w.GetBssList();
                    sb.AppendLine("BSS entries: " + list.Count);
                    foreach (var b in list.Where(x => x.Ssid == c.Ssid)) sb.AppendLine(string.Format("  home: {0} {1} GHz ch {2} {3} dBm", b.Bssid, b.Band, b.Channel, b.Rssi));
                    sb.AppendLine("  busiest 5 GHz channels: " + string.Join(", ", list.Where(x => x.Band == "5").GroupBy(x => x.Channel).OrderByDescending(g => g.Count()).Take(4).Select(g => "ch " + g.Key + " ×" + g.Count())));
                    sb.AppendLine("Intel=" + Adapter.IsIntel + " canLockBands=" + Adapter.CanLockBands + " supports6=" + Adapter.Supports6);
                    ssidForHistory = c.Ssid;
                }
                var props = Adapter.Read(Profiles.AllKeys);
                sb.AppendLine("Props: " + string.Join(", ", props.Select(kv => kv.Key + "=" + kv.Value)));
                sb.AppendLine("Inferred mode: " + Profiles.InferMode(props) + "   drift vs '5': " + string.Join(", ", Profiles.Target("5").Where(kv => props.ContainsKey(kv.Key) && props[kv.Key] != null && props[kv.Key] != kv.Value).Select(kv => kv.Key + " " + props[kv.Key] + "->" + kv.Value)));
                var gw = Adapter.Gateway(); sb.AppendLine("Gateway: " + gw + "  ifIndex " + Adapter.IfIndex() + "  DNS: " + string.Join(", ", DnsTools.Current()) + "  static DNS: " + string.Join(", ", DnsTools.ReadStatic()));
                var q = new EventLogQuery("System", PathType.LogName, "*[System[(EventID=5002)]]") { ReverseDirection = true };
                using (var rdr = new EventLogReader(q))
                {
                    EventRecord r; int n = 0;
                    while ((r = rdr.ReadEvent()) != null && n < 3) using (r) { if (!(r.ProviderName ?? "").StartsWith("Netwtw")) continue; n++; var d = Decoder.Decode5002(Decoder.HexFromXml(r.ToXml())); sb.AppendLine("Last 5002 @" + r.TimeCreated + ": " + d.Title + " — " + d.Detail + " — " + d.Known); }
                }
                string summary; var h = EventMonitor.ImportHistory(ssidForHistory, State.Load().KnownBands, out summary);
                sb.AppendLine(summary);
                foreach (var kv in h.OrderByDescending(x => x.Value[1]).Take(8)) sb.AppendLine(string.Format("  {0,-14} assoc {1,4}  crashes {2,4}", Watchdog.KeyLabel(kv.Key), kv.Value[0], kv.Value[1]));
                int total; var u = UdpTable.EphemeralByPid(out total); sb.AppendLine("UDP ephemeral in use: " + total + "  top: " + UdpTable.Top(u, 3));
                var gcfg = new Config(); foreach (var n in GameList.Popular) if (!gcfg.GameProcesses.Contains(n)) gcfg.GameProcesses.Add(n);
                var gd = new GameDetector(); gd.Discover(gcfg);
                sb.AppendLine("Game folders (" + gd.RootCount + "):"); foreach (var r in gd.RootList) sb.AppendLine("  " + r);
                foreach (var tp in new[] { @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64\deadlock.exe", @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
                    @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe", Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Roblox\Versions\version-x\RobloxPlayerBeta.exe"), @"C:\Windows\explorer.exe" })
                { string g; sb.AppendLine(string.Format("  test {0,-5} {1}  <- {2}", gd.IsGamePath(tp, gcfg, out g), g, tp)); }
                string gn, gp; bool nm; sb.AppendLine("Game running right now: " + (gd.Detect(gcfg, out gn, out gp, out nm) ? GameList.DisplayName(gn) : "none"));
                sb.AppendLine("Latency 5 s: " + Tests.Measure(gw, 5, 250, 100));
                if (dns) { string choice; sb.AppendLine(DnsTools.BenchmarkAndMaybeApply(false, out choice)); sb.AppendLine("Would choose: " + choice); }
            }
            catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
