using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Fontes
{
    /// <summary>Extracts real Windows Shell icons via SHGetFileInfo and ExtractIconEx
    /// for folders, .url internet/game shortcuts, and indexed .exe/.dll icon resources.</summary>
    public static class WinIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_LARGEICON = 0x0;
        const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        public static Icon Folder()
        {
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
                finally { DestroyIcon(info.hIcon); }
            }
            return SystemIcons.Application;
        }

        /// <summary>Returns the Windows Shell icon for an existing file or folder path.</summary>
        public static Icon ForPath(string path)
        {
            if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
                return null;
            try
            {
                var info = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(path, 0, ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
                if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
                    finally { DestroyIcon(info.hIcon); }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Returns the Windows Shell icon associated with .url / .html web shortcuts
        /// (never SystemIcons.Shield).</summary>
        public static Icon UrlIcon()
        {
            foreach (var dummyExt in new[] { "shortcut.html", "shortcut.url" })
            {
                try
                {
                    var info = new SHFILEINFO();
                    IntPtr r = SHGetFileInfo(dummyExt, FILE_ATTRIBUTE_NORMAL, ref info,
                        (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                    if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                    {
                        try { return (Icon)Icon.FromHandle(info.hIcon).Clone(); }
                        finally { DestroyIcon(info.hIcon); }
                    }
                }
                catch { }
            }
            return SystemIcons.Application;
        }

        /// <summary>Extracts a specific icon index (positive 0-based index or negative resource ID)
        /// from an executable, DLL, or ICO file via ExtractIconEx.</summary>
        public static Icon ExtractIndexed(string filePath, int iconIndex)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;
            IntPtr[] large = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            try
            {
                uint count = ExtractIconEx(filePath, iconIndex, large, small, 1);
                if (count > 0)
                {
                    IntPtr handle = large[0] != IntPtr.Zero ? large[0] : small[0];
                    if (handle != IntPtr.Zero)
                        return (Icon)Icon.FromHandle(handle).Clone();
                }
            }
            catch { }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
            return null;
        }
    }
}
