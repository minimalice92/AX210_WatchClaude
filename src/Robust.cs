// =====================================================================================
//  AX210_WatchClaude — 1.0.3 additions (Claude, 2026-11-26)
//   Robustness: the card vanishing (Code 10/43) or Wi-Fi switched off; radar (DFS) channel changes;
//               Windows Update replacing the driver; Bluetooth sharing the card's antenna
//   Use cases:  laptops on battery; Ethernet plugged in; streaming/VR/cloud gaming; several home networks;
//               mesh systems (node hold during games); metered connections
//   Upkeep:     settings + history backup/restore; update notice; weekly summary; this PC's own hardware address
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AX210WatchClaude
{
    public class OutageRec { public DateTime Start { get; set; } public double Seconds { get; set; } public string Cause { get; set; } }

    /// Runs a console program quietly: its exit code, or -1 on failure or timeout.
    static class Exe
    {
        public static int Run(string file, string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
                    output = (so.Result + se.Result).Trim(); return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }
        public static string Sys(string name) { return Path.Combine(Environment.SystemDirectory, name); }
    }

    // ---------------------------------------------------------------- Plug and Play (cfgmgr32)
    static class Pnp
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNode(out uint devInst, string id, int flags);
        [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_Size(out int len, string filter, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List(string filter, char[] buffer, int len, int flags);

        /// The device's problem code (0 = none; 10/43 = failed; 22 = disabled) and whether it's present at all.
        public static void Status(string id, out int code, out bool present)
        {
            code = 0; present = false; if (string.IsNullOrEmpty(id)) return;
            uint dev; if (CM_Locate_DevNode(out dev, id, 0) != 0) return;   // CR_NO_SUCH_DEVNODE: gone from the bus
            present = true; uint st, prob;
            if (CM_Get_DevNode_Status(out st, out prob, dev, 0) == 0 && (st & 0x400) != 0) code = (int)prob;   // DN_HAS_PROBLEM
        }
        /// Windows knows the device at all, present or not (false once it has been uninstalled).
        public static bool Exists(string id) { uint dev; return !string.IsNullOrEmpty(id) && CM_Locate_DevNode(out dev, id, 1 /* CM_LOCATE_DEVNODE_PHANTOM */) == 0; }
        /// Instance IDs of present devices from one enumerator ("HID", "BTHENUM", …).
        public static List<string> Present(string enumerator)
        {
            var r = new List<string>();
            try
            {
                int len; const int flags = 0x1 | 0x100;   // CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT
                if (CM_Get_Device_ID_List_Size(out len, enumerator, flags) != 0 || len <= 1) return r;
                var buf = new char[len];
                if (CM_Get_Device_ID_List(enumerator, buf, len, flags) != 0) return r;
                r.AddRange(new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch { }
            return r;
        }
    }

    // ---------------------------------------------------------------- metered connection (Network List Manager)
    static class NetCost
    {
        [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface INetworkCostManager { void GetCost(out uint cost, IntPtr destIPAddr); }
        static readonly Guid NetworkListManager = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
        /// True when Windows treats the internet connection as metered: fixed or variable cost, roaming, or over its data limit.
        public static bool IsMetered()
        {
            object o = null;
            try
            {
                o = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManager));
                uint cost; ((INetworkCostManager)o).GetCost(out cost, IntPtr.Zero);
                return (cost & (0x2 | 0x4 | 0x10000 | 0x40000)) != 0;
            }
            catch { return false; }
            finally { if (o != null) try { Marshal.ReleaseComObject(o); } catch { } }
        }
    }

    // ---------------------------------------------------------------- Ethernet
    static class Wired
    {
        [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
        static readonly Regex Virtual = new Regex("virtual|hyper-v|vmware|virtualbox|vpn|\\btap\\b|tap-windows|wireguard|tailscale|zerotier|loopback|bluetooth|wan miniport|npcap|kernel debug|teredo|isatap|anyconnect|fortinet|openvpn|vethernet", RegexOptions.IgnoreCase);
        /// The wired adapter carrying the internet traffic, or null: a physical Ethernet adapter that's up with a default
        /// gateway while the route to the internet doesn't go out over Wi-Fi.
        public static string Active(Guid wifi)
        {
            try
            {
                uint best; if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0), out best) != 0) return null;
                var all = NetworkInterface.GetAllNetworkInterfaces();   // one enumeration for both the Wi-Fi index and the wired candidates
                var w = all.FirstOrDefault(n => string.Equals(n.Id, wifi.ToString("B"), StringComparison.OrdinalIgnoreCase));
                int wifiIfIndex = -1; if (w != null) try { wifiIfIndex = w.GetIPProperties().GetIPv4Properties().Index; } catch { }
                if (wifiIfIndex > 0 && best == (uint)wifiIfIndex) return null;   // the internet goes over Wi-Fi
                foreach (var ni in all)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var t = ni.NetworkInterfaceType;
                    if (t != NetworkInterfaceType.Ethernet && t != NetworkInterfaceType.GigabitEthernet && t != NetworkInterfaceType.FastEthernetT && t != NetworkInterfaceType.FastEthernetFx) continue;
                    if (Virtual.IsMatch(ni.Description + " " + ni.Name)) continue;
                    var ip = ni.GetIPProperties();
                    if (!ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                    int idx; try { idx = ip.GetIPv4Properties().Index; } catch { continue; }
                    if (idx != best) continue;   // up with a gateway, but the internet route goes elsewhere (a dock's port, a VPN)
                    return ni.Description;
                }
            }
            catch { }
            return null;
        }
    }

    // ---------------------------------------------------------------- Bluetooth (shares the card's antenna and 2.4 GHz)
    static class Bt
    {
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices); }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection { [PreserveSig] int GetCount(out int count); [PreserveSig] int Item(int index, out IMMDevice device); }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store); }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetAt(int i, out PROPERTYKEY key); [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT v); }
        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
        static readonly Guid DevProps = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");   // PKEY_Device_*: 14 = friendly name, 24 = enumerator

        static string Str(IPropertyStore ps, int pid)
        {
            var k = new PROPERTYKEY { fmtid = DevProps, pid = pid }; PROPVARIANT v;
            if (ps.GetValue(ref k, out v) != 0) return "";
            try { return v.vt == 31 /* VT_LPWSTR */ && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) ?? "" : ""; } finally { PropVariantClear(ref v); }
        }

        /// Bluetooth audio connected right now (active endpoints whose device sits on Bluetooth), as device names.
        public static List<string> Audio()
        {
            var r = new List<string>(); object e = null;
            try
            {
                e = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                IMMDeviceCollection col; if (((IMMDeviceEnumerator)e).EnumAudioEndpoints(2 /* render + capture */, 1 /* active */, out col) != 0 || col == null) return r;
                int n; col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d; if (col.Item(i, out d) != 0 || d == null) continue;
                    IPropertyStore ps = null;
                    try
                    {
                        if (d.OpenPropertyStore(0, out ps) != 0 || ps == null) continue;
                        if (!Str(ps, 24).StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;   // BTHENUM (classic) / BTHLE…
                        var name = Str(ps, 14); var m = Regex.Match(name, @"\((.+)\)\s*$"); if (m.Success) name = m.Groups[1].Value;
                        name = Regex.Replace(name, @"\s+(Hands-Free.*|Stereo|AG Audio)$", "", RegexOptions.IgnoreCase).Trim();
                        r.Add(Ui.Redact ? "Bluetooth headphones" : name == "" ? "Bluetooth audio" : name);
                    }
                    finally { if (ps != null) Marshal.ReleaseComObject(ps); Marshal.ReleaseComObject(d); }
                }
                Marshal.ReleaseComObject(col);
            }
            catch { }
            finally { if (e != null) try { Marshal.ReleaseComObject(e); } catch { } }
            return r.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// Bluetooth controllers, mice and keyboards connected right now (present HID devices on Bluetooth classic or LE).
        public static int Hid()
        {
            return Pnp.Present("HID")
                .Where(id => id.IndexOf("{00001124-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("{00001812-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(id => { var p = id.Split('\\'); return Regex.Replace(p.Length > 1 ? p[1] : id, @"&Col\d+", "", RegexOptions.IgnoreCase); })
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        /// "WH-1000XM4, 1 controller/mouse/keyboard" for what's connected over Bluetooth right now, or "".
        public static string Connected()
        {
            var parts = new List<string>(Audio());
            int hid = Hid(); if (hid > 0) parts.Add(hid == 1 ? "1 controller/mouse/keyboard" : hid + " controllers/mice/keyboards");
            return string.Join(", ", parts);
        }
    }

    // ---------------------------------------------------------------- update notice
    static class Updates
    {
        const string Repo = "https://github.com/minimalice92/AX210_WatchClaude";
        public const string Releases = Repo + "/releases/latest";
        const string Api = "https://api.github.com/repos/minimalice92/AX210_WatchClaude/releases/latest";

        /// "v1.2.3" / "1.2" -> a version; null when it isn't one.
        public static Version Parse(string tag)
        {
            var m = Regex.Match(tag ?? "", @"^\s*[vV]?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
        }
        public static bool IsNewer(string tag, string current) { var a = Parse(tag); var b = Parse(current); return a != null && b != null && a > b; }

        /// The latest published release: (tag, page URL), or null. The request carries only the app's name and version.
        public static Tuple<string, string> Latest()
        {
            try
            {
                Tests.Prep();
                var req = (HttpWebRequest)WebRequest.Create(Api);
                req.UserAgent = "AX210_WatchClaude/" + Program.Version; req.Accept = "application/vnd.github+json"; req.Timeout = 10000; req.ReadWriteTimeout = 10000;
                string body; using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) body = sr.ReadToEnd();
                var d = Json.S().Deserialize<Dictionary<string, object>>(body); object tag, url, draft, pre;
                if (d == null || !d.TryGetValue("tag_name", out tag) || !(tag is string) || Parse((string)tag) == null) return null;
                if ((d.TryGetValue("draft", out draft) && draft is bool && (bool)draft) || (d.TryGetValue("prerelease", out pre) && pre is bool && (bool)pre)) return null;
                d.TryGetValue("html_url", out url); var u = url as string ?? "";
                if (!u.StartsWith(Repo + "/releases/", StringComparison.Ordinal)) u = Releases;   // only ever opens this project's own release page
                return Tuple.Create((string)tag, u);
            }
            catch { return null; }
        }
    }

    // ---------------------------------------------------------------- backup file
    /// Settings and learned history. Leaves out what only fits the PC it came from: the pre-app "original" settings,
    /// last-known-good adapter values, saved profiles, QoS policy names and the card's hardware ID.
    public class BackupFile
    {
        public string App { get; set; } public string Version { get; set; } public DateTime Exported { get; set; }
        public Config Config { get; set; }
        public Dictionary<string, ChannelStat> Channels { get; set; }
        public Dictionary<string, string> KnownBands { get; set; }
        public List<IncidentRec> Incidents { get; set; }
        public List<SessionRec> Sessions { get; set; }
        public Dictionary<string, DriverStat> DriverStats { get; set; }
        public Dictionary<string, GameRec> Games { get; set; }
        public List<OutageRec> Outages { get; set; }
        public double LastDownloadMbps { get; set; } public double LastUploadMbps { get; set; }
    }

    partial class Watchdog
    {
        public volatile bool CardMissing, RadioIsOff, Metered; public volatile string WiredName;
        DateTime? missingSince; volatile int vanishStep; volatile string vanishCode = ""; bool cardWasMissing;
        DateTime lastWired, lastCost, nextMacCheck = DateTime.Now.AddMinutes(3), lastMeshSteer;
        volatile bool updateChecking;

        /// The card the app looks after is remembered by its Plug and Play ID. Init decides at start-up whether the adapter
        /// Windows listed is that card, a replacement for it (TakeOver), or another adapter while it's down (StandIn).
        void RobustStart()
        {
            if (StandIn) Log.Write("INFO", "Windows lists another Wi-Fi adapter but not this PC's own card; watching for the card and leaving the other adapter alone");
        }
        /// This PC's card is down and Windows lists only another adapter (a USB one, say): the app waits for its card and
        /// leaves the other adapter alone, so nothing of it (driver, settings, hardware ID) gets into the card's records.
        public volatile bool StandIn;
        bool OwnCardInUse() { string saved; lock (StLock) saved = St.WifiPnpId; return string.IsNullOrEmpty(saved) || string.Equals(Adapter.InstanceId, saved, StringComparison.OrdinalIgnoreCase); }

        /// Is `other` this PC's card from now on?
        ///  * A fresh install (no card and no history known), or a PC with no internal Wi-Fi card at all: whatever Windows lists.
        ///  * No saved card but history from an earlier version (1.0.2 kept no card ID): an internal (PCI) card, the one
        ///    whose hardware ID matches the saved one, or any adapter when no internal card is in the PC (it fell off the
        ///    bus). The saved settings stay with the card they came from either way (EnsureOriginals).
        ///  * A saved card: an internal card takes over from a USB one at once, and from an internal one once that's
        ///    uninstalled or gone from the bus. A crashed, hung or disabled card stays this PC's card (recovered or waited
        ///    for); a USB adapter takes over from it only after a long absence (AdapterWatch), so the app stays useful.
        public static bool TakeOver(string saved, string other, bool history, string savedHwid, string otherHwid, bool internalExists)
        {
            if (string.IsNullOrEmpty(other)) return false;
            bool internalCard = IsInternal(other);
            if (string.IsNullOrEmpty(saved))
                return !history || internalCard || !internalExists || (!string.IsNullOrEmpty(savedHwid) && string.Equals(savedHwid, otherHwid, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(saved, other, StringComparison.OrdinalIgnoreCase)) return false;
            if (!internalCard)
            {
                if (IsInternal(saved)) return false;   // a USB adapter never replaces a known internal card here (only the 30-minute fallback)
                if (!Pnp.Exists(saved)) return true;   // a USB-only PC: its dongle was replaced, or moved to another port (new device ID)
                int c0; bool on0; Pnp.Status(saved, out c0, out on0); return !on0;
            }
            if (!IsInternal(saved)) return true;
            if (!Pnp.Exists(saved)) return true;
            int code; bool onBus; Pnp.Status(saved, out code, out onBus);
            return !onBus;
        }
        /// TakeOver with this PC's saved card and history filled in.
        bool TakesOver(string other)
        {
            string saved, hwid; bool history;
            lock (StLock) { saved = St.WifiPnpId; hwid = St.DriverHardwareId; history = St.OriginalProps.Count > 0 || !string.IsNullOrEmpty(St.DriverHardwareId) || !string.IsNullOrEmpty(St.LastDriverVersion); }
            return TakeOver(saved, other, history, hwid, Adapter.HardwareIdOf(other), Adapter.InternalWifiKnown());
        }

        /// From now on, look after this adapter: a replacement card, this PC's internal card coming back after a USB
        /// adapter, or another adapter after the card stayed gone a long time. Whose saved settings are whose follows the
        /// one rule in EnsureOriginals. Its driver is learned, not announced. Without a readable device ID it isn't
        /// adopted (it couldn't be recognized on the next tick).
        void Adopt(Tuple<Guid, string, bool> x, string why)
        {
            if (Adapter.InstanceIdOf(x.Item1) == "") return;
            Wlan.Use(x.Item1, x.Item2); Adapter.Init(x.Item1); StandIn = false;
            var pid = Adapter.InstanceId;
            lock (StLock) if (pid != "") St.WifiPnpId = pid;
            EnsureOriginals();   // whose saved settings are whose; this card's originals if it has none
            missingSince = null; vanishStep = 0; vanishCode = ""; CardMissing = false; WlanServiceDown = false;
            lastProfileCheck = DateTime.MinValue; resumeGrace = DateTime.Now.AddSeconds(40); ladderFrom = resumeGrace; ladder = 0;
            var v = Adapter.DriverVersion(); lock (driverLock) DriverVersion = v;
            if (v != "?") { lock (StLock) St.LastDriverVersion = v; OnDriverChanged(); }
            Save();
            if (MayChange) ThreadPool.QueueUserWorkItem(_ => Harden());
            Log.Write("INFO", "Looking after " + x.Item2 + " from now on (" + why + ")");
        }

        void RobustTick(ConnInfo c, DateTime now)
        {
            if ((now - lastWired).TotalSeconds >= 15)
            {
                lastWired = now; var w = Cfg.EthernetStandDown ? Wired.Active(Adapter.Guid) : null;
                if (w != WiredName) { Log.Write("CONN", w != null ? "Ethernet carries the internet traffic (" + w + "); Wi-Fi is watched only" : "Ethernet no longer in use; managing Wi-Fi again"); WiredName = w; }
            }
            if ((now - lastCost).TotalSeconds >= 60)
            {
                lastCost = now; bool m = NetCost.IsMetered();
                if (m != Metered) { Metered = m; Log.Write("INFO", m ? "This connection is metered: data-heavy tests ask first and the update check waits" : "The connection is no longer metered", false); }
            }
            DfsWatch(c);
            MeshWatch(c, now);
            if (MayChange && Cfg.FixedHardwareAddress && now >= nextMacCheck && c.Connected && IsHome(c.Ssid) && !InGame && WiredName == null && !ActionBusy && !Paused) { nextMacCheck = now.AddHours(6); MacCheck(c); }
            if (Cfg.UpdateCheck && !ReadOnly && !Metered && !updateChecking && (now - started).TotalMinutes >= 10 && (DateTime.UtcNow - St.LastUpdateCheck).TotalDays >= 7) { updateChecking = true; ThreadPool.QueueUserWorkItem(_ => CheckForUpdate()); }
            if (Cfg.WeeklySummary && !ReadOnly && !InGame && (now - started).TotalMinutes >= 10) WeeklyDue();
        }

        // ---------- the card vanished (Code 10/43, fell off the bus) or Wi-Fi is switched off
        DateTime vanishCalm, lastDisabledCheck; public DateTime vanishHoldUntil; public volatile bool CardAbsent; string missingCause = "";
        int vanishRunning; volatile bool reenabledCutOff; public DateTime absentSince = DateTime.MaxValue; DateTime lastInternalCheck;
        string CardId() { string id; lock (StLock) id = St.WifiPnpId; return string.IsNullOrEmpty(id) ? Adapter.InstanceId : id; }

        /// This PC's Wi-Fi card among the listed interfaces: the same Plug and Play device as before (Windows may give it a
        /// new interface GUID when it re-adds it), or the best card when the app never knew one. Another adapter, such as a
        /// USB dongle, doesn't stand in for a crashed card.
        Tuple<Guid, string, bool> FindOwnCard()
        {
            var l = Wlan.Interfaces(); if (l == null || l.Count == 0) return null;
            string id; lock (StLock) id = St.WifiPnpId;
            // no card ID saved yet: the best-ranked adapter the takeover rule allows (after an upgrade, not a USB adapter
            // that happens to be listed while the internal card is down)
            if (string.IsNullOrEmpty(id) && !Adapter.Found) return l.OrderByDescending(x => WlanClient.Score(x.Item2)).FirstOrDefault(x => TakesOver(Adapter.InstanceIdOf(x.Item1)));
            if (string.IsNullOrEmpty(id)) return l.FirstOrDefault(x => x.Item1 == Wlan.Iface);
            return l.FirstOrDefault(x => string.Equals(Adapter.InstanceIdOf(x.Item1), id, StringComparison.OrdinalIgnoreCase));
        }
        static bool DriverInstallRunning() { try { var p = Process.GetProcessesByName("drvinst"); foreach (var x in p) x.Dispose(); return p.Length > 0; } catch { return false; } }
        /// Windows' Wi-Fi service (WLAN AutoConfig). When it stops, every card looks gone; the fix is starting it, not the card.
        public volatile bool WlanServiceDown;
        static bool WlanServiceRunning()
        {
            try { using (var sc = new System.ServiceProcess.ServiceController("WlanSvc")) return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running; }
            catch { return true; }   // can't tell: assume it's fine
        }

        void AdapterWatch(DateTime now)
        {
            bool present = Wlan.Present() && OwnCardInUse();
            if (!present)
            {
                Wlan.Reopen();   // a restarted WLAN service
                var own = FindOwnCard();
                if (own != null)
                {
                    bool fresh = !Adapter.Found || own.Item1 != Wlan.Iface || !OwnCardInUse();
                    Wlan.Use(own.Item1, own.Item2); if (fresh) Adapter.Init(own.Item1); present = Wlan.Present() && OwnCardInUse();
                    if (present && fresh)
                    {
                        StandIn = false; Log.Write("INFO", "Wi-Fi adapter found: " + own.Item2);
                        var pid = Adapter.InstanceId; if (pid != "") lock (StLock) if (string.IsNullOrEmpty(St.WifiPnpId)) St.WifiPnpId = pid;
                        EnsureOriginals();   // before anything tunes it
                        if (missingSince == null) { DriverVersionCheck(); if (MayChange) ThreadPool.QueueUserWorkItem(_ => Harden()); }   // showed up after start-up (a return is handled below)
                    }
                }
                else if (missingSince != null)
                {
                    // a replacement card (the old one uninstalled, or gone from the bus while another internal card works)
                    // becomes this PC's card; any other adapter is left alone while the app waits for its card
                    var l = Wlan.Interfaces();
                    var repl = l == null ? null : l.FirstOrDefault(i => TakesOver(Adapter.InstanceIdOf(i.Item1)));
                    if (repl != null) { Adopt(repl, "this PC's Wi-Fi card was replaced"); return; }
                    // gone for 30 minutes after the shutdown advice (dead, removed, or disabled on purpose) while another
                    // adapter is listed: look after that one, so the app stays useful
                    if ((now - missingSince.Value).TotalMinutes >= 30 && vanishStep >= 3 && l != null && l.Count > 0)
                    {
                        var other = l.OrderByDescending(x => WlanClient.Score(x.Item2)).FirstOrDefault(x => Adapter.InstanceIdOf(x.Item1) != "");
                        if (other != null) { Adopt(other, "this PC's card has been gone for 30 minutes"); return; }
                    }
                }
            }
            RadioIsOff = present && Wlan.RadioOff();
            CardAbsent = !present;   // not listed right now (even while that's expected): nothing to reconnect or restart
            if (present) absentSince = DateTime.MaxValue; else if (absentSince == DateTime.MaxValue) absentSince = now;
            if (present)
            {
                if (missingSince != null)
                {
                    Log.Write("CONN", string.Format(WlanServiceDown ? "Windows' Wi-Fi service is running again after {0:0} s" : "The Wi-Fi card is back after {0:0} s", (now - missingSince.Value).TotalSeconds));
                    if (vanishStep > 0 && vanishStep < 9) Note("cardback", "Wi-Fi is back", "It's working again.", ToolTipIcon.Info, 0);
                    missingSince = null; vanishStep = 0; vanishCode = ""; CardMissing = false; WlanServiceDown = false; StandIn = false; reenabledCutOff = false;
                    Adapter.Init(Wlan.Iface); lastProfileCheck = DateTime.MinValue;   // re-check its settings soon
                    EnsureOriginals();      // missing at start-up: back up before anything tunes it
                    DriverVersionCheck();   // Windows may have re-added it with a new driver
                    // Windows reconnects by itself within seconds: the reconnect steps wait, then start over from step 1
                    resumeGrace = now.AddSeconds(40); ladderFrom = now.AddSeconds(40); ladder = 0;
                }
                else if ((now - lastInternalCheck).TotalSeconds >= 60)   // using a USB adapter: this PC's internal card is back
                {
                    lastInternalCheck = now; string saved; lock (StLock) saved = St.WifiPnpId;
                    if (!string.IsNullOrEmpty(saved) && !IsInternal(saved))
                    {
                        var inner = (Wlan.Interfaces() ?? new List<Tuple<Guid, string, bool>>()).FirstOrDefault(i => IsInternal(Adapter.InstanceIdOf(i.Item1)));
                        if (inner != null) Adopt(inner, "this PC's internal card is available again");
                    }
                }
                return;
            }
            if (string.IsNullOrEmpty(CardId()) && !Adapter.Found) { CardMissing = false; return; }   // never had a Wi-Fi card: "No Wi-Fi adapter found"
            // the app (band switch, adapter restart, driver rollback, its own card restart), Windows (driver install, resume)
            // or a driver start is working on the card: its going away is expected, and recovery waits until they're done.
            // A test the user started (speed, stability…) doesn't hold it up. Neither does anything but the app's own card
            // work when Windows' Wi-Fi service has stopped: that's never part of a driver install or resume.
            bool svcDown = !WlanServiceRunning();
            bool held = Adapter.WorkedRecently(TimeSpan.FromSeconds(30)) || (!svcDown && (now < resumeGrace || now < vanishHoldUntil || DriverInstallRunning()));
            if (missingSince == null)
            {
                if (held) return;
                missingSince = vanishCalm = now; vanishStep = 0;
                Log.Write("CONN", svcDown ? "Windows' Wi-Fi service (WLAN AutoConfig) has stopped" : "The Wi-Fi card has gone from Windows (it stopped responding, or was disabled or removed)");
            }
            WlanServiceDown = svcDown;
            CardMissing = true; cardWasMissing = true;
            missingCause = svcDown ? "Windows' Wi-Fi service had stopped" : vanishStep == 9 ? "the Wi-Fi card was disabled" : "the Wi-Fi card had stopped responding";
            if (vanishStep == 9)   // disabled: look again now and then, in case it's enabled but still not working
            {
                if ((now - lastDisabledCheck).TotalSeconds < 30) return; lastDisabledCheck = now;
                int code; bool onBus; Pnp.Status(CardId(), out code, out onBus);
                if (code != 22) { vanishStep = 0; vanishCalm = now; vanishCode = ""; Log.Write("CONN", "The Wi-Fi card is enabled again but still not working; watching it"); }
                return;
            }
            if (held) { vanishCalm = now; return; }   // the clock starts again once they're done, so recovery never interrupts them
            double calm = (now - vanishCalm).TotalSeconds; int next = vanishStep + 1;
            // its own one-at-a-time guard rather than the shared action lock, which a long user test may be holding
            if (next <= 3 && calm >= (next == 1 ? 25 : next == 2 ? 60 : 75) && Interlocked.CompareExchange(ref vanishRunning, 1, 0) == 0)
            {
                vanishStep = next;   // before the step runs: it may set 9 (disabled)
                ThreadPool.QueueUserWorkItem(_ => { try { VanishStep(next); } finally { Volatile.Write(ref vanishRunning, 0); } });
            }
        }

        /// 1-2: restart the card (or rescan for it when it's off the bus); 3: tell the user it needs a full power cycle.
        void VanishStep(int step)
        {
            try
            {
                if (!WlanServiceRunning())
                {
                    WlanServiceDown = true;
                    if (step < 3)
                    {
                        if (!MayChange) return;
                        string err = null;
                        try { using (var sc = new System.ServiceProcess.ServiceController("WlanSvc")) { sc.Start(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); } }
                        catch (Exception ex) { err = ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
                        Log.Write("ACTION", "Recovery " + step + "/2: starting Windows' Wi-Fi service (WLAN AutoConfig) — " + (err == null ? "done" : "didn't work: " + err));
                        return;
                    }
                    Log.Write("CONN", "Windows' Wi-Fi service won't stay running");
                    Note("wlansvc", "Wi-Fi service won't start", "Windows' Wi-Fi service (WLAN AutoConfig) keeps stopping. Restarting the PC usually fixes it; if it doesn't, check it isn't disabled in Services.", ToolTipIcon.Error, 0);
                    return;
                }
                var id = CardId();
                int code; bool onBus; Pnp.Status(id, out code, out onBus);
                vanishCode = code > 0 ? code.ToString() : "";
                if (code == 22)
                {
                    // the app re-enables right after its own adapter restarts (Adapter). One it had to cut off can finish its
                    // "disable" step later: that leftover is the app's doing too. Any other disabled card is the user's choice.
                    if (MayChange && step < 3 && !reenabledCutOff && Adapter.CutOffRecently(TimeSpan.FromMinutes(5)))
                    {
                        reenabledCutOff = true; string o0; int rc0;
                        using (Adapter.Work()) rc0 = Exe.Run(Exe.Sys("pnputil.exe"), "/enable-device \"" + id + "\"", 60000, out o0);
                        Log.Write("ACTION", "Card recovery " + step + "/2: an adapter restart the app had to cut off left the card disabled; enabled it again — " + (rc0 == 0 ? "done" : "didn't work"));
                        return;
                    }
                    vanishStep = 9; missingCause = "the Wi-Fi card was disabled"; lastDisabledCheck = DateTime.Now; Log.Write("CONN", "The Wi-Fi card is disabled (Device Manager or Network Connections); leaving it alone"); return;   // the user's choice
                }
                if (step < 3)
                {
                    if (!MayChange) return;   // watch-only: just the advice at step 3
                    string o; int rc; var pnputil = Exe.Sys("pnputil.exe");
                    using (Adapter.Work())
                        if (onBus) rc = Exe.Run(pnputil, "/restart-device \"" + id + "\"", 90000, out o);
                        else rc = Exe.Run(pnputil, "/scan-devices", 90000, out o);
                    Log.Write("ACTION", "Card recovery " + step + "/2: " + (onBus ? "restarting the Wi-Fi card" + (code > 0 ? " (Windows reports problem code " + code + ")" : "") : "the card isn't detected at all; asked Windows to look for hardware again") +
                        " — " + (rc == 0 ? "done" : "didn't work (" + (o.Length > 160 ? o.Substring(0, 160) : o) + ")"));
                    if (step == 1) Note("cardgone1", "Wi-Fi card stopped responding", MayChange ? "Trying to restart it…" : "", ToolTipIcon.Warning, 0);
                    return;
                }
                Log.Write("CONN", "The Wi-Fi card is still gone" + (code > 0 ? " (problem code " + code + ")" : onBus ? "" : " (not detected)") + "; it needs a full power cycle");
                Note("cardgone", "Wi-Fi card needs a full shutdown", "Windows can't bring it back. Shut down (not Restart), wait 30 seconds, then turn the PC on. Hold Shift while you click Shut down to make it a full shutdown.", ToolTipIcon.Error, 0);
            }
            catch (Exception ex) { Log.Write("ERROR", "card recovery: " + ex.Message); }
        }

        string CardMissingText()
        {
            var code = vanishCode; string pc = code != "" ? " (problem code " + code + ")" : "";
            if (WlanServiceDown) return "Windows' Wi-Fi service (WLAN AutoConfig) has stopped" + (vanishStep >= 3 ? " and won't stay running — restarting the PC usually fixes it" : MayChange ? " — starting it" : "");
            if (vanishStep == 9) return "The Wi-Fi card is disabled (Device Manager or Network Connections)";
            if (vanishStep >= 3) return "Wi-Fi card stopped responding" + pc + " — shut down fully (not Restart) to bring it back";
            return "Wi-Fi card stopped responding" + pc + (MayChange ? " — trying to restart it" : "");
        }

        // ---------- outages: what caused them (radar channel change, card gone)
        string dropBssid = "", dropBand = ""; int dropChannel;
        string lastApBssid = "", lastApBand = ""; int lastApChannel;
        public static bool IsDfs(int ch) { return ch >= 52 && ch <= 144; }   // 5 GHz channels routers must leave when they detect radar

        void OnLinkLost(ConnInfo c) { dropBssid = lastApBssid; dropBand = lastApBand; dropChannel = lastApChannel; cardWasMissing = CardMissing; dropSsid = c.Connected ? c.Ssid : lastConnSsid; }

        string OnLinkBack(ConnInfo c, DateTime since, double secs)
        {
            string cause = "";
            if (cardWasMissing) cause = missingCause != "" ? missingCause : "the Wi-Fi card had stopped responding";
            // same access point (BSSID) on a new channel; a different node of a mesh shares the brand but isn't the same radio
            else if (dropBand == "5" && IsDfs(dropChannel) && c.Connected && c.Band == "5" && c.Channel > 0 && c.Channel != dropChannel && c.Bssid == dropBssid)
            { cause = "the router moved off radar channel " + dropChannel + " to " + c.Channel + " (DFS)"; RecordDfs(dropChannel, c.Channel, true); }
            lock (StLock) { St.Outages.Add(new OutageRec { Start = since.ToUniversalTime(), Seconds = Math.Round(secs, 1), Cause = cause }); if (St.Outages.Count > 400) St.Outages.RemoveRange(0, St.Outages.Count - 300); }
            cardWasMissing = false;
            if (c.Connected) { lastApBssid = c.Bssid; lastApBand = c.Band; lastApChannel = c.Channel; }
            return cause;
        }

        /// Same access point, new channel, no drop: the router followed a radar detection (or re-picked its channel).
        void DfsWatch(ConnInfo c)
        {
            if (downSince != null || !c.Connected || c.Band == "?" || c.Band == "" || c.Channel <= 0) return;   // a drop is labelled when the link comes back
            if ((DateTime.Now - Pinger.LastGwOk).TotalSeconds >= 6 && (DateTime.Now - Pinger.LastNetOk).TotalSeconds >= 6) return;   // nothing answering: this tick may be the start of a drop
            if (c.Bssid == lastApBssid && lastApBand == "5" && c.Band == "5" && c.Channel != lastApChannel && IsDfs(lastApChannel)) RecordDfs(lastApChannel, c.Channel, false);
            lastApBssid = c.Bssid; lastApBand = c.Band; lastApChannel = c.Channel;
        }
        void RecordDfs(int from, int to, bool dropped)
        {
            lock (StLock) { St.Incidents.Add(new IncidentRec { Time = DateTime.UtcNow, Kind = "dfs", Title = "Radar channel change", Key = "5|" + from, Detail = from + " → " + to }); if (St.Incidents.Count > 600) St.Incidents.RemoveRange(0, St.Incidents.Count - 500); }
            Log.Write("CONN", "The router left 5 GHz channel " + from + " for " + to + ": most likely radar nearby (a DFS channel)" + (dropped ? ", which dropped the connection" : ""));
            Note("dfs", "Router changed channel (radar)", "Your router had to leave channel " + from + ", a radar (DFS) channel, and moved to " + to + "." + (dropped ? " That's why Wi-Fi dropped." : "") +
                " Setting its 5 GHz to a channel from 36–48 or 149–165 avoids this.", ToolTipIcon.Info, 24 * 60);
        }

        // ---------- Windows Update replaced the driver
        /// Windows Update logs "Installation Successful" (event 19) only once the install is done, after the driver has
        /// already started: look after 30 s, 2 min and 10 min.
        void CheckDriverSourceLater(string from, string to)
        {
            if (ReadOnly || string.IsNullOrEmpty(to) || to == "?" || !Adapter.IsIntel) return;   // driver records are Intel-only
            new Thread(() => { foreach (var wait in new[] { 30, 90, 480 }) { Thread.Sleep(wait * 1000); if (DriverVersion != to || CheckDriverSource(from, to)) return; } }) { IsBackground = true, Name = "driver source" }.Start();
        }

        /// True once it's settled: Windows Update installed this exact version (its update title carries the version, which
        /// also keeps Intel Ethernet driver updates from matching).
        bool CheckDriverSource(string from, string to)
        {
            try
            {
                if (string.IsNullOrEmpty(to) || to == "?") return true; string found = null;
                var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19) and TimeCreated[timediff(@SystemTime) <= 259200000]]]") { ReverseDirection = true };
                using (var rdr = new EventLogReader(q))
                {
                    EventRecord r;
                    while (found == null && (r = rdr.ReadEvent()) != null)
                        using (r)
                        {
                            string title = ""; try { title = r.Properties.Count > 0 ? r.Properties[0].Value as string ?? "" : ""; } catch { }
                            if (title.IndexOf(" - Net - ", StringComparison.OrdinalIgnoreCase) >= 0 && Regex.IsMatch(title, @"(^|[^\d.])" + Regex.Escape(to) + @"($|[^\d.])")) found = title;
                        }
                }
                if (found == null) return false;
                lock (StLock) { DriverStat d; if (!St.DriverStats.TryGetValue(to, out d)) St.DriverStats[to] = d = new DriverStat { FirstSeen = DateTime.UtcNow }; d.ViaWindowsUpdate = true; }
                Save();
                bool saved = DriverBackups().Contains(from);
                Log.Write("DRIVER", "Windows Update installed the Wi-Fi driver " + to + " (" + found + ")");
                Note("wudriver", "Windows Update replaced your Wi-Fi driver", (string.IsNullOrEmpty(from) ? "" : from + " → ") + to + ". " +
                    (saved ? "If Wi-Fi gets worse, right-click the tray icon → Roll back Wi-Fi driver. " : "") + "The watchdog compares crash rates and will say if this one is worse.", ToolTipIcon.Info, 0);
                return true;
            }
            catch (Exception ex) { Log.Write("ERROR", "driver source check: " + ex.Message, false); return false; }
        }

        // ---------- laptops: normal Wi-Fi power saving on battery, lifted while a game runs
        public static bool HasBattery
        {
            get { try { var s = SystemInformation.PowerStatus.BatteryChargeStatus; return (s & BatteryChargeStatus.NoSystemBattery) == 0 && s != BatteryChargeStatus.Unknown; } catch { return false; } }
        }
        static string ActiveScheme()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes")) { var v = k == null ? null : k.GetValue("ActivePowerScheme") as string; return v != null && Regex.IsMatch(v, "^[0-9a-fA-F-]{36}$") ? v : null; } }
            catch { return null; }
        }
        /// On battery with no game: the user's own value from before the app, else Windows' default for the plan.
        string BatteryWifiValue()
        {
            string v; bool unknown;
            lock (StLock) { unknown = St.OriginalsUnknown || St.OriginalSystem.ContainsKey("unknown"); St.OriginalSystem.TryGetValue("wifiDC", out v); }
            if (!unknown && Regex.IsMatch(v ?? "", "^0x[0-9a-fA-F]+$")) return Convert.ToInt64(v.Substring(2), 16).ToString();
            var s = ActiveScheme(); var d = s == null ? null : PlanDefault(s, SubWifi, SetWifi, false);
            return d != null ? Convert.ToInt64(d.Substring(2), 16).ToString() : "2";   // 2 = medium power saving, Windows' usual battery value
        }
        readonly object batteryLock = new object();
        /// After Harden: the battery value matches whether a game is running right now (Harden reads InGame early, and a
        /// game boost can land in between).
        void BatteryReconcile()
        {
            lock (batteryLock)
            {
                var v = InGame ? "0" : BatteryWifiValue(); string o; var pc = Exe.Sys("powercfg.exe");
                Exe.Run(pc, "/setdcvalueindex SCHEME_CURRENT " + SubWifi + " " + SetWifi + " " + v, 15000, out o);
                Exe.Run(pc, "/setactive SCHEME_CURRENT", 15000, out o);
            }
        }
        void BatteryGameBoost(bool on, int gen)
        {
            if (!HasBattery || !Cfg.BatteryAware || !MayChange) return;
            lock (batteryLock)
            {
                if (gen != Volatile.Read(ref wfdGen)) return;   // a later game start/end superseded this one
                var v = on ? "0" : BatteryWifiValue(); string o; var pc = Exe.Sys("powercfg.exe");
                Exe.Run(pc, "/setdcvalueindex SCHEME_CURRENT " + SubWifi + " " + SetWifi + " " + v, 15000, out o);
                Exe.Run(pc, "/setactive SCHEME_CURRENT", 15000, out o);
                Log.Write("INFO", on ? "Laptop: Wi-Fi power saving on battery lifted while the game runs" : "Laptop: Wi-Fi power saving on battery back to normal", false);
            }
        }

        // ---------- streaming / VR / cloud gaming: a session while the app itself moves real traffic
        DateTime? streamBusySince, streamQuietSince; bool streamOn; string streamApp;
        readonly Dictionary<int, Tuple<ulong, double, double>> ioLast = new Dictionary<int, Tuple<ulong, double, double>>();   // bytes, monotonic seconds, rate

        /// MB/s of I/O the process did since the last look (sockets count, so for these apps it's mostly network); 0 the
        /// first time or when it can't be read.
        double IoRate(int pid)
        {
            double t = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;   // monotonic: a clock change can't freeze it
            ulong bytes; IntPtr hp = Native2.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid); if (hp == IntPtr.Zero) return 0;
            try { Native2.IO_COUNTERS c; if (!Native2.GetProcessIoCounters(hp, out c)) return 0; bytes = c.ReadBytes + c.WriteBytes + c.OtherBytes; }
            finally { Native2.CloseHandle(hp); }
            Tuple<ulong, double, double> last; double r = 0;
            if (ioLast.TryGetValue(pid, out last))
            {
                double dt = t - last.Item2;
                if (dt <= 0.5) return last.Item3;   // too close to the last look (an extra tick): keep that rate
                if (bytes >= last.Item1) r = (bytes - last.Item1) / dt / 1e6;
            }
            ioLast[pid] = Tuple.Create(bytes, t, r); return r;
        }

        /// Apps that only run during a session (SteamVR, Steam's streaming client) count at once. The others also idle in
        /// the background all day, so they count only while their own traffic is high (0.5 MB/s, about 4 Mbps, for 10 s)
        /// and Wi-Fi agrees; a download in another app or disk work doesn't start a session.
        bool StreamingActive(List<Tuple<string, int>> procs, out string app)
        {
            var now = DateTime.UtcNow; app = null;   // UTC: daylight saving can't stall the 10 s / 60 s timers
            if (procs == null || procs.Count == 0) { StreamReset(); return false; }
            var always = procs.FirstOrDefault(p => GameList.StreamingAlways.Contains(p.Item1));
            if (always != null) { streamOn = true; app = streamApp = always.Item1; return true; }
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs) { double r = IoRate(p.Item2), v; rates[p.Item1] = rates.TryGetValue(p.Item1, out v) ? Math.Max(v, r) : r; }
            foreach (var gone in ioLast.Keys.Where(k => !procs.Any(p => p.Item2 == k)).ToList()) ioLast.Remove(gone);
            if (streamOn && (streamApp == null || !rates.ContainsKey(streamApp))) { StreamReset(); return false; }   // the streaming app closed
            var top = rates.OrderByDescending(kv => kv.Value).First(); bool wasOn = streamOn;
            double wifi = Math.Max(RxMbps, TxMbps), own = wasOn ? rates[streamApp] : top.Value;   // a running session is judged on its own app
            StreamDecide(own >= 0.5 && wifi >= 5, own < 0.15 || wifi < 1.5, now);
            if (streamOn && !wasOn) streamApp = top.Key;   // the busiest one is the one streaming
            app = streamOn ? streamApp : null;
            return streamOn;
        }
        void StreamReset() { streamOn = false; streamBusySince = streamQuietSince = null; streamApp = null; ioLast.Clear(); }
        /// On after 10 s of "busy", off after 60 s of "quiet".
        bool StreamDecide(bool busy, bool quiet, DateTime now)
        {
            if (!streamOn)
            {
                if (!busy) streamBusySince = null;
                else if (streamBusySince == null) streamBusySince = now;
                else if ((now - streamBusySince.Value).TotalSeconds >= 10) { streamOn = true; streamQuietSince = null; streamApp = null; }
            }
            else
            {
                if (!quiet) streamQuietSince = null;
                else if (streamQuietSince == null) streamQuietSince = now;
                else if ((now - streamQuietSince.Value).TotalSeconds >= 60) { streamOn = false; streamBusySince = null; streamApp = null; }
            }
            return streamOn;
        }

        // ---------- mesh: hold the best node during a game
        volatile string holdBssid = ""; string holdBand = "";
        void MeshHoldStart()
        {
            var c = Conn; holdBssid = "";
            if (!Cfg.MeshHold || !c.Connected || !IsHome(c.Ssid) || IsAvoidBssid(c.Bssid) || !ChannelOk(c.Band, c.Channel)) return;   // never hold a spot the placement fix should move off
            if (!Wlan.GetBssList().Any(b => b.Ssid == c.Ssid && b.Band == c.Band && b.Bssid != c.Bssid && !IsAvoidBssid(b.Bssid))) return;   // one access point: nothing to hold
            holdBand = c.Band; holdBssid = c.Bssid;
            Log.Write("GAME", "Holding " + Describe(c) + " for the game (other nodes of your network are in range)", false);
        }
        void MeshHoldEnd() { holdBssid = ""; }

        /// Mid-game roam to a clearly weaker node while the held one is still strong: steer back (at most every 10 min).
        void MeshWatch(ConnInfo c, DateTime now)
        {
            var hold = holdBssid;
            if (hold == "" || !InGame || !c.Connected || c.Bssid == hold || !IsHome(c.Ssid) || c.Band != holdBand || IsAvoidBssid(c.Bssid)) return;
            var held = Wlan.GetBssList().FirstOrDefault(b => b.Bssid == hold);
            // never steer back to a spot the placement fix moved away from (extender, avoided or quarantined channel)
            if (IsAvoidBssid(hold) || (held != null && !ChannelOk(held.Band, held.Channel))) { holdBssid = ChannelOk(c.Band, c.Channel) ? c.Bssid : ""; return; }
            // nothing scans during games: without a fresh reading of the held node, keep the hold and decide on a later tick
            if (held == null || held.AgeSeconds > 10) return;
            if (held.Rssi < -67 || held.Rssi - c.Rssi < 8)
            {
                Log.Write("GAME", "Moved to another node mid-game: " + Describe(c) + string.Format(" (the previous one is at {0} dBm)", held.Rssi), false);
                holdBssid = ChannelOk(c.Band, c.Channel) ? c.Bssid : ""; return;   // the new one is as good: hold it
            }
            if (!MayChange || Paused || ActionBusy || WiredName != null || (now - lastMeshSteer).TotalMinutes < 10) return;
            lastMeshSteer = now; string profile = HomeProfile; int weak = c.Rssi, strong = held.Rssi;
            RunAction("Back to the stronger node", () =>
            {
                var rc = Wlan.Connect(profile, new[] { hold });
                Log.Write("ACTION", string.Format("Mid-game roam to a weaker node ({0} dBm vs {1} dBm); steering back to {2} (rc={3})", weak, strong, hold, rc));
                Note("meshback", "Back on the stronger node", string.Format("Your PC drifted to a weaker node mid-game ({0} dBm), so it moved back ({1} dBm).", weak, strong), ToolTipIcon.Info, 10);
            });
        }

        /// 15 s into a game, on a weak node with other nodes known: one quick scan for fresh readings, and if a clearly
        /// stronger node of the home network answers, move there before the match (one short drop now, not a weak link all match).
        void MeshPreGame(ConnInfo c)
        {
            if (!Cfg.MeshHold || !MayChange || Paused || WiredName != null || !c.Connected || !IsHome(c.Ssid) || IsAvoidBssid(c.Bssid) || c.Rssi > -65) return;
            if (!Wlan.GetBssList().Any(b => b.Ssid == c.Ssid && b.Band == c.Band && b.Bssid != c.Bssid && !IsAvoidBssid(b.Bssid))) return;   // no other node known
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Wlan.Scan(); Thread.Sleep(3500);
                var cur = Wlan.GetConnection(); if (!cur.Connected || cur.Bssid != c.Bssid || !InGame) return;
                var best = Wlan.GetBssList().Where(b => b.Ssid == c.Ssid && b.Band == c.Band && b.Bssid != c.Bssid && !IsAvoidBssid(b.Bssid) && ChannelOk(b.Band, b.Channel) && b.AgeSeconds <= 10).OrderByDescending(b => b.Rssi).FirstOrDefault();
                if (best == null || best.Rssi - cur.Rssi < 10) return;
                string profile = HomeProfile, to = best.Bssid, band = c.Band; int from = cur.Rssi, toRssi = best.Rssi;
                RunAction("Moving to the stronger node", () =>
                {
                    var rc = Wlan.Connect(profile, new[] { to });
                    string why; bool ok = rc == 0 && WaitForBand(band, 20, out why);
                    Log.Write("ACTION", string.Format("Pre-game: moved from a weaker node ({0} dBm) to {1} ({2} dBm) — {3}", from, to, toRssi, ok ? "done" : "didn't work"));
                    if (!ok) return;
                    if (holdBssid != "") holdBssid = Wlan.GetConnection().Bssid;
                    Note("meshpre", "Moved to a stronger node", string.Format("A stronger node of your network was in range ({0} vs {1} dBm), so the PC moved to it before the match.", toRssi, from), ToolTipIcon.Info, 30);
                });
            });
        }

        // ---------- this PC's own hardware address on home networks
        static string Hex(string s) { return Regex.Replace(s ?? "", "[^0-9A-Fa-f]", "").ToUpperInvariant(); }
        /// True when the adapter's current address isn't its own (a random hardware address); null when unknown.
        public static bool? MacDiffers(string current, string permanent) { string a = Hex(current), b = Hex(permanent); if (a.Length != 12 || b.Length != 12) return null; return a != b; }
        static bool? RandomAddressInUse()
        {
            string so, se;
            Ps.Run("$a=Get-NetAdapter -Name " + Ps.Q(Adapter.Name) + " -ErrorAction SilentlyContinue; 'MAC=' + [string]$a.MacAddress + '|' + [string]$a.PermanentAddress", 30, out so, out se);
            var m = Regex.Match(so ?? "", @"MAC=([^|\r\n]*)\|([^\r\n]*)"); return m.Success ? MacDiffers(m.Groups[1].Value, m.Groups[2].Value) : null;
        }
        void MacCheck(ConnInfo c)
        {
            string profile = c.Profile, ssid = c.Ssid, band = c.Band;
            if (string.IsNullOrEmpty(profile) || profile.Contains("\"")) return;
            lock (StLock) { DateTime t; if (St.MacFixTried.TryGetValue(profile, out t) && (DateTime.UtcNow - t).TotalDays < 7) return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // only Windows' random address is the app's business: an address typed into the adapter's "Network Address"
                // also differs from the card's own, and a profile already set to the card's own address needs nothing
                if (Adapter.AddressOverride != "") return;
                if (Regex.IsMatch(Wlan.ProfileXml(profile) ?? "", @"<enableRandomization>\s*false", RegexOptions.IgnoreCase)) return;
                if (RandomAddressInUse() != true) return;   // already this PC's own address
                RunAction("Using this PC's own hardware address", () => FixAddress(profile, ssid, band));
            });
        }
        readonly object macLock = new object();   // turning random addresses off and putting them back never overlap
        void FixAddress(string profile, string ssid, string band)
        {
            lock (macLock) FixAddressLocked(profile, ssid, band);
        }
        void FixAddressLocked(string profile, string ssid, string band)
        {
            // the check ran in the background for a few seconds: the option, automatic fixes or the network may have changed since
            if (InGame || WiredName != null || Paused || !Cfg.FixedHardwareAddress || !MayChange || !IsHome(ssid)) return;
            var now0 = Wlan.GetConnection(); if (!now0.Connected || now0.Ssid != ssid || now0.Profile != profile) return;   // moved to another network meanwhile
            var xml = Wlan.ProfileXml(profile) ?? "";
            string orig = !Regex.IsMatch(xml, @"<enableRandomization>", RegexOptions.IgnoreCase) ? "none" : Regex.IsMatch(xml, @"<randomizeEveryday>\s*true", RegexOptions.IgnoreCase) ? "daily" : "yes";
            string o; int rc = Exe.Run(Exe.Sys("netsh.exe"), "wlan set profileparameter name=\"" + profile + "\"" + IfArg() + " randomization=no", 20000, out o);
            if (rc != 0) { lock (StLock) St.MacFixTried[profile] = DateTime.UtcNow; Save(); Log.Write("ERROR", "Couldn't turn off the random hardware address for " + ssid + ": " + o); return; }
            lock (StLock) { if (!St.OriginalRandomization.ContainsKey(profile)) St.OriginalRandomization[profile] = orig; St.MacFixTried.Remove(profile); }
            Save();
            Note("mac", "Using this PC's own address on " + ssid, "So the router always recognizes this PC, and its device priority and reservations stick. Wi-Fi reconnects once.", ToolTipIcon.Info, 0);
            Thread.Sleep(4000);
            var mid = Wlan.GetConnection();   // Windows applies it on the next connection; nudge it only if nothing changed meanwhile
            bool stillOk = !InGame && !Paused && WiredName == null && Cfg.FixedHardwareAddress && MayChange && mid.Connected && mid.Ssid == ssid && mid.Profile == profile;
            if (stillOk && RandomAddressInUse() == true) Wlan.Connect(profile, null);
            string why; WaitForBand(band, 40, out why);
            var after = Wlan.GetConnection();
            if (!after.Connected || after.Ssid != ssid) { Log.Write("ACTION", "Random hardware address turned off for " + ssid + " (Wi-Fi moved to another network meanwhile; it applies when it's back)"); return; }
            if (RandomAddressInUse() != true) { Log.Write("ACTION", "Random hardware address turned off for " + ssid + " (it was " + (orig == "daily" ? "changing daily" : "on") + "); now using this PC's own address"); return; }
            // the setting didn't change the address (something else sets it): put it back and don't try again for a week
            string o2; Exe.Run(Exe.Sys("netsh.exe"), "wlan set profileparameter name=\"" + profile + "\"" + IfArg() + " randomization=" + RandomizationValue(orig), 20000, out o2);
            lock (StLock) { St.OriginalRandomization.Remove(profile); St.MacFixTried[profile] = DateTime.UtcNow; }
            Save(); Log.Write("ACTION", "Turning off the random hardware address for " + ssid + " didn't change the address, so the setting is back as it was");
        }
        /// netsh's interface argument; left out when no card is selected (then netsh changes the profile on every interface that has it).
        static string IfArg() { return string.IsNullOrEmpty(Adapter.Name) ? "" : " interface=\"" + Adapter.Name + "\""; }
        /// The netsh value that puts a profile back: its own setting, or for a profile that had none ("none"), whatever
        /// Windows' global "random hardware addresses" switch gives now (netsh can't remove a per-network setting).
        static string RandomizationValue(string orig)
        {
            if (orig == "daily" || orig == "yes") return orig;
            string o; Exe.Run(Exe.Sys("netsh.exe"), "wlan show randomization", 15000, out o);
            return Regex.IsMatch(o ?? "", @"randomization\s+disabled", RegexOptions.IgnoreCase) ? "no" : "yes";
        }
        /// Puts back the random hardware address setting on every network where the app turned it off.
        public string RestoreRandomization(string why) { lock (macLock) return RestoreRandomizationLocked(why); }
        string RestoreRandomizationLocked(string why)
        {
            Dictionary<string, string> d; lock (StLock) d = new Dictionary<string, string>(St.OriginalRandomization); if (d.Count == 0) return "";
            int ok = 0;
            foreach (var kv in d)
            {
                string o; int rc = Exe.Run(Exe.Sys("netsh.exe"), "wlan set profileparameter name=\"" + kv.Key + "\"" + IfArg() + " randomization=" + RandomizationValue(kv.Value), 20000, out o);
                if (rc == 0) { ok++; lock (StLock) St.OriginalRandomization.Remove(kv.Key); }
                else Log.Write("ERROR", "Couldn't put back the random hardware address for a network: " + o);
            }
            Save(); Log.Write("ACTION", "Random hardware address put back on " + ok + " network" + (ok == 1 ? "" : "s") + " (" + why + ")");
            return ok == 0 ? "" : "Random hardware address put back on " + ok + " network" + (ok == 1 ? "" : "s") + " (applies at the next connection).";
        }

        // ---------- update notice
        public string AvailableUpdate { get { string t; lock (StLock) t = St.UpdateTag; return !string.IsNullOrEmpty(t) && Updates.IsNewer(t, Program.Version) ? Updates.Parse(t).ToString(3) : null; } }
        public string UpdatePageUrl { get { string u; lock (StLock) u = St.UpdateUrl; return string.IsNullOrEmpty(u) ? Updates.Releases : u; } }
        void CheckForUpdate()
        {
            try
            {
                var r = Updates.Latest();
                lock (StLock)
                {
                    if (r == null) { St.LastUpdateCheck = DateTime.UtcNow.AddDays(-6); }   // couldn't reach GitHub: try again tomorrow
                    else { St.LastUpdateCheck = DateTime.UtcNow; St.UpdateTag = r.Item1; St.UpdateUrl = r.Item2; }
                }
                Save();
                var tag = AvailableUpdate; if (r == null || tag == null) return;
                bool told; lock (StLock) { told = St.UpdateNotified == r.Item1; St.UpdateNotified = r.Item1; }
                Save(); Log.Write("INFO", "Update available: AX210_WatchClaude " + tag);
                if (!told) Note("update", "AX210_WatchClaude " + tag + " is available", "Right-click the tray icon → Update available, to open the download page.", ToolTipIcon.Info, 0);
            }
            catch (Exception ex) { Log.Write("ERROR", "update check: " + ex.Message, false); }
            finally { updateChecking = false; }
        }

        // ---------- weekly summary
        static bool Clean(SessionRec s) { return s.Crashes == 0 && s.GwLossPct < 0.5 && s.NetLossPct < 1 && s.GwP95 < 20; }
        static string Dur(double s) { return s < 90 ? s.ToString("0") + " s" : (s / 60).ToString("0") + " min"; }
        void WeeklyDue()
        {
            DateTime last; lock (StLock) last = St.LastWeeklySummary;
            if (last == default(DateTime)) { lock (StLock) St.LastWeeklySummary = DateTime.UtcNow; Save(); return; }   // the first one comes after a week of data
            if ((DateTime.UtcNow - last).TotalDays < 7) return;
            lock (StLock) St.LastWeeklySummary = DateTime.UtcNow; Save();
            var t = WeeklySummaryText(false); Log.Write("SUMMARY", t);
            Note("weekly", "Your week on Wi-Fi", t, ToolTipIcon.Info, 0);
        }
        public string WeeklySummaryText(bool detailed)
        {
            var since = DateTime.UtcNow.AddDays(-7);
            List<SessionRec> ses; List<OutageRec> outs; int crashes, dfs;
            lock (StLock)
            {
                ses = St.Sessions.Where(s => s.Start >= since).ToList(); outs = St.Outages.Where(o => o.Start >= since).ToList();
                crashes = St.Incidents.Count(i => i.Kind == "crash" && i.Time >= since); dfs = St.Incidents.Count(i => i.Kind == "dfs" && i.Time >= since);
            }
            var parts = new List<string>();
            if (ses.Count > 0)
            {
                int clean = ses.Count(Clean);
                parts.Add(string.Format("{0} game session{1} ({2:0.#} h), {3} clean", ses.Count, ses.Count == 1 ? "" : "s", ses.Sum(s => s.Minutes) / 60, clean == ses.Count ? "all" : clean.ToString()));
                var med = ses.Where(s => s.GwMed > 0).Select(s => s.GwMed).OrderBy(x => x).ToList();
                if (med.Count > 0) parts.Add(string.Format("router ping {0:0} ms typical", med[med.Count / 2]));
            }
            else parts.Add("no game sessions");
            parts.Add(crashes == 0 ? "no driver crashes" : crashes + " driver crash" + (crashes == 1 ? "" : "es"));
            var real = outs.Where(o => o.Seconds >= 3).ToList();
            parts.Add(real.Count == 0 ? "no dropouts" : string.Format("{0} dropout{1} (longest {2})", real.Count, real.Count == 1 ? "" : "s", Dur(real.Max(o => o.Seconds))));
            if (dfs > 0) parts.Add(dfs + " radar channel change" + (dfs == 1 ? "" : "s"));
            var text = "This week: " + string.Join(" · ", parts) + "." + BtStallNote();
            if (detailed && ses.Count > 0)
                text += "\r\n\r\n" + string.Join("\r\n", ses.GroupBy(s => s.Game).OrderByDescending(g => g.Sum(s => s.Minutes)).Select(g =>
                    string.Format("  {0,-26} {1,3} session{2}  {3,5:0.#} h  {4} clean", g.Key, g.Count(), g.Count() == 1 ? " " : "s", g.Sum(s => s.Minutes) / 60, g.Count(Clean))));
            return text;
        }
        /// " Sessions with Bluetooth connected had 3× the stalls." when the last 30 days show a clear difference.
        string BtStallNote()
        {
            List<SessionRec> s; lock (StLock) s = St.Sessions.Where(x => x.Start >= DateTime.UtcNow.AddDays(-30) && x.Minutes >= 5 && x.Bluetooth != null).ToList();   // null: from before 1.0.3
            var with = s.Where(x => x.Bluetooth != "").ToList(); var without = s.Where(x => x.Bluetooth == "").ToList();
            if (with.Count < 3 || without.Count < 3) return "";
            double a = with.Sum(x => x.Stalls) / with.Sum(x => x.Minutes), b = Math.Max(without.Sum(x => x.Stalls) / without.Sum(x => x.Minutes), 0.05);
            return a >= 0.2 && a >= 2 * b ? string.Format(" Sessions with Bluetooth connected had {0:0.#}× the stalls.", a / b) : "";
        }

        // ---------- backup and restore
        public string ExportBackup(string path)
        {
            string json;
            lock (StLock)
                json = Json.Pretty(Json.S().Serialize(new BackupFile
                {
                    App = Paths.App, Version = Program.Version, Exported = DateTime.UtcNow, Config = Cfg, Channels = St.Channels, KnownBands = St.KnownBands, Incidents = St.Incidents,
                    Sessions = St.Sessions, DriverStats = St.DriverStats, Games = St.Games, Outages = St.Outages, LastDownloadMbps = St.LastDownloadMbps, LastUploadMbps = St.LastUploadMbps
                }));
            File.WriteAllText(path, json);
            Log.Write("INFO", "Settings and history backed up to " + Path.GetFileName(path));
            return "Backed up settings and history to " + path + ".";
        }

        public static BackupFile ReadBackup(string path)
        {
            var b = Json.S().Deserialize<BackupFile>(File.ReadAllText(path));
            if (b == null || b.App != Paths.App || b.Config == null) throw new Exception("it isn't an AX210_WatchClaude backup");
            return b;
        }

        public string ImportBackup(string path)
        {
            BackupFile b; Config cfg;
            try { b = ReadBackup(path); bool ch; cfg = Config.Parse(Json.S().Serialize(b.Config), out ch); }
            catch (Exception ex) { return "That file couldn't be used: " + ex.Message; }
            lock (StLock) MergeHistory(St, b);
            Save();
            var r = ApplySettings(cfg);
            Log.Write("INFO", "Restored settings and history from a backup made " + b.Exported.ToLocalTime().ToString("yyyy-MM-dd"));
            return "Restored settings and history from " + b.Exported.ToLocalTime().ToString("MMM d, yyyy") + " (version " + (b.Version ?? "?") + "). " + r;
        }

        /// Times in a backup file keep milliseconds only, so records are matched at that precision (UTC).
        static long Ms(DateTime t) { return t.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond; }
        /// Adds a backup's history to the current one: counts take the larger value, lists are merged without duplicates.
        internal static void MergeHistory(State st, BackupFile b)
        {
            foreach (var kv in b.Channels ?? new Dictionary<string, ChannelStat>())
            {
                if (kv.Value == null) continue; ChannelStat s;
                if (!st.Channels.TryGetValue(kv.Key, out s)) { st.Channels[kv.Key] = kv.Value; continue; }
                s.Seconds = Math.Max(s.Seconds, kv.Value.Seconds); s.Crashes = Math.Max(s.Crashes, kv.Value.Crashes); s.Stalls = Math.Max(s.Stalls, kv.Value.Stalls);
                s.HistCrashes = Math.Max(s.HistCrashes, kv.Value.HistCrashes); s.HistAssociations = Math.Max(s.HistAssociations, kv.Value.HistAssociations);
            }
            foreach (var kv in b.KnownBands ?? new Dictionary<string, string>()) if (!st.KnownBands.ContainsKey(kv.Key) && kv.Value != null) st.KnownBands[kv.Key] = kv.Value;
            st.Incidents = st.Incidents.Concat((b.Incidents ?? new List<IncidentRec>()).Where(x => x != null)).GroupBy(x => Ms(x.Time) + "|" + x.Kind + "|" + x.Title).Select(g => g.First()).OrderBy(x => x.Time).ToList();
            if (st.Incidents.Count > 500) st.Incidents.RemoveRange(0, st.Incidents.Count - 500);
            st.Sessions = st.Sessions.Concat((b.Sessions ?? new List<SessionRec>()).Where(x => x != null)).GroupBy(x => Ms(x.Start) + "|" + x.Game).Select(g => g.First()).OrderBy(x => x.Start).ToList();
            if (st.Sessions.Count > 120) st.Sessions.RemoveRange(0, st.Sessions.Count - 120);
            st.Outages = st.Outages.Concat((b.Outages ?? new List<OutageRec>()).Where(x => x != null)).GroupBy(x => Ms(x.Start)).Select(g => g.First()).OrderBy(x => x.Start).ToList();
            if (st.Outages.Count > 300) st.Outages.RemoveRange(0, st.Outages.Count - 300);
            foreach (var kv in b.DriverStats ?? new Dictionary<string, DriverStat>())
            {
                if (kv.Value == null) continue; DriverStat d;
                if (!st.DriverStats.TryGetValue(kv.Key, out d)) { kv.Value.BackedUp = false; st.DriverStats[kv.Key] = kv.Value; continue; }   // the driver copies stay on the old PC
                d.Hours = Math.Max(d.Hours, kv.Value.Hours); d.Crashes = Math.Max(d.Crashes, kv.Value.Crashes); d.ViaWindowsUpdate |= kv.Value.ViaWindowsUpdate;
                if (kv.Value.FirstSeen > DateTime.MinValue && kv.Value.FirstSeen < d.FirstSeen) d.FirstSeen = kv.Value.FirstSeen;
            }
            foreach (var kv in b.Games ?? new Dictionary<string, GameRec>())
                if (kv.Value != null && !st.Games.ContainsKey(kv.Key)) { kv.Value.Prioritized = false; st.Games[kv.Key] = kv.Value; }   // re-tagged on this PC the next time it's played
            if (st.LastDownloadMbps <= 0) st.LastDownloadMbps = b.LastDownloadMbps;
            if (st.LastUploadMbps <= 0) st.LastUploadMbps = b.LastUploadMbps;
        }
    }
}
