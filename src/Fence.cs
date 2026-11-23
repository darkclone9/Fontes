using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace Fontes
{
    public enum DockEdge
    {
        Auto = 0,   // DesktopDockUI.Simulation.DeskGroup.EDGE_AUTO
        Top = 1,    // EDGE_TOP
        Bottom = 2, // EDGE_BOTTOM
        Left = 3,   // EDGE_LEFT
        Right = 4   // EDGE_RIGHT
    }

    public enum TitleBarMode
    {
        Always = 0, // DesktopDockUI.Settings.LabelMode
        OnHover = 1,
        Never = 2
    }

    /// <summary>One shaded, titled group of items — modeled after Fences'
    /// DesktopDockUI.Simulation.DeskGroup (_ID, _x, _y, _cx, _cy, _scroll, _szTitle,
    /// _Opacity, _SortMethod, _SortFlags, _szFolder, _Edge, _Rolledup, _SwipeIndex, _dwViewFlags).</summary>
    public class Fence
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public Rectangle Rect { get; set; }
        public int Z { get; set; }
        public Color Shade { get; set; }
        public int Opacity { get; set; } = 205;    // DeskGroup._Opacity (30..255)
        public Color TextColor { get; set; } = Color.FromArgb(242, 245, 252);
        public Color? TintColor { get; set; }      // Settings.DesktopTintIconsColor
        public bool RolledUp { get; set; }         // DeskGroup._Rolledup
        public bool RollupOnHover { get; set; } = true; // Auto-expand rolled-up fence on mouse hover
        public bool IsHoverExpanded { get; set; }  // Transient hover-expand state
        public bool SnapToGrid { get; set; } = true;
        public bool Locked { get; set; }           // VIEWSETTINGFLAG_UNLOCKFENCECONTENTS
        public DockEdge Edge { get; set; } = DockEdge.Auto;
        public TitleBarMode LabelMode { get; set; } = TitleBarMode.Always;
        public string SortMethod { get; set; } = "Name"; // Name, Category, TimesOpened, DateModified
        public bool SortAscending { get; set; } = true;
        public int ScrollOffset { get; set; }      // DeskGroup._scroll (vertical scroll in pixels)
        public int ContentHeight { get; private set; }
        public int PageIndex { get; set; } = 0;    // DeskGroup._SwipeIndex (Desktop Page 0..2)
        public string SearchFilter { get; set; } = ""; // In-fence quick filter query
        public bool IsSearching { get; set; }      // Whether the inline search box is active
        public string RuleCategory { get; set; }   // auto-organize: items matching this land here
        public string PortalPath { get; set; }     // root folder portal binding (_szFolder)
        public string CurrentPortalPath { get; set; } // active folder inside portal navigation
        public Stack<string> PortalHistory { get; } = new Stack<string>();
        public List<ShortcutItem> Items { get; set; } = new List<ShortcutItem>();

        public const int CornerRadius = 12;
        public const int TitleHeight = 30;
        public const int CellSize = 92;            // icon cell: 32 icon + 40 2-line 9pt label + padding
        public const int TopPad = 8;
        public const int BottomPad = 8;
        public const int VerticalPadding = TopPad + BottomPad;
        public const int SidePad = 10;

        public Fence()
        {
            Id = Guid.NewGuid().ToString("N");
            Shade = Color.FromArgb(205, 26, 28, 36);
        }

        /// <summary>Returns the items matching the current SearchFilter (if any).</summary>
        public List<ShortcutItem> FilteredItems
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SearchFilter)) return Items;
                string q = SearchFilter.Trim();
                return Items.Where(it =>
                    it.IsParentNavigation ||
                    (it.DisplayText != null && it.DisplayText.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (it.Category != null && it.Category.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (it.Target != null && Path.GetFileName(it.Target).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                ).ToList();
            }
        }

        public static int WidthForColumns(int cols) => SidePad * 2 + Math.Max(1, cols) * CellSize + 6;

        public static int HeightForRows(int rows) => TitleHeight + VerticalPadding + Math.Max(1, rows) * CellSize;

        public int Columns => Math.Max(1, (Rect.Width - SidePad * 2) / CellSize);

        public int VisibleRows => Math.Max(1, (Rect.Height - TitleHeight - VerticalPadding) / CellSize);

        public void SnapHeightToRows()
        {
            Rect = new Rectangle(Rect.X, Rect.Y, Rect.Width, HeightForRows(VisibleRows));
        }

        public bool IsEffectiveCollapsed => RolledUp && !IsHoverExpanded;

        public Color EffectiveShade => Color.FromArgb(Math.Max(30, Math.Min(255, Opacity)), Shade.R, Shade.G, Shade.B);

        public Rectangle EffectiveBounds => IsEffectiveCollapsed
            ? new Rectangle(Rect.X, Rect.Y, Rect.Width, TitleHeight)
            : Rect;

        public Rectangle TitleBarRect => new Rectangle(Rect.X, Rect.Y, Rect.Width, Math.Min(TitleHeight, Rect.Height));

        // Interactive Title Bar Action Buttons (DesktopDockUI.Settings.UseTitlebarControls)
        public Rectangle CloseButtonRect => new Rectangle(Rect.Right - 26, Rect.Y + 5, 20, 20);
        public Rectangle RollButtonRect => new Rectangle(Rect.Right - 48, Rect.Y + 5, 20, 20);
        public Rectangle SearchButtonRect => new Rectangle(Rect.Right - 70, Rect.Y + 5, 20, 20);

        /// <summary>Strictly row-aligned interior clip height (VisibleRows * CellSize) so the bottom
        /// row of icons is never sliced in half mid-icon.</summary>
        public int VisibleClientHeight => VisibleRows * CellSize;

        public Rectangle ClientClipRect => new Rectangle(
            Rect.X + 4,
            Rect.Y + TitleHeight + TopPad,
            Math.Max(1, Rect.Width - 8),
            Math.Max(1, Math.Min(Rect.Height - TitleHeight - 4, VisibleClientHeight)));

        public bool HasOverflow => !IsEffectiveCollapsed && ContentHeight > VisibleClientHeight;

        public Rectangle ScrollbarTrackRect => new Rectangle(
            Rect.Right - 7,
            Rect.Y + TitleHeight + TopPad,
            4,
            Math.Max(12, ClientClipRect.Height));

        public Rectangle ScrollbarThumbRect
        {
            get
            {
                var track = ScrollbarTrackRect;
                if (!HasOverflow || ContentHeight <= 0) return track;
                float ratio = Math.Min(1f, (float)VisibleClientHeight / ContentHeight);
                int thumbH = Math.Max(20, (int)(track.Height * ratio));
                int maxScroll = Math.Max(1, ContentHeight - VisibleClientHeight);
                int thumbY = track.Y + (int)((track.Height - thumbH) * (Math.Min(maxScroll, Math.Max(0, ScrollOffset)) / (float)maxScroll));
                return new Rectangle(track.X, thumbY, track.Width, thumbH);
            }
        }

        public string ActivePortalDirectory => !string.IsNullOrEmpty(CurrentPortalPath) ? CurrentPortalPath : PortalPath;

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(PortalPath) && !string.IsNullOrEmpty(CurrentPortalPath) &&
                    !string.Equals(PortalPath.TrimEnd('\\'), CurrentPortalPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return Title + " \u203A " + Path.GetFileName(CurrentPortalPath.TrimEnd('\\'));
                }
                return Title;
            }
        }

        public void RollUp()
        {
            RolledUp = !RolledUp;
            IsHoverExpanded = false;
        }

        public void SortItems()
        {
            if (Items == null || Items.Count <= 1) return;
            var nav = Items.Where(x => x.IsParentNavigation).ToList();
            var regular = Items.Where(x => !x.IsParentNavigation).ToList();

            Func<ShortcutItem, IComparable> keySelector;
            switch ((SortMethod ?? "Name").ToLowerInvariant())
            {
                case "category":
                    keySelector = x => (x.IsFolderPortal ? "0-" : "1-") + (x.Category ?? "") + "-" + (x.DisplayText ?? "");
                    break;
                case "timesopened":
                    keySelector = x => x.TimesOpened;
                    break;
                case "datemodified":
                    keySelector = x => x.LastModified;
                    break;
                default:
                    keySelector = x => (x.IsFolderPortal ? "0-" : "1-") + (x.DisplayText ?? "").ToLowerInvariant();
                    break;
            }

            var sorted = SortAscending
                ? regular.OrderBy(keySelector).ToList()
                : regular.OrderByDescending(keySelector).ToList();

            nav.AddRange(sorted);
            Items = nav;
        }

        /// <summary>Arranges FilteredItems into a centered, row-aligned grid inside the fence;
        /// overflows become scrollable via ScrollOffset.</summary>
        public void AutoArrange(Point? cellsOriginOverride = null, bool growToFit = false)
        {
            int itemW = CellSize, itemH = CellSize;
            int cols = Columns;
            int gridW = cols * itemW;
            int startX = Rect.X + Math.Max(SidePad, (Rect.Width - gridW) / 2);
            int startY = Rect.Y + TitleHeight + TopPad;
            Point cellsOrigin = cellsOriginOverride ?? new Point(startX, startY);

            var visibleList = FilteredItems;
            int col = 0;
            int x = cellsOrigin.X, y = cellsOrigin.Y;

            foreach (var it in visibleList)
            {
                it.Cell = new Point(x, y);
                x += itemW;
                col++;
                if (col >= cols)
                {
                    col = 0;
                    x = cellsOrigin.X;
                    y += itemH;
                }
            }

            int neededRows = Math.Max(1, (visibleList.Count + cols - 1) / cols);
            ContentHeight = neededRows * itemH;

            if (growToFit)
            {
                int requiredH = HeightForRows(neededRows);
                if (requiredH > Rect.Height)
                    Rect = new Rectangle(Rect.X, Rect.Y, Rect.Width, Math.Min(HeightForRows(6), requiredH));
            }

            ClampScroll();
        }

        public void ClampScroll()
        {
            int maxScroll = Math.Max(0, ContentHeight - VisibleClientHeight);
            if (ScrollOffset > maxScroll) ScrollOffset = maxScroll;
            if (ScrollOffset < 0) ScrollOffset = 0;
        }

        public void ScrollBy(int deltaPixels)
        {
            ScrollOffset += deltaPixels;
            ClampScroll();
        }

        public Rectangle ItemCellRect(ShortcutItem it)
        {
            return new Rectangle(it.Cell.X + 1, it.Cell.Y - ScrollOffset, CellSize - 2, CellSize - 2);
        }
    }
}
