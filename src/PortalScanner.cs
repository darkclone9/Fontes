using System;
using System.Collections.Generic;
using System.IO;

namespace Fontes
{
    /// <summary>Scans a folder for its entries (files + subfolders) to populate a folder
    /// portal fence — Fences' "folder portal mirrors a directory" behaviour (_szFolder / get_IsFolderView).
    /// Supports in-place subfolder navigation with a parent ("..") navigation item.</summary>
    public static class PortalScanner
    {
        public static List<ShortcutItem> ScanFolder(string folder, string rootPortalFolder = null)
        {
            var items = new List<ShortcutItem>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return items;
            try
            {
                string normFolder = Path.GetFullPath(folder).TrimEnd('\\');
                string normRoot = !string.IsNullOrEmpty(rootPortalFolder)
                    ? Path.GetFullPath(rootPortalFolder).TrimEnd('\\')
                    : normFolder;

                if (!string.Equals(normFolder, normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Directory.GetParent(normFolder);
                    if (parent != null)
                    {
                        items.Add(new ShortcutItem
                        {
                            Name = ".. (Up)",
                            Path = parent.FullName,
                            Target = parent.FullName,
                            IsFolderPortal = true,
                            IsParentNavigation = true,
                            Category = "Folder"
                        });
                    }
                }

                var subItems = new List<ShortcutItem>();
                foreach (var d in Directory.EnumerateDirectories(folder))
                {
                    var di = new DirectoryInfo(d);
                    if ((di.Attributes & FileAttributes.Hidden) != 0) continue;
                    subItems.Add(new ShortcutItem
                    {
                        Name = Path.GetFileName(d),
                        Path = d,
                        Target = d,
                        IsFolderPortal = true,
                        Category = "Folder",
                        LastModified = di.LastWriteTime,
                        DateAdded = di.CreationTime
                    });
                }
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var fi = new FileInfo(f);
                    if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;
                    var it = ShortcutResolver.FromFile(f);
                    it.Category = RuleEngine.Classify(it);
                    subItems.Add(it);
                }
                subItems.Sort((a, b) => string.Compare((a.IsFolderPortal ? "0-" : "1-") + a.DisplayText,
                                                       (b.IsFolderPortal ? "0-" : "1-") + b.DisplayText,
                                                       StringComparison.OrdinalIgnoreCase));
                items.AddRange(subItems);
            }
            catch { }
            return items;
        }
    }
}
