// =====================================================================================
//  AX210_WatchClaude — status window
//  Custom-drawn, follows Windows light/dark mode, sized to fit its contents (and small screens):
//  status banner · metric tiles · ping graph · grouped info cards · actions · tabbed tables
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AX210WatchClaude
{
    class Theme
    {
        public bool Dark;
        public Color Bg, Card, CardBorder, Stripe, Text, Sub, Faint, Accent, Good, Warn, Bad, Purple, Grid, GraphGw, GraphNet, Hover, Selection, InputBg;
        public static Theme Current()
        {
            bool dark = false;
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) dark = k != null && (k.GetValue("AppsUseLightTheme") as int?) == 0; } catch { }
            if (dark) return new Theme
            {
                Dark = true, Bg = C(0x17191D), Card = C(0x22252B), CardBorder = C(0x32363E), Stripe = C(0x262930), Text = C(0xEDEFF2), Sub = C(0xAEB4BD), Faint = C(0x5F646D),
                Accent = C(0x5B9BFF), Good = C(0x3FB950), Warn = C(0xE3A008), Bad = C(0xF85149), Purple = C(0xB28CFF), Grid = C(0x2D3138),
                GraphGw = C(0x5B9BFF), GraphNet = C(0x8B949E), Hover = C(0x2C3038), Selection = C(0x22406E), InputBg = C(0x1A1D22)
            };
            return new Theme
            {
                Dark = false, Bg = C(0xF3F4F6), Card = C(0xFFFFFF), CardBorder = C(0xE1E4E8), Stripe = C(0xF8F9FB), Text = C(0x101623), Sub = C(0x5B6370), Faint = C(0xC3C7CE),
                Accent = C(0x2563EB), Good = C(0x16A34A), Warn = C(0xD97706), Bad = C(0xDC2626), Purple = C(0x7C3AED), Grid = C(0xECEEF1),
                GraphGw = C(0x2563EB), GraphNet = C(0x9CA3AF), Hover = C(0xF1F3F6), Selection = C(0xDBE7FF), InputBg = C(0xF7F8FA)
            };
        }
        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public Color Level(int lvl) { return lvl == 0 ? Good : lvl == 1 ? Warn : lvl == 2 ? Bad : Sub; }
    }

    static class Ui
    {
        public static float S = 1f;
        public static int D(float px) { return (int)Math.Round(px * S); }
        public static Font Title, Big, CardTitle, Body, BodyBold, Small, Mono, MonoBold, Icon;
        public static void InitFonts()
        {
            if (Title != null) return;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f; } catch { }   // pixel sizes follow the display scale; fonts (points) already do
            // Windows 11's Segoe UI Variable (optical sizes tuned for each size) renders crisper than classic Segoe UI; fall back on Windows 10.
            Title = Pick(new[] { "Segoe UI Variable Display Semibold", "Segoe UI Semibold" }, 13f); Big = Pick(new[] { "Segoe UI Variable Display Semibold", "Segoe UI Semibold" }, 17f);
            CardTitle = Pick(new[] { "Segoe UI Variable Small Semibold", "Segoe UI Semibold" }, 7.5f);
            Body = Pick(new[] { "Segoe UI Variable Text", "Segoe UI" }, 9f); BodyBold = Pick(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" }, 9f);
            Small = Pick(new[] { "Segoe UI Variable Small", "Segoe UI" }, 8.25f);
            Mono = Pick(new[] { "Cascadia Mono", "Consolas" }, 9f) ?? new Font(FontFamily.GenericMonospace, 9f); MonoBold = new Font(Mono, FontStyle.Bold);
            Icon = Pick(new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 10f);
        }
        static Font Pick(string[] names, float size) { foreach (var n in names) { var f = new Font(n, size); if (f.Name == n) return f; f.Dispose(); } return new Font("Segoe UI", size); }

        // ---- screenshot redaction (--render … --redact): network names, MAC addresses, user names, global IPv6
        public static bool Redact; public static readonly Dictionary<string, string> RedactWords = new Dictionary<string, string>();
        static readonly Dictionary<string, string> fakeMacs = new Dictionary<string, string>();
        public static string Rd(string s)
        {
            if (!Redact || string.IsNullOrEmpty(s)) return s;
            foreach (var kv in RedactWords) if (!string.IsNullOrEmpty(kv.Key)) s = s.Replace(kv.Key, kv.Value);
            s = Regex.Replace(s, @"(?<![0-9A-Fa-f:])([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])", m =>
            {
                var k = m.Value.ToLowerInvariant().Replace('-', ':'); string f;
                lock (fakeMacs) if (!fakeMacs.TryGetValue(k, out f)) fakeMacs[k] = f = string.Format("02:00:5e:10:{0:x2}:{1:x2}", 0x20 + fakeMacs.Count, 0x31 + fakeMacs.Count);
                return f;
            });
            s = Regex.Replace(s, @"(?<![0-9A-Fa-f:])[23][0-9A-Fa-f]{3}:[0-9A-Fa-f:]{4,}", m => DnsTools.IsKnownResolver(m.Value) ? m.Value : "2001:db8::1");
            s = Regex.Replace(s, @"\bLinux/[\w.\-]+", "Linux");   // router firmware string in the UPnP line
            return s.Replace(@"\Users\" + Environment.UserName, @"\Users\you");
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        public static void DarkTitle(IntPtr h, bool dark) { int v = dark ? 1 : 0; try { DwmSetWindowAttribute(h, 20, ref v, 4); } catch { } }
        public static void DarkScroll(Control c, bool dark) { if (dark) try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Max(1f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        public static Color Mix(Color a, Color b, float t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }
        public static Size PillSize(string text) { var s = TextRenderer.MeasureText(text, Small, Size.Empty, TextFormatFlags.NoPadding); return new Size(s.Width + D(16), D(20)); }
        public static void Pill(Graphics g, Rectangle r, string text, Color c, Theme t)
        {
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, r.Height / 2f)) using (var b = new SolidBrush(Mix(t.Card, c, t.Dark ? 0.24f : 0.13f))) g.FillPath(b, p);
            g.SmoothingMode = sm;
            TextRenderer.DrawText(g, text, Small, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // ---------------------------------------------------------------- building blocks
    class CardBase : Control
    {
        protected readonly Theme T;
        public CardBase(Theme t)
        {
            T = t; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg; ForeColor = t.Text; Margin = new Padding(Ui.D(4));
        }
        protected void PaintCard(Graphics g, Color? strip)
        {
            g.Clear(T.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.D(9)))
            {
                using (var b = new SolidBrush(T.Card)) g.FillPath(b, p);
                if (strip.HasValue) { var st = g.Save(); g.SetClip(p); using (var b = new SolidBrush(strip.Value)) g.FillRectangle(b, 0, 0, Ui.D(3), Height); g.Restore(st); }
                using (var pen = new Pen(T.CardBorder)) g.DrawPath(pen, p);
            }
        }
    }

    class HeaderBanner : CardBase
    {
        int level = 3; string title = "Starting…", sub = ""; List<Tuple<string, Color>> pills = new List<Tuple<string, Color>>();
        public HeaderBanner(Theme t) : base(t) { }
        public void Set(int lvl, string ti, string su, List<Tuple<string, Color>> p)
        {
            ti = Ui.Rd(ti); su = Ui.Rd(su);
            bool same = lvl == level && ti == title && su == sub && p.Count == pills.Count && p.Zip(pills, (a, b) => a.Item1 == b.Item1 && a.Item2 == b.Item2).All(x => x);
            if (same) return; level = lvl; title = ti; sub = su; pills = p; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var c = T.Level(level); PaintCard(g, c);
            int r = Ui.D(16), cx = Ui.D(16), cy = Height / 2;
            using (var b = new SolidBrush(Ui.Mix(T.Card, c, T.Dark ? 0.26f : 0.15f))) g.FillEllipse(b, cx, cy - r, 2 * r, 2 * r);
            string glyph = level == 0 ? "" : level == 1 ? "" : level == 2 ? "" : "";
            if (Ui.Icon != null) TextRenderer.DrawText(g, glyph, Ui.Icon, new Rectangle(cx, cy - r, 2 * r, 2 * r), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int tx = cx + 2 * r + Ui.D(14), px = Width - Ui.D(16);
            for (int i = pills.Count - 1; i >= 0; i--)
            {
                var sz = Ui.PillSize(pills[i].Item1); px -= sz.Width;
                Ui.Pill(g, new Rectangle(px, cy - sz.Height / 2, sz.Width, sz.Height), pills[i].Item1, pills[i].Item2, T); px -= Ui.D(6);
            }
            int tw = Math.Max(Ui.D(100), px - tx - Ui.D(10));
            var ts = TextRenderer.MeasureText(g, "Ag", Ui.Title, Size.Empty, TextFormatFlags.NoPadding); var ss = TextRenderer.MeasureText(g, "Ag", Ui.Body, Size.Empty, TextFormatFlags.NoPadding);
            int ty = cy - (ts.Height + Ui.D(2) + ss.Height) / 2;
            TextRenderer.DrawText(g, title, Ui.Title, new Rectangle(tx, ty, tw, ts.Height), T.Text, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, sub, Ui.Body, new Rectangle(tx, ty + ts.Height + Ui.D(2), tw, ss.Height), T.Sub, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    class MetricCard : CardBase
    {
        readonly string title; string value = "—", unit = "", detail = ""; Color accent; int bars = -1;
        public MetricCard(Theme t, string title) : base(t) { this.title = title.ToUpperInvariant(); accent = t.Faint; }
        public void Set(string v, string u, string d, Color a, int barCount = -1)
        {
            if (v == value && u == unit && d == detail && a == accent && barCount == bars) return;
            value = v; unit = u; detail = d; accent = a; bars = barCount; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; PaintCard(g, accent); int x = Ui.D(15);
            TextRenderer.DrawText(g, title, Ui.CardTitle, new Point(x, Ui.D(11)), T.Sub, TextFormatFlags.NoPadding);
            int vy = Ui.D(26); var vs = TextRenderer.MeasureText(g, value, Ui.Big, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, value, Ui.Big, new Point(x - Ui.D(1), vy), T.Text, TextFormatFlags.NoPadding);
            if (unit != "")
            {
                var us = TextRenderer.MeasureText(g, unit, Ui.Body, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, unit, Ui.Body, new Point(x + vs.Width + Ui.D(3), vy + vs.Height - us.Height - Ui.D(4)), T.Sub, TextFormatFlags.NoPadding);
            }
            if (bars >= 0)
            {
                int bw = Ui.D(4), gap = Ui.D(3), bx = Width - Ui.D(15) - 4 * bw - 3 * gap, by = Ui.D(24);
                for (int i = 0; i < 4; i++) { int h = Ui.D(4 + 3.5f * i); using (var b = new SolidBrush(i < bars ? accent : T.Faint)) g.FillRectangle(b, bx + i * (bw + gap), by - h, bw, h); }
            }
            TextRenderer.DrawText(g, detail, Ui.Small, new Rectangle(x, Height - Ui.D(23), Width - x - Ui.D(8), Ui.D(17)), T.Sub, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    class InfoCard : CardBase
    {
        public class Row { public string K, V; public Color? C; public bool Bold; }
        readonly string title; List<Row> rows = new List<Row>(); int lastH = -1; public int KeyWidth;
        public InfoCard(Theme t, string title) : base(t) { this.title = title.ToUpperInvariant(); KeyWidth = Ui.D(112); AutoSize = true; }
        int ValueWidth(int w) { return Math.Max(Ui.D(60), w - Ui.D(15) - KeyWidth - Ui.D(22)); }
        int Measure(int w)
        {
            int y = Ui.D(34);
            foreach (var r in rows) y += Math.Max(TextRenderer.MeasureText(r.V ?? "", Ui.Body, new Size(ValueWidth(w), 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height, Ui.Body.Height) + Ui.D(6);
            return y + Ui.D(6);
        }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 50 && proposed.Width < 10000 ? proposed.Width : Math.Max(Width, Ui.D(300));
            return new Size(w, Measure(w));
        }
        public void SetRows(List<Row> r)
        {
            foreach (var x in r) x.V = Ui.Rd(x.V);
            bool same = r.Count == rows.Count;
            for (int i = 0; same && i < r.Count; i++) { var a = r[i]; var b = rows[i]; same = a.K == b.K && a.V == b.V && a.C == b.C && a.Bold == b.Bold; }
            if (same) return;
            rows = r; int h = Measure(Width);
            if (h != lastH) { lastH = h; for (var p = Parent; p != null; p = p.Parent) p.PerformLayout(); }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; PaintCard(g, null);
            TextRenderer.DrawText(g, title, Ui.CardTitle, new Point(Ui.D(15), Ui.D(12)), T.Sub, TextFormatFlags.NoPadding);
            int y = Ui.D(34), vx = Ui.D(15) + KeyWidth + Ui.D(7), vw = ValueWidth(Width);
            using (var pen = new Pen(T.Grid))
                foreach (var r in rows)
                {
                    var s = TextRenderer.MeasureText(g, r.V ?? "", Ui.Body, new Size(vw, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    int h = Math.Max(s.Height, Ui.Body.Height);
                    TextRenderer.DrawText(g, r.K, Ui.Small, new Rectangle(Ui.D(15), y + Ui.D(1), KeyWidth, Ui.Body.Height), T.Sub, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, r.V ?? "", r.Bold ? Ui.BodyBold : Ui.Body, new Rectangle(vx, y, vw, h + Ui.D(2)), r.C ?? T.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    y += h + Ui.D(6);
                    if (r != rows[rows.Count - 1]) g.DrawLine(pen, Ui.D(15), y - Ui.D(3), Width - Ui.D(15), y - Ui.D(3));
                }
        }
    }

    class PingGraphX : CardBase
    {
        readonly Watchdog wd; const int Secs = 180;
        public PingGraphX(Theme t, Watchdog w) : base(t) { wd = w; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; PaintCard(g, null); int W = Width, H = Height, L = Ui.D(46), R = Ui.D(16), Tp = Ui.D(34), B = Ui.D(24);
            const string gt = "PING · LAST 3 MINUTES";
            TextRenderer.DrawText(g, gt, Ui.CardTitle, new Point(Ui.D(15), Ui.D(12)), T.Sub, TextFormatFlags.NoPadding);
            int stalls10 = wd.Pinger.Stats(600).Stalls; var gts = TextRenderer.MeasureText(g, gt, Ui.CardTitle, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, string.Format("·   {0} stall{1} ≥{2} ms in the last 10 min", stalls10, stalls10 == 1 ? "" : "s", wd.Cfg.StallMs), Ui.Small,
                new Point(Ui.D(15) + gts.Width + Ui.D(8), Ui.D(11)), stalls10 > 0 ? T.Warn : T.Sub, TextFormatFlags.NoPadding);
            var legend = new[] { Tuple.Create("Router", T.GraphGw), Tuple.Create("Internet", T.GraphNet), Tuple.Create("Lost packet", T.Bad), Tuple.Create("Stall line", T.Warn) };
            int lx = W - Ui.D(16);
            for (int i = legend.Length - 1; i >= 0; i--)
            {
                var s = TextRenderer.MeasureText(g, legend[i].Item1, Ui.Small, Size.Empty, TextFormatFlags.NoPadding); lx -= s.Width;
                TextRenderer.DrawText(g, legend[i].Item1, Ui.Small, new Point(lx, Ui.D(11)), T.Sub, TextFormatFlags.NoPadding); lx -= Ui.D(12);
                using (var b = new SolidBrush(legend[i].Item2)) g.FillEllipse(b, lx, Ui.D(11) + s.Height / 2 - Ui.D(4), Ui.D(8), Ui.D(8)); lx -= Ui.D(14);
            }
            var now = DateTime.Now; var data = wd.Pinger.Recent(Secs);
            double peak = data.Select(x => (double)Math.Max(x.Gw, x.Net)).DefaultIfEmpty(0).Max();
            double max = new[] { 50.0, 100, 150, 250, 500, 1000 }.First(v => v >= peak || v == 1000);
            Func<double, float> Y = v => (float)(Tp + (H - Tp - B) * (1 - Math.Min(v, max) / max));
            Func<DateTime, float> X = t => (float)(L + (W - L - R) * (1 - (now - t).TotalSeconds / Secs));
            double step = max <= 50 ? 10 : max <= 100 ? 25 : max <= 250 ? 50 : max <= 500 ? 100 : 250;
            using (var grid = new Pen(T.Grid))
                for (double v = 0; v <= max + 0.1; v += step)
                {
                    float y = Y(v); g.DrawLine(grid, L, y, W - R, y);
                    TextRenderer.DrawText(g, v.ToString("0") + (v + step > max + 0.1 ? " ms" : ""), Ui.Small, new Rectangle(0, (int)y - Ui.D(8), L - Ui.D(8), Ui.D(16)), T.Sub, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            int stall = wd.Cfg.StallMs;
            if (stall < max) using (var p = new Pen(Ui.Mix(T.Card, T.Warn, 0.75f), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, L, Y(stall), W - R, Y(stall));
            foreach (var m in new[] { 3, 2, 1, 0 })
            {
                float x = X(now.AddMinutes(-m)); var txt = m == 0 ? "now" : m + " min ago"; var s = TextRenderer.MeasureText(g, txt, Ui.Small, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, txt, Ui.Small, new Point((int)Math.Min(Math.Max(x - s.Width / 2f, L), W - R - s.Width), H - B + Ui.D(5)), T.Sub, TextFormatFlags.NoPadding);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Series(g, data.Where(s => s.Net >= 0).ToList(), s => s.Net, T.GraphNet, X, Y, false, H - B, 3.5);
            Series(g, data.Where(s => s.Gw >= 0).ToList(), s => s.Gw, T.GraphGw, X, Y, true, H - B, 2.5);
            using (var red = new Pen(T.Bad, Ui.D(2))) foreach (var s in data.Where(s => s.Gw == -1 || s.Net == -1)) { float x = X(s.T); g.DrawLine(red, x, H - B - Ui.D(10), x, H - B); }
            if (data.Count == 0) TextRenderer.DrawText(g, "collecting…", Ui.Body, new Rectangle(L, Tp, W - L - R, H - Tp - B), T.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        static void Series(Graphics g, List<PingSample> pts, Func<PingSample, int> sel, Color c, Func<DateTime, float> X, Func<double, float> Y, bool fill, float baseY, double gapSec)
        {
            var segs = new List<List<PointF>>(); List<PointF> cur = null; DateTime last = DateTime.MinValue;
            foreach (var p in pts) { if (cur == null || (p.T - last).TotalSeconds > gapSec) { cur = new List<PointF>(); segs.Add(cur); } cur.Add(new PointF(X(p.T), Y(sel(p)))); last = p.T; }
            foreach (var s in segs.Where(x => x.Count >= 2))
            {
                if (fill)
                    using (var path = new GraphicsPath())
                    {
                        path.AddLines(s.ToArray()); path.AddLine(s[s.Count - 1].X, s[s.Count - 1].Y, s[s.Count - 1].X, baseY); path.AddLine(s[s.Count - 1].X, baseY, s[0].X, baseY); path.CloseFigure();
                        using (var b = new LinearGradientBrush(new RectangleF(0, Y(50) - 1, 1, baseY - Y(50) + 2), Color.FromArgb(80, c), Color.FromArgb(8, c), LinearGradientMode.Vertical)) g.FillPath(b, path);
                    }
                using (var pen = new Pen(c, fill ? 2f : 1.4f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, s.ToArray());
            }
        }
    }

    class ActionButton : Control
    {
        readonly Theme T; string glyph, label; readonly bool primary; bool hover, down; Color surface;
        public Color Surface { get { return surface; } set { surface = value; BackColor = value; Invalidate(); } }
        public ActionButton(Theme t, string glyph, string label, bool primary)
        {
            T = t; this.glyph = glyph; this.label = label; this.primary = primary; surface = t.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg; Cursor = Cursors.Hand; Margin = new Padding(Ui.D(4), Ui.D(3), Ui.D(2), Ui.D(3)); FitSize();
        }
        public void SetLabel(string g, string l) { if (g == glyph && l == label) return; glyph = g; label = l; FitSize(); Invalidate(); }
        bool HasGlyph { get { return Ui.Icon != null && !string.IsNullOrEmpty(glyph); } }
        void FitSize() { var s = TextRenderer.MeasureText(label, Ui.BodyBold, Size.Empty, TextFormatFlags.NoPadding); Size = new Size(s.Width + Ui.D(HasGlyph ? 44 : 28), Ui.D(32)); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(surface); g.SmoothingMode = SmoothingMode.AntiAlias;
            bool en = Enabled;
            Color fill = primary ? (!en ? Ui.Mix(T.Accent, T.Bg, 0.55f) : down ? Ui.Mix(T.Accent, Color.Black, 0.18f) : hover ? Ui.Mix(T.Accent, Color.White, 0.12f) : T.Accent)
                                 : (down ? Ui.Mix(T.Hover, T.CardBorder, 0.6f) : hover && en ? T.Hover : T.Card);
            using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.D(7)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (!primary) using (var pen = new Pen(hover && en ? Ui.Mix(T.CardBorder, T.Accent, 0.5f) : T.CardBorder)) g.DrawPath(pen, p);
            }
            int x = Ui.D(12);
            if (HasGlyph)
            {
                TextRenderer.DrawText(g, glyph, Ui.Icon, new Rectangle(x, 0, Ui.D(16), Height), primary ? Color.White : en ? T.Accent : T.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                x += Ui.D(22);
            }
            else x = Ui.D(14);
            TextRenderer.DrawText(g, label, Ui.BodyBold, new Rectangle(x, 0, Width - x - Ui.D(6), Height), primary ? Color.White : en ? T.Text : T.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    }

    class TabStrip : Control
    {
        readonly Theme T; readonly string[] tabs; public int Selected; int hover = -1; Rectangle[] rects = new Rectangle[0];
        public event Action<int> Changed;
        public TabStrip(Theme t, string[] tabs)
        {
            T = t; this.tabs = tabs; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg; Cursor = Cursors.Hand; Margin = new Padding(Ui.D(4), Ui.D(2), Ui.D(4), 0);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(T.Bg); g.SmoothingMode = SmoothingMode.AntiAlias; int x = Ui.D(2); rects = new Rectangle[tabs.Length];
            for (int i = 0; i < tabs.Length; i++)
            {
                var s = TextRenderer.MeasureText(g, tabs[i], Ui.BodyBold, Size.Empty, TextFormatFlags.NoPadding);
                var r = new Rectangle(x, 0, s.Width + Ui.D(26), Height); rects[i] = r;
                if (i == hover && i != Selected) using (var b = new SolidBrush(T.Hover)) using (var p = Ui.Round(new RectangleF(r.X, r.Y + Ui.D(3), r.Width, r.Height - Ui.D(9)), Ui.D(7))) g.FillPath(b, p);
                TextRenderer.DrawText(g, tabs[i], i == Selected ? Ui.BodyBold : Ui.Body, new Rectangle(r.X, r.Y, r.Width, r.Height - Ui.D(4)), i == Selected ? T.Text : T.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (i == Selected) using (var b = new SolidBrush(T.Accent)) using (var p = Ui.Round(new RectangleF(r.X + Ui.D(12), r.Bottom - Ui.D(4), r.Width - Ui.D(24), Ui.D(3)), Ui.D(2))) g.FillPath(b, p);
                x += r.Width + Ui.D(2);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) { int h = Array.FindIndex(rects, r => r.Contains(e.Location)); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { int h = Array.FindIndex(rects, r => r.Contains(e.Location)); if (h >= 0) SelectTab(h); }
        public void SelectTab(int i) { if (i == Selected) return; Selected = i; Invalidate(); var c = Changed; if (c != null) c(i); }
    }

    class CardPanel : Panel
    {
        readonly Theme T;
        public CardPanel(Theme t)
        {
            T = t; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = t.Bg; Padding = new Padding(Ui.D(10), Ui.D(6), Ui.D(10), Ui.D(8)); Margin = new Padding(Ui.D(4));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(T.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.D(9))) { using (var b = new SolidBrush(T.Card)) g.FillPath(b, p); using (var pen = new Pen(T.CardBorder)) g.DrawPath(pen, p); }
        }
    }

    class NiceList : ListView
    {
        readonly Theme T; public int PillColumn = -1;
        public NiceList(Theme t, string[] cols)
        {
            T = t; View = View.Details; FullRowSelect = true; OwnerDraw = true; DoubleBuffered = true; BorderStyle = BorderStyle.None; HeaderStyle = ColumnHeaderStyle.Nonclickable;
            BackColor = t.Card; ForeColor = t.Text; Font = Ui.Body; Dock = DockStyle.Fill; HideSelection = false; MultiSelect = false;
            SmallImageList = new ImageList { ImageSize = new Size(1, Ui.D(26)) };   // row height
            foreach (var c in cols) { var p = c.Split(':'); Columns.Add(p[0], Ui.D(int.Parse(p[1]))); }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.DarkScroll(this, T.Dark); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); FitLast(); }
        public void FitLast()
        {
            if (Columns.Count == 0 || !IsHandleCreated) return; int used = 0; for (int i = 0; i < Columns.Count - 1; i++) used += Columns[i].Width;
            int w = ClientSize.Width - used; if (w > Ui.D(90) && Columns[Columns.Count - 1].Width != w) Columns[Columns.Count - 1].Width = w;
        }
        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(T.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Ui.CardTitle, new Rectangle(e.Bounds.X + Ui.D(8), e.Bounds.Y, e.Bounds.Width - Ui.D(10), e.Bounds.Height), T.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using (var p = new Pen(T.CardBorder)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }
        protected override void OnDrawItem(DrawListViewItemEventArgs e) { }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics; var bg = e.Item.Selected ? T.Selection : e.ItemIndex % 2 == 1 ? T.Stripe : T.Card;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
            var text = e.SubItem.Text ?? ""; var r = new Rectangle(e.Bounds.X + Ui.D(8), e.Bounds.Y, Math.Max(0, e.Bounds.Width - Ui.D(12)), e.Bounds.Height);
            if (e.ColumnIndex == PillColumn && text != "" && e.Item.Tag is Color)
            {
                var sz = Ui.PillSize(text); Ui.Pill(g, new Rectangle(r.X, r.Y + (r.Height - sz.Height) / 2, Math.Min(sz.Width, r.Width), sz.Height), text, (Color)e.Item.Tag, T); return;
            }
            TextRenderer.DrawText(g, text, e.Item.Font ?? Ui.Body, r, e.Item.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    // ---------------------------------------------------------------- tray menu styling
    class MenuColors : ProfessionalColorTable
    {
        readonly Theme T; public MenuColors(Theme t) { T = t; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return T.Card; } }
        public override Color ImageMarginGradientBegin { get { return T.Card; } }
        public override Color ImageMarginGradientMiddle { get { return T.Card; } }
        public override Color ImageMarginGradientEnd { get { return T.Card; } }
        public override Color MenuBorder { get { return T.CardBorder; } }
        public override Color MenuItemBorder { get { return T.Hover; } }
        public override Color MenuItemSelected { get { return T.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return T.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return T.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return T.Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return T.Hover; } }
        public override Color SeparatorDark { get { return T.CardBorder; } }
        public override Color SeparatorLight { get { return T.Card; } }
        public override Color CheckBackground { get { return T.Card; } }
        public override Color CheckSelectedBackground { get { return T.Hover; } }
        public override Color CheckPressedBackground { get { return T.Hover; } }
    }
    class MenuRenderer : ToolStripProfessionalRenderer
    {
        readonly Theme T;
        public MenuRenderer(Theme t) : base(new MenuColors(t)) { T = t; RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? T.Text : T.Faint; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = T.Sub; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (Ui.Icon != null) TextRenderer.DrawText(e.Graphics, "", Ui.Icon, e.ImageRectangle, T.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            else base.OnRenderItemCheck(e);
        }
    }

    // ---------------------------------------------------------------- the window
    class StatusForm : Form
    {
        readonly Watchdog wd; readonly TrayApp app; readonly Theme T;
        readonly HeaderBanner header; readonly MetricCard mRouter, mNet, mLoss, mSignal, mLink, mTraffic; readonly PingGraphX graph;
        readonly InfoCard iConn, iSys, iGames; readonly ActionButton bPause; readonly TabStrip tabs;
        readonly NiceList events, channels, games, sessions, drivers; readonly RichTextBox results; readonly Control[] pages;
        int shownLogVersion = -1; DateTime lastLists;

        public StatusForm(Watchdog w, TrayApp a)
        {
            wd = w; app = a; Ui.InitFonts(); T = Theme.Current();
            using (var g = CreateGraphics()) Ui.S = g.DpiX / 96f;
            Text = "AX210_WatchClaude"; Icon = Brand.AppIcon();
            BackColor = T.Bg; ForeColor = T.Text; Font = Ui.Body; AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;

            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(Ui.D(8)), BackColor = T.Bg };
            foreach (var rs in new[] { new RowStyle(SizeType.Absolute, RowHeader), new RowStyle(SizeType.Absolute, RowTiles), new RowStyle(SizeType.Absolute, RowGraph),
                                       new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.Absolute, RowTabs), new RowStyle(SizeType.Percent, 100) })
                root.RowStyles.Add(rs);

            header = new HeaderBanner(T) { Dock = DockStyle.Fill };
            var metrics = Grid(6, false);
            mRouter = Tile(metrics, 0, "Router ping"); mNet = Tile(metrics, 1, "Internet ping"); mLoss = Tile(metrics, 2, "Packet loss · 60 s");
            mSignal = Tile(metrics, 3, "Signal"); mLink = Tile(metrics, 4, "Link rate"); mTraffic = Tile(metrics, 5, "Traffic now");
            graph = new PingGraphX(T, wd) { Dock = DockStyle.Fill };
            var info = Grid(3, true);
            iConn = new InfoCard(T, "Connection") { Dock = DockStyle.Fill }; iSys = new InfoCard(T, "System") { Dock = DockStyle.Fill }; iGames = new InfoCard(T, "Games") { Dock = DockStyle.Fill };
            info.Controls.Add(iConn, 0, 0); info.Controls.Add(iSys, 1, 0); info.Controls.Add(iGames, 2, 0);

            bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = T.Bg, WrapContents = true, Margin = new Padding(0, Ui.D(2), 0, Ui.D(1)) };
            Btn(bar, "", "Network checkup", true, () => app.Checkup());
            Btn(bar, "", "Latency test", false, () => app.Latency());
            Btn(bar, "", "Speed test", false, () => app.Speed());
            Btn(bar, "", "Band test", false, () => app.BandTest());
            Btn(bar, "", "Stability test", false, () => app.Stability());
            Btn(bar, "", "DNS benchmark", false, () => app.Dns());
            Btn(bar, "", "Open logs", false, () => app.OpenLogs());
            bPause = Btn(bar, "", "Pause 1 h", false, () => app.TogglePause());
            Btn(bar, "", "Settings", false, () => app.ShowSettings());

            tabs = new TabStrip(T, new[] { "Events", "Channels", "Games", "Game sessions", "Drivers", "Test results" }) { Dock = DockStyle.Fill };
            var content = new CardPanel(T) { Dock = DockStyle.Fill };
            events = new NiceList(T, new[] { "Time:108", "Type:104", "What happened:640" }) { PillColumn = 1 };
            channels = new NiceList(T, new[] { "Band / channel:170", "Hours here:90", "Crashes (live):105", "Crashes (history):125", "Associations (history):155", "Stalls / hour:100", "Status:200" }) { PillColumn = 6 };
            games = new NiceList(T, new[] { "Game:240", "Executable:280", "Last played:150", "Wi-Fi traffic priority:180" }) { PillColumn = 3 };
            sessions = new NiceList(T, new[] { "Started:96", "Game:140", "Result:90", "Min:48", "Router med / p95 / max:160", "Internet med / p95:130", "Loss %:64", "Stalls:60", "Crashes:68", "Where:220" }) { PillColumn = 2 };
            drivers = new NiceList(T, new[] { "Intel driver:190", "Hours connected:130", "Crashes:90", "Crashes / hour:110", "First seen:120", "Saved copy for rollback:190" }) { PillColumn = 5 };
            results = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = T.Card, ForeColor = T.Text, Font = Ui.Mono, WordWrap = false, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Both };
            results.HandleCreated += (s, e) => Ui.DarkScroll(results, T.Dark);
            pages = new Control[] { events, channels, games, sessions, drivers, results };
            foreach (var p in pages) { p.Visible = p == events; content.Controls.Add(p); }
            tabs.Changed += i => { for (int k = 0; k < pages.Length; k++) pages[k].Visible = k == i; var nl = pages[i] as NiceList; if (nl != null) nl.FitLast(); };

            root.Controls.Add(header, 0, 0); root.Controls.Add(metrics, 0, 1); root.Controls.Add(graph, 0, 2); root.Controls.Add(info, 0, 3);
            root.Controls.Add(bar, 0, 4); root.Controls.Add(tabs, 0, 5); root.Controls.Add(content, 0, 6);
            Controls.Add(root);

            var timer = new Timer { Interval = 2000 }; timer.Tick += (s, e) => { if (Visible && WindowState != FormWindowState.Minimized) RefreshView(); }; timer.Start();
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
            Shown += (s, e) => RefreshView();
            RefreshView(); FitToContent();
        }

        readonly TableLayoutPanel root; readonly FlowLayoutPanel bar;
        static int RowHeader { get { return Ui.D(64); } }
        static int RowTiles { get { return Ui.D(84); } }
        static int RowGraph { get { return Ui.D(150); } }
        static int RowTabs { get { return Ui.D(36); } }

        /// Sizes the window to its contents: wide enough for the button row and the six tiles, tall enough to show every
        /// info row plus a useful slice of the tables, and never bigger than the screen it opens on.
        void FitToContent()
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int pad = root.Padding.Horizontal, btnW = pad + Ui.D(6);
            foreach (Control b in bar.Controls) btnW += b.Width + b.Margin.Horizontal;
            int w = Math.Min(wa.Width - Ui.D(16), Math.Max(Ui.D(1060), btnW));
            int colW = (w - pad) / 3 - Ui.D(8);
            int infoH = new[] { iConn, iSys, iGames }.Max(c => c.GetPreferredSize(new Size(colW, 0)).Height) + Ui.D(8);
            int barH = bar.GetPreferredSize(new Size(w - pad, 0)).Height + bar.Margin.Vertical;
            int h = root.Padding.Vertical + RowHeader + RowTiles + RowGraph + infoH + barH + RowTabs + Ui.D(250);
            ClientSize = new Size(w, Math.Min(h, wa.Height - Ui.D(16) - (Height - ClientSize.Height)));
            MinimumSize = new Size(Math.Min(Ui.D(940), wa.Width), Math.Min(Ui.D(620), wa.Height));
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + Math.Max(0, (wa.Height - Height) / 2));
        }

        TableLayoutPanel Grid(int cols, bool autoRow)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = cols, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0), BackColor = T.Bg, AutoSize = autoRow };
            for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            t.RowStyles.Add(autoRow ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Percent, 100));
            return t;
        }
        MetricCard Tile(TableLayoutPanel grid, int col, string title) { var m = new MetricCard(T, title) { Dock = DockStyle.Fill }; grid.Controls.Add(m, col, 0); return m; }
        ActionButton Btn(FlowLayoutPanel bar, string glyph, string label, bool primary, Action click) { var b = new ActionButton(T, glyph, label, primary); b.Click += (s, e) => { if (app != null) click(); }; bar.Controls.Add(b); return b; }
        static InfoCard.Row R(string k, string v, Color? c = null, bool bold = false) { return new InfoCard.Row { K = k, V = v, C = c, Bold = bold }; }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ui.DarkTitle(Handle, T.Dark); }

        Tuple<string, Color> WatchdogPill()
        {
            if (!wd.IsAdmin) return Tuple.Create("Monitor only", T.Warn);
            if (!wd.Cfg.AutoActions) return Tuple.Create("Auto actions off", T.Warn);
            if (wd.Paused) return Tuple.Create("Paused until " + wd.PausedUntil.ToString("HH:mm"), T.Warn);
            if (!string.IsNullOrEmpty(wd.Busy)) return Tuple.Create(wd.Busy.Length > 42 ? wd.Busy.Substring(0, 42) + "…" : wd.Busy, T.Accent);
            return Tuple.Create("Watching", T.Good);
        }
        string WatchdogText()
        {
            return !wd.IsAdmin ? "monitor only (not elevated)" : !wd.Cfg.AutoActions ? "monitor only (auto actions off)" : wd.Paused ? "paused until " + wd.PausedUntil.ToString("HH:mm") : wd.Busy != "" ? "busy: " + wd.Busy : "watching";
        }

        public void RefreshView()
        {
            var c = wd.Conn; var p = wd.P60; var p10 = wd.Pinger.Stats(600); bool up = c.Connected;
            // banner
            string h = wd.Health ?? ""; int ix = h.IndexOf(" — "); string ht = ix >= 0 ? h.Substring(0, ix) : h, hs = ix >= 0 ? h.Substring(ix + 3) : "";
            if (ht.Length > 0) ht = char.ToUpper(ht[0]) + ht.Substring(1);
            var pills = new List<Tuple<string, Color>>();
            if (wd.InGame) { var s0 = wd.Session; pills.Add(Tuple.Create(wd.CurrentGame + (s0 != null ? " · " + (int)(DateTime.Now - s0.Start).TotalMinutes + " min" : ""), T.Purple)); }
            if (wd.St.TurboActive) pills.Add(Tuple.Create("Fast-download mode", T.Accent));
            if (wd.St.WifiDirectDisabled.Count > 0) pills.Add(Tuple.Create("Wi-Fi Direct paused", T.Sub));
            if (wd.WiredName != null) pills.Add(Tuple.Create("Ethernet in use", T.Sub));
            if (wd.Metered) pills.Add(Tuple.Create("Metered", T.Warn));
            var upd = wd.AvailableUpdate; if (upd != null) pills.Add(Tuple.Create("Update " + upd + " available", T.Accent));
            pills.Add(WatchdogPill());
            header.Set(wd.Level, ht, hs, pills);
            // tiles
            mRouter.Set(up && p.GwN > 0 ? p.GwP50.ToString("0") : "—", "ms", string.Format("p95 {0:0} · max {1:0} ms", p.GwP95, p.GwMax),
                !up ? T.Bad : p.GwP95 < 20 && p10.Stalls < 3 ? T.Good : p.GwP95 < 50 ? T.Warn : T.Bad);
            mNet.Set(p.NetN > 0 ? p.NetP50.ToString("0") : "—", "ms", string.Format("p95 {0:0} · max {1:0} ms", p.NetP95, p.NetMax), p.NetN == 0 ? T.Faint : p.NetP95 < 60 ? T.Good : p.NetP95 < 120 ? T.Warn : T.Bad);
            double loss = Math.Max(p.GwLossPct, p.NetLossPct);
            mLoss.Set(loss.ToString("0.#"), "%", string.Format("router {0:0.#}% · internet {1:0.#}%", p.GwLossPct, p.NetLossPct), loss == 0 ? T.Good : loss < 2 ? T.Warn : T.Bad);
            int bars = !up ? 0 : c.Rssi >= -55 ? 4 : c.Rssi >= -65 ? 3 : c.Rssi >= -72 ? 2 : 1;
            mSignal.Set(up ? c.Rssi.ToString() : "—", up ? "dBm" : "", up ? string.Format("{0}% quality · {1} GHz ch {2}", c.Quality, c.Band, c.Channel) : "not connected", !up ? T.Bad : bars >= 3 ? T.Good : bars == 2 ? T.Warn : T.Bad, bars);
            mLink.Set(up ? c.RxMbps.ToString() : "—", up ? "Mbps" : "", up ? string.Format("↓ {0} receive · ↑ {1} send", c.RxMbps, c.TxMbps) : "", up ? T.Accent : T.Faint);
            mTraffic.Set(wd.RxMbps < 10 ? wd.RxMbps.ToString("0.0") : wd.RxMbps.ToString("0"), "Mbps ↓", string.Format("↑ {0:0.0} Mbps up{1}", wd.TxMbps, wd.St.TurboActive ? " · fast-download mode (6 GHz)" : ""), T.Accent);
            graph.Invalidate();
            // cards
            bool badCh = up && c.Band == "5" && wd.Cfg.AvoidChannels5.Contains(c.Channel), onExt = up && wd.IsAvoidBssid(c.Bssid);
            string ll = wd.LowLatencyState ?? "";
            iConn.SetRows(new List<InfoCard.Row> {
                R("Network", (up ? c.Ssid + (wd.IsHome(c.Ssid) || string.IsNullOrEmpty(wd.Cfg.HomeSsid) ? "" : "  ·  not a home network (watching only)") : "—") + (wd.Metered ? "  ·  metered" : "") + (wd.WiredName != null ? "  ·  Ethernet in use" : "")),
                R("Access point", up ? wd.ApName(c.Bssid) + "  ·  " + c.Bssid : "—", onExt ? (Color?)T.Bad : null),
                R("Band / channel", up ? c.Band + " GHz · channel " + c.Channel : "—", badCh ? (Color?)T.Bad : null),
                R("Band mode", !Adapter.CanLockBands ? "not supported by this adapter's driver" : Profiles.Name(wd.St.CurrentMode) + (wd.St.CurrentMode == "6b" ? " (5 GHz allowed)" : "") + "  ·  preferred " + wd.PreferredBand() + " GHz"),
                R("Band order", string.Join("  ›  ", wd.UsableBands.Select(b => b + " GHz"))),
                R("Avoiding", wd.AvoidSummary()),
                R("Low-latency mode", ll == "" ? (wd.IsAdmin ? "pending" : "needs admin") : ll, ll.Contains("ON") || ll.Contains("OFF") ? (Color?)T.Warn : null) });
            int c24 = wd.CrashCount(24), c7 = wd.CrashCount(168);
            iSys.SetRows(new List<InfoCard.Row> {
                R(Adapter.IsIntel ? "Intel driver" : "Wi-Fi driver", wd.DriverVersion),
                R("Driver crashes", c24 + " in 24 h  ·  " + c7 + " in 7 days", c24 > 0 ? (Color?)T.Bad : c7 > 0 ? (Color?)T.Warn : null),
                R("DNS", string.Join("  ·  ", DnsTools.Current().OrderBy(x => x.Contains(":") ? 1 : 0).Take(4)) + (wd.St.DnsChoice != "" ? "\n" + wd.St.DnsChoice : "")),
                R("UDP ports in use", wd.UdpInUse + (wd.UdpTop != "" ? "  ·  " + wd.UdpTop : ""), wd.UdpInUse >= wd.Cfg.UdpWarnThreshold ? (Color?)T.Warn : null),
                R("Watchdog", WatchdogText()) });
            int seen, pri; lock (wd.StLock) { seen = wd.St.Games.Count; pri = wd.St.Games.Values.Count(g => g.Prioritized); }
            var ses = wd.Session; string st = "—";
            if (ses != null) lock (ses) { var gl = ses.Gw.ToList(); gl.Sort(); st = string.Format("{0:0} min · router {1:0} / {2:0} ms · {3} stall{4}", (DateTime.Now - ses.Start).TotalMinutes, PingMonitor.Pct(gl, 50), PingMonitor.Pct(gl, 95), ses.Stalls, ses.Stalls == 1 ? "" : "s"); }
            SessionRec last; lock (wd.StLock) last = wd.St.Sessions.Count > 0 ? wd.St.Sessions[wd.St.Sessions.Count - 1] : null;
            string lastText = last == null ? "none yet" : string.Format("{0} · {1:0} min · {2} · {3}", last.Game, last.Minutes, last.Start.ToLocalTime().ToString("MMM d HH:mm"),
                last.Crashes == 0 && last.GwLossPct < 0.5 && last.NetLossPct < 1 && last.GwP95 < 20 ? "clean" : "had issues");
            iGames.SetRows(new List<InfoCard.Row> {
                R("Game", wd.InGame ? wd.CurrentGame + " — non-urgent changes wait" : "none running", wd.InGame ? (Color?)T.Purple : null, wd.InGame),
                R("Game session", st),
                R("Last session", lastText),
                R("Game folders", wd.Detector.RootCount + " watched"),
                R("Games seen", seen + (seen == 1 ? " game" : " games") + "  ·  " + pri + " with Wi-Fi priority") });
            bPause.SetLabel(wd.Paused ? "" : "", wd.Paused ? "Resume" : "Pause 1 h");
            RefreshLists();
        }

        static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string> {
            {"INCIDENT","Crash"}, {"ACTION","Action"}, {"CONN","Connection"}, {"GAME","Game"}, {"SESSION","Session"}, {"DRIVER","Driver"}, {"DNS","DNS"},
            {"UDP","Ports"}, {"ISP","Internet"}, {"TEST","Test"}, {"ERROR","Error"}, {"START","Start"}, {"INFO","Info"}, {"SUMMARY","Summary"} };
        Color TypeColor(string t)
        {
            switch (t)
            {
                case "INCIDENT": case "ERROR": return T.Bad;
                case "ACTION": case "SUMMARY": return T.Accent;
                case "GAME": case "SESSION": return T.Purple;
                case "DRIVER": case "DNS": case "UDP": case "ISP": return T.Warn;
                case "START": return T.Good;
                default: return T.Sub;
            }
        }

        void RefreshLists()
        {
            if (Log.Version != shownLogVersion)
            {
                shownLogVersion = Log.Version; events.BeginUpdate(); events.Items.Clear();
                foreach (var r in Log.Recent().Take(300))
                {
                    string tn; if (!TypeNames.TryGetValue(r[1], out tn)) tn = r[1];
                    var it = new ListViewItem(new[] { r[0], tn, Ui.Rd(r[2]) }) { Tag = TypeColor(r[1]), ForeColor = r[1] == "INCIDENT" || r[1] == "ERROR" ? T.Bad : T.Text };
                    events.Items.Add(it);
                }
                events.EndUpdate(); events.FitLast();
            }
            if ((DateTime.Now - lastLists).TotalSeconds < 10) return;
            lastLists = DateTime.Now; var curKey = wd.Conn.Connected ? wd.Conn.Key : "";

            channels.BeginUpdate(); channels.Items.Clear();
            lock (wd.StLock)
                foreach (var kv in wd.St.Channels.OrderBy(x => x.Key))
                {
                    var parts = kv.Key.Split('|'); int ch; int.TryParse(parts.Length > 1 ? parts[1] : "0", out ch);
                    bool avoided = parts[0] == "5" && wd.Cfg.AvoidChannels5.Contains(ch), quar = wd.IsQuarantined(kv.Key);
                    string status = avoided ? "Avoided (config)" : quar ? "Quarantined until " + wd.St.Quarantine[kv.Key].ToLocalTime().ToString("MM-dd HH:mm") : "OK";
                    double hrs = kv.Value.Seconds / 3600.0; bool now = kv.Key == curKey;
                    var it = new ListViewItem(new[] { (now ? "● " : "") + Watchdog.KeyLabel(kv.Key) + (now ? "  (now)" : ""), hrs.ToString("0.0"), kv.Value.Crashes.ToString(), kv.Value.HistCrashes.ToString(),
                        kv.Value.HistAssociations.ToString(), hrs > 0.05 ? (kv.Value.Stalls / hrs).ToString("0.0") : "—", status })
                    { Tag = avoided ? T.Bad : quar ? T.Warn : T.Good, ForeColor = now ? T.Accent : T.Text, Font = now ? Ui.BodyBold : Ui.Body };
                    channels.Items.Add(it);
                }
            channels.EndUpdate(); channels.FitLast();

            games.BeginUpdate(); games.Items.Clear();
            lock (wd.StLock)
                foreach (var g in wd.St.Games.Values.OrderByDescending(x => x.LastSeen))
                {
                    string pr = g.Prioritized ? "On (UDP)" : wd.Cfg.QosTagGame ? "Pending" : "Off";
                    games.Items.Add(new ListViewItem(new[] { g.Name, g.Exe, g.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), pr }) { Tag = g.Prioritized ? T.Good : wd.Cfg.QosTagGame ? T.Warn : T.Sub, ForeColor = T.Text });
                }
            games.EndUpdate(); games.FitLast();

            sessions.BeginUpdate(); sessions.Items.Clear();
            lock (wd.StLock)
                foreach (var s in wd.St.Sessions.AsEnumerable().Reverse().Take(100))
                {
                    bool clean = s.Crashes == 0 && s.GwLossPct < 0.5 && s.NetLossPct < 1 && s.GwP95 < 20;
                    sessions.Items.Add(new ListViewItem(new[] { s.Start.ToLocalTime().ToString("MM-dd HH:mm"), s.Game, clean ? "Clean" : "Issues", s.Minutes.ToString("0"),
                        string.Format("{0:0} / {1:0} / {2:0}", s.GwMed, s.GwP95, s.GwMax), string.Format("{0:0} / {1:0}", s.NetMed, s.NetP95), Math.Max(s.GwLossPct, s.NetLossPct).ToString("0.#"),
                        s.Stalls.ToString(), s.Crashes.ToString(), Ui.Rd(s.Where) }) { Tag = clean ? T.Good : T.Warn, ForeColor = T.Text });
                }
            sessions.EndUpdate(); sessions.FitLast();

            drivers.BeginUpdate(); drivers.Items.Clear(); var saved = wd.DriverBackups();
            lock (wd.StLock)
                foreach (var kv in wd.St.DriverStats.OrderByDescending(x => x.Value.FirstSeen))
                {
                    bool cur = kv.Key == wd.DriverVersion; string copy = cur ? (kv.Value.BackedUp ? "Saved" : "Saving…") : saved.Contains(kv.Key) ? "Saved" : "Not saved";
                    drivers.Items.Add(new ListViewItem(new[] { kv.Key + (cur ? "  (current)" : "") + (kv.Value.ViaWindowsUpdate ? "  · Windows Update" : ""), kv.Value.Hours.ToString("0.0"), kv.Value.Crashes.ToString(),
                        kv.Value.Hours > 0.5 ? (kv.Value.Crashes / kv.Value.Hours).ToString("0.00") : "—", kv.Value.FirstSeen.ToLocalTime().ToString("yyyy-MM-dd"), copy })
                    { Tag = copy == "Saved" ? T.Good : copy == "Saving…" ? T.Warn : T.Sub, ForeColor = cur ? T.Accent : T.Text, Font = cur ? Ui.BodyBold : Ui.Body });
                }
            drivers.EndUpdate(); drivers.FitLast();
        }

        public void SelectTabForPreview(int i) { lastLists = DateTime.MinValue; RefreshLists(); tabs.SelectTab(i); }
        void Add(string s, Color c, bool bold) { results.SelectionStart = results.TextLength; results.SelectionLength = 0; results.SelectionColor = c; results.SelectionFont = bold ? Ui.MonoBold : Ui.Mono; results.AppendText(s); }
        public void ShowResult(string title, string text)
        {
            int startAt = results.TextLength; text = Ui.Rd(text);
            Add(DateTime.Now.ToString("HH:mm:ss") + "  " + title.ToUpperInvariant() + "\n", T.Accent, true);
            bool first = true;
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                Color col = T.Text; bool bold = false;
                if (raw.StartsWith("[OK")) col = T.Good; else if (raw.StartsWith("[WARN")) col = T.Warn; else if (raw.StartsWith("[FAIL")) col = T.Bad; else if (raw.StartsWith("[INFO")) col = T.Sub;
                else if (first && raw.Trim() != "") bold = true;
                if (raw.Trim() != "") first = false;
                Add(raw + "\n", col, bold);
            }
            Add("\n", T.Text, false);
            tabs.SelectTab(5);
            results.SelectionStart = results.TextLength; results.ScrollToCaret();   // go to the end first…
            results.SelectionStart = startAt; results.SelectionLength = 0; results.ScrollToCaret();   // …then bring the new result's header into view at the top
        }
    }
}
