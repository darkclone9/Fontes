using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Fontes
{
    /// <summary>A user-configurable organization rule modeled after Stardock Fences'
    /// DesktopDockUI.Simulation.Rule and Criteria_Type, Criteria_Name, Criteria_Target,
    /// Criteria_Modified, and Criteria_TimesOpened.</summary>
    public class FenceRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New Rule";
        public bool Enabled { get; set; } = true;
        public string TargetFenceTitle { get; set; } = "Apps";
        public string ExtensionFilter { get; set; } = "";   // comma-separated e.g. ".exe,.lnk" or blank
        public string NamePattern { get; set; } = "";       // substring / wildcard match on DisplayText
        public string TargetPattern { get; set; } = "";     // substring match on Target path
        public int? MaxAgeDays { get; set; }                // Criteria_Modified: modified within N days
        public int? MinTimesOpened { get; set; }            // Criteria_TimesOpened: opened at least N times

        public bool Matches(ShortcutItem it)
        {
            if (!Enabled || it == null) return false;
            bool hasAnyCriterion = false;

            if (!string.IsNullOrWhiteSpace(ExtensionFilter))
            {
                hasAnyCriterion = true;
                var exts = ExtensionFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string itemExt = it.TargetExtension ?? it.PathExtension ?? "";
                string rawExt = it.PathExtension ?? "";
                bool extMatch = exts.Any(e =>
                {
                    string clean = e.Trim();
                    if (!clean.StartsWith(".")) clean = "." + clean;
                    return string.Equals(itemExt, clean, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(rawExt, clean, StringComparison.OrdinalIgnoreCase);
                });
                if (!extMatch) return false;
            }

            if (!string.IsNullOrWhiteSpace(NamePattern))
            {
                hasAnyCriterion = true;
                string name = it.DisplayText ?? "";
                if (name.IndexOf(NamePattern.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(TargetPattern))
            {
                hasAnyCriterion = true;
                string target = (it.Target ?? "") + " " + (it.Path ?? "");
                if (target.IndexOf(TargetPattern.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (MaxAgeDays.HasValue && MaxAgeDays.Value > 0)
            {
                hasAnyCriterion = true;
                if ((DateTime.Now - it.LastModified).TotalDays > MaxAgeDays.Value)
                    return false;
            }

            if (MinTimesOpened.HasValue && MinTimesOpened.Value > 0)
            {
                hasAnyCriterion = true;
                if (it.TimesOpened < MinTimesOpened.Value)
                    return false;
            }

            return hasAnyCriterion;
        }
    }

    /// <summary>The auto-organize rule engine — Fences' DesktopDockUI.Simulation.RuleSettings analogue.
    /// Evaluates custom multi-criteria FenceRules first, then falls back to built-in category heuristics.</summary>
    public class RuleEngine
    {
        public static List<FenceRule> CustomRules { get; set; } = new List<FenceRule>();

        public static readonly string[] GameExts   = { ".exe", ".bat", ".cmd", ".com", ".scr", ".url" };
        public static readonly string[] MediaExts  = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".webm" };
        public static readonly string[] AudioExts  = { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" };
        public static readonly string[] ImageExts  = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".tiff" };
        public static readonly string[] DocExts    = { ".txt", ".pdf", ".doc", ".docx", ".rtf", ".odt", ".md", ".csv", ".xls", ".xlsx", ".ppt", ".pptx" };

        private static readonly string[] GameKeywords =
            { "steam", "epic", "battle.net", "blizzard", "gog", "playnite", "origin", "ubisoft", "xbox", "launcher",
              "fallout", "skyrim", "cyberpunk", "witcher", "elden", "rimworld", "minecraft", "wow", "warcraft", "godot" };

        /// <summary>Return a category / target fence label for the item.</summary>
        public static string Classify(ShortcutItem it)
        {
            if (it == null) return "Misc";

            // 1. Evaluate custom rules first (DesktopDockUI.Simulation.RuleSettings)
            if (CustomRules != null)
            {
                foreach (var rule in CustomRules)
                {
                    if (rule.Matches(it) && !string.IsNullOrWhiteSpace(rule.TargetFenceTitle))
                        return rule.TargetFenceTitle.Trim();
                }
            }

            // 2. Built-in category classification
            if (it.IsFolderPortal || (!string.IsNullOrEmpty(it.Path) && Directory.Exists(it.Path)))
                return "Folders";

            var ext = !string.IsNullOrEmpty(it.TargetExtension) ? it.TargetExtension : it.PathExtension;
            var name = (it.Name ?? "").ToLowerInvariant();
            var target = (it.Target ?? "").ToLowerInvariant();

            if (it.IsUrl && ! target.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) return "Web";
            if (ContainsAny(target + " " + name, GameKeywords)) return "Games";
            if (ContainsAnyExt(ext, MediaExts)) return "Video";
            if (ContainsAnyExt(ext, AudioExts)) return "Audio";
            if (ContainsAnyExt(ext, ImageExts)) return "Images";
            if (ContainsAnyExt(ext, DocExts)) return "Docs";
            if (ContainsAnyExt(ext, GameExts) || it.IsLink) return "Apps";
            if (string.IsNullOrEmpty(ext)) return "Misc";
            return "Files";
        }

        public static bool ContainsAnyExt(string ext, string[] exts)
        {
            foreach (var e in exts)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var n in needles)
                if (haystack.Contains(n)) return true;
            return false;
        }

        /// <summary>Group a flat list of shortcuts by category; return category => items map.</summary>
        public static Dictionary<string, List<ShortcutItem>> GroupByCategory(IEnumerable<ShortcutItem> items)
        {
            var map = new Dictionary<string, List<ShortcutItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                var c = Classify(it);
                if (!map.ContainsKey(c)) map[c] = new List<ShortcutItem>();
                map[c].Add(it);
            }
            return map;
        }
    }
}
