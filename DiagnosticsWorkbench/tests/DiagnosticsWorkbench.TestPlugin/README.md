# Scenario test plug-in (not shipped)

Generates real plug-in trace logs so every Diagnostics Workbench feature can be tested. It does nothing unless
an account's **Description** starts with `DWTEST:`.

## Register (Plugin Registration Tool)

1. Register `DiagnosticsWorkbench.TestPlugin.dll` (sandbox, database).
2. Step 1: **Update** of **account**, Post-operation, **Synchronous**, **no filtering attributes**.
   Add a **Pre-image** named `PreImage` with **all attributes**. (Both are deliberate: they trigger the tool's configuration flags.)
3. Step 2: **Update** of **account**, Post-operation, **Asynchronous**, filtering attribute `description`.

## Scenarios (set an account's Description to…)

| Description | What happens | Feature it exercises |
| --- | --- | --- |
| `DWTEST:fail` (run 3+ times) | Exception with a GUID, timestamp and random number | Fingerprint grouping (one group despite variable text) |
| `DWTEST:null` | NullReferenceException | Second error group |
| `DWTEST:slow` | Sleeps 3 s | Slow run (sync threshold 2 s) |
| `DWTEST:veryslow` | Sleeps 62 s | Near-timeout (60 s) |
| `DWTEST:recurse` | Updates itself to depth 3 | 3-step chain, possible-recursion flag |
| `DWTEST:chainfail` | Updates itself to depth 3, then fails | Failed chain |
| `DWTEST:asyncslow` | Async step sleeps 12 s | Async slow run (threshold 10 s) |

Unregister the assembly when you've finished testing.
