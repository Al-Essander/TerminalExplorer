# Terminal Explorer

A file manager for **Windows 11** in the style of **MS-DOS / Far Manager**. Zero third-party
dependencies: only the .NET BCL and P/Invoke to system libraries.

Working title. The specification lives in the project passport; the accepted architecture
decisions and deviations from it are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Status

This is an **early, functional prototype**. It builds and runs, but has **not yet been
extensively tested**. Feedback and bug reports are welcome.

## AI assistance

This project was built **with AI assistance** — the code was generated and reviewed by AI
coding agents. The **core search idea**, an *n-gram signature (Bloom-like) filter* for fast
substring search across an entire NTFS volume, is adapted from my coursework project:

**[2026-Coursework — Adaptive multi-criteria filtering in structured catalogs with
heterogeneous parameters](https://github.com/Al-Essander/2026-Coursework)**

## What's implemented

**Core** (`tools/MftProbe`) — verified on a real drive, results in
[docs/BENCHMARKS.md](docs/BENCHMARKS.md). Whole-volume indexing through the MFT: 1.2M records
in ~3.5 s. Substring search with a bigram-signature filter: from 0.8 ms.

**Shell** (`src/TerminalExplorer.App`) — working frame:

- Tabs with session persistence between runs (each has its own navigation history)
- Navigation: address bar, back/forward/up, mouse side buttons, Alt+←/→
- Virtualized list, system icons, sort by any column
- **Real Windows context menu** via `IContextMenu` — "New", "Open with", and entries
  from installed programs; the custom menu stays on Ctrl+Right-click
- Drag & drop in and out, delete to the Recycle Bin
- Context menus for drives and favorites, a pinned-addon panel
- Four palettes: Calm, MS-DOS, Classic, Custom (with an HSV picker)
- **Details panel** (`F12`): folder tree, type map, size distribution, a Benford's-law
  check, traversal cost, and a live filesystem-activity indicator
- Add-ons: [template](examples/addon-template) and a folder-structure generator
- **Blender**: scene preview and file composition read directly from `.blend`, without
  launching Blender
- Rename, clipboard, zoom, auto-refresh of the folder on external changes

**Not yet done**: MFT-index search wired into the shell (the core isn't connected yet),
thumbnails for regular files, WSL and cloud-folder locations, view modes other than the table.

## .blend format

Parsed from real files — the open descriptions of this layout don't match, because it changed
in newer Blender versions.

|                | Old format      | New (Blender 4+)      |
|----------------|-----------------|-----------------------|
| Header         | `BLENDER-v293`, 12 bytes | `BLENDER17-01v0502`, 17 bytes |
| Version        | 3 digits → 2.93 | 4 digits → 5.02       |
| Block header   | 16 + pointer size | **32 bytes**          |
| Block size     | int32 at offset 4 | **int64 at offset 16** |

The extended header and 64-bit size are the cost of supporting files larger than 4 GB.

What is read: the Blender version, compression type, pointer width, the embedded preview
(`TEST` block — raw RGBA, not PNG), and the number of datablocks by two-letter codes
(`OB` — objects, `ME` — meshes, `MA` — materials). For zstd-compressed files only the
header is available: decompression would require a third-party library.

## Where data lives

Settings, session, favorites, templates, sounds, and user add-ons live in a `Data` folder
**next to the executable**. The program is portable along with its data, and you don't have to
hunt for them in hidden `AppData`. If the program's folder isn't writable (e.g. an install in
`Program Files`), the data falls back to `%APPDATA%\TerminalExplorer` — otherwise portable mode
would silently lose settings. To open the folder: main menu → "Program data folder".

## Add-ons

Custom add-ons go in `Data\Addons` next to the program — one folder per add-on, with an
`addon.json` manifest. The format and substitutions are described in
[examples/addon-template/README.md](examples/addon-template/README.md); copy that folder and
edit it to suit you.

The built-in "Folder structure generator" add-on creates a directory tree from a template (up
to three levels of nesting) and can save templates. Open it from the main menu, with `Ctrl+G`,
or by launching with an argument:

```bash
TerminalExplorer.exe --folder-tree "C:\Projects"
```

### Hotkeys

|                         |                                                |
|-------------------------|------------------------------------------------|
| `Ctrl+T` / `Ctrl+W`     | new tab / close                                |
| `Ctrl+Tab`              | next tab (`+Shift` — previous)                 |
| `Ctrl+L`                | focus the address bar                          |
| `Alt+←` / `Alt+→`       | back / forward (or mouse side buttons)         |
| `Backspace`             | up one level                                   |
| `F5`                    | refresh                                        |
| `F9`                    | switch palette                                 |
| `F10`                   | main menu                                      |
| `F12`                   | details panel                                  |
| `F2`                    | rename                                         |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | copy / cut / paste                    |
| `Ctrl+A`                | select all                                     |
| `Ctrl+wheel`, `Ctrl+±`, `Ctrl+0` | zoom                               |
| `Ctrl+Right-click`      | custom menu instead of the system one          |
| `Ctrl+D`                | add to favorites                               |
| `Ctrl+G`                | folder structure generator                     |
| `Delete`                | delete to Recycle Bin                          |

A middle click on a tab closes it. File drag & drop: normal — copy, with `Shift` held — move.

## Build requirements

- .NET 10 SDK (currently only the runtimes are installed, not the SDK)
- Windows 10 1809+ / Windows 11, x64

## Build

Double-click `build.cmd` — it checks for the SDK and produces **a single portable exe** in the
`dist` folder. The runtime is packed in: the file can be copied to any Windows x64 machine,
nothing needs to be installed there. The size is on the order of 40–60 MB, the price of a
self-contained build.

Manual equivalent:

```bash
dotnet publish tools/MftProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

## Run

`dist\MftProbe.exe` — double-click. Windows itself will request elevation: reading the MFT
requires opening the volume with `GENERIC_READ`, which is unavailable to an ordinary process
(the app manifest declares `requireAdministrator`).

You can pass a drive letter as the first argument, `C` by default:

```bash
dist\MftProbe.exe D
```

The program prints an indexing log, memory metrics, and enters an interactive search mode.

## What the benchmark checks

| Hypothesis                                       | Expectation                                |
|--------------------------------------------------|--------------------------------------------|
| Whole-volume indexing through the MFT            | seconds, not minutes                       |
| Memory for 1M records                            | on the order of 60–90 MB                   |
| Naive substring search                           | tens of milliseconds                       |
| Search with bigram signatures                    | single-digit milliseconds, 90%+ filtered   |
| Actual FP rate vs the Bloom formula              | divergence due to bigram skew              |

Each query is run twice — naive scan and with the filter — and the results are cross-checked.
A mismatch in the number of found records would mean a false negative, which a Bloom filter
cannot produce; the benchmark separately checks this condition and shouts if it's violated.

The `log` command in search mode shows the accumulated process log.

If the numbers line up — the search architecture is confirmed and the shell can be built. If
not — the core must be changed now, not after writing the UI.