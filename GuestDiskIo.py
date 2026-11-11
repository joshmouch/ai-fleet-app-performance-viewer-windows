"""One bounded ext4 I/O sample on a Hyper-V attachment or WSL root disk."""

import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

SECONDS = 6
MAP_LIMIT = 8192
DEPTH = 24
MASK = (1 << 64) - 1
BPFTRACE_VERSION = "0.27.0"
BPFTRACE_ASSETS = {
    "x86_64": (195597080, "16194f713ba1fbff2dd76b7abffad4cd61f91e5663b116a5b03c907ae3e20cd8"),
    "aarch64": (196080712, "13879e0b6c6b3fa6384ad24859157b6ce352aac9d84f55eabadc68c2c6084274"),
}


def fail(message):
    print("Guest disk I/O: " + message, file=sys.stderr)
    sys.exit(1)


def read_json(args):
    return json.loads(subprocess.check_output(args, text=True, timeout=4))


def select_wsl_root():
    mount, = read_json(["findmnt", "-J", "-l", "-M", "/", "-o", "SOURCE,FSTYPE,FSROOT,MAJ:MIN"])["filesystems"]
    if mount["fstype"] != "ext4" or mount["fsroot"] != "/" or not mount["source"].startswith("/dev/"):
        fail("The WSL root filesystem could not be matched to an ext4 block device.")
    major, minor = map(int, mount["maj:min"].split(":"))
    return mount["source"], {(major << 20) | minor: "/"}


