# Fontes Engineering & Visual Architecture Guidelines (`AGENTS.md`)

## 1. Multi-Monitor & Wallpaper Engine Desktop Overlay Architecture
- **Never stretch `SPI_GETDESKWALLPAPER` directly onto a single monitor without checking `SystemInformation.VirtualScreen`**:
  - On multi-monitor Windows setups running Wallpaper Engine (or Windows `Span` wallpaper mode), `SPI_GETDESKWALLPAPER` (`WallpaperEngineOverride_*.jpg` or `TranscodedWallpaper`) is a composite image spanning the entire `SystemInformation.VirtualScreen` bounding box (including negative coordinates and black void regions between mismatched monitor resolutions).
  - Always detect when the wallpaper dimensions/aspect match `SystemInformation.VirtualScreen` and crop the exact sub-rectangle for the target monitor (`(monRect.X - vs.X) * scaleX`, `(monRect.Y - vs.Y) * scaleY`, `monRect.Width * scaleX`, `monRect.Height * scaleY`).
- **Preserve Live Animated Wallpapers via Permanent Window `Region` Clipping (`Region` Must NEVER Be `null`)**:
  - Do not cover the entire desktop with an opaque full-screen `Form` during normal operation OR during mouse drag/resize/draw interactions, as setting `Region = null` and painting `SPI_GETDESKWALLPAPER` flashes an outdated static wallpaper (`WallpaperEngineOverride_*.jpg`) over live animated Wallpaper Engine scenes.
  - WinForms/Win32 automatically holds mouse capture (`Capture = true`) between `OnMouseDown` and `OnMouseUp` even when the cursor moves outside `MainForm.Region`. Keep `MainForm.Region` strictly clipped at all times to the union of the visible fences (`Fence.EffectiveBounds`), floating HUD pill, status toast, 4px magnetic `_snapGuides` lines, 40x40 dragged item ghost tile, and rubber-band `_drawFenceRect`.
  - When `_drawFenceMode` is active, use a temporary low-level mouse hook (`WH_MOUSE_LL`) only for the duration of `_drawFenceMode` to catch the initial desktop click/drag without expanding `MainForm.Region` to full screen.
  - Never paint `SPI_GETDESKWALLPAPER` (`GetFrostedDesktopWallpaper` / `GetDesktopWallpaper`) inside live `PaintFence`, `PaintHudPill`, or `OnPaint`, because the static disk image often mismatches the active live Wallpaper Engine scene. Render rich dark-glass card gradients (`LinearGradientBrush` derived from `Fence.Shade` and `Fence.Opacity` with a top specular glass sheen and dual inner/outer borders) instead.

## 4. Vector Header Action Glyphs & Column-Aligned Proportional Horizontal Layout Sizing
- **Never rely on Unicode `\u2315` (`TELEPHONE RECORDER`) in `Segoe UI` for the header Search button**:
  - `Segoe UI` lacks a glyph for `U+2315` and renders a missing-glyph rectangle box (`▯`). Always render fence header buttons (Search magnifying glass, Roll-up/Roll-down chevrons, Close `×`) using anti-aliased GDI+ vector primitives (`DrawEllipse`, `DrawLine`, `DrawLines`).
- **Column-Aligned Proportional Horizontal Layout Widths**:
  - When applying a `"Horizontal"` `FencesLayoutArea` (such as a bottom dock preset), size small fences (`Items.Count < equalCols * rows`) to their natural column width (`Fence.WidthForColumns(Math.Max(3, colsNeeded))`) and allocate the remaining horizontal width across high-item-count fences in whole icon columns (`Fence.CellSize = 92`) first before distributing leftover pixel remainders, preventing large fences from falling 1–2 pixels short of a column boundary.

## 2. `.url` & `.lnk` Shortcut Icon Extraction Rules
- **Never use `SystemIcons.Shield` as a fallback icon for `.url` or web shortcuts**:
  - `SystemIcons.Shield` is the Windows UAC security elevation shield and makes normal `.url` shortcuts look like elevated administrative prompts.
