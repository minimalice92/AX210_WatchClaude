// =====================================================================================
//  AX210_WatchClaude — Settings window (Claude, 2026-11-25)
//  Every config.json option as a toggle / number / list, with plain-English help,
//  validation on Save, live apply, and buttons for the raw file.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AX210WatchClaude
{
    // ---------------------------------------------------------------- applying settings
    partial class Watchdog
    {
        static bool Same<TItem>(IEnumerable<TItem> a, IEnumerable<TItem> b) { return (a ?? Enumerable.Empty<TItem>()).SequenceEqual(b ?? Enumerable.Empty<TItem>()); }

        /// Saves a new config and applies whatever needs applying right away.
        public string ApplySettings(Config n)
        {
            var o = Cfg; var notes = new List<string>();
            n.ConfigVersion = Math.Max(n.ConfigVersion, o.ConfigVersion);
            n.Sanitize(); Cfg = n; n.Save(true);
            Pinger.Internet = n.InternetTarget; Pinger.StallMs = n.StallMs;
            if (!Same(o.ExtraGameFolders, n.ExtraGameFolders) || !Same(o.IgnoreGameFolders, n.IgnoreGameFolders)) { Detector.Discover(n); notes.Add("game folders re-scanned (" + Detector.RootCount + " watched)"); }
            if (IsAdmin)
            {
                if (o.QosTagGame != n.QosTagGame || !Same(o.QosAlwaysExes, n.QosAlwaysExes)) { ThreadPool.QueueUserWorkItem(_ => Harden()); notes.Add("game traffic priority " + (n.QosTagGame ? "updated" : "turned off")); }
                if (o.UploadLimiter != n.UploadLimiter || o.UploadCapMbps != n.UploadCapMbps) { ThreadPool.QueueUserWorkItem(_ => ApplyUploadLimiter(true)); notes.Add("upload limiter " + (n.UploadLimiter ? "updated" : "off")); }
                if (o.SuspendWifiDirectWhileGaming && !n.SuspendWifiDirectWhileGaming) ThreadPool.QueueUserWorkItem(_ => RestoreWifiDirect("setting turned off"));
                if (o.LowLatencyMode && !n.LowLatencyMode && Wlan.Ok)
                {
                    Wlan.SetBool(WlanNative.OpBackgroundScan, true); Wlan.SetBool(WlanNative.OpStreamingMode, false);
                    LowLatencyState = "off (turned off in settings)"; notes.Add("low-latency mode off; Windows scanning back to normal");
                }
                if (!o.LowLatencyMode && n.LowLatencyMode) { LowLatencyState = ""; notes.Add("low-latency mode on"); }
            }
            if (!Same(o.BandOrder, n.BandOrder)) { lock (StLock) St.NextProbe = DateTime.MinValue; notes.Add("band order " + string.Join(" › ", n.BandOrder) + " GHz"); }
            if (o.AutoActions != n.AutoActions) notes.Add(n.AutoActions ? "automatic fixes on" : "automatic fixes off (monitor only)");
            if (!o.AutoActions && n.AutoActions && CanAct) ThreadPool.QueueUserWorkItem(_ => Harden());   // the start-up tuning was skipped while it was off
            if (o.TurboDownloads != n.TurboDownloads) notes.Add("fast downloads " + (n.TurboDownloads ? "on" : "off"));
            Log.Write("INFO", "Settings saved" + (notes.Count > 0 ? ": " + string.Join("; ", notes) : ""));
            nudge.Set();
            return notes.Count > 0 ? char.ToUpper(notes[0][0]) + string.Join("; ", notes).Substring(1) + "." : "Saved. Nothing needed re-applying.";
        }

        /// Re-reads config.json. A file with a mistake in it is reported, never replaced with defaults.
        public string ReloadFromFile()
        {
            Config c;
            try { var text = File.ReadAllText(Paths.ConfigFile); if (string.IsNullOrWhiteSpace(text)) throw new Exception("the file is empty"); bool ch; c = Config.Parse(text, out ch); }
            catch (Exception ex) { Log.Write("ERROR", "config.json has a mistake; kept the current settings (" + ex.Message + ")"); return "config.json has a mistake, so nothing changed:\n" + ex.Message; }
            return ApplySettings(c);
        }
    }

    // ---------------------------------------------------------------- input controls
    class ToggleSwitch : Control
    {
        readonly Theme T; bool on, hover;
        public event EventHandler CheckedChanged;
        public ToggleSwitch(Theme t)
        {
            T = t; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(Ui.D(46), Ui.D(24)); Cursor = Cursors.Hand; BackColor = t.Card; TabStop = true;
        }
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); } }
        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f);
            Color track = on ? (hover ? Ui.Mix(T.Accent, Color.White, 0.12f) : T.Accent) : Ui.Mix(T.InputBg, T.Sub, hover ? 0.38f : 0.25f);
            using (var p = Ui.Round(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(track)) g.FillPath(b, p);
                if (Focused) using (var pen = new Pen(Ui.Mix(T.Accent, Color.White, 0.3f), 1.5f)) g.DrawPath(pen, p);
            }
            float d = Height - Ui.D(10), x = on ? Width - d - Ui.D(6) : Ui.D(6);
            using (var b = new SolidBrush(on ? Color.White : T.Text)) g.FillEllipse(b, x, (Height - d) / 2f, d, d);
        }
    }

    class InputFrame : Panel
    {
        readonly Theme T; public readonly TextBox Box;
        public InputFrame(Theme t, bool multiline, int width, int height)
        {
            T = t; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Card; Size = new Size(width, height); Padding = multiline ? new Padding(Ui.D(9), Ui.D(7), Ui.D(3), Ui.D(5)) : new Padding(Ui.D(10), (height - Ui.Body.Height) / 2, Ui.D(8), 0);
            Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = t.InputBg, ForeColor = t.Text, Font = Ui.Body, Multiline = multiline, Dock = DockStyle.Fill, AcceptsReturn = multiline, WordWrap = false, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
            Box.HandleCreated += (s, e) => Ui.DarkScroll(Box, T.Dark);
            Box.GotFocus += (s, e) => Invalidate(); Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(T.Card); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.D(7)))
            {
                using (var b = new SolidBrush(T.InputBg)) g.FillPath(b, p);
                using (var pen = new Pen(Box.Focused ? T.Accent : T.CardBorder)) g.DrawPath(pen, p);
            }
        }
    }

    class NumberField : Panel
    {
        readonly InputFrame frame;
        public NumberField(Theme t, string unit, int boxWidth = 96)
        {
            BackColor = t.Card; int bw = Ui.D(boxWidth);
            frame = new InputFrame(t, false, bw, Ui.D(32)) { Location = new Point(0, 0), Margin = Padding.Empty };
            frame.Box.TextAlign = HorizontalAlignment.Right;
            var u = new Label { Text = unit, Font = Ui.Body, ForeColor = t.Sub, BackColor = t.Card, AutoSize = true, UseMnemonic = false, Location = new Point(bw + Ui.D(9), Ui.D(7)) };
            Controls.Add(frame); Controls.Add(u);
            Size = new Size(bw + Ui.D(12) + TextRenderer.MeasureText(unit, Ui.Body).Width, Ui.D(32));
        }
        public string Raw { get { return frame.Box.Text; } set { frame.Box.Text = value; } }
    }

    class BandOrderEditor : Panel
    {
        readonly Theme T; readonly ListBox list;
        public BandOrderEditor(Theme t)
        {
            T = t; BackColor = t.Card; Size = new Size(Ui.D(SectionCard.ColWidth), Ui.D(92));
            var frame = new InputFrame(t, true, Ui.D(SectionCard.ColWidth - 92), Ui.D(92)) { Location = new Point(0, 0) };
            frame.Controls.Clear(); frame.Padding = new Padding(Ui.D(4));
            list = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Ui.D(27), BorderStyle = BorderStyle.None, BackColor = t.InputBg, ForeColor = t.Text, Font = Ui.Body, IntegralHeight = false, Dock = DockStyle.Fill };
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return; bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? T.Selection : T.InputBg)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, (e.Index + 1) + ".   " + list.Items[e.Index] + " GHz" + ((string)list.Items[e.Index] == "6" && Adapter.Found && !Adapter.Supports6 ? "   (not on this card)" : e.Index == 0 ? "   preferred" : ""), e.Index == 0 ? Ui.BodyBold : Ui.Body,
                    new Rectangle(e.Bounds.X + Ui.D(8), e.Bounds.Y, e.Bounds.Width - Ui.D(10), e.Bounds.Height), e.Index == 0 ? T.Text : T.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            frame.Controls.Add(list);
            var up = new ActionButton(t, "", "Up", false) { Surface = t.Card, Location = new Point(Ui.D(SectionCard.ColWidth - 82), 0) };
            var down = new ActionButton(t, "", "Down", false) { Surface = t.Card, Location = new Point(Ui.D(SectionCard.ColWidth - 82), Ui.D(50)) };
            up.Click += (s, e) => Shift(-1); down.Click += (s, e) => Shift(1);
            Controls.Add(frame); Controls.Add(up); Controls.Add(down);
        }
        void Shift(int d)
        {
            int i = list.SelectedIndex, j = i + d; if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var x = list.Items[i]; list.Items.RemoveAt(i); list.Items.Insert(j, x); list.SelectedIndex = j; list.Invalidate();
        }
        public List<string> Order
        {
            get { return list.Items.Cast<string>().ToList(); }
            set
            {
                list.Items.Clear();
                foreach (var b in value) if (b == "5" || b == "6" || b == "2.4") if (!list.Items.Contains(b)) list.Items.Add(b);
                foreach (var b in new[] { "5", "6", "2.4" }) if (!list.Items.Contains(b)) list.Items.Add(b);
                list.SelectedIndex = 0;
            }
        }
    }

    class SectionCard : Panel
    {
        public const int ColWidth = 280;   // input column, in 96-dpi pixels
        readonly Theme T; readonly string title, subtitle; int y; readonly List<int> seps = new List<int>(); readonly int pad, colIn;
        public SectionCard(Theme t, string title, int width, string subtitle)
        {
            T = t; this.title = title.ToUpperInvariant(); this.subtitle = subtitle;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg; Width = width; Margin = new Padding(0, 0, 0, Ui.D(12)); pad = Ui.D(20); colIn = Ui.D(ColWidth);
            y = Ui.D(42) + (subtitle != null ? TextRenderer.MeasureText(subtitle, Ui.Small, new Size(width - 2 * pad, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + Ui.D(12) : 0);
            Height = y + Ui.D(6);
        }
        public void AddRow(string label, string desc, Control input)
        {
            int textW = Width - pad * 2 - colIn - Ui.D(18);
            if (Controls.Count > 0) seps.Add(y - Ui.D(9));
            int lh = TextRenderer.MeasureText(label, Ui.BodyBold, new Size(textW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            Controls.Add(new Label { Text = label, Font = Ui.BodyBold, ForeColor = T.Text, BackColor = T.Card, AutoSize = false, UseMnemonic = false, Location = new Point(pad, y), Size = new Size(textW, lh + Ui.D(2)) });
            int textH = lh;
            if (!string.IsNullOrEmpty(desc))
            {
                int dh = TextRenderer.MeasureText(desc, Ui.Small, new Size(textW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                Controls.Add(new Label { Text = desc, Font = Ui.Small, ForeColor = T.Sub, BackColor = T.Card, AutoSize = false, UseMnemonic = false, Location = new Point(pad, y + lh + Ui.D(3)), Size = new Size(textW, dh + Ui.D(3)) });
                textH = lh + Ui.D(3) + dh;
            }
            input.Location = input is ToggleSwitch ? new Point(Width - pad - input.Width, y - Ui.D(1)) : new Point(Width - pad - colIn, y - Ui.D(5));
            Controls.Add(input);
            y += Math.Max(textH, input.Height - Ui.D(5)) + Ui.D(18);
            Height = y - Ui.D(2);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(T.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.D(9))) { using (var b = new SolidBrush(T.Card)) g.FillPath(b, p); using (var pen = new Pen(T.CardBorder)) g.DrawPath(pen, p); }
            TextRenderer.DrawText(g, title, Ui.CardTitle, new Point(pad, Ui.D(17)), T.Accent, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (subtitle != null) TextRenderer.DrawText(g, subtitle, Ui.Small, new Rectangle(pad, Ui.D(38), Width - 2 * pad, Ui.D(200)), T.Sub, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            using (var pen = new Pen(T.Grid)) foreach (var s in seps) g.DrawLine(pen, pad, s, Width - pad, s);
        }
    }

    // ---------------------------------------------------------------- the window
    class SettingsForm : Form
    {
        readonly Watchdog wd; readonly Theme T; readonly Action<string> onSaved; readonly FlowLayoutPanel flow;
        readonly ToggleSwitch tAuto, tNotify, tDefer, tLowLat, tEnforce, tQos, tWfd, tReports, tGuard, tDns, tUpload, tTurbo, tStartup;
        readonly bool startupWas; readonly Panel foot;
        readonly InputFrame fHome, fAvoidCh, fAvoidBssid, fQosExes, fGames, fExtra, fIgnore, fTarget;
        readonly NumberField nDwell, nProbe, nQuar, nRx, nTx, nDnsDays, nCap, nTurbo, nStall, nSample, nKeep, nUdp;
        readonly BandOrderEditor order;

        public SettingsForm(Watchdog w, Action<string> saved)
        {
            wd = w; onSaved = saved; Ui.InitFonts(); T = Theme.Current();
            Text = "AX210_WatchClaude — Settings"; BackColor = T.Bg; ForeColor = T.Text; Font = Ui.Body; AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen; Icon = Brand.AppIcon(); MaximizeBox = false;
            int cardW = Ui.D(720);
            ClientSize = new Size(cardW + Ui.D(32) + SystemInformation.VerticalScrollBarWidth, Ui.D(820));

            flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = T.Bg, Padding = new Padding(Ui.D(16), Ui.D(12), 0, Ui.D(6)) };
            flow.HandleCreated += (s, e) => Ui.DarkScroll(flow, T.Dark);
            flow.Controls.Add(new Label { Text = "Changes take effect when you press Save. Everything here is stored in config.json.", Font = Ui.Body, ForeColor = T.Sub, BackColor = T.Bg, AutoSize = true, Margin = new Padding(Ui.D(2), 0, 0, Ui.D(10)) });

            var gen = new SectionCard(T, "General", cardW, null);
            gen.AddRow("Automatic fixes", "Let the watchdog change bands, reconnect and re-apply settings by itself. Off = it only watches and logs.", tAuto = Tog());
            gen.AddRow("Notifications", "Pop-up messages for crashes, band switches, outages, session reports and warnings.", tNotify = Tog());
            gen.AddRow("Wait for games", "Hold non-urgent changes until the game closes. Urgent fixes, like an active driver crash, still happen.", tDefer = Tog());
            gen.AddRow("Start with Windows", "Starts quietly in the tray when you sign in, and restarts itself within 5 minutes if it ever stops.", tStartup = Tog());
            gen.AddRow("Home network", "The Wi-Fi name the watchdog manages. On any other network it only watches.", fHome = Box(false, 32));
            var bSetup = new ActionButton(T, "", "Run setup assistant…", false) { Surface = T.Card };
            bSetup.Click += (s, e) => { var a = TrayApp.Current; if (a != null) { Close(); a.ShowSetup(); } };
            gen.AddRow("Setup assistant", "Re-detects your adapter, extenders and crash-prone channels and suggests settings.", bSetup);
            flow.Controls.Add(gen);

            var wifi = new SectionCard(T, "Wi-Fi & bands", cardW, Adapter.CanLockBands ? null : "This adapter's driver doesn't support Intel band locking, so band preference and channel avoidance only steer between access points.");
            wifi.AddRow("Band preference", "The first band is preferred. If it's unusable the watchdog moves down the list, then comes back to the top when it can.", order = new BandOrderEditor(T));
            wifi.AddRow("Avoid 5 GHz channels", "Comma-separated. The setup assistant suggests channels that crashed the driver repeatedly on this PC.", fAvoidCh = Box(false, 32));
            wifi.AddRow("Avoid access points", "One BSSID per line: extenders that broadcast your network name but shouldn't be used.", fAvoidBssid = Box(true, 100));
            wifi.AddRow("Low-latency mode", "Keeps Windows' background Wi-Fi scans off and streaming mode on while connected.", tLowLat = Tog());
            wifi.AddRow("Keep adapter settings optimized", "Re-applies the tuned Intel settings if a driver update or anything else changes them.", tEnforce = Tog());
            wifi.AddRow("Minimum time between band switches", "Stops the watchdog from flip-flopping. Urgent fixes ignore this.", nDwell = Num("minutes"));
            wifi.AddRow("Retry the preferred band every", "How often to check whether the preferred band is usable again (never during games).", nProbe = Num("hours"));
            wifi.AddRow("Avoid a crashing channel for", "After 2 driver crashes in 30 minutes on a channel. Five crashes in 24 hours triples this.", nQuar = Num("hours"));
            flow.Controls.Add(wifi);

            var game = new SectionCard(T, "Gaming", cardW, "Games in Steam, Battle.net, Epic, Ubisoft, EA, GOG, Xbox, Riot and Roblox folders are found automatically.");
            game.AddRow("Wi-Fi priority for game traffic", "Tags each game's real-time (UDP) traffic so it goes first on your Wi-Fi. Downloads are never prioritized.", tQos = Tog());
            game.AddRow("Prioritize from startup", "Game programs tagged right away, one per line (e.g. Overwatch.exe). Other games get tagged the first time you play them.", fQosExes = Box(true, 72));
            game.AddRow("Pause Wi-Fi Direct while gaming", "Stops the card leaving your channel to look for casting or hotspot devices. Restored when the game closes.", tWfd = Tog());
            game.AddRow("Game session reports", "A one-line summary when a game closes: ping, loss, stalls, crashes and where you were connected.", tReports = Tog());
            game.AddRow("Warn about downloads while gaming", "Tells you when something on this PC is downloading or uploading heavily during a game, and names it.", tGuard = Tog());
            game.AddRow("Download warning above", "Sustained for 20 seconds while a game is running.", nRx = Num("Mbps"));
            game.AddRow("Upload warning above", "Sustained for 20 seconds while a game is running.", nTx = Num("Mbps"));
            game.AddRow("Game names", "Process names (without .exe) that always count as a game, one per line. Needed for games whose anti-cheat hides where they run from.", fGames = Box(true, 130));
            var extraWrap = new Panel { BackColor = T.Card, Size = new Size(Ui.D(SectionCard.ColWidth), Ui.D(120)) };
            fExtra = Box(true, 82); fExtra.Location = new Point(0, 0); extraWrap.Controls.Add(fExtra);
            var addFolder = new ActionButton(T, "", "Add folder…", false) { Surface = T.Card, Location = new Point(0, Ui.D(88)) };
            addFolder.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Choose a folder that contains games", ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK) fExtra.Box.AppendText((fExtra.Box.Text.Length > 0 && !fExtra.Box.Text.EndsWith("\n") ? "\r\n" : "") + d.SelectedPath + "\r\n");
            };
            extraWrap.Controls.Add(addFolder);
            game.AddRow("Extra game folders", "Other places you keep games, one per line. Anything you play from these counts as a game.", extraWrap);
            game.AddRow("Ignored folders", "Folder names inside game libraries that are tools, not games (Wallpaper Engine, launchers).", fIgnore = Box(true, 100));
            flow.Controls.Add(game);

            var net = new SectionCard(T, "Network extras", cardW, null);
            net.AddRow("Automatic DNS", "Benchmarks the main DNS providers and switches only for a clear win (at least 2 ms and 10% faster).", tDns = Tog());
            net.AddRow("Re-test DNS every", null, nDnsDays = Num("days"));
            net.AddRow("Upload limiter", "Caps this PC's non-game uploads just below your line speed so big uploads don't add lag. Game traffic is exempt.", tUpload = Tog());
            net.AddRow("Upload cap", string.Format("0 = automatic: 90% of your last speed-test upload ({0}).", wd.St.LastUploadMbps > 1 ? wd.St.LastUploadMbps.ToString("0") + " Mbps" : "no speed test yet"), nCap = Num("Mbps"));
            net.AddRow("Fast downloads on 6 GHz", "During a big download with no game open, switch to 6 GHz's faster link, then back when it ends or a game starts. Each switch drops you for 3–5 s.", tTurbo = Tog());
            net.AddRow("Fast downloads kick in above", "Sustained for 30 seconds with no game running.", nTurbo = Num("Mbps"));
            flow.Controls.Add(net);

            var mon = new SectionCard(T, "Monitoring", cardW, null);
            mon.AddRow("Stall threshold", "A router ping at or above this counts as a stall in the graph, stats and reports.", nStall = Num("ms"));
            mon.AddRow("Internet ping target", "Pinged every 2 seconds to tell Wi-Fi problems apart from internet outages.", fTarget = Box(false, 32));
            mon.AddRow("Sample interval", "How often a line is written to the samples log.", nSample = Num("seconds"));
            mon.AddRow("Keep samples for", null, nKeep = Num("days"));
            mon.AddRow("Network port warning at", "Windows has about 16,000 UDP ports for outgoing connections. Warn when this many are in use.", nUdp = Num("ports"));
            flow.Controls.Add(mon);

            // footer
            foot = new Panel { Dock = DockStyle.Bottom, Height = Ui.D(56), BackColor = T.Bg, Width = ClientSize.Width };
            foot.Paint += (s, e) => { using (var p = new Pen(T.CardBorder)) e.Graphics.DrawLine(p, 0, 0, foot.Width, 0); };
            var bOpen = new ActionButton(T, "", "Open config.json", false);
            var bFolder = new ActionButton(T, "", "Open folder", false);
            var bReload = new ActionButton(T, "", "Reload from file", false);
            var bDefaults = new ActionButton(T, "", "Restore defaults", false);
            var bCancel = new ActionButton(T, "", "Cancel", false);
            var bSave = new ActionButton(T, "", "Save", true);
            int x = Ui.D(16), by = (foot.Height - bSave.Height) / 2;
            foreach (var b in new[] { bOpen, bFolder, bReload, bDefaults }) { b.Location = new Point(x, by); x += b.Width + Ui.D(6); foot.Controls.Add(b); }
            bSave.Location = new Point(foot.Width - Ui.D(16) - bSave.Width, by); bCancel.Location = new Point(bSave.Left - Ui.D(8) - bCancel.Width, by);
            bSave.Anchor = AnchorStyles.Top | AnchorStyles.Right; bCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            foot.Controls.Add(bCancel); foot.Controls.Add(bSave);
            bOpen.Click += (s, e) => { try { Process.Start("notepad.exe", Paths.ConfigFile); } catch { } MessageBox.Show(this, "config.json is open in Notepad. After saving it there, press \"Reload from file\" here.", "AX210_WatchClaude", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            bFolder.Click += (s, e) => { try { Process.Start("explorer.exe", Paths.Data); } catch { } };
            bReload.Click += (s, e) => { var r = wd.ReloadFromFile(); Fill(wd.Cfg); MessageBox.Show(this, r, "Reload from file", MessageBoxButtons.OK, r.StartsWith("config.json has a mistake") ? MessageBoxIcon.Warning : MessageBoxIcon.Information); };
            bDefaults.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Fill in the default for every setting? Your home network name is kept. Nothing is saved until you press Save.", "Restore defaults", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var d = new Config { HomeSsid = wd.Cfg.HomeSsid }; foreach (var n in GameList.Popular) if (!d.GameProcesses.Contains(n)) d.GameProcesses.Add(n); Fill(d);
            };
            bCancel.Click += (s, e) => Close();
            bSave.Click += (s, e) =>
            {
                Config n; string err;
                if (!TryBuild(out n, out err)) { MessageBox.Show(this, err, "A few things need fixing", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var r = wd.ApplySettings(n);
                if (tStartup.Checked != startupWas && wd.IsAdmin)
                    r += Setup.RegisterTask(tStartup.Checked) ? (tStartup.Checked ? " Starts with Windows." : " Won't start with Windows (the Start menu shortcut still works).") : " Couldn't change the start-with-Windows setting.";
                var cb = onSaved; if (cb != null) cb(r); Close();
            };
            Controls.Add(foot); Controls.Add(flow); flow.BringToFront();
            AcceptButton = null; KeyPreview = true; KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            startupWas = Setup.IsAutostart(); tStartup.Checked = startupWas; tStartup.Enabled = wd.IsAdmin;
            Fill(wd.Cfg);
            // tall enough for everything when the screen allows, otherwise as tall as the screen (and the list scrolls)
            int content = flow.Padding.Vertical; foreach (Control c in flow.Controls) content += c.Height + c.Margin.Vertical;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(ClientSize.Width, Math.Min(content + foot.Height, wa.Height - Ui.D(16) - (Height - ClientSize.Height)));
            MinimumSize = new Size(Width, Math.Min(Ui.D(480), wa.Height)); MaximumSize = new Size(Width, wa.Height + Ui.D(40));
        }

        ToggleSwitch Tog() { return new ToggleSwitch(T); }
        InputFrame Box(bool multi, int h) { return new InputFrame(T, multi, Ui.D(SectionCard.ColWidth), Ui.D(h)); }
        NumberField Num(string unit) { return new NumberField(T, unit); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.DarkTitle(Handle, T.Dark); }
        public void ScrollForPreview(double fraction) { flow.AutoScrollPosition = new Point(0, (int)(flow.DisplayRectangle.Height * fraction)); }

        static string J(IEnumerable<string> l) { return string.Join("\r\n", l ?? Enumerable.Empty<string>()); }
        static string F(double v) { return v.ToString("0.##", CultureInfo.CurrentCulture); }
        static IEnumerable<string> Lines(InputFrame f) { return f.Box.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x != ""); }

        void Fill(Config c)
        {
            tAuto.Checked = c.AutoActions; tNotify.Checked = c.Notifications; tDefer.Checked = c.DeferWhileGaming; fHome.Box.Text = Ui.Rd(c.HomeSsid ?? "");
            order.Order = c.BandOrder ?? new List<string>(); fAvoidCh.Box.Text = string.Join(", ", c.AvoidChannels5 ?? new List<int>()); fAvoidBssid.Box.Text = Ui.Rd(J(c.AvoidBssids));
            tLowLat.Checked = c.LowLatencyMode; tEnforce.Checked = c.EnforceAdapterProfile;
            nDwell.Raw = c.SwitchDwellMinutes.ToString(); nProbe.Raw = c.ProbePreferredHours.ToString(); nQuar.Raw = c.QuarantineHours.ToString();
            tQos.Checked = c.QosTagGame; fQosExes.Box.Text = J(c.QosAlwaysExes); tWfd.Checked = c.SuspendWifiDirectWhileGaming; tReports.Checked = c.SessionReports;
            tGuard.Checked = c.BandwidthGuard; nRx.Raw = F(c.GuardRxMbps); nTx.Raw = F(c.GuardTxMbps);
            fGames.Box.Text = J(c.GameProcesses); fExtra.Box.Text = J(c.ExtraGameFolders); fIgnore.Box.Text = J(c.IgnoreGameFolders);
            tDns.Checked = c.AutoDns; nDnsDays.Raw = c.DnsBenchmarkDays.ToString(); tUpload.Checked = c.UploadLimiter; nCap.Raw = F(c.UploadCapMbps);
            tTurbo.Checked = c.TurboDownloads; nTurbo.Raw = F(c.TurboRxMbps);
            nStall.Raw = c.StallMs.ToString(); fTarget.Box.Text = c.InternetTarget ?? ""; nSample.Raw = c.SampleSeconds.ToString(); nKeep.Raw = c.KeepSampleDays.ToString(); nUdp.Raw = c.UdpWarnThreshold.ToString();
        }

        bool TryBuild(out Config n, out string err)
        {
            n = Json.S().Deserialize<Config>(Json.S().Serialize(wd.Cfg)); err = null; var errs = new List<string>();
            n.AutoActions = tAuto.Checked; n.Notifications = tNotify.Checked; n.DeferWhileGaming = tDefer.Checked; n.HomeSsid = fHome.Box.Text.Trim();
            n.BandOrder = order.Order;
            var ch = new List<int>();
            foreach (var p in fAvoidCh.Box.Text.Split(new[] { ',', ' ', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            { int v; if (int.TryParse(p, out v) && v >= 1 && v <= 196) ch.Add(v); else errs.Add("Avoid 5 GHz channels: \"" + p + "\" isn't a channel number."); }
            n.AvoidChannels5 = ch.Distinct().ToList();
            var bs = new List<string>();
            foreach (var line in Lines(fAvoidBssid))
            { var b = line.ToLowerInvariant().Replace('-', ':'); if (Regex.IsMatch(b, "^([0-9a-f]{2}:){5}[0-9a-f]{2}$")) bs.Add(b); else errs.Add("Avoid access points: \"" + line + "\" isn't a BSSID (like 12:34:56:ab:cd:ef)."); }
            n.AvoidBssids = bs.Distinct().ToList();
            n.LowLatencyMode = tLowLat.Checked; n.EnforceAdapterProfile = tEnforce.Checked;
            n.QosTagGame = tQos.Checked; n.SuspendWifiDirectWhileGaming = tWfd.Checked; n.SessionReports = tReports.Checked; n.BandwidthGuard = tGuard.Checked;
            // accepts names, file names or full paths (quoted or not, e.g. from "Copy as path"); keeps just the program name
            n.QosAlwaysExes = Lines(fQosExes).Select(x => Names.CleanProgram(x, true)).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            n.GameProcesses = Lines(fGames).Select(x => Names.CleanProgram(x, false)).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            n.ExtraGameFolders = Lines(fExtra).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            n.IgnoreGameFolders = Lines(fIgnore).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            n.AutoDns = tDns.Checked; n.UploadLimiter = tUpload.Checked; n.TurboDownloads = tTurbo.Checked;
            n.InternetTarget = fTarget.Box.Text.Trim(); if (n.InternetTarget == "" || n.InternetTarget.Contains(" ")) errs.Add("Internet ping target: enter an address like 1.1.1.1.");
            int iv; double dv;
            if (Int(nDwell, "Minimum time between band switches", 0, 1440, errs, out iv)) n.SwitchDwellMinutes = iv;
            if (Int(nProbe, "Retry the preferred band every", 1, 168, errs, out iv)) n.ProbePreferredHours = iv;
            if (Int(nQuar, "Avoid a crashing channel for", 1, 720, errs, out iv)) n.QuarantineHours = iv;
            if (Dbl(nRx, "Download warning", 1, 10000, errs, out dv)) n.GuardRxMbps = dv;
            if (Dbl(nTx, "Upload warning", 1, 10000, errs, out dv)) n.GuardTxMbps = dv;
            if (Int(nDnsDays, "Re-test DNS every", 1, 365, errs, out iv)) n.DnsBenchmarkDays = iv;
            if (Dbl(nCap, "Upload cap", 0, 10000, errs, out dv)) n.UploadCapMbps = dv;
            if (Dbl(nTurbo, "Fast downloads kick in above", 50, 10000, errs, out dv)) n.TurboRxMbps = dv;
            if (Int(nStall, "Stall threshold", 20, 2000, errs, out iv)) n.StallMs = iv;
            if (Int(nSample, "Sample interval", 1, 600, errs, out iv)) n.SampleSeconds = iv;
            if (Int(nKeep, "Keep samples for", 1, 365, errs, out iv)) n.KeepSampleDays = iv;
            if (Int(nUdp, "Network port warning", 100, 16000, errs, out iv)) n.UdpWarnThreshold = iv;
            if (errs.Count == 0) return true;
            err = string.Join("\n\n", errs.Take(4)) + (errs.Count > 4 ? "\n\n…and " + (errs.Count - 4) + " more." : "");
            return false;
        }
        static bool Int(NumberField f, string name, int min, int max, List<string> errs, out int v)
        {
            if (int.TryParse(f.Raw.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out v) && v >= min && v <= max) return true;
            errs.Add(string.Format("{0}: enter a whole number from {1} to {2}.", name, min, max)); return false;
        }
        static bool Dbl(NumberField f, string name, double min, double max, List<string> errs, out double v)
        {
            if ((double.TryParse(f.Raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v) || double.TryParse(f.Raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) && v >= min && v <= max) return true;
            errs.Add(string.Format("{0}: enter a number from {1} to {2}.", name, min, max)); return false;
        }
    }
}
