# Lumen

A Power BI-style dashboard builder: WPF, C# 7.3, .NET Framework 4.7, x64, no JavaScript and no browser control.
The specification is `PRD-dashboard-builder-wpf-net47-no-js.md`. This is the Phase 1 MVP (section 5.1).

Manuals: [UserPowerClone.html](UserPowerClone.html) for report authors, [DevPowerClone.html](DevPowerClone.html) for developers.

## Build and run

Needs Visual Studio 2022 (or its MSBuild) with the .NET Framework 4.7 targeting pack. Use MSBuild, not `dotnet build`, because the desktop project has WPF markup and a Framework target.

```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" Lumen.sln -restore -p:Configuration=Release
```

```bash
src/Lumen.Desktop/bin/Release/Lumen.exe
```

Home > Get data > Sample report loads a three-page demo. You can also pass a `.lumen` file on the command line.

## Lumen Viewer

`src/Lumen.Viewer/bin/Release/LumenViewer.exe` opens a `.lumen` report read-only: pages, slicers, filter values, cross-filtering, data refresh and export, with no editing and no way to save. Pass a file on the command line, use Open, or drop one on the window. It builds with the solution.

To deploy it, give people `src/Lumen.Setup/bin/Release/LumenViewerSetup.exe`, a single file that holds the viewer. It installs per user with no administrator rights, to `%LOCALAPPDATA%\Programs\Lumen Viewer`, adds a Start menu entry and an entry in Windows Settings > Apps, and offers `.lumen` files to the viewer without taking the default from another app. It needs .NET Framework 4.7 or later: without it, setup stops and links to Microsoft's download page. The setup program itself runs on .NET 4.0, so it can start on a machine that has only an older 4.x. The installer is not code signed, so Windows SmartScreen warns on first run.

| Setup switch | Effect |
| --- | --- |
| `/quiet` | No window. Exit code 0 done, 1 failed, 2 .NET 4.7 missing, 3 the viewer is running. |
| `/dir=path` | Install folder. |
| `/desktop` | Add a desktop shortcut. |
| `/noassoc` | Leave `.lumen` files alone. |
| `/assumerelease=n` | Pretend .NET reports Release n, to test the check. 460798 is 4.7. |

`src/Lumen.DesktopSetup/bin/Release/LumenSetup.exe` does the same for the Lumen builder, without the viewer. It installs to `%LOCALAPPDATA%\Programs\Lumen`, takes the same switches, and can sit beside Lumen Viewer. Removing either one leaves the other in place. `BuildLumenInstaller.html` walks through creating it, with every source file listed.

## Tests and checks

```bash
dotnet test tests/Lumen.Engine.Tests/Lumen.Engine.Tests.csproj --no-build -c Release
```

| Command | What it does |
| --- | --- |
| `Lumen.exe --screenshot out.png` | Drives the real shell through pages, cross-filtering, slicers, undo, imports, dialogs, themes, save and reopen, and PDF. Writes `out-*.png` and `out.log`. If the machine has been idle for 30 s it also moves the real mouse for a few seconds to test drag and drop. |
| `Lumen.exe --bench 10000000 out.txt` | Times slicer clicks at the given row count and reports memory. |
| `LumenViewer.exe --check report.lumen out.png` | Drives the viewer through every page, a slicer, a cross-filter, Reset, refresh and PDF, checks the file is unchanged, and writes `out-*.png` and `out.log`. Exit code 0 means every step passed. |

## Layout

| Project | Contents |
| --- | --- |
| `Lumen.Model` | Semantic model, report document, query contract, undo stack. No WPF. |
| `Lumen.Engine` | Columnar store, dictionary encoding, filter propagation, LQL parser and compiler, query execution. |
| `Lumen.Connectors` | CSV, Excel, JSON, SQL Server, transform steps, sample data. |
| `Lumen.Charting` | Scales, Power BI format strings, `DrawingVisual` chart host. |
| `Lumen.Visuals` | Visual contracts, format options, themes, and the 14 visual types. |
| `Lumen.Reporting` | What any app that shows a report needs: `ReportSession`, `ReportSurface`, the `.lumen` reader and writer, filter pane, export, chrome styles. |
| `Lumen.Desktop` | The builder: undo, editing canvas, panes, views, dialogs. |
| `Lumen.Viewer` | The read-only viewer, a window of about 450 lines over `Lumen.Reporting`. |
| `Lumen.Setup` | The viewer installer. A .NET 4.0 WinForms exe with the viewer's Release output embedded as resources. |
| `Lumen.DesktopSetup` | The Lumen installer. It compiles the code of `Lumen.Setup` with its own `Product.cs` and embeds `Lumen.Desktop`. |

## How a slicer click runs

1. The canvas waits 30 ms to coalesce clicks, then builds one `QuerySpec` per visual and runs them in parallel.
2. Each filter becomes a row `Bitset`, memoised per dataset version, so visuals share them and per-group contexts rescan nothing.
3. Dimension filters reach the fact table through the foreign key array, or through an inverted index when the filter is narrow.
4. Rows are partitioned by group key once per query. Measures then run over those partitions, in parallel across groups and across row chunks.
5. Finished results are cached by dataset version and query, ignoring filter ids.

## Differences from the PRD

| PRD | Here |
| --- | --- |
| AvalonDock, Prism, Autofac, Serilog, System.Reactive, MEF | Grid splitters and tabs, plain classes, a static visual registry |
| CsvHelper | Hand-written CSV reader |
| RibbonWindow | Plain `Window` holding a `Ribbon`, which stays light in dark themes |
| `TimeScale`, mark animation | Band and linear scales, no animation |
| Date stored as days, Decimal as `decimal` | Date stored as ticks, Decimal as `double` |

Not implemented: calculated model columns (a custom column transform step does the job), filters that target a measure.
