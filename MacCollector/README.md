# Optional macOS host physical I/O stream

Build with .NET 10. This standalone console app uses the shared Core window contract.

~~~sh
dotnet build MacCollector
dotnet MacCollector/bin/Debug/net10.0/MacCollector.dll --host --interval 2
dotnet MacCollector/bin/Debug/net10.0/MacCollector.dll --host --interval 2 --duration 14
dotnet MacCollector/bin/Debug/net10.0/MacCollector.dll --pid 123 --duration 4
dotnet MacCollector/bin/Debug/net10.0/MacCollector.dll --check
~~~

Host mode (also the default) launches exactly one unfiltered
sudo -n fs_usage -w -f diskio capture. One capture GUID and increasing sequences identify
nonoverlapping, actual-elapsed windows. No PID or process-name filter is required.
No duration means stream until SIGINT/SIGTERM. The caller owns supervision.
Run shell operations through the environment's bounded execution owner. The collector
never prompts or changes sudo authorization. stdout contains private paths: keep it private.

Configure the existing Web Sources command as dotnet with Arguments pointing to the
collector DLL, --host, --interval, 2. The same shared file/process rankings consume it,
replacing each host's preceding window. Do not sum latest rolling Windows windows.
This collector's windows are disjoint intervals.

Process probe mode requires --pid and emits diskPaths.scope=process:PID. It is excluded
from the shared viewer's host aggregation and the exporter refuses it. Old positional
PID syntax is removed. Host is explicit diskPaths.scope=host; existing producers default
to that scope for wire compatibility.

## Ownership, accounting and coverage

fs_usage suffixes are kernel thread IDs, not PIDs. A periodic process census reads
proc_pidinfo(PROC_PIDLISTTHREADIDS=28); flavor 6 returns incompatible user handles.
Known kernel thread IDs retain their process identity for ten seconds after last census
visibility to cover delayed completions. Process exit never clears parsed rows.
Inaccessible, newly born/short-lived or expired thread identities remain PID-zero unknown
rows. No guessing from command names. Process names come from the census, not trace text.

Every parsed data/metadata completion contributes all four directional counters.
Unresolved ownership appears as PID zero. Data records retain a supplied filename even
when ownership is unavailable. Metadata/device-only records use empty paths and retain
read/write bytes and operations. Data records preserve fs_usage path text
(including omitted leading slash or basename-only ambiguity). Captured bytes reconcile
with all rows; named plus keyless equals captured. File rankings show physical data;
process/unknown totals can include metadata. Completions are not logical application calls.

Coverage is always partial: census permission failures, tracing loss, caches, APFS,
compression, writeback, and unidentified completions prevent a complete application-I/O
claim. Cache hits have no physical record. eventsLost is a count of loss notices, not a
precise event loss estimate. Malformed recognized physical rows flag error. stderr/nonzero
native exit flags error. Shutdown drains parsed pipe completions. No records are discarded
because a process exited. CPU rows are empty with an explicit unsupported CpuStatus,
rendered by the shared viewer; zero CPU utilization is not claimed.

## Focused acceptance

Publish Web with `dotnet publish Web -c Release`, then run python3 MacCollector/live_check.py on macOS. It starts the actual
shared Web viewer with one host collector; two known simultaneous readers/writers plus
an independent background worker exercise it. It verifies consecutive host windows,
constant capture identity, all four worker counters, background/unknown coverage,
row reconciliation, and the prerendered unsupported-CPU message. It prints aggregates
only and keeps real filenames in memory. Synthetic --check cases exercise loss/error,
PID-vs-thread confusion, accounting retention and process-scope export refusal.
PR 36 owns stored history; preserve the same host-scope admission when converting frames
to StoredFileWindow so process probes cannot enter host history.
