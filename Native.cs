using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Forms = System.Windows.Forms;

namespace Cordel
{
    /// Win32 bits WPF does not expose.
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
        [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr pidl);
        [DllImport("shell32.dll")] static extern void ILFree(IntPtr pidl);
        [DllImport("shell32.dll")] static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, IntPtr inner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        public static string ClipboardOwnerProcess()
        {
            var h = GetClipboardOwner();
            if (h == IntPtr.Zero) return null;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return null; }
        }

        public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
        public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_CAPTION = 0x00C00000;
        public const uint SWP_NOACTIVATE = 0x10, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public static Forms.Screen Primary { get { return Forms.Screen.PrimaryScreen; } }

        public static double ScaleOf(Forms.Screen s)
        {
            var c = new POINT { X = s.Bounds.Left + s.Bounds.Width / 2, Y = s.Bounds.Top + s.Bounds.Height / 2 };
            uint x, y;
            try { if (GetDpiForMonitor(MonitorFromPoint(c, 2), 0, out x, out y) == 0) return x / 96.0; } catch { }
            return 1;
        }

        public static string KnownFolder(Guid id)
        {
            IntPtr p;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }

        /// Whether the app in front fills the whole screen without a title bar:
        /// a video, a game or a presentation. The line stays out of its way.
        public static bool FullScreenOn(Forms.Screen s, IntPtr self)
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || h == self) return false;
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            var cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            var b = s.Bounds;
            if (!(r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom)) return false;
            long style = (long)GetWindowLongPtr(h, GWL_STYLE);
            return (style & WS_CAPTION) != WS_CAPTION;
        }

        /// The same data object Explorer builds when you drag a file, so every
        /// target, the Recycle Bin included, treats it as a real file.
        public static IDataObject ShellDataObject(string path)
        {
            IntPtr folder = IntPtr.Zero, full = IntPtr.Zero;
            uint o;
            try
            {
                if (SHParseDisplayName(System.IO.Path.GetDirectoryName(path), IntPtr.Zero, out folder, 0, out o) != 0) return null;
                if (SHParseDisplayName(path, IntPtr.Zero, out full, 0, out o) != 0) return null;
                var iid = new Guid("0000010e-0000-0000-C000-000000000046");
                object obj;
                if (SHCreateDataObject(folder, 1, new[] { ILFindLastID(full) }, IntPtr.Zero, ref iid, out obj) != 0) return null;
                return obj as IDataObject;
            }
            finally
            {
                if (folder != IntPtr.Zero) ILFree(folder);
                if (full != IntPtr.Zero) ILFree(full);
            }
        }

        [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDragSourceHelper
        {
            void InitializeFromBitmap(ref SHDRAGIMAGE img, IDataObject data);
            void InitializeFromWindow(IntPtr hwnd, ref POINT pt, IDataObject data);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SHDRAGIMAGE { public int cx, cy, ox, oy; public IntPtr hbmp; public int crColorKey; }

        /// The photo follows the pointer during the drag, as Explorer does.
        public static void AttachDragImage(IDataObject data, System.Drawing.Bitmap bmp)
        {
            var helper = (IDragSourceHelper)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0")));
            var img = new SHDRAGIMAGE
            {
                cx = bmp.Width, cy = bmp.Height, ox = bmp.Width / 2, oy = bmp.Height / 2,
                hbmp = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0)), crColorKey = unchecked((int)0xFFFFFFFF),
            };
            helper.InitializeFromBitmap(ref img, data);
        }
    }
}
