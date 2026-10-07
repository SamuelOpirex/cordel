using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Cordel
{
    static class T
    {
        static readonly bool es = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
        public static string L(string en, string spanish) { return es ? spanish : en; }
    }

    static class Paths
    {
        public static readonly string Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cordel");
        /// Images copied to the clipboard land here. They are disposable: taking
        /// one down sends it to the Recycle Bin. It lives in Pictures, not in
        /// AppData, because Store apps like the new Paint cannot open files there.
        public static readonly string Inbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Cordel");

        /// Earlier versions kept clipboard captures in AppData. Move them to the
        /// new folder and keep them on the line.
        public static void MigrateInbox()
        {
            var old = Path.Combine(Data, "Portapapeles");
            if (!Directory.Exists(old)) return;
            try
            {
                Directory.CreateDirectory(Inbox);
                var pegged = Settings.Pegged;
                foreach (var f in Directory.GetFiles(old))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var ext = Path.GetExtension(f);
                    var target = Path.Combine(Inbox, name + ext);
                    for (int n = 2; File.Exists(target); n++) target = Path.Combine(Inbox, name + " (" + n + ")" + ext);
                    File.Move(f, target);
                    for (int i = 0; i < pegged.Count; i++)
                        if (Same(pegged[i], f)) pegged[i] = target;
                }
                Settings.Pegged = pegged;
                Directory.Delete(old);
                Log.Write("clipboard captures moved to " + Inbox);
            }
            catch (Exception e) { Log.Write("migrate: " + e.Message); }
        }

        /// Where Windows saves Win+PrtScn and the Snipping Tool's automatic copies.
        public static string Screenshots
        {
            get
            {
                var p = Native.KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"));
                if (string.IsNullOrEmpty(p))
                    p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static bool Same(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public static bool InInbox(string p)
        {
            return Path.GetFullPath(p).StartsWith(Inbox + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    static class Log
    {
        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                var f = Path.Combine(Paths.Data, "cordel.log");
                if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    /// Small key=value file. The pegged photos live in their own list.
    static class Settings
    {
        static readonly string file = Path.Combine(Paths.Data, "settings.ini");
        static readonly string peggedFile = Path.Combine(Paths.Data, "pegged.txt");
        static Dictionary<string, string> values;

        static Dictionary<string, string> Values
        {
            get
            {
                if (values != null) return values;
                values = new Dictionary<string, string>();
                try
                {
                    foreach (var line in File.ReadAllLines(file))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1);
                    }
                }
                catch { }
                return values;
            }
        }

        public static bool Get(string key, bool def = false)
        {
            string v;
            return Values.TryGetValue(key, out v) ? v == "1" : def;
        }

        public static void Set(string key, bool on)
        {
            Values[key] = on ? "1" : "0";
            try
            {
                Directory.CreateDirectory(Paths.Data);
                File.WriteAllLines(file, Values.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch (Exception e) { Log.Write("settings: " + e.Message); }
        }

        public static List<string> Pegged
        {
            get { try { return File.ReadAllLines(peggedFile).Where(l => l.Length > 0).ToList(); } catch { return new List<string>(); } }
            set { try { Directory.CreateDirectory(Paths.Data); File.WriteAllLines(peggedFile, value); } catch { } }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Cordel") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("Cordel", "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"");
                    else k.DeleteValue("Cordel", false);
                }
            }
        }

        public static bool DarkApps
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Equals(k.GetValue("AppsUseLightTheme"), 0);
            }
        }

        public static bool DarkTaskbar
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k == null || !Equals(k.GetValue("SystemUsesLightTheme"), 1);
            }
        }
    }

    static class Imaging
    {
        /// Reads the whole file into memory first, so the image is never kept
        /// locked and can be moved, edited or deleted while it hangs.
        public static BitmapSource Load(string path, int maxPixels = 0)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.StreamSource = new MemoryStream(bytes);
                if (maxPixels > 0)
                {
                    var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (frame.PixelWidth >= frame.PixelHeight) bi.DecodePixelWidth = Math.Min(frame.PixelWidth, maxPixels);
                    else bi.DecodePixelHeight = Math.Min(frame.PixelHeight, maxPixels);
                }
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        public static byte[] Png(BitmapSource src)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        public static byte[] Fingerprint(BitmapSource src)
        {
            try
            {
                var small = new TransformedBitmap(src, new ScaleTransform(32.0 / src.PixelWidth, 32.0 / src.PixelHeight));
                var gray = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
                var px = new byte[gray.PixelWidth * gray.PixelHeight];
                gray.CopyPixels(px, gray.PixelWidth, 0);
                return px;
            }
            catch { return null; }
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            long sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return sum <= a.Length / 2;
        }

        public static System.Drawing.Bitmap ToGdi(BitmapSource src, int max)
        {
            double s = Math.Min(1, (double)max / Math.Max(src.PixelWidth, src.PixelHeight));
            var scaled = new TransformedBitmap(src, new ScaleTransform(s, s));
            using (var ms = new MemoryStream(Png(scaled)))
                return new System.Drawing.Bitmap(ms);
        }
    }

    /// Tiny synthesized sounds: a light tink when a photo hangs, a soft pop
    /// when it comes down, a rustle for the Recycle Bin.
    static class Sound
    {
        public static bool On = true;
        static byte[] tink, pop, rustle;

        static byte[] Wav(double seconds, Func<double, double> f)
        {
            const int rate = 44100;
            int n = (int)(rate * seconds);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double fadeIn = Math.Min(1, t / 0.002);
                    w.Write((short)(Math.Max(-1, Math.Min(1, f(t) * fadeIn)) * 32767));
                }
                return ms.ToArray();
            }
        }

        static void Play(ref byte[] cache, Func<byte[]> make)
        {
            if (!On) return;
            if (cache == null) cache = make();
            try { new SoundPlayer(new MemoryStream(cache)).Play(); } catch { }
        }

        public static void Tink()
        {
            Play(ref tink, () => Wav(0.18, t => 0.22 * Math.Exp(-t * 32) *
                (Math.Sin(2 * Math.PI * 2093 * t) + 0.35 * Math.Sin(2 * Math.PI * 3140 * t))));
        }

        public static void Pop()
        {
            Play(ref pop, () => Wav(0.12, t => 0.32 * Math.Exp(-t * 40) *
                Math.Sin(2 * Math.PI * (620 * t - 1500 * t * t))));
        }

        public static void Rustle()
        {
            var rnd = new Random(7);
            double last = 0;
            Play(ref rustle, () => Wav(0.28, t =>
            {
                last = last * 0.6 + (rnd.NextDouble() * 2 - 1) * 0.4;
                return 0.35 * last * Math.Exp(-t * 14) * (0.6 + 0.4 * Math.Sin(t * 90));
            }));
        }
    }

    /// Notices new screenshots in a folder. Cordel never takes screenshots:
    /// keep Win+Shift+S, Win+PrtScn or any tool that saves there.
    sealed class ScreenshotWatcher : IDisposable
    {
        static readonly HashSet<string> exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

        public readonly string Folder;
        readonly Action<string> onNew;
        readonly Action onChange;
        readonly FileSystemWatcher fsw;
        readonly DispatcherTimer debounce;
        HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int retries;

        public ScreenshotWatcher(string folder, Action<string> onNew, Action onChange)
        {
            Folder = folder;
            this.onNew = onNew;
            this.onChange = onChange;
            known = new HashSet<string>(Listing(), StringComparer.OrdinalIgnoreCase);
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounce.Tick += (s, e) => { debounce.Stop(); Scan(); };
            fsw = new FileSystemWatcher(folder) { IncludeSubdirectories = false, InternalBufferSize = 64 * 1024 };
            FileSystemEventHandler h = (s, e) => Kick();
            fsw.Created += h; fsw.Deleted += h; fsw.Changed += h;
            fsw.Renamed += (s, e) => Kick();
            fsw.EnableRaisingEvents = true;
        }

        void Kick()
        {
            var d = System.Windows.Application.Current == null ? null : System.Windows.Application.Current.Dispatcher;
            if (d != null) d.BeginInvoke(new Action(() => { debounce.Stop(); debounce.Start(); }));
        }

        IEnumerable<string> Listing()
        {
            try
            {
                return new DirectoryInfo(Folder).GetFiles()
                    .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .OrderBy(f => f.CreationTimeUtc).Select(f => f.FullName).ToList();
            }
            catch { return new string[0]; }
        }

        void Scan()
        {
            var files = Listing().ToList();
            bool busy = false;
            foreach (var f in files)
            {
                if (known.Contains(f) || !exts.Contains(Path.GetExtension(f))) continue;
                // The writer may still hold the file. Try again shortly.
                if (!Readable(f)) { busy = true; continue; }
                known.Add(f);
                onNew(f);
            }
            known.IntersectWith(files);
            if (busy && retries++ < 20) debounce.Start(); else retries = 0;
            onChange();
        }

        static bool Readable(string f)
        {
            try
            {
                using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return s.Length > 0;
            }
            catch { return false; }
        }

        public void Dispose() { fsw.Dispose(); debounce.Stop(); }
    }

    /// The icon, drawn in code: a line with two photos pegged to it.
    static class Art
    {
        static byte[] Render(int size, bool tray, bool lightInk)
        {
            var dv = new DrawingVisual();
            using (var g = dv.RenderOpen())
            {
                double s = size / 64.0;
                g.PushTransform(new ScaleTransform(s, s));
                Color ink;
                if (!tray)
                {
                    var bg = new LinearGradientBrush(Color.FromRgb(0x2F, 0x80, 0xED), Color.FromRgb(0x1B, 0x3F, 0xA8), 90);
                    g.DrawRoundedRectangle(bg, null, new Rect(2, 2, 60, 60), 14, 14);
                    ink = Colors.White;
                }
                else ink = lightInk ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20);

                var rope = new StreamGeometry();
                using (var c = rope.Open())
                {
                    c.BeginFigure(new Point(tray ? 1 : 6, tray ? 12 : 17), false, false);
                    c.QuadraticBezierTo(new Point(32, tray ? 24 : 27), new Point(tray ? 63 : 58, tray ? 12 : 17), true, true);
                }
                g.DrawGeometry(null, new Pen(new SolidColorBrush(ink), tray ? 3.2 : 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, rope);

                Action<double, double, double, double, double> card = (cx, top, w, h, angle) =>
                {
                    g.PushTransform(new RotateTransform(angle, cx, top));
                    var frame = new Rect(cx - w / 2, top + 4, w, h);
                    if (tray)
                    {
                        g.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(ink), 3.6), frame, 3.5, 3.5);
                    }
                    else
                    {
                        g.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
                            new Rect(frame.X + 0.8, frame.Y + 1.6, w, h), 4, 4);
                        g.DrawRoundedRectangle(Brushes.White, null, frame, 4, 4);
                        var photo = new Rect(frame.X + 2.5, frame.Y + 2.5, w - 5, h - 5);
                        g.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0x9F, 0xD3, 0xFF), Color.FromRgb(0x5C, 0x9E, 0xF0), 90), null, photo, 2, 2);
                        var hill = new StreamGeometry();
                        using (var c = hill.Open())
                        {
                            c.BeginFigure(new Point(photo.Left, photo.Bottom), true, true);
                            c.LineTo(new Point(photo.Left + photo.Width * 0.4, photo.Top + photo.Height * 0.45), true, false);
                            c.LineTo(new Point(photo.Left + photo.Width * 0.65, photo.Top + photo.Height * 0.7), true, false);
                            c.LineTo(new Point(photo.Right, photo.Top + photo.Height * 0.5), true, false);
                            c.LineTo(new Point(photo.Right, photo.Bottom), true, false);
                        }
                        g.PushClip(new RectangleGeometry(photo, 2, 2));
                        g.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0xC9)), null, hill);
                        g.Pop();
                    }
                    var pin = new Rect(cx - (tray ? 3 : 2.2), top - 3, tray ? 6 : 4.4, tray ? 11 : 10);
                    g.DrawRoundedRectangle(tray ? new SolidColorBrush(ink) : new LinearGradientBrush(Color.FromRgb(0xE6, 0xE9, 0xEE), Color.FromRgb(0xA4, 0xAB, 0xB6), 0),
                        null, pin, 1.6, 1.6);
                    g.Pop();
                };
                if (tray) card(32, 21, 34, 28, -4);
                else { card(22, 21, 20, 25, -5); card(43, 22, 18, 22, 4); }
                g.Pop();
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return Imaging.Png(rtb);
        }

        /// A PNG-compressed .ico with several sizes.
        public static byte[] Ico(bool tray, bool lightInk, params int[] sizes)
        {
            var images = sizes.Select(s => Render(s, tray, lightInk)).ToList();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
                return ms.ToArray();
            }
        }
    }
}
