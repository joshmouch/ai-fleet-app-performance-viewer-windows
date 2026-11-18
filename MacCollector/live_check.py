"""Controlled macOS test; prints aggregate evidence only, never file paths."""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

dll = str(Path(__file__).parent / "bin/Debug/net10.0/MacCollector.dll")
with tempfile.TemporaryDirectory(prefix="viewer-physical-check-") as root:
    path = Path(root) / "controlled.bin"
    payload = os.urandom(16 * 1024 * 1024)
    path.write_bytes(payload)
    results = {}
    for case in ("write", "read", "cache"):
        if case == "cache":
            with path.open("rb") as stream:
                stream.read()
        trace = subprocess.Popen(["dotnet", dll, str(os.getpid()), "4"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        time.sleep(1.2)
        if case == "write":
            with path.open("r+b", buffering=0) as stream:
                fcntl.fcntl(stream, 48, 1)  # F_NOCACHE
                stream.write(payload)
                os.fsync(stream.fileno())
        else:
            with path.open("rb", buffering=0) as stream:
                if case == "read":
                    fcntl.fcntl(stream, 48, 1)
                for _ in range(3 if case == "cache" else 1):
                    stream.seek(0)
                    while stream.read(1024 * 1024):
                        pass
        output, error = trace.communicate(timeout=12)
        assert trace.returncode == 0, "collector failed: " + error
        frame = json.loads(output)
        disk = frame["diskPaths"]
        assert "error=False" in disk["status"] and "identityLost=False" in disk["status"], disk["status"]
        rows = [r for r in disk["files"] if (Path(r["file"]["path"]).resolve() == path.resolve()
                or r["file"]["path"] in (path.name, str(path.resolve()).lstrip("/")))]
        totals = {k: sum(r["totals"][k] for r in rows) for k in ("readBytes", "writeBytes", "reads", "writes")}
        assert all(r["file"]["pid"] == os.getpid() for r in rows)
        assert frame["cpu"]["rows"][0]["started"] > 0
        assert disk["capturedBytes"] == disk["namedBytes"] + disk["keylessBytes"]
        if case == "write":
            assert totals["writeBytes"] > 0 and totals["writes"] > 0, {"totals": totals, "status": disk["status"], "captured": disk["capturedBytes"], "named": disk["namedBytes"], "keyless": disk["keylessBytes"], "rows": len(disk["files"])}
        elif case == "read":
            assert totals["readBytes"] > 0 and totals["reads"] > 0, totals
        else:
            assert totals["readBytes"] == 0 and totals["reads"] == 0, totals
        results[case] = totals
    print(json.dumps({"host": "josh-mac", "result": "PASS", "cases": results,
                      "coverage": "physical data operations, not logical calls; partial attribution"}))
