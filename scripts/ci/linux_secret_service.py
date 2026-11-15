"""Private, disposable D-Bus/Secret Service for native Host qualification."""
from contextlib import ExitStack, contextmanager
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time


@contextmanager
def daemon(command, log, password=None):
    process = subprocess.Popen(command, env=dict(os.environ),
        stdin=subprocess.PIPE if password is not None else subprocess.DEVNULL,
        stdout=log, stderr=subprocess.STDOUT)
    try:
        if password is not None:
            try:
                process.stdin.write(password)
            finally:
                process.stdin.close()
        yield process
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        else:
            process.wait(timeout=5)


@contextmanager
def session(log_path):
    if sys.platform != "linux":
        raise ValueError("Private Secret Service qualification requires Linux")
    tools = {name: shutil.which(name) for name in ("dbus-daemon", "gnome-keyring-daemon", "secret-tool", "gdbus")}
    missing = [name for name, path in tools.items() if not path]
    if missing:
        raise ValueError("Missing required Linux Secret Service prerequisites: " + ", ".join(missing))
    previous = dict(os.environ)
    log_path = Path(log_path)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.TemporaryDirectory(prefix="sunder-keyring-", dir=os.environ.get("RUNNER_TEMP")) as temporary, \
                log_path.open("ab") as log, ExitStack() as services:
            root = Path(temporary)
            # Never connect to a desktop bus/control socket or read its keyrings.
            # Keep HOME/tool caches unchanged; only keyring/XDG state is disposable.
            for key in ("DBUS_SESSION_BUS_ADDRESS", "DBUS_STARTER_ADDRESS", "DBUS_STARTER_BUS_TYPE",
                        "GNOME_KEYRING_CONTROL", "GNOME_KEYRING_PID"):
                os.environ.pop(key, None)
            for key, leaf in (("XDG_DATA_HOME", "data"), ("XDG_CONFIG_HOME", "config"),
                              ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime")):
                path = root / leaf
                path.mkdir(mode=0o700)
                os.environ[key] = str(path)
            address = "unix:path=" + str(root / "bus")
            os.environ.update(DBUS_SESSION_BUS_ADDRESS=address, SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS="1")
            bus = services.enter_context(daemon([tools["dbus-daemon"], "--session", "--nofork", "--nopidfile",
                                                 "--address=" + address], log))
            deadline = time.monotonic() + 10
            while not (root / "bus").exists():
                if bus.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("Private D-Bus did not start; see " + str(log_path))
                time.sleep(0.05)
            subprocess.run([tools["gdbus"], "call", "--session", "--dest", "org.freedesktop.DBus",
                "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.GetId"],
                check=True, timeout=15, stdout=log, stderr=subprocess.STDOUT)
            control = root / "control"
            control.mkdir(mode=0o700)
            keyring = services.enter_context(daemon([tools["gnome-keyring-daemon"], "--foreground", "--unlock",
                "--components=secrets", "--control-directory=" + str(control)], log, password=b"sunder-ci-only"))
            # Do not auto-activate a fallback service. Require this owned daemon.
            subprocess.run([tools["gdbus"], "wait", "--session", "--timeout", "10", "org.freedesktop.secrets"],
                check=True, timeout=15, stdout=log, stderr=subprocess.STDOUT)
            if bus.poll() is not None or keyring.poll() is not None:
                raise RuntimeError("Private Secret Service exited before qualification; see " + str(log_path))
            yield
    finally:
        os.environ.clear()
        os.environ.update(previous)
