using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace Fontes
{
    /// <summary>Represents a parsed Stardock Fences .fencelayout Area
    /// (DesktopDockUI.Layouts.LayoutArea).</summary>
    public class FencesLayoutArea
    {
        public string Alignment { get; set; } = "TopRight";
        public string Orientation { get; set; } = "Vertical";
        public double Width { get; set; } = 0.25;
        public double Height { get; set; } = 1.0;
        public double XOffset { get; set; }
        public double YOffset { get; set; }
        public int WidthPixelMod { get; set; }
        public int HeightPixelMod { get; set; }
        public int XPixelMod { get; set; }
        public int YPixelMod { get; set; }
    }

    /// <summary>Represents a parsed Stardock Fences .fencelayout template
    /// (DesktopDockUI.Layouts.Layout).</summary>
    public class FencesLayoutTemplate
    {
        public string Name { get; set; }
        public string SourcePath { get; set; }
        public List<FencesLayoutArea> Areas { get; set; } = new List<FencesLayoutArea>();
    }

    /// <summary>Persists fences + items + custom rules to XML, manages automatic daily snapshots
    /// (SnapshotInfo / Settings.AutoTakeSnapshots), and imports Stardock Fences .fencelayout templates.</summary>
    public static class XmlStore
    {
        public static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fontes");

        public static string DefaultPath => Path.Combine(AppDataDir, "layout.xml");

        public static string SnapshotsDir => Path.Combine(AppDataDir, "Snapshots");

        public const string StardockLayoutsDir = @"C:\Program Files (x86)\Stardock\Fences\Layouts";

        public static void Save(List<Fence> fences, string path = null, bool takeDailySnapshot = true)
        {
            var doc = new XmlDocument();
            var root = doc.CreateElement("FontesSnapshot");
            root.SetAttribute("timestamp", DateTime.Now.ToString("o"));
            root.SetAttribute("resolutionFingerprint", DesktopHost.GetResolutionFingerprint());
            root.SetAttribute("layoutVersion", "3");
            doc.AppendChild(root);

            var groups = doc.CreateElement("Groups");
            root.AppendChild(groups);

            foreach (var f in fences)
            {
                var g = doc.CreateElement("Fence");
                g.SetAttribute("id", f.Id);
                g.SetAttribute("title", f.Title ?? "");
                g.SetAttribute("z", f.Z.ToString());
                g.SetAttribute("shade", Argb(f.Shade));
                g.SetAttribute("opacity", f.Opacity.ToString());
                g.SetAttribute("textColor", Argb(f.TextColor));
                if (f.TintColor.HasValue) g.SetAttribute("tintColor", Argb(f.TintColor.Value));
                g.SetAttribute("rolled", f.RolledUp.ToString());
                g.SetAttribute("rollupOnHover", f.RollupOnHover.ToString());
                g.SetAttribute("snap", f.SnapToGrid.ToString());
                g.SetAttribute("locked", f.Locked.ToString());
                g.SetAttribute("edge", ((int)f.Edge).ToString());
                g.SetAttribute("labelMode", ((int)f.LabelMode).ToString());
                g.SetAttribute("sortMethod", f.SortMethod ?? "Name");
                g.SetAttribute("sortAsc", f.SortAscending.ToString());
                g.SetAttribute("scroll", f.ScrollOffset.ToString());
                g.SetAttribute("page", f.PageIndex.ToString());
                if (!string.IsNullOrEmpty(f.RuleCategory)) g.SetAttribute("rule", f.RuleCategory);
                if (!string.IsNullOrEmpty(f.PortalPath))   g.SetAttribute("portal", f.PortalPath);

                var rect = doc.CreateElement("Rect");
                rect.SetAttribute("x", f.Rect.X.ToString());
                rect.SetAttribute("y", f.Rect.Y.ToString());
                rect.SetAttribute("w", f.Rect.Width.ToString());
                rect.SetAttribute("h", f.Rect.Height.ToString());
                g.AppendChild(rect);

                var items = doc.CreateElement("Items");
                foreach (var it in f.Items)
                {
                    if (it.IsParentNavigation) continue;
                    var e = doc.CreateElement("Item");
                    e.SetAttribute("name", it.Name ?? "");
                    e.SetAttribute("path", it.Path ?? "");
                    e.SetAttribute("target", it.Target ?? "");
                    e.SetAttribute("args", it.Arguments ?? "");
                    e.SetAttribute("workdir", it.WorkingDir ?? "");
                    e.SetAttribute("iconLoc", it.IconLocation ?? "");
                    if (it.IconIndex != 0) e.SetAttribute("iconIdx", it.IconIndex.ToString());
                    e.SetAttribute("portal", it.IsFolderPortal.ToString());
                    e.SetAttribute("cat", it.Category ?? "");
                    e.SetAttribute("cellx", it.Cell.X.ToString());
                    e.SetAttribute("celly", it.Cell.Y.ToString());
                    e.SetAttribute("timesOpened", it.TimesOpened.ToString());
                    e.SetAttribute("iconText", it.DisplayText);
                    items.AppendChild(e);
                }
                g.AppendChild(items);
                groups.AppendChild(g);
            }

            if (RuleEngine.CustomRules != null && RuleEngine.CustomRules.Count > 0)
            {
                var rulesEl = doc.CreateElement("Rules");
                foreach (var r in RuleEngine.CustomRules)
                {
                    var re = doc.CreateElement("Rule");
                    re.SetAttribute("id", r.Id ?? Guid.NewGuid().ToString("N"));
                    re.SetAttribute("name", r.Name ?? "");
                    re.SetAttribute("enabled", r.Enabled.ToString());
                    re.SetAttribute("targetFence", r.TargetFenceTitle ?? "");
                    re.SetAttribute("ext", r.ExtensionFilter ?? "");
                    re.SetAttribute("namePattern", r.NamePattern ?? "");
                    re.SetAttribute("targetPattern", r.TargetPattern ?? "");
                    if (r.MaxAgeDays.HasValue) re.SetAttribute("maxAgeDays", r.MaxAgeDays.Value.ToString());
                    if (r.MinTimesOpened.HasValue) re.SetAttribute("minTimesOpened", r.MinTimesOpened.Value.ToString());
                    rulesEl.AppendChild(re);
                }
                root.AppendChild(rulesEl);
            }

            var savePath = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            doc.Save(savePath);

            if (takeDailySnapshot && path == null)
            {
                EnsureDailySnapshot(doc);
            }
        }

        public static string CreateManualSnapshot(List<Fence> fences, string label = null)
        {
            Directory.CreateDirectory(SnapshotsDir);
            string tag = string.IsNullOrWhiteSpace(label)
                ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
                : DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + SanitizeFileName(label);
            string snapPath = Path.Combine(SnapshotsDir, "snapshot_" + tag + ".xml");
            Save(fences, snapPath, false);
            return snapPath;
        }

        static void EnsureDailySnapshot(XmlDocument doc)
        {
            try
            {
                Directory.CreateDirectory(SnapshotsDir);
                string dailyName = "daily_" + DateTime.Now.ToString("yyyy-MM-dd") + ".xml";
                string dailyPath = Path.Combine(SnapshotsDir, dailyName);
                if (!File.Exists(dailyPath))
                    doc.Save(dailyPath);
            }
            catch { }
        }

        public static List<string> ListSnapshots()
        {
            if (!Directory.Exists(SnapshotsDir)) return new List<string>();
            return Directory.GetFiles(SnapshotsDir, "*.xml")
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .ToList();
        }

        public static List<Fence> Load(string path = null)
        {
            var file = path ?? DefaultPath;
            var list = new List<Fence>();
            if (!File.Exists(file)) return list;
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch { return list; }

            var rootEl = doc.DocumentElement;
            string layoutVersion = rootEl != null ? Attr(rootEl, "layoutVersion") : null;

            var rulesNode = doc.SelectSingleNode("//Rules");
            if (rulesNode != null)
            {
                var loadedRules = new List<FenceRule>();
                foreach (XmlElement re in rulesNode.SelectNodes("Rule"))
                {
                    var rule = new FenceRule
                    {
                        Id = Attr(re, "id") ?? Guid.NewGuid().ToString("N"),
                        Name = Attr(re, "name") ?? "Rule",
                        Enabled = BoolAttr(re, "enabled", true),
                        TargetFenceTitle = Attr(re, "targetFence") ?? "Apps",
                        ExtensionFilter = Attr(re, "ext") ?? "",
                        NamePattern = Attr(re, "namePattern") ?? "",
                        TargetPattern = Attr(re, "targetPattern") ?? ""
                    };
                    if (re.HasAttribute("maxAgeDays") && int.TryParse(re.GetAttribute("maxAgeDays"), out int mad))
                        rule.MaxAgeDays = mad;
                    if (re.HasAttribute("minTimesOpened") && int.TryParse(re.GetAttribute("minTimesOpened"), out int mto))
                        rule.MinTimesOpened = mto;
                    loadedRules.Add(rule);
                }
                RuleEngine.CustomRules = loadedRules;
            }

            var groups = doc.SelectSingleNode("//Groups");
            if (groups == null) return list;
            foreach (XmlElement g in groups.SelectNodes("Fence"))
            {
                var shade = ParseArgb(Attr(g, "shade"), Color.FromArgb(205, 26, 28, 36));
                var f = new Fence
                {
                    Id = Attr(g, "id") ?? Guid.NewGuid().ToString("N"),
                    Title = Attr(g, "title") ?? "",
                    Z = IntAttr(g, "z", 0),
                    Shade = shade,
                    Opacity = IntAttr(g, "opacity", shade.A > 0 ? shade.A : 205),
                    TextColor = ParseArgb(Attr(g, "textColor"), Color.FromArgb(242, 245, 252)),
                    RolledUp = BoolAttr(g, "rolled", false),
                    RollupOnHover = BoolAttr(g, "rollupOnHover", true),
                    SnapToGrid = BoolAttr(g, "snap", true),
                    Locked = BoolAttr(g, "locked", false),
                    Edge = (DockEdge)IntAttr(g, "edge", 0),
                    LabelMode = (TitleBarMode)IntAttr(g, "labelMode", 0),
                    SortMethod = Attr(g, "sortMethod") ?? "Name",
                    SortAscending = BoolAttr(g, "sortAsc", true),
                    ScrollOffset = IntAttr(g, "scroll", 0),
                    PageIndex = Math.Max(0, Math.Min(2, IntAttr(g, "page", 0))),
                    RuleCategory = Attr(g, "rule"),
                    PortalPath = Attr(g, "portal")
                };
                if (g.HasAttribute("tintColor"))
                    f.TintColor = ParseArgb(Attr(g, "tintColor"), Color.White);

                var rect = g.SelectSingleNode("Rect") as XmlElement;
                if (rect != null)
                    f.Rect = new Rectangle(IntAttr(rect, "x", 36), IntAttr(rect, "y", 56),
                                           IntAttr(rect, "w", Fence.WidthForColumns(3)), IntAttr(rect, "h", Fence.HeightForRows(2)));

                if (!string.IsNullOrEmpty(f.PortalPath) && Directory.Exists(f.PortalPath))
                {
                    f.CurrentPortalPath = f.PortalPath;
                    f.Items = PortalScanner.ScanFolder(f.PortalPath, f.PortalPath);
                }
                else
                {
                    var items = g.SelectSingleNode("Items") as XmlElement;
                    if (items != null)
                        foreach (XmlElement e in items.SelectNodes("Item"))
                        {
                            var it = new ShortcutItem
                            {
                                Name = Attr(e, "name") ?? "",
                                Path = Attr(e, "path") ?? "",
                                Target = Attr(e, "target") ?? "",
                                Arguments = Attr(e, "args"),
                                WorkingDir = Attr(e, "workdir"),
                                IconLocation = Attr(e, "iconLoc"),
                                IconIndex = IntAttr(e, "iconIdx", 0),
                                IsFolderPortal = BoolAttr(e, "portal", false),
                                Category = Attr(e, "cat"),
                                TimesOpened = IntAttr(e, "timesOpened", 0),
                                Cell = new Point(IntAttr(e, "cellx", 0), IntAttr(e, "celly", 0))
                            };
                            if (it.IsUrl && File.Exists(it.Path) && string.IsNullOrEmpty(it.IconLocation))
                                ShortcutResolver.ResolveUrl(it);
                            f.Items.Add(it);
                        }
                }

                // Prune stale empty default "Web & Docs" or stray empty "New Fence *" boxes from default layout
                if (path == null &&
                    f.Items.Count == 0 &&
                    string.IsNullOrEmpty(f.PortalPath) &&
                    (string.Equals(f.Title, "Web & Docs", StringComparison.OrdinalIgnoreCase) ||
                     (f.Title != null && f.Title.StartsWith("New Fence", StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                f.SnapHeightToRows();
                f.AutoArrange();
                list.Add(f);
            }

            // Migrate v1 (270x220) or v2 (370x390 / 284x132 at CellSize=86) default layout geometries to v3 row-aligned grids
            if (path == null && !string.Equals(layoutVersion, "3", StringComparison.Ordinal) && list.Count > 0 &&
                list.All(x => x.Rect.Width == 270 || x.Rect.Width == 370 || x.Rect.Width == 284))
            {
                int curX = 36;
                int curY = 56;
                const int gap = 18;
                foreach (var f in list)
                {
                    int cols = f.Items.Count >= 12 ? 4 : 3;
                    int rows = f.Items.Count >= 12 ? 4 : (f.Items.Count > 3 ? 2 : 1);
                    int w = Fence.WidthForColumns(cols);
                    int h = Fence.HeightForRows(rows);
                    f.Opacity = Math.Max(f.Opacity, 205);
                    f.Rect = new Rectangle(curX, curY, w, h);
                    f.AutoArrange();
                    curX += w + gap;
                }
            }

            return list;
        }

        /// <summary>Discovers and parses Stardock Fences .fencelayout files from
        /// C:\Program Files (x86)\Stardock\Fences\Layouts (plus built-in presets if absent).</summary>
        public static List<FencesLayoutTemplate> DiscoverFencesLayouts()
        {
            var list = new List<FencesLayoutTemplate>();
            if (Directory.Exists(StardockLayoutsDir))
            {
                foreach (var file in Directory.GetFiles(StardockLayoutsDir, "*.fencelayout"))
                {
                    var tpl = ParseFencesLayoutFile(file);
                    if (tpl != null && tpl.Areas.Count > 0)
                        list.Add(tpl);
                }
            }

            if (list.Count == 0)
            {
                list.Add(new FencesLayoutTemplate
                {
                    Name = "Right side",
                    Areas = { new FencesLayoutArea { Alignment = "TopRight", Orientation = "Vertical", Width = 0.30, Height = 1.0, WidthPixelMod = -8 } }
                });
                list.Add(new FencesLayoutTemplate
                {
                    Name = "Right (Double)",
                    Areas =
                    {
                        new FencesLayoutArea { Alignment = "TopRight", Orientation = "Vertical", Width = 0.19, Height = 1.0, XOffset = -0.19, XPixelMod = -16 },
                        new FencesLayoutArea { Alignment = "TopRight", Orientation = "Vertical", Width = 0.19, Height = 1.0 }
                    }
                });
                list.Add(new FencesLayoutTemplate
                {
                    Name = "Bottom Dock",
                    Areas = { new FencesLayoutArea { Alignment = "BottomRight", Orientation = "Horizontal", Width = 0.70, Height = 0.28 } }
                });
            }
            return list;
        }

        public static FencesLayoutTemplate ParseFencesLayoutFile(string filePath)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(filePath);
                var nameNode = doc.SelectSingleNode("//Layout/Name");
                var tpl = new FencesLayoutTemplate
                {
                    Name = nameNode != null && !string.IsNullOrWhiteSpace(nameNode.InnerText)
                        ? nameNode.InnerText.Trim() + " (" + Path.GetFileNameWithoutExtension(filePath) + ")"
                        : Path.GetFileNameWithoutExtension(filePath),
                    SourcePath = filePath
                };

                var areaNodes = doc.SelectNodes("//Layout/Areas/LayoutArea");
                if (areaNodes != null)
                {
                    foreach (XmlElement an in areaNodes)
                    {
                        var area = new FencesLayoutArea
                        {
                            Alignment = ChildText(an, "Alignment", "TopRight"),
                            Orientation = ChildText(an, "Orientation", "Vertical"),
                            Width = ChildDouble(an, "Width", 0.25),
                            Height = ChildDouble(an, "Height", 1.0),
                            XOffset = FirstArrayDouble(an, "XOffsetModifers"),
                            YOffset = FirstArrayDouble(an, "YOffsetModifers"),
                            WidthPixelMod = FirstArrayInt(an, "WidthModifersNoRounding"),
                            HeightPixelMod = FirstArrayInt(an, "HeightModifersNoRounding"),
                            XPixelMod = FirstArrayInt(an, "XOffsetModifersNoRounding"),
                            YPixelMod = FirstArrayInt(an, "YOffsetModifersNoRounding")
                        };
                        tpl.Areas.Add(area);
                    }
                }
                return tpl;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Applies a Stardock Fences LayoutTemplate (DesktopDockUI.Layouts.Layout.Apply)
        /// by distributing existing fences across the template's LayoutAreas.</summary>
        public static void ApplyFencesLayout(List<Fence> fences, FencesLayoutTemplate tpl, Rectangle workArea, int toolbarHeight)
        {
            if (fences == null || fences.Count == 0 || tpl == null || tpl.Areas.Count == 0) return;
            int topOffset = Math.Max(48, toolbarHeight + 12);
            Rectangle usable = new Rectangle(workArea.X + 12, workArea.Y + topOffset,
                Math.Max(300, workArea.Width - 24), Math.Max(200, workArea.Height - topOffset - 12));

            var areaRects = new List<Rectangle>();
            foreach (var a in tpl.Areas)
            {
                int w = Math.Max(160, (int)(usable.Width * Math.Max(0.12, Math.Min(1.0, a.Width))) + a.WidthPixelMod);
                int h = Math.Max(120, (int)(usable.Height * Math.Max(0.15, Math.Min(1.0, a.Height))) + a.HeightPixelMod);
                int x = usable.X;
                int y = usable.Y;

                string align = (a.Alignment ?? "TopRight").ToLowerInvariant();
                if (align.Contains("right")) x = usable.Right - w;
                else if (align.Contains("center")) x = usable.X + (usable.Width - w) / 2;

                if (align.Contains("bottom")) y = usable.Bottom - h;
                else if (align.Contains("middle")) y = usable.Y + (usable.Height - h) / 2;

                x += (int)(usable.Width * a.XOffset) + a.XPixelMod;
                y += (int)(usable.Height * a.YOffset) + a.YPixelMod;

                areaRects.Add(new Rectangle(x, y, w, h));
            }

            var buckets = new List<List<Fence>>();
            for (int i = 0; i < areaRects.Count; i++) buckets.Add(new List<Fence>());
            for (int i = 0; i < fences.Count; i++)
                buckets[i % areaRects.Count].Add(fences[i]);

            const int gap = 12;
            for (int aIdx = 0; aIdx < areaRects.Count; aIdx++)
            {
                var bucket = buckets[aIdx];
                if (bucket.Count == 0) continue;
                var ar = areaRects[aIdx];
                bool horiz = string.Equals(tpl.Areas[aIdx].Orientation, "Horizontal", StringComparison.OrdinalIgnoreCase);

                if (horiz)
                {
                    int slotW = Math.Max(160, (ar.Width - gap * (bucket.Count - 1)) / bucket.Count);
                    for (int j = 0; j < bucket.Count; j++)
                    {
                        bucket[j].RolledUp = false;
                        bucket[j].Rect = new Rectangle(ar.X + j * (slotW + gap), ar.Y, slotW, ar.Height);
                        bucket[j].SnapHeightToRows();
                        bucket[j].AutoArrange();
                    }
                }
                else
                {
                    int slotH = Math.Max(Fence.HeightForRows(1), (ar.Height - gap * (bucket.Count - 1)) / bucket.Count);
                    for (int j = 0; j < bucket.Count; j++)
                    {
                        bucket[j].RolledUp = false;
                        bucket[j].Rect = new Rectangle(ar.X, ar.Y + j * (slotH + gap), ar.Width, slotH);
                        bucket[j].SnapHeightToRows();
                        bucket[j].AutoArrange();
                    }
                }
            }
        }

        static string ChildText(XmlElement parent, string tag, string def)
        {
            var n = parent.SelectSingleNode(tag);
            return n != null && !string.IsNullOrWhiteSpace(n.InnerText) ? n.InnerText.Trim() : def;
        }

        static double ChildDouble(XmlElement parent, string tag, double def)
        {
            var s = ChildText(parent, tag, null);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
        }

        static double FirstArrayDouble(XmlElement parent, string tag)
        {
            var n = parent.SelectSingleNode(tag + "/double");
            return n != null && double.TryParse(n.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        }

        static int FirstArrayInt(XmlElement parent, string tag)
        {
            var n = parent.SelectSingleNode(tag + "/int");
            return n != null && int.TryParse(n.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        static string Attr(XmlElement e, string n) => e.HasAttribute(n) ? e.GetAttribute(n) : null;
        static int IntAttr(XmlElement e, string n, int d) => e.HasAttribute(n) && int.TryParse(e.GetAttribute(n), out var v) ? v : d;
        static bool BoolAttr(XmlElement e, string n, bool d) => e.HasAttribute(n) && bool.TryParse(e.GetAttribute(n), out var v) ? v : d;
        public static string Argb(Color c) => string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        public static Color ParseArgb(string s, Color def)
        {
            if (string.IsNullOrEmpty(s) || s.Length != 9 || s[0] != '#') return def;
            try
            {
                return Color.FromArgb(
                    Convert.ToInt32(s.Substring(1, 2), 16),
                    Convert.ToInt32(s.Substring(3, 2), 16),
                    Convert.ToInt32(s.Substring(5, 2), 16),
                    Convert.ToInt32(s.Substring(7, 2), 16));
            }
            catch { return def; }
        }
    }
}
