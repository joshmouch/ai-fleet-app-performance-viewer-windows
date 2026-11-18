# Mac physical I/O focused receipt

On November 18, 2026, the published Blazor browser viewer on josh-mac received eight consecutive windows from one host capture. The process and file rankings identified both controlled workers. Each recorded 180,363,264 read bytes in 303 operations and 180,355,072 write bytes in 172 operations. A third worker and background activity ran concurrently. Unknown processes retained 267,976,704 read bytes and 318,338,357 write bytes. Checks reconciled file rows, preserved exited workers and verified that CPU capability was visible. The positional PID probe produced one window. A termination request closed the trace and delivered its final window in 0.529 seconds.

2026-11-17, josh-mac, .NET 10. Optional collector with the shared Core window contract.
All shell operations ran through finite `with-limits` bounds. Public evidence below
contains aggregates and synthetic fixture descriptions only, not user paths or traces.

| Check | Result |
| --- | --- |
| macOS build | Passed, zero warnings/errors |
| `--check` | Passed: four counters, thread != PID, identity fence, spaces, metadata, keyless, loss, malformed input, empty window, contract roundtrip |
| Controlled flushed uncached write | 16,777,216 write bytes, 16 writes; zero reads |
| Controlled uncached read | 16,777,216 read bytes, 16 reads; zero writes |
| Three warmed-cache reads | Zero physical bytes/operations |
| Live identity | Attributed rows use controlled worker PID, nonzero process start time |
| Live accounting | Captured bytes equal named plus keyless bytes; no reported loss/error |
| Existing exporter | `FileWindowExport.Encode` accepts keyless, loss-notice and identity-fenced windows |

Commands: `dotnet build MacCollector`,
`dotnet MacCollector/bin/Debug/net10.0/MacCollector.dll --check`,
`python3 MacCollector/live_check.py`. The last two were run on josh-mac.

Loss/error checks are injected parser cases, not a claim to have induced kernel loss.
Identity replacement is a synthetic fence check; live tests verify the actual stable
worker. No-loss output is not proof of complete capture. Coverage remains partial.

Diagnosis established two macOS boundaries: thread handles from
`PROC_PIDLISTTHREADS=6` differ from fs_usage kernel IDs; XNU's
`PROC_PIDLISTTHREADIDS=28` supplies matching IDs. fs_usage may omit a path's leading
slash. The collector preserves the rendered path; tests recognize the controlled
file's canonical spelling without changing public path semantics.

Review fixes retain valid filenames containing loss-diagnostic words, terminate/reap
the trace on exceptional exits, refresh thread census only for unknown suffixes,
and reconcile unattributed counters through empty-path PID-zero rows. The final
source passed the macOS build, focused checks and all three live cases again.
