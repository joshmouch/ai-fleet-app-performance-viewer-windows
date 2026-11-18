"""On an OrbStack Mac: check a published web host against a MacCollector binary and a live guest."""
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright


def check(published, collector, guest):
    fixture = """
import json, os, select, sys, tempfile
from pathlib import Path
from contextlib import ExitStack
with tempfile.TemporaryDirectory(dir='/var/tmp') as d, ExitStack() as stack:
    paths = [Path(d)/name/'viewer-group-check.dat' for name in ('first','second')]
    for p in paths: p.parent.mkdir()
    files = [stack.enter_context(p.open('w+b', buffering=0)) for p in paths]
    print(json.dumps([os.getpid(), [str(p) for p in paths]]), flush=True)
    while not select.select([sys.stdin], [], [], 0.04)[0]:
        for f in files:
            os.pwrite(f.fileno(), bytes(4096), 0)
            os.pread(f.fileno(), 4096, 0)
"""
    with tempfile.TemporaryDirectory() as tmp, sync_playwright() as pw:
        trace = subprocess.run(["dotnet", collector, "--host", "--interval", "2", "--duration", "5"],
                               check=True, capture_output=True, text=True)
        frames = [json.loads(line) for line in trace.stdout.splitlines()]
        host = max(frames, key=lambda f: f["diskPaths"]["capturedBytes"])
        sample = host["diskPaths"]
        directional = {k: sum(row["totals"][k] for row in sample["files"]) for k in ("readBytes", "writeBytes")}
        assert sum(directional.values()) == sample["capturedBytes"] > 0
        expected = f'Host {sample["capturedBytes"] / sample["seconds"] / 1048576:,.2f} MiB/s'
        replay = Path(tmp) / "host.ndjson"
        replay.write_text(json.dumps(host) + "\n")
        orb = [shutil.which("orb"), "-m", guest, "-u", "root", "python3"]
        worker = subprocess.Popen(orb + ["-u", "-c", fixture], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, text=True)
        server = None
        try:
            pid, paths = json.loads(worker.stdout.readline())
            with socket.socket() as sock:
                sock.bind(("127.0.0.1", 0))
                port = sock.getsockname()[1]
            env = dict(os.environ, ViewerPort=str(port), Sources__0__Name="Mac capture",
                       Sources__0__ReplayFile=str(replay), Sources__1__Name=guest,
                       Sources__1__GuestOf=host["host"], Sources__1__Command=orb[0])
            for i, arg in enumerate(orb[1:] + ["-", "loop", "root"]):
                env[f"Sources__1__Arguments__{i}"] = arg
            server = subprocess.Popen(["dotnet", "PerformanceViewer.Web.dll"], cwd=published, env=env,
                                      stdout=subprocess.DEVNULL)
            url = f"http://127.0.0.1:{port}"
            def window(previous=None):
                deadline = time.monotonic() + 45
                state = None
                while time.monotonic() < deadline:
                    try:
                        with urlopen(url + "/api/windows", timeout=2) as response:
                            physical, state = json.load(response)
                    except OSError:
                        time.sleep(.25)
                        continue
                    assert physical["frame"]["diskPaths"]["capturedBytes"] == sample["capturedBytes"]
                    if state["selected"] and state["guestEnd"] != previous:
                        rows = {r["file"]["path"]: r["totals"] for r in state["guestDisk"]["files"]
                                if r["file"]["pid"] == pid and r["file"]["path"] in paths}
                        if len(rows) == 2:
                            for t in rows.values():
                                assert t["reads"] > 0 and t["writes"] > 0
                                assert t["readBytes"] == 4096*t["reads"] and t["writeBytes"] == 4096*t["writes"]
                            return state["guestEnd"], list(rows.values())
                    time.sleep(.25)
                raise AssertionError(state and state["status"])
            first, initial = window()
            browser = pw.chromium.launch(channel=os.environ.get("VIEWER_TEST_BROWSER_CHANNEL"), headless=True)
            page = browser.new_page()
            page.goto(url)
            page.get_by_role("button", name="Disk", exact=True).click()
            page.get_by_label("Group by").select_option("File")
            page.get_by_label("Sort by").select_option("Guest total")
            choice = page.get_by_role("checkbox", name=f'{host["host"]} / {guest}')
            expect(choice).to_be_checked()
            summary = page.locator(".summary")
            expect(summary).to_contain_text(expected)
            page.get_by_label("Filter").fill(Path(paths[0]).name)
            data = page.locator("tbody tr")
            expect(data).to_have_count(2)
            for path in paths:
                row = data.filter(has_text=str(Path(path).parent) + "/")
                expect(row.locator("td").nth(1)).to_have_text(str(pid))
                expect(row.locator("td").nth(8)).to_have_text("\u2014")
                assert float(row.locator("td").nth(13).inner_text()) > 0
            page.get_by_label("Filter").fill("")
            choice.uncheck()
            expect(summary).to_contain_text(expected)
            expect(summary).not_to_contain_text("Guest files")
            choice.check()
            expect(summary).to_contain_text(expected)
            page.get_by_label("Group by").select_option("Process")
            page.get_by_label("Show").select_option("100")
            page.get_by_label("Filter").fill("python")
            expect(data.filter(has=page.get_by_text(str(pid), exact=True))).to_have_count(1)
            second, updated = window(first)
            page.get_by_role("button", name="CPU", exact=True).click()
            expect(page.get_by_role("heading", name="CPU processes")).to_be_visible()
            browser.close()
            return dict(guest=guest, host=host["host"], physical=directional, guestEnds=[first, second],
                        first=initial, second=updated, distinctFolders=2, processRanking=True,
                        physicalSummaryUnchanged=True)
        finally:
            worker.stdin.write("\n")
            worker.stdin.flush()
            worker.stdin.close()
            if server:
                server.terminate()
                server.wait(15)
            worker.wait(10)


if __name__ == "__main__":
    print(json.dumps(check(*sys.argv[1:])))
