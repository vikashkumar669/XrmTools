<p align="center"><img src="https://raw.githubusercontent.com/vikashkumar669/XrmTools/main/ChartDesignerPro/src/ChartDesignerPro/Resources/icon128.png" width="96" alt="Chart Designer Pro icon"></p>

# Chart Designer Pro for XrmToolBox

A chart studio for **Dataverse model-driven app charts**. Browse system and personal charts, see what each one shows in plain words, edit their data and presentation XML, check them against what Unified Interface actually supports, and save changes back safely: with a diff, a conflict check, a backup and a publish of only the chart's table.

> **Status: released.** Install it from XrmToolBox › Tool Library once it has been approved there. The visual designer, preview and comparisons come in later versions; see [Roadmap](#roadmap).

| | |
|---|---|
| **NuGet package** | [`VikashKumar.XrmToolBox.ChartDesignerPro`](https://www.nuget.org/packages/VikashKumar.XrmToolBox.ChartDesignerPro) |
| **Current version** | 1.2026.11.3 |
| **Requires** | XrmToolBox 1.2025.10.74 or later (.NET Framework 4.8) |
| **License** | MIT |

---

## Documentation

| | |
|---|---|
| [Supported features and limitations](https://github.com/vikashkumar669/XrmTools/blob/main/ChartDesignerPro/docs/SupportedFeatures.md) | What works, what's read-only, and what was verified on a real environment |
| [Validation rules](https://github.com/vikashkumar669/XrmTools/blob/main/ChartDesignerPro/docs/ValidationRules.md) | All 25 checks, their severities and sources |
| [Troubleshooting](https://github.com/vikashkumar669/XrmTools/blob/main/ChartDesignerPro/docs/Troubleshooting.md) | Common problems, permissions, and restoring a backup |
| [Architecture](https://github.com/vikashkumar669/XrmTools/blob/main/ChartDesignerPro/docs/Architecture.md) | Projects, layers, the save sequence and design principles |

---

## Features in this version

### Chart Explorer
- Lists **system charts** (`savedqueryvisualization`) and **personal charts** (`userqueryvisualization`) that you own or that are shared with you.
- **Table filter.** With a table selected, *Load charts* only asks Dataverse for that table's charts.
- **Search** by name, description, table, type or chart id (with or without braces).
- **Sort** by name, table, scope, type or modified date.
- **Paged loading** (5,000 rows per page) with **Cancel**.
- If personal charts can't be read (for example, missing privileges), the system charts still load and a warning explains why.

### Favourites and recent charts
- **☆ Favourite** marks the open chart. A ★ column shows favourites in the list.
- **Show: All charts / ★ Favourites / Recent** filters the list. Recent lists the last 15 charts you opened, most recent first.
- Both are kept per connection.

### Summary (plain-language explanation)
- One sentence describing what the chart shows, for example *"Counts Accounts by Owner, shown as a bar chart."* or *"Sums Est. Revenue by Actual Close Date (month), shown as a line chart, limited to the top 5."*
- Facts: table, categories (with date grouping), measures and aggregations, chart type, number of series, Top/Bottom N, related (linked) tables, the chart's own filters, sort and titles.
- Uses table and column **display names** from metadata, and falls back to logical names when metadata can't be read.
- Notes explain things that are easy to miss, for example that a chart without its own filter shows the records of the view it is displayed with.

### XML editor
- **✎ Edit** (Ctrl+E) switches the open chart to edit mode. Charts that can't be edited (Power BI, web resource, managed and not customizable) stay read-only, and the reason is shown.
- **Editor features:**
  - XML colouring and a line-number gutter
  - undo/redo (Ctrl+Z / Ctrl+Y)
  - **find and replace** (Ctrl+F / Ctrl+H, F3 for next) with match-case and a match count
  - **Format XML** (Ctrl+Shift+F)
  - plain-text paste, and Tab inserts two spaces
- **Live status:** "Well-formed" or the parse error with its line and column. Click the error to jump to it.
- **Change tracking:**
  - ● marks a changed tab and the chart title
  - the Summary follows your edits
  - **↶ Discard changes** returns to the stored XML
- **Safety:**
  - Changing charts or closing the tool with unsaved edits asks first.
  - The environment bar turns amber while editing, and changes stay in the tool until you save.
  - Opening a chart never makes it look changed: line endings are preserved exactly.

### Validation
- **24 checks** run whenever a chart is opened or edited. Each result has a rule ID, a severity (✕ Error, ⚠ Warning, ℹ Info, ✓ Passed), what's wrong, why it matters, a suggestion, and the line. Double-click a result to jump to that line.
- Checks never change a chart.
- The tab title shows the counts, e.g. *Validation (1 ✕, 2 ⚠)*.

| Group | Rules | Examples |
|---|---|---|
| Structure | CDP-S001–S008, S020 | well-formed XML, `<datadefinition>` / `<fetchcollection>` / `<categorycollection>` present, measure aliases match aggregate aliases, measure collections match `<Series>`, one `<fetch>`, one non-empty `<ChartArea>` per category (Dataverse rejects the save otherwise), size within the 16,000-character chart import limit |
| FetchXML | CDP-F001–F005 | `aggregate="true"`, aliases on grouped and aggregate attributes, valid `aggregate` and `dategrouping` values, unique aliases |
| Metadata | CDP-M001–M004 | query table matches the chart's table; columns exist (linked tables too); sum/avg only on numbers; min/max on numbers or dates; count vs countcolumn; date grouping only on dates; dates grouped without `dategrouping` |
| Unified Interface | CDP-U001–U007 | documented chart types; properties Unified Interface ignores (per Microsoft's documented list); at most 5 series, or exactly 1 for comparison charts; `#VAL`/`#TOTAL` label keywords unsupported; colour ignored on pie, doughnut and funnel; `CustomColorOverride`; colour formats |

- When metadata can't be read, metadata checks say **"Not checked"**; they never claim to have passed.

### Diff
- Compares your edited XML with what's stored in Dataverse, for the Data XML or the Presentation XML: − removed and + added lines, with line numbers from both versions.
- **Ignore formatting** compares the XML structure only, so whitespace, indentation and attribute order don't count.
- **Changed lines only** hides unchanged lines, apart from a little context around each change.

### Import, export and backups
- **⇩ Export** (chart toolbar) saves the open chart as:
  - a **Chart Designer Pro file (.json)**: versioned, holding the chart's columns exactly as stored, plus where it came from;
  - **Chart XML (.xml)**: a `<visualization>` file with the name, table and the data and presentation XML, exact or formatted;
  - the **Data XML** or **Presentation XML** on its own.

  If the chart has unsaved edits, you choose whether to export your edited version or the stored one.
- **⇧ Import…** loads a `.json` or `.xml` file into the open chart **as edits**: nothing is saved to Dataverse, the Diff and Validation tabs show the result, and **↶ Discard changes** undoes it.
  - Accepted files: the tool's JSON; `<visualization>` files, with the XML inside either nested or escaped; or a bare `<datadefinition>` or `<Chart>` file.
  - Before anything changes, the tool shows what will be replaced and warns if the file is for a different table.
  - Files are parsed safely, with no DTDs and a size limit.
- **⇩ Chart list (CSV)** (main toolbar) exports the charts shown in the list: name, table, scope, type, default, managed, customizable, modified on/by, owner, id and description. Cells that could run as spreadsheet formulas are neutralised.
- **Back up the stored version now** writes the chart as stored in Dataverse to `%APPDATA%\MscrmTools\XrmToolBox\Settings\ChartDesignerPro\Backups\<environment>\`. **Open backup folder** opens that folder. A backup is a recovery copy: to restore, import it and save.

> The `.xml` export uses the same layout as the app's **Export Chart**, checked against a real export on 2026-11-27. The app's **Import Chart** accepts it: a `<visualizationid>` is required, but the app always creates a new personal chart from the file and never overwrites the chart with that id. Exports from the app can be imported into this tool too. The `.json` file keeps more detail (source environment, chart type, modified time).

### Chart inspection
- **Data XML** (`datadescription`: FetchXML, categories, measures) and **Presentation XML** (`presentationdescription`: chart type, colours, labels, axes), with colouring.
- **Indent (view only)** shows read-only XML indented. This is display only: the stored XML is never changed. In edit mode the editor always shows the exact stored text.
- A **well-formed check** with the line and column of any error.
- The **Properties** panel shows the table, scope, type, default flag, managed and customizable state, owner, modified on/by and the chart id.
- The tool explains **why a chart is read-only** when it is:
  - Power BI visualizations
  - web-resource visualizations
  - managed charts whose *IsCustomizable* is false

### Saving to Dataverse
**💾 Save** (Ctrl+S) runs these steps in order. Nothing is written until you confirm in step 3.
1. **Validate** the edited XML. Errors block saving and open the Validation tab. Warnings are allowed, but you must tick a box to acknowledge them.
2. **Check for conflicts.** The chart is read again. If someone changed it after you opened it, or deleted it, you're told who and when, and you can stop.
3. **Confirm.** The dialog shows:
   - the **environment name and URL**, the chart, its scope and table
   - a diff of every changed column
   - the warnings to acknowledge
   - for system charts, **Save and publish the table** (default) or **Save without publishing**
   - where the backup goes
4. **Back up** the version currently in Dataverse as JSON. If the backup can't be written, nothing is saved.
5. **Update only the changed columns** (`datadescription`, `presentationdescription`).
6. **Publish only the chart's table** (system charts, when chosen) with a scoped `PublishXmlRequest`, never *Publish all*. Personal charts don't need publishing.
7. **Verify** by reading the chart back: exact, same XML reformatted, or different (reported).
8. **Reopen** the saved version read-only and update the list.

Failures are reported for each step:
- A failed update publishes nothing, and your edits stay open.
- A failed publish leaves the saved change in place and shows **⇪ Publish table** so you can retry.

There is no automatic undo. To go back, import the backup (⇧ Import…) and save it again.

**Permissions:** saving a system chart needs write access to System Charts (and publish rights); saving a personal chart needs write access to your own User Charts.

### Safety
- XML is parsed with DTDs prohibited, no external resources resolved and a size cap, so imported or stored XML can't expand entities or read files.
- Settings (table filter, scope toggles, indent) are saved per connection. Nothing secret is stored.

---

## How to use it

1. In XrmToolBox, connect to an environment and open **Chart Designer Pro**.
2. Optionally pick a **Table**, then click **▶ Load charts** (F5).
3. Type in **Search** (Ctrl+F) to narrow the list, click a column header to sort, or use **Show** for favourites and recent charts.
4. Select a chart. The **Summary** tab explains it, the **Data XML** and **Presentation XML** tabs show its definition, **Validation** lists problems and **Diff** shows your changes.
5. Click **✎ Edit** to change the XML, then **💾 Save** (Ctrl+S) to validate, review and save it to Dataverse.

**Permissions:**
- Reading needs read access to System Charts. Personal charts are listed only when your role can read them.
- Saving needs write access to System Charts (plus publish rights) or to your own User Charts.

---

## Roadmap

| Milestone | Content | Status |
|---|---|---|
| M1 | Plugin shell, Chart Explorer, read-only XML view, metadata service, logging | ✅ Done |
| M2 | Favourites and recent charts, plain-language chart summary | ✅ Done |
| M3 | XML editor: line numbers, find/replace, format, edit mode, unsaved-change prompts | ✅ Done |
| M4 | Validation (structure, FetchXML, metadata, Unified Interface compatibility) and diff | ✅ Done |
| M5 | Import/export (XML, versioned JSON, CSV inventory) and local backups | ✅ Done |
| M6 | Safe save: confirmation with diff, conflict check, backup, scoped publish, post-save verification | ✅ Done |
| M7 | Documentation and first release | ✅ Done (1.2026.11.1, 2026-11-27) |

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
  docs/                                  supported features, validation rules, troubleshooting, architecture
  src/ChartDesignerPro/                  XrmToolBox plugin (net48, WinForms)
    ChartDesignerPro.csproj              compiles the core sources in, so the tool ships as one DLL
    ChartDesignerPro.nuspec              Tool Library package definition
    ChartDesignerProPlugin.cs            MEF export: name, description, icons, colours
    ChartDesignerProControl.cs           UI: explorer, XML tabs, properties
    Controls/                            grid, banner, toolbar helpers, XML editor (XmlEditorBox, XmlRtf), DiffView
    Data/                                Dataverse access: charts, metadata, log adapter
  src/ChartDesignerPro.Core/             chart logic with no SDK or WinForms dependency
    Abstractions/  Models/  Xml/  Explorer/  Summary/  Editing/  Validation/  Comparison/  ImportExport/  Saving/
  tests/ChartDesignerPro.Tests/          dependency-free test runner
```

## Release notes

**1.2026.11.3**: new icon (donut chart with an XML badge), clearer at small sizes. No other changes.

**1.2026.11.2**: republished so nuget.org and the Tool Library pick up the package icon. No changes to the tool.

**1.2026.11.1**: first release
- Chart Explorer for system and personal charts: table filter, search, sort, paged loading with cancel
- Favourites and recently opened charts (per connection)
- Summary tab: plain-language explanation of what each chart shows, using display names
- XML editor with colouring, line numbers, undo/redo, find/replace, format and live well-formed status; change tracking and discard prompts
- Safe save to Dataverse: validation gate, conflict check, confirmation showing environment, URL and diff, backup first, changed columns only, scoped publish of the chart's table, read-back verification
- Validation: 25 checks across structure, FetchXML, metadata and Unified Interface compatibility, with explanations, suggestions and go-to-line
- Diff of your edits against the stored XML, exact or ignoring formatting
- Import (.json / .xml) into a chart as reviewable edits; export as JSON, chart XML, data or presentation XML; chart list as CSV; local backups
- Properties panel and read-only reasons
- Settings saved per connection

## License
MIT. See [LICENSE](https://github.com/vikashkumar669/XrmTools/blob/main/ChartDesignerPro/LICENSE).
