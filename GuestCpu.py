"""Sample CPU time for visible processes on a Linux VM."""

import json
import os
import time
from pathlib import Path


def snapshot():
    processes = {}
    for entry in Path("/proc").iterdir():
        if not entry.name.isdecimal():
            continue
        try:
            stat = (entry / "stat").read_text(errors="replace")
            name, _, rest = stat.partition(" (")[2].rpartition(")")
            fields = rest.split()
            processes[int(entry.name), int(fields[19])] = name, int(fields[11]) + int(fields[12])
        except (OSError, ValueError, IndexError):
            continue
    return processes


before = snapshot()
start = time.monotonic()
time.sleep(1)
after = snapshot()
seconds = time.monotonic() - start
ticks = os.sysconf("SC_CLK_TCK")
rows = []
for identity, (name, current) in after.items():
    previous = before.get(identity)
    if previous is not None and current > previous[1]:
        rows.append({"Pid": identity[0], "Process": name, "Cores": (current - previous[1]) / ticks / seconds})
rows.sort(key=lambda row: row["Cores"], reverse=True)
print(json.dumps({"LogicalProcessors": os.cpu_count() or 1, "Seconds": seconds, "Rows": rows[:50]}))
