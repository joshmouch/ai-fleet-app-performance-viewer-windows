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

def stop_collector(proc):
    if proc.poll() is None:
        proc.kill()
    proc.wait(timeout=5)

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
        directories = [Path(folder)/name for name in ("first", "second")]
        for d in directories: d.mkdir()
        paths = [d/"viewer-held.dat" for d in directories]
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
                        target=collect([r for r in own if r["file"]["path"].endswith(str(paths[0].relative_to(folder)))]),
                        control=collect([r for r in own if r["file"]["path"].endswith(str(paths[1].relative_to(folder)))]),
                        captured=sums, seconds=d["seconds"]))
            except Exception as e: q.put(e)
            finally: q.put(None)
        for label, exe in (("baseline", stable), ("candidate", candidate)):
            p = subprocess.Popen([exe, "--collect","--interval","2","--count","8"],
                                 stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,encoding="utf-8")
            cleanup.callback(stop_collector, p)
            q = queue.Queue()
            threading.Thread(target=read_lines,args=(p,q),daemon=True).start()
            streams.append((label,p,q))
        def next_frame(label, q):
            try:
                v = q.get(timeout=12)
            except queue.Empty:
                raise AssertionError(f"{label} produced no disk frame within 12 seconds") from None
            if isinstance(v,Exception): raise v
            assert v is not None, f"{label} ended"
            return v
        for label,_,q in streams:
            next_frame(label, q)
            print(f"{label}: disk capture ready", file=sys.stderr, flush=True)
        for h, count in zip(handles,counts):
            io(h, read, count)
            io(h, write, count)
        print("controlled transfers complete", file=sys.stderr, flush=True)
        # Keep both original handles open. The workload never opens an alias.
        before = []
        for label,_,q in streams:
            f = next_frame(label, q)
            while any(sum(f[k][i] for k in ("target","control","unknown")) < sum(counts)*block for i in (0,1)):
                f=next_frame(label, q)
            print(f"{label}: captured controlled totals at {f['seconds']:.1f}s", file=sys.stderr, flush=True)
            before.append(f)
        after = []
        for label,proc,q in streams:
            last = next_frame(label, q)
            while True:
                item=q.get(timeout=12)
                if item is None: break
                if isinstance(item,Exception): raise item
                last=item
            assert proc.wait(timeout=5) == 0, f"{label} collector failed"
            after.append(last)
        result=dict(host=os.environ["COMPUTERNAME"],before=before,after=after,
                    bytesPerDirection=[n*block for n in counts])
        print(json.dumps(result),flush=True)
        assert after[0]["target"] == after[0]["control"] == [0]*4
        for key, count in zip(("target","control"), counts):
            assert after[1][key][:2] == [count*block]*2
            assert all(v>0 for v in after[1][key][2:])
        assert [sum(after[1][key][i] for key in ("unknown","target","control"))
                for i in range(4)] == after[0]["unknown"]

if __name__ == "__main__":
    exercise(*sys.argv[1:])
