#!/usr/bin/env python3
"""Own a build-side DMG attachment; never convert a force-detached writable image."""
import argparse
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import time


class ImageError(RuntimeError):
    pass


def run(arguments, timeout=30):
    # DiskImages helpers must not inherit a pipe held open by another process.
    with tempfile.TemporaryFile() as output, tempfile.TemporaryFile() as errors:
        try:
            result = subprocess.run(arguments, stdout=output, stderr=errors, timeout=timeout)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ImageError(f"{' '.join(arguments)}: {error}") from error
        output.seek(0)
        errors.seek(0)
        return subprocess.CompletedProcess(arguments, result.returncode, output.read(), errors.read())


def command_error(result):
    message = (result.stdout + result.stderr).decode(errors="replace").strip()
    return ImageError(f"{' '.join(result.args)} exited {result.returncode}: {message}")


def decode_plist(result):
    if result.returncode:
        raise command_error(result)
    try:
        return plistlib.loads(result.stdout)
    except (ValueError, plistlib.InvalidFileException) as error:
        raise ImageError("hdiutil returned an invalid attachment plist") from error


def device(image, mount):
    """Resolve current ownership before every detach, including partial attach failures."""
    info = decode_plist(run(["hdiutil", "info", "-plist"]))
    matches = [item for item in info["images"] if Path(item["image-path"]).resolve() == image]
    if not matches:
        if mount.is_mount():
            raise ImageError(f"Refusing to detach an unowned mount at {mount}")
        return None
    if len(matches) != 1:
        raise ImageError(f"Ambiguous attachments for {image}")
    entities = matches[0]["system-entities"]
    mounts = {Path(item["mount-point"]).resolve() for item in entities if "mount-point" in item}
    roots = {item["dev-entry"] for item in entities if re.fullmatch(r"/dev/disk[0-9]+", item.get("dev-entry", ""))}
    if mounts - {mount} or len(roots) != 1:
        raise ImageError(f"Unexpected device or mount identity for {image}: {entities}")
    return roots.pop()


def attach(image, mount, read_write=False):
    arguments = ["hdiutil", "attach", "-plist", "-nobrowse", "-noautoopen", "-mountpoint", str(mount)]
    arguments += ["-readwrite", "-noverify"] if read_write else ["-readonly"]
    result = run([*arguments, str(image)], timeout=120)
    info = decode_plist(result)
    entities = info["system-entities"]
    if not any(Path(item["mount-point"]).resolve() == mount for item in entities if "mount-point" in item):
        raise ImageError(f"hdiutil did not mount {image} at {mount}")
    current = device(image, mount)
    if current is None or not any(item.get("dev-entry") == current for item in entities):
        raise ImageError(f"Could not confirm attachment identity for {image}")
    print(f"Attached {image} as {current} at {mount}", flush=True)


def diagnose(mount):
    if not shutil.which("lsof"):
        return
    print(f"Open handles on {mount} (current-user visibility):", file=sys.stderr, flush=True)
    try:
        result = run(["lsof", "+f", "--", str(mount)], timeout=5)
        print((result.stdout + result.stderr).decode(errors="replace").strip() or "No visible open handles.", file=sys.stderr)
    except ImageError as error:
        print(f"Could not collect open handles: {error}", file=sys.stderr)


def detach(image, mount, discard=False):
    attempts = 1 if discard else 5
    for attempt in range(attempts):
        current = device(image, mount)
        if current is None:
            return
        arguments = ["hdiutil", "detach", current]
        if discard:
            print(f"Discarding failed staging attachment {current} ({image}); forcing detach.", file=sys.stderr, flush=True)
            arguments.append("-force")
        result = run(arguments)
        if result.returncode == 0:
            if device(image, mount) is not None:
                raise ImageError(f"hdiutil reported success but {image} is still attached")
            return
        print(f"Detach attempt {attempt + 1}/{attempts}: {command_error(result)}", file=sys.stderr, flush=True)
        if attempt == 0 or attempt == attempts - 1:
            diagnose(mount)
        if result.returncode != 16 or attempt == attempts - 1:
            raise command_error(result)
        delay = 2 ** (attempt + 1)
        print(f"Image is busy; retrying in {delay}s.", file=sys.stderr, flush=True)
        time.sleep(delay)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("attach", "detach"))
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--mount", type=Path, required=True)
    parser.add_argument("--read-write", action="store_true")
    parser.add_argument("--discard", action="store_true", help="Force detach for failed-build cleanup only")
    args = parser.parse_args()
    image, mount = args.image.resolve(), args.mount.resolve()
    try:
        if args.operation == "attach":
            if args.discard:
                parser.error("--discard is only valid for detach")
            attach(image, mount, args.read_write)
        else:
            if args.read_write:
                parser.error("--read-write is only valid for attach")
            detach(image, mount, args.discard)
    except (ImageError, KeyError, TypeError) as error:
        print(f"DMG {args.operation} failed for {image} at {mount}: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
