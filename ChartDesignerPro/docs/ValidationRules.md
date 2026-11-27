# Validation rules

Chart Designer Pro checks the open chart every time it is opened and while you edit it. The results are in the **Validation** tab: double-click a result with a line number to jump to it.

**Severities**
- **Error**: Dataverse is likely to reject the chart or the chart won't load. **Errors block saving.**
- **Warning**: the chart probably doesn't do what you expect. You can save after ticking a box in the save dialog.
- **Information**: something worth knowing, for example a property Unified Interface ignores.
- **Passed**: one row per rule that found nothing (tick *Passed* to see them).

Rules never change the chart. Suggestions are text only.

Metadata rules (`CDP-M…`) need the table's metadata. If it can't be read, they report *not checked: metadata unavailable* rather than *passed*. Use **⟳ Metadata** to read it again.

## Structure

| Rule | Checks | Severity |
|---|---|---|
| CDP-S001 | The data XML (`datadescription`) is well-formed. | Error |
| CDP-S002 | The presentation XML (`presentationdescription`) is well-formed. An empty presentation XML is a warning. | Error / Warning |
| CDP-S003 | The data XML has `<datadefinition>`, `<fetchcollection>`, `<fetch>` and `<categorycollection>`. | Error |
| CDP-S004 | Each `<category>` has a `<measurecollection>`, and each `<measure>` has an `alias`. | Error |
| CDP-S005 | Every measure alias matches an aggregated attribute in the query. | Error |
| CDP-S006 | The number of measure collections matches the number of `<Series>` in the presentation XML. | Warning |
| CDP-S007 | The data XML contains one `<fetch>` query. | Warning |
| CDP-S008 | There is one non-empty `<ChartArea>` per category. Dataverse rejects the save with *"The number of chart areas must be equal to the number of categories."*, and a `<ChartArea>` with no attributes and no children isn't counted (verified on Dataverse, 2026-11-27). | Error |
| CDP-S020 | The chart is within the documented 16,000-character (base64) limit for importing a chart file through the app. Saving through this tool isn't affected. | Warning |

## FetchXML

| Rule | Checks | Severity |
|---|---|---|
| CDP-F001 | The `<fetch>` has `aggregate="true"` and an `<entity>`. | Error |
| CDP-F002 | Grouped attributes (`groupby="true"`) have an alias. | Error |
| CDP-F003 | Aggregate functions are valid (`count`, `countcolumn`, `sum`, `avg`, `min`, `max`). | Error |
| CDP-F004 | `dategrouping` values are valid (`day`, `week`, `month`, `quarter`, `year`, `fiscal-period`, `fiscal-year`), and only used on grouped attributes. | Error / Warning |
| CDP-F005 | Aliases are unique. | Warning |

## Metadata

| Rule | Checks | Severity |
|---|---|---|
| CDP-M001 | The query's `<entity>` is the chart's table (`primaryentitytypecode`). | Error |
| CDP-M002 | Every column in the query exists on its table, including linked tables. Linked tables whose metadata can't be read are reported as not checked. | Error / Information |
| CDP-M003 | Aggregations suit the column type: `sum` and `avg` on number or currency columns; `min` and `max` on number, currency or date columns. A note explains that `count` counts records, not values. | Error / Information |
| CDP-M004 | `dategrouping` is only used on date columns, and a date column grouped without `dategrouping` (one category per distinct date and time) is flagged. | Error / Warning |

## Unified Interface compatibility

Model-driven apps draw charts with a different engine from the one the XML was designed for. Only a documented subset of presentation properties is honoured. These rules follow Microsoft's documentation, last reviewed on 2026-11-25.

| Rule | Checks | Severity |
|---|---|---|
| CDP-U001 | Each `<Series>` has a `ChartType`, and the type is one documented for model-driven charts (Column, StackedColumn, StackedColumn100, Bar, StackedBar, StackedBar100, Area, StackedArea, StackedArea100, Line, Pie, Funnel, Tag, Doughnut, Point). | Warning |
| CDP-U002 | Presentation properties are in the documented Unified Interface subset. Others are kept in the XML but ignored by the app. | Information |
| CDP-U003 | At most five series with one category; a comparison chart (two grouped attributes) has exactly one series; no more than two grouped attributes. | Warning |
| CDP-U004 | Label formats are supported. `#VAL` and `#TOTAL` aren't supported (warning); `#PERCENT` is only partly supported (information). In a sandbox test a pie with `Label="#PERCENT{P0}"` showed counts. | Warning / Information |
| CDP-U005 | Series colours are used by the chart type. Pie and Funnel charts ignore `Series.Color`. | Information |
| CDP-U006 | Custom colours (`Color`, `PaletteCustomColors`) are kept only when `CustomColorOverride="true"`; otherwise the modern look replaces them. Confirmed in a sandbox test. | Information |
| CDP-U007 | Colours use a supported format: `r,g,b`, `a,r,g,b`, `#RRGGBB` or a named colour. | Warning |

## Sources

- Microsoft Learn: *Understand charts: underlying data and chart representation* (the Unified Interface property list)
- Microsoft Learn: *Visualization data description schema*
- Microsoft Learn: *FetchXML aggregation*
- Microsoft Support: chart label issue with `#PERCENT`, `#VAL` and `#TOTAL`
- Sandbox tests on 2026-11-27, where noted above
