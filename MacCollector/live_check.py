"""Run on macOS after building MacCollector and Web. Never print real paths/windows."""
import fcntl
import json
import os
import signal
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

base = Path(__file__).resolve().parent.parent
if len(sys.argv) > 1 and sys.argv[1] == "--worker":
    with tempfile.NamedTemporaryFile(prefix="viewer-io-", suffix=".bin") as stream:
        payload = os.urandom(4 * 1024 * 1024)
        fcntl.fcntl(stream, 48, 1)  # F_NOCACHE
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
        print("ready", flush=True)
        sys.stdin.readline()
        end = time.monotonic() + 7
        while time.monotonic() < end:
            stream.seek(0)
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
            stream.seek(0)
            assert stream.read(len(payload)) == payload
            time.sleep(.15)
    sys.exit(0)

# Two known workers plus an independent background worker, all started before the census.
workers = []
viewer = None
browser = None
playwright = None
try:
    from playwright.sync_api import sync_playwright
    playwright = sync_playwright().start()
    browser = playwright.chromium.launch(headless=True, channel=os.environ.get("VIEWER_TEST_BROWSER_CHANNEL"))
    page = browser.new_page()
    for _ in range(3):
        p = subprocess.Popen([sys.executable, __file__, "--worker"], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        workers.append(p)
        assert p.stdout.readline().strip() == "ready"
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = os.environ.copy()
    env.update({"ViewerPort": str(port), "Sources__0__Name": "Mac physical host",
                "Sources__0__Command": "dotnet",
                "Sources__0__Arguments__0": str(base / "MacCollector/bin/Debug/net10.0/MacCollector.dll"),
                "Sources__0__Arguments__1": "--host",
                "Sources__0__Arguments__2": "--interval", "Sources__0__Arguments__3": "2",
                "Sources__0__Arguments__4": "--duration", "Sources__0__Arguments__5": "14"})
    published = base / "Web/bin/Release/net10.0/publish"
    viewer = subprocess.Popen(["dotnet", "PerformanceViewer.Web.dll"], cwd=published,
                              env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    url = "http://127.0.0.1:" + str(port)
    frames = {}
    deadline = time.monotonic() + 23
    started = False
    rendered = False
    page_loaded = False
    file_ranking = process_ranking = False
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url + "/api/windows", timeout=2) as response:
                sources = json.load(response)
            if not page_loaded:
                page.goto(url, wait_until="domcontentloaded", timeout=5000)
                page.get_by_role("button", name="CPU", exact=True).wait_for(timeout=10000)
                page_loaded = True
            if sources and sources[0].get("frame"):
                source = sources[0]
                frame = source["frame"]
                frames[frame["sequence"]] = frame
                if not started:
                    for p in workers:
                        p.stdin.write("go\n")
                        p.stdin.flush()
                    started = True
                if source["selected"] and not rendered:
                    rendered = page.get_by_text("Unsupported: this collector does not measure CPU", exact=False).count() > 0
                    if rendered:
                        page.get_by_role("button", name="Disk", exact=True).click()
                if rendered and source["selected"] and not (file_ranking and process_ranking):
                    page.get_by_label("Group by", exact=True).select_option(label="Process")
                    process_ranking |= all(page.locator("tbody tr").filter(has=page.get_by_text(str(p.pid), exact=True)).count() > 0 for p in workers[:2])
                    page.get_by_label("Group by", exact=True).select_option(label="File")
                    page.get_by_label("Filter", exact=True).fill("viewer-io-")
                    file_ranking |= all(page.locator("tbody tr").filter(has=page.get_by_text(str(p.pid), exact=True)).count() > 0 for p in workers[:2])
                    page.get_by_label("Filter", exact=True).fill("")
                if "final=True" in frame["diskPaths"]["status"]:
                    break
        except (OSError, ValueError):
            pass
        time.sleep(.15)
    for p in workers:
        assert p.wait(timeout=3) == 0
    assert started and rendered and file_ranking and process_ranking and len(frames) >= 4, {
        "started": started, "cpuCapability": rendered, "fileRanking": file_ranking,
        "processRanking": process_ranking, "windows": len(frames),
        "lastStatus": sources[0].get("status") if sources else None,
        "lastDiskStatus": frame["diskPaths"]["status"] if frames else None}
    ordered = sorted(frames.values(), key=lambda f: f["sequence"])
    assert len({f["capture"] for f in ordered}) == 1, "capture restarted between windows"
    assert [f["sequence"] for f in ordered] == list(range(1, len(ordered) + 1)), "sequence gap"
    totals = {p.pid: {k: 0 for k in ("readBytes", "writeBytes", "reads", "writes")} for p in workers}
    unknown = {k: 0 for k in ("readBytes", "writeBytes", "reads", "writes")}
    background = 0
    named_workers = set()
    for f in ordered:
        d = f["diskPaths"]
        assert d["scope"] == "host" and not f["cpu"]["rows"] and f["cpuStatus"].startswith("Unsupported")
        assert "error=False" in d["status"] and "malformed=0" in d["status"], d["status"]
        assert sum(r["totals"]["readBytes"] + r["totals"]["writeBytes"] for r in d["files"]) == d["capturedBytes"]
        assert d["capturedBytes"] == d["namedBytes"] + d["keylessBytes"]
        for row in d["files"]:
            row_pid, t = row["file"]["pid"], row["totals"]
            if row_pid in totals:
                if row["file"]["path"]:
                    named_workers.add(row_pid)
                for k in t:
                    totals[row_pid][k] += t[k]
            elif row_pid != 0:
                background += t["readBytes"] + t["writeBytes"]
            if row_pid == 0:
                for k in t:
                    unknown[k] += t[k]
    for t in totals.values():
        assert all(v > 0 for v in t.values()), "missing worker directional counters"
    assert named_workers == set(totals), "controlled file rankings missing"
    assert background > 0 or unknown["readBytes"] + unknown["writeBytes"] > 0, "no background/unknown coverage"
    assert "final=True" in ordered[-1]["diskPaths"]["status"], "stream did not close cleanly"
    viewer.terminate()
    viewer.wait()
    collector = ["dotnet", str(base / "MacCollector/bin/Debug/net10.0/MacCollector.dll")]
    legacy = subprocess.run(collector + [str(os.getpid()), "2"], capture_output=True, check=True)
    assert len(legacy.stdout.splitlines()) == 1
    assert json.loads(legacy.stdout)["diskPaths"]["scope"] == "process:" + str(os.getpid())
    with subprocess.Popen(collector + ["--pid", str(os.getpid()), "--interval", "1", "--duration", "15"],
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE) as trace:
        first = json.loads(trace.stdout.readline())
        before_stop = time.monotonic()
        trace.send_signal(signal.SIGTERM)
        remaining, _ = trace.communicate()
        stop_seconds = time.monotonic() - before_stop
        last = json.loads(remaining.splitlines()[-1])
        assert trace.returncode == 0 and last["capture"] == first["capture"] and "final=True" in last["diskPaths"]["status"]
        assert stop_seconds < 8, f"Trace took {stop_seconds:.1f}s to stop"
    print(json.dumps({"host": "josh-mac", "result": "PASS", "captureCount": 1,
                      "consecutiveWindows": len(ordered), "knownWorkers": list(totals.values())[:2],
                      "backgroundWorker": list(totals.values())[2], "otherBackgroundBytes": background,
                      "unknown": unknown, "viewerApi": True, "viewerCpuCapability": rendered,
                      "viewerFileRanking": file_ranking, "viewerProcessRanking": process_ranking,
                      "processExitPreserved": True, "legacyWindows": 1, "terminationSeconds": round(stop_seconds, 3)}))
finally:
    if browser:
        browser.close()
    if playwright:
        playwright.stop()
    for p in workers:
        if p.poll() is None:
            p.kill()
            p.wait()
    if viewer and viewer.poll() is None:
        viewer.terminate()
        try:
            viewer.wait(timeout=5)
        except subprocess.TimeoutExpired:
            viewer.kill()
            viewer.wait()
