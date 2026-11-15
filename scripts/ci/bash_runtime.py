#!/usr/bin/env python3
"""Launch native Bash, never the Windows WSL launcher, with an explicit argv."""
import os
from pathlib import Path
import shutil
import subprocess
import sys

VARIABLE = "SUNDER_BASH_EXECUTABLE"


def executable(environment=None, *, windows=None):
    environment = os.environ if environment is None else environment
    windows = sys.platform == "win32" if windows is None else windows
    lookup = {key.upper(): value for key, value in environment.items()} if windows else environment
    configured = lookup.get(VARIABLE)
    if configured is not None:
        candidates = [Path(configured)]
    elif windows:
        # CreateProcess can resolve System32's WSL shim before an unqualified PATH
        # entry. Locate native Bash through Git, then validate the actual runtime.
        git = shutil.which("git", path=lookup.get("PATH", ""))
        roots = list(Path(git).resolve().parents)[:3] if git else []
        roots += [Path(lookup[key]) / "Git" for key in ("PROGRAMFILES", "PROGRAMFILES(X86)") if lookup.get(key)]
        candidates = [root / relative for root in roots for relative in ("bin/bash.exe", "usr/bin/bash.exe")]
    else:
        found = shutil.which("bash", path=environment.get("PATH", ""))
        candidates = [Path(found)] if found else []
    for path in candidates:
        if path.is_absolute() and path.is_file():
            value = str(path.resolve())
            result = subprocess.run([value, "--noprofile", "--norc", "-c", 'printf "%s\\n" "$BASH_VERSION" "$OSTYPE"'],
                                    env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15)
            lines = result.stdout.splitlines()
            if result.returncode or len(lines) != 2 or not lines[0] or (windows and lines[1] not in ("msys", "cygwin")):
                raise ValueError(f"Expected {'native Windows Bash (msys/cygwin)' if windows else 'Bash'} at {value}; "
                                 f"exit={result.returncode}, stdout={result.stdout!r}, stderr={result.stderr!r}")
            return value
    raise ValueError(f"Native Bash unavailable; set {VARIABLE} to an absolute {'Git Bash' if windows else 'Bash'} executable path")


def command(*arguments):
    # Resolve in the child, so command construction and --list remain tool-free.
    return [sys.executable, "-B", str(Path(__file__).resolve()), "--run", *map(str, arguments)]


def inherited_environment():
    if sys.platform != "win32":
        return dict(os.environ)
    # Python uppercases os.environ keys on Windows; Bash's variables are case
    # sensitive. Copy the original Unicode environment block for this bridge.
    import ctypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    get = kernel.GetEnvironmentStringsW
    get.argtypes = []
    get.restype = ctypes.c_void_p
    free = kernel.FreeEnvironmentStringsW
    free.argtypes = [ctypes.c_void_p]
    free.restype = ctypes.c_int
    block = get()
    if not block:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        environment, offset = {}, 0
        while entry := ctypes.wstring_at(block + offset):
            separator = entry.find("=", 1)  # Preserve Windows' =C: drive entries too.
            if separator < 0:
                raise ValueError("Invalid Windows environment entry")
            environment[entry[:separator]] = entry[separator + 1:]
            offset += len(entry.encode("utf-16-le", errors="surrogatepass")) + 2
        return environment
    finally:
        free(block)


def main(arguments):
    if arguments and arguments[0] != "--run":
        raise ValueError("Usage: bash_runtime.py [--run <Bash arguments> ...]")
    environment = inherited_environment()
    path = executable(environment)
    if not arguments:
        print(path)
        return 0
    environment = {key: value for key, value in environment.items() if key.upper() != VARIABLE}
    environment[VARIABLE] = path
    return subprocess.run([path, "--noprofile", "--norc", *arguments[1:]], env=environment).returncode


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error))
