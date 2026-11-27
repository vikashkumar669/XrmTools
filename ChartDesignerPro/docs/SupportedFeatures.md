# Supported features and limitations

This page says what Chart Designer Pro does in version 1.2026.11.1, what the platform allows, and what was verified on a real environment. It does not claim support for anything that hasn't been checked.

**Key:** ✅ supported · 🟡 supported with limitations · ⛔ not supported in this version

## Charts the tool works with

| Chart | Read | Edit and save | Notes |
|---|---|---|---|
| System chart (`savedqueryvisualization`), unmanaged | ✅ | ✅ | Saving publishes the chart's table (optional). Verified on a sandbox. |
| System chart, managed and customizable | ✅ | 🟡 | Your change goes into an unmanaged layer on top of the managed solution, as with any customization. |
| System chart, managed and **not** customizable | ✅ | ⛔ | Read-only. Change it in the solution that owns it. |
| Personal chart (`userqueryvisualization`) you own | ✅ | ✅ | Takes effect when saved; no publishing. Verified on a sandbox. |
| Personal charts of other users | ⛔ | ⛔ | Dataverse only returns your own personal charts and those shared with you. |
| Power BI visualization (`charttype` = 1) | ✅ | ⛔ | Shown read-only. |
| Web-resource visualization | ✅ | ⛔ | Shown read-only. |

## Features

| Feature | Status | Notes |
|---|---|---|
| Chart list for one table or all tables, search, sort, favourites, recent | ✅ | Paged by 5,000 with Cancel. |
| Plain-language summary of a chart | ✅ | Uses display names from metadata; falls back to logical names. |
| XML editor (colouring, line numbers, undo/redo, find/replace, format) | ✅ | Edits the exact stored text. *Indent (view only)* never changes the stored XML. |
| Validation (25 rules) | ✅ | See [ValidationRules.md](ValidationRules.md). Rules are text only; nothing is fixed automatically. |
| Diff of your edits against the stored XML | ✅ | Exact, or ignoring formatting. |
| Import a chart file into the open chart as edits | ✅ | Tool JSON, `<visualization>` XML (the app's *Export Chart* format, verified), bare `<datadefinition>` or `<Chart>`. |
| Export JSON, chart XML, data or presentation XML, chart list CSV | ✅ | The XML export can be imported with the app's *Import Chart* (verified). |
| Local backups | ✅ | JSON files per environment; a backup is a recovery copy, not a rollback. |
| Save to Dataverse with confirmation, conflict check, backup and verification | ✅ | Only changed XML columns are written. See the README for the steps. |
| Publish only the chart's table | ✅ | Scoped `PublishXmlRequest`; never *Publish all*. Verified on a sandbox. |
| Create, delete, assign or share charts | ⛔ | Not in this version. |
| Change a chart's name, description or default flag | ⛔ | Not in this version. Only the data and presentation XML are edited. |
| Visual designer (choose type, categories, measures without XML) | ⛔ | Planned. |
| Chart preview | ⛔ | Planned. A local preview will be an approximation: model-driven apps render charts with a different engine. |
| Compare charts, compare environments, bulk validation | ⛔ | Planned. |

## What was verified on a real environment (2026-11-27)

On a Dynamics 365 sandbox with test charts:
- The app's **Export Chart** file is `<visualization>` with `<visualizationid>`, nested data and presentation XML and `isdefault` true/false. The tool reads it exactly.
- The app's **Import Chart** requires `<visualizationid>`, but always creates a new personal chart; it never overwrites a chart by id.
- Dataverse rejects a chart whose chart areas don't match its categories (rule CDP-S008).
- Top N (`count` with an `order` on the aggregate alias), two series with month grouping and a secondary axis, and a comparison chart all render in Unified Interface.
- Custom colours are replaced by the modern look unless `CustomColorOverride="true"`. A pie's `#PERCENT` label showed counts.
- Saving a personal chart and a system chart (with scoped publish) changed the chart in the app; the conflict check caught a change made after the chart was opened.

## Platform limits the tool reports

- At most 5 series with one category; comparison charts take exactly one series and two grouped attributes.
- A secondary Y axis is supported for multi-series charts; there is no secondary X axis.
- Pie and Funnel charts ignore series colours.
- `#VAL` and `#TOTAL` labels aren't supported, and `#PERCENT` only partly.
- Chart files imported through the app are limited to 16,000 base64 characters.
- At run time a chart's query is combined with the filters of the view it's displayed with.
