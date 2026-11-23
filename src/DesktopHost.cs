using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Fontes
{
    /// <summary>Embeds the Fontes form INTO the Windows desktop shell or pins it to the bottom of the
    /// desktop Z-order (HWND_BOTTOM) with WS_EX_TOOLWINDOW so it never shows on the taskbar or Alt+Tab
    /// and never floats over normal apps (like Clip Studio Paint) unless Peek-on-Top (Ctrl+Alt+Space) is active.</summary>
    public static class DesktopHost
    {
        const uint WM_SPAWN_WORKER = 0x052C;
        const uint SMTO_NORMAL = 0x0000;
        const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        const int WS_CHILD = 0x40000000;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_CAPTION = 0x00C00000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        const uint SPI_GETDESKWALLPAPER = 0x0073;

        public static readonly IntPtr HWND_TOP = new IntPtr(0);
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_HOTKEY = 0x0312;
        public const int HOTKEY_ID_PEEK = 0xF001;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        public static bool IsEmbedded { get; private set; }

        static Bitmap _cachedWallpaper;
        static Bitmap _cachedFrostedWallpaper;
        static Size _cachedWallpaperSize;
        static Rectangle _cachedMonitorRect;
        static string _cachedWallpaperPath;
        static DateTime _cachedWallpaperWriteTime;

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>Mirrors DesktopDock64.dll's FindDesktopWindowSlow: finds the WorkerW or Progman
        /// window that hosts SHELLDLL_DefView so we can anchor neatly to the desktop.</summary>
        public static IntPtr FindDesktopHostWindow()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr result;
                SendMessageTimeout(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out result);
            }

            IntPtr defViewParent = IntPtr.Zero;
            IntPtr workerWBehind = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                IntPtr shellView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    defViewParent = hWnd;
                    workerWBehind = FindWindowEx(IntPtr.Zero, hWnd, "WorkerW", null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (defViewParent != IntPtr.Zero) return defViewParent;
            if (workerWBehind != IntPtr.Zero) return workerWBehind;
            return progman;
        }

        /// <summary>Positions the form on the primary desktop working area and pins it to HWND_BOTTOM
        /// so it stays behind normal application windows (or embeds into Progman/WorkerW when embedIntoShell is true).</summary>
        public static void Embed(Form form, bool embedIntoShell = false)
        {
            IntPtr h = form.Handle;
            if (h == IntPtr.Zero) return;

            var vs = VirtualScreen();
            if (embedIntoShell)
            {
                IntPtr host = FindDesktopHostWindow();
                if (host != IntPtr.Zero)
                {
                    IntPtr style = GetWindowLongPtr64(h, GWL_STYLE);
                    long s = style.ToInt64();
                    s &= ~WS_POPUP; s &= ~WS_CAPTION; s |= WS_CHILD;
                    SetWindowLongPtr64(h, GWL_STYLE, new IntPtr(s));
                    SetParent(h, host);
                    MoveWindow(h, 0, 0, vs.Width, vs.Height, true);
                    SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    IsEmbedded = true;
                    return;
                }
            }

            if (IsEmbedded)
            {
                SetParent(h, IntPtr.Zero);
                IntPtr style = GetWindowLongPtr64(h, GWL_STYLE);
                long s = style.ToInt64();
                s &= ~WS_CHILD; s |= WS_POPUP;
                SetWindowLongPtr64(h, GWL_STYLE, new IntPtr(s));
                IsEmbedded = false;
            }

            // Ensure WS_EX_TOOLWINDOW is set so Windows never puts a button on the taskbar or in Alt+Tab
            try
            {
                long ex = GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64();
                ex |= WS_EX_TOOLWINDOW;
                SetWindowLongPtr64(h, GWL_EXSTYLE, new IntPtr(ex));
            }
            catch { }

            var work = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : vs;
            MoveWindow(h, work.Left, work.Top, work.Width, work.Height, true);
            SendToDesktopBottom(h);
        }

        /// <summary>Pins the window to the bottom of the Z-order (directly on top of the desktop shell,
        /// behind all standard application windows).</summary>
        public static void SendToDesktopBottom(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || IsEmbedded) return;
            SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        /// <summary>Toggles Peek-on-Top mode (bringing fences above active windows temporarily when
        /// the user presses Ctrl+Alt+Space, or dropping them back to HWND_BOTTOM).</summary>
        public static void SetPeekTopMost(IntPtr hWnd, bool peekOnTop)
        {
            if (hWnd == IntPtr.Zero || IsEmbedded) return;
            if (peekOnTop)
            {
                SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);
            }
            else
            {
                SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                SendToDesktopBottom(hWnd);
            }
        }

        public static void RegisterPeekHotkeys(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { RegisterHotKey(handle, HOTKEY_ID_PEEK, MOD_CONTROL | MOD_ALT, (uint)Keys.Space); } catch { }
        }

        public static void UnregisterPeekHotkeys(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { UnregisterHotKey(handle, HOTKEY_ID_PEEK); } catch { }
        }

        /// <summary>Reads the user's current Windows desktop wallpaper and extracts the exact slice
        /// corresponding to the target monitor (handling multi-monitor VirtualScreen composite images
        /// generated by Wallpaper Engine or Windows Span mode without black void bars).</summary>
        public static Bitmap GetDesktopWallpaper(Size targetSize, Rectangle? monitorScreenBounds = null)
        {
            if (targetSize.Width <= 0 || targetSize.Height <= 0) return null;
            string path = GetWallpaperPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            Rectangle vs = SystemInformation.VirtualScreen;
            Rectangle monRect = monitorScreenBounds ?? (Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : vs);
            DateTime writeTime = DateTime.MinValue;
            try { writeTime = File.GetLastWriteTimeUtc(path); } catch { }

            if (_cachedWallpaper != null &&
                _cachedWallpaperSize == targetSize &&
                _cachedMonitorRect == monRect &&
                _cachedWallpaperWriteTime == writeTime &&
                string.Equals(_cachedWallpaperPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedWallpaper;
            }

            try
            {
                using (var src = Image.FromFile(path))
                {
                    var bmp = new Bitmap(targetSize.Width, targetSize.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        Rectangle primaryBounds = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds : vs;
                        bool isMultiMonitor = Screen.AllScreens.Length > 1 &&
                                              (vs.Width > primaryBounds.Width || vs.Height > primaryBounds.Height);
                        float srcAspect = (float)src.Width / Math.Max(1, src.Height);
                        float vsAspect = (float)vs.Width / Math.Max(1, vs.Height);
                        float monAspect = (float)primaryBounds.Width / Math.Max(1, primaryBounds.Height);

                        bool isVirtualComposite = isMultiMonitor &&
                            ((Math.Abs(src.Width - vs.Width) <= 32 && Math.Abs(src.Height - vs.Height) <= 32) ||
                             (Math.Abs(srcAspect - vsAspect) < Math.Abs(srcAspect - monAspect) && Math.Abs(srcAspect - vsAspect) < 0.12f));

                        if (isVirtualComposite)
                        {
                            float scaleX = (float)src.Width / Math.Max(1, vs.Width);
                            float scaleY = (float)src.Height / Math.Max(1, vs.Height);
                            float sx = Math.Max(0f, (monRect.X - vs.X) * scaleX);
                            float sy = Math.Max(0f, (monRect.Y - vs.Y) * scaleY);
                            float sw = Math.Min(src.Width - sx, monRect.Width * scaleX);
                            float sh = Math.Min(src.Height - sy, monRect.Height * scaleY);

                            g.DrawImage(src,
                                new Rectangle(0, 0, targetSize.Width, targetSize.Height),
                                new RectangleF(sx, sy, Math.Max(1f, sw), Math.Max(1f, sh)),
                                GraphicsUnit.Pixel);
                        }
                        else
                        {
                            float scale = Math.Max((float)targetSize.Width / src.Width, (float)targetSize.Height / src.Height);
                            int dw = (int)(src.Width * scale);
                            int dh = (int)(src.Height * scale);
                            int dx = (targetSize.Width - dw) / 2;
                            int dy = (targetSize.Height - dh) / 2;
                            g.DrawImage(src, new Rectangle(dx, dy, dw, dh));
                        }
                    }

                    if (_cachedWallpaper != null) _cachedWallpaper.Dispose();
                    if (_cachedFrostedWallpaper != null)
                    {
                        _cachedFrostedWallpaper.Dispose();
                        _cachedFrostedWallpaper = null;
                    }
                    _cachedWallpaper = bmp;
                    _cachedWallpaperSize = targetSize;
                    _cachedMonitorRect = monRect;
                    _cachedWallpaperPath = path;
                    _cachedWallpaperWriteTime = writeTime;
                    return _cachedWallpaper;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Returns a soft frosted-glass blurred version of the monitor's cropped wallpaper
        /// for rendering inside translucent fence cards.</summary>
        public static Bitmap GetFrostedDesktopWallpaper(Size targetSize, Rectangle? monitorScreenBounds = null)
        {
            var baseWp = GetDesktopWallpaper(targetSize, monitorScreenBounds);
            if (baseWp == null) return null;
            if (_cachedFrostedWallpaper != null) return _cachedFrostedWallpaper;

            try
            {
                int smallW = Math.Max(32, targetSize.Width / 7);
                int smallH = Math.Max(32, targetSize.Height / 7);
                using (var pass1 = new Bitmap(smallW, smallH))
                {
                    using (var g1 = Graphics.FromImage(pass1))
                    {
                        g1.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g1.DrawImage(baseWp, new Rectangle(0, 0, smallW, smallH));
                    }

                    var frosted = new Bitmap(targetSize.Width, targetSize.Height);
                    using (var g2 = Graphics.FromImage(frosted))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(pass1, new Rectangle(0, 0, targetSize.Width, targetSize.Height));
                    }
                    _cachedFrostedWallpaper = frosted;
                    return _cachedFrostedWallpaper;
                }
            }
            catch
            {
                return baseWp;
            }
        }

        public static string GetWallpaperPath()
        {
            try
            {
                var sb = new StringBuilder(512);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0 && File.Exists(sb.ToString()))
                    return sb.ToString();
            }
            catch { }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    var val = key?.GetValue("WallPaper") as string;
                    if (!string.IsNullOrEmpty(val) && File.Exists(val)) return val;
                }
            }
            catch { }

            string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(transcoded)) return transcoded;
            return null;
        }

        /// <summary>Mirrors DesktopDock64.dll's GetResolutionFingerprintEx: produces a deterministic
        /// fingerprint of the active display configuration for layout persistence.</summary>
        public static string GetResolutionFingerprint()
        {
            try
            {
                var screens = Screen.AllScreens;
                var sb = new StringBuilder();
                sb.Append("Count").Append(screens.Length);
                foreach (var s in screens)
                {
                    sb.Append("_").Append(s.Primary ? "P" : "S")
                      .Append(s.Bounds.Width).Append("x").Append(s.Bounds.Height)
                      .Append("@").Append(s.Bounds.X).Append(",").Append(s.Bounds.Y);
                }
                return sb.ToString();
            }
            catch
            {
                var vs = SystemInformation.VirtualScreen;
                return string.Format("VS_{0}x{1}", vs.Width, vs.Height);
            }
        }

        public static Rectangle VirtualScreen() =>
            Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : SystemInformation.VirtualScreen;
    }
}