- **Full `.url` and `.lnk` Icon Resolution Pipeline**:
  - Parse both `IconFile=` and `IconIndex=` from `.url` internet/Steam/launcher shortcuts (and expand environment variables).
  - Use `ExtractIconEx` (`WinIcons.ExtractIndexed`) when `IconFile` or `IShellLinkW.GetIconLocation` points to an indexed resource inside a `.dll` or `.exe`.
  - Skip `uninstall.exe` icon paths when a `.lnk` targets a `.url` web shortcut (e.g. `ASIO4ALL Web Site.lnk` -> `ASIO4ALL Web Site.url`).
  - Fall back to `SHGetFileInfo` (`SHGFI_ICON | SHGFI_LARGEICON`) via `WinIcons.ForPath` / `WinIcons.UrlIcon` so Windows Shell supplies the user's real browser or file-type icon.

## 3. Row-Aligned Fence Geometry & Empty Default Fence Pruning
- **Never clip the bottom row of icons mid-icon**:
  - Fence heights and `ClientClipRect` must be strictly row-aligned:
    - `HeightForRows(rows) = TitleHeight + VerticalPadding + rows * CellSize`
    - `VisibleClientHeight = VisibleRows * CellSize`
  - Snap fence heights to row boundaries (`SnapHeightToRows()`) after creation, layout migration, and user resize operations, and scroll by `CellSize` steps so icon cells are never sliced in half vertically.
- **Prune Empty Welcome/Placeholder Fences**:
  - Automatically remove 0-item default placeholder fences (such as empty `"Web & Docs"` fences when `AutoOrganize()` creates `"Web"` and `"Docs"`) during `AutoOrganize()` and layout loading.

## 5. Non-Destructive `SysListView32` Native Desktop Icon Hiding & Complete Desktop Sync
- **Hide ONLY `SysListView32` — Never Hide `SHELLDLL_DefView`, `Progman`, or `WorkerW`**:
  - Wallpaper Engine renders its live animated wallpaper surface inside `Progman` / `WorkerW`. Hiding `SHELLDLL_DefView`, `Progman`, or `WorkerW` blacks out or freezes live animated wallpapers.
  - Always locate `SHELLDLL_DefView` (under `Progman` or top-level `WorkerW` via `FindWindowEx`) and then target ONLY its child `SysListView32` (`FindWindowEx(shellDefView, IntPtr.Zero, "SysListView32", null)`) with `ShowWindow(hListView, SW_HIDE / SW_SHOW)`.
  - Periodically verify `AreNativeDesktopIconsVisible()` on `_uiTimer.Tick` while `_hideNativeDesktopIcons` is active so that if `explorer.exe` restarts, `SysListView32` is automatically re-hidden.
  - Guarantee restoration (`SetNativeDesktopIconsVisible(true)`) on `MainForm.FormClosing`, `AppDomain.CurrentDomain.ProcessExit`, `UnhandledException`, `Application.ThreadException`, and via the `--restore-desktop-icons` CLI flag.
- **Complete Desktop Scanning (`Folders`, Loose Files, and `Recycle Bin`) & Live `FileSystemWatcher` Sync**:
  - `DesktopScanner.Scan()` must enumerate non-hidden/non-system directories (`Category = "Folders"`), loose files (skipping `desktop.ini`, `thumbs.db`, `*.tmp`, and `~$*` Office lock files), and the built-in Windows `Recycle Bin` (`shell:RecycleBinFolder`, using `SHGetStockIconInfo` with `SIID_RECYCLER = 31`).
  - Maintain `FileSystemWatcher` instances on both `SpecialFolder.DesktopDirectory` and `SpecialFolder.CommonDesktopDirectory` (`Created`, `Deleted`, `Renamed`) and debounce `SyncDesktopChanges()` on the UI timer so newly created, renamed, or deleted desktop items sync into fences automatically without resetting custom fence positions.
