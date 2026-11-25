// AX210_WatchClaude — brand mark (app icon, tray icons, installer art). Shared by the app and tools/MakeAssets.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace AX210WatchClaude
{
    public static class Brand
    {
        public static readonly Color GradA = Color.FromArgb(59, 130, 246), GradB = Color.FromArgb(124, 77, 255);

        public static GraphicsPath Squircle(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Max(1f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// The mark: a rounded square (brand gradient, or a solid status colour) with a white Wi-Fi symbol.
        public static Bitmap Render(int size, Color? solid)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
                bool tiny = size <= 24;
                float inset = tiny ? 0f : size * 0.03f;
                var r = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                using (var p = Squircle(r, r.Width * 0.235f))
                {
                    if (solid.HasValue) using (var b = new SolidBrush(solid.Value)) g.FillPath(b, p);
                    else using (var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), GradA, GradB, 45f)) g.FillPath(b, p);
                    if (!tiny)
                    {
                        var st = g.Save(); g.SetClip(p);
                        var hr = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.5f);
                        using (var hb = new LinearGradientBrush(new RectangleF(hr.X, hr.Y - 1, hr.Width, hr.Height + 2), Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f)) g.FillRectangle(hb, hr);
                        g.Restore(st);
                    }
                }
                float cx = size * 0.5f, cy = size * (tiny ? 0.71f : 0.685f);
                float[] radii = tiny ? new[] { 0.37f, 0.215f } : new[] { 0.385f, 0.268f, 0.152f };
                float pw = tiny ? Math.Max(1.7f, size * 0.125f) : size * 0.074f;
                using (var pen = new Pen(Color.White, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    foreach (var k in radii) { float R = size * k; g.DrawArc(pen, cx - R, cy - R, 2 * R, 2 * R, 221, 98); }
                float dot = tiny ? Math.Max(2.4f, size * 0.17f) : size * 0.115f;
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, cx - dot / 2, cy - dot / 2, dot, dot);
            }
            return bmp;
        }

        /// Multi-size app icon from the embedded resource (falls back to drawing it).
        public static Icon AppIcon()
        {
            try { var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AX210WatchClaude.app.ico"); if (s != null) using (s) return new Icon(s); } catch { }
            using (var b = Render(32, null)) return Icon.FromHandle(b.GetHicon());
        }
        public static Icon StatusIcon(int size, Color? c) { using (var b = Render(size, c)) return Icon.FromHandle(b.GetHicon()); }

        // ---------- asset writers (used at build time)
        static byte[] Dib(Bitmap b)
        {
            int s = b.Width; var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--) for (int x = 0; x < s; x++) { var c = b.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
            int maskRow = ((s + 31) / 32) * 4; w.Write(new byte[maskRow * s]);
            return ms.ToArray();
        }
        public static void WriteIco(string path, int[] sizes)
        {
            var data = new List<byte[]>();
            foreach (var s in sizes)
                using (var b = Render(s, null))
                {
                    if (s >= 256) { using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); data.Add(ms.ToArray()); } }
                    else data.Add(Dib(b));
                }
            using (var fs = new FileStream(path, FileMode.Create)) using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i]; w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32); w.Write(data[i].Length); w.Write(offset); offset += data[i].Length;
                }
                foreach (var d in data) w.Write(d);
            }
        }
        public static void WritePng(string path, int size) { using (var b = Render(size, null)) b.Save(path, ImageFormat.Png); }

        /// 32-bit BMP with real alpha (Inno Setup: WizardImageAlphaFormat=defined).
        public static void WriteBmp32(string path, Bitmap src)
        {
            int w = src.Width, h = src.Height, rowBytes = w * 4, size = rowBytes * h;
            using (var fs = new FileStream(path, FileMode.Create)) using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(14 + 40 + size); bw.Write(0); bw.Write(14 + 40);
                bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(size); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
                for (int y = h - 1; y >= 0; y--) for (int x = 0; x < w; x++) { var c = src.GetPixel(x, y); bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A); }
            }
        }
        /// Installer side panel: brand gradient, the mark, and the name.
        public static Bitmap WizardPanel(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new LinearGradientBrush(new Rectangle(0, -1, w, h + 2), GradA, GradB, 70f)) g.FillRectangle(b, 0, 0, w, h);
                int m = (int)(w * 0.52); int mx = (w - m) / 2, my = (int)(h * 0.26);
                using (var mark = Render(m, Color.FromArgb(40, 255, 255, 255))) g.DrawImage(mark, mx, my, m, m);
                using (var f = new Font("Segoe UI Semibold", w * 0.085f, GraphicsUnit.Pixel)) using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                {
                    g.DrawString("AX210", f, Brushes.White, new RectangleF(0, my + m + h * 0.05f, w, f.Height * 1.3f), sf);
                    using (var f2 = new Font("Segoe UI", w * 0.07f, GraphicsUnit.Pixel)) using (var wb = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                        g.DrawString("WatchClaude", f2, wb, new RectangleF(0, my + m + h * 0.05f + f.Height * 1.1f, w, f2.Height * 1.3f), sf);
                }
            }
            return bmp;
        }
    }
}
