using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Cordel
{
    static class Layout
    {
        public const double PanelHeight = 210, RopeTop = 10, Spacing = 174, CardWidth = 150, PinAbove = 9;
        public const double MaxPhotoH = 104, Inset = 4, Radius = 12, CardBelowTop = 28, Hidden = -(PanelHeight + 12);

        /// The rope hangs as a parabola from edge to edge of the screen.
        public static double Sag(double w) { return Math.Min(30, w * 0.018); }

        public static double RopeY(double x, double w)
        {
            if (w <= 0) return RopeTop;
            double f = x / w;
            return RopeTop + 4 * Sag(w) * f * (1 - f);
        }

        public static double X(int i, int n, double w) { return w / 2 - Math.Max(n - 1, 0) * Spacing / 2 + i * Spacing; }

        /// Card centres stay this far from the screen edges; past them, the
        /// photos that do not fit pile up.
        public const double Margin = 96, PileStep = 9, PileMax = 4;

        /// The wooden clothespin: the line crosses its upper part, the spring
        /// sits at the photo's top edge and the lower half grips the photo.
        public const double PinW = 7.3, PinH = 50;

        static BitmapSource pin;
        public static BitmapSource Pin
        {
            get
            {
                if (pin != null) return pin;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = typeof(Layout).Assembly.GetManifestResourceStream("Cordel.pinza.png");
                bi.EndInit();
                bi.Freeze();
                return pin = bi;
            }
        }

        public static bool Overflows(int n, double w) { return (n - 1) * Spacing > w - 2 * Margin; }

        public static double MaxScroll(int n, double w) { return Math.Max(0, (n - 1) * Spacing - (w - 2 * Margin)); }

        /// Where photo i of n hangs. Newest on the right; with scroll 0 the
        /// line shows the newest and the oldest pile up on the left. Scrolling
        /// brings older ones in and piles the newest on the right instead.
        public static double X(int i, int n, double w, double scroll, out double pile)
        {
            pile = 0;
            if (!Overflows(n, w)) return X(i, n, w);
            double left = Margin, right = w - Margin;
            double ideal = right - (n - 1 - i) * Spacing + scroll;
            if (ideal < left) { pile = (left - ideal) / Spacing; return left - PileStep * Math.Min(pile, PileMax); }
            if (ideal > right) { pile = (ideal - right) / Spacing; return right + PileStep * Math.Min(pile, PileMax); }
            return ideal;
        }

        public static Size PhotoSize(BitmapSource img)
        {
            double maxW = CardWidth - 14, maxH = MaxPhotoH;
            double w = img.PixelWidth, h = img.PixelHeight;
            if (w <= 0 || h <= 0) return new Size(maxW, maxH);
            double s = Math.Min(maxW / w, maxH / h);
            return new Size(w * s, h * s);
        }
    }

    /// One screenshot hanging on the line, with its animation state.
    sealed class Pegged
    {
        static readonly Random rnd = new Random();
        public readonly Guid Id = Guid.NewGuid();
        public string Path;
        public BitmapSource Thumb;
        public DateTime Stamp;
        /// Every photo hangs a little crooked, like on a real line.
        public readonly double Tilt = rnd.NextDouble() * 5 - 2.5;
        public bool Falling;
        /// Still flying in from where it was captured; the card waits hidden.
        public bool Flying;
        /// A tiny grayscale fingerprint, to recognise the same capture twice.
        public byte[] Print;

        /// How deep in a pile it sits: 0 on the open line.
        public double Pile;
        public double X, VX, Swing, VSwing, Drop, VDrop, Alpha, Scale = 1, Hover, ClipHover, Copied;
        public bool Placed, Hovered, ClipHovered;
        public BitmapSource Shadow;
        public Size ShadowFor;

        public Size Photo { get { return Layout.PhotoSize(Thumb); } }
        public Size Card { get { var p = Photo; return new Size(p.Width + Layout.Inset * 2, p.Height + Layout.Inset * 2); } }
    }

    /// A transparent strip along the top of the primary screen that floats over
    /// every window, never takes focus and lets clicks through everywhere
    /// except over the photos.
    sealed class LineWindow : Window
    {
        public readonly IntPtr Handle;
        public readonly LineView View = new LineView();
        bool clickThrough = true;
        int originX, originY;
        double scale = 1;

        public LineWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Title = "Cordel";
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            Content = View;
            Handle = new WindowInteropHelper(this).EnsureHandle();
            long ex = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(ex));
        }

        public Forms.Screen Screen { get { return Native.Primary; } }

        /// Always the primary screen, right under the top of its work area.
        public void Place()
        {
            var s = Screen;
            var wa = s.WorkingArea;
            double monitorScale = Native.ScaleOf(s);
            originX = wa.Left; originY = wa.Top;
            int h = (int)Math.Ceiling(Layout.PanelHeight * monitorScale);
            // Twice: moving across monitors may make WPF resize for the new DPI.
            for (int i = 0; i < 2; i++)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, wa.Left, wa.Top, wa.Width, h, Native.SWP_NOACTIVATE);
            var src = PresentationSource.FromVisual(this);
            scale = src != null && src.CompositionTarget != null ? src.CompositionTarget.TransformToDevice.M11 : monitorScale;
            View.WidthDip = wa.Width / scale;
            View.InvalidateVisual();
        }

        public void BringToTop()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
        }

        public Point ToLocal(Native.POINT p) { return new Point((p.X - originX) / scale, (p.Y - originY) / scale); }

        public double Scale { get { return scale; } }

        public System.Drawing.RectangleF ToScreen(Rect r)
        {
            return new System.Drawing.RectangleF((float)(originX + r.X * scale), (float)(originY + r.Y * scale),
                                                 (float)(r.Width * scale), (float)(r.Height * scale));
        }

        /// Height of the strip above the window (a taskbar docked on top), in DIPs.
        public double AboveDip { get { return (Screen.WorkingArea.Top - Screen.Bounds.Top) / scale; } }

        public void SetClickThrough(bool on)
        {
            if (on == clickThrough) return;
            clickThrough = on;
            long ex = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(ex));
        }
    }

    /// The line and everything on it, drawn by hand so every photo can swing,
    /// sway with the breeze and fall with its own little physics.
    sealed class LineView : FrameworkElement
    {
        public List<Pegged> Items = new List<Pegged>();
        public Controller Host;
        public double WidthDip;
        public event Action Hidden;

        double reveal = Layout.Hidden, revealV, hideFrom, hideT;
        bool revealed;
        bool running;
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT;

        Pegged pressed, dragging, downOn;
        Point downAt;
        bool didLongPress;
        readonly DispatcherTimer hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.45) };

        // Theme
        readonly bool dark = Settings.DarkApps;
        readonly Typeface face = new Typeface(new FontFamily("Montserrat, Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        Brush glass, ink;
        Pen glassEdge, glassOutline, photoEdge, halo;
        Brush metal, slot;
        Pen metalEdge;
        BitmapSource clipShadow;

        public bool IsDragging { get { return dragging != null; } }
        public bool IsPressed { get { return pressed != null; } }
        public bool IsRevealed { get { return revealed; } }
        public bool MenuOpen;
        double W { get { return WidthDip > 0 ? WidthDip : ActualWidth; } }

        public LineView()
        {
            Focusable = false;
            hold.Tick += (s, e) => LongPress();
            glass = Freeze(new SolidColorBrush(dark ? Color.FromArgb(205, 43, 43, 46) : Color.FromArgb(200, 255, 255, 255)));
            ink = Freeze(new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1F, 0x1F, 0x1F)));
            glassEdge = Freeze(new Pen(new LinearGradientBrush(Color.FromArgb(150, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90), 0.75));
            glassOutline = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(dark ? (byte)60 : (byte)26, 0, 0, 0)), 0.5));
            photoEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 0.5));
            halo = Freeze(new Pen(new SolidColorBrush(dark ? Color.FromArgb(220, 30, 30, 30) : Color.FromArgb(235, 255, 255, 255)), 3.5) { LineJoin = PenLineJoin.Round });
            var m = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            m.GradientStops.Add(new GradientStop(Gray(0.70), 0));
            m.GradientStops.Add(new GradientStop(Gray(0.93), 0.35));
            m.GradientStops.Add(new GradientStop(Gray(0.82), 0.65));
            m.GradientStops.Add(new GradientStop(Gray(0.62), 1));
            metal = Freeze(m);
            metalEdge = Freeze(new Pen(new LinearGradientBrush(Color.FromArgb(230, 255, 255, 255), Color.FromArgb(46, 0, 0, 0), 90), 0.6));
            slot = Freeze(new SolidColorBrush(Color.FromArgb(82, 0, 0, 0)));
            clipShadow = MakeShadow(Layout.PinW, Layout.PinH, 1.5, 3);
        }

        static Color Gray(double v) { byte b = (byte)(v * 255); return Color.FromRgb(b, b, b); }
        static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        // MARK: Public API used by the controller

        public void SetRevealed(bool on)
        {
            if (on == revealed) return;
            revealed = on;
            if (!on) { hideFrom = reveal; hideT = 0; CancelPress(); }
            Kick();
        }

        public void Added(Pegged p, bool animate)
        {
            p.X = TargetX(p);
            p.Placed = true;
            // A capture that flies in is already in place; the flight does the arriving.
            if (p.Flying) p.Alpha = 0;
            else if (animate) { p.Drop = -46; p.Swing = 16; p.Alpha = 0; }
            else p.Alpha = 1;
            Kick();
        }

        /// The capture has reached the line: the real card takes over with a small sway.
        public void Land(Pegged p)
        {
            if (!p.Flying) return;
            p.Flying = false;
            p.VSwing += 2.2 * 6.3;
            Kick();
        }

        /// The fall itself is drawn over the whole screen by Flight, so the
        /// card here steps aside at once.
        public void StartFall(Pegged p)
        {
            if (p == downOn) CancelPress();
            Items.Remove(p);
            Kick();
        }

        /// Where a card rests once the line is down and settled.
        public Rect RestingCardRect(Pegged c)
        {
            var size = c.Card;
            double x = TargetX(c);
            double top = Layout.RopeY(x, W) - Layout.PinAbove;
            return new Rect(x - size.Width / 2, top + Layout.CardBelowTop, size.Width, size.Height);
        }

        /// Where it hangs right now, unrotated, for the fall to start from.
        public Rect CurrentCardRect(Pegged c)
        {
            var r = CardRect(c);
            return new Rect(r.X, r.Y, r.Width * c.Scale, r.Height * c.Scale);
        }

        public void ShowCopied(Pegged p) { p.Copied = 1.2; p.VSwing += 3 * 6.3; Kick(); }

        public void Gust(Random rnd)
        {
            foreach (var p in Items.Where(i => !i.Falling))
            {
                var item = p;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(rnd.NextDouble() * 0.35) };
                double amount = 1.6 + rnd.NextDouble() * 1.8;
                t.Tick += (s, e) => { t.Stop(); item.VSwing += amount * 6.3; Kick(); };
                t.Start();
            }
        }

        public void SetPointer(Point p)
        {
            var over = dragging == null ? CardAt(p) : null;
            bool onClip = over != null && InClip(over, p);
            bool changed = false;
            foreach (var c in Items)
            {
                bool h = c == over;
                bool ch = h && onClip;
                if (c.Hovered != h) { c.Hovered = h; changed = true; }
                if (c.ClipHovered != ch) { c.ClipHovered = ch; changed = true; }
            }
            Cursor = onClip ? Cursors.Hand : null;
            if (changed) Kick();
        }

        /// The clip is the way to take a photo down. Its target is a little
        /// larger than the clip itself so it is easy to hit.
        public Rect ClipRect(Pegged c)
        {
            double top = Layout.RopeY(c.X, W) - Layout.PinAbove + c.Drop + reveal;
            return new Rect(c.X - 11, top - 4, 22, Layout.CardBelowTop + 6);
        }

        bool InClip(Pegged c, Point p) { return ClipRect(c).Contains(p); }

        public void ClearHover() { SetPointer(new Point(-9999, -9999)); }

        List<Pegged> DrawOrder()
        {
            return Items.Where(i => i.Pile <= Layout.PileMax + 1).OrderByDescending(i => i.Pile).ToList();
        }

        public Rect CardRect(Pegged c)
        {
            var size = c.Card;
            double top = Layout.RopeY(c.X, W) - Layout.PinAbove + c.Drop + reveal;
            return new Rect(c.X - size.Width / 2, top + Layout.CardBelowTop, size.Width, size.Height);
        }

        public Pegged CardAt(Point p, double pad = 0)
        {
            var order = DrawOrder();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var c = order[i];
                if (c.Falling || c.Alpha < 0.5) continue;
                var r = CardRect(c);
                r.Inflate(pad, pad);
                if (r.Contains(p) || ClipRect(c).Contains(p)) return c;
            }
            return null;
        }

        // MARK: Animation

        public void Kick()
        {
            if (running) return;
            running = true;
            lastT = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            double t = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(1 / 20.0, t - lastT);
            lastT = t;
            if (dt <= 0) return;
            bool active = Step(dt);
            InvalidateVisual();
            if (!active)
            {
                running = false;
                CompositionTarget.Rendering -= OnFrame;
            }
        }

        static void Resp(double response, double damping, out double k, out double c)
        {
            double w = 2 * Math.PI / response;
            k = w * w; c = 2 * damping * w;
        }

        /// Semi-implicit spring with small substeps, settles to the target.
        static bool Spring(ref double x, ref double v, double target, double k, double c, double dt, double eps)
        {
            int n = Math.Max(1, (int)Math.Ceiling(dt * 240));
            double h = dt / n;
            for (int i = 0; i < n; i++)
            {
                double a = -k * (x - target) - c * v;
                v += a * h;
                x += v * h;
            }
            if (Math.Abs(x - target) < eps && Math.Abs(v) < eps * 8) { x = target; v = 0; return false; }
            return true;
        }

        static bool Approach(ref double x, double target, double rate, double dt)
        {
            x += (target - x) * (1 - Math.Exp(-rate * dt));
            if (Math.Abs(x - target) < 0.002) { x = target; return false; }
            return true;
        }

        double scroll, scrollTarget;

        double TargetX(Pegged p) { double pile; return TargetX(p, scrollTarget, out pile); }

        double TargetX(Pegged p, double sc, out double pile)
        {
            var live = Items.Where(i => !i.Falling).ToList();
            int idx = live.IndexOf(p);
            return Layout.X(Math.Max(0, idx), live.Count, W, sc, out pile);
        }

        int LiveCount { get { return Items.Count(i => !i.Falling); } }

        public bool Overflows { get { return Layout.Overflows(LiveCount, W); } }

        double ClampScroll(double v) { return Math.Max(0, Math.Min(Layout.MaxScroll(LiveCount, W), v)); }

        /// One wheel notch moves the line by one photo.
        public void Scroll(int wheelDelta)
        {
            scrollTarget = ClampScroll(Math.Round((scrollTarget + Math.Sign(wheelDelta) * Layout.Spacing) / Layout.Spacing) * Layout.Spacing);
            Kick();
        }

        /// Back to the newest photos, where a new capture arrives.
        public void ScrollToEnd() { scrollTarget = 0; Kick(); }

        public bool IsPiled(Pegged p) { return p.Pile > 0.5; }

        /// Clicking a pile slides the line until that photo is in the open.
        public void ScrollTo(Pegged p)
        {
            var live = Items.Where(i => !i.Falling).ToList();
            int n = live.Count, idx = live.IndexOf(p);
            if (idx < 0) return;
            double right = W - Layout.Margin;
            // The scroll that puts this photo at the edge it was piled on.
            double atRight = (n - 1 - idx) * Layout.Spacing;
            double atLeft = atRight - (W - 2 * Layout.Margin);
            scrollTarget = ClampScroll(p.X < W / 2 ? atLeft + Layout.Spacing : atRight - Layout.Spacing);
            Kick();
        }

        bool Step(double dt)
        {
            bool active = false;
            double k, c;

            if (revealed)
            {
                Resp(0.42, 0.82, out k, out c);
                active |= Spring(ref reveal, ref revealV, 0, k, c, dt, 0.2);
            }
            else if (reveal > Layout.Hidden)
            {
                // Tucks away like an auto-hiding taskbar: a quick ease-in.
                hideT += dt;
                double f = Math.Min(1, hideT / 0.22);
                reveal = hideFrom + (Layout.Hidden - hideFrom) * f * f;
                revealV = 0;
                if (f < 1) active = true;
                else
                {
                    // Once tucked away, the line goes back to the newest photos,
                    // so it always comes down in its usual place.
                    scroll = scrollTarget = 0;
                    var settled = Items.Where(i => !i.Falling).ToList();
                    for (int i = 0; i < settled.Count; i++)
                    {
                        double pile;
                        settled[i].X = Layout.X(i, settled.Count, W, 0, out pile);
                        settled[i].Pile = pile;
                        settled[i].VX = 0;
                    }
                    if (Hidden != null) Hidden();
                }
            }

            var live = Items.Where(i => !i.Falling).ToList();
            scrollTarget = ClampScroll(scrollTarget);
            active |= Approach(ref scroll, scrollTarget, 12, dt);
            foreach (var p in Items.ToList())
            {
                if (p.Falling) continue;
                double pile;
                double tx = Layout.X(live.IndexOf(p), live.Count, W, scroll, out pile);
                p.Pile = pile;
                Resp(0.55, 0.78, out k, out c);
                active |= Spring(ref p.X, ref p.VX, tx, k, c, dt, 0.05);
                active |= Spring(ref p.Swing, ref p.VSwing, 0, 42, 2.5, dt, 0.02);
                Resp(0.42, 0.72, out k, out c);
                active |= Spring(ref p.Drop, ref p.VDrop, 0, k, c, dt, 0.05);
                active |= Approach(ref p.Alpha, p.Flying ? 0 : 1, 14, dt);
                active |= Approach(ref p.ClipHover, p.ClipHovered && dragging == null ? 1 : 0, 18, dt);
                bool isPressed = p == pressed;
                double scaleTarget = isPressed ? 0.95 : (p.Hovered ? 1.035 : 1);
                active |= Approach(ref p.Scale, scaleTarget, isPressed ? 5 : 18, dt);
                active |= Approach(ref p.Hover, p.Hovered && dragging != p ? 1 : 0, 16, dt);
                if (p.Copied > 0) { p.Copied = Math.Max(0, p.Copied - dt); active = true; }
            }
            if (dragging != null) active = true;
            return active;
        }

        // MARK: Drawing

        protected override void OnRender(DrawingContext dc)
        {
            double w = W;
            if (w <= 0 || reveal <= Layout.Hidden + 0.5) return;
            dc.PushTransform(new TranslateTransform(0, reveal));
            DrawRope(dc, w);
            dc.Pop();
            // Deepest in a pile first, so the photo nearest the open line is on top.
            foreach (var p in DrawOrder()) DrawCard(dc, p, w);
        }

        StreamGeometry rope;
        double ropeFor;
        LinearGradientBrush ropeMask;

        void DrawRope(DrawingContext dc, double w)
        {
            if (rope == null || ropeFor != w)
            {
                ropeFor = w;
                rope = new StreamGeometry();
                using (var g = rope.Open())
                {
                    g.BeginFigure(new Point(-20, Layout.RopeTop), false, false);
                    g.QuadraticBezierTo(new Point(w / 2, Layout.RopeTop + 2 * Layout.Sag(w)), new Point(w + 20, Layout.RopeTop), true, true);
                }
                rope.Freeze();
                ropeMask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(w, 0) };
                ropeMask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                ropeMask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
                ropeMask.GradientStops.Add(new GradientStop(Colors.Black, 0.92));
                ropeMask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                ropeMask.Freeze();
            }
            // A thin neutral line with a faint highlight and a soft shadow, so
            // it reads on light and dark backgrounds alike.
            dc.PushOpacityMask(ropeMask);
            dc.PushTransform(new TranslateTransform(0, 1.2));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 2.2), rope);
            dc.Pop();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Gray(0.55)), 1.2), rope);
            dc.PushTransform(new TranslateTransform(0, -0.35));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)), 0.4), rope);
            dc.Pop();
            dc.Pop();
        }

        void DrawCard(DrawingContext dc, Pegged p, double w)
        {
            var size = p.Card;
            var photo = p.Photo;
            double top = Layout.RopeY(p.X, w) - Layout.PinAbove + p.Drop + reveal;
            double cardTop = top + Layout.CardBelowTop;
            double left = p.X - size.Width / 2;
            var card = new Rect(left, cardTop, size.Width, size.Height);
            int pushes = 0;

            double alpha = p.Alpha;
            if (p.Falling || alpha <= 0.005) return;
            dc.PushOpacity(alpha); pushes++;
            dc.PushTransform(new RotateTransform(p.Swing + p.Tilt, p.X, top)); pushes++;

            dc.PushTransform(new ScaleTransform(p.Scale, p.Scale, p.X, cardTop));
            dc.PushOpacity(dragging == p ? 0.45 : 1);

            if (p.Shadow == null || p.ShadowFor != size) { p.Shadow = MakeShadow(size.Width, size.Height, Layout.Radius, 10); p.ShadowFor = size; }
            double pad = 30, sy = 5 + 3 * p.Hover;
            dc.PushOpacity((dark ? 0.35 : 0.18) + 0.08 * p.Hover);
            dc.DrawImage(p.Shadow, new Rect(left - pad, cardTop - pad + sy, size.Width + 2 * pad, size.Height + 2 * pad));
            dc.Pop();

            dc.DrawRoundedRectangle(glass, null, card, Layout.Radius, Layout.Radius);
            var outer = card; outer.Inflate(0.5, 0.5);
            dc.DrawRoundedRectangle(null, glassOutline, outer, Layout.Radius + 0.5, Layout.Radius + 0.5);
            dc.DrawRoundedRectangle(null, glassEdge, card, Layout.Radius, Layout.Radius);

            double r = Layout.Radius - Layout.Inset;
            var pr = new Rect(left + Layout.Inset, cardTop + Layout.Inset, photo.Width, photo.Height);
            dc.PushClip(new RectangleGeometry(pr, r, r));
            dc.DrawImage(p.Thumb, pr);
            dc.Pop();
            dc.DrawRoundedRectangle(null, photoEdge, pr, r, r);
            dc.Pop(); // drag dim

            if (p.Copied > 0)
            {
                double a = Math.Min(1, Math.Min(p.Copied / 0.2, (1.2 - p.Copied) / 0.15));
                dc.PushOpacity(a);
                DrawText(dc, "✓ " + T.L("Copied", "Copiado"), 11.5, p.X, cardTop + size.Height + 14 - 4 * (1 - a));
                dc.Pop();
            }
            dc.Pop(); // scale

            DrawClip(dc, p.X, top, p.ClipHover);
            for (; pushes > 0; pushes--) dc.Pop();
        }

        /// The wooden clothespin. Under the pointer it grows a little, inviting
        /// a click to take the photo down.
        void DrawClip(DrawingContext dc, double x, double top, double hover)
        {
            double s = 1 + 0.18 * hover;
            dc.PushTransform(new ScaleTransform(s, s, x, top + Layout.PinAbove));
            var rect = new Rect(x - Layout.PinW / 2, top, Layout.PinW, Layout.PinH);
            if (hover > 0.01)
            {
                var ring = rect; ring.Inflate(3, 3);
                dc.PushOpacity(hover);
                dc.DrawRoundedRectangle(glass, glassOutline, ring, 4, 4);
                dc.Pop();
            }
            dc.PushOpacity(0.35);
            dc.DrawImage(clipShadow, new Rect(rect.X - 9 + 1, rect.Y - 9 + 2, rect.Width + 18, rect.Height + 18));
            dc.Pop();
            dc.DrawImage(Layout.Pin, rect);
            dc.Pop();
        }

        void DrawText(DrawingContext dc, string text, double size, double cx, double cy)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, ink, dpi);
            var geo = ft.BuildGeometry(new Point(cx - ft.Width / 2, cy - ft.Height / 2));
            dc.DrawGeometry(null, halo, geo);
            dc.DrawGeometry(ink, null, geo);
        }

        static BitmapSource MakeShadow(double w, double h, double r, double blur)
        {
            double pad = blur * 3;
            var dv = new DrawingVisual { Effect = new BlurEffect { Radius = blur * 2, KernelType = KernelType.Gaussian } };
            using (var g = dv.RenderOpen()) g.DrawRoundedRectangle(Brushes.Black, null, new Rect(pad, pad, w, h), r, r);
            var bmp = new RenderTargetBitmap((int)Math.Ceiling((w + 2 * pad) * 2), (int)Math.Ceiling((h + 2 * pad) * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        // MARK: Gestures. Click copies, double click opens, hold edits,
        // drag sends the file, a click on the clip takes it down.

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            SetPointer(p);
            if (downOn != null && e.LeftButton == MouseButtonState.Pressed && !didLongPress)
            {
                var d = p - downAt;
                if (d.Length > 4) BeginDrag(downOn);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            var c = CardAt(p);
            e.Handled = true;
            if (c == null) return;
            if (IsPiled(c)) { ScrollTo(c); return; }
            if (InClip(c, p)) { Cursor = null; Host.Discard(c); return; }
            if (e.ClickCount == 2) { CancelPress(); Host.Open(c); return; }
            downOn = c; pressed = c; downAt = p; didLongPress = false;
            hold.Stop(); hold.Start();
            CaptureMouse();
            Kick();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            var c = downOn;
            bool click = c != null && !didLongPress;
            CancelPress();
            if (click) Host.Copy(c);
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            var c = CardAt(e.GetPosition(this));
            e.Handled = true;
            if (c == null) return;
            var menu = Host.MenuFor(c);
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            MenuOpen = true;
            menu.Closed += (s, a) => MenuOpen = false;
            menu.IsOpen = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e) { if (dragging == null) ClearHover(); }

        void CancelPress()
        {
            hold.Stop();
            downOn = null;
            pressed = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Kick();
        }

        void LongPress()
        {
            hold.Stop();
            var c = downOn;
            if (c == null) return;
            didLongPress = true;
            pressed = null;
            Kick();
            Host.Edit(c);
        }

        void BeginDrag(Pegged c)
        {
            hold.Stop();
            pressed = null;
            downOn = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            dragging = c;
            c.Hovered = false;
            Kick();
            try
            {
                DataObject data = null;
                var shell = Native.ShellDataObject(c.Path);
                if (shell != null)
                {
                    try { using (var bmp = Imaging.ToGdi(c.Thumb, 220)) Native.AttachDragImage(shell, bmp); }
                    catch (Exception ex) { Log.Write("drag image: " + ex.Message); }
                    data = new DataObject(shell);
                }
                if (data == null)
                {
                    data = new DataObject();
                    data.SetFileDropList(new System.Collections.Specialized.StringCollection { c.Path });
                }
                // Apps take a copy and the photo stays. A folder takes the file
                // (Explorer moves it on the same drive) and the photo leaves.
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch (Exception ex) { Log.Write("drag: " + ex); }
            dragging = null;
            Kick();
            Host.DragEnded();
        }
    }
}
