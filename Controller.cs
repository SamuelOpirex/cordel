using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("Cordel")]
[assembly: System.Reflection.AssemblyProduct("Cordel")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace Cordel
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                File.WriteAllBytes(args[1], Art.Ico(false, true, 16, 20, 24, 32, 48, 64, 256));
                return 0;
            }
            bool first;
            var mutex = new Mutex(true, "Local\\Cordel.SingleInstance", out first);
            if (!first) return 0;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log.Write("unhandled: " + e.Exception); e.Handled = true; };
            var ctl = new Controller();
            app.Startup += (s, e) => ctl.Start();
            app.Exit += (s, e) => ctl.Stop();
            int code = app.Run();
            GC.KeepAlive(mutex);
            return code;
        }
    }

    /// Decides what hangs on the line and when the line shows itself.
    sealed class Controller
    {
        static readonly Random rnd = new Random();
        LineWindow win;
        LineView view;
        Forms.NotifyIcon tray;
        ScreenshotWatcher watcher;
        HwndSource messages;
        readonly DispatcherTimer mouse = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        readonly DispatcherTimer gust = new DispatcherTimer();

        bool revealed, pinned, wanted, keepOpen, suppressed, hotkeyOk;
        DateTime peekUntil = DateTime.MinValue, ignoreClipboardUntil = DateTime.MinValue, lastFolderCapture = DateTime.MinValue;
        DateTime? hotSince, awaySince;
        int lastLive;
        /// Photos pile up and scroll rather than falling off; this only keeps
        /// memory in check.
        const int maxItems = 50;
        bool restoring;

        /// How long the pointer rests against the top edge before the line
        /// comes down, and how long it is away before it tucks back up.
        const double RevealDelay = 0.3, RetractDelay = 0.5;

        int LiveCount { get { return view.Items.Count(i => !i.Falling); } }
        bool ClipboardCaptures { get { return Settings.Get("clipboard"); } }

        public void Start()
        {
            Paths.MigrateInbox();
            Directory.CreateDirectory(Paths.Inbox);
            Sound.On = !Settings.Get("soundOff");
            win = new LineWindow();
            view = win.View;
            view.Host = this;
            view.Hidden += () => { if (!revealed) win.Hide(); };
            win.Place();
           

            restoring = true;
            foreach (var p in Settings.Pegged.Where(File.Exists)) Hang(p, quietly: true);
            restoring = false;
            lastLive = LiveCount;
            wanted = lastLive > 0;

            watcher = new ScreenshotWatcher(Paths.Screenshots, path =>
            {
                lastFolderCapture = DateTime.Now;
                HangCapture(path);
            }, Prune);
            CaptureLocator.Start();
            CaptureLocator.OnWheel = OnWheel;

            var mp = new HwndSourceParameters("CordelMessages") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
            messages = new HwndSource(mp);
            messages.AddHook(WndProc);
            // Ctrl+Alt+T, without auto-repeat.
            hotkeyOk = Native.RegisterHotKey(messages.Handle, 1, 0x0002 | 0x0001 | 0x4000, (uint)'T');
            Native.AddClipboardFormatListener(messages.Handle);

            SetUpTray();
            mouse.Tick += (s, e) => Tick();
            gust.Tick += (s, e) => Breeze();
            Breeze();

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) =>
                win.Dispatcher.BeginInvoke(new Action(() => { win.Place(); }));

            Refresh();
            CleanInbox();

            if (!Settings.Get("welcomed"))
            {
                Settings.Set("welcomed", true);
                keepOpen = true; wanted = true;
                Refresh();
                Reveal(pin: true);
                tray.ShowBalloonTip(6000, "Cordel",
                    T.L("Your screenshots will hang at the top of the main screen. Rest the pointer on the top edge to see them, or press Ctrl+Alt+T.",
                        "Tus capturas se colgarán arriba en la pantalla principal. Apoya el puntero en el borde superior para verlas, o pulsa Ctrl+Alt+T."),
                    Forms.ToolTipIcon.None);
                After(5, () => { if (LiveCount == 0) { keepOpen = false; wanted = false; Refresh(); } });
            }
            if (!hotkeyOk)
                tray.ShowBalloonTip(5000, "Cordel", T.L("Ctrl+Alt+T is taken by another app.", "Otra aplicación ya usa Ctrl+Alt+T."), Forms.ToolTipIcon.Warning);
        }

        public void Stop()
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (messages != null) Native.UnregisterHotKey(messages.Handle, 1);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312) { Toggle(); handled = true; }          // WM_HOTKEY
            else if (msg == 0x031D) OnClipboard();                     // WM_CLIPBOARDUPDATE
            return IntPtr.Zero;
        }

        static void After(double seconds, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (s, e) => { t.Stop(); a(); };
            t.Start();
        }

        // MARK: Hanging and dropping

        Pegged Hang(string path, bool quietly = false, bool flying = false, System.Windows.Media.Imaging.BitmapSource thumb = null)
        {
            if (view.Items.Any(i => !i.Falling && Paths.Same(i.Path, path))) return null;
            if (thumb == null) thumb = Imaging.Load(path, 480);
            if (thumb == null) return null;
            var p = new Pegged { Path = path, Thumb = thumb, Stamp = SafeStamp(path), Flying = flying, Print = Imaging.Fingerprint(thumb) };
            view.Items.Add(p);
            view.Added(p, animate: !quietly);
            Save();
            if (!quietly)
            {
                Sound.Tink();
                // Every new capture brings the line down, even when it is full
                // and the count does not change.
                Arrived();
            }
            // A full line lets the oldest photo fall off the far end.
            while (LiveCount > maxItems) Drop(view.Items.First(i => !i.Falling), true);
            ItemsChanged();
            return p;
        }

        static DateTime SafeStamp(string p) { try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; } }

        /// A new screenshot lifts off from where it was taken and flies to its
        /// place on the line, on the primary screen.
        void HangCapture(string path)
        {
            System.Drawing.Rectangle from;
            try
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(path)),
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                from = CaptureLocator.Locate(frame.PixelWidth, frame.PixelHeight);
            }
            catch { Hang(path); return; }

            // The Snipping Tool can both copy and save the same capture. The
            // first to arrive hangs; the twin never hangs a second photo.
            var thumb = Imaging.Load(path, 480);
            if (thumb == null) return;
            var print = Imaging.Fingerprint(thumb);
            var twin = view.Items.FirstOrDefault(i => !i.Falling && i.Thumb.PixelWidth == thumb.PixelWidth
                && i.Thumb.PixelHeight == thumb.PixelHeight && Imaging.Same(i.Print, print));
            if (twin != null)
            {
                if (Paths.InInbox(twin.Path) && !Paths.InInbox(path))
                {
                    // The saved file replaces the clipboard copy on the line.
                    var copy = twin.Path;
                    twin.Path = path;
                    twin.Stamp = SafeStamp(path);
                    Save();
                    try { File.Delete(copy); } catch (Exception e) { Log.Write("twin: " + e.Message); }
                }
                else if (Paths.InInbox(path))
                {
                    try { File.Delete(path); } catch { }
                }
                return;
            }
            var p = Hang(path, flying: true, thumb: thumb);
            if (p == null) return;
            // Let the line come down and lay out before measuring the landing spot.
            After(0.03, () => Fly(p, from));
        }

        void Fly(Pegged p, System.Drawing.Rectangle from)
        {
            if (!revealed || !view.Items.Contains(p)) { view.Land(p); return; }
            try
            {
                int pixels = Math.Min(3000, Math.Max(400, Math.Max(from.Width, from.Height)));
                var image = Flight.LoadGdi(p.Path, pixels);
                var to = win.ToScreen(view.RestingCardRect(p));
                Flight.Fly(image, from, to, p.Tilt, win.Scale, () => view.Land(p));
            }
            catch (Exception e) { Log.Write("fly: " + e.Message); view.Land(p); }
        }

        void Drop(Pegged p, bool quietly = false)
        {
            if (p == null || p.Falling) return;
            // A discarded card falls over the whole screen, from where it hangs.
            if (revealed && !p.Flying && p.Alpha > 0.5)
            {
                try
                {
                    var card = win.ToScreen(view.CurrentCardRect(p));
                    Flight.Fall(Imaging.ToGdi(p.Thumb, 480), card, p.Tilt + p.Swing, win.Scale);
                }
                catch (Exception e) { Log.Write("fall: " + e.Message); }
            }
            p.Falling = true;
            p.Hovered = false;
            view.StartFall(p);
            Save();
            if (!quietly) Sound.Pop();
            ItemsChanged();
        }

        void Clear()
        {
            var live = view.Items.Where(i => !i.Falling).ToList();
            for (int n = 0; n < live.Count; n++)
            {
                var item = live[n];
                bool quiet = n > 0;
                if (n == 0) Drop(item); else After(0.06 * n, () => Drop(item, quiet));
            }
        }

        /// Photos whose file was deleted or moved away fall off by themselves;
        /// edited ones show the new version.
        void Prune()
        {
            foreach (var p in view.Items.Where(i => !i.Falling).ToList())
            {
                if (!File.Exists(p.Path)) { Drop(p, true); continue; }
                var stamp = SafeStamp(p.Path);
                if (stamp != p.Stamp)
                {
                    var t = Imaging.Load(p.Path, 480);
                    if (t != null) { p.Thumb = t; p.Print = Imaging.Fingerprint(t); p.Stamp = stamp; view.Kick(); }
                }
            }
        }

        void Save()
        {
            if (restoring) return;
            Settings.Pegged = view.Items.Where(i => !i.Falling).Select(i => i.Path).ToList();
        }

        void ItemsChanged()
        {
            if (restoring) return;
            int live = LiveCount;
            if (live == 0 && !keepOpen)
            {
                After(0.7, () => { if (LiveCount == 0 && !keepOpen) { wanted = false; Refresh(); } });
            }
            view.Kick();
            lastLive = live;
        }

        /// New captures always travel to the line on the primary screen.
        void Arrived()
        {
            win.Place();
            view.ScrollToEnd();
            wanted = true;
            Refresh();
            Reveal(peekSeconds: 2.5);
        }

        /// The wheel anywhere over the line scrolls it, when there is more
        /// than fits. Elsewhere it goes to the app under the pointer as usual.
        bool OnWheel(Native.POINT pt, int delta)
        {
            if (!revealed || !view.Overflows) return false;
            var b = win.Screen.Bounds;
            if (pt.X < b.Left || pt.X >= b.Right || pt.Y < b.Top || pt.Y >= b.Bottom) return false;
            var local = win.ToLocal(pt);
            if (local.Y > Layout.PanelHeight - 30) return false;
            view.Scroll(delta);
            awaySince = null;
            return true;
        }

        void Breeze()
        {
            // Every so often a little wind moves the line.
            if (view.Items.Count > 0 && !view.IsDragging && revealed) view.Gust(rnd);
            gust.Stop();
            gust.Interval = TimeSpan.FromSeconds(7 + rnd.NextDouble() * 9);
            gust.Start();
        }

        // MARK: Actions on one photo

        public void Copy(Pegged p)
        {
            try
            {
                var full = Imaging.Load(p.Path);
                if (full == null) throw new IOException("cannot read " + p.Path);
                var data = new DataObject();
                data.SetImage(full);
                var png = Path.GetExtension(p.Path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllBytes(p.Path) : Imaging.Png(full);
                data.SetData("PNG", new MemoryStream(png));
                data.SetFileDropList(new StringCollection { p.Path });
                ignoreClipboardUntil = DateTime.Now.AddSeconds(2);
                for (int attempt = 0; ; attempt++)
                {
                    try { Clipboard.SetDataObject(data, true); break; }
                    catch (System.Runtime.InteropServices.COMException) { if (attempt >= 4) throw; Thread.Sleep(60); }
                }
                view.ShowCopied(p);
            }
            catch (Exception e) { Log.Write("copy: " + e.Message); System.Media.SystemSounds.Beep.Play(); }
        }

        public void Open(Pegged p) { Shell(p.Path, null); }

        /// Press and hold: edit it in Paint. Saving shows the new version.
        public void Edit(Pegged p) { Shell("mspaint.exe", "\"" + p.Path + "\""); }

        void ShowInExplorer(Pegged p) { Shell("explorer.exe", "/select,\"" + p.Path + "\""); }

        static void Shell(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
            catch (Exception e) { Log.Write("open: " + e.Message); System.Media.SystemSounds.Beep.Play(); }
        }

        void Trash(Pegged p)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Sound.Rustle();
                Drop(p, true);
            }
            catch (Exception e) { Log.Write("trash: " + e.Message); System.Media.SystemSounds.Beep.Play(); }
        }

        /// The cross: clipboard captures go to the Recycle Bin, screenshots
        /// saved by Windows stay where they are and just come off the line.
        public void Discard(Pegged p)
        {
            if (Paths.InInbox(p.Path)) Trash(p); else Drop(p);
        }

        void SaveToDesktop(Pegged p)
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var name = Path.GetFileNameWithoutExtension(p.Path);
                var ext = Path.GetExtension(p.Path);
                var target = Path.Combine(desktop, name + ext);
                for (int n = 2; File.Exists(target); n++) target = Path.Combine(desktop, name + " (" + n + ")" + ext);
                File.Move(p.Path, target);
                Drop(p, true);
            }
            catch (Exception e) { Log.Write("desktop: " + e.Message); System.Media.SystemSounds.Beep.Play(); }
        }

        public void DragEnded()
        {
            // Dropped into a folder: it now lives there. Explorer may finish
            // the move a moment later, so look again then.
            Prune();
            After(0.6, Prune);
            After(1.5, Prune);
        }

        public ContextMenu MenuFor(Pegged p)
        {
            var menu = new ContextMenu();
            Action<string, Action> add = (title, act) =>
            {
                var item = new MenuItem { Header = title };
                item.Click += (s, e) => act();
                menu.Items.Add(item);
            };
            add(T.L("Copy", "Copiar"), () => Copy(p));
            add(T.L("Open", "Abrir"), () => Open(p));
            add(T.L("Edit in Paint", "Editar en Paint"), () => Edit(p));
            add(T.L("Show in Explorer", "Mostrar en el Explorador"), () => ShowInExplorer(p));
            bool inbox = Paths.InInbox(p.Path);
            if (inbox) add(T.L("Save to Desktop", "Guardar en el Escritorio"), () => SaveToDesktop(p));
            menu.Items.Add(new Separator());
            if (inbox) add(T.L("Discard", "Descartar"), () => Discard(p));
            else
            {
                add(T.L("Take down", "Descolgar"), () => Drop(p));
                add(T.L("Move to Recycle Bin", "Mover a la Papelera"), () => Trash(p));
            }
            return menu;
        }

        // MARK: Clipboard captures (PrtScn alone, or any tool that only copies)

        static readonly string[] snippingTools = { "SnippingTool", "ScreenClippingHost", "ScreenSketch" };

        void OnClipboard()
        {
            if (DateTime.Now < ignoreClipboardUntil) return;
            // Images copied by the Snipping Tool always hang, so "Copy" works
            // like "Save". Images copied from other apps only with the option.
            var owner = Native.ClipboardOwnerProcess();
            bool snip = owner != null && snippingTools.Any(n => n.Equals(owner, StringComparison.OrdinalIgnoreCase));
            if (!snip && !ClipboardCaptures) return;
            After(0.15, () =>
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        if (Forms.Clipboard.ContainsFileDropList() || !Forms.Clipboard.ContainsImage()) return;
                        Directory.CreateDirectory(Paths.Inbox);
                        var target = Path.Combine(Paths.Inbox, T.L("Capture ", "Captura ") + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png");
                        for (int n = 2; File.Exists(target); n++) target = target.Substring(0, target.Length - 4) + " (" + n + ").png";
                        var png = Forms.Clipboard.GetData("PNG") as MemoryStream;
                        if (png != null) File.WriteAllBytes(target, png.ToArray());
                        else using (var img = Forms.Clipboard.GetImage()) img.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                        HangCapture(target);
                        return;
                    }
                    catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(80); }
                    catch (Exception e) { Log.Write("clipboard (" + owner + "): " + e.Message); return; }
                }
            });
        }

        /// Clipboard captures nobody kept go to the Recycle Bin after a week.
        void CleanInbox()
        {
            try
            {
                var pegged = view.Items.Select(i => Path.GetFullPath(i.Path)).ToList();
                foreach (var f in new DirectoryInfo(Paths.Inbox).GetFiles())
                {
                    if (f.LastWriteTime > DateTime.Now.AddDays(-7)) continue;
                    if (pegged.Any(p => string.Equals(p, f.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.FullName,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }
            catch (Exception e) { Log.Write("clean: " + e.Message); }
        }

        // MARK: Showing and hiding

        bool Blocked { get { return Native.FullScreenOn(win.Screen, win.Handle); } }

        void Refresh()
        {
            if (!wanted || Blocked) SetRevealed(false);
            // The pointer is watched while there is a line, even tucked away,
            // to notice it resting against the top edge.
            if (wanted) mouse.Start(); else { mouse.Stop(); win.SetClickThrough(true); }
        }

        void Reveal(bool pin = false, double peekSeconds = 0)
        {
            if (!wanted || Blocked) return;
            if (pin) pinned = true;
            if (peekSeconds > 0) peekUntil = DateTime.Now.AddSeconds(peekSeconds);
            awaySince = null;
            SetRevealed(true);
        }

        void SetRevealed(bool on)
        {
            if (on == revealed) return;
            revealed = on;
            if (on)
            {
                if (!win.IsVisible) { win.Show(); win.Place(); }
                win.BringToTop();
            }
            else
            {
                pinned = false;
                peekUntil = DateTime.MinValue;
                win.SetClickThrough(true);
                view.ClearHover();
            }
            view.SetRevealed(on);
        }

        void Toggle()
        {
            if (revealed)
            {
                SetRevealed(false);
                if (LiveCount == 0) { keepOpen = false; wanted = false; Refresh(); }
            }
            else
            {
                keepOpen = true;
                wanted = true;
                win.Place();
                Refresh();
                Reveal(pin: true);
            }
        }

        void Tick()
        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return;
            var now = DateTime.Now;
            var screen = win.Screen;
            var b = screen.Bounds;
            bool onScreen = pt.X >= b.Left && pt.X < b.Right && pt.Y >= b.Top && pt.Y < b.Bottom;
            // The top edge of the primary screen, plus a taskbar docked up there.
            int band = Math.Max(2, screen.WorkingArea.Top - b.Top);
            bool inBand = onScreen && pt.Y < b.Top + band;
            bool buttonDown = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;

            // A click up there (a browser tab, a title bar) puts the line away
            // and keeps it away until the pointer leaves the edge.
            if (inBand && buttonDown)
            {
                suppressed = true;
                hotSince = null;
                if (revealed && !view.IsDragging) SetRevealed(false);
            }
            if (!inBand) suppressed = false;

            if (!revealed)
            {
                if (inBand && !suppressed && !Blocked)
                {
                    if (hotSince == null) hotSince = now;
                    if ((now - hotSince.Value).TotalSeconds >= RevealDelay)
                    {
                        hotSince = null;
                        Reveal();
                    }
                }
                else hotSince = null;
                return;
            }

            var local = win.ToLocal(pt);
            if (!view.IsDragging)
            {
                view.SetPointer(local);
                bool overPhoto = onScreen && view.CardAt(local, 4) != null;
                win.SetClickThrough(!overPhoto && !view.MenuOpen);
            }

            // The line's zone runs from its lowest point up to the top of the
            // screen, so moving up never hides it.
            bool inside = onScreen && local.Y <= Layout.PanelHeight;
            if (inside && pinned) pinned = false;
            bool busy = pinned || view.IsDragging || view.IsPressed || view.MenuOpen || now < peekUntil;
            if (inside || busy) awaySince = null;
            else
            {
                if (awaySince == null) awaySince = now;
                if ((now - awaySince.Value).TotalSeconds >= RetractDelay)
                {
                    awaySince = null;
                    SetRevealed(false);
                }
            }
        }

        // MARK: Tray

        void SetUpTray()
        {
            using (var ms = new MemoryStream(Art.Ico(true, Settings.DarkTaskbar, 16, 20, 24, 32, 40, 48)))
                tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(ms, Forms.SystemInformation.SmallIconSize), Text = "Cordel", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Toggle(); };
            menu.Opening += (s, e) => BuildMenu(menu);
            BuildMenu(menu);
        }

        void BuildMenu(Forms.ContextMenuStrip menu)
        {
            menu.Items.Clear();
            Func<string, Action, Forms.ToolStripMenuItem> add = (title, act) =>
            {
                var item = new Forms.ToolStripMenuItem(title);
                item.Click += (s, e) => act();
                menu.Items.Add(item);
                return item;
            };
            var toggle = add(revealed ? T.L("Hide line", "Ocultar cordel") : T.L("Show line", "Mostrar cordel"), Toggle);
            toggle.ShortcutKeyDisplayString = "Ctrl+Alt+T";
            add(T.L("Take everything down", "Descolgar todo"), Clear).Enabled = LiveCount > 0;
            var clip = add(T.L("Also hang images copied from other apps", "Colgar también imágenes copiadas de otras apps"), () => Settings.Set("clipboard", !ClipboardCaptures));
            clip.Checked = ClipboardCaptures;
            clip.ToolTipText = T.L("For PrtScn without saving, or tools that only copy to the clipboard",
                "Para ImprPant sin guardar, o herramientas que solo copian al portapapeles");
            add(T.L("Open screenshots folder", "Abrir carpeta de capturas"), () => Shell(watcher.Folder, null));
            menu.Items.Add(new Forms.ToolStripSeparator());
            add(T.L("Sounds", "Sonidos"), () => { Sound.On = !Sound.On; Settings.Set("soundOff", !Sound.On); }).Checked = Sound.On;
            add(T.L("Start with Windows", "Iniciar con Windows"), () =>
            {
                try { Settings.StartsWithWindows = !Settings.StartsWithWindows; }
                catch (Exception e) { Log.Write("startup: " + e.Message); System.Media.SystemSounds.Beep.Play(); }
            }).Checked = Settings.StartsWithWindows;
            menu.Items.Add(new Forms.ToolStripSeparator());
            add(T.L("Quit Cordel", "Salir de Cordel"), () => Application.Current.Shutdown());
        }
    }
}
