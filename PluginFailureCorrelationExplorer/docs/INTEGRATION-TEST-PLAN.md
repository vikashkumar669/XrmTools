# Integration test plan

Run this plan against a **non-production** Dataverse environment, such as a trial or developer environment. It doesn't need production data or credentials. Record the result of each step (Pass, Fail or Note) and the build version.

## Setup

1. **Use a non-production environment** where you have System Customizer (or System Administrator) rights. Also prepare a second user with only a basic role, for step P1.
2. **Turn tracing on.** Set plug-in trace logging to **All**: *Settings → Administration → System Settings → Customization → Enable logging to plug-in trace log*.
3. **Register a scenario plug-in** that can produce controlled outcomes. The Diagnostics Workbench repository folder has one you can reuse (`DiagnosticsWorkbench/tests/DiagnosticsWorkbench.TestPlugin`). Register steps that:
   - throw `InvalidPluginExecutionException` with a varying record name (recurring fingerprint)
   - throw `NullReferenceException` in two **different** plug-in classes with the same message (fingerprint separation)
   - sleep for 2.5 s synchronously, and 12 s asynchronously (slow thresholds)
   - update the same record from a post-operation step at depth 1 to trigger a nested step (parent/child, same correlation)
   - write `ITracingService` output containing a fake token `Bearer test.abc.def`, a fake e-mail address and a GUID (redaction)
4. **Generate traffic.** Create or update about 50 records to exercise those steps.
5. **Install the build.** Close XrmToolBox, then copy `PluginFailureCorrelationExplorer.dll` to `%APPDATA%\MscrmTools\XrmToolBox\Plugins` (a Debug build does this), and restart XrmToolBox.

## Connection and retrieval

| # | Step | Expected |
| --- | --- | --- |
| C1 | Open the tool without a connection, then connect | The header shows "Not connected", then the organization name, URL, environment ID (when available), version and "Plug-in trace log: All" |
| C2 | Analyze the last 24 hours, with no filters | The trace count matches an Advanced Find count of plug-in trace logs for the same range (within traces written during the run) |
| C3 | Set the result limit to 10 and analyze | 10 traces; the dashboard banner and report say the limit was reached; PFC005 "result limit" finding |
| C4 | Exceptions: *Only traces with an exception* | Every trace has an exception; **confirms the server-side `not-null` filter**. If a fault occurs instead, record its message: the fallback note should appear and results should still load |
| C5 | Exceptions: *Only traces without an exception* | No trace shows ✖ |
| C6 | Filter by message, table, plug-in type (partial), correlation ID, request ID and minimum duration, one at a time | Each filter narrows the results correctly; an invalid GUID shows a validation message and makes no call |
| C7 | Analyze 30 days with the limit at 50,000, then press Cancel while it is retrieving | Stops after the current page; a partial analysis with a "cancelled" note; the UI stays responsive throughout |
| C8 | Select traces quickly with the arrow keys | No freeze; trace text loads for the trace that stays selected |
| C9 | Delete one trace in another window, then select it in the tool | The detail shows "couldn't be loaded" with a plain message; no crash |

## Analysis

| # | Step | Expected |
| --- | --- | --- |
| A1 | Failure groups tab | The InvalidPluginExecutionException failures with different record names form **one** group; the two NullReferenceException plug-ins form **two** groups |
| A2 | Group detail | Shows the rules applied, the normalized message and the hashed input; double-click opens exactly that group's traces |
| A3 | Trace explorer, nested step | The nested trace shows the depth-1 trace as **Suspected parent/child** (parent), and other traces in the chain as **Same causal chain** |
| A4 | Chain ◀ ▶ | Moves through the correlation chain in start-time order |
| A5 | Slow steps | The 2.5 s sync step and 12 s async step appear in PFC003; a 2.5 s async step does **not** |
| A6 | Settings: sync threshold 3,000 ms | Re-analysis without a Dataverse call; the 2.5 s sync step is no longer slow |
| A7 | Settings: custom rule (e.g. `ORD-\d+` → `<order>`) | Re-analysis; groups merge as expected; a note says saved baselines are no longer comparable |

## Trend comparison

| # | Step | Expected |
| --- | --- | --- |
| T1 | Compare with *No baseline* | "Trend analysis unavailable" with a reason; no PFC006 or PFC007 findings |
| T2 | Analyze the last hour with *Previous period* after generating new failures | New and increasing patterns are reported; the baseline description shows the previous window |
| T3 | Previous period with no traces (e.g. a range before tracing was enabled) | Refused: "baseline period contains no traces" |
| T4 | Enable snapshots, analyze, change a filter, then compare with the snapshot | Refused: "different filters" |

## Reports and privacy

| # | Step | Expected |
| --- | --- | --- |
| R1 | Export HTML with *Redact exports* on | Opens in Edge or Chrome without errors; no `<script>`; GUIDs appear as `[GUID-n]`; the fake e-mail and token are absent |
| R2 | Export HTML with redaction off | GUIDs visible; the bearer token is **still** absent |
| R3 | Search every export for the trace output text | Not present (trace text is never exported) |
| R4 | Export each CSV type and open it in Excel | Correct columns; UTF-8 characters intact; no formulas evaluated |
| R5 | Export JSON | Valid JSON (e.g. `Get-Content x.json \| ConvertFrom-Json`) |
| R6 | Copy diagnostic summary | Contains IDs, type, message and fingerprint; no trace output; no token |

## Permissions and failures

| # | Step | Expected |
| --- | --- | --- |
| P1 | Connect as the basic user and analyze | Plain message about the missing read privilege on plugintracelog; nothing crashes |
| P2 | Remove read access on `pluginassembly` only | Analysis works; a note says step details couldn't be read |
| P3 | Disconnect the network during retrieval | Plain network message; the UI recovers |
| P4 | Let the connection token expire (or revoke the session), then analyze | "Reconnect in XrmToolBox" message |
| P5 | Set the trace setting to *Exception* and analyze | Header pill and banner warn; a PFC005 finding about the trace setting |

## Exit criteria

Every C, A and R step passes. Any Fail in the P steps has an understandable message and no crash. Record the result of C4 (server-side `not-null` on `exceptiondetails`) in the README's known limitations.
