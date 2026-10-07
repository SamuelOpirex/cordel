using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Forms = System.Windows.Forms;

namespace Cordel
{
    /// Windows does not record where a screenshot was taken, so it is worked
    /// out from the mouse: a region snip is the drag between press and
    /// release, a window snip is the window clicked, and a full screen
    /// capture is a monitor or the whole desktop.
    static class CaptureLocator
    {
        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public Native.POINT pt; public int mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);

        /// Called for every wheel turn; returning true swallows it.
        public static Func<Native.POINT, int, bool> OnWheel;
        static HookProc proc;
        static IntPtr hook;
        static Native.POINT downAt;

        struct Drag { public Rectangle Rect; public Native.POINT Up; public DateTime Time; }
        /// Recent presses and releases. A snip is often followed by more
        /// clicks (Save, Copy), so the matching drag is searched by size.
        static readonly List<Drag> drags = new List<Drag>();
        static readonly object gate = new object();

        public static void Start()
        {
            proc = Hook;
            hook = SetWindowsHookEx(14, proc, GetModuleHandle(null), 0); // WH_MOUSE_LL
            if (hook == IntPtr.Zero) Log.Write("mouse hook failed");
        }

        static IntPtr Hook(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = (int)w;
                if (msg == 0x020A && OnWheel != null) // WM_MOUSEWHEEL
                {
                    var s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                    try { if (OnWheel(s.pt, (short)(s.mouseData >> 16))) return new IntPtr(1); }
                    catch (Exception e) { Log.Write("wheel: " + e.Message); }
                }
                else if (msg == 0x0201 || msg == 0x0202)
                {
                    var s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                    if (msg == 0x0201) downAt = s.pt;
                    else
                    {
                        var r = Rectangle.FromLTRB(Math.Min(downAt.X, s.pt.X), Math.Min(downAt.Y, s.pt.Y),
                                                   Math.Max(downAt.X, s.pt.X), Math.Max(downAt.Y, s.pt.Y));
                        lock (gate)
                        {
                            drags.Add(new Drag { Rect = r, Up = s.pt, Time = DateTime.Now });
                            if (drags.Count > 40) drags.RemoveAt(0);
                        }
                    }
                }
            }
            return CallNextHookEx(hook, code, w, l);
        }

        static bool Near(int a, int b, int tol) { return Math.Abs(a - b) <= tol; }

        public static Rectangle Locate(int w, int h)
        {
            List<Drag> recent;
            lock (gate)
            {
                var cutoff = DateTime.Now.AddMinutes(-3);
                recent = drags.Where(d => d.Time > cutoff).Reverse().ToList();
            }

            // Region snip: the most recent drag of the same size.
            foreach (var d in recent)
                if (Near(d.Rect.Width, w, 6) && Near(d.Rect.Height, h, 6))
                    return new Rectangle(d.Rect.X + (d.Rect.Width - w) / 2, d.Rect.Y + (d.Rect.Height - h) / 2, w, h);

            // Full screen: one monitor, or every monitor at once (Win+PrtScn).
            Native.POINT cur;
            Native.GetCursorPos(out cur);
            var under = Forms.Screen.FromPoint(new Point(cur.X, cur.Y));
            if (under.Bounds.Width == w && under.Bounds.Height == h) return under.Bounds;
            foreach (var s in Forms.Screen.AllScreens)
                if (s.Bounds.Width == w && s.Bounds.Height == h) return s.Bounds;
            var all = Forms.SystemInformation.VirtualScreen;
            if (all.Width == w && all.Height == h) return all;

            // Window snip: a window recently clicked that has the image's size.
            foreach (var d in recent.Take(10))
            {
                var win = GetAncestor(WindowFromPoint(d.Up), 2);
                Native.RECT wr;
                if (win == IntPtr.Zero || DwmGetWindowAttribute(win, 9, out wr, 16) != 0) continue; // DWMWA_EXTENDED_FRAME_BOUNDS
                var rr = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
                if (Near(rr.Width, w, 24) && Near(rr.Height, h, 24))
                    return new Rectangle(rr.X + (rr.Width - w) / 2, rr.Y + (rr.Height - h) / 2, w, h);
            }

            // Anything else rises from the middle of the screen in use.
            var b = under.Bounds;
            double fit = Math.Min(1, Math.Min(b.Width * 0.6 / w, b.Height * 0.6 / h));
            int fw = (int)(w * fit), fh = (int)(h * fit);
            return new Rectangle(b.Left + (b.Width - fw) / 2, b.Top + (b.Height - fh) / 2, fw, fh);
        }
    }

    /// A borderless per-pixel-alpha window that floats above everything,
    /// ignores the mouse and is repainted with a fresh bitmap every frame.
    /// Drawing in physical pixels keeps it right on every monitor and DPI.
    sealed class FlightWindow : Forms.Form
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Native.POINT pos, ref SIZE size, IntPtr src, ref Native.POINT srcPos, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public FlightWindow()
        {
            FormBorderStyle = Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = Forms.FormStartPosition.Manual;
            AutoScaleMode = Forms.AutoScaleMode.None;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Layered, click-through, tool window, never activated, topmost.
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        // One premultiplied DIB reused every frame: GDI+ draws straight into
        // it and the window shows it, with no per-frame allocation or copy.
        IntPtr memDC, dib, oldObj;
        Bitmap canvas;
        int capW, capH;

        public Graphics Begin(int w, int h)
        {
            if (w > capW || h > capH)
            {
                ReleaseCanvas();
                capW = Math.Max(w, capW); capH = Math.Max(h, capH);
                var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = capW, biHeight = -capH, biPlanes = 1, biBitCount = 32 };
                IntPtr bits;
                memDC = CreateCompatibleDC(IntPtr.Zero);
                dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                oldObj = SelectObject(memDC, dib);
                canvas = new Bitmap(capW, capH, capW * 4, PixelFormat.Format32bppPArgb, bits);
            }
            var g = Graphics.FromImage(canvas);
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0))) g.FillRectangle(clear, 0, 0, w, h);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SetClip(new Rectangle(0, 0, w, h));
            return g;
        }

        public void Present(int w, int h, int x, int y, double alpha)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                var size = new SIZE { cx = w, cy = h };
                var pos = new Native.POINT { X = x, Y = y };
                var src = new Native.POINT();
                var blend = new BLEND { Op = 0, Flags = 0, Alpha = (byte)Math.Max(0, Math.Min(255, alpha * 255)), Format = 1 };
                UpdateLayeredWindow(Handle, screen, ref pos, ref size, memDC, ref src, 0, ref blend, 2);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        void ReleaseCanvas()
        {
            if (canvas != null) { canvas.Dispose(); canvas = null; }
            if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteObject(dib); DeleteDC(memDC); memDC = IntPtr.Zero; }
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseCanvas();
            base.Dispose(disposing);
        }
    }

    /// The capture lifting off the screen and flying up to the line. It turns
    /// into the hanging card on the way: it shrinks, tilts into place and
    /// grows its glass frame and clip, so nothing changes on landing. The same
    /// window draws a discarded card falling off the line over the screen.
    sealed class Flight
    {
        const double FlyDuration = 0.65, FallDuration = 0.55, Arc = 30;
        /// Before moving, the capture lifts off: it gains a white border and a
        /// shadow and grows a touch, so it reads as a photo leaving the screen.
        const double Lift = 0.18;

        readonly FlightWindow window = new FlightWindow();
        readonly List<Bitmap> mips = new List<Bitmap>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly RectangleF from, to;
        readonly double tilt, scale, duration;
        readonly bool falling, dark;
        Action done;
        double fadeStart = -1;

        static readonly List<Flight> current = new List<Flight>();

        Flight(Bitmap image, RectangleF from, RectangleF to, double tilt, double scale, bool falling)
        {
            this.from = from; this.to = to; this.tilt = tilt; this.scale = scale; this.falling = falling;
            dark = Settings.DarkApps;
            duration = falling ? FallDuration : FlyDuration;
            // Smaller copies of the image so each frame scales a nearby size.
            mips.Add(image);
            while (mips[mips.Count - 1].Width > 200)
            {
                var prev = mips[mips.Count - 1];
                var half = new Bitmap(Math.Max(1, prev.Width / 2), Math.Max(1, prev.Height / 2), PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(half))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(prev, new Rectangle(0, 0, half.Width, half.Height));
                }
                mips.Add(half);
            }
        }

        /// - from: the captured area in physical screen pixels.
        /// - to: the card's resting frame on the line, in physical pixels, unrotated.
        public static void Fly(Bitmap image, RectangleF from, RectangleF to, double tilt, double scale, Action landed)
        {
            new Flight(image, from, to, tilt, scale, false).Run(landed);
        }

        public static void Fall(Bitmap image, RectangleF card, double tilt, double scale)
        {
            new Flight(image, card, card, tilt, scale, true).Run(null);
        }

        void Run(Action landed)
        {
            done = landed;
            current.Add(this);
            var h = window.Handle;
            // Time starts now, after the image is prepared, so no frame is skipped.
            clock.Restart();
            Frame();
            window.Show();
            System.Windows.Media.CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            try { Frame(); }
            catch (Exception ex) { Log.Write("flight: " + ex.Message); Finish(); }
        }

        void Frame()
        {
            double t = clock.Elapsed.TotalSeconds;
            if (fadeStart >= 0)
            {
                // The real card fades in underneath; this one fades out over it.
                double a = 1 - (t - fadeStart) / 0.16;
                if (a <= 0) { Finish(); return; }
                Draw(1, a);
                return;
            }
            if (!falling && t < Lift)
            {
                double l = t / Lift;
                Draw(0, 1, 1 - (1 - l) * (1 - l));
                return;
            }
            double k = Math.Min(1, (falling ? t : t - Lift) / duration);
            Draw(k, 1);
            if (k < 1) return;
            if (falling) { Finish(); return; }
            fadeStart = t;
            if (done != null) { var d = done; done = null; d(); }
        }

        void Finish()
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
            current.Remove(this);
            Log.Write((falling ? "fall" : "flight") + ": " + frames + " frames in " + clock.ElapsedMilliseconds + " ms");
            if (done != null) { var d = done; done = null; d(); }
            window.Close();
            window.Dispose();
            foreach (var m in mips) m.Dispose();
        }

        static double EaseInOutCubic(double x) { return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2; }

        static double Smooth(double x, double a, double b)
        {
            double t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
            return t * t * (3 - 2 * t);
        }

        void Draw(double raw, double opacity, double lift = 1)
        {
            float s = (float)scale;
            double w, h, cx, top, angle, alpha = opacity, chrome, inset, radius;
            if (falling)
            {
                double e = raw * raw * raw;
                w = to.Width; h = to.Height; cx = to.X + to.Width / 2;
                top = to.Y + 520 * s * e;
                angle = tilt + (tilt * 7 + 20) * e;
                alpha *= 1 - e;
                chrome = 1; inset = Layout.Inset * s; radius = Layout.Radius * s;
            }
            else
            {
                double k = EaseInOutCubic(raw);
                Func<double, double, double> lerp = (a, b) => a + (b - a) * k;
                w = lerp(from.Width, to.Width); h = lerp(from.Height, to.Height);
                cx = lerp(from.X + from.Width / 2, to.X + to.Width / 2);
                top = lerp(from.Y, to.Y) - Math.Sin(Math.PI * k) * Arc * s;
                angle = tilt * k;
                chrome = Smooth(k, 0.35, 1);
                inset = Layout.Inset * s * k;
                radius = Layout.Radius * s * k;
                // The lift's small growth eases back out as it flies.
                double grow = 1 + 0.025 * lift * (1 - k);
                top -= (grow - 1) * h / 2;
                w *= grow; h *= grow;
            }
            if (falling) lift = 1;

            // The card in local space: top center at the origin, then rotated.
            float pad = 24 * s, clipAbove = (float)Layout.CardBelowTop * s;
            var local = new RectangleF((float)(-w / 2) - pad, -clipAbove - pad, (float)w + 2 * pad, (float)h + clipAbove + 2 * pad);
            var pts = new[] { new PointF(local.Left, local.Top), new PointF(local.Right, local.Top), new PointF(local.Left, local.Bottom), new PointF(local.Right, local.Bottom) };
            using (var m = new Matrix()) { m.Rotate((float)angle); m.TransformPoints(pts); }
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            int bw = Math.Max(1, (int)Math.Ceiling(maxX - minX)), bh = Math.Max(1, (int)Math.Ceiling(maxY - minY));

            using (var g = window.Begin(bw, bh))
            {
                bool big = w * h > 600000;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = big ? PixelOffsetMode.HighSpeed : PixelOffsetMode.HighQuality;
                g.InterpolationMode = big ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
                g.TranslateTransform(-minX, -minY);
                g.RotateTransform((float)angle);
                DrawCard(g, (float)w, (float)h, (float)inset, (float)radius, (float)chrome, (float)lift, s);
            }
            window.Present(bw, bh, (int)Math.Round(cx + minX), (int)Math.Round(top + minY), alpha);
            frames++;
        }

        int frames;

        void DrawCard(Graphics g, float w, float h, float inset, float radius, float chrome, float lift, float s)
        {
            var card = new RectangleF(-w / 2, 0, w, h);
            // The border that lifts it off the screen hands over to the glass frame.
            float border = lift * (1 - chrome);
            float bw = 3 * s;
            // A soft shadow, built from a few widening translucent layers.
            float shade = Math.Max(chrome, lift);
            if (shade > 0.01)
                for (int i = 3; i >= 0; i--)
                {
                    var r = card; r.Inflate(i * 3 * s + bw * border, i * 3 * s + bw * border); r.Offset(0, 5 * s);
                    using (var path = Rounded(r, radius + i * 3 * s))
                    using (var b = new SolidBrush(Color.FromArgb((int)(shade * (dark ? 34 : 22)), 0, 0, 0)))
                        g.FillPath(b, path);
                }
            if (border > 0.01)
            {
                var outer = card; outer.Inflate(bw, bw);
                using (var path = Rounded(outer, radius + bw))
                using (var b = new SolidBrush(Color.FromArgb((int)(245 * border), 255, 255, 255)))
                    g.FillPath(b, path);
                using (var path = Rounded(outer, radius + bw))
                using (var pen = new Pen(Color.FromArgb((int)(60 * border), 0, 0, 0), 1 * s))
                    g.DrawPath(pen, path);
            }
            using (var path = Rounded(card, radius))
            using (var b = new SolidBrush(dark ? Color.FromArgb((int)(205 * chrome), 43, 43, 46) : Color.FromArgb((int)(200 * chrome), 255, 255, 255)))
                g.FillPath(b, path);

            var photo = new RectangleF(card.X + inset, card.Y + inset, w - 2 * inset, h - 2 * inset);
            if (photo.Width > 1 && photo.Height > 1)
            {
                Bitmap src = mips[0];
                foreach (var m in mips) if (m.Width >= photo.Width) src = m;
                var state = g.Save();
                using (var clip = Rounded(photo, Math.Max(0, radius - inset)))
                {
                    if (radius - inset > 0.5) g.SetClip(clip);
                    g.DrawImage(src, photo);
                }
                g.Restore(state);
            }

            if (chrome > 0.01)
            {
                using (var path = Rounded(card, radius))
                using (var pen = new Pen(Color.FromArgb((int)(150 * chrome), 255, 255, 255), 0.75f * s))
                    g.DrawPath(pen, path);
                // The clothespin, placed exactly as on the line.
                var pinRect = new RectangleF((float)(-Layout.PinW / 2) * s, -(float)Layout.CardBelowTop * s,
                                             (float)Layout.PinW * s, (float)Layout.PinH * s);
                using (var attrs = new ImageAttributes())
                {
                    attrs.SetColorMatrix(new ColorMatrix { Matrix33 = chrome });
                    g.DrawImage(PinImage, Rectangle.Round(pinRect), 0, 0, PinImage.Width, PinImage.Height, GraphicsUnit.Pixel, attrs);
                }
            }
        }

        static Bitmap pinImage;
        static Bitmap PinImage
        {
            get
            {
                return pinImage ?? (pinImage = new Bitmap(typeof(Flight).Assembly.GetManifestResourceStream("Cordel.pinza.png")));
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d < 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Bitmap LoadGdi(string path, int maxPixels)
        {
            var bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                double f = Math.Min(1, (double)maxPixels / Math.Max(img.Width, img.Height));
                var bmp = new Bitmap(Math.Max(1, (int)(img.Width * f)), Math.Max(1, (int)(img.Height * f)), PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(img, new Rectangle(0, 0, bmp.Width, bmp.Height));
                }
                return bmp;
            }
        }
    }
}
