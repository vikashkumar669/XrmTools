<p align="center"><img src="https://raw.githubusercontent.com/vikashkumar669/XrmTools/main/DataQualityStudio/src/DataQualityStudio/Resources/icon128.png" width="96" alt="Data Quality Studio icon"></p>

# Data Quality Studio for XrmToolBox

**Dataverse Excel Mapping & Data Quality Studio**: a configuration-driven import validator for teams working with Excel migration templates.

Open an Excel or CSV file, map its columns to a Dataverse table, and find every data problem *before* you import. Each issue comes with its row number, column, message and a suggested fix, and you can export the results, including a copy of the data with the fixes applied.

| | |
|---|---|
| **NuGet package** | `VikashKumar.XrmToolBox.DataQualityStudio` |
| **Current version** | 1.2026.10.1 |
| **Requires** | XrmToolBox 1.2025.10.74 or later (.NET Framework 4.8) |
| **Author** | Vikash Kumar |
| **License** | MIT |

**Why it stands out:** it combines Excel-based migration preparation, mapping validation and data-quality checks in one tool, and it only reads from Dataverse. Nothing is created or changed in your environment.

---

## Features

### 1. Uses the active XrmToolBox connection
Tables and columns come from the environment you're connected to. Use **⟳** to reload metadata after you change customizations.

