// Build-time helper: renders the icon and installer artwork from src/Brand.cs.
// Usage: make-assets.exe <assets dir> <docs dir>
using System;
using System.IO;

namespace AX210WatchClaude
{
    static class MakeAssets
    {
        static void Main(string[] args)
        {
            string assets = args[0], docs = args[1];
            Directory.CreateDirectory(assets); Directory.CreateDirectory(docs);
            Brand.WriteIco(Path.Combine(assets, "app.ico"), new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
            Brand.WritePng(Path.Combine(docs, "logo.png"), 256);
            Brand.WritePng(Path.Combine(docs, "logo-128.png"), 128);
            // Inno Setup wizard art at 100% / 150% / 200% DPI
            foreach (var s in new[] { new[] { 164, 314 }, new[] { 246, 471 }, new[] { 328, 628 } })
                using (var b = Brand.WizardPanel(s[0], s[1])) Brand.WriteBmp32(Path.Combine(assets, string.Format("wizard-{0}x{1}.bmp", s[0], s[1])), b);
            foreach (var s in new[] { 55, 83, 110 })
                using (var b = Brand.Render(s, null)) Brand.WriteBmp32(Path.Combine(assets, string.Format("wizard-small-{0}.bmp", s)), b);
            Console.WriteLine("assets written");
        }
    }
}
