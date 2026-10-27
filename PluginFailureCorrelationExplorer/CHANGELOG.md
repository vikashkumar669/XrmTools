# Changelog

All notable changes to Plugin Failure Correlation Explorer. Versions follow the XrmToolBox convention `1.YYYY.M.revision`.

## 1.2026.10.1 (unreleased)

First version.

- **Retrieval.** Plug-in trace logs are retrieved through the XrmToolBox connection (read-only).
  - Server-side filters for date range, exception presence, message, table, plug-in type, correlation ID, request ID and minimum duration.
  - 5,000-row pages, a configurable result limit, and cancellation.
  - Service protection faults are retried at most twice, honouring Retry-After.
  - Exception text is loaded only for failed traces, and trace text only on demand. The step configuration columns are never queried.
- **Fingerprinting.** Exceptions are fingerprinted with named, ordered normalization rules (GUIDs, dates and times, reference numbers, URLs, e-mail addresses, quoted values, IL offsets, line numbers, numbers) plus custom rules. The plug-in type and top user frames are included, so generic messages don't merge.
- **Correlation.** Links between traces carry evidence levels: exact request, same causal chain, suspected parent/child, heuristic and same pattern. Each link explains which signals produced it.
- **Performance analysis.** Thresholds apply per execution mode. Average and median are reported from 5 measured executions, and the nearest-rank P95 from 20. Executions near the 2-minute timeout are flagged.
- **Diagnostic rules.** Nine rules (PFC001–PFC009). Each finding has facts, metrics, correlation evidence, possible explanations, recommendations and supporting trace IDs.
- **Trend comparison.** Compare with the previous period or a saved aggregate snapshot. The comparison is refused, with a reason, when it wouldn't be like-for-like.
- **User interface.** Dashboard, Failure groups, Trace explorer (related traces, chain navigation, find, copy) and Diagnostic report tabs.
- **Exports.** Standalone HTML (no scripts, CSP), JSON and CSV. GUIDs, e-mail addresses, URL query strings and custom values can be redacted. Credentials are always removed.
- **Tests.** 109 unit tests on synthetic fixtures, including an end-to-end sample scenario.
