using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Fontes
{
    /// <summary>The Fontes desktop canvas: renders translucent frosted dark-glass fences over the live
    /// desktop using dynamic window Region clipping (so empty desktop areas are 100% see-through to live
    /// animated Wallpaper Engine backgrounds and native desktop clicks), with a floating top-center HUD pill
    /// and system tray NotifyIcon.</summary>
    public class MainForm : Form
    {
        List<Fence> _fences = new List<Fence>();
        Rectangle _work;

        public const int ToolbarH = 0; // Replaced full-width top bar with compact floating top-center HUD pill
        const int HudTopY = 10;
        const int HudHeight = 34;
        const int SnapGrid = 12;
        const int MagneticSnapDist = 10;
        const int SnapGap = 12;

        // Typography
        readonly Font _titleFont;
        readonly Font _badgeFont;
        readonly Font _itemFont;
        readonly Font _hudFont;
        readonly Font _hudBrandFont;

        // System tray & status timer
        NotifyIcon _trayIcon;
        Timer _uiTimer;

        // HUD pill state
        List<(string, Action)> _hudButtons;
        int _hudHover = -1;
        bool _hudCompact;
        bool _autoHideHud = true;
        bool _hudWakeActive;
        bool _drawFenceMode;

        // Desktop Pages (Stardock Fences Desktop Swipe / _SwipeIndex)
        int _currentPage = 0;
        public const int MaxPages = 3;

        // Per-fence live search & header button state
        Fence _activeSearchFence;
        int _hoveredHeaderBtn = -1; // 0 = Search, 1 = Roll, 2 = Close

        // Fence drag / resize state
        Fence _dragFence;
        Point _dragOffset;
        bool _potentialFenceMove;
        bool _dragMove;
        ResizeEdge _resize;
        bool _dragResize;
        Rectangle _resizeStart;
        Point _resizeFrom;

        // Item click / drag / launch state
        ShortcutItem _pressedItem;
        Fence _pressedItemFence;
        Point _mouseDownLoc;
        Point _dragItemLocation;
        bool _isDraggingItem;
        ShortcutItem _hoveredItem;
        Fence _hoveredFence;
        DateTime _lastItemClickTime = DateTime.MinValue;
        ShortcutItem _lastClickedItem;

        // Drag-to-create fence on empty desktop (CreateFenceWithMouseMode)
        bool _isDrawingNewFence;
        bool _drawFenceMoveQueued;
        Point _drawFenceStart;
        Rectangle _drawFenceRect;
        IntPtr _drawFenceHook = IntPtr.Zero;
        DesktopHost.LowLevelMouseProc _drawFenceHookProc;

        // Magnetic alignment guide lines shown during move/resize
        List<(Point, Point)> _snapGuides = new List<(Point, Point)>();

        // Quick-hide & Peek state
        bool _quickHide;
        bool _peek;
        bool _peekOnTop;
        bool _inModalDialog;
        bool _singleClickLaunch;
        bool _embedInProgman;
        string _statusBanner = "";
        DateTime _statusUntil = DateTime.MinValue;

        public List<Fence> Fences => _fences;

        public int CurrentPage
        {
            get => _currentPage;
            set
            {
                int next = Math.Max(0, Math.Min(MaxPages - 1, value));
                if (_currentPage != next)
                {
                    if (_drawFenceMode || _isDrawingNewFence)
                    {
                        _drawFenceMode = false;
                        _isDrawingNewFence = false;
                        _drawFenceMoveQueued = false;
                        _drawFenceRect = Rectangle.Empty;
                        UninstallDrawFenceHook();
                        Cursor = Cursors.Default;
                    }
                    _currentPage = next;
                    _hoveredFence = null;
                    _hoveredItem = null;
                    _activeSearchFence = null;
                    SetStatus("Desktop Page " + (_currentPage + 1) + " of " + MaxPages + " (Alt+Left / Alt+Right)");
                    UpdateWindowRegion();
                    Invalidate();
                }
            }
        }

        IEnumerable<Fence> ActivePageFences => _fences.Where(f => f.PageIndex == _currentPage);

        bool IsHudExpanded => !_autoHideHud || _hudWakeActive || _peekOnTop || _drawFenceMode || _quickHide || IsStatusToastVisible;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_TOOLWINDOW hides Fontes from the Windows taskbar ("hotbar") and from the Alt+Tab switcher
                cp.ExStyle |= DesktopHost.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        bool IsPointerDragActive =>
            _dragMove || _dragResize || _isDraggingItem || _isDrawingNewFence;

        bool IsStatusToastVisible =>
            _drawFenceMode || _peek || _quickHide || (DateTime.Now < _statusUntil && !string.IsNullOrEmpty(_statusBanner));

        public MainForm()
        {
            Text = "Fontes — Desktop Organizer";
            BackColor = Color.FromArgb(20, 22, 28);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = false;
            DoubleBuffered = true;
            AllowDrop = true;
            KeyPreview = true;

            _titleFont = CreateFont("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            _badgeFont = CreateFont("Segoe UI", 8f, FontStyle.Bold);
            _itemFont = CreateFont("Segoe UI", 9f, FontStyle.Regular);
            _hudFont = CreateFont("Segoe UI", 8.75f, FontStyle.Regular);
            _hudBrandFont = CreateFont("Segoe UI Semibold", 9f, FontStyle.Bold);

            _work = DesktopHost.VirtualScreen();
            Bounds = new Rectangle(_work.X, _work.Y, _work.Width, _work.Height);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            BuildHudPill();
            LoadFences();
            if (_fences.Count == 0)
            {
                CreateWelcomeFences();
                AutoSave(true);
            }

            BuildCanvasContextMenu();
            InitSystemTray();

            _uiTimer = new Timer { Interval = 350 };
            bool lastStatusToast = IsStatusToastVisible;
            _uiTimer.Tick += (s, e) =>
            {
                bool curToast = IsStatusToastVisible;
                bool regionChanged = false;
                bool needsRepaint = false;
                if (curToast != lastStatusToast)
                {
                    lastStatusToast = curToast;
                    regionChanged = true;
                    needsRepaint = true;
                }

                if (!IsPointerDragActive && IsHandleCreated && !IsDisposed)
                {
                    Point cur = PointToClient(Cursor.Position);
                    var fullHud = GetExpandedHudPillBounds();
                    bool nearTopCenter = cur.Y >= 0 && cur.Y <= (fullHud.Bottom + 14) &&
                                         cur.X >= (fullHud.Left - 30) && cur.X <= (fullHud.Right + 30);
                    if (nearTopCenter != _hudWakeActive)
                    {
                        _hudWakeActive = nearTopCenter;
                        regionChanged = true;
                        needsRepaint = true;
                    }

                    if (_hudHover != -1 && !GetHudPillBounds().Contains(cur))
                    {
                        _hudHover = -1;
                        needsRepaint = true;
                    }
                    if (_hoveredFence != null && !_hoveredFence.EffectiveBounds.Contains(cur))
                    {
                        _hoveredFence = null;
                        _hoveredItem = null;
                        _hoveredHeaderBtn = -1;
                        needsRepaint = true;
                    }
                    else if (_hoveredItem != null && _hoveredFence != null && HitItem(_hoveredFence, cur) != _hoveredItem)
                    {
                        _hoveredItem = HitItem(_hoveredFence, cur);
                        needsRepaint = true;
                    }
                    foreach (var f in ActivePageFences)
                    {
                        if (f.IsHoverExpanded && !f.Rect.Contains(cur))
                        {
                            f.IsHoverExpanded = false;
                            regionChanged = true;
                            needsRepaint = true;
                        }
                    }

                    // Keep pinned at desktop bottom when not peeking on top
                    if (!_peekOnTop && !_inModalDialog)
                        DesktopHost.SendToDesktopBottom(Handle);
                }

                if (regionChanged) UpdateWindowRegion();
                if (needsRepaint) Invalidate();
            };
            _uiTimer.Start();

            Shown += (s, e) =>
            {
                DesktopHost.Embed(this, _embedInProgman);
                DesktopHost.RegisterPeekHotkeys(Handle);
                UpdateWindowRegion();
            };
            FormClosing += (s, e) =>
            {
                CleanupResources();
                DesktopHost.UnregisterPeekHotkeys(Handle);
                AutoSave(false);
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateWindowRegion();
        }

        void CleanupResources()
        {
            UninstallDrawFenceHook();
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Dispose();
                _uiTimer = null;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CleanupResources();
                CanvasMenu?.Dispose();
                _titleFont?.Dispose();
                _badgeFont?.Dispose();
                _itemFont?.Dispose();
                _hudFont?.Dispose();
                _hudBrandFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        static Font CreateFont(string family, float size, FontStyle style)
        {
            try
            {
                var f = new Font(family, size, style);
                if (string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase))
                    return f;
                f.Dispose();
            }
            catch { }
            return new Font("Segoe UI", size, style);
        }

        void InitSystemTray()
        {
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _trayIcon = new NotifyIcon
            {
                Icon = appIcon ?? SystemIcons.Application,
                Text = "Fontes — Desktop Organizer (Double-click to Hide/Show Fences)",
                ContextMenuStrip = CanvasMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => ToggleQuickHide();
        }

        /// <summary>Clips MainForm's window Region strictly to the floating HUD pill + visible fences +
        /// active snap guides + dragged item ghost + rubber-band draw rectangle. Region is NEVER null,
        /// ensuring 100% of the empty desktop passes through natively to live animated Wallpaper Engine
        /// backgrounds without ever flashing a full-screen static wallpaper.</summary>
        public void UpdateWindowRegion()
        {
            if (IsDisposed || !IsHandleCreated) return;

            var reg = new Region();
            reg.MakeEmpty();

            if (IsHudExpanded)
            {
                var hudBounds = GetHudPillBounds();
                using (var hudPath = Rounded(new Rectangle(hudBounds.X, hudBounds.Y, hudBounds.Width + 1, hudBounds.Height + 1), HudHeight / 2))
                {
                    reg.Union(hudPath);
                }
            }

            if (IsStatusToastVisible)
            {
                var toastBounds = GetStatusPillBounds();
                using (var toastPath = Rounded(new Rectangle(toastBounds.X, toastBounds.Y, toastBounds.Width + 1, toastBounds.Height + 1), 13))
                {
                    reg.Union(toastPath);
                }
            }

            if (!_quickHide && !_peek)
            {
                foreach (var f in ActivePageFences)
                {
                    var r = f.EffectiveBounds;
                    using (var fp = Rounded(new Rectangle(r.X, r.Y, r.Width + 1, r.Height + 1), Fence.CornerRadius))
                    {
                        reg.Union(fp);
                    }
                }
            }

            if (_snapGuides.Count > 0)
            {
                foreach (var seg in _snapGuides)
                {
                    int gx = Math.Min(seg.Item1.X, seg.Item2.X) - 2;
                    int gy = Math.Min(seg.Item1.Y, seg.Item2.Y) - 2;
                    int gw = Math.Abs(seg.Item2.X - seg.Item1.X) + 4;
                    int gh = Math.Abs(seg.Item2.Y - seg.Item1.Y) + 4;
                    reg.Union(new Rectangle(gx, gy, gw, gh));
                }
            }

            if (_isDraggingItem && _pressedItem != null)
            {
                Point cur = _dragItemLocation;
                using (var dragPath = Rounded(new Rectangle(cur.X - 20, cur.Y - 20, 41, 41), 8))
                {
                    reg.Union(dragPath);
                }
            }

            if (_isDrawingNewFence && _drawFenceRect.Width > 4 && _drawFenceRect.Height > 4)
            {
                using (var drawPath = Rounded(new Rectangle(_drawFenceRect.X, _drawFenceRect.Y, _drawFenceRect.Width + 1, _drawFenceRect.Height + 1), Fence.CornerRadius))
                {
                    reg.Union(drawPath);
                }
            }

            var prev = Region;
            Region = reg;
            prev?.Dispose();
        }

        ContextMenuStrip CanvasMenu { get; set; }

        void BuildCanvasContextMenu()
        {
            var ctx = new ContextMenuStrip();
            ctx.Items.Add("New fence", null, (s, e) => AddFenceAtCenter());
            ctx.Items.Add("Draw new fence with mouse…", null, (s, e) => ToggleDrawFenceMode());
            ctx.Items.Add("Auto-organize desktop icons", null, (s, e) => AutoOrganize());
            ctx.Items.Add("Configure Rules…", null, (s, e) => ShowRulesDialog());
            ctx.Items.Add(new ToolStripSeparator());

            var layoutsMenu = new ToolStripMenuItem("Apply Fences Layout Preset");
            foreach (var tpl in XmlStore.DiscoverFencesLayouts())
            {
                var captured = tpl;
                layoutsMenu.DropDownItems.Add(captured.Name, null, (s, e) =>
                {
                    XmlStore.ApplyFencesLayout(_fences, captured, new Rectangle(0, 0, Width, Height), ToolbarH);
                    AutoSave();
                    SetStatus("Applied layout: " + captured.Name);
                    UpdateWindowRegion();
                    Invalidate();
                });
            }
            ctx.Items.Add(layoutsMenu);

            var snapsMenu = new ToolStripMenuItem("Snapshots & Backups");
            snapsMenu.DropDownOpening += (s, e) => PopulateSnapshotsMenu(snapsMenu);
            ctx.Items.Add(snapsMenu);

            var pagesMenu = new ToolStripMenuItem("Desktop Pages (Alt+Left / Alt+Right)");
            for (int pIdx = 0; pIdx < MaxPages; pIdx++)
            {
                int capturedPage = pIdx;
                pagesMenu.DropDownItems.Add(new ToolStripMenuItem("Switch to Page " + (capturedPage + 1), null, (s, e) =>
                {
                    CurrentPage = capturedPage;
                }) { Checked = _currentPage == capturedPage });
            }
            ctx.Items.Add(pagesMenu);

            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Peek fences on top of open apps (Ctrl+Alt+Space)", null, (s, e) => TogglePeekOnTop());
            ctx.Items.Add("Roll all up", null, (s, e) => { foreach (var f in ActivePageFences) f.RolledUp = true; AutoSave(); UpdateWindowRegion(); Invalidate(); });
            ctx.Items.Add("Unroll all", null, (s, e) => { foreach (var f in ActivePageFences) f.RolledUp = false; AutoSave(); UpdateWindowRegion(); Invalidate(); });
            ctx.Items.Add("Quick-hide / show fences", null, (s, e) => ToggleQuickHide());

            var autoHideHudItem = new ToolStripMenuItem("Auto-hide top HUD pill until top edge hovered", null, (s, e) =>
            {
                _autoHideHud = !_autoHideHud;
                UpdateWindowRegion();
                Invalidate();
            });
            var singleClickItem = new ToolStripMenuItem("Single-click to launch items", null, (s, e) =>
            {
                _singleClickLaunch = !_singleClickLaunch;
                SetStatus(_singleClickLaunch ? "Single-click launch enabled" : "Double-click launch enabled");
            });
            var shellEmbedItem = new ToolStripMenuItem("Embed into Windows Progman shell layer", null, (s, e) =>
            {
                _embedInProgman = !_embedInProgman;
                DesktopHost.Embed(this, _embedInProgman);
                SetStatus(_embedInProgman ? "Embedded in Progman layer" : "Desktop-pinned overlay mode (no taskbar)");
                UpdateWindowRegion();
                Invalidate();
            });
            ctx.Opening += (s, e) =>
            {
                autoHideHudItem.Checked = _autoHideHud;
                singleClickItem.Checked = _singleClickLaunch;
                shellEmbedItem.Checked = _embedInProgman;
            };
            ctx.Items.Add(autoHideHudItem);
            ctx.Items.Add(singleClickItem);
            ctx.Items.Add(shellEmbedItem);

            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Save layout now", null, (s, e) => SaveLayout());
            ctx.Items.Add("Reload saved layout", null, (s, e) => LoadLayout());
            ctx.Items.Add("Exit Fontes", null, (s, e) => Close());
            CanvasMenu = ctx;
        }

        void PopulateSnapshotsMenu(ToolStripMenuItem snapsMenu)
        {
            snapsMenu.DropDownItems.Clear();
            snapsMenu.DropDownItems.Add("Take Snapshot Now…", null, (s, e) =>
            {
                string path = XmlStore.CreateManualSnapshot(_fences, "manual");
                SetStatus("Snapshot saved: " + Path.GetFileName(path));
            });
            snapsMenu.DropDownItems.Add(new ToolStripSeparator());
            var snaps = XmlStore.ListSnapshots();
            if (snaps.Count == 0)
            {
                snapsMenu.DropDownItems.Add("(No snapshots yet)").Enabled = false;
            }
            else
            {
                foreach (var snap in snaps.Take(12))
                {
                    string captured = snap;
                    snapsMenu.DropDownItems.Add("Restore " + Path.GetFileNameWithoutExtension(captured), null, (s, e) =>
                    {
                        var loaded = XmlStore.Load(captured);
                        if (loaded.Count > 0)
                        {
                            _fences = loaded;
                            AutoSave(false);
                            SetStatus("Restored snapshot: " + Path.GetFileNameWithoutExtension(captured));
                            UpdateWindowRegion();
                            Invalidate();
                        }
                    });
                }
            }
        }

        #region Floating Top-Center HUD Pill

        void BuildHudPill()
        {
            _hudButtons = new List<(string, Action)>
            {
                ("\u25C6 Fontes", ToggleHudCompact),
                ("+ Fence", AddFenceAtCenter),
                ("Draw", ToggleDrawFenceMode),
                ("Organize", AutoOrganize),
                ("Rules", ShowRulesDialog),
                ("Layouts", ShowLayoutsMenuFromToolbar),
                ("Snapshots", ShowSnapshotsMenuFromToolbar),
                ("Page 1/3", CycleDesktopPage),
                ("Roll All", ToggleRollAll),
                ("Hide", ToggleQuickHide),
                ("Exit", Close)
            };
        }

        void CycleDesktopPage()
        {
            CurrentPage = (_currentPage + 1) % MaxPages;
        }

        public void TogglePeekOnTop()
        {
            _peekOnTop = !_peekOnTop;
            if (_peekOnTop && _quickHide) _quickHide = false;
            DesktopHost.SetPeekTopMost(Handle, _peekOnTop);
            SetStatus(_peekOnTop
                ? "Peek on Top active \u2014 fences elevated over apps (press Ctrl+Alt+Space or Esc to return to desktop)"
                : "Fences pinned to desktop background");
            UpdateWindowRegion();
            Invalidate();
        }

        void ToggleHudCompact()
        {
            _hudCompact = !_hudCompact;
            _hudHover = -1;
            UpdateWindowRegion();
            Invalidate();
        }

        public bool IsDrawFenceHookInstalled => _drawFenceHook != IntPtr.Zero;

        public void ToggleDrawFenceMode()
        {
            _drawFenceMode = !_drawFenceMode;
            _isDrawingNewFence = false;
            _drawFenceMoveQueued = false;
            _drawFenceRect = Rectangle.Empty;
            if (_drawFenceMode)
            {
                InstallDrawFenceHook();
                if (IsHandleCreated && !IsDisposed) Activate();
            }
            else
            {
                UninstallDrawFenceHook();
            }
            Cursor = _drawFenceMode ? Cursors.Cross : Cursors.Default;
            UpdateWindowRegion();
            Invalidate();
        }

        void InstallDrawFenceHook()
        {
            if (_drawFenceHook != IntPtr.Zero) return;
            _drawFenceHookProc = DrawFenceMouseHookCallback;
            IntPtr hMod = DesktopHost.GetModuleHandle(null);
            _drawFenceHook = DesktopHost.SetWindowsHookEx(DesktopHost.WH_MOUSE_LL, _drawFenceHookProc, hMod, 0);
        }

        void UninstallDrawFenceHook()
        {
            if (_drawFenceHook != IntPtr.Zero)
            {
                DesktopHost.UnhookWindowsHookEx(_drawFenceHook);
                _drawFenceHook = IntPtr.Zero;
            }
        }

        void FinishDrawFenceMode(Rectangle? finalRectOverride = null)
        {
            var rect = finalRectOverride ?? _drawFenceRect;
            _isDrawingNewFence = false;
            _drawFenceMoveQueued = false;
            _drawFenceMode = false;
            _drawFenceRect = Rectangle.Empty;
            UninstallDrawFenceHook();
            Cursor = Cursors.Default;
            if (rect.Width >= 120 && rect.Height >= 90)
            {
                AddFenceAt(rect.Location, rect.Size);
                SetStatus("Created new fence (" + rect.Width + "x" + rect.Height + ")");
            }
            UpdateWindowRegion();
            Invalidate();
        }

        IntPtr DrawFenceMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _drawFenceMode && IsHandleCreated && !IsDisposed)
            {
                int msg = wParam.ToInt32();
                if (msg == DesktopHost.WM_RBUTTONDOWN)
                {
                    _isDrawingNewFence = false;
                    _drawFenceMoveQueued = false;
                    BeginInvoke((Action)(() =>
                    {
                        if (_drawFenceMode) ToggleDrawFenceMode();
                    }));
                    return new IntPtr(1);
                }
                if (msg == DesktopHost.WM_LBUTTONDOWN ||
                    (msg == DesktopHost.WM_MOUSEMOVE && _isDrawingNewFence) ||
                    (msg == DesktopHost.WM_LBUTTONUP && _isDrawingNewFence))
                {
                    var info = (DesktopHost.MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(
                        lParam, typeof(DesktopHost.MSLLHOOKSTRUCT));
                    Point clientPt = new Point(info.pt.x - Bounds.X, info.pt.y - Bounds.Y);

                    if (msg == DesktopHost.WM_LBUTTONDOWN)
                    {
                        bool onHud = IsHudExpanded && GetHudPillBounds().Contains(clientPt);
                        bool onToast = IsStatusToastVisible && GetStatusPillBounds().Contains(clientPt);
                        if (!onHud && !onToast && ClientRectangle.Contains(clientPt))
                        {
                            _isDrawingNewFence = true;
                            _drawFenceMoveQueued = false;
                            _drawFenceStart = clientPt;
                            _drawFenceRect = new Rectangle(clientPt.X, clientPt.Y, 1, 1);
                            BeginInvoke((Action)(() =>
                            {
                                if (!IsDisposed && IsHandleCreated)
                                {
                                    Activate();
                                    Invalidate();
                                    UpdateWindowRegion();
                                }
                            }));
                            return new IntPtr(1);
                        }
                    }
                    else if (msg == DesktopHost.WM_MOUSEMOVE && _isDrawingNewFence)
                    {
                        int x = Math.Min(_drawFenceStart.X, clientPt.X);
                        int y = Math.Max(0, Math.Min(_drawFenceStart.Y, clientPt.Y));
                        int w = Math.Abs(clientPt.X - _drawFenceStart.X);
                        int h = Math.Abs(clientPt.Y - _drawFenceStart.Y);
                        _drawFenceRect = new Rectangle(x, y, w, h);
                        if (!_drawFenceMoveQueued)
                        {
                            _drawFenceMoveQueued = true;
                            BeginInvoke((Action)(() =>
                            {
                                _drawFenceMoveQueued = false;
                                if (!IsDisposed && IsHandleCreated && _isDrawingNewFence)
                                {
                                    Invalidate();
                                    UpdateWindowRegion();
                                    Update();
                                }
                            }));
                        }
                    }
                    else if (msg == DesktopHost.WM_LBUTTONUP && _isDrawingNewFence)
                    {
                        var finalRect = _drawFenceRect;
                        _isDrawingNewFence = false;
                        _drawFenceMoveQueued = false;
                        BeginInvoke((Action)(() => FinishDrawFenceMode(finalRect)));
                        return new IntPtr(1);
                    }
                }
            }
            return DesktopHost.CallNextHookEx(_drawFenceHook, nCode, wParam, lParam);
        }

        void ToggleRollAll()
        {
            bool anyUnrolled = _fences.Any(f => !f.RolledUp);
            foreach (var f in _fences)
            {
                f.RolledUp = anyUnrolled;
                f.IsHoverExpanded = false;
            }
            AutoSave();
            UpdateWindowRegion();
            Invalidate();
        }

        int ActiveHudButtonCount => _hudCompact ? 1 : _hudButtons.Count;

        string GetHudButtonLabel(int index)
        {
            if (index == 0) return _hudCompact ? "\u25C6 Fontes \u25BE" : "\u25C6 Fontes";
            if (index == 2 && _drawFenceMode) return "Cancel Draw";
            if (index == 7) return "Page " + (_currentPage + 1) + "/" + MaxPages;
            if (index == 8) return ActivePageFences.Any(f => !f.RolledUp) ? "Roll All" : "Unroll All";
            if (index == 9) return _quickHide ? "Show" : "Hide";
            return _hudButtons[index].Item1;
        }

        int GetHudButtonWidth(int index)
        {
            if (index == 0) return _hudCompact ? 98 : 84;
            string lbl = GetHudButtonLabel(index);
            return Math.Max(56, lbl.Length * 7 + 18);
        }

        Rectangle GetExpandedHudPillBounds()
        {
            const int sidePad = 8, gap = 4;
            int count = ActiveHudButtonCount;
            int totalW = sidePad * 2;
            for (int i = 0; i < count; i++)
            {
                totalW += GetHudButtonWidth(i);
                if (i < count - 1) totalW += gap;
            }
            int x = Math.Max(12, (Width - totalW) / 2);
            return new Rectangle(x, HudTopY, totalW, HudHeight);
        }

        Rectangle GetHudPillBounds()
        {
            return GetExpandedHudPillBounds();
        }

        Rectangle HudButtonRect(int index)
        {
            var pill = GetHudPillBounds();
            const int sidePad = 8, gap = 4, bh = 24;
            int x = pill.X + sidePad;
            for (int i = 0; i < index; i++)
                x += GetHudButtonWidth(i) + gap;
            int bw = GetHudButtonWidth(index);
            return new Rectangle(x, pill.Y + (HudHeight - bh) / 2, bw, bh);
        }

        string GetActiveStatusText()
        {
            if (_drawFenceMode)
                return "Draw Fence Mode \u2014 Drag anywhere on screen to draw a new fence (Esc or right-click to cancel)";
            if (_peek)
                return "Peek active \u2014 fences tucked away (release Ctrl to show)";
            if (_quickHide)
                return "Fences hidden \u2014 click 'Show' in top pill or double-click tray icon to restore";
            if (DateTime.Now < _statusUntil && !string.IsNullOrEmpty(_statusBanner))
                return "\u2713 " + _statusBanner;
            return "";
        }

        Rectangle GetStatusPillBounds()
        {
            string msg = GetActiveStatusText();
            int w = Math.Max(220, Math.Min(Width - 40, msg.Length * 7 + 34));
            return new Rectangle((Width - w) / 2, HudTopY + HudHeight + 6, w, 26);
        }

        void ShowLayoutsMenuFromToolbar()
        {
            var m = new ContextMenuStrip();
            foreach (var tpl in XmlStore.DiscoverFencesLayouts())
            {
                var captured = tpl;
                m.Items.Add(captured.Name, null, (s, e) =>
                {
                    XmlStore.ApplyFencesLayout(_fences, captured, new Rectangle(0, 0, Width, Height), ToolbarH);
                    AutoSave();
                    SetStatus("Applied layout: " + captured.Name);
                    UpdateWindowRegion();
                    Invalidate();
                });
            }
            var r = HudButtonRect(5);
            m.Show(this, new Point(r.Left, GetHudPillBounds().Bottom + 2));
        }

        void ShowSnapshotsMenuFromToolbar()
        {
            var m = new ContextMenuStrip();
            var root = new ToolStripMenuItem("Snapshots");
            PopulateSnapshotsMenu(root);
            foreach (ToolStripItem item in root.DropDownItems.Cast<ToolStripItem>().ToList())
                m.Items.Add(item);
            var r = HudButtonRect(6);
            m.Show(this, new Point(r.Left, GetHudPillBounds().Bottom + 2));
        }

        void PaintHudPill(Graphics g, bool forceVisible = false)
        {
            if (!forceVisible && !IsHudExpanded && !IsStatusToastVisible)
                return;

            if (forceVisible || IsHudExpanded)
            {
                var pill = GetHudPillBounds();
                using (var pillPath = Rounded(pill, HudHeight / 2))
                {
                    using (var bg = new LinearGradientBrush(pill, Color.FromArgb(245, 28, 32, 46), Color.FromArgb(245, 16, 18, 26), LinearGradientMode.Vertical))
                        g.FillPath(bg, pillPath);

                    var borderRect = new Rectangle(pill.X, pill.Y, pill.Width - 1, pill.Height - 1);
                    using (var borderPath = Rounded(borderRect, HudHeight / 2))
                    using (var pen = new Pen(Color.FromArgb(115, 255, 255, 255), 1f))
                        g.DrawPath(pen, borderPath);
                }

                int count = ActiveHudButtonCount;
                for (int i = 0; i < count; i++)
                {
                    var r = HudButtonRect(i);
                    bool isHover = (_hudHover == i);
                    bool isActiveToggle = (i == 2 && _drawFenceMode) || (i == 9 && _quickHide);
                    Color btnFill = isActiveToggle
                        ? Color.FromArgb(210, 62, 118, 210)
                        : (isHover ? Color.FromArgb(190, 68, 78, 108) : (i == 0 ? Color.FromArgb(160, 42, 52, 76) : Color.FromArgb(120, 36, 38, 50)));

                    using (var bp = Rounded(r, 12))
                    using (var fill = new SolidBrush(btnFill))
                    {
                        g.FillPath(fill, bp);
                        if (isHover || i == 0 || isActiveToggle)
                        {
                            using (var pen = new Pen(Color.FromArgb(isHover || isActiveToggle ? 150 : 75, 255, 255, 255), 1f))
                                g.DrawPath(pen, bp);
                        }
                    }

                    var font = i == 0 ? _hudBrandFont : _hudFont;
                    var fg = i == 0 ? Color.FromArgb(155, 205, 255) : Color.FromArgb(240, 244, 252);
                    GfxText.Center(g, GetHudButtonLabel(i), r, Brushes.White, font, fg);
                }
            }

            if (IsStatusToastVisible)
            {
                var toastRect = GetStatusPillBounds();
                string msg = GetActiveStatusText();
                using (var tp = Rounded(toastRect, 13))
                using (var bg = new SolidBrush(Color.FromArgb(228, 18, 22, 32)))
                {
                    g.FillPath(bg, tp);
                    var br = new Rectangle(toastRect.X, toastRect.Y, toastRect.Width - 1, toastRect.Height - 1);
                    using (var bp = Rounded(br, 13))
                    using (var pen = new Pen(Color.FromArgb(110, 130, 200, 255), 1f))
                        g.DrawPath(pen, bp);
                }
                Color statusColor = _drawFenceMode
                    ? Color.FromArgb(145, 210, 255)
                    : Color.FromArgb(140, 235, 180);
                GfxText.Center(g, msg, toastRect, Brushes.White, _hudFont, statusColor);
            }
        }

        #endregion

        #region Per-Fence Context Menu & Customization Dialogs

        void ShowFenceMenu(Fence f, Point at)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Rename fence…", null, (s, e) => RenameFence(f));
            m.Items.Add("Customize color, opacity & style…", null, (s, e) => CustomizeFence(f));
            m.Items.Add("Roll up / unroll", null, (s, e) => { f.RollUp(); AutoSave(); UpdateWindowRegion(); Invalidate(); });

            var hoverRollItem = new ToolStripMenuItem("Expand on mouse hover when rolled up", null, (s, e) =>
            {
                f.RollupOnHover = !f.RollupOnHover;
                AutoSave();
                Invalidate();
            }) { Checked = f.RollupOnHover };
            m.Items.Add(hoverRollItem);

            var labelMenu = new ToolStripMenuItem("Title bar visibility");
            foreach (TitleBarMode mode in Enum.GetValues(typeof(TitleBarMode)))
            {
                var captured = mode;
                labelMenu.DropDownItems.Add(new ToolStripMenuItem(captured.ToString(), null, (s, e) =>
                {
                    f.LabelMode = captured;
                    AutoSave();
                    Invalidate();
                }) { Checked = f.LabelMode == captured });
            }
            m.Items.Add(labelMenu);

            var sortMenu = new ToolStripMenuItem("Sort items by");
            foreach (var sm in new[] { "Name", "Category", "TimesOpened", "DateModified" })
            {
                string captured = sm;
                sortMenu.DropDownItems.Add(new ToolStripMenuItem(captured, null, (s, e) =>
                {
                    if (string.Equals(f.SortMethod, captured, StringComparison.OrdinalIgnoreCase))
                        f.SortAscending = !f.SortAscending;
                    else
                    {
                        f.SortMethod = captured;
                        f.SortAscending = true;
                    }
                    f.SortItems();
                    f.AutoArrange();
                    AutoSave();
                    Invalidate();
                }) { Checked = string.Equals(f.SortMethod, captured, StringComparison.OrdinalIgnoreCase) });
            }
            m.Items.Add(sortMenu);

            m.Items.Add("Filter / Search items in fence… (Ctrl+F)", null, (s, e) => ToggleFenceSearch(f));
            if (!string.IsNullOrEmpty(f.SearchFilter))
            {
                m.Items.Add("Clear search filter ('" + f.SearchFilter + "')", null, (s, e) =>
                {
                    f.SearchFilter = "";
                    f.IsSearching = false;
                    if (_activeSearchFence == f) _activeSearchFence = null;
                    f.ScrollOffset = 0;
                    f.AutoArrange();
                    Invalidate();
                });
            }

            var pageMoveMenu = new ToolStripMenuItem("Move fence to Desktop Page");
            for (int pIdx = 0; pIdx < MaxPages; pIdx++)
            {
                int capturedPage = pIdx;
                pageMoveMenu.DropDownItems.Add(new ToolStripMenuItem("Page " + (capturedPage + 1), null, (s, e) =>
                {
                    f.PageIndex = capturedPage;
                    AutoSave();
                    SetStatus("Moved '" + f.Title + "' to Page " + (capturedPage + 1));
                    UpdateWindowRegion();
                    Invalidate();
                }) { Checked = f.PageIndex == capturedPage });
            }
            m.Items.Add(pageMoveMenu);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Bind Folder Portal…", null, (s, e) => BindFolderPortal(f));
            if (!string.IsNullOrEmpty(f.PortalPath))
            {
                m.Items.Add("Open Portal Folder in Explorer", null, (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo { FileName = f.ActivePortalDirectory, UseShellExecute = true }); } catch { }
                });
                m.Items.Add("Refresh Folder Portal", null, (s, e) => RefreshPortal(f));
                m.Items.Add("Unbind Folder Portal", null, (s, e) =>
                {
                    f.PortalPath = null;
                    f.CurrentPortalPath = null;
                    f.PortalHistory.Clear();
                    AutoSave();
                    Invalidate();
                });
            }

            m.Items.Add(new ToolStripSeparator());
            var snapItem = new ToolStripMenuItem("Snap to grid & adjacent fences", null, (s, e) =>
            {
                f.SnapToGrid = !f.SnapToGrid;
                AutoSave();
                Invalidate();
            }) { Checked = f.SnapToGrid };
            m.Items.Add(snapItem);

            var lockItem = new ToolStripMenuItem("Lock fence position & size", null, (s, e) =>
            {
                f.Locked = !f.Locked;
                AutoSave();
                Invalidate();
            }) { Checked = f.Locked };
            m.Items.Add(lockItem);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Delete fence", null, (s, e) =>
            {
                _fences.Remove(f);
                AutoSave();
                UpdateWindowRegion();
                Invalidate();
            });
            m.Show(this, at);
        }

        void ToggleFenceSearch(Fence f)
        {
            if (f == null) return;
            if (_activeSearchFence == f && f.IsSearching)
            {
                f.IsSearching = false;
                f.SearchFilter = "";
                _activeSearchFence = null;
            }
            else
            {
                foreach (var other in _fences) other.IsSearching = false;
                f.IsSearching = true;
                if (f.RolledUp) f.RolledUp = false;
                _activeSearchFence = f;
                SetStatus("Type to filter '" + f.Title + "' (Backspace to edit, Esc to clear)");
            }
            f.ScrollOffset = 0;
            f.AutoArrange();
            UpdateWindowRegion();
            Invalidate();
        }

        void RenameFence(Fence f)
        {
            _inModalDialog = true;
            try
            {
                using (var dlg = new Form
            {
                Text = "Rename Fence",
                Size = new Size(340, 145),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 38),
                ForeColor = Color.FromArgb(225, 228, 236),
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                var lbl = new Label { Text = "Fence title:", Location = new Point(14, 14), AutoSize = true };
                var txt = new TextBox { Text = f.Title, Location = new Point(14, 36), Width = 295 };
                var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(145, 72), Width = 78 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(231, 72), Width = 78 };
                dlg.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(txt.Text))
                {
                    f.Title = txt.Text.Trim();
                    AutoSave();
                    Invalidate();
                }
            }
            }
            finally
            {
                _inModalDialog = false;
                if (!_peekOnTop) DesktopHost.SendToDesktopBottom(Handle);
            }
        }

        void CustomizeFence(Fence f)
        {
            _inModalDialog = true;
            try
            {
            using (var dlg = new Form
            {
                Text = "Customize Fence — " + f.Title,
                Size = new Size(400, 330),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 38),
                ForeColor = Color.FromArgb(225, 228, 236),
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                var lblOp = new Label { Text = "Background Opacity (" + f.Opacity + "/255):", Location = new Point(16, 18), AutoSize = true };
                var trkOp = new TrackBar
                {
                    Minimum = 30,
                    Maximum = 255,
                    Value = Math.Max(30, Math.Min(255, f.Opacity)),
                    TickFrequency = 25,
                    Location = new Point(16, 40),
                    Width = 345
                };
                trkOp.Scroll += (s, e) =>
                {
                    f.Opacity = trkOp.Value;
                    lblOp.Text = "Background Opacity (" + f.Opacity + "/255):";
                    Invalidate();
                };

                var btnShade = new Button
                {
                    Text = "Pick Background Color…",
                    Location = new Point(16, 95),
                    Size = new Size(165, 30),
                    BackColor = Color.FromArgb(255, f.Shade.R, f.Shade.G, f.Shade.B)
                };
                btnShade.Click += (s, e) =>
                {
                    using (var cd = new ColorDialog { Color = Color.FromArgb(255, f.Shade.R, f.Shade.G, f.Shade.B), FullOpen = true })
                    {
                        if (cd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            f.Shade = Color.FromArgb(f.Opacity, cd.Color.R, cd.Color.G, cd.Color.B);
                            btnShade.BackColor = cd.Color;
                            Invalidate();
                        }
                    }
                };

                var btnText = new Button
                {
                    Text = "Pick Title/Label Color…",
                    Location = new Point(195, 95),
                    Size = new Size(165, 30),
                    BackColor = Color.FromArgb(48, 48, 60)
                };
                btnText.Click += (s, e) =>
                {
                    using (var cd = new ColorDialog { Color = f.TextColor, FullOpen = true })
                    {
                        if (cd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            f.TextColor = cd.Color;
                            Invalidate();
                        }
                    }
                };

                var btnTint = new Button
                {
                    Text = f.TintColor.HasValue ? "Change Icon Tint…" : "Enable Icon Color Tint…",
                    Location = new Point(16, 138),
                    Size = new Size(165, 30),
                    BackColor = Color.FromArgb(48, 48, 60)
                };
                var btnClearTint = new Button
                {
                    Text = "Clear Icon Tint",
                    Location = new Point(195, 138),
                    Size = new Size(165, 30),
                    BackColor = Color.FromArgb(48, 48, 60)
                };
                btnTint.Click += (s, e) =>
                {
                    using (var cd = new ColorDialog { Color = f.TintColor ?? Color.FromArgb(140, 190, 255), FullOpen = true })
                    {
                        if (cd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            f.TintColor = cd.Color;
                            foreach (var it in f.Items) it.InvalidateIconCache();
                            Invalidate();
                        }
                    }
                };
                btnClearTint.Click += (s, e) =>
                {
                    f.TintColor = null;
                    foreach (var it in f.Items) it.InvalidateIconCache();
                    Invalidate();
                };

                var lblRule = new Label { Text = "Auto-Organize Category Rule (e.g. Apps, Games, Docs, Web):", Location = new Point(16, 184), AutoSize = true };
                var txtRule = new TextBox { Text = f.RuleCategory ?? "", Location = new Point(16, 206), Width = 345 };

                var ok = new Button { Text = "Done", DialogResult = DialogResult.OK, Location = new Point(275, 248), Width = 86, Height = 30 };
                dlg.Controls.AddRange(new Control[] { lblOp, trkOp, btnShade, btnText, btnTint, btnClearTint, lblRule, txtRule, ok });
                dlg.AcceptButton = ok;

                dlg.ShowDialog(this);
                f.RuleCategory = string.IsNullOrWhiteSpace(txtRule.Text) ? null : txtRule.Text.Trim();
                AutoSave();
                Invalidate();
            }
            }
            finally
            {
                _inModalDialog = false;
                if (!_peekOnTop) DesktopHost.SendToDesktopBottom(Handle);
            }
        }

        void ShowRulesDialog()
        {
            _inModalDialog = true;
            try
            {
            using (var dlg = new Form
            {
                Text = "Fontes Auto-Organization Rules (Stardock Fences Rule Engine)",
                Size = new Size(560, 400),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(28, 28, 36),
                ForeColor = Color.FromArgb(225, 228, 236),
                MaximizeBox = false,
                MinimizeBox = false
            })
            {
                var lst = new ListBox
                {
                    Location = new Point(14, 14),
                    Size = new Size(515, 165),
                    BackColor = Color.FromArgb(20, 20, 26),
                    ForeColor = Color.White
                };

                void RefreshList()
                {
                    lst.Items.Clear();
                    foreach (var r in RuleEngine.CustomRules)
                    {
                        lst.Items.Add(string.Format("[{0}] {1} -> Fence '{2}' (ext: {3}, name: {4}, target: {5})",
                            r.Enabled ? "ON" : "OFF", r.Name, r.TargetFenceTitle,
                            string.IsNullOrEmpty(r.ExtensionFilter) ? "*" : r.ExtensionFilter,
                            string.IsNullOrEmpty(r.NamePattern) ? "*" : r.NamePattern,
                            string.IsNullOrEmpty(r.TargetPattern) ? "*" : r.TargetPattern));
                    }
                }
                RefreshList();

                var lblName = new Label { Text = "Rule Name:", Location = new Point(14, 190), AutoSize = true };
                var txtName = new TextBox { Text = "Dev Tools", Location = new Point(14, 210), Width = 140 };

                var lblFence = new Label { Text = "Target Fence Title:", Location = new Point(168, 190), AutoSize = true };
                var txtFence = new TextBox { Text = "Dev", Location = new Point(168, 210), Width = 140 };

                var lblExt = new Label { Text = "Extensions (e.g. .exe,.lnk):", Location = new Point(322, 190), AutoSize = true };
                var txtExt = new TextBox { Text = ".exe,.lnk", Location = new Point(322, 210), Width = 205 };

                var lblNamePat = new Label { Text = "Name Contains:", Location = new Point(14, 245), AutoSize = true };
                var txtNamePat = new TextBox { Text = "", Location = new Point(14, 265), Width = 240 };

                var lblTargetPat = new Label { Text = "Target Path Contains:", Location = new Point(270, 245), AutoSize = true };
                var txtTargetPat = new TextBox { Text = "Code", Location = new Point(270, 265), Width = 257 };

                var btnAdd = new Button { Text = "Add Rule", Location = new Point(14, 310), Size = new Size(105, 32) };
                var btnToggle = new Button { Text = "Toggle ON/OFF", Location = new Point(128, 310), Size = new Size(115, 32) };
                var btnDelete = new Button { Text = "Delete Rule", Location = new Point(252, 310), Size = new Size(105, 32) };
                var btnRunNow = new Button { Text = "Run Rules Now", Location = new Point(366, 310), Size = new Size(162, 32) };

                btnAdd.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(txtFence.Text)) return;
                    RuleEngine.CustomRules.Add(new FenceRule
                    {
                        Name = string.IsNullOrWhiteSpace(txtName.Text) ? "Custom Rule" : txtName.Text.Trim(),
                        TargetFenceTitle = txtFence.Text.Trim(),
                        ExtensionFilter = txtExt.Text.Trim(),
                        NamePattern = txtNamePat.Text.Trim(),
                        TargetPattern = txtTargetPat.Text.Trim(),
                        Enabled = true
                    });
                    RefreshList();
                    AutoSave();
                };

                btnToggle.Click += (s, e) =>
                {
                    int idx = lst.SelectedIndex;
                    if (idx >= 0 && idx < RuleEngine.CustomRules.Count)
                    {
                        RuleEngine.CustomRules[idx].Enabled = !RuleEngine.CustomRules[idx].Enabled;
                        RefreshList();
                        AutoSave();
                    }
                };

                btnDelete.Click += (s, e) =>
                {
                    int idx = lst.SelectedIndex;
                    if (idx >= 0 && idx < RuleEngine.CustomRules.Count)
                    {
                        RuleEngine.CustomRules.RemoveAt(idx);
                        RefreshList();
                        AutoSave();
                    }
                };

                btnRunNow.Click += (s, e) =>
                {
                    AutoOrganize();
                    dlg.Close();
                };

                dlg.Controls.AddRange(new Control[]
                {
                    lst, lblName, txtName, lblFence, txtFence, lblExt, txtExt,
                    lblNamePat, txtNamePat, lblTargetPat, txtTargetPat,
                    btnAdd, btnToggle, btnDelete, btnRunNow
                });
                dlg.ShowDialog(this);
            }
            }
            finally
            {
                _inModalDialog = false;
                if (!_peekOnTop) DesktopHost.SendToDesktopBottom(Handle);
            }
        }

        void BindFolderPortal(Fence f)
        {
            _inModalDialog = true;
            try
            {
            using (var d = new FolderBrowserDialog { Description = "Choose a folder to mirror into '" + f.Title + "' as a live Folder Portal" })
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    f.PortalPath = d.SelectedPath;
                    f.CurrentPortalPath = d.SelectedPath;
                    f.PortalHistory.Clear();
                    f.Items = PortalScanner.ScanFolder(d.SelectedPath, f.PortalPath);
                    f.ScrollOffset = 0;
                    f.AutoArrange();
                    AutoSave();
                    SetStatus("Bound Folder Portal: " + Path.GetFileName(d.SelectedPath));
                    UpdateWindowRegion();
                    Invalidate();
                }
            }
            }
            finally
            {
                _inModalDialog = false;
                if (!_peekOnTop) DesktopHost.SendToDesktopBottom(Handle);
            }
        }

        void RefreshPortal(Fence f)
        {
            if (string.IsNullOrEmpty(f.PortalPath)) return;
            string dir = f.ActivePortalDirectory;
            if (!Directory.Exists(dir)) dir = f.PortalPath;
            f.CurrentPortalPath = dir;
            f.Items = PortalScanner.ScanFolder(dir, f.PortalPath);
            f.SortItems();
            f.AutoArrange();
            Invalidate();
        }

        void NavigatePortalTo(Fence f, string targetFolder)
        {
            if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder)) return;
            if (string.IsNullOrEmpty(f.PortalPath))
                f.PortalPath = targetFolder;

            f.CurrentPortalPath = targetFolder;
            f.Items = PortalScanner.ScanFolder(targetFolder, f.PortalPath);
            f.ScrollOffset = 0;
            f.AutoArrange();
            SetStatus("Portal: " + f.DisplayTitle);
            Invalidate();
        }

        #endregion

        #region Fence Lifecycle & Auto-Organize

        void AddFenceAtCenter()
        {
            int w = Fence.WidthForColumns(3);
            int h = Fence.HeightForRows(2);
            AddFenceAt(new Point((Width - w) / 2, (Height - h) / 2), new Size(w, h));
        }

        void AddFenceAt(Point pt, Size? size = null)
        {
            int w = size?.Width ?? Fence.WidthForColumns(3);
            int h = size?.Height ?? Fence.HeightForRows(2);
            var f = new Fence
            {
                Title = "New Fence " + (_fences.Count + 1),
                Rect = new Rectangle(Math.Max(12, pt.X), Math.Max(56, pt.Y), w, h),
                Z = _fences.Count == 0 ? 0 : _fences.Max(x => x.Z) + 1,
                Shade = Shade(44, 54, 74),
                Opacity = 210,
                PageIndex = _currentPage
            };
            f.SnapHeightToRows();
            _fences.Add(f);
            AutoSave();
            UpdateWindowRegion();
            Invalidate();
        }

        void CreateWelcomeFences()
        {
            var scanned = DesktopScanner.Scan();
            if (scanned.Count > 0)
            {
                AutoOrganize();
                return;
            }

            int w = Fence.WidthForColumns(4);
            int h = Fence.HeightForRows(3);
            _fences.Add(MakeFence("Apps", new Rectangle(36, 56, w, h), Shade(42, 54, 76), "Apps"));
            _fences.Add(MakeFence("Games", new Rectangle(36 + w + 18, 56, w, h), Shade(50, 42, 74), "Games"));
        }

        Fence MakeFence(string title, Rectangle r, Color c, string ruleCat = null)
        {
            return new Fence { Title = title, Rect = r, Z = _fences.Count, Shade = c, Opacity = c.A, RuleCategory = ruleCat };
        }

        Color Shade(int r, int g, int b) => Color.FromArgb(210, r, g, b);

        public void AutoOrganize()
        {
            var items = DesktopScanner.Scan();
            var groups = RuleEngine.GroupByCategory(items);

            string[] preferredOrder = { "Apps", "Games", "Web", "Docs", "Folders", "Video", "Audio", "Images", "Files", "Misc" };
            var orderedCategories = groups.Keys
                .OrderBy(k =>
                {
                    int idx = Array.FindIndex(preferredOrder, p => string.Equals(p, k, StringComparison.OrdinalIgnoreCase));
                    return idx >= 0 ? idx : 99;
                })
                .ThenBy(k => k)
                .ToList();

            // Prune empty non-portal default/auto fences whose category has 0 items
            _fences.RemoveAll(f =>
                string.IsNullOrEmpty(f.PortalPath) &&
                (f.Items.Count == 0 || !string.IsNullOrEmpty(f.RuleCategory)) &&
                (string.Equals(f.Title, "Web & Docs", StringComparison.OrdinalIgnoreCase) ||
                 (!groups.ContainsKey(f.RuleCategory ?? "") && !groups.ContainsKey(f.Title ?? "") && f.Items.Count == 0)));

            foreach (var category in orderedCategories)
            {
                var catItems = groups[category];
                var f = _fences.FirstOrDefault(x =>
                    string.Equals(x.RuleCategory, category, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Title, category, StringComparison.OrdinalIgnoreCase));

                int cols = catItems.Count >= 12 ? 4 : 3;
                int rows = catItems.Count >= 12 ? 4 : (catItems.Count > 3 ? 2 : 1);
                Size desiredSize = new Size(Fence.WidthForColumns(cols), Fence.HeightForRows(rows));

                if (f == null)
                {
                    Color c = CatShade(category);
                    f = new Fence
                    {
                        Title = category,
                        RuleCategory = category,
                        Shade = c,
                        Opacity = c.A,
                        Z = _fences.Count == 0 ? 0 : _fences.Max(x => x.Z) + 1,
                        Rect = NextFreeRect(desiredSize)
                    };
                    _fences.Add(f);
                }
                if (string.IsNullOrEmpty(f.PortalPath))
                {
                    f.Items = catItems.ToList();
                    f.SortItems();
                    f.SnapHeightToRows();
                    f.AutoArrange();
                }
            }

            // Final pass: remove any 0-item welcome placeholder fence
            _fences.RemoveAll(f =>
                string.IsNullOrEmpty(f.PortalPath) &&
                f.Items.Count == 0 &&
                (string.Equals(f.Title, "Web & Docs", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(f.RuleCategory)));

            AutoSave();
            SetStatus("Organized " + items.Count + " desktop shortcut(s) into " + _fences.Count + " fence(s)");
            UpdateWindowRegion();
            Invalidate();
        }

        Color CatShade(string cat)
        {
            var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                {"Apps",    Shade(38, 48, 68)},
                {"Games",   Shade(46, 38, 68)},
                {"Web",     Shade(34, 54, 72)},
                {"Docs",    Shade(38, 58, 52)},
                {"Folders", Shade(54, 56, 40)},
                {"Video",   Shade(58, 38, 54)},
                {"Audio",   Shade(58, 50, 36)},
                {"Images",  Shade(52, 58, 42)},
                {"Files",   Shade(40, 42, 52)},
                {"Misc",    Shade(38, 46, 48)},
            };
            return map.TryGetValue(cat, out var c) ? c : Shade(40, 46, 56);
        }

        Rectangle NextFreeRect(Size? size = null)
        {
            int w = size?.Width ?? Fence.WidthForColumns(3);
            int h = size?.Height ?? Fence.HeightForRows(2);
            const int startX = 36, startY = 56, gap = 18;

            int curX = startX;
            int curY = startY;
            for (int i = 0; i < _fences.Count; i++)
            {
                var existing = _fences[i].Rect;
                curX = existing.Right + gap;
                curY = existing.Top;
                if (curX + w > Width - 24)
                {
                    curX = startX;
                    curY = _fences.Max(f => f.Rect.Bottom) + gap;
                }
            }
            if (curY + h > Height - 20)
                curY = startY + ((_fences.Count * 32) % 180);
            return new Rectangle(curX, curY, w, h);
        }

        #endregion

        #region Persistence

        void AutoSave(bool dailySnapshot = true)
        {
            try { XmlStore.Save(_fences, null, dailySnapshot); } catch { }
        }

        void SaveLayout()
        {
            try
            {
                XmlStore.Save(_fences, null, true);
                SetStatus("Saved " + _fences.Count + " fence(s) & daily snapshot");
                UpdateWindowRegion();
                Invalidate();
            }
            catch (Exception ex) { MessageBox.Show("Save failed: " + ex.Message); }
        }

        void LoadLayout()
        {
            var loaded = XmlStore.Load();
            if (loaded.Count > 0)
            {
                _fences = loaded;
                foreach (var f in _fences) f.AutoArrange();
                SetStatus("Loaded " + loaded.Count + " fence(s)");
                UpdateWindowRegion();
                Invalidate();
            }
            else SetStatus("No saved layout found");
        }

        void SetStatus(string s)
        {
            _statusBanner = s;
            _statusUntil = DateTime.Now.AddSeconds(4);
            Text = "Fontes — " + s;
            UpdateWindowRegion();
            Invalidate();
        }

        void LoadFences()
        {
            var loaded = XmlStore.Load();
            if (loaded.Count > 0)
            {
                _fences = loaded;
                AutoSave(false);
            }
        }

        #endregion

        #region Launch & Portal Navigation

        void ActivateItem(ShortcutItem it, Fence from)
        {
            if (it == null || from == null) return;

            bool isDir = it.IsFolderPortal || it.IsParentNavigation || (!string.IsNullOrEmpty(it.Path) && Directory.Exists(it.Path));
            if (isDir && !string.IsNullOrEmpty(from.PortalPath))
            {
                NavigatePortalTo(from, it.Path ?? it.Target);
                return;
            }

            it.TimesOpened++;
            AutoSave(false);
            if (_peekOnTop)
            {
                _peekOnTop = false;
                DesktopHost.SetPeekTopMost(Handle, false);
            }
            Launch(it, from);
            SetStatus("Launched: " + it.DisplayText);
        }

        void Launch(ShortcutItem it, Fence from)
        {
            if (it.IsFolderPortal || (it.Path != null && Directory.Exists(it.Path)))
            {
                try { Process.Start(new ProcessStartInfo { FileName = it.Path ?? it.Target, UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show("Could not open folder: " + ex.Message); }
                return;
            }
            string target = !string.IsNullOrEmpty(it.Path) && File.Exists(it.Path) && it.IsLink
                ? it.Path
                : (!string.IsNullOrEmpty(it.Target) ? it.Target : it.Path);

            try
            {
                var psi = new ProcessStartInfo { FileName = target, UseShellExecute = true };
                if (!it.IsLink && !string.IsNullOrEmpty(it.Arguments))
                    psi.Arguments = it.Arguments;
                if (!string.IsNullOrEmpty(it.WorkingDir) && Directory.Exists(it.WorkingDir))
                    psi.WorkingDirectory = it.WorkingDir;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show("Could not launch '" + it.DisplayText + "': " + ex.Message); }
        }

        #endregion

        #region Hit Testing & Magnetic Snapping

        Fence HitFence(Point p)
        {
            foreach (var f in ActivePageFences.OrderByDescending(x => x.Z))
                if (f.EffectiveBounds.Contains(p)) return f;
            return null;
        }

        int HitHeaderButton(Fence f, Point p)
        {
            if (f == null || !f.TitleBarRect.Contains(p)) return -1;
            if (f.CloseButtonRect.Contains(p)) return 2;
            if (f.RollButtonRect.Contains(p)) return 1;
            if (!f.IsEffectiveCollapsed && f.SearchButtonRect.Contains(p)) return 0;
            return -1;
        }

        enum ResizeEdge { None, N, S, E, W, NE, NW, SE, SW }

        ResizeEdge HitEdge(Fence f, Point p)
        {
            if (f.Locked || f.IsEffectiveCollapsed) return ResizeEdge.None;
            const int t = 7;
            const int topT = 3; // Narrower top edge so title-bar dragging never accidentally triggers North resize
            var r = f.Rect;
            bool left = Math.Abs(p.X - r.Left) <= t;
            bool right = Math.Abs(p.X - r.Right) <= t;
            bool topCorner = Math.Abs(p.Y - r.Top) <= t;
            bool topEdge = Math.Abs(p.Y - r.Top) <= topT;
            bool bottom = Math.Abs(p.Y - r.Bottom) <= t;

            if (topCorner && left) return ResizeEdge.NW;
            if (topCorner && right) return ResizeEdge.NE;
            if (bottom && left) return ResizeEdge.SW;
            if (bottom && right) return ResizeEdge.SE;
            if (left) return ResizeEdge.W;
            if (right) return ResizeEdge.E;
            if (topEdge) return ResizeEdge.N;
            if (bottom) return ResizeEdge.S;
            return ResizeEdge.None;
        }

        Cursor CursorFor(ResizeEdge e)
        {
            switch (e)
            {
                case ResizeEdge.N: case ResizeEdge.S: return Cursors.SizeNS;
                case ResizeEdge.E: case ResizeEdge.W: return Cursors.SizeWE;
                case ResizeEdge.NE: case ResizeEdge.SW: return Cursors.SizeNESW;
                case ResizeEdge.NW: case ResizeEdge.SE: return Cursors.SizeNWSE;
                default: return Cursors.Default;
            }
        }

        ShortcutItem HitItem(Fence f, Point p)
        {
            if (f == null || f.IsEffectiveCollapsed) return null;
            if (!f.ClientClipRect.Contains(p)) return null;
            foreach (var it in f.FilteredItems)
                if (f.ItemCellRect(it).Contains(p)) return it;
            return null;
        }

        bool OnTitle(Fence f, Point p) => f.TitleBarRect.Contains(p);

        Rectangle ApplyMagneticSnap(Fence moving, Rectangle candidate)
        {
            _snapGuides.Clear();
            if (!moving.SnapToGrid) return candidate;

            int x = candidate.X;
            int y = candidate.Y;
            int w = candidate.Width;
            int h = candidate.Height;
            moving.Edge = DockEdge.Auto;

            // 1. Screen edge docking
            if (Math.Abs(x - 0) <= MagneticSnapDist) { x = 0; moving.Edge = DockEdge.Left; }
            else if (Math.Abs((x + w) - Width) <= MagneticSnapDist) { x = Width - w; moving.Edge = DockEdge.Right; }

            if (Math.Abs(y - 0) <= MagneticSnapDist) { y = 0; moving.Edge = DockEdge.Top; }
            else if (Math.Abs((y + h) - Height) <= MagneticSnapDist) { y = Height - h; moving.Edge = DockEdge.Bottom; }

            // 2. Fence-to-fence edge alignment & SnapGap spacing
            bool snappedX = false, snappedY = false;
            foreach (var other in ActivePageFences)
            {
                if (other == moving) continue;
                var or = other.EffectiveBounds;

                if (!snappedX)
                {
                    if (Math.Abs(x - or.Left) <= MagneticSnapDist)
                    {
                        x = or.Left; snappedX = true;
                        _snapGuides.Add((new Point(x, Math.Min(y, or.Top)), new Point(x, Math.Max(y + h, or.Bottom))));
                    }
                    else if (Math.Abs((x + w) - or.Right) <= MagneticSnapDist)
                    {
                        x = or.Right - w; snappedX = true;
                        _snapGuides.Add((new Point(or.Right, Math.Min(y, or.Top)), new Point(or.Right, Math.Max(y + h, or.Bottom))));
                    }
                    else if (Math.Abs(x - (or.Right + SnapGap)) <= MagneticSnapDist)
                    {
                        x = or.Right + SnapGap; snappedX = true;
                        _snapGuides.Add((new Point(or.Right + SnapGap / 2, Math.Min(y, or.Top)), new Point(or.Right + SnapGap / 2, Math.Max(y + h, or.Bottom))));
                    }
                    else if (Math.Abs((x + w) - (or.Left - SnapGap)) <= MagneticSnapDist)
                    {
                        x = or.Left - SnapGap - w; snappedX = true;
                        _snapGuides.Add((new Point(or.Left - SnapGap / 2, Math.Min(y, or.Top)), new Point(or.Left - SnapGap / 2, Math.Max(y + h, or.Bottom))));
                    }
                }

                if (!snappedY)
                {
                    if (Math.Abs(y - or.Top) <= MagneticSnapDist)
                    {
                        y = or.Top; snappedY = true;
                        _snapGuides.Add((new Point(Math.Min(x, or.Left), y), new Point(Math.Max(x + w, or.Right), y)));
                    }
                    else if (Math.Abs((y + h) - or.Bottom) <= MagneticSnapDist)
                    {
                        y = or.Bottom - h; snappedY = true;
                        _snapGuides.Add((new Point(Math.Min(x, or.Left), or.Bottom), new Point(Math.Max(x + w, or.Right), or.Bottom)));
                    }
                    else if (Math.Abs(y - (or.Bottom + SnapGap)) <= MagneticSnapDist)
                    {
                        y = or.Bottom + SnapGap; snappedY = true;
                        _snapGuides.Add((new Point(Math.Min(x, or.Left), or.Bottom + SnapGap / 2), new Point(Math.Max(x + w, or.Right), or.Bottom + SnapGap / 2)));
                    }
                    else if (Math.Abs((y + h) - (or.Top - SnapGap)) <= MagneticSnapDist)
                    {
                        y = or.Top - SnapGap - h; snappedY = true;
                        _snapGuides.Add((new Point(Math.Min(x, or.Left), or.Top - SnapGap / 2), new Point(Math.Max(x + w, or.Right), or.Top - SnapGap / 2)));
                    }
                }
            }

            if (!snappedX && moving.Edge == DockEdge.Auto)
                x = (int)(Math.Round(x / (double)SnapGrid) * SnapGrid);
            if (!snappedY && moving.Edge == DockEdge.Auto)
                y = (int)(Math.Round(y / (double)SnapGrid) * SnapGrid);

            return new Rectangle(x, y, w, h);
        }

        #endregion

        #region Mouse & Drag-and-Drop

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _mouseDownLoc = e.Location;
            _dragItemLocation = e.Location;
            _snapGuides.Clear();

            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
            {
                ResetInteractionState();
                return;
            }

            // 1. Floating HUD pill click
            if (IsHudExpanded && GetHudPillBounds().Contains(e.Location))
            {
                if (e.Button == MouseButtons.Right)
                {
                    CanvasMenu.Show(this, e.Location);
                    return;
                }
                int count = ActiveHudButtonCount;
                for (int i = 0; i < count; i++)
                {
                    if (HudButtonRect(i).Contains(e.Location) && e.Button == MouseButtons.Left)
                    {
                        _hudButtons[i].Item2();
                        return;
                    }
                }
                return;
            }

            // 1b. Floating Status Toast pill click
            if (IsStatusToastVisible && GetStatusPillBounds().Contains(e.Location))
            {
                if (e.Button == MouseButtons.Right)
                {
                    if (_drawFenceMode) ToggleDrawFenceMode();
                    else CanvasMenu.Show(this, e.Location);
                    return;
                }
                if (_drawFenceMode)
                {
                    ToggleDrawFenceMode();
                }
                else if (_quickHide)
                {
                    ToggleQuickHide();
                }
                else if (!_peek)
                {
                    _statusUntil = DateTime.MinValue;
                    UpdateWindowRegion();
                    Invalidate();
                }
                return;
            }

            // 2. If in Draw Fence crosshair mode
            if (_drawFenceMode)
            {
                if (e.Button == MouseButtons.Right)
                {
                    ToggleDrawFenceMode();
                    return;
                }
                _isDrawingNewFence = true;
                _drawFenceStart = e.Location;
                _drawFenceRect = Rectangle.Empty;
                UpdateWindowRegion();
                return;
            }

            if (_quickHide)
            {
                if (e.Button == MouseButtons.Right) CanvasMenu.Show(this, e.Location);
                return;
            }

            var f = HitFence(e.Location);
            if (f == null)
            {
                if (e.Button == MouseButtons.Right)
                {
                    CanvasMenu.Show(this, e.Location);
                }
                ResetInteractionState(keepDrawFence: _drawFenceMode);
                return;
            }

            // Bring clicked fence to front if it wasn't already at the top of Z-order
            int maxZ = _fences.Count > 0 ? _fences.Max(x => x.Z) : 0;
            if (f.Z < maxZ)
            {
                f.Z = maxZ + 1;
                Invalidate(f.EffectiveBounds);
            }

            if (e.Button == MouseButtons.Right)
            {
                ShowFenceMenu(f, e.Location);
                return;
            }

            // Left button on interactive title bar buttons (Search / Roll / Close)
            int hBtn = HitHeaderButton(f, e.Location);
            if (hBtn == 0)
            {
                ToggleFenceSearch(f);
                return;
            }
            if (hBtn == 1)
            {
                f.RollUp();
                AutoSave();
                UpdateWindowRegion();
                Invalidate();
                return;
            }
            if (hBtn == 2)
            {
                if (f.Items.Count == 0 || MessageBox.Show(this, "Remove fence '" + f.Title + "'?", "Fontes", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    if (_activeSearchFence == f) _activeSearchFence = null;
                    _fences.Remove(f);
                    AutoSave();
                    UpdateWindowRegion();
                    Invalidate();
                }
                return;
            }

            // Left button on title bar double-click -> Roll up / unroll
            if (OnTitle(f, e.Location) && e.Clicks >= 2)
            {
                f.RollUp();
                AutoSave();
                UpdateWindowRegion();
                Invalidate();
                return;
            }

            // Left button on resize edge (wait until mouse moves >2px before resizing)
            var edge = HitEdge(f, e.Location);
            if (edge != ResizeEdge.None)
            {
                _resize = edge;
                _dragResize = false;
                _resizeStart = f.Rect;
                _resizeFrom = e.Location;
                _dragFence = f;
                return;
            }

            // Left button on an item inside the fence
            var item = HitItem(f, e.Location);
            if (item != null)
            {
                _pressedItem = item;
                _pressedItemFence = f;
                _isDraggingItem = false;
                return;
            }

            // Left button on title bar or empty fence body -> prepare fence move (if not locked)
            if (!f.Locked && (OnTitle(f, e.Location) || f.EffectiveBounds.Contains(e.Location)))
            {
                _dragFence = f;
                _dragOffset = new Point(e.X - f.Rect.X, e.Y - f.Rect.Y);
                _potentialFenceMove = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 1. Drawing a new fence rectangle
            if (_isDrawingNewFence)
            {
                int x = Math.Min(_drawFenceStart.X, e.X);
                int y = Math.Max(0, Math.Min(_drawFenceStart.Y, e.Y));
                int w = Math.Abs(e.X - _drawFenceStart.X);
                int h = Math.Abs(e.Y - _drawFenceStart.Y);
                var nextRect = new Rectangle(x, y, w, h);
                if (nextRect != _drawFenceRect)
                {
                    _drawFenceRect = nextRect;
                    Invalidate();
                    UpdateWindowRegion();
                    Update();
                }
                return;
            }

            // 2. Resizing a fence (keep window Region tightly clipped to fences)
            if (_resize != ResizeEdge.None && _dragFence != null && e.Button == MouseButtons.Left)
            {
                if (!_dragResize && (Math.Abs(e.X - _resizeFrom.X) > 2 || Math.Abs(e.Y - _resizeFrom.Y) > 2))
                {
                    _dragResize = true;
                }
                if (_dragResize)
                {
                    var oldRect = _dragFence.Rect;
                    ApplyResize(_dragFence, e.Location);
                    if (_dragFence.Rect != oldRect)
                    {
                        _dragFence.AutoArrange();
                        Invalidate();
                        UpdateWindowRegion();
                        Update();
                    }
                }
                return;
            }

            // 3. Moving a fence (keep window Region tightly clipped to fences + snap guides)
            if (_potentialFenceMove && !_dragMove && _dragFence != null && e.Button == MouseButtons.Left)
            {
                if (Math.Abs(e.X - _mouseDownLoc.X) > 3 || Math.Abs(e.Y - _mouseDownLoc.Y) > 3)
                {
                    _dragMove = true;
                }
            }

            if (_dragMove && _dragFence != null)
            {
                var oldRect = _dragFence.Rect;
                int oldGuideCount = _snapGuides.Count;
                var r = _dragFence.Rect;
                r.X = Math.Max(0, Math.Min(Width - 60, e.Location.X - _dragOffset.X));
                r.Y = Math.Max(0, Math.Min(Height - 28, e.Location.Y - _dragOffset.Y));
                var snapped = ApplyMagneticSnap(_dragFence, r);
                if (snapped != oldRect || _snapGuides.Count != oldGuideCount)
                {
                    _dragFence.Rect = snapped;
                    _dragFence.AutoArrange();
                    Invalidate();
                    UpdateWindowRegion();
                    Update();
                }
                return;
            }

            // 4. Dragging an item between fences (keep window Region clipped to fences + 40x40 item ghost)
            if (_pressedItem != null && e.Button == MouseButtons.Left)
            {
                int dx = Math.Abs(e.X - _mouseDownLoc.X);
                int dy = Math.Abs(e.Y - _mouseDownLoc.Y);
                if (!_isDraggingItem && (dx > SystemInformation.DragSize.Width || dy > SystemInformation.DragSize.Height))
                {
                    _isDraggingItem = true;
                    Cursor = Cursors.Hand;
                }
                if (_isDraggingItem && _dragItemLocation != e.Location)
                {
                    _dragItemLocation = e.Location;
                    Invalidate();
                    UpdateWindowRegion();
                    Update();
                }
                return;
            }

            // 5. Floating HUD pill hover
            int hover = -1;
            var hudBounds = GetHudPillBounds();
            if (hudBounds.Contains(e.Location))
            {
                int count = ActiveHudButtonCount;
                for (int i = 0; i < count; i++)
                    if (HudButtonRect(i).Contains(e.Location)) { hover = i; break; }
            }
            if (hover != _hudHover)
            {
                _hudHover = hover;
                Invalidate(hudBounds);
            }

            if (_drawFenceMode)
            {
                Cursor = hover >= 0 ? Cursors.Hand : Cursors.Cross;
                return;
            }

            if (_quickHide) return;

            // 6. Roll-up hover expansion (RollupOnHover) & item hover feedback
            bool needsRepaint = false;
            bool regionChanged = false;
            foreach (var f in ActivePageFences)
            {
                if (f.RolledUp && f.RollupOnHover)
                {
                    bool insideFull = f.Rect.Contains(e.Location);
                    bool insideTitle = f.TitleBarRect.Contains(e.Location);
                    if (!f.IsHoverExpanded && insideTitle)
                    {
                        f.IsHoverExpanded = true;
                        f.AutoArrange();
                        needsRepaint = true;
                        regionChanged = true;
                    }
                    else if (f.IsHoverExpanded && !insideFull)
                    {
                        f.IsHoverExpanded = false;
                        needsRepaint = true;
                        regionChanged = true;
                    }
                }
            }

            if (regionChanged)
                UpdateWindowRegion();

            var hf = HitFence(e.Location);
            var hi = hf != null ? HitItem(hf, e.Location) : null;
            int hBtn = hf != null ? HitHeaderButton(hf, e.Location) : -1;
            if (hf != _hoveredFence || hi != _hoveredItem || hBtn != _hoveredHeaderBtn)
            {
                _hoveredFence = hf;
                _hoveredItem = hi;
                _hoveredHeaderBtn = hBtn;
                needsRepaint = true;
            }

            if (hover >= 0 || hBtn >= 0)
            {
                Cursor = Cursors.Hand;
            }
            else if (hf != null && hi == null && !_dragMove)
            {
                var edge = HitEdge(hf, e.Location);
                Cursor = CursorFor(edge);
            }
            else
            {
                Cursor = hi != null ? Cursors.Hand : Cursors.Default;
            }

            if (needsRepaint) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // 1. Finish drawing a new fence rectangle
            if (_isDrawingNewFence)
            {
                FinishDrawFenceMode();
                return;
            }

            // 2. Item click (launch) or drop (move between fences)
            if (_pressedItem != null && _pressedItemFence != null)
            {
                var item = _pressedItem;
                var fromFence = _pressedItemFence;
                bool wasDrag = _isDraggingItem;

                _pressedItem = null;
                _pressedItemFence = null;
                _isDraggingItem = false;
                Cursor = Cursors.Default;

                if (wasDrag)
                {
                    var target = HitFence(e.Location);
                    if (target != null && target != fromFence && string.IsNullOrEmpty(target.PortalPath))
                    {
                        fromFence.Items.Remove(item);
                        target.Items.Add(item);
                        fromFence.AutoArrange();
                        target.AutoArrange();
                        AutoSave();
                        SetStatus("Moved '" + item.DisplayText + "' to " + target.Title);
                    }
                    else
                    {
                        fromFence.AutoArrange();
                    }
                    UpdateWindowRegion();
                    Invalidate();
                    return;
                }
                else
                {
                    var now = DateTime.Now;
                    bool isDoubleClick = (_lastClickedItem == item) &&
                                         ((now - _lastItemClickTime).TotalMilliseconds <= SystemInformation.DoubleClickTime);
                    _lastClickedItem = item;
                    _lastItemClickTime = now;

                    if (_singleClickLaunch || isDoubleClick || item.IsParentNavigation)
                    {
                        _lastClickedItem = null;
                        ActivateItem(item, fromFence);
                    }
                    Invalidate();
                    return;
                }
            }

            bool wasMoveOrResize = _dragMove || _dragResize || _snapGuides.Count > 0;
            if (_dragMove || _dragResize)
            {
                if (_dragResize && _dragFence != null)
                {
                    _dragFence.SnapHeightToRows();
                    _dragFence.AutoArrange();
                }
                _snapGuides.Clear();
                AutoSave();
            }

            ResetInteractionState();
            if (wasMoveOrResize)
            {
                Invalidate();
                UpdateWindowRegion();
            }
        }

        public void SimulateMouseDown(MouseEventArgs e) => OnMouseDown(e);
        public void SimulateMouseMove(MouseEventArgs e) => OnMouseMove(e);
        public void SimulateMouseUp(MouseEventArgs e) => OnMouseUp(e);
        public bool SimulateCmdKey(Keys keyData)
        {
            var msg = new Message();
            return ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && (_dragMove || _dragResize || _isDraggingItem || _potentialFenceMove))
            {
                if (_dragResize && _dragFence != null)
                {
                    _dragFence.SnapHeightToRows();
                    _dragFence.AutoArrange();
                }
                if (_dragMove || _dragResize)
                    AutoSave();
                ResetInteractionState(keepDrawFence: _drawFenceMode);
                UpdateWindowRegion();
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (IsPointerDragActive) return;

            bool needsRepaint = false;
            bool regionChanged = false;
            Point cur = PointToClient(Cursor.Position);

            if (_hudHover != -1 && !GetHudPillBounds().Contains(cur))
            {
                _hudHover = -1;
                needsRepaint = true;
            }
            if (_hoveredFence != null || _hoveredItem != null)
            {
                _hoveredFence = null;
                _hoveredItem = null;
                needsRepaint = true;
            }
            foreach (var f in _fences)
            {
                if (f.IsHoverExpanded && !f.Rect.Contains(cur))
                {
                    f.IsHoverExpanded = false;
                    regionChanged = true;
                    needsRepaint = true;
                }
            }

            if (regionChanged) UpdateWindowRegion();
            if (needsRepaint) Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_quickHide) return;
            var f = HitFence(e.Location);
            if (f != null && !f.IsEffectiveCollapsed && f.HasOverflow)
            {
                int step = Fence.CellSize * (e.Delta > 0 ? -1 : 1);
                f.ScrollBy(step);
                Invalidate(f.Rect);
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            var p = PointToClient(Cursor.Position);
            if (GetHudPillBounds().Contains(p)) return;
            if (HitFence(p) == null)
            {
                _isDrawingNewFence = false;
                _drawFenceRect = Rectangle.Empty;
                ToggleQuickHide();
            }
        }

        public void ToggleQuickHide()
        {
            if (_drawFenceMode || _isDrawingNewFence)
            {
                _drawFenceMode = false;
                _isDrawingNewFence = false;
                _drawFenceMoveQueued = false;
                _drawFenceRect = Rectangle.Empty;
                UninstallDrawFenceHook();
                Cursor = Cursors.Default;
            }
            _quickHide = !_quickHide;
            SetStatus(_quickHide ? "Fences hidden (click Show or double-click tray icon)" : "Fences restored");
            UpdateWindowRegion();
            Invalidate();
        }

        void ResetInteractionState(bool keepDrawFence = false)
        {
            _dragFence = null;
            _potentialFenceMove = false;
            _dragMove = false;
            _resize = ResizeEdge.None;
            _dragResize = false;
            _pressedItem = null;
            _pressedItemFence = null;
            _isDraggingItem = false;
            _snapGuides.Clear();
            if (!keepDrawFence)
            {
                _isDrawingNewFence = false;
                _drawFenceRect = Rectangle.Empty;
            }
        }

        void ApplyResize(Fence f, Point p)
        {
            var dx = p.X - _resizeFrom.X;
            var dy = p.Y - _resizeFrom.Y;
            var r = _resizeStart;
            int x = r.X, y = r.Y, w = r.Width, h = r.Height;

            bool west = _resize == ResizeEdge.W || _resize == ResizeEdge.NW || _resize == ResizeEdge.SW;
            bool east = _resize == ResizeEdge.E || _resize == ResizeEdge.NE || _resize == ResizeEdge.SE;
            bool north = _resize == ResizeEdge.N || _resize == ResizeEdge.NW || _resize == ResizeEdge.NE;
            bool south = _resize == ResizeEdge.S || _resize == ResizeEdge.SW || _resize == ResizeEdge.SE;

            int minW = Fence.WidthForColumns(1);
            int minH = Fence.HeightForRows(1);

            if (west) { x = Math.Min(r.Right - minW, r.X + dx); w = r.Right - x; }
            if (east) { w = Math.Max(minW, r.Width + dx); }
            if (north) { y = Math.Max(0, Math.Min(r.Bottom - minH, r.Y + dy)); h = r.Bottom - y; }
            if (south) { h = Math.Max(minH, r.Height + dy); }

            f.Rect = new Rectangle(x, y, w, h);
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            if (drgevent.Data.GetDataPresent(DataFormats.FileDrop))
                drgevent.Effect = DragDropEffects.Copy;
            else
                drgevent.Effect = DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            if (!drgevent.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = drgevent.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            Point clientPt = PointToClient(new Point(drgevent.X, drgevent.Y));
            var targetFence = HitFence(clientPt);
            if (targetFence == null)
            {
                AddFenceAt(clientPt);
                targetFence = _fences.Last();
                targetFence.Title = files.Length == 1 && Directory.Exists(files[0])
                    ? Path.GetFileName(files[0])
                    : "Dropped Items";
            }

            if (files.Length == 1 && Directory.Exists(files[0]) && (OnTitle(targetFence, clientPt) || targetFence.Items.Count == 0))
            {
                targetFence.PortalPath = files[0];
                targetFence.CurrentPortalPath = files[0];
                targetFence.Title = Path.GetFileName(files[0]);
                targetFence.Items = PortalScanner.ScanFolder(files[0], files[0]);
                targetFence.AutoArrange();
                AutoSave();
                SetStatus("Created Folder Portal: " + targetFence.Title);
                UpdateWindowRegion();
                Invalidate();
                return;
            }

            foreach (var file in files)
            {
                var it = ShortcutResolver.FromFile(file);
                it.Category = RuleEngine.Classify(it);
                targetFence.Items.Add(it);
            }
            targetFence.AutoArrange();
            AutoSave();
            SetStatus("Added " + files.Length + " item(s) to " + targetFence.Title);
            UpdateWindowRegion();
            Invalidate();
        }

        #endregion

        #region Painting

        /// <summary>Renders a full-monitor composite preview (primary monitor wallpaper + see-through
        /// fences + floating HUD pill) for automated visual verification.</summary>
        public Bitmap RenderVerificationPreview()
        {
            var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                var wp = DesktopHost.GetDesktopWallpaper(Size, _work);
                if (wp != null)
                {
                    g.DrawImage(wp, 0, 0, Width, Height);
                }
                else
                {
                    g.Clear(Color.FromArgb(20, 22, 30));
                }

                if (!_peek && !_quickHide)
                {
                    foreach (var f in ActivePageFences.OrderBy(x => x.Z))
                        PaintFence(g, f);
                }
                PaintHudPill(g, forceVisible: true);
            }
            return bmp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // UpdateWindowRegion() clips MainForm strictly to the visible fences, HUD pill, status toast,
            // thin snap guide lines, dragged item ghost, and rubber-band draw rect so the live animated
            // Wallpaper Engine desktop background shows through 100% natively without ever flashing a
            // static wallpaper image!
            if (!_peek && !_quickHide)
            {
                foreach (var f in ActivePageFences.OrderBy(x => x.Z))
                {
                    if (!e.ClipRectangle.IsEmpty && !e.ClipRectangle.IntersectsWith(f.EffectiveBounds))
                        continue;
                    PaintFence(g, f);
                }
            }

            PaintHudPill(g);

            // Paint magnetic snap alignment guide lines
            if (_snapGuides.Count > 0)
            {
                using (var guidePen = new Pen(Color.FromArgb(235, 65, 215, 255), 1.8f) { DashStyle = DashStyle.Dash })
                {
                    foreach (var seg in _snapGuides)
                        g.DrawLine(guidePen, seg.Item1, seg.Item2);
                }
            }

            // Paint drag-to-create fence selection rectangle
            if (_isDrawingNewFence && _drawFenceRect.Width > 4 && _drawFenceRect.Height > 4)
            {
                using (var path = Rounded(_drawFenceRect, Fence.CornerRadius))
                using (var fill = new SolidBrush(Color.FromArgb(185, 38, 64, 112)))
                using (var pen = new Pen(Color.FromArgb(240, 130, 195, 255), 1.5f) { DashStyle = DashStyle.Dash })
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
                string dimText = string.Format("New Fence ({0} \u00D7 {1})", _drawFenceRect.Width, _drawFenceRect.Height);
                DrawShadowedText(g, dimText, _titleFont, Color.White, _drawFenceRect.X + 12, _drawFenceRect.Y + 8);
            }

            // Paint dragged item ghost tile
            if (_isDraggingItem && _pressedItem != null)
            {
                var cur = _dragItemLocation;
                var tileRect = new Rectangle(cur.X - 20, cur.Y - 20, 40, 40);
                using (var tp = Rounded(tileRect, 8))
                using (var tb = new SolidBrush(Color.FromArgb(235, 28, 34, 48)))
                using (var tpen = new Pen(Color.FromArgb(180, 130, 195, 255), 1f))
                {
                    g.FillPath(tb, tp);
                    g.DrawPath(tpen, tp);
                }
                var bmp = _pressedItem.GetCachedIconBitmap();
                if (bmp != null)
                    g.DrawImage(bmp, new Rectangle(cur.X - 16, cur.Y - 16, 32, 32));
            }
        }

        void PaintFence(Graphics g, Fence f)
        {
            var r = f.EffectiveBounds;
            bool isHoveredFence = (_hoveredFence == f);

            using (var path = Rounded(r, Fence.CornerRadius))
            {
                // 1. Deep charcoal dark-glass base so live desktop never shows stale static wallpaper textures
                using (var baseBg = new SolidBrush(Color.FromArgb(242, 16, 18, 24)))
                {
                    g.FillPath(baseBg, path);
                }

                // 2. Rich diagonal dark-glass card gradient from f.Shade & f.Opacity
                Color baseShade = f.Shade;
                int alpha = Math.Max(30, Math.Min(255, f.Opacity));
                int rTop = Math.Min(255, (int)(baseShade.R * 0.96f + 16));
                int gTop = Math.Min(255, (int)(baseShade.G * 0.96f + 18));
                int bTop = Math.Min(255, (int)(baseShade.B * 0.96f + 24));
                int rBot = Math.Max(10, (int)(baseShade.R * 0.68f + 6));
                int gBot = Math.Max(12, (int)(baseShade.G * 0.68f + 8));
                int bBot = Math.Max(16, (int)(baseShade.B * 0.68f + 12));
                var gradRect = new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
                using (var shadeGrad = new LinearGradientBrush(
                    gradRect,
                    Color.FromArgb(alpha, rTop, gTop, bTop),
                    Color.FromArgb(alpha, rBot, gBot, bBot),
                    LinearGradientMode.ForwardDiagonal))
                {
                    g.FillPath(shadeGrad, path);
                }

                // 3. Subtle top specular glass sheen
                var sheenRect = new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, Math.Min(48, r.Height / 2)));
                using (var sheen = new LinearGradientBrush(
                    sheenRect,
                    Color.FromArgb(24, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical))
                {
                    var oldClip = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    g.FillRectangle(sheen, sheenRect);
                    g.Clip = oldClip;
                }
            }

            bool showTitleBar = f.IsEffectiveCollapsed ||
                                f.LabelMode == TitleBarMode.Always ||
                                (f.LabelMode == TitleBarMode.OnHover && isHoveredFence);

            if (f.IsEffectiveCollapsed)
            {
                using (var tbr = new SolidBrush(Color.FromArgb(160, 10, 12, 18)))
                using (var tp = Rounded(r, Fence.CornerRadius))
                {
                    g.FillPath(tbr, tp);
                }
                DrawTitle(g, f, r);
                DrawFenceBorder(g, r, isHoveredFence);
                return;
            }

            if (showTitleBar)
            {
                var headerRect = new Rectangle(r.X, r.Y, r.Width, Fence.TitleHeight);
                using (var tbr = new SolidBrush(Color.FromArgb(165, 10, 12, 18)))
                using (var tp = Rounded(headerRect, new int[] { Fence.CornerRadius, Fence.CornerRadius, 0, 0 }))
                {
                    g.FillPath(tbr, tp);
                }
                using (var divPen = new Pen(Color.FromArgb(48, 255, 255, 255), 1f))
                {
                    g.DrawLine(divPen, r.X + 1, r.Y + Fence.TitleHeight, r.Right - 2, r.Y + Fence.TitleHeight);
                }
                DrawTitle(g, f, headerRect);
            }

            // Clip items strictly to row-aligned interior client region
            var oldItemClip = g.Clip;
            g.SetClip(f.ClientClipRect, CombineMode.Intersect);

            foreach (var it in f.FilteredItems)
            {
                var cell = f.ItemCellRect(it);
                if (!cell.IntersectsWith(f.ClientClipRect)) continue;
                DrawItem(g, f, it, cell);
            }

            g.Clip = oldItemClip;

            // Extra-thin rounded scrollbar when items overflow
            if (f.HasOverflow)
            {
                var track = f.ScrollbarTrackRect;
                var thumb = f.ScrollbarThumbRect;
                using (var trkPath = Rounded(track, 2))
                using (var trkBrush = new SolidBrush(Color.FromArgb(45, 255, 255, 255)))
                    g.FillPath(trkBrush, trkPath);
                using (var thmPath = Rounded(thumb, 2))
                using (var thmBrush = new SolidBrush(Color.FromArgb(175, 195, 220, 255)))
                    g.FillPath(thmBrush, thmPath);
            }

            DrawFenceBorder(g, r, isHoveredFence);
        }

        static void DrawFenceBorder(Graphics g, Rectangle r, bool isHoveredFence)
        {
            if (r.Width > 3 && r.Height > 3)
            {
                var innerRect = new Rectangle(r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                using (var innerPath = Rounded(innerRect, Math.Max(1, Fence.CornerRadius - 1)))
                using (var innerPen = new Pen(Color.FromArgb(isHoveredFence ? 55 : 28, 255, 255, 255), 1f))
                {
                    g.DrawPath(innerPen, innerPath);
                }
            }
            var outerRect = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
            int borderAlpha = isHoveredFence ? 190 : 115;
            using (var borderPath = Rounded(outerRect, Fence.CornerRadius))
            using (var pen = new Pen(Color.FromArgb(borderAlpha, 210, 225, 255), 1f))
            {
                g.DrawPath(pen, borderPath);
            }
        }

        public enum HeaderButtonKind
        {
            Search = 0,
            RollUp = 1,
            RollDown = 2,
            Close = 3
        }

        void DrawTitle(Graphics g, Fence f, Rectangle headerRect)
        {
            int x = headerRect.X + 12;
            var titleSize = TextRenderer.MeasureText(f.DisplayTitle, _titleFont);
            int textY = headerRect.Y + (Fence.TitleHeight - titleSize.Height) / 2;

            DrawShadowedText(g, f.DisplayTitle, _titleFont, f.TextColor, x, textY);

            int badgeX = x + titleSize.Width + 6;

            // Item count badge pill (e.g. "Apps  152" or "Apps  8/152" when searching)
            var filtered = f.FilteredItems;
            string countText = !string.IsNullOrEmpty(f.SearchFilter)
                ? filtered.Count + "/" + f.Items.Count
                : f.Items.Count.ToString();
            var countSize = TextRenderer.MeasureText(countText, _badgeFont);
            int bw = Math.Max(22, countSize.Width + 8);
            int bh = 18;
            int by = headerRect.Y + (Fence.TitleHeight - bh) / 2;
            var badgeRect = new Rectangle(badgeX, by, bw, bh);

            using (var bp = Rounded(badgeRect, 9))
            using (var bf = new SolidBrush(Color.FromArgb(130, 58, 74, 104)))
            using (var bpen = new Pen(Color.FromArgb(85, 255, 255, 255), 1f))
            {
                g.FillPath(bf, bp);
                g.DrawPath(bpen, bp);
            }
            GfxText.Center(g, countText, badgeRect, Brushes.White, _badgeFont, Color.FromArgb(225, 235, 252));
            badgeX += bw + 8;

            if (f.IsSearching || !string.IsNullOrEmpty(f.SearchFilter))
            {
                string filterLabel = "Filter: " + (string.IsNullOrEmpty(f.SearchFilter) ? "type…" : f.SearchFilter);
                var fs = TextRenderer.MeasureText(filterLabel, _badgeFont);
                int fw = Math.Min(Math.Max(62, fs.Width + 14), Math.Max(62, f.SearchButtonRect.Left - badgeX - 6));
                var searchPill = new Rectangle(badgeX, by, fw, bh);
                using (var sp = Rounded(searchPill, 9))
                using (var sf = new SolidBrush(Color.FromArgb(185, 35, 95, 165)))
                using (var spen = new Pen(Color.FromArgb(150, 140, 210, 255), 1f))
                {
                    g.FillPath(sf, sp);
                    g.DrawPath(spen, sp);
                }
                GfxText.Center(g, filterLabel, searchPill, Brushes.White, _badgeFont, Color.White);
                badgeX += fw + 6;
            }
            else if (!string.IsNullOrEmpty(f.PortalPath))
            {
                var portalRect = new Rectangle(badgeX, by, 52, bh);
                using (var pp = Rounded(portalRect, 9))
                using (var pf = new SolidBrush(Color.FromArgb(130, 45, 95, 145)))
                    g.FillPath(pf, pp);
                GfxText.Center(g, "PORTAL", portalRect, Brushes.White, _badgeFont, Color.FromArgb(175, 225, 255));
            }

            bool isHoveredFence = (_hoveredFence == f);
            if (isHoveredFence || f.IsSearching)
            {
                if (!f.IsEffectiveCollapsed)
                    DrawHeaderActionButton(g, f.SearchButtonRect, HeaderButtonKind.Search, _hoveredHeaderBtn == 0 || f.IsSearching, false);
                DrawHeaderActionButton(g, f.RollButtonRect, f.IsEffectiveCollapsed ? HeaderButtonKind.RollDown : HeaderButtonKind.RollUp, _hoveredHeaderBtn == 1, false);
                DrawHeaderActionButton(g, f.CloseButtonRect, HeaderButtonKind.Close, _hoveredHeaderBtn == 2, true);
            }
            else if (f.Locked)
            {
                DrawShadowedText(g, "LOCKED", _badgeFont, Color.FromArgb(255, 205, 120), headerRect.Right - 58, by + 2);
            }
            else if (f.IsEffectiveCollapsed)
            {
                DrawHeaderActionButton(g, new Rectangle(headerRect.Right - 26, headerRect.Y + 5, 20, 20), HeaderButtonKind.RollDown, false, false);
            }
        }

        public static void DrawHeaderActionButton(Graphics g, Rectangle r, HeaderButtonKind kind, bool isHover, bool isDanger)
        {
            if (isHover)
            {
                Color bg = isDanger ? Color.FromArgb(195, 190, 55, 65) : Color.FromArgb(175, 70, 95, 145);
                using (var p = Rounded(r, 6))
                using (var b = new SolidBrush(bg))
                    g.FillPath(b, p);
            }

            Color fg = isHover ? Color.White : Color.FromArgb(215, 195, 212, 236);
            var oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(fg, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (kind)
                {
                    case HeaderButtonKind.Search:
                        g.DrawEllipse(pen, r.X + 4.5f, r.Y + 4.5f, 7.2f, 7.2f);
                        g.DrawLine(pen, r.X + 10.6f, r.Y + 10.6f, r.X + 15.0f, r.Y + 15.0f);
                        break;
                    case HeaderButtonKind.RollUp:
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(r.X + 5.5f, r.Y + 12.2f),
                            new PointF(r.X + 10.0f, r.Y + 7.6f),
                            new PointF(r.X + 14.5f, r.Y + 12.2f)
                        });
                        break;
                    case HeaderButtonKind.RollDown:
                        g.DrawLines(pen, new PointF[]
                        {
                            new PointF(r.X + 5.5f, r.Y + 7.8f),
                            new PointF(r.X + 10.0f, r.Y + 12.4f),
                            new PointF(r.X + 14.5f, r.Y + 7.8f)
                        });
                        break;
                    case HeaderButtonKind.Close:
                        g.DrawLine(pen, r.X + 6.0f, r.Y + 6.0f, r.X + 14.0f, r.Y + 14.0f);
                        g.DrawLine(pen, r.X + 14.0f, r.Y + 6.0f, r.X + 6.0f, r.Y + 14.0f);
                        break;
                }
            }
            g.SmoothingMode = oldSmooth;
        }

        void DrawItem(Graphics g, Fence f, ShortcutItem it, Rectangle cell)
        {
            bool isHovered = (_hoveredItem == it);
            if (isHovered)
            {
                var hoverRect = new Rectangle(cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2);
                using (var hp = Rounded(hoverRect, 8))
                using (var hb = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
                using (var hpen = new Pen(Color.FromArgb(115, 255, 255, 255), 1f))
                {
                    g.FillPath(hb, hp);
                    g.DrawPath(hpen, hp);
                }
            }

            var bmp = it.GetCachedIconBitmap(f.TintColor);
            int iw = 32, ih = 32;
            var iconRect = new Rectangle(cell.X + (cell.Width - iw) / 2, cell.Y + 6, iw, ih);
            if (bmp != null)
                g.DrawImage(bmp, iconRect);

            var lblRect = new Rectangle(cell.X, cell.Y + iw + 8, cell.Width, 40);
            var shadowRect = new Rectangle(lblRect.X + 1, lblRect.Y + 1, lblRect.Width, lblRect.Height);

            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var sb = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
            using (var fg = new SolidBrush(f.TextColor))
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Near;
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                fmt.FormatFlags = StringFormatFlags.LineLimit;
                g.DrawString(it.DisplayText, _itemFont, sb, shadowRect, fmt);
                g.DrawString(it.DisplayText, _itemFont, fg, lblRect, fmt);
            }

            if (it.IsFolderPortal)
            {
                using (var p = new Pen(Color.FromArgb(180, 125, 195, 255), 1.2f))
                {
                    var gb = new Rectangle(cell.Right - 14, cell.Y + 4, 8, 8);
                    g.DrawEllipse(p, gb);
                }
            }
        }

        static void DrawShadowedText(Graphics g, string text, Font font, Color color, int x, int y)
        {
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, text, font, new Point(x + 1, y + 1), Color.FromArgb(195, 0, 0, 0), flags);
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, flags);
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            return Rounded(r, new int[] { rad, rad, rad, rad });
        }

        static GraphicsPath Rounded(Rectangle r, int[] rads)
        {
            var p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            int hw = Math.Max(1, r.Width / 2);
            int hh = Math.Max(1, r.Height / 2);
            int d1 = ClampRad(rads[0] * 2, hw * 2, hh * 2);
            int d2 = ClampRad(rads[1] * 2, hw * 2, hh * 2);
            int d3 = ClampRad(rads[2] * 2, hw * 2, hh * 2);
            int d4 = ClampRad(rads[3] * 2, hw * 2, hh * 2);
            p.AddArc(r.X, r.Y, d1, d1, 180, 90);
            p.AddArc(r.Right - d2, r.Y, d2, d2, 270, 90);
            p.AddArc(r.Right - d3, r.Bottom - d3, d3, d3, 0, 90);
            p.AddArc(r.X, r.Bottom - d4, d4, d4, 90, 90);
            p.CloseFigure();
            return p;
        }

        static int ClampRad(int d, int maxW, int maxH)
        {
            if (d <= 0) return 1;
            return Math.Max(1, Math.Min(d, Math.Min(maxW, maxH)));
        }

        #endregion

        #region Hotkeys & WndProc (HWND_BOTTOM Desktop Pinning, Peek = Ctrl or Global Ctrl+Alt+Space, Search & Page Switch)

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == DesktopHost.WM_WINDOWPOSCHANGING && !_peekOnTop && !_inModalDialog && !DesktopHost.IsEmbedded)
            {
                // Clamp Z-order to HWND_BOTTOM so clicking or dragging fences never brings Fontes
                // in front of open application windows (like Clip Studio Paint, browsers, or games).
                var wp = (DesktopHost.WINDOWPOS)System.Runtime.InteropServices.Marshal.PtrToStructure(
                    m.LParam, typeof(DesktopHost.WINDOWPOS));
                wp.hwndInsertAfter = DesktopHost.HWND_BOTTOM;
                wp.flags &= ~DesktopHost.SWP_NOZORDER;
                System.Runtime.InteropServices.Marshal.StructureToPtr(wp, m.LParam, false);
            }
            if (m.Msg == DesktopHost.WM_HOTKEY && m.WParam.ToInt32() == DesktopHost.HOTKEY_ID_PEEK)
            {
                TogglePeekOnTop();
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Left))
            {
                CurrentPage = (_currentPage + MaxPages - 1) % MaxPages;
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Right))
            {
                CurrentPage = (_currentPage + 1) % MaxPages;
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                var target = _hoveredFence ?? ActivePageFences.FirstOrDefault();
                if (target != null)
                {
                    ToggleFenceSearch(target);
                    return true;
                }
            }
            if (keyData == Keys.Escape)
            {
                if (_activeSearchFence != null)
                {
                    _activeSearchFence.SearchFilter = "";
                    _activeSearchFence.IsSearching = false;
                    _activeSearchFence.ScrollOffset = 0;
                    _activeSearchFence.AutoArrange();
                    _activeSearchFence = null;
                    Invalidate();
                    return true;
                }
                if (_peekOnTop)
                {
                    TogglePeekOnTop();
                    return true;
                }
                if (_drawFenceMode || _isDrawingNewFence)
                {
                    _drawFenceMode = false;
                    _isDrawingNewFence = false;
                    _drawFenceRect = Rectangle.Empty;
                    UninstallDrawFenceHook();
                    Cursor = Cursors.Default;
                    UpdateWindowRegion();
                    Invalidate();
                    return true;
                }
            }
            if (_activeSearchFence != null && keyData == Keys.Back)
            {
                string cur = _activeSearchFence.SearchFilter ?? "";
                if (cur.Length > 0)
                {
                    _activeSearchFence.SearchFilter = cur.Substring(0, cur.Length - 1);
                    _activeSearchFence.ScrollOffset = 0;
                    _activeSearchFence.AutoArrange();
                    Invalidate(_activeSearchFence.Rect);
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.ControlKey))
            {
                if (!_peek)
                {
                    _peek = true;
                    UpdateWindowRegion();
                    Invalidate();
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            var searchFence = _activeSearchFence ?? _hoveredFence;
            if (searchFence != null && !char.IsControl(e.KeyChar))
            {
                if (!searchFence.IsSearching)
                {
                    foreach (var other in _fences) other.IsSearching = false;
                    searchFence.IsSearching = true;
                    _activeSearchFence = searchFence;
                }
                searchFence.SearchFilter = (searchFence.SearchFilter ?? "") + e.KeyChar;
                searchFence.ScrollOffset = 0;
                searchFence.AutoArrange();
                Invalidate(searchFence.Rect);
                e.Handled = true;
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey && _peek)
            {
                _peek = false;
                UpdateWindowRegion();
                Invalidate();
            }
            base.OnKeyUp(e);
        }

        #endregion
    }
}