def wsl_bpftrace():
    # Older distro packages cannot read the BTF emitted by current WSL kernels.
    # Keep the pinned upstream tool in the root user's cache, outside the package database.
    arch = platform.machine()
    if arch not in BPFTRACE_ASSETS:
        fail(f"Automatic bpftrace setup is unavailable on {arch}.")
    size, expected_digest = BPFTRACE_ASSETS[arch]
    cache = Path.home() / ".cache" / "ai-fleet-performance-viewer"
    cache.mkdir(mode=0o700, parents=True, exist_ok=True)
    installation = cache / f"bpftrace-{BPFTRACE_VERSION}-{arch}"
    executable = installation / "AppRun"
    if os.access(executable, os.X_OK):
        return str(executable)

    url = f"https://github.com/bpftrace/bpftrace/releases/download/v{BPFTRACE_VERSION}/bpftrace-{arch}"
    try:
        with tempfile.TemporaryDirectory(dir=cache, prefix="download-") as staging:
            bundle = Path(staging) / "bpftrace"
            digest = hashlib.sha256()
            downloaded = 0
            with bundle.open("wb") as output, urllib.request.urlopen(url, timeout=15) as response:
                while block := response.read(1024 * 1024):
                    downloaded += len(block)
                    if downloaded > size:
                        fail("The bpftrace download exceeded its expected size.")
                    output.write(block)
                    digest.update(block)
            if downloaded != size or digest.hexdigest() != expected_digest:
                fail("The bpftrace download failed its SHA-256 check.")
            # Run the extracted AppImage on WSL, where FUSE mounting can be unavailable.
            bundle.chmod(0o700)
            subprocess.run([bundle, "--appimage-extract"], cwd=staging, check=True, timeout=40,
                           stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            image = Path(staging) / "squashfs-root"
            if not os.access(image / "AppRun", os.X_OK):
                fail("The extracted bpftrace package has no executable entry point.")
            try:
                image.rename(installation)
            except OSError:
                if not os.access(executable, os.X_OK):
                    raise
    except (OSError, subprocess.SubprocessError) as error:
        fail(f"Could not set up bpftrace in WSL ({type(error).__name__}).")
    return str(executable)


def select_disk(location):
    devices = read_json(["lsblk", "--tree", "-J", "-o", "PATH,HCTL,MAJ:MIN,TYPE,FSTYPE"])["blockdevices"]
    matches = []
    for disk in devices:
        address = (disk.get("hctl") or "").split(":")
        if disk["type"] != "disk" or len(address) != 4 or address[1:] != ["0", "0", str(location)]:
            continue
        driver = Path(f"/sys/class/scsi_host/host{address[0]}/proc_name")
        if driver.read_text().strip() == "storvsc_host":
            matches.append(disk)
    if len(matches) != 1:
        fail(f"Could not uniquely identify Hyper-V SCSI slot {location} in Linux.")
    disk = matches[0]
    filesystem_mounts = read_json(["findmnt", "-J", "-l", "-o", "MAJ:MIN,TARGET,FSROOT"])["filesystems"]
    roots = {}
    for device in [disk] + [d for d in disk.get("children", []) if d["type"] == "part"]:
        if device.get("fstype") != "ext4":
            continue
        number = device["maj:min"]
        major, minor = map(int, number.split(":"))
        # Linux dev_t in the kernel is not the userspace stat(2) encoding.
        dev = (major << 20) | minor
        targets = [m["target"] for m in filesystem_mounts if m["maj:min"] == number and m["fsroot"] == "/"]
        roots[dev] = min(targets, key=lambda path: (len(path), path)) if targets else None
    if not roots:
        fail(f"No ext4 filesystem found on {disk['path']}.")
    return disk["path"], roots


def bpf_program(devices):
    condition = " || ".join(f"$dev == {dev}" for dev in devices)
    probe = r"""
kretfunc:ext4_file_DIRECTION_iter
/retval > 0/
{
    $file = args.iocb->ki_filp;
    $dev = $file->f_inode->i_sb->s_dev;
    if (CONDITION) {
        $d = $file->f_path.dentry;
        $ino = $file->f_inode->i_ino;
        $leaf = (uint64)$d;
        @total_DIRECTION = stats(retval);
        @process_DIRECTION[pid] = stats(retval);
        @comm[pid] = ((struct task_struct *)curtask)->group_leader->comm;

        $tracked = @tracked_DIRECTION[pid, $leaf, $ino, $dev];
        if (!$tracked) {
            @tracked_DIRECTION[pid, $leaf, $ino, $dev] = 1;
            $tracked = @tracked_DIRECTION[pid, $leaf, $ino, $dev];
        }
        if ($tracked) {
            @DIRECTION[pid, $leaf, $ino, $dev] = sum(retval);
            @ops_DIRECTION[pid, $leaf, $ino, $dev] = count();
        } else {
            printf("io|DIRECTION|%d|%llu|%llu|%llu|%llu", pid, $leaf, $ino, (uint64)$dev, (uint64)retval);
        }

        $i = 0;
        while ((uint64)$d != 0 && $i < DEPTH_LIMIT) {
            $key = (uint64)$d;
            $slot = (($key >> 6) ^ $ino) % CACHE_SLOTS;
            $seen = @seen[$slot];
            if ($seen.0 == $key && $seen.1 == $ino) { break; }
            $up = $d->d_parent;
            $up_ino = $up->d_inode->i_ino;
            printf("name|%llu|%llu|%llu|%llu|%s", $key, $ino, (uint64)$up, $up_ino, str($d->d_name.name));
            @seen[$slot] = ($key, $ino);
            if ($up == $d) { break; }
            $ino = $up_ino;
            $d = $up;
            $i++;
        }
    }
}
"""
    directions = ("read", "write")
    probes = "".join(probe.replace("DIRECTION", direction).replace("CONDITION", condition)
                     .replace("DEPTH_LIMIT", str(DEPTH)).replace("CACHE_SLOTS", str(MAP_LIMIT))
                     for direction in directions)
    reported = ["comm"]
    cleared = ["seen"]
    for direction in directions:
        reported.extend([direction, f"ops_{direction}", f"total_{direction}", f"process_{direction}"])
        cleared.append(f"tracked_{direction}")
    actions = [f"print(@{name});" for name in reported]
    actions.extend(f"clear(@{name});" for name in reported + cleared)
    return probes + f"interval:s:{SECONDS} {{ exit(); }}\nEND {{ {' '.join(actions)} }}\n"


def report(output, device, roots, name_capacity):
    # File names are sent on cache misses. The cache can replace entries without
    # losing names already delivered to this sample or preventing new ones.
    maps = {}
    nodes = {}
    overflow = {"read": {}, "write": {}}
    for line in output.splitlines():
        if line:
            event = json.loads(line)
            if event["type"] in ("map", "stats"):
                # Newer bpftrace can print the empty maps again after END clears them.
                maps.update((key, value) for key, value in event["data"].items() if value)
            if event["type"] == "printf":
                kind, _, data = event["data"].partition("|")
                if kind == "name":
                    pointer, inode, parent, parent_inode, name = data.split("|", 4)
                    nodes[int(pointer) & MASK, int(inode) & MASK] = (
                        int(parent) & MASK, int(parent_inode) & MASK, name)
                elif kind == "io":
                    direction, pid, pointer, inode, dev, size = data.split("|")
                    key = int(pid), int(pointer) & MASK, int(inode) & MASK, int(dev)
                    stats = overflow[direction].setdefault(key, [0, 0])
                    stats[0] += int(size)
                    stats[1] += 1
    processes = maps.get("@comm", {})

    def guest_path(pointer, inode, dev):
        key = pointer & MASK, inode & MASK
        parts = []
        root = roots[dev]
        for _ in range(DEPTH):
            node = nodes.get(key)
            if node is None:
                break
            parent, parent_inode, name = node
            if parent == key[0]:
                return (root or "(filesystem root)").rstrip("/") + "/" + "/".join(reversed(parts))
            if name:
                parts.append(name + ("…" if len(name.encode("utf-8")) >= name_capacity - 1 else ""))
            key = parent, parent_inode
        return "…/" + "/".join(reversed(parts)) if parts else "(path unavailable)"

    rows = {}
    for direction, count, size in (("read", "Reads", "ReadBytes"), ("write", "Writes", "WriteBytes")):
        accounted = {}

        def add(pid, path, byte_count, op_count):
            row = rows.setdefault((pid, path), {
                "Pid": pid, "Process": processes.get(str(pid), "Unknown"), "Path": path,
                "ReadBytes": 0, "WriteBytes": 0, "Reads": 0, "Writes": 0,
            })
            row[size] += byte_count
            row[count] += op_count
            totals = accounted.setdefault(pid, [0, 0])
            totals[0] += byte_count
            totals[1] += op_count

        operations = maps.get(f"@ops_{direction}", {})
        for key, byte_count in maps.get(f"@{direction}", {}).items():
            pid, pointer, inode, dev = map(int, key.split(","))
            add(pid, guest_path(pointer, inode, dev), byte_count, operations.get(key, 0))
        for (pid, pointer, inode, dev), stats in overflow[direction].items():
            add(pid, guest_path(pointer, inode, dev), *stats)

        # Reconcile detail against independent counters, even if bpftrace could
        # not deliver an overflow record or keep a per-file or per-process key.
        for key, stats in maps.get(f"@process_{direction}", {}).items():
            pid = int(key)
            used_bytes, used_ops = accounted.get(pid, (0, 0))
            missing_bytes = stats["total"] - used_bytes
            missing_ops = stats["count"] - used_ops
            if missing_bytes < 0 or missing_ops < 0:
                fail("The guest process counters did not reconcile.")
            if missing_bytes or missing_ops:
                add(pid, "(path unavailable)", missing_bytes, missing_ops)
        total = maps.get(f"@total_{direction}", {"total": 0, "count": 0})
        missing_bytes = total["total"] - sum(stats[0] for stats in accounted.values())
        missing_ops = total["count"] - sum(stats[1] for stats in accounted.values())
        if missing_bytes < 0 or missing_ops < 0:
            fail("The guest disk counters did not reconcile.")
        if missing_bytes or missing_ops:
            add(0, "(path unavailable)", missing_bytes, missing_ops)

    limited = any(path == "(path unavailable)" or path.startswith("…/") for _, path in rows)
    return {"Device": device, "Seconds": SECONDS, "Rows": list(rows.values()), "Limited": limited}


def block_snapshot(device):
    try:
        fields = (Path("/sys/class/block") / Path(device).name / "stat").read_text().split()
        # The block layer reports sectors in 512-byte units, independent of device sector size.
        return time.monotonic(), int(fields[2]) * 512, int(fields[6]) * 512
    except (OSError, ValueError, IndexError):
        return None


def main():
    target = sys.argv[1]
    wsl = target == "root"
    device, roots = select_wsl_root() if wsl else select_disk(int(target))
    tracer = wsl_bpftrace() if wsl else shutil.which("bpftrace")
    if tracer is None:
        fail("bpftrace is unavailable in this guest.")
    # The newer compiler cannot load this probe with the larger stack buffer.
    name_capacity = 128 if wsl else 192
    options = dict(os.environ, BPFTRACE_MAX_STRLEN=str(name_capacity), BPFTRACE_MAX_MAP_KEYS=str(MAP_LIMIT),
                   BPFTRACE_PERF_RB_PAGES="128")
    command = [tracer, "--no-warnings"] if wsl else [tracer]
    before = block_snapshot(device)
    result = subprocess.run(command + ["-q", "-f", "json", "-"], input=bpf_program(roots),
                            text=True, encoding="utf-8", errors="replace", capture_output=True,
                            timeout=17, env=options)
    after = block_snapshot(device)
    if result.returncode != 0:
        lines = result.stderr.strip().splitlines()
        fail("Could not attach the ext4 probes." + (" " + lines[0][:140] if lines else ""))
    sample = report(result.stdout, device, roots, name_capacity)
    if before and after and after[0] > before[0] and after[1] >= before[1] and after[2] >= before[2]:
        sample.update(DiskSeconds=after[0] - before[0], DiskReadBytes=after[1] - before[1],
                      DiskWriteBytes=after[2] - before[2])
    print(json.dumps(sample, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except subprocess.TimeoutExpired:
        fail("The guest sampler did not finish.")
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        fail(f"The guest disk or sampler was unavailable ({type(error).__name__}).")
