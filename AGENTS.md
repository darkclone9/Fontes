# Fontes Engineering & Visual Architecture Guidelines (`AGENTS.md`)

## 1. Multi-Monitor & Wallpaper Engine Desktop Overlay Architecture
- **Never stretch `SPI_GETDESKWALLPAPER` directly onto a single monitor without checking `SystemInformation.VirtualScreen`**:
  - On multi-monitor Windows setups running Wallpaper Engine (or Windows `Span` wallpaper mode), `SPI_GETDESKWALLPAPER` (`WallpaperEngineOverride_*.jpg` or `TranscodedWallpaper`) is a composite image spanning the entire `SystemInformation.VirtualScreen` bounding box (including negative coordinates and black void regions between mismatched monitor resolutions).
  - Always detect when the wallpaper dimensions/aspect match `SystemInformation.VirtualScreen` and crop the exact sub-rectangle for the target monitor (`(monRect.X - vs.X) * scaleX`, `(monRect.Y - vs.Y) * scaleY`, `monRect.Width * scaleX`, `monRect.Height * scaleY`).
- **Preserve Live Animated Wallpapers via Window `Region` Clipping**:
  - Do not cover the entire desktop with an opaque full-screen `Form` during normal operation, as that blocks live animated Wallpaper Engine backgrounds and intercepts native desktop clicks.
  - Clip `MainForm.Region` to the union of the visible fences' rounded rectangles (`Fence.EffectiveBounds`) and the compact top-center floating HUD pill (`UpdateWindowRegion()`), expanding `Region = null` only temporarily during active fence move/resize drags or Draw-Fence crosshair mode.
  - Inside each fence card, composite a frosted blur of the properly-cropped monitor wallpaper slice under the translucent dark-glass tint (`Fence.EffectiveShade`).

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
