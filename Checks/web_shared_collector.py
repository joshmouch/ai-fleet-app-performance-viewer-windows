"""Check that one loopback viewer port admits only one collector."""

from collections import deque
from contextlib import ExitStack
from http.client import HTTPConnection
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
from threading import Thread
import time


def probe_collector(observer_port, stop):
    with socket.socket(type=socket.SOCK_DGRAM) as observer:
        observer.sendto(str(os.getpid()).encode(), ("127.0.0.1", observer_port))
    deadline = time.monotonic() + 35
    while time.monotonic() < deadline and not stop.exists():
        time.sleep(0.05)


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def wait_for_viewer(port, process, log):
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline and process.poll() is None:
        connection = HTTPConnection("127.0.0.1", port, timeout=1)
        try:
            connection.request("GET", "/api/windows")
            response = connection.getresponse()
            response.read()
            if response.status == 200:
                return
        except OSError:
            pass
        finally:
            connection.close()
        time.sleep(0.1)
    raise AssertionError("".join(log.copy()))


def stop_process(process):
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=5)


def exercise(executable):
    with tempfile.TemporaryDirectory() as directory, ExitStack() as cleanup:
        root = Path(directory)
        observer = cleanup.enter_context(socket.socket(type=socket.SOCK_DGRAM))
        observer.bind(("127.0.0.1", 0))
        observer.settimeout(3)
        port = free_port()
        env = dict(os.environ, ViewerPort=str(port), Logging__EventLog__LogLevel__Default="None",
                   Sources__0__Name="Probe",
                   Sources__0__Command=sys.executable, Sources__0__Arguments__0=__file__,
                   Sources__0__Arguments__1="--probe-collector",
                   Sources__0__Arguments__2=str(observer.getsockname()[1]))

        def start(name):
            stop = root / f"{name}.stop"
            log = deque(maxlen=64)
            process = subprocess.Popen([str(executable)], cwd=executable.parent,
                                       env=dict(env, Sources__0__Arguments__3=str(stop)),
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       encoding="utf-8", errors="replace")

            def read_log():
                with process.stdout:
                    for line in process.stdout:
                        log.append(line)

            reader = Thread(target=read_log, daemon=True)
            reader.start()
            cleanup.callback(reader.join, 5)
            cleanup.callback(stop_process, process)
            cleanup.callback(stop.touch)
            return process, log, stop

        first, first_log, first_stop = start("owner")
        wait_for_viewer(port, first, first_log)
        first_collector = int(observer.recv(32))
        second, second_log, _ = start("contender")
        assert second.wait(timeout=12) != 0, "".join(second_log.copy())
        observer.settimeout(0.2)
        try:
            contender_collector = int(observer.recv(32))
        except TimeoutError:
            pass
        else:
            raise AssertionError(f"The contender started collector {contender_collector} while {first_collector} was running.")

        first_stop.touch()
        stop_process(first)
        third, third_log, _ = start("successor")
        wait_for_viewer(port, third, third_log)
        observer.settimeout(3)
        assert int(observer.recv(32)) != first_collector
    print("One collector owned the running port, the contender started none, and the successor resumed collection.")


if __name__ == "__main__":
    if sys.argv[1] == "--probe-collector":
        probe_collector(int(sys.argv[2]), Path(sys.argv[3]))
    else:
        exercise(Path(sys.argv[1]).resolve())
