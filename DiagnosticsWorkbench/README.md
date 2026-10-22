# Dataverse Developer Diagnostics Workbench

An XrmToolBox tool that turns Dataverse plug-in trace logs into diagnostics. It shows which errors keep recurring, which steps are slow, and how related executions chain together. Results can be exported as CSV or as a shareable HTML report.

It doesn't replace Plugin Trace Viewer, FetchXML Builder or Plugin Registration. Loading and listing logs is only the foundation. The value is the analysis on top of the logs.

## Features

| Area | What you get |
| --- | --- |
| **Load** | Filter trace logs by date range (last hour, 24 hours, 7 days, 30 days or a custom range), table, message and plugin type. Loads 5,000 rows per page up to a row cap (50,000 by default), and can be cancelled. |
| **Trace setting** | Reads `organization.plugintracelogsetting` and warns you when logging is **Off** or **Exception**-only. |
| **Errors** | Groups recurring failures by a normalized exception fingerprint. Each group shows hits, distinct executions, first and last seen, a daily trend and the configuration flags of its step. |
| **Performance** | Shows p50, p95 and max duration per plugin type and message. Runs over the slow threshold are counted, and synchronous runs close to the 2-minute timeout are flagged. |
| **Chains** | Links logs that share a correlation ID and shows them as a tree indented by depth. A chain is marked failed if any step failed. |
| **Step metadata** | Shows each step's stage, mode and rank, filtering attributes, images, assembly and version. |
| **Configuration flags** | Flags Update steps without filtering attributes, images that retrieve every column, the same step running at several depths in one chain (possible recursion), and steps that are now disabled. |
| **View filters** | "Errors only", "slow only" and a text search (type, message, exception, trace output and IDs) apply instantly, without reloading. |
| **Detail pane** | Shows the execution context, step registration, flags, full exception, trace output and chain. **Copy as Markdown** gives you a ticket-ready summary. |
| **Export** | Exports the current tab as CSV (UTF-8, Excel-safe) or all loaded logs. The self-contained HTML report can optionally be redacted to strip GUIDs, users and trace output. |
| **Settings** | Saved **per connection**: sync and async thresholds, near-timeout limit, row cap, per-type threshold overrides and custom normalization rules. |

## How fingerprinting works

1. Take the exception type and the first message line. Stack frames, fault XML and trace output are dropped, but an Organization Service `ErrorCode` is kept.
2. Apply your custom rules, then the built-in ones in this order: GUID → `<guid>`, dates and times → `<datetime>`, quoted text → `<str>`, URLs → `<url>`, e-mail addresses → `<email>`, numbers with 2 or more digits → `<n>`. Hex codes such as `0x80040217` are kept.
3. Collapse whitespace and lowercase the text. The fingerprint is the SHA-1 hash of *plugin type + message + normalized text*, shortened to 12 hex characters.

## Build and test locally

Requirements: Visual Studio 2022 or later (or the .NET SDK) with the .NET Framework 4.8 targeting pack, and XrmToolBox installed.

1. **Close XrmToolBox.** A Debug build copies `DiagnosticsWorkbench.dll` into `%APPDATA%\MscrmTools\XrmToolBox\Plugins`.
2. Open `DiagnosticsWorkbench.sln` and build the **Debug** configuration, or run:
   ```
   dotnet build src\DiagnosticsWorkbench\DiagnosticsWorkbench.csproj -c Debug
   ```
3. Run the unit tests (no test framework or packages needed):
   ```
   dotnet run --project tests\DiagnosticsWorkbench.Tests
   ```
4. Start XrmToolBox. The *XrmToolBox* launch profile does this from Visual Studio with F5 and attaches the debugger. Open **Dataverse Developer Diagnostics Workbench** and connect to an environment.
5. Press **Load logs (F5)**.

Set `CopyToXrmToolBox=false` if you don't want Debug builds copied to the Plugins folder.

## Releasing to the XrmToolBox Tool Library

