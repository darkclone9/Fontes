using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Fontes
{
    /// <summary>Resolves .lnk / .url / loose files into a launch Target, Arguments, WorkingDir,
    /// IconLocation, IconIndex, and timestamps. Mirrors DesktopDock64.dll's Shell_GetLnkPath /
    /// Shell_GetUrlPath and Fences.exe's DesktopDockUI.Simulation.IconElement.ResolveLink.</summary>
    public static class ShortcutResolver
    {
        public static ShortcutItem FromFile(string path)
        {
            bool isDir = Directory.Exists(path);
            var it = new ShortcutItem
            {
                Path = path,
                Name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
                IsFolderPortal = isDir
            };

            try
            {
                if (isDir)
                {
                    var di = new DirectoryInfo(path);
                    it.LastModified = di.LastWriteTime;
                    it.DateAdded = di.CreationTime;
                    it.Target = path;
                    return it;
                }

                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    it.LastModified = fi.LastWriteTime;
                    it.DateAdded = fi.CreationTime;
                }

                string ext = Path.GetExtension(path);
                if (string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase))
                    ResolveUrl(it);
                else if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
                    ResolveLnk(it);
                else
                {
                    it.Target = path;
                    it.Arguments = null;
                    it.WorkingDir = Path.GetDirectoryName(path);
                }
            }
            catch { }
            return it;
        }

        public static void ResolveUrl(ShortcutItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Path) || !File.Exists(it.Path)) return;
            foreach (var rawLine in File.ReadAllLines(it.Path))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    it.Target = line.Substring(4).Trim();
                }
                else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    string rawIcon = line.Substring(9).Trim().Trim('"');
                    it.IconLocation = Environment.ExpandEnvironmentVariables(rawIcon);
                }
                else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                {
                    int idx;
                    if (int.TryParse(line.Substring(10).Trim(), out idx))
                        it.IconIndex = idx;
                }
            }
        }

        static void ResolveLnk(ShortcutItem it)
        {
            IShellLinkW link = (IShellLinkW)new ShellLink();
            try
            {
                IPersistFile pf = (IPersistFile)link;
                pf.Load(it.Path, 0);

                StringBuilder pathBuf = new StringBuilder(1024);
                if (link.GetPath(pathBuf, pathBuf.Capacity, IntPtr.Zero, 0) == 0 && pathBuf.Length > 0)
                    it.Target = pathBuf.ToString();

                StringBuilder argsBuf = new StringBuilder(1024);
                link.GetArguments(argsBuf, argsBuf.Capacity);
                if (argsBuf.Length > 0)
                    it.Arguments = argsBuf.ToString();

                StringBuilder dirBuf = new StringBuilder(1024);
                link.GetWorkingDirectory(dirBuf, dirBuf.Capacity);
                if (dirBuf.Length > 0)
                    it.WorkingDir = dirBuf.ToString();
                else if (!string.IsNullOrEmpty(it.Target) && File.Exists(it.Target))
                    it.WorkingDir = Path.GetDirectoryName(it.Target);

                StringBuilder iconBuf = new StringBuilder(1024);
                int iconIdx;
                link.GetIconLocation(iconBuf, iconBuf.Capacity, out iconIdx);
                it.IconIndex = iconIdx;
                if (iconBuf.Length > 0)
                {
                    string expanded = Environment.ExpandEnvironmentVariables(iconBuf.ToString().Trim().Trim('"'));
                    if (File.Exists(expanded))
                        it.IconLocation = expanded;
                }
            }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            }
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] // CLSID_ShellLink
    public class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")] // IID_IShellLinkW
    public interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010B-0000-0000-C000-000000000046")] // IPersistFile
    public interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }
}