### 2. Excel or CSV input
- `.xlsx` / `.xlsm` workbooks (read directly, so Excel doesn't need to be installed). Pick the **sheet** and the **header row**, so title rows above the headers are fine. Real Excel dates are read as dates, including the 1904 date system.
- `.csv` / `.txt`, with the delimiter (`,` `;` tab `|`) and encoding (UTF-8, UTF-16, ANSI) detected automatically. Excel's `sep=` hint and quoted multi-line values are supported.
- **Other formats: save as `.xlsx` first.** Legacy `.xls` (Excel 97–2003), `.xlsb`, `.ods` and Google Sheets aren't read directly:
  - **Excel:** *File → Save As → Excel Workbook (\*.xlsx)*
  - **Google Sheets:** *File → Download → Microsoft Excel (.xlsx)*
  - **LibreOffice:** *File → Save As → Excel 2007-365 (.xlsx)*

  Password-protected workbooks must be saved without the password. If the tool can't read a file, it tells you why and the validation doesn't start.

### 3. Mapping
- **Auto-map** matches headers to columns by display name, logical name, schema name, logical name without the publisher prefix, `Display (logicalname)` style headers, common abbreviations (`No.`, `Qty`, `Tel`, `DOB`…) and similarity. Fuzzy guesses are marked **Suggested (n%)** so you can check them.
- For each column you can set:
  - **Transform**: UPPERCASE, lowercase, Title Case, Collapse spaces, Remove spaces, Digits only
  - **Value map**: `Old=New; Old2=New2`, applied before validation (e.g. `Y=Yes; N=No; Hot=1`). Use `\;` or `\=` for a literal `;` or `=`.
  - **Default**: the value used when a cell is empty
  - **Lookup match column**: the column on the target table used to find records. Blank means its primary name column.
  - **Key**: columns that together identify a row. Duplicate keys in the file are flagged.
- The bar above the grid shows required columns that aren't mapped and any Dataverse column mapped twice.

### 4. Validation
| Check | Result |
|---|---|
| Required column not mapped (Create mode) | Error, reported once |
| Required value empty | Error in Create mode; warning in Update mode (it would clear the value) |
| Text longer than the column's max length | Error; the fix is the value cut to the max length |
| Line breaks in single-line text | Warning; the fix replaces them with spaces |
| E-mail / URL format columns | Warning; the fix is the repaired value when one is obvious (`www.x.com` → `https://www.x.com`, stray spaces removed) |
| Whole number, decimal, float, currency | Must parse (`1,234.56`, `1.234,56`, `$1,200`, `(500)`, `EUR 10`); range and precision are checked; symbols that were removed are reported |
| Date / date and time | ISO, day-first, month-first, named months and Excel serial numbers are accepted. Ambiguous dates (`03/04/2026`) and dropped times on date-only columns get a warning; impossible dates and years before 1753 are errors |
| Yes/No | `Yes/No`, `true/false`, `1/0`, `Y/N` and the column's own labels |
| Choice, Status, Status reason | Matched by label (any language) or numeric value. Loose matches (`E-mail` → `Email`) get a warning; unknown values get an error with *Did you mean…?* |
| Choices (multi-select) | Each value separated by `;` (or `,` / `|`) is checked |
| Status reason vs Status | A status reason must belong to the mapped status |
| Lookup, Customer, Owner | Checked against Dataverse in batches of 100 values. **Not found** → error with similar names; **several matches** → warning; **inactive record** → warning. GUIDs work for every lookup. For lookups with several target tables, prefix the value with the table (`contact:Jane Doe`). Owners can be matched by e-mail address. |
| Duplicate keys | Warning pointing to the first row with the same key |
| Unmapped source columns | Info. They're listed and skipped, and **validation always runs to the end** |

Options: **Create / Update** mode, date order, number format, trim spaces, and **Check lookups** (turn it off for a quick offline pass).

### 5. Preview
The **Preview** tab shows every row as it would be imported: choices with their value (`Consulting (9)`), lookups with the record they matched (`→ Contoso Ltd [account]`), and dates and numbers normalized. Cells are colored by severity; hover over one to see its messages. You can filter to rows with errors, or with errors or warnings.

### 6. Issues and exports
- The **Issues** tab lists every problem with its row, severity, source column, Dataverse column, value, message and suggested fix. You can filter by severity, search, and sort by any column. Double-click an issue to jump to its cell in the preview, or to its mapping row.
- **Export results**:
  - **Excel workbook (.xlsx)** with four sheets:
    - *Summary*: counts, options and the mapping
    - *Issues*
    - *Data with fixes*: the original rows with every concrete correction applied (green) and unresolved problems in red or amber. A status column comes first and the row's issues are in the last column, so long issue text never runs over empty data cells
    - *Transformed preview*
  - **Issues as CSV**
  - **Data with suggested fixes as CSV**, ready to fix the rest and import
- Suggested fixes are only filled in when they're safe. Lookups are never swapped for a similar name automatically; the similar names are listed in the suggestion instead.
- All Excel output is saved as `.xlsx`, so it opens in Excel, Excel Online, Google Sheets or LibreOffice and can be fed straight back into the tool.
- **Template** creates a blank `.xlsx` import workbook for the table: display-name headers (required ones in red) and a *Column reference* sheet with types, max lengths and allowed values.

### 7. Reusable mapping profiles
**Profiles ▾ → Save profile** (Ctrl+S) stores the table, the options, the sheet and header row, and every column mapping as a readable JSON file in `%APPDATA%\MscrmTools\XrmToolBox\Settings\DataQualityStudio\Profiles`. Load a profile on next month's file and only new columns need attention. If a profile column is missing from the file, you're told which ones. **Export profile to file / Import profile from file** let you share profiles with your team or keep them in source control.

A profile looks like this (only non-default settings are written for each column):
```json
{
  "Name": "Accounts migration sample",
  "EntityLogicalName": "account",
  "Mode": "Create",
  "DateOrder": "Auto",
  "NumberFormat": "Auto",
  "TrimWhitespace": true,
  "CheckLookups": true,
  "SheetName": "Accounts",
  "HeaderRow": 2,
  "Mappings": [
    { "SourceColumn": "Account Name", "TargetField": "name" },
    { "SourceColumn": "Account Number", "TargetField": "accountnumber", "IsKey": true },
    { "SourceColumn": "Credit Hold", "TargetField": "creditonhold", "ValueMap": "Y=Yes; N=No" },
    { "SourceColumn": "Primary Contact", "TargetField": "primarycontactid", "LookupMatchField": "fullname" },
    { "SourceColumn": "Legacy ID" }
  ]
}
```
A column without `TargetField` is deliberately left unmapped, so auto-map won't touch it.

---

## How to use it

1. Connect to an environment in XrmToolBox and open **Data Quality Studio**.
2. **Open file…** (Ctrl+O). Choose the sheet and the header row.
3. Pick the **Table** (type to search). Columns are auto-mapped.
4. Review the **Mapping** tab: fix suggested matches, and add value maps, defaults, lookup match columns and keys.
5. Press **▶ Validate** (F5).
6. Work through **Issues**, check the **Preview**, then use **Export results**.
7. Save the mapping as a **profile** (Ctrl+S) for next time.

Try it with `samples/Accounts migration sample.xlsx` (header row **2**, table **Account**). It has deliberate problems: a missing name, an over-long name, a misspelled industry, a decimal employee count, an ambiguous date, an invalid date, a duplicate account number, bad e-mails and URLs, an invalid Yes/No value, and contacts that won't exist in your environment.

**Permissions:** read access to the table's metadata and to the lookup target tables. The tool never writes to Dataverse.

### Toolbar and shortcuts
| Item | What it does |
|---|---|
| **Open file…** (Ctrl+O) | Open an `.xlsx`, `.xlsm`, `.csv` or `.txt` file |
| **Sheet / Header row** | Choose the worksheet and the row that holds the headers; the file is re-read immediately |
| **Table** | Dataverse table to validate against (type to search, Enter to pick) |
| **⟳** | Reload tables and columns from Dataverse |
| **▶ Validate** (F5) | Run all checks and build the preview (can be cancelled) |
| **Export results ▾** | Excel workbook, issues CSV, or corrected data CSV |
| **Template** | Blank `.xlsx` import template for the table |
| **Profiles ▾** | Save, load, import, export and delete mapping profiles |
| Mapping tab: **Auto-map / Re-map all / Clear** | Map unmapped columns, redo the whole mapping, or unmap everything |
| Mapping tab: **Mode, Dates, Numbers, Trim spaces, Check lookups** | Validation options (saved in profiles) |

---

## Troubleshooting
| Symptom | Fix |
|---|---|
| *The file isn't a valid .xlsx workbook* | The file is `.xls`, password protected or damaged. Save it again as `.xlsx` (see above). |
| *Row N is empty, so it can't be used as the header row* | Pick the row that has the column headers in **Header row**. |
| Export fails with *is the file open in Excel?* | Close the file in Excel or pick another file name. |
| A new column doesn't appear in the **Dataverse column** list | Click **⟳** to reload metadata. Only columns that are valid for create or update are listed. |
| Lookups show *not verified* | You can't read the target table, or the **Lookup match column** doesn't exist on it. Check the mapping-level issues at the top of the Issues tab. |
| Validation of a big file with many lookups is slow | Lookups are queried 100 distinct values at a time. Turn off **Check lookups** for a quick first pass, then turn it back on for the final check. |
| Dates like `03/04/2026` are flagged as ambiguous | Set **Dates** to day/month/year or month/day/year to match the file. |

---

## Tested scenarios

On 18 Oct 2026, the validation engine, file readers and exporters were tested by an automated suite, and the UI ran end to end against a simulated Dataverse service with the account and contact metadata:

| Scenario | Result |
|---|---|
| `.xlsx` with a title row, Excel dates and date-times, and blank cells; semicolon CSV with UTF-8 BOM, European numbers and quoted line breaks | Read correctly |
| Auto-mapping the sample file's 15 headers | 13 mapped by display name (including `No. of Employees`); 2 reported as unmapped |
| The sample file's deliberate problems | Every one detected, with the expected message and suggested fix |
| Lookup checks | 2 batched queries for the whole file; case-insensitive match, inactive record, not found with *Did you mean* |
| Exports | Excel opens with frozen headers and filters; CSVs open in Excel |
| Profile save, clear and re-apply | Mappings, value maps and keys restored |
| 50,000 rows × 13 mapped columns | Validated in about 3 s; Excel export in about 5 s |

### Live test in XrmToolBox (19 Oct 2026)
Built in Visual Studio 2026 (Debug) and run in XrmToolBox 1.2026.9.78 against a Dataverse trial environment (**CS Trial**):

| Scenario | Result |
|---|---|
| Build and install | Rebuild succeeded; the post-build step copied the DLL to the Plugins folder; the tool appears in *Tools* with its icon, version and author |
| Open with the active connection | 1,934 tables loaded; Account: 126 importable columns, 1 required |
| `Accounts migration sample.xlsx`, header row 2, table Account | 13 of 15 columns auto-mapped; 15 errors and 9 warnings in 2.0 s. Choice values come from the environment (Consulting = 7), and lookups were checked against real contacts. The sample's *Last Date Included in Campaign* is reported as not settable on create, which is correct for that column. |
| `Contacts sample (semicolon).csv`, table Contact | 8 of 9 columns auto-mapped. The Company Name customer lookup was checked against both account and contact; `45.000,00` was read as 45000 |
| Export to Excel | Opens in Excel without repair prompts, with all four sheets, colors, frozen headers and filters |
| Save profile (Ctrl+S) | Saved to the profiles folder |

Fixed after the live test: dotted dates (`12.05.1985`) are read as day-first instead of being flagged as ambiguous; short codes such as `M` now suggest *Male*; opening a new file resets the header row to 1 unless a profile says otherwise; the "closest column" hint for unmapped headers is stricter.

Test it with your own data before relying on it for a production migration.

---

## Build and debug

1. Open `DataQualityStudio.sln` in Visual Studio 2022 or 2026 with the *.NET desktop development* workload.
2. **Close XrmToolBox completely**, then build in **Debug**. A post-build step copies `DataQualityStudio.dll` to `%APPDATA%\MscrmTools\XrmToolBox\Plugins`. If XrmToolBox is running, the copy fails with *MSB3027 (file locked)*.
3. To debug with F5, set `executablePath` in `src/DataQualityStudio/Properties/launchSettings.json` to your `XrmToolBox.exe` and use the **XrmToolBox** launch profile.

The tool is a single DLL with no third-party dependencies. Excel files are read and written with `System.IO.Compression`, and profiles use `DataContractJsonSerializer`.

---

## Releasing to the XrmToolBox Tool Library

The package is set up like Theme Migrator: `XrmToolBox` is the first tag, the DLL is under `lib\net48\Plugins\`, and the package depends on the `XrmToolBox` package. Data Quality Studio is released on its own; it never changes or republishes Theme Migrator.

### Option A: GitHub Actions (recommended)
`.github/workflows/data-quality-studio.yml` runs only when files under `DataQualityStudio/` (or the workflow itself) change. It builds the Release DLL, checks that the csproj and nuspec versions match, packs and checks the `.nupkg` layout, and uploads it as a run artifact. When a push to `main` carries a version that **isn't on nuget.org yet** (or a tag `DataQualityStudio-v<version>` is pushed), it also publishes to nuget.org with **Trusted Publishing** and creates the GitHub release `DataQualityStudio-v<version>`.

**One-time setup:** on nuget.org, *Account → Trusted Publishing*, add a policy for owner `VikashKumar0491`, repository `vikashkumar669/XrmTools`, workflow file `data-quality-studio.yml`. Until it exists, the publish step fails but the build and pack still run.

### Option B: from your PC
1. Bump the version in **both** `src/DataQualityStudio/DataQualityStudio.csproj` and `src/DataQualityStudio/DataQualityStudio.nuspec` (format `1.YYYY.M.rev`), and update `<releaseNotes>`.
2. Build and pack:
   ```powershell
   cd <your clone>\XrmTools\DataQualityStudio
   .\build\pack.ps1                                    # build + pack -> .\artifacts\
   .\build\pack.ps1 -Push -ApiKey <your-nuget-api-key>  # build + pack + push
   ```
3. After the first push, wait until the package is *listed* on nuget.org, then submit **`VikashKumar.XrmToolBox.DataQualityStudio`** on the XrmToolBox tool submission page.

---

## Project layout
This tool lives in its own folder of the [XrmTools](https://github.com/vikashkumar669/XrmTools) repository, next to Theme Migrator, with its own solution, package and workflow.
```
XrmTools/
  .github/workflows/data-quality-studio.yml  build, pack, publish (this tool only)
  DataQualityStudio/
    DataQualityStudio.sln
    build/pack.ps1                         build, pack, push from a PC
    samples/                               sample workbook, CSV and profile
    src/DataQualityStudio/
      DataQualityStudio.csproj             net48, XrmToolBoxPackage (compile-only)
      DataQualityStudio.nuspec             Tool Library package definition
      DataQualityStudioPlugin.cs           MEF export: name, description, icons, colors
      DataQualityStudioControl.cs          UI: toolbar, Mapping / Preview / Issues tabs
      Models/Models.cs                     mappings, profiles, metadata and result models
      Services/SpreadsheetReader.cs        .xlsx and CSV reader
      Services/MetadataService.cs          table and column metadata (cached)
      Services/MappingSuggester.cs         auto-mapping
      Services/ValidationEngine.cs         rules, preview values, suggestions
      Services/LookupResolver.cs           batched lookup checks and "did you mean"
      Services/ValueParsers.cs             number, date, Yes/No parsing; text similarity
      Services/XlsxWriter.cs               .xlsx writer
      Services/ResultExporter.cs           Excel/CSV exports and import template
      Services/ProfileStore.cs             profile files
      Controls/UiHelpers.cs                grid, toolbar and dialog helpers
      Resources/icon32|80|128|256.png      tool icons
```

## Release notes

**1.2026.10.1**: first release (tested live in XrmToolBox, see *Tested scenarios*)
- Excel (`.xlsx`/`.xlsm`) and CSV input with sheet and header-row selection
- Auto-mapping by display name, logical name, schema name, abbreviations and similarity
- Validation of required columns, text, formats, numbers, dates, Yes/No, choices, status pairs, lookups and duplicate keys
- Transforms, value maps and defaults per column
- Preview, issues list with suggested fixes, Excel/CSV exports and import template
- Reusable mapping profiles

## License
MIT. See [LICENSE](https://github.com/vikashkumar669/XrmTools/blob/main/DataQualityStudio/LICENSE).
