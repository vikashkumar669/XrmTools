<p align="center"><img src="https://raw.githubusercontent.com/vikashkumar669/XrmTools/main/ChartDesignerPro/src/ChartDesignerPro/Resources/icon128.png" width="96" alt="Chart Designer Pro icon"></p>

# Chart Designer Pro for XrmToolBox

A chart studio for **Dataverse model-driven app charts**. Browse system and personal charts, read their data and presentation XML, and (in later versions) validate them against what Unified Interface actually supports and save changes safely.

> **Status: in development (milestone M1, read-only).** This version never writes to Dataverse. Editing, validation, diff and safe save arrive in later milestones; see [Roadmap](#roadmap).

| | |
|---|---|
| **NuGet package** | `VikashKumar.XrmToolBox.ChartDesignerPro` (not published yet) |
| **Current version** | 1.2026.11.1 (development build) |
| **Requires** | XrmToolBox 1.2025.10.74 or later (.NET Framework 4.8) |
| **License** | MIT |

---

## Features in this version

### Chart Explorer
- Lists **system charts** (`savedqueryvisualization`) and **personal charts** (`userqueryvisualization`) that you own or that are shared with you.
- **Table filter.** With a table selected, *Load charts* only asks Dataverse for that table's charts.
- **Search** by name, description, table, type or chart id (with or without braces).
- **Sort** by name, table, scope, type or modified date.
- **Paged loading** (5,000 rows per page) with **Cancel**.
- If personal charts can't be read (for example, missing privileges), the system charts still load and a warning explains why.

### Chart inspection (read-only)
- **Data XML** (`datadescription`: FetchXML, categories, measures) and **Presentation XML** (`presentationdescription`: chart type, colours, labels, axes), with colouring.
- **Indent XML** shows the XML indented. This is display only: the stored XML is never changed.
- A **well-formed check** with the line and column of any error.
- The **Properties** panel shows the table, scope, type, default flag, managed and customizable state, owner, modified on/by and the chart id.
- The tool explains **why a chart is read-only** when it is:
  - Power BI visualizations
  - web-resource visualizations
  - managed charts whose *IsCustomizable* is false

### Safety
- XML is parsed with DTDs prohibited, no external resources resolved and a size cap, so imported or stored XML can't expand entities or read files.
- Settings (table filter, scope toggles, indent) are saved per connection. Nothing secret is stored.

---

## How to use it

1. In XrmToolBox, connect to an environment and open **Chart Designer Pro**.
2. Optionally pick a **Table**, then click **▶ Load charts** (F5).
3. Type in **Search** (Ctrl+F) to narrow the list. Click a column header to sort.
4. Select a chart to open it, then switch between the **Data XML** and **Presentation XML** tabs.

**Permissions:** read access to System Charts. Personal charts are listed only when your role can read them.

---

## Roadmap

| Milestone | Content | Status |
|---|---|---|
| M1 | Plugin shell, Chart Explorer, read-only XML view, metadata service, logging | ✅ this version |
| M2 | Favourites and recent charts, plain-language chart summary | Planned |
| M3 | XML editor: line numbers, find/replace, format, edit mode, unsaved-change prompts | Planned |
| M4 | Validation (structure, FetchXML, metadata, Unified Interface compatibility) and diff | Planned |
| M5 | Import/export (XML, versioned JSON, CSV inventory) and local backups | Planned |
| M6 | Safe save: confirmation with diff, conflict check, backup, scoped publish, post-save verification | Planned (after sandbox verification) |
| M7 | Documentation and first release | Planned |

Later phases add the visual designer, local preview, chart comparison and environment comparison.

---

## Build, test and debug

1. Open `ChartDesignerPro.sln` in Visual Studio 2022 or 2026 with the *.NET desktop development* workload.
2. **Close XrmToolBox completely**, then build in **Debug**. A post-build step copies `ChartDesignerPro.dll` to `%APPDATA%\MscrmTools\XrmToolBox\Plugins`. If XrmToolBox is running, the copy fails with *MSB3027 (file locked)*.
3. To debug with F5, set `executablePath` in `src/ChartDesignerPro/Properties/launchSettings.json` to your `XrmToolBox.exe` and use the **XrmToolBox** launch profile.
4. Run the core tests (no Dataverse needed):
   ```powershell
   dotnet run --project tests\ChartDesignerPro.Tests -c Release
   ```
   The tests run on .NET Framework 4.8 on Windows and on .NET 8 elsewhere. They never connect to an environment.

---

## Releasing to the XrmToolBox Tool Library

This works the same way as the other tools in this repo.

The Tool Library is fed from **nuget.org**. The package must:
- put `XrmToolBox` first in its tags,
- contain the DLL under `lib\net48\Plugins\`,
- and depend on the `XrmToolBox` package.

`ChartDesignerPro.nuspec` already does all three.

### One-time setup before the first release
On nuget.org, open *Account → Trusted Publishing* and add a policy:
- owner `VikashKumar0491`
- repository `vikashkumar669/XrmTools`
- workflow `chart-designer-pro.yml`
- package `VikashKumar.XrmToolBox.ChartDesignerPro`

### Option A: GitHub Actions (recommended)
1. Bump the version in **both** `src/ChartDesignerPro/ChartDesignerPro.csproj` and `src/ChartDesignerPro/ChartDesignerPro.nuspec` (format `1.YYYY.M.rev`), and update `<releaseNotes>`.
2. Commit and push to `main`. When the workflow sees a version that **isn't on nuget.org yet**, it:
   - runs the core tests and builds the Release DLL,
   - checks that the csproj and nuspec versions match,
   - packs the `.nupkg` and checks its layout,
   - publishes through Trusted Publishing,
   - and creates the GitHub release `ChartDesignerPro-v<version>`.

Pull requests and pushes that don't change the version only test, build and pack. Every build's `.nupkg` is available under the workflow run's **Artifacts**.

### Option B: from your PC
```powershell
cd <repo>\ChartDesignerPro
.\build\pack.ps1                                    # tests + build + pack -> .\artifacts\
.\build\pack.ps1 -Push -ApiKey <your-nuget-api-key> # tests + build + pack + push
```

### Register the tool with XrmToolBox (one time, after the first push)
1. Wait until the package shows as *listed* on nuget.org (usually 10–60 minutes).
2. Sign in at https://www.xrmtoolbox.com, open the tool submission page, and submit **`VikashKumar.XrmToolBox.ChartDesignerPro`**.
3. After approval it appears in **XrmToolBox → Tool Library**.

---

## Project layout
```
ChartDesignerPro/
  ChartDesignerPro.sln
  build/pack.ps1                         tests, build, pack, push from a PC
  src/ChartDesignerPro/                  XrmToolBox plugin (net48, WinForms)
    ChartDesignerPro.csproj              compiles the core sources in, so the tool ships as one DLL
    ChartDesignerPro.nuspec              Tool Library package definition
    ChartDesignerProPlugin.cs            MEF export: name, description, icons, colours
    ChartDesignerProControl.cs           UI: explorer, XML tabs, properties
    Controls/                            grid, banner, toolbar helpers, XML viewer
    Data/                                Dataverse access: charts, metadata, log adapter
  src/ChartDesignerPro.Core/             chart logic with no SDK or WinForms dependency
    Abstractions/  Models/  Xml/  Explorer/
  tests/ChartDesignerPro.Tests/          dependency-free test runner
```

## Release notes

**1.2026.11.1**: development build (not released)
- Chart Explorer for system and personal charts: table filter, search, sort, paged loading with cancel
- Read-only data and presentation XML with colouring, optional indentation and well-formedness check
- Properties panel and read-only reasons
- Settings saved per connection

## License
MIT. See [LICENSE](https://github.com/vikashkumar669/XrmTools/blob/main/ChartDesignerPro/LICENSE).
