"""Run on a Linux guest with a Btrfs /var/tmp and a tmpfs /dev/shm."""
import json
import multiprocessing
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from contextlib import ExitStack


def check():
    sampler = Path(__file__).resolve().parents[1] / "GuestDiskIo.py"
    block = bytes(4096)
    ctx = multiprocessing.get_context("fork")
    with ExitStack() as cleanup:
        paths = []
        # The two files share a basename. A basename-only match cannot pass.
        parents = ["/var/tmp", "/var/tmp", "/dev/shm"]
        if len(sys.argv) == 2:
            other = sys.argv[1]
            def mount(path):
                result = subprocess.check_output(["findmnt", "-JT", path, "-o", "FSTYPE,UUID"], text=True)
                return json.loads(result)["filesystems"][0]
            assert mount(other) == mount("/") and os.stat(other).st_dev != os.stat("/").st_dev
            parents.append(other)
        for parent in parents:
            directory = Path(cleanup.enter_context(tempfile.TemporaryDirectory(dir=parent)))
            paths.append(directory / "guest-sample.dat")
        files = [cleanup.enter_context(path.open("w+b", buffering=0)) for path in paths]
        stop = ctx.Event()

        def io(targets):
            while not stop.is_set():
                for f in targets:
                    os.pwrite(f.fileno(), block, 0)
                    os.pread(f.fileno(), len(block), 0)
                stop.wait(0.04)

        guest = ctx.Process(target=io, args=(files[:2],))
        outside = ctx.Process(target=io, args=(files[2:],))
        guest.start()
        outside.start()
        try:
            result = subprocess.run([sys.executable, sampler, "root"], check=True, capture_output=True,
                                    text=True, timeout=40)
        except subprocess.CalledProcessError as error:
            raise RuntimeError(error.stderr.strip()) from error
        finally:
            stop.set()
            guest.join()
            outside.join()
        assert guest.exitcode == outside.exitcode == 0
        assert all(path.stat().st_size == len(block) for path in paths)
        sample = json.loads(result.stdout)
        rows = [row for row in sample["Rows"] if row["Pid"] == guest.pid]
        selected = []
        for path in paths[:2]:
            matches = [row for row in rows if row["Path"] == str(path)]
            assert len(matches) == 1, {
                "expected": str(path), "pid": guest.pid, "rows": len(sample["Rows"]),
                "workerRows": rows, "markerRows": [r for r in sample["Rows"] if path.name in r["Path"]],
            }
            row = matches[0]
            assert row["Reads"] > 0 and row["Writes"] > 0
            assert row["ReadBytes"] == row["Reads"] * len(block)
            assert row["WriteBytes"] == row["Writes"] * len(block)
            selected.append({key: row[key] for key in ("ReadBytes", "WriteBytes", "Reads", "Writes")})
        assert all(row["Pid"] != outside.pid for row in sample["Rows"])
        assert "DiskReadBytes" not in sample and "DiskWriteBytes" not in sample
        print(json.dumps({"guest": os.uname().nodename, "controlled": selected, "rows": len(sample["Rows"]),
                          "tmpfsExcluded": True, "otherSubvolumeTested": len(parents) == 4,
                          "sharedBlockDeviceExcluded": True}))


if __name__ == "__main__":
    check()
