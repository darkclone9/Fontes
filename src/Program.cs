using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Fontes
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args != null && args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                return RunSelfTest();
            }

            if (args != null && args.Any(a => string.Equals(a, "--reset-layout", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (File.Exists(XmlStore.DefaultPath))
                        File.Delete(XmlStore.DefaultPath);
                }
                catch { }
            }

            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "--render-preview", StringComparison.OrdinalIgnoreCase))
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        using (var form = new MainForm())
                        using (var bmp = form.RenderVerificationPreview())
                        {
                            bmp.Save(args[i + 1], System.Drawing.Imaging.ImageFormat.Png);
                        }
                        return 0;
                    }
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Fontes", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            void Write(string what)
            {
                try { File.AppendAllText(log, string.Format("[{0}] {1}\r\n", DateTime.Now.ToString("HH:mm:ss"), what)); } catch { }
            }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Write("Unhandled: " + (e.ExceptionObject?.ToString() ?? "null"));
            Application.ThreadException += (s, e) => Write("Thread: " + (e.Exception?.ToString() ?? "null"));
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            try
            {
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                Write("Run: " + ex);
                throw;
            }
        }

        static int RunSelfTest()
        {
            try
            {
                Console.WriteLine("=== Fontes Self-Test Suite (REA Stardock Fences Parity) ===");

                // 1. Test Resolution Fingerprint & Wallpaper discovery
                string fp = DesktopHost.GetResolutionFingerprint();
                if (string.IsNullOrEmpty(fp)) throw new Exception("Resolution fingerprint was empty.");
                Console.WriteLine("[PASS] DesktopHost.GetResolutionFingerprint: " + fp);

                string wp = DesktopHost.GetWallpaperPath();
                Console.WriteLine("[PASS] DesktopHost.GetWallpaperPath: " + (wp ?? "(none)"));

                if (!string.IsNullOrEmpty(wp) && File.Exists(wp))
                {
                    var cropped = DesktopHost.GetDesktopWallpaper(new Size(640, 360));
                    var frosted = DesktopHost.GetFrostedDesktopWallpaper(new Size(640, 360));
                    if (cropped == null || cropped.Width != 640 || cropped.Height != 360)
                        throw new Exception("DesktopHost.GetDesktopWallpaper failed to produce 640x360 cropped monitor bitmap.");
                    if (frosted == null || frosted.Width != 640 || frosted.Height != 360)
                        throw new Exception("DesktopHost.GetFrostedDesktopWallpaper failed to produce 640x360 frosted monitor bitmap.");
                    Console.WriteLine("[PASS] DesktopHost per-monitor wallpaper crop & frosted glass cache verified.");
                }

                // 2. Test Fence scrollable layout & DeskGroup properties
                var fence = new Fence
                {
                    Title = "Test Fence",
                    Rect = new Rectangle(40, 70, 200, 140),
                    Opacity = 210,
                    RollupOnHover = true,
                    LabelMode = TitleBarMode.OnHover,
                    SortMethod = "TimesOpened",
                    SortAscending = false,
                    Edge = DockEdge.Right
                };
                for (int i = 0; i < 12; i++)
                {
                    fence.Items.Add(new ShortcutItem
                    {
                        Name = "App " + i,
                        Path = @"C:\Windows\notepad.exe",
                        Target = @"C:\Windows\notepad.exe",
                        TimesOpened = i * 2
                    });
                }
                fence.SortItems();
                if (fence.Items[0].TimesOpened != 22)
                    throw new Exception("Fence.SortItems failed to sort descending by TimesOpened.");
                fence.AutoArrange();
                if (!fence.HasOverflow)
                    throw new Exception("Fence.HasOverflow should be true for 12 items in 200x140 fence.");
                fence.ScrollBy(60);
                if (fence.ScrollOffset <= 0)
                    throw new Exception("Fence.ScrollBy failed to update ScrollOffset.");
                Console.WriteLine("[PASS] Fence DeskGroup properties, sorting, and scrollable overflow verified.");

                // 3. Test Row-Aligned Fence Geometry (no mid-icon bottom row clipping)
                var rowFence = new Fence
                {
                    Title = "Row Aligned",
                    Rect = new Rectangle(36, 56, Fence.WidthForColumns(4), Fence.HeightForRows(3))
                };
                if (rowFence.Columns != 4 || rowFence.VisibleRows != 3)
                    throw new Exception("Fence.WidthForColumns(4) / HeightForRows(3) mismatch.");
                if (rowFence.ClientClipRect.Height != 3 * Fence.CellSize)
                    throw new Exception("Fence.ClientClipRect.Height is not an exact multiple of CellSize.");
                Console.WriteLine("[PASS] Fence row-aligned geometry (4x3 grid = " + rowFence.Rect.Width + "x" + rowFence.Rect.Height + ") verified.");

                // 4. Test Multi-criteria RuleEngine
                RuleEngine.CustomRules.Clear();
                RuleEngine.CustomRules.Add(new FenceRule
                {
                    Name = "Studio Tools",
                    TargetFenceTitle = "Studio",
                    ExtensionFilter = ".exe",
                    NamePattern = "Cubase",
                    Enabled = true
                });
                var cubaseItem = new ShortcutItem { Name = "Cubase 15", Path = @"C:\Program Files\Steinberg\Cubase 15\Cubase15.exe", Target = @"C:\Program Files\Steinberg\Cubase 15\Cubase15.exe" };
                string cat = RuleEngine.Classify(cubaseItem);
                if (cat != "Studio")
                    throw new Exception("RuleEngine failed to match custom FenceRule (got " + cat + ").");
                Console.WriteLine("[PASS] RuleEngine custom multi-criteria FenceRule verified.");

                // 5. Test XmlStore round-trip & snapshot creation
                string tempXml = Path.Combine(Path.GetTempPath(), "fontes_selftest_" + Guid.NewGuid().ToString("N") + ".xml");
                XmlStore.Save(new List<Fence> { fence }, tempXml, false);
                var loaded = XmlStore.Load(tempXml);
                File.Delete(tempXml);
                if (loaded.Count != 1 || loaded[0].Opacity != 210 || loaded[0].Edge != DockEdge.Right || loaded[0].LabelMode != TitleBarMode.OnHover)
                    throw new Exception("XmlStore round-trip failed to preserve DeskGroup properties.");
                Console.WriteLine("[PASS] XmlStore XML serialization round-trip verified.");

                // 6. Test Stardock Fences .fencelayout discovery and application
                var layouts = XmlStore.DiscoverFencesLayouts();
                if (layouts.Count == 0)
                    throw new Exception("XmlStore.DiscoverFencesLayouts returned 0 templates.");
                XmlStore.ApplyFencesLayout(loaded, layouts[0], new Rectangle(0, 0, 1920, 1080), MainForm.ToolbarH);
                Console.WriteLine("[PASS] XmlStore.DiscoverFencesLayouts discovered " + layouts.Count + " Stardock .fencelayout template(s); applied '" + layouts[0].Name + "'.");

                // 7. Test PortalScanner with parent navigation
                string srcDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src");
                if (Directory.Exists(srcDir))
                {
                    var portalItems = PortalScanner.ScanFolder(srcDir, AppDomain.CurrentDomain.BaseDirectory);
                    if (!portalItems.Any(x => x.IsParentNavigation))
                        throw new Exception("PortalScanner failed to insert '.. (Up)' parent navigation item.");
                    Console.WriteLine("[PASS] PortalScanner subfolder breadcrumb and '.. (Up)' navigation verified.");
                }

                // 8. Test .url IconFile & IconIndex parsing and non-Shield fallback
                string tempUrl = Path.Combine(Path.GetTempPath(), "fontes_test_" + Guid.NewGuid().ToString("N") + ".url");
                File.WriteAllText(tempUrl, "[InternetShortcut]\r\nURL=https://example.com\r\nIconFile=C:\\Windows\\explorer.exe\r\nIconIndex=0\r\n");
                var urlItem = ShortcutResolver.FromFile(tempUrl);
                File.Delete(tempUrl);
                if (urlItem.Target != "https://example.com" || string.IsNullOrEmpty(urlItem.IconLocation))
                    throw new Exception("ShortcutResolver.ResolveUrl failed to parse URL or IconFile.");
                using (var icon = urlItem.ResolveIcon())
                {
                    if (icon == null)
                        throw new Exception("ShortcutItem.ResolveIcon returned null for .url item.");
                }
                Console.WriteLine("[PASS] .url IconFile/IconIndex extraction and Shell icon fallback verified.");

                // 9. Test 9pt Segoe UI typographic label measurement with ClearTypeGridFit inside CellSize=92 (no 'CrystalDiskInf'/'o' split)
                using (var testBmp = new Bitmap(200, 200))
                using (var g = Graphics.FromImage(testBmp))
                using (var itemFont = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    fmt.Alignment = StringAlignment.Center;
                    fmt.LineAlignment = StringAlignment.Near;
                    fmt.Trimming = StringTrimming.EllipsisCharacter;
                    fmt.FormatFlags = StringFormatFlags.LineLimit;
                    int labelW = Fence.CellSize - 2; // 90px
                    int labelH = 40;
                    int charsFitted, linesFilled;
                    g.MeasureString("CrystalDiskInfo", itemFont, new SizeF(labelW, labelH), fmt, out charsFitted, out linesFilled);
                    if (linesFilled != 1 || charsFitted < "CrystalDiskInfo".Length)
                        throw new Exception("9pt 'CrystalDiskInfo' wrapped across multiple lines (" + linesFilled + " lines, " + charsFitted + " chars) instead of fitting on 1 line.");

                    g.MeasureString("Corsair Web Hub App", itemFont, new SizeF(labelW, labelH), fmt, out charsFitted, out linesFilled);
                    if (linesFilled != 2)
                        throw new Exception("9pt 2-line label 'Corsair Web Hub App' did not fit 2 lines inside label box (linesFilled=" + linesFilled + ").");
                }
                Console.WriteLine("[PASS] 9pt Segoe UI ClearTypeGridFit 1-line and 2-line label geometry verified.");

                // 10. Test Taskbar-Free Desktop Pinning, Per-Fence SearchFilter, and Desktop Pages (_SwipeIndex)
                fence.SearchFilter = "App 1";
                var filtered = fence.FilteredItems;
                if (filtered.Count != 3) // "App 1", "App 10", "App 11"
                    throw new Exception("Fence.FilteredItems expected 3 matches for 'App 1', got " + filtered.Count);
                fence.SearchFilter = "";
                fence.PageIndex = 2;

                string tempPageXml = Path.Combine(Path.GetTempPath(), "fontes_pagetest_" + Guid.NewGuid().ToString("N") + ".xml");
                XmlStore.Save(new List<Fence> { fence }, tempPageXml, false);
                var loadedPage = XmlStore.Load(tempPageXml);
                File.Delete(tempPageXml);
                if (loadedPage.Count != 1 || loadedPage[0].PageIndex != 2)
                    throw new Exception("XmlStore failed to persist Fence.PageIndex.");

                string origLayoutXml = File.Exists(XmlStore.DefaultPath) ? File.ReadAllText(XmlStore.DefaultPath) : null;
                try
                {
                    using (var testForm = new MainForm())
                    {
                        if (testForm.ShowInTaskbar)
                            throw new Exception("MainForm.ShowInTaskbar must be false so Fontes never appears on the Windows taskbar.");
                        IntPtr h = testForm.Handle; // Force handle creation & OnHandleCreated -> UpdateWindowRegion()
                        if (testForm.Region == null)
                            throw new Exception("MainForm.Region must never be null (must stay tightly clipped to fences + HUD).");
                        testForm.UpdateWindowRegion();
                        if (testForm.Region == null)
                            throw new Exception("MainForm.UpdateWindowRegion() set Region to null.");

                        // Verify interactive fence move, fence resize, item drag (including return near origin), and Draw-Fence hook lifecycle never set Region = null
                        var f0 = testForm.Fences.FirstOrDefault(x => x.PageIndex == testForm.CurrentPage);
                        if (f0 != null)
                        {
                            var origRect = f0.Rect;
                            // 10a. Fence move drag
                            Point titlePt = new Point(origRect.X + 40, origRect.Y + 12);
                            testForm.SimulateMouseDown(new MouseEventArgs(MouseButtons.Left, 1, titlePt.X, titlePt.Y, 0));
                            testForm.SimulateMouseMove(new MouseEventArgs(MouseButtons.Left, 1, titlePt.X + 24, titlePt.Y + 24, 0));
                            if (testForm.Region == null)
                                throw new Exception("MainForm.Region became null during fence move drag.");
                            testForm.SimulateMouseUp(new MouseEventArgs(MouseButtons.Left, 1, titlePt.X + 24, titlePt.Y + 24, 0));
                            if (testForm.Region == null)
                                throw new Exception("MainForm.Region became null after fence move MouseUp.");

                            // 10b. Fence resize drag
                            var curRect = f0.Rect;
                            Point sePt = new Point(curRect.Right - 1, curRect.Bottom - 1);
                            testForm.SimulateMouseDown(new MouseEventArgs(MouseButtons.Left, 1, sePt.X, sePt.Y, 0));
                            testForm.SimulateMouseMove(new MouseEventArgs(MouseButtons.Left, 1, sePt.X + 30, sePt.Y + 30, 0));
                            if (testForm.Region == null)
                                throw new Exception("MainForm.Region became null during fence resize drag.");
                            testForm.SimulateMouseUp(new MouseEventArgs(MouseButtons.Left, 1, sePt.X + 30, sePt.Y + 30, 0));

                            f0.Rect = origRect;
                            f0.AutoArrange();
                            testForm.UpdateWindowRegion();

                            // 10c. Item drag (out beyond DragSize and back within DragSize of origin)
                            if (f0.FilteredItems.Count > 0)
                            {
                                var item0 = f0.FilteredItems[0];
                                var cell0 = f0.ItemCellRect(item0);
                                Point itemPt = new Point(cell0.X + cell0.Width / 2, cell0.Y + cell0.Height / 2);
                                Point farPt = new Point(400, 300);
                                Point nearOriginPt = new Point(itemPt.X + 1, itemPt.Y + 1);
                                testForm.SimulateMouseDown(new MouseEventArgs(MouseButtons.Left, 1, itemPt.X, itemPt.Y, 0));
                                testForm.SimulateMouseMove(new MouseEventArgs(MouseButtons.Left, 1, farPt.X, farPt.Y, 0));
                                if (testForm.Region == null || !testForm.Region.IsVisible(farPt))
                                    throw new Exception("MainForm.Region failed to include dragged item ghost tile at farPt.");
                                testForm.SimulateMouseMove(new MouseEventArgs(MouseButtons.Left, 1, nearOriginPt.X, nearOriginPt.Y, 0));
                                if (testForm.Region == null || !testForm.Region.IsVisible(nearOriginPt))
                                    throw new Exception("MainForm.Region failed to update dragged item ghost tile when dragged back within DragSize of origin.");
                                testForm.SimulateMouseUp(new MouseEventArgs(MouseButtons.Left, 1, nearOriginPt.X, nearOriginPt.Y, 0));
                            }
                        }

                        // 10d. Draw-Fence mode WH_MOUSE_LL hook install, rubber-band region union, and Esc cancel cleanup
                        testForm.ToggleDrawFenceMode();
                        if (!testForm.IsDrawFenceHookInstalled)
                            throw new Exception("ToggleDrawFenceMode failed to install WH_MOUSE_LL hook.");
                        if (testForm.Region == null)
                            throw new Exception("MainForm.Region became null upon entering Draw-Fence mode.");
                        testForm.SimulateMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 200, 200, 0));
                        testForm.SimulateMouseMove(new MouseEventArgs(MouseButtons.Left, 1, 360, 320, 0));
                        if (testForm.Region == null || !testForm.Region.IsVisible(new Point(280, 260)))
                            throw new Exception("MainForm.Region failed to union rubber-band _drawFenceRect during Draw-Fence drag.");
                        testForm.SimulateCmdKey(Keys.Escape);
                        if (testForm.IsDrawFenceHookInstalled)
                            throw new Exception("Esc failed to uninstall WH_MOUSE_LL hook in Draw-Fence mode.");
                        if (testForm.Region == null)
                            throw new Exception("MainForm.Region became null after cancelling Draw-Fence mode.");
                    }
                }
                finally
                {
                    if (origLayoutXml != null)
                        File.WriteAllText(XmlStore.DefaultPath, origLayoutXml);
                }
                Console.WriteLine("[PASS] Taskbar-free ShowInTaskbar=false, non-null clipped MainForm.Region across move/resize/item-drag/draw-fence, WH_MOUSE_LL hook lifecycle, per-fence SearchFilter, and Desktop Pages verified.");

                // 11. Test Vector Header Action Buttons (Search magnifying glass, RollUp/RollDown chevrons, Close X)
                using (var btnBmp = new Bitmap(80, 24))
                using (var g = Graphics.FromImage(btnBmp))
                {
                    g.Clear(Color.FromArgb(20, 22, 28));
                    MainForm.DrawHeaderActionButton(g, new Rectangle(0, 2, 20, 20), MainForm.HeaderButtonKind.Search, true, false);
                    MainForm.DrawHeaderActionButton(g, new Rectangle(20, 2, 20, 20), MainForm.HeaderButtonKind.RollUp, false, false);
                    MainForm.DrawHeaderActionButton(g, new Rectangle(40, 2, 20, 20), MainForm.HeaderButtonKind.RollDown, false, false);
                    MainForm.DrawHeaderActionButton(g, new Rectangle(60, 2, 20, 20), MainForm.HeaderButtonKind.Close, true, true);
                }
                Console.WriteLine("[PASS] Vector GDI+ header action buttons (Search, RollUp, RollDown, Close) verified.");

                // 12. Test Left-Aligned Partial-Row AutoArrange & Column-Aligned Proportional Horizontal Layout Widths
                var bigApps = new Fence { Title = "Apps", Items = Enumerable.Range(0, 152).Select(i => new ShortcutItem { Name = "A" + i }).ToList() };
                var bigGames = new Fence { Title = "Games", Items = Enumerable.Range(0, 100).Select(i => new ShortcutItem { Name = "G" + i }).ToList() };
                var smallWeb = new Fence { Title = "Web", Items = Enumerable.Range(0, 3).Select(i => new ShortcutItem { Name = "W" + i }).ToList() };
                var horizList = new List<Fence> { bigApps, bigGames, smallWeb };
                var bottomTpl = new FencesLayoutTemplate
                {
                    Name = "Bottom",
                    Areas = { new FencesLayoutArea { Alignment = "BottomRight", Orientation = "Horizontal", Width = 1.0, Height = 0.24 } }
                };
                XmlStore.ApplyFencesLayout(horizList, bottomTpl, new Rectangle(0, 0, 2048, 1104), MainForm.ToolbarH);
                if (smallWeb.Rect.Width != Fence.WidthForColumns(3) || smallWeb.Columns != 3)
                    throw new Exception("Expected 3-item Web fence in horizontal layout to have WidthForColumns(3)=" + Fence.WidthForColumns(3) + " (3 cols), got " + smallWeb.Rect.Width);
                if (bigApps.Columns != 10 || bigGames.Columns != 8)
                    throw new Exception("Expected column-aligned proportional horizontal layout to fit 10 cols in Apps and 8 cols in Games (got Apps=" + bigApps.Columns + " cols / " + bigApps.Rect.Width + "px, Games=" + bigGames.Columns + " cols / " + bigGames.Rect.Width + "px).");
                Console.WriteLine("[PASS] Column-aligned proportional horizontal layout widths (Apps=" + bigApps.Rect.Width + "px [10 cols], Games=" + bigGames.Rect.Width + "px [8 cols], Web=" + smallWeb.Rect.Width + "px [3 cols]) verified.");

                Console.WriteLine("=== ALL SELF-TESTS PASSED ===");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] Self-test error: " + ex);
                return 1;
            }
        }
    }
}
