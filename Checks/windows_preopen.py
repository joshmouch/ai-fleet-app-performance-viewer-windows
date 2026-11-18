"""Compare two viewer executables on a Windows machine using manifest disk fallback.

Run with the installed 1.0.14 collector and the candidate executable as positional
arguments. The test holds both files open until both collectors have finished.
"""
import ctypes as c
from ctypes import wintypes as wt
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
from contextlib import ExitStack

k = c.WinDLL("kernel32", use_last_error=True)
def api(name, ret, *args):
    fn = getattr(k, name)
    fn.argtypes = args
    fn.restype = ret
    return fn
create = api("CreateFileW", wt.HANDLE, wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.LPVOID, wt.DWORD, wt.DWORD, wt.HANDLE)
close = api("CloseHandle", wt.BOOL, wt.HANDLE)
seek = api("SetFilePointerEx", wt.BOOL, wt.HANDLE, c.c_longlong, wt.LPVOID, wt.DWORD)
size = api("GetFileSizeEx", wt.BOOL, wt.HANDLE, c.POINTER(c.c_longlong))
read = api("ReadFile", wt.BOOL, wt.HANDLE, wt.LPVOID, wt.DWORD, c.POINTER(wt.DWORD), wt.LPVOID)
write = api("WriteFile", wt.BOOL, wt.HANDLE, wt.LPVOID, wt.DWORD, c.POINTER(wt.DWORD), wt.LPVOID)
alloc = api("VirtualAlloc", wt.LPVOID, wt.LPVOID, c.c_size_t, wt.DWORD, wt.DWORD)
free = api("VirtualFree", wt.BOOL, wt.LPVOID, c.c_size_t, wt.DWORD)
def check(ok):
    if not ok: raise c.WinError(c.get_last_error())

def exercise(stable, candidate):
    block = 65536
    with tempfile.TemporaryDirectory() as folder, ExitStack() as cleanup:
        buf = alloc(None, block, 0x3000, 4)
        check(buf)
        cleanup.callback(free, buf, 0, 0x8000)
        def open_file(path, access, disposition, flags):
            handle = create(str(path), access, 7, None, disposition, flags, None)
            check(handle != c.c_void_p(-1).value)
            cleanup.callback(close, handle)
            return handle
        def io(handle, op, blocks):
            check(seek(handle, 0, None, 0))
            n = wt.DWORD()
            for _ in range(blocks):
                check(op(handle, buf, block, c.byref(n), None))
                assert n.value == block
        paths = [Path(folder)/f"viewer-held-{i}.dat" for i in range(2)]
        handles = [open_file(p, 0xc0000000, 1, 0xa0000080) for p in paths]
        counts = [24, 12]
        for h, count in zip(handles, counts): io(h, write, count)
        streams = []
        def read_lines(proc, q):
            try:
                for line in proc.stdout:
                    d = json.loads(line)["diskPaths"]
                    if d["status"] and not d["seconds"]: continue
                    t = ["readBytes", "writeBytes", "reads", "writes"]
                    rows = d["files"]
                    sums = {key:sum(r["totals"][key] for r in rows) for key in t}
                    assert sums["readBytes"]+sums["writeBytes"] == d["capturedBytes"]
                    assert not d["eventsLost"], d["eventsLost"]
                    own = [r for r in rows if r["file"]["pid"] == os.getpid()]
                    def collect(subset):
                        return [sum(r["totals"][key] for r in subset) for key in t]
                    q.put(dict(unknown=collect([r for r in own if not r["file"]["path"]]),
                        target=collect([r for r in own if r["file"]["path"].endswith(paths[0].name)]),
                        control=collect([r for r in own if r["file"]["path"].endswith(paths[1].name)]),
                        captured=sums, seconds=d["seconds"]))
            except Exception as e: q.put(e)
            finally: q.put(None)
        for exe in (stable, candidate):
            p = subprocess.Popen([exe, "--collect","--interval","2","--count","13"],
                                 stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding="utf-8")
            cleanup.callback(lambda proc=p: proc.wait(35))
            q = queue.Queue()
            threading.Thread(target=read_lines,args=(p,q),daemon=True).start()
            streams.append((p,q))
        def next_frame(q):
            v = q.get(timeout=45)
            if isinstance(v,Exception): raise v
            assert v is not None, "collector ended"
            return v
        for _,q in streams: next_frame(q)
        sessions=subprocess.check_output(["logman","query","-ets"],text=True,errors="replace")
        manifests=[]
        for p,_ in streams:
            logger=next(line.split()[0] for line in sessions.splitlines() if f"CpuTimelines-FileIo-{p.pid}-" in line)
            providers=subprocess.check_output(["logman","query",logger,"-ets"],text=True,errors="replace").lower()
            manifests.append("microsoft-windows-kernel-disk" in providers or "c7bde69a-e1e0-4177-b6ef-283ad1525271" in providers)
        assert all(manifests),manifests
        for h, count in zip(handles,counts):
            io(h, read, count)
            io(h, write, count)
        before = []
        for _,q in streams:
            f = next_frame(q)
            while f["unknown"][0]<sum(counts)*block or f["unknown"][1]<sum(counts)*block:
                f=next_frame(q)
            assert f["target"] == f["control"] == [0]*4
            before.append(f)
        alias = open_file(paths[0], 0x80, 3, 0x80)
        length = c.c_longlong()
        check(size(alias,c.byref(length)))
        assert length.value == block*counts[0]
        after = []
        for _,q in streams:
            last = next_frame(q)
            while True:
                item=q.get(timeout=45)
                if item is None: break
                if isinstance(item,Exception): raise item
                last=item
            after.append(last)
        result=dict(host=os.environ["COMPUTERNAME"],manifest=manifests,before=before,after=after,
                    bytesPerDirection=[n*block for n in counts])
        print(json.dumps(result),flush=True)
        assert after[0]["target"] == [0]*4
        assert after[1]["target"][0:2] == [counts[0]*block]*2
        assert all(v>0 for v in after[1]["target"][2:])
        assert after[1]["control"] == [0]*4
        assert [a+b for a,b in zip(after[1]["unknown"],after[1]["target"])] == after[0]["unknown"]
        assert all(v>=counts[1]*block for v in after[1]["unknown"][:2])

if __name__ == "__main__":
    exercise(*sys.argv[1:])
