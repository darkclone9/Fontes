using System;
using System.Collections.Generic;
using System.IO;

namespace Fontes
{
    /// <summary>Scans the real User + Public desktop folders for shortcuts (.lnk / .url),
    /// folders, loose files, and the built-in Windows Recycle Bin, resolving each into a
    /// ShortcutItem. This is the source of "organize desktop into fences", Fences-style.</summary>
    public static class DesktopScanner
    {
        public static List<ShortcutItem> Scan()
        {
            var items = new List<ShortcutItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Built-in Windows Recycle Bin desktop item
            var recycleBin = new ShortcutItem
            {
                Name = "Recycle Bin",
                Path = "shell:RecycleBinFolder",
                Target = "shell:RecycleBinFolder",
                Category = "Folders",
                IsFolderPortal = false
            };
            items.Add(recycleBin);
            seen.Add(recycleBin.Path);

            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            Collect(items, seen, userDesktop);
            Collect(items, seen, publicDesktop);
            items.Sort((a, b) => string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase));
            return items;
        }

        public static bool ShouldIncludeDirectory(string dirPath)
        {
            if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return false;
            try
            {
                var di = new DirectoryInfo(dirPath);
                if ((di.Attributes & FileAttributes.Hidden) != 0 || (di.Attributes & FileAttributes.System) != 0)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool ShouldIncludeFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
            try
            {
                var fi = new FileInfo(filePath);
                if ((fi.Attributes & FileAttributes.Hidden) != 0 || (fi.Attributes & FileAttributes.System) != 0)
                    return false;

                string name = fi.Name;
                string ext = fi.Extension;
                if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void Collect(List<ShortcutItem> into, HashSet<string> seen, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    if (!ShouldIncludeDirectory(d)) continue;
                    string key;
                    DirectoryInfo di = null;
                    try
                    {
                        di = new DirectoryInfo(d);
                        key = di.FullName;
                    }
                    catch
                    {
                        key = d;
                    }
                    if (seen.Contains(key)) continue;
                    seen.Add(key);

                    var dirItem = new ShortcutItem
                    {
                        Name = di != null ? di.Name : Path.GetFileName(d),
                        Path = key,
                        Target = key,
                        WorkingDir = dir,
                        IsFolderPortal = false,
                        DateAdded = di != null ? di.CreationTime : DateTime.Now,
                        LastModified = di != null ? di.LastWriteTime : DateTime.Now
                    };
                    dirItem.Category = RuleEngine.Classify(dirItem);
                    into.Add(dirItem);
                }
            }
            catch { /* skip unreadable dirs */ }

            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (!ShouldIncludeFile(f)) continue;

                    string key;
                    try { key = new FileInfo(f).FullName; } catch { key = f; }
                    if (seen.Contains(key)) continue;
                    seen.Add(key);

                    var it = ShortcutResolver.FromFile(f);
                    it.Category = RuleEngine.Classify(it);
                    into.Add(it);
                }
            }
            catch { /* skip unreadable dirs */ }
        }
    }
}