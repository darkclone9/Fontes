# Fontes — Stardock Fences-Style Desktop Organizer for Windows

**Fontes** is a lightweight, zero-dependency C# / Windows Forms desktop icon and folder portal organizer inspired by **Stardock Fences 6.4.6.1** (reverse-engineered using [Reverse Engineer Anything (REA)](https://github.com/morluto/rea)).

Designed specifically for modern **multi-monitor Windows 10/11 setups running live animated wallpapers (Wallpaper Engine)**, Fontes clips its window `HRGN` strictly to the visible frosted-glass fence cards so 100% of your surrounding desktop remains live and interactive, while pinning itself to `HWND_BOTTOM` with `WS_EX_TOOLWINDOW` so it never clutters your taskbar ("hotbar"), `Alt+Tab`, or active applications.

---

## Key Features

- **Taskbar-Free & Desktop-Pinned (`HWND_BOTTOM` + `WS_EX_TOOLWINDOW`)**:
  - Never shows an open window icon on your Windows taskbar or in `Alt+Tab`.
  - Stays pinned behind normal application windows (browsers, IDEs, Clip Studio Paint, games) unless you press **`Ctrl+Alt+Space`** (**Peek Fences on Top**) to temporarily elevate fences over active apps.
- **True See-Through Desktop & Multi-Monitor Wallpaper Engine Support**:
  - Dynamically clips `MainForm.Region` to the union of the visible fences' rounded cards (`Fence.EffectiveBounds`).
  - Automatically detects composite multi-monitor `SPI_GETDESKWALLPAPER` (`WallpaperEngineOverride_*.jpg` / `TranscodedWallpaper`) images and crops only the exact target monitor sub-rectangle to render a frosted-glass blur behind each translucent fence card.
- **Non-Destructive Native Windows Desktop Icon Hiding (`SysListView32`)**:
  - Automatically hides the native Windows Explorer desktop icon layer (`SysListView32` inside `SHELLDLL_DefView`) while Fontes is running so desktop shortcuts never appear duplicated behind your fences, while leaving `Progman` and `WorkerW` 100% intact for Wallpaper Engine live animated wallpapers.
  - Automatically restores native Windows desktop icons on exit, crash recovery, or via `Fontes.exe --restore-desktop-icons` (and can be toggled anytime via the **`Icons`** button in the top HUD pill or `"Show native Windows desktop icons"` in the right-click context menu).
- **Complete Desktop Scanner (`Folders`, Loose Files & `Recycle Bin`) + Live `FileSystemWatcher` Sync**:
  - Scans all desktop shortcuts (`.lnk`, `.url`), desktop folders (`Folders`), loose files (`.txt`, `.csv`, `.pdf`, `.md`, `.png`, `.7z`, `.bat`, `.reg`, etc.), and the built-in Windows **`Recycle Bin`** (`shell:RecycleBinFolder`).
  - Watches User and Public Desktops in real time via `FileSystemWatcher` (`Created`, `Deleted`, `Renamed`) to automatically add new desktop files/folders into their matching category fence and remove deleted items.
  - Right-click any item inside a fence to open its rich **Item Context Menu** (**Open / Launch**, **Open File Location in Explorer**, **Turn Folder into Folder Portal Fence**, **Move to Fence ▸**, **Remove from Fence**, and **Fence Options ▸**).
- **Auto-Hiding Top-Center HUD Pill & System Tray Icon**:
  - The top-center HUD pill stays tucked out of sight while you work in maximized apps and smoothly appears when you move your cursor to the top-center edge of the desktop.
- **Per-Fence Live Search & Interactive Title Bar Controls**:
  - Hover any fence title bar to reveal **`⌕` (Live Filter/Search)**, **`▴/▾` (Roll-Up/Unroll)**, and **`×` (Close/Delete)**.
  - Press **`Ctrl+F`** (or simply start typing while hovering a fence) to filter 150+ shortcuts instantaneously.
- **Desktop Pages (`Page 1/3` — `Alt+Left` / `Alt+Right`)**:
  - Organize fences across 3 virtual Desktop Pages (`DeskGroup._SwipeIndex`) and move fences between pages from the right-click menu.