The package follows the same layout as the other tools in this repository: `XrmToolBox` is the first tag, the DLL is under `lib\net48\Plugins\`, the package depends on the `XrmToolBox` package, and the icon is referenced by URL. Diagnostics Workbench is released on its own; it never changes or republishes another tool.

### Option A: GitHub Actions (recommended)
`.github/workflows/diagnostics-workbench.yml` runs only when files under `DiagnosticsWorkbench/` (or the workflow itself) change. It builds the Release DLL, checks that the csproj and nuspec versions match, packs and checks the `.nupkg` layout, and uploads it as a run artifact. When a push to `main` carries a version that **isn't on nuget.org yet** (or a tag `DiagnosticsWorkbench-v<version>` is pushed), it also publishes to nuget.org with **Trusted Publishing** and creates the GitHub release `DiagnosticsWorkbench-v<version>`.

**One-time setup:** on nuget.org, *Account → Trusted Publishing*, add a policy for owner `VikashKumar0491`, repository `vikashkumar669/XrmTools`, workflow file `diagnostics-workbench.yml`. Until it exists, the publish step fails but the build and pack still run.

### Option B: from your PC
1. Bump the version in **both** `src/DiagnosticsWorkbench/DiagnosticsWorkbench.csproj` and `src/DiagnosticsWorkbench/DiagnosticsWorkbench.nuspec` (format `1.YYYY.M.rev`), and update `<releaseNotes>`. The script refuses to pack if they differ.
2. Build and pack:
   ```powershell
   cd <your clone>\XrmTools\DiagnosticsWorkbench
   .\build\pack.ps1                                    # build + pack -> .\artifacts\
   .\build\pack.ps1 -Push -ApiKey <your-nuget-api-key>  # build + pack + push
   ```
3. After the first push, wait until the package is *listed* on nuget.org, then submit **`VikashKumar.XrmToolBox.DiagnosticsWorkbench`** on the XrmToolBox tool submission page.

## Project layout

```
src/DiagnosticsWorkbench.Core/   analysis core: plain C#, no SDK/UI dependency (net48 + net8.0)
  Models/    TraceLog, StepInfo, result types, settings
  Analysis/  ExceptionNormalizer, ErrorGrouper, PerformanceAnalyzer, CorrelationBuilder, ConfigFlagger, DiagnosticsSession
  Export/    CsvExporter, HtmlReportBuilder, MarkdownFormatter
  Data/      FetchXmlBuilder
src/DiagnosticsWorkbench/        XrmToolBox plugin (net48): compiles the core sources in, so it ships as one DLL
  Data/      TraceLogRepository, StepMetadataRepository (Dataverse access)
  Controls/  CardList (colour-coded cards), SettingsDialog, UI helpers
tests/DiagnosticsWorkbench.Tests/      dependency-free test runner for the core
tests/DiagnosticsWorkbench.TestPlugin/ scenario plug-in used for live testing (not shipped)
```

## Permissions needed

The tool needs read access to `plugintracelog`, `organization` (for the trace setting), `sdkmessageprocessingstep`, `sdkmessageprocessingstepimage`, `plugintype` and `pluginassembly`. It never writes to Dataverse.

## Release notes

**1.2026.10.1**: first release (tested live in XrmToolBox against a trial environment)
- Load plug-in trace logs by date range, table, message and plugin type (paged, with a row cap and cancel)
- Trace setting check with a warning when logging is Off or Exception-only
- Errors: recurring failures grouped by normalized exception fingerprint, with hits, executions, first/last seen and trend
- Performance: p50/p95/max per plugin type, message and mode; slow and near-timeout runs; per-type threshold overrides
- Chains: multi-step executions linked by correlation ID, shown as a tree by depth
- Step metadata: stage, rank, filtering attributes, images and assembly; configuration flags
- Export to CSV and a self-contained HTML report, optionally redacted; Copy as Markdown
- Settings and custom normalization rules saved per connection

## License

MIT. See [LICENSE](https://github.com/vikashkumar669/XrmTools/blob/main/DiagnosticsWorkbench/LICENSE).
