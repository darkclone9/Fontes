# REA Reverse-Engineering Analysis: Stardock Fences 6.4.6.1 -> Fontes

This report documents the static reverse-engineering analysis performed with **REA (`rea-agents@6.10.1`)** against the local installation of **Stardock Fences 6.4.6.1** (`C:\Program Files (x86)\Stardock\Fences\`) and maps those findings directly to the upgraded architecture of **Fontes** (`C:\Users\Gary\dev\Fontes`).

---

## 1. Audited Artifacts & REA Evidence Provenance

| Artifact Path | Format / Arch | SHA-256 Digest | REA Operation | REA Evidence ID |
| :--- | :--- | :--- | :--- | :--- |
| `C:\Program Files (x86)\Stardock\Fences\Fences.exe` | PE32 / .NET Framework v4.8 (`anycpu`, MVID `d1e21121-8e51-49eb-ba45-2a39923231e4`) | `e27ef02e4ed74ae6337732cd54df6c158bb9d7791fdc9136edcf82b36d796bc8` | `inspect_managed_artifact` | `ev_f752799c422877251df109dc86da3887d526ecaedea66fd8d5dc703b5cb24dc1` |
| `C:\Program Files (x86)\Stardock\Fences\Fences.exe` | Managed ECMA-335 Metadata (831 TypeDefs, 5,602 Fields, 6,861 MethodDefs, 76,454 Call Edges, 18,465 User Strings) | `e27ef02e4ed74ae6337732cd54df6c158bb9d7791fdc9136edcf82b36d796bc8` | `inspect_managed_members` | `ev_c7c9f022f4aae1268a01f99586f7abacc635630b5f523561277ebf86cc58963a` |
| `C:\Program Files (x86)\Stardock\Fences\DesktopDock64.dll` | PE32+ (`amd64` native shell extension / desktop hook engine, 6,165,600 bytes) | `1bf3064a9e513a9b94871ac5f0450eeb79b4d2161cd69d1ee6a4c5f6f2cde6e1` | `inspect_native_exports` | `ev_d2b8cd82d4a446489278c0fa6751b86b8ec73d09eecd31f0161835fce82107e0` |
| `C:\Program Files (x86)\Stardock\Fences\Layouts\*.fencelayout` | XML Serialized `DesktopDockUI.Layouts.Layout` definitions | 10 preset layout templates (`Inital0`, `Inital1`, `Right`, `RightDouble`, `Bottom`, `BottomRight`, `BottomRightCorner`, `LeftAndRight`, `TopBottom`, `TopLeftAndRight`) | Direct XML Schema Inspection | Verified against `DesktopDockUI.Layouts.Layout` & `LayoutArea` TypeDefs |

---

## 2. Native Shell Layer (`DesktopDock64.dll` -> `DesktopHost.cs` & `ShortcutResolver.cs`)

REA's `inspect_native_exports` (`ev_d2b8cd82d4a446489278c0fa6751b86b8ec73d09eecd31f0161835fce82107e0`) recovered all 30 exports from `DesktopDock64.dll`:

| Ordinal | Export Name | RVA / Address | Role in Stardock Fences | Fontes Implementation |
| :--- | :--- | :--- | :--- | :--- |
| 5 | `FindDesktopWindowSlow` | `0x18011b260` | Locates `Progman` / `WorkerW` / `SHELLDLL_DefView` desktop window hierarchy | `DesktopHost.FindDesktopHostWindow()` & `DesktopHost.Embed()` |
| 6 | `FixIconSpacing` | `0x18019e110` | Aligns desktop grid spacing with fences (`SnapGrid`, `CellSize`) | `Fence.CellSize` & `MainForm.SnapGrid` / magnetic snapping |
| 9 / 10 | `GetResolutionFingerprint` / `Ex` | `0x180140a90` | Generates display topology hash for per-resolution fence storage | `DesktopHost.GetResolutionFingerprint()` saved in `XmlStore` |
| 13 | `GetWallpaperImage` | `0x180138860` | Queries active Windows desktop wallpaper for translucent background blending | `DesktopHost.GetDesktopWallpaper()` rendered in `MainForm.OnPaint` |
| 20 | `ShellGraphics_LoadIconOfFile` | `0x18012ef50` | Extracts shell file/folder icons with caching | `ShortcutItem.GetCachedIconBitmap()` & `WinIcons.Folder()` |
| 21 / 23 | `Shell_GetLnkPath` / `Shell_GetUrlPath` | `0x18012ca70` | Resolves `.lnk` (`IShellLinkW`) target, arguments, working dir, and `.url` targets | `ShortcutResolver.ResolveLnk()` & `ResolveUrl()` |

---

## 3. Core Managed Models (`Fences.exe` -> `Fence.cs`, `ShortcutItem.cs`, `RuleEngine.cs`)

### 3.1 `DesktopDockUI.Simulation.DeskGroup` (Token `0x02000138`) -> `Fence.cs`
From `ev_c7c9f022f4aae1268a01f99586f7abacc635630b5f523561277ebf86cc58963a`, `DeskGroup` stores the per-fence state:
- `_ID`, `_GUID`, `_szTitle`, `_szTitleDefault` -> `Fence.Id`, `Fence.Title`
- `_x`, `_y`, `_cx`, `_cy` -> `Fence.Rect`
- `_scroll` (`int`) -> `Fence.ScrollOffset` (vertical pixel scroll when items exceed visible height; replaces forced fence height growth)
- `_Opacity` (`int`) -> `Fence.Opacity` (`30..255` alpha control)
- `_SortMethod`, `_SortFlags` (`uint`) -> `Fence.SortMethod` (`Name`, `Category`, `TimesOpened`, `DateModified`) & `Fence.SortAscending`
- `_szFolder` (`string`) & `get_IsFolderView` -> `Fence.PortalPath`, `Fence.CurrentPortalPath`, and `Fence.PortalHistory` stack for in-place directory navigation
- `_Edge`, `_EdgeLast` (`EDGE_AUTO = 0`, `EDGE_TOP = 1`, `EDGE_BOTTOM = 2`, `EDGE_LEFT = 3`, `EDGE_RIGHT = 4`) -> `Fence.Edge` (docking to screen borders)
- `_Rolledup`, `_RolledupDockedLast` -> `Fence.RolledUp`, `Fence.RollupOnHover`, and `Fence.IsHoverExpanded`
- `_dwViewFlags` (`VIEWSETTINGFLAG_UNLOCKFENCECONTENTS`) -> `Fence.Locked` & `Fence.LabelMode` (`Always`, `OnHover`, `Never`)

### 3.2 `DesktopDockUI.Simulation.IconElement` (Token `0x0200013a`) -> `ShortcutItem.cs`
`IconElement` tracks each shortcut inside a `DeskGroup`:
- `Name`, `_ResolvedName`, `_ResolvedPath`, `_ResolvedArguments` -> `ShortcutItem.Name`, `Target`, `Arguments`, `WorkingDir`
- `xRelative`, `yRelative`, `xAbsolulte`, `yAbsolulte` -> `ShortcutItem.Cell`
- `TimesOpened` (`uint`), `AddedToFenceDateLow/High`, `DateLastClickedLow/High` -> `ShortcutItem.TimesOpened`, `DateAdded`, `LastModified`

### 3.3 `DesktopDockUI.Simulation.Rule` & `Criteria_*` (Tokens `0x0200012f`..`0x02000135`) -> `RuleEngine.cs`
Fences evaluates rules composed of polymorphic criteria classes:
- `Criteria_Type` (file extension / category)
- `Criteria_Name` (display name pattern match)
- `Criteria_Target` (resolved target executable/URL match)
- `Criteria_Modified` (file age / modification date)
- `Criteria_TimesOpened` (launch frequency threshold)

In `Fontes`, `RuleEngine` implements both the default category classifier and a persisted list of `FenceRule` objects supporting extension, name pattern, target pattern, and max-age criteria, editable via the interactive **Rules Editor** dialog.

### 3.4 `DesktopDockUI.Layouts.Layout` & `LayoutArea` (Tokens `0x02000127`, `0x02000128`) -> `XmlStore.cs`
Fences ships XML `.fencelayout` templates under `C:\Program Files (x86)\Stardock\Fences\Layouts\`. Each `<LayoutArea>` specifies:
- `<Alignment>` (`TopRight`, `BottomRight`, `TopLeft`, `BottomLeft`, `BottomCenter`, `TopCenter`)
- `<Orientation>` (`Vertical` or `Horizontal`)
- `<Width>` and `<Height>` (normalized `0.0..1.0` screen fractions)
- `<XOffsetModifers>`, `<YOffsetModifers>` (normalized screen offsets)
- `<WidthModifersNoRounding>`, `<HeightModifersNoRounding>`, `<XOffsetModifersNoRounding>`, `<YOffsetModifersNoRounding>` (pixel adjustments)

`XmlStore.LoadFencesLayoutPreset()` parses these `.fencelayout` files directly (and includes built-in fallbacks) to arrange `Fontes` fences into proportional screen areas.
