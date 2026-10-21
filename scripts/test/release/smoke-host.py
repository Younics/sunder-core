#!/usr/bin/env python3
"""Launch a packaged Supervisor and its nested Runtime with isolated temporary state."""
import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("directory", type=Path)
args = parser.parse_args()
binary = args.directory.resolve() / ("Sunder.Host.Supervisor.exe" if os.name == "nt" else "Sunder.Host.Supervisor")
with tempfile.TemporaryDirectory(prefix="sunder-host-smoke-") as directory:
    root = Path(directory).resolve()
    token = secrets.token_hex(32)
    environment = dict(os.environ, SUNDER_HOST_STATE_ROOT=str(root / "host"), SUNDER_RUNTIME_STATE_ROOT=str(root / "runtime"),
                       SUNDER_RUNTIME_BEARER_TOKEN=token, SUNDER_RUNTIME_CONNECTION_FILE=str(root / "host/connection/host.json"))
    environment.pop("SUNDER_HOST_RUNTIME_PATH", None)
    with (root / "host.log").open("wb") as log:
        process = subprocess.Popen([str(binary), "--urls", "http://127.0.0.1:0"], cwd=binary.parent,
                                   env=environment, stdout=log, stderr=subprocess.STDOUT)
        try:
            connection = root / "host/connection/host.json"
            deadline = time.monotonic() + 90
            url = None
            def request(path, body=None, authenticated=True):
                headers = {"Authorization": "Bearer " + token} if authenticated else {}
                data = None if body is None else json.dumps(body).encode()
                if data is not None: headers["Content-Type"] = "application/json"
                with urlopen(Request(url.rstrip("/") + path, data=data, headers=headers), timeout=5) as response:
                    return json.load(response)
            while time.monotonic() < deadline:
                if process.poll() is not None: raise RuntimeError("Packaged Supervisor exited during startup")
                try:
                    url = json.loads(connection.read_bytes())["runtimeUrl"]
                    status = request("/api/host/v1/status")
                    if status["state"] == 2: break  # Ready
                except (OSError, ValueError, URLError):
                    pass
                time.sleep(0.2)
            else: raise RuntimeError("Nested packaged Runtime did not become ready")
            try:
                request("/api/host/v1/status", authenticated=False)
            except HTTPError as error:
                if error.code != 401: raise
            else: raise RuntimeError("Host accepted an unauthenticated management request")
            handshake = request("/api/host/handshake")
            if not handshake.get("hostId") or not status.get("runtimeInstanceId"):
                raise RuntimeError("Packaged Host did not establish both process identities")
            request("/api/host/v1/runtime/stop", dict(mutationId=str(uuid.uuid4()), expectedDeploymentGeneration=status["deploymentGeneration"]))
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if request("/api/host/v1/status")["state"] == 0: break  # Stopped
                time.sleep(0.2)
            else: raise RuntimeError("Packaged Runtime did not stop")
            print("Packaged Supervisor/Runtime startup, authentication and shutdown passed")
        except Exception:
            log.flush()
            print((root / "host.log").read_text(errors="replace")[-16000:])
            raise
        finally:
            process.terminate()
            try: process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