- **In-Place Folder Portals**:
  - Bind any directory as a live **Folder Portal** fence with in-place subfolder navigation, `.. (Up)` parent navigation, and breadcrumb titles (`Title › Subfolder`).
- **Multi-Criteria Auto-Organize Rule Engine & Stardock `.fencelayout` Presets**:
  - Built-in category rules (`Apps`, `Games`, `Web`, `Docs`, `Folders`, `Video`, `Audio`, `Images`, `Files`, `Misc`) plus custom multi-criteria rules (extension, name substring, target path, modification age, times opened).
  - Imports Stardock Fences `.fencelayout` XML templates directly from `C:\Program Files (x86)\Stardock\Fences\Layouts\` (with built-in fallback templates).
  - Automatic daily XML layout snapshots under `%APPDATA%\Fontes\Snapshots\`.

---

## Keyboard & Mouse Shortcuts

| Shortcut / Action | Behavior |
| :--- | :--- |
| **Double-click shortcut** | Launch application / URL (or navigate into subfolder inside a Folder Portal) |
| **Double-click fence title bar** | Roll up / unroll fence (auto-expands on mouse hover when rolled up) |
| **Double-click empty desktop** | Quick-Hide / restore all fences |
| **`Ctrl + Alt + Space`** | **Peek on Top**: temporarily elevate fences above active windows |
| **Hold `Ctrl`** | Quick-Peek desktop behind fences |
| **`Ctrl + F` (or type on fence)** | Live-filter shortcuts inside the hovered fence (`Esc` to clear) |
| **`Alt + Left` / `Alt + Right`** | Switch between Desktop Pages (`Page 1..3`) |
| **Mouse wheel on fence** | Scroll overflowing fence rows cleanly by full row steps |

---

## Building & Running Self-Tests

Fontes uses an SDK-style `Fontes.csproj` targeting `.NET Framework 4.8` (`net48`) with zero external NuGet dependencies:

```powershell
# Build Fontes.exe and run the 14 automated self-tests
powershell -ExecutionPolicy Bypass -File .\build.ps1

# Launch Fontes
.\Fontes.exe
```

---

## Architecture & REA Analysis

- `src/MainForm.cs` — Desktop-pinned see-through canvas, auto-hiding HUD pill, magnetic fence snapping, per-fence search, and customization dialogs.
- `src/DesktopHost.cs` — Win32 `HWND_BOTTOM` / `WS_EX_TOOLWINDOW` / `Progman` / `WorkerW` integration, multi-monitor wallpaper cropping, frosted-glass blur cache, and `Ctrl+Alt+Space` global hotkey.
- `src/Fence.cs` — `DeskGroup`-parity model (`Rect`, `Opacity`, `ScrollOffset`, `PageIndex`, `SearchFilter`, `RollupOnHover`, `LabelMode`, `PortalPath`, row-aligned grid geometry).
- `src/ShortcutItem.cs` & `src/ShortcutResolver.cs` — COM `IShellLinkW` `.lnk` and `.url` (`IconFile=` / `IconIndex=`) resolution with 32×32 tinted bitmap caching.
- `src/WinIcons.cs` — `SHGetFileInfo` and `ExtractIconEx` native shell icon extraction.
- `src/PortalScanner.cs` — Folder Portal directory scanner with `.. (Up)` navigation.
- `src/RuleEngine.cs` — Multi-criteria `FenceRule` evaluation and category grouping.
- `src/XmlStore.cs` — Layout persistence (`%APPDATA%\Fontes\layout.xml`), daily snapshots, and Stardock `.fencelayout` parser.
- [REA_FENCES_ANALYSIS.md](./REA_FENCES_ANALYSIS.md) — Detailed static reverse-engineering report mapping Stardock Fences 6.4.6.1 (`Fences.exe` and `DesktopDock64.dll`) to Fontes.
- [AGENTS.md](./AGENTS.md) — Engineering guidelines for multi-monitor Wallpaper Engine overlays and WinForms icon rendering.
