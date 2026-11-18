# Optional macOS physical I/O collector

Standalone .NET 10 console project; no Windows collector or service changes. It references
`../Core/PerformanceWindow.cs` through the Core project and emits one finite JSONL window.

```
dotnet run --project MacCollector -- PID 4
dotnet run --project MacCollector -- --check
python3 MacCollector/live_check.py
```

Build first before the live check. Run as the observing account with noninteractive sudo
permission for `/usr/bin/fs_usage`. The collector invokes `sudo -n`: it never prompts or
changes authorization. Failure returns nonzero. Shell invocations use the environment's
bounded execution owner (`with-limits`). Capture output may contain private file paths;
keep real windows private. The committed parser fixtures are synthetic.

`fs_usage -w -f diskio -t SECONDS PID` is the physical observation source. Its command
suffix is a thread ID. `proc_pidinfo(PROC_PIDLISTTHREADIDS=28)` maps observed threads to the
selected PID; process start time fences PID reuse. Unresolved threads are keyless, not
guessed from a command name. A process exit/replacement discards attributed rows and
marks identity loss. Metadata and device-only records are keyless, not file data.
Preserve path text exactly: fs_usage may omit the leading slash or supply only a
basename. Never invent its directory. Same-named files can coalesce within one process.

The four file counters are physical data bytes and completion records in each direction,
not application read/write calls. Cache hits create no physical data record. APFS,
compression, readahead and delayed writeback prevent equality to logical bytes. Kernel
writeback and short-lived uncensused threads may remain unattributed. Device identifiers
are parsed but no integer Windows disk index is invented. Boot is unknown; CPU is
unmeasured (zero placeholder explicitly labeled in status). Each invocation is a new
capture, sequence 1; use actual elapsed seconds, not a rolling sum.

Coverage is always partial, even with zero loss notices. `capturedBytes = namedBytes +
keylessBytes` for parsed records. Unrecognized physical rows flag error. stderr or a
nonzero trace exit flags error. `eventsLost` counts loss notices, a lower bound;
the actual lost-event count is unknown, as status states. Empty-path PID-zero rows
carry unattributed physical counters (including metadata), so rows reconcile with
coverage and pass the existing file-window exporter. No private traces are fixtures.

The live check uses a temporary random-data file, verifies uncached reads, flushed writes,
warmed cache reads, process identity and coverage accounting, and prints only aggregates.
The platform-independent `--check` covers all four counters, spaces in paths, thread/PID
confusion, identity fencing, metadata, malformed rows, loss and JSON contract roundtrip.
