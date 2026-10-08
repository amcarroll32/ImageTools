# Design language

This is the shared design language for a family of small Windows desktop tools. It comes from
[Disk Visualizer](https://github.com/amcarroll32/DiskVisualizer), the reference app. A new project
that follows this document should look, behave and read like Disk Visualizer's sibling, even
though it shares no code with it.

The values below are copied from Disk Visualizer v1.2.0. If the two ever disagree, the reference
app wins; update this file to match it.

---

## 1. Intent

These are the reasons behind every rule further down. When a situation isn't covered, decide it
from these.

1. **Honest and harmless.** The app never connects to the internet, shows ads or collects data. It
   doesn't change the user's files unless that is the app's stated job, and then only when the user
   asks. If the app can't safely do something, it explains how the user can do it themselves
   (Disk Visualizer's *Space hogs* say how to reclaim space but never delete anything).
2. **One portable exe.** There is no installer, nothing to set up, and no runtime to install. It
   runs as a normal user. Admin rights, if they help at all, are an optional one-click restart that
   explains what changes.
3. **Fast, and progressive when it can't be instant.** Results appear as they arrive, and one slow
   item never blocks the others. Status text always says what is happening and how far along it is.
4. **The data is the interface.** Chrome is quiet warm neutrals with hairline separators, and color
   is kept for meaning. One accent (blue) marks interactive and "in progress" things. Every status
   color comes with a text label or icon.
5. **Readable by everyone.** Data colors are checked for color-vision deficiency. Ink color is
   chosen per fill by contrast. Light and dark themes are both first-class.
6. **Discoverable, then fast.** Everything can be done with the mouse, and the common actions have
   keyboard shortcuts that the status bar lists. Hovering explains; clicking acts; right-clicking
   offers options.
7. **Plain, exact wording.** Short sentences, real numbers, no hype. The app tells the user its
   limits instead of hiding them.

---

## 2. Platform and packaging

| Concern | Choice |
|---|---|
| UI stack | WPF on .NET 10 (`net10.0-windows`), using the built-in **Fluent** theme via `Application.ThemeMode` |
| Target OS | 64-bit Windows 10 and 11 |
| Distribution | One self-contained, single-file, compressed exe (`win-x64`), published from a `Portable` publish profile |
| Elevation | `asInvoker` in the manifest; never require admin |
| DPI / paths | `PerMonitorV2` DPI awareness, `longPathAware` |
| Settings | `<AppName>.settings.json` **next to the exe**, so settings travel with the app. If that folder isn't writable, fall back to `%LocalAppData%\<AppName>\settings.json`. If saving fails both ways, carry on without telling the user. |
| Network | None. Don't check for updates and don't send telemetry. |

### `.csproj` baseline

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Version>1.0.0</Version>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <!-- Regenerate with tools/make-icon.ps1 -->
    <ApplicationIcon>Assets/app.ico</ApplicationIcon>
    <!-- Fluent theme (Application.ThemeMode) is flagged experimental -->
    <NoWarn>$(NoWarn);WPF0001</NoWarn>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
    <TieredPGO>true</TieredPGO>
  </PropertyGroup>
  <ItemGroup>
    <Resource Include="Assets/app.ico" />
  </ItemGroup>
</Project>
```

### `Properties/PublishProfiles/Portable.pubxml`

```xml
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <DebugType>none</DebugType>
    <PublishDir>publish\</PublishDir>
  </PropertyGroup>
</Project>
```

Build with `dotnet publish -p:PublishProfile=Portable`.

### `app.manifest` essentials

`requestedExecutionLevel level="asInvoker"`, the Windows 10 `supportedOS` GUID
`{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`, `dpiAware` `true/pm`, `dpiAwareness` `PerMonitorV2`,
and `longPathAware` `true`.

---

## 3. Color

All colors are warm neutrals with a slight yellow-brown cast, never blue-grey. Every token has a
dark and a light value. Dark is the default, but the app follows Windows on first launch.

### 3.1 Chrome tokens

The XAML resource key is how the token is referenced from markup (always via `DynamicResource`).

| Token | XAML key | Dark | Light | Used for |
|---|---|---|---|---|
| Window background | `WindowBg` | `#1a1a19` | `#f9f9f7` | Window, toolbar, status bar |
| Surface | — | `#1e1e1c` | `#fcfcfb` | Large drawn surfaces |
| Card background | `CardBg` | `#242422` | `#ffffff` | Cards in side panels |
| Card border | `CardBorder` | `#383835` | `#e1e0d9` | 1px card outline |
| Hairline | `Hairline` | `#383835` | `#e1e0d9` | 1px section separators |
| Tooltip background | `TipBg` | `#F2232321` (95%) | `#F7FFFFFF` (97%) | Hover tooltips |
| Tooltip border | `TipBorder` | `#4a4a46` | `#c9c8c0` | Tooltip outline |
| Badge background | `BadgeBg` | `#1f2d40` | `#e1ecfb` | Accent-tinted pills (such as "Administrator") |
| Bar track | `BarTrack` | `#2c2c2a` | `#e8e7e2` | Empty part of proportion bars |
| Progress track | — | `#383835` | `#dcdbd4` | Empty part of progress/capacity bars drawn on the canvas |
| Link hover | `LinkHover` | `#2e2e2b` | `#efeee9` | Hover fill on flat link rows |
| Primary ink | `InkPrimary` | `#ffffff` | `#0b0b0b` | Body text, titles |
| Secondary ink | `InkSecondary` | `#c3c2b7` | `#52514e` | Subtitles, descriptions, status text |
| Muted ink | `InkMuted` | `#898781` | `#6f6e69` | Detail lines, percentages, hints, placeholders, section headings |
| Accent ink | `AccentInk` | `#6da7ec` | `#1c5cab` | Accent-colored text and glyphs |
| Accent fill | `AccentFill` | `#3987e5` | `#2a78d6` | Bars, progress, focus targets |

### 3.2 Status colors

These are always paired with a text label or glyph, never color alone.

| Meaning | Dark | Light |
|---|---|---|
| Good / healthy / safe | `#0ca30c` | `#0ca30c` |
| Warning / caution | `#fab219` | `#fab219` |
| Critical / unhealthy / over 90% full | `#e66767` | `#d03b3b` |

A proportion or capacity bar normally uses Accent fill. It switches to Critical once the value is
past its danger threshold (Disk Visualizer uses 90%).

### 3.3 Categorical data palette (9 slots)

Use these to color *kinds* of things. The slots are checked for color-vision deficiency **in this
order**, so assign categories to slots from the top down, don't skip slots, and don't reorder them.
Slot 9 (grey) is always "Other". The palette is stepped separately for each theme.

| Slot | Dark | Light | Disk Visualizer meaning |
|---|---|---|---|
| 1 blue | `#3987e5` | `#2a78d6` | Video |
| 2 orange | `#d95926` | `#eb6834` | Images |
| 3 aqua | `#199e70` | `#1baf7a` | Audio |
| 4 yellow | `#c98500` | `#eda100` | Documents |
| 5 pink | `#d55181` | `#e87ba4` | Archives |
| 6 green | `#008300` | `#008300` | Programs & system |
| 7 violet | `#9085e9` | `#4a3aa7` | Disk images & VMs |
| 8 red | `#e66767` | `#e34948` | App & game data, caches |
| 9 grey | `#898781` | `#898781` | Other files |

**Keep meanings consistent across the family.** If a new app shows file types, use the same
type→color mapping as above (for example, images are always orange). If its categories are
unrelated to file types, it starts again at slot 1.

### 3.4 Ordinal ramp (5 steps, single blue hue)

This ramp is for ordered values such as age, size bands or ratings. Five steps is the most a
single-hue ramp can hold while staying distinguishable on a light surface. **The step that should
stand out gets the most contrast with the surface.** In Disk Visualizer that is the oldest files,
so the ramp runs the opposite way in each theme:

| Step (recent → old) | Dark | Light |
|---|---|---|
| 1 | `#184f95` | `#86b6ef` |
| 2 | `#2a78d6` | `#5598e7` |
| 3 | `#5598e7` | `#2a78d6` |
| 4 | `#9ec5f4` | `#1c5cab` |
| 5 | `#cde2fb` | `#0d366b` |
| Unknown | `#5e5d58` | `#b4b3ac` |

### 3.5 Special fills on a data canvas

| Fill | Dark | Light | Notes |
|---|---|---|---|
| Container depth fills (cycle by depth) | `#262624` `#2e2e2b` `#353532` `#2b2b29` `#32322f` | `#f1f0ec` `#e8e7e2` `#e0dfd9` `#ecebe6` `#e4e3dd` | Nested containers alternate subtly |
| Container border | `#3d3d3a` | `#c9c8c0` | 1px |
| Top-level group border | `#6b6a65` | `#8f8e87` | Stronger frame for the outermost groups |
| Group header band | `#2f2f2c` | `#e3e2dc` | Title strip behind a group's name |
| Aggregated "many small items" | `#5e5d58` | `#b4b3ac` | Cushioned like a normal item |
| Empty / free | hatch `#2a2a28` + `#383835` at 45° | hatch `#f3f2ee` + `#dcdbd4` at 45° | Something that *isn't there* |
| Unknown / unreadable | hatch `#3a3533` + `#5a4f4b` at 135° | hatch `#ece4e1` + `#cdbfb9` at 135° | Something that is there but can't be seen |
| Dimmed (doesn't match a filter) | `#2a2a28` | `#eeede9` | Flat, no label |

**Cushion:** every solid data fill is drawn as a linear gradient from 18% toward white (top-left) to
18% toward black (bottom-right), so neighboring same-colored blocks still read as separate.
Legend swatches use the flat color.

**Hatch:** 8×8 px tile, background color with a 1.5 px line, rotated 45° or 135°.

**Seams:** leave a 1 device-pixel gap on the right and bottom of each data block.

**Ink on data fills:** choose near-black `#0b0b0b` or white per fill, whichever has the higher WCAG
contrast ratio.

### 3.6 Interaction overlays

| State | Dark | Light |
|---|---|---|
| Hover (leaf item) | 2px outline in primary ink `#ffffff` | 2px `#0b0b0b` |
| Hover target (the container a click will open) | 2px `#6da7ec` | 2px `#2a78d6` |
| Selected | 2px `#fab219` outline + `#40fab219` wash | 2px `#c98500` outline + `#40eda100` wash |
| Search match | 1.5px `#f0efec` outline | 1.5px `#0b0b0b` outline |

Overlays are inset 1px and drawn on a separate layer, so hover and selection redraw without
re-laying out the data.

---

## 4. Typography

- **Text font:** Segoe UI (the Fluent default). Use Regular and **SemiBold** only, never Bold or
  Light.
- **Icon font:** `Segoe Fluent Icons, Segoe MDL2 Assets` (with fallback), defined once as the
  resource `Icons`.
- Text that is drawn in code uses `TextFormattingMode.Display`, a single line, and
  `CharacterEllipsis` trimming.

| Role | Size | Weight | Ink |
|---|---|---|---|
| Body, buttons, list names, legend rows | default (14) | Regular | Primary |
| Side-panel view title | 20 | SemiBold | Primary |
| Side-panel subtitle | default | Regular | Secondary |
| Section heading ("SPACE BY TYPE") | 11, **ALL CAPS** | SemiBold | Muted |
| Card title + its value | default | SemiBold | Primary |
| Card body / advice, small buttons | 12 | Regular | Secondary |
| Row detail lines, captions, paths, hints | 11 | Regular | Muted |
| Tooltip title | 13 | SemiBold | Primary |
| Tooltip path / hint | 11 | Regular | Muted / Accent |
| Canvas group title | 13 | SemiBold | Primary |
| Canvas group stats | 11 | Regular | Secondary |
| Canvas container header | 11: name SemiBold, then two spaces, then size in Muted | | Primary + Muted |
| Canvas item label / item size | 11 (SemiBold for real items) / 10.5 | | Contrast ink |

---

## 5. Icons

Use Segoe Fluent Icons glyphs only, with no bitmap icons in the UI. Sizes are 14 in buttons and
menus, 15 for row and group glyphs, 13 for inline alerts and the search magnifier, and 10 for a
dropdown chevron.

| Glyph | Code | Meaning |
|---|---|---|
| Up arrow | `E74A` | Up one level |
| Refresh | `E72C` | Rescan / reload |
| Hard drive | `EDA2` | Drive (generic) |
| SSD | `E964` | Solid-state drive |
| USB | `E88E` | USB stick |
| Memory card | `E7F1` | SD card |
| Network | `E8CE` | Network location |
| Cloud | `E753` | Cloud location |
| Chevron down | `E70D` | Button opens a menu |
| Shield | `EA18` | Admin / elevated |
| Search | `E721` | Search box |
| Zoom in | `E71E` | Zoom into |
| Open folder | `E838` | Open / show in Explorer |
| Copy | `E8C8` | Copy path |
| Warning triangle | `E814` | Alert (paired with a label) |
| Sun / Moon | `E706` / `E708` | Theme toggle. **The button shows the mode you'd switch to.** |

---

## 6. Shape and spacing

- **Corner radii:** 2 for swatches and health strips, 1.5 or 3 for thin and thick bars (half their
  height), 4 for badges and hover rows, 6 for cards and tooltips. Fluent controls keep their own
  radii.
- **No drop shadows,** apart from those Fluent controls draw themselves. Depth comes from a slightly
  lighter fill plus a 1px border.
- **Hairlines** are 1px in the `Hairline` token. They separate toolbar from content, content from
  status bar, and the legend from the list above it.
- Set `UseLayoutRounding="True"` on the window. Snap canvas drawing to device pixels.

| Thing | Value |
|---|---|
| Toolbar padding | `10,8` |
| Toolbar button padding | `10,6`; gap between buttons 6; gap before a new group 14 |
| Icon-to-label gap inside a button | 8 |
| Main area margin | 8 all round |
| Splitter between canvas and side panel | 8 wide, transparent |
| Side panel | 400 wide by default, minimum 300 |
| Side panel header margin | `6,2,6,8` |
| List row padding | `8,4` |
| Swatch | 11×11, radius 2, 8–9 to the right |
| Proportion bar in a row | 3 tall |
| Capacity bar under a title | 6 tall |
| Canvas progress/capacity bar | 4 tall, radius 2 |
| Card | padding `12,10,12,6`, margin `2,0,2,8`, 1px border, radius 6 |
| Small button (inside cards) | padding `10,4`, font 12, margin `0,0,6,6` |
| Tooltip | padding `12,9`, max width 440, radius 6, 1px border |
| Status bar padding | `12,6` |
| Window | 1440×900 default, minimum 1200×560, centered on screen |

---

## 7. Window layout

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [↑] [⟳ Rescan] [▭ Drives ▾] [⛉ Run as admin]  Breadcrumbs ›   12 files  ·  4 GB  │
│                                      [🔍 Search: …] [Color: Type ▾] [☑ Opt] [☾] │
├──────────────────────────────────────────────────────────────┬───────────────┤ hairline
│                                                              │ View title    │
│                                                              │ subtitle      │
│              Main canvas / visualization                     │ ▬▬▬▬▬▬▬░░░░   │
│              (fills all remaining space)                     │ Tab Tab Tab   │
│                                                              │ rows…         │
│                                                              │───────────────│ hairline
│                                                              │ LEGEND        │
│                                                              │ ■ name  size %│
├──────────────────────────────────────────────────────────────┴───────────────┤ hairline
│ Status: what happened, with counts       Click: x  ·  Right-click: y  ·  F5: z │
└──────────────────────────────────────────────────────────────────────────────┘
```

(The toolbar is one row in the app; it is drawn on two lines here only to fit.)

- **Toolbar** is a `DockPanel`. Navigation and actions are docked left; breadcrumbs fill the middle
  (a horizontal `ScrollViewer` with the scrollbar hidden). View controls are docked right, in this
  order from the far right: theme toggle, toggles (`CheckBox`), mode `ComboBox` (labelled
  "*Name: Value*", such as "Color: Type"), search box, and the search result summary in Secondary
  ink.
- **Search box** is 270 wide. A muted magnifier glyph sits inside the left edge (text padding
  `32,5,8,6`), and a muted placeholder that gives examples ("Search: name, \*.mp4, .iso") hides once
  the user types.
- **Breadcrumbs** are transparent flat buttons (padding `8,4`, hand cursor).
- **Side panel**, top to bottom:
  - a title for the current view (20 SemiBold);
  - a one- or two-line subtitle with the key numbers;
  - an optional capacity bar;
  - a `TabControl` with transparent background and no border;
  - a legend docked to the bottom.
- **Status bar:** on the left, Secondary ink, what just happened or what is in progress. On the
  right, Muted ink, the main mouse and keyboard shortcuts.

---

## 8. Components

**Toolbar button.** A Fluent `Button` with a 14px glyph, then 8px, then a sentence-case label.
Icon-only buttons are fine when the glyph is universal (up, theme), but they must have a tooltip
that includes the shortcut, such as "Up one level (Backspace)". A button that opens a menu adds a
10px `E70D` chevron after the label.

**Status badge.** Shows a state rather than an action, such as "Administrator". It is a `Border`
with `BadgeBg`, radius 4, padding `10,5`, and a glyph plus text in `AccentInk`. It replaces the
button that produced the state.

**List row** (shared by every list):

```
│▌ ■  Name (SemiBold for top-level items)     ⚠ Warning   1.25 TB │
│▌    ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬░░░░░░░░░░░░░░░░░░░░░░░ (3px bar)          │
│▌    detail line, 11, muted, ellipsis + tooltip with full text   │
│▌    optional second detail line                                 │
```

- `▌` is an optional 4px health strip (radius 2) in a status color.
- `■` is an 11px swatch or a 15px glyph.
- The size is right-aligned, in Primary ink.
- Use a virtualizing `ListBox` (Recycling mode) with a stretched item container.
- Hovering a row highlights the matching item on the canvas, and double-clicking opens it.

**Legend row.** Swatch, name (ellipsis), size, then percentage in a 52-wide right-aligned Muted
column. It sits under an ALL-CAPS heading such as "SPACE BY TYPE".

**Card** (for advice or findings). Title and value in SemiBold on one line. Below that:
- a status line: a 12px glyph in a status color plus a 12px SemiBold label;
- advice text, 12, Secondary;
- clickable location rows;
- a wrap panel of small buttons, such as "Open in Explorer" and "Find all".

**Link row.** A flat, full-width button in Accent ink with radius 4 and padding `4,2`. It gets a
`LinkHover` fill on mouse-over, uses 11px text, and puts a Muted size on the right.

**Tooltip** (custom, following the mouse over the canvas; not the system tooltip). From top to
bottom:
- title, 13 SemiBold;
- size, Primary;
- detail and age, Secondary, wrapping;
- full path, 11 Muted, with 6 above;
- action hint, 11 Accent, with 6 above (such as "Click to zoom in").

**Context menu.** A Fluent `ContextMenu` whose items each have a 14px glyph as their `Icon`.
Headers are sentence case and quote names with curly quotes: Zoom into “Games”. It offers the
likely next step first and the Explorer and copy actions after.

**Progress.** Show it in place, where the result will appear: a 4px rounded bar on the progress
track with "Scanning… 42%" in Secondary 12 above it. Avoid modal progress dialogs.

**Errors.** Report them in the status bar, not in message boxes: "Couldn't open Explorer:
{message}". Use a dialog only for something that truly needs a decision, and the UAC prompt is
Windows' own.

---

## 9. Data canvas conventions

These apply when the app has a main visualization:

- Draw into `DrawingVisual`s inside a custom `FrameworkElement`, not thousands of WPF elements.
  Hover and selection go on a second visual. Debounce resize (about 60 ms). Cap the number of text
  labels (Disk Visualizer caps them at 3000).
- Containers are framed boxes with a 16px header strip ("Name  size"). Top-level groups get a 34px
  band with:
  - a 4px health strip on the left;
  - a 15px glyph and a 13 SemiBold title;
  - right-aligned stats in 11 Secondary;
  - a 4px capacity bar along the bottom.
- When space runs out, cut the least important text first. Shorten the stats, then drop them, and
  keep the name and any alert. Only label items at least 44×15 px, and only show a size line if the
  item is at least 30 px tall.
- **Filtering dims, it doesn't hide.** Items that don't match the filter turn flat Dimmed; matches
  keep their colors and get a match outline; the path down to each match stays lit. The search
  summary in the toolbar shows the count and total, such as "2 folders, 12 files  ·  4.20 GB".
- Clicking zooms into a container. Backspace, the mouse back button and the breadcrumbs go up.
  Right-click opens the context menu. Hovering a container shows the Target outline; hovering a
  leaf shows the Hover outline.
- Show the space nothing occupies (free, unknown) with hatches, so totals add up to the real whole.

---

## 10. Interaction and keyboard

| Input | Action |
|---|---|
| Click | Open / zoom into |
| Right-click | Context menu |
| Backspace, mouse back button | Up one level |
| Ctrl+F | Focus search |
| Esc (in search) | Clear search |
| F5 | Rescan / reload |
| Double-click a list row | Go to it on the canvas |

The status bar's right side spells out the most important ones, such as
`Click: zoom in  ·  Right-click: options  ·  Backspace: up  ·  Ctrl+F: search  ·  F5: rescan`.

Remember view preferences (theme, include/exclude choices) silently. Don't remember transient state
such as the search text or zoom level.

---

## 11. Writing style

- **Sentence case** everywhere: buttons, tabs, menu items, headings. The only exception is the
  ALL-CAPS section headings in side panels.
- Use plain words: "Space hogs", "Largest", "Run as admin", "Free space", "Unreadable / other".
- **Tooltips explain consequences**, not just names: "Restart with administrator rights to include
  folders a normal user can't read (…). The app stays read-only."
- **Separators:** a middle dot with two spaces each side, `  ·  `, everywhere (status text,
  subtitles, row details, stats, shortcut lists). Tooltip size lines use three spaces each side
  (`1.2 GB   ·   4.5% of C:`). Use line breaks rather than more dots once a line gets long.
- **Partial results say so:** "No matches yet…", "4.20 GB so far".
- **Punctuation:** a real ellipsis `…` ("Scanning…", "Looking for drives…"), curly quotes “ ”
  around names, and spaced en dashes for ranges ("1 – 6 months") and for asides in status text.
- **Errors** start with "Couldn't …:" and then the system message. Cancellations say what state the
  app is still in: "Administrator restart was cancelled; still running as a normal user."
- **Empty states** say why the list is empty: "Nothing here matches the search."

### Number formats (current culture)

| Kind | Rule | Examples |
|---|---|---|
| Bytes | Binary units with Windows labels (1 KB = 1024 B). Three significant figures: `N0` at 100 or more, `N1` at 10 or more, `N2` below that. Under 1 KB, show whole bytes. | `512 B`, `1.25 TB`, `98.1 GB`, `622 GB` |
| Counts | Thousands separators, singular/plural words | `2,008,922 files`, `1 folder` |
| Percent | `N1` under 10%, `N0` from 10%; `<0.1%` for tiny values, `0%` for zero | `6.5%`, `41%`, `<0.1%` |
| Dates | `d MMM yyyy` | `7 Oct 2026` |
| Relative time | just now, then hours, days, months, then years to one decimal (whole years from 10 up) | `3 days ago`, `2.5 years ago` |
| Durations | `14.3s`, `2m 05s` style | `Scanned 5 drives in 14.3s` |
| Capacity | "{used} used of {total}  ·  {free} free" | `76.9 GB used of 466 GB  ·  389 GB free` |

---

## 12. Theme implementation

Use the same mechanism as Disk Visualizer, so the theme code is familiar across the family.

1. **One static `Theme` class** (`src/<Area>/Theme.cs`) holds every color as a frozen `Brush` or
   `Pen` property, plus the palettes as `int[]` RGB arrays for dark and light. `Theme.Apply(bool
   dark)` rebuilds every property. Code reads `Theme.X` at draw time.
2. **XAML uses `DynamicResource` keys** (`WindowBg`, `Hairline`, `InkPrimary`, `InkSecondary`,
   `InkMuted`, `AccentInk`, `AccentFill`, `CardBg`, `CardBorder`, `TipBg`, `TipBorder`, `BadgeBg`,
   `BarTrack`, `LinkHover`, plus the `Icons` font family). The window declares the dark values as
   defaults in `Window.Resources`. After each theme change, an `ApplyThemeResources()` method copies
   the `Theme` brushes into `Resources[...]`.
3. **`App.ApplyTheme(bool dark)`** calls `Theme.Apply(dark)` and sets `Application.ThemeMode` to
   `ThemeMode.Dark` or `ThemeMode.Light`, so the Fluent controls switch too.
4. **Settings** store `Theme` as `"Light"`, `"Dark"` or `null`. `null` means follow Windows, read
   from `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`
   (0 = dark).

Helpers worth copying exactly:

```csharp
/// <summary>Subtle top-left highlight so adjacent same-colored blocks read as separate.</summary>
private static Brush Cushion(Color c) => Frozen(new LinearGradientBrush(
    Blend(c, Colors.White, 0.18), Blend(c, Colors.Black, 0.18), new Point(0, 0), new Point(1, 1)));

/// <summary>Whichever of near-black or white ink has more contrast against the fill.</summary>
private static Brush TextFor(Color c)
{
    static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    double lum = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    return (lum + 0.05) / 0.05 >= 1.05 / (lum + 0.05) ? Solid(Rgb(0x0b0b0b)) : Solid(Colors.White);
}

private static Brush Hatch(Color background, Color line, double angle)
{
    var drawing = new DrawingGroup();
    using (var dc = drawing.Open())
    {
        dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 8, 8));
        dc.DrawRectangle(new SolidColorBrush(line), null, new Rect(0, 0, 8, 1.5));
    }
    return Frozen(new DrawingBrush(drawing)
    {
        TileMode = TileMode.Tile,
        Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute,
        Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute,
        Transform = new RotateTransform(angle),
    });
}
```

Freeze every brush and pen.

---

## 13. App icon

Every app in the family uses the same tile with a different motif inside.

- **Tile:** a rounded square in `#262624` with a `#55544f` border. At 256 px the margin is 8,
  the corner radius 48 and the border 6, scaled proportionally for other sizes.
- **Motif:** a simple shape made of flat rounded blocks in categorical slots 1–4 (dark values
  `#3987e5`, `#d95926`, `#199e70`, `#c98500`). Disk Visualizer's motif is a tiny treemap. A new app
  picks a motif that hints at what it does, built from the same blocks and colors.
- **Sizes:** 16, 20, 24, 32, 40, 48, 64, 128 and 256 px. Draw each size natively rather than
  scaling one down; 24 px and below use a simpler layout on whole pixels with 1 px gaps.
- Generate the icon with `tools/make-icon.ps1` (System.Drawing). Pack PNG frames into
  `Assets/app.ico` and keep the script in the repo.

---

## 14. Repository shape

```
App.xaml(.cs)            Theme + settings bootstrap
MainWindow.xaml(.cs)     Toolbar, side panel, tooltips, navigation
src/Model/               Pure data and formatting (Format.cs lives here)
src/Platform/            Windows specifics: settings, elevation, OS queries
src/<Visual>/            Custom drawing control + Theme.cs
tools/make-icon.ps1      Icon generator
Assets/app.ico
docs/screenshot.png      Used by the README
Properties/PublishProfiles/Portable.pubxml
```

**README structure:**
- the title and a one-sentence description;
- a bold line in the house tone, such as "**No ads, no spyware, no installer. Just a simple tool
  that …**";
- a download link to the latest release and a screenshot;
- **Features**: each bullet opens with a short bold phrase and continues in plain sentences;
- **Running it**: a *Portable exe* section and a *From source* section;
- **Notes and limits**;
- **Code layout**: a table of path and what it does.

---

## 15. Checklist for a new app

- [ ] Fluent `ThemeMode` plus a `Theme` class with every token above, in both themes
- [ ] Follows Windows on first launch; the theme toggle shows the mode you'd switch to and is remembered
- [ ] Toolbar / canvas + 400px side panel / status bar, separated by hairlines
- [ ] Segoe UI Regular and SemiBold only, and Segoe Fluent Icons only
- [ ] Data colors come from the categorical slots in order, or from the ordinal ramp; status colors always have a label
- [ ] Shared meanings kept (images are orange, and so on)
- [ ] Hover outline, Target outline, amber selection, and filtering that dims rather than hides
- [ ] Custom tooltip with title / size / details / path / action hint
- [ ] Errors go to the status bar as "Couldn't …: reason"
- [ ] Shortcuts listed in the status bar; Backspace / Ctrl+F / Esc / F5 behave as described
- [ ] Numbers formatted as in §11
- [ ] Portable single-file exe, `asInvoker`, settings next to the exe, no network
- [ ] Icon on the shared tile, generated by script
- [ ] README follows the house structure and tone
