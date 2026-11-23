using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Fontes
{
    /// <summary>An item that can live inside a fence: a scanned desktop shortcut, a launcher,
    /// or a folder portal entry. Modeled after Fences' DesktopDockUI.Simulation.IconElement
    /// (Name, _ResolvedName, _ResolvedPath, _ResolvedArguments, TimesOpened, DateAdded, Cell).</summary>
    public class ShortcutItem
    {
        public string Name { get; set; }
        public string Path { get; set; }          // source .lnk / .url / .exe, or portal entry path
        public string Target { get; set; }        // resolved launch target (blank if not launchable)
        public string Arguments { get; set; }
        public string WorkingDir { get; set; }
        public string IconLocation { get; set; }
        public int IconIndex { get; set; }
        public bool IsFolderPortal { get; set; }  // portal entries render as folders
        public bool IsParentNavigation { get; set; } // ".." parent folder entry inside a Folder Portal
        public Point Cell { get; set; }           // grid cell within the fence
        public string Category { get; set; }
        public int TimesOpened { get; set; }      // IconElement.TimesOpened
        public DateTime DateAdded { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;

        Bitmap _cachedBitmap;
        Color? _cachedTint;

        // Fields used by the rule engine to classify shortcuts.
        public bool IsUrl => string.Equals(PathExtension, ".url", StringComparison.OrdinalIgnoreCase);
        public bool IsLink => string.Equals(PathExtension, ".lnk", StringComparison.OrdinalIgnoreCase);
        public string PathExtension => System.IO.Path.GetExtension(Path ?? "");

        public string TargetExtension
        {
            get
            {
                if (!string.IsNullOrEmpty(Target) && !IsUrl)
                {
                    try { return System.IO.Path.GetExtension(Target); } catch { }
                }
                return PathExtension;
            }
        }

        public Icon ResolveIcon()
        {
            try
            {
                if (IsParentNavigation || IsFolderPortal || (!string.IsNullOrEmpty(Path) && Directory.Exists(Path)))
                    return WinIcons.Folder();

                bool targetIsUrl = IsUrl || (!string.IsNullOrEmpty(Target) && Target.EndsWith(".url", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(IconLocation))
                {
                    string loc = Environment.ExpandEnvironmentVariables(IconLocation.Trim().Trim('"'));
                    // If a .lnk points to a .url website shortcut but uses uninstall.exe as its icon (e.g. ASIO4ALL Web Site),
                    // skip the uninstaller icon so it doesn't display an uninstaller UAC shield on a web shortcut.
                    bool skipUninstallerForUrl = targetIsUrl && loc.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!skipUninstallerForUrl && File.Exists(loc))
                    {
                        if (loc.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                        {
                            try { return new Icon(loc, 32, 32); } catch { }
                        }
                        if (IconIndex != 0 || loc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var indexed = WinIcons.ExtractIndexed(loc, IconIndex);
                            if (indexed != null) return indexed;
                        }
                        var assoc = Icon.ExtractAssociatedIcon(loc);
                        if (assoc != null) return assoc;
                    }
                    else if (!skipUninstallerForUrl)
                    {
                        // If IconLocation points to a moved/uninstalled subfolder binary (e.g. J:\World of Warcraft\_classic_beta_\WowB.exe),
                        // check the parent install folder for an existing launcher executable before falling back to the default URL icon.
                        try
                        {
                            string dir = System.IO.Path.GetDirectoryName(loc);
                            string parentDir = !string.IsNullOrEmpty(dir) ? System.IO.Path.GetDirectoryName(dir) : null;
                            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                            {
                                foreach (var exe in Directory.GetFiles(parentDir, "*.exe"))
                                {
                                    var assoc = Icon.ExtractAssociatedIcon(exe);
                                    if (assoc != null) return assoc;
                                }
                            }
                        }
                        catch { }

                        if (targetIsUrl)
                            return WinIcons.UrlIcon();
                    }
                }

                if (!string.IsNullOrEmpty(Target) && File.Exists(Target))
                {
                    if (Target.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        return WinIcons.UrlIcon();
                    var byTarget = WinIcons.ForPath(Target) ?? Icon.ExtractAssociatedIcon(Target);
                    if (byTarget != null) return byTarget;
                }

                if (targetIsUrl)
                {
                    return WinIcons.UrlIcon();
                }

                if (!string.IsNullOrEmpty(Path) && File.Exists(Path))
                {
                    var byPath = WinIcons.ForPath(Path) ?? Icon.ExtractAssociatedIcon(Path);
                    if (byPath != null) return byPath;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        /// <summary>Caches the 32x32 bitmap representation of the shortcut icon (and applies optional
        /// Fences DesktopTintIcons color tinting) so OnPaint never leaks GDI handles.</summary>
        public Bitmap GetCachedIconBitmap(Color? tint = null)
        {
            if (_cachedBitmap != null && _cachedTint == tint)
                return _cachedBitmap;

            if (_cachedBitmap != null)
            {
                _cachedBitmap.Dispose();
                _cachedBitmap = null;
            }

            using (var icon = ResolveIcon())
            using (var raw = icon.ToBitmap())
            {
                var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    if (tint.HasValue)
                    {
                        float r = tint.Value.R / 255f;
                        float gr = tint.Value.G / 255f;
                        float b = tint.Value.B / 255f;
                        var cm = new ColorMatrix(new float[][]
                        {
                            new float[] { 0.35f + 0.65f * r, 0.15f * gr, 0.15f * b, 0, 0 },
                            new float[] { 0.15f * r, 0.35f + 0.65f * gr, 0.15f * b, 0, 0 },
                            new float[] { 0.15f * r, 0.15f * gr, 0.35f + 0.65f * b, 0, 0 },
                            new float[] { 0, 0, 0, 1, 0 },
                            new float[] { 0, 0, 0, 0, 1 }
                        });
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            g.DrawImage(raw, new Rectangle(0, 0, 32, 32), 0, 0, raw.Width, raw.Height, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else
                    {
                        g.DrawImage(raw, new Rectangle(0, 0, 32, 32));
                    }
                }
                _cachedBitmap = bmp;
                _cachedTint = tint;
                return _cachedBitmap;
            }
        }

        public void InvalidateIconCache()
        {
            if (_cachedBitmap != null)
            {
                _cachedBitmap.Dispose();
                _cachedBitmap = null;
            }
        }

        public string DisplayText => string.IsNullOrEmpty(Name)
            ? System.IO.Path.GetFileNameWithoutExtension(Path ?? "")
            : Name;
    }
}
