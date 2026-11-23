using System;
using System.Collections.Generic;
using System.IO;

namespace Fontes
{
    /// <summary>Scans the real User + Public desktop folders for shortcuts (.lnk / .url)
    /// and loose files, resolves each into a ShortcutItem. This is the source of
    /// "organize desktop into fences", Fences-style.</summary>
    public static class DesktopScanner
    {
        public static List<ShortcutItem> Scan()
        {
            var items = new List<ShortcutItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

            Collect(items, seen, userDesktop);
            Collect(items, seen, publicDesktop);
            items.Sort((a, b) => string.Compare(a.DisplayText, b.DisplayText, StringComparison.OrdinalIgnoreCase));
            return items;
        }

        static void Collect(List<ShortcutItem> into, HashSet<string> seen, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f);
                    bool relevant = string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase);
                    if (!relevant) continue;

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