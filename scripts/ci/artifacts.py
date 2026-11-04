#!/usr/bin/env python3
"""Preview/remove ignored artifacts; pins protect pending release inputs."""
import argparse
import os
from pathlib import Path
import shutil
import stat
import subprocess
import time

PIN = ".sunder-keep"


def roots(workspace):
    return [workspace / "artifacts", *sorted((workspace / "repos").glob("*/artifacts"))]


def entries(path):
    yield path
    if path.is_dir() and not path.is_symlink():
        for directory, folders, files in os.walk(path, followlinks=False):
            for name in [*folders, *files]: yield Path(directory) / name


def validate_path(workspace, requested):
    path = Path(os.path.abspath(workspace / requested))
    if not any(path != root and path.is_relative_to(root) for root in roots(workspace)):
        raise ValueError("Select a child of an artifacts directory: " + str(path))
    if path.resolve() != path or path.is_symlink():
        raise ValueError("Artifact selection traverses a symbolic link: " + str(path))
    return path


def protected(path, workspace, active=()):
    for parent in [path, *path.parents]:
        if (parent / PIN).exists(): return "retention pin"
        if parent == workspace: break
    for entry in entries(path):
        if entry.name == PIN: return "retention pin inside selection"
    if any(str(path) in command for command in active): return "active process/worktree"
    return None


def active_references(workspace):
    commands = []
    if os.name != "nt":
        output = subprocess.check_output(["ps", "-axo", "pid=,command="], text=True)
        commands += [line.split(None, 1)[1] for line in output.splitlines() if len(line.split(None, 1)) == 2
                     and int(line.split(None, 1)[0]) not in (os.getpid(), os.getppid())]
    for artifact_root in roots(workspace):
        repository = artifact_root.parent
        if (repository / ".git").exists():
            commands.append(subprocess.check_output(["git", "worktree", "list", "--porcelain"], cwd=repository, text=True))
    return commands


def ignored_only(path):
    repository = next(parent for parent in path.parents if (parent / ".git").exists())
    relative = str(path.relative_to(repository))
    files = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", relative], cwd=repository)
    if files:
        raise ValueError("Selection contains tracked or unignored files: " + str(path))
    if subprocess.run(["git", "check-ignore", "-q", "--", relative], cwd=repository).returncode:
        raise ValueError("Selection is not ignored: " + str(path))


def remove(path):
    def retry(function, name, error):
        if not isinstance(error[1], PermissionError): raise error[1]
        for candidate in (Path(name).parent, Path(name)):
            info = candidate.lstat()
            if not stat.S_ISLNK(info.st_mode) and (not hasattr(os, "getuid") or info.st_uid == os.getuid()):
                mode = stat.S_IMODE(info.st_mode) | stat.S_IRUSR | stat.S_IWUSR
                if stat.S_ISDIR(info.st_mode): mode |= stat.S_IXUSR
                os.chmod(candidate, mode)
        function(name)
    if path.is_dir(): shutil.rmtree(path, onerror=retry)
    else: path.unlink()


def report_candidates(workspace, keep, days):
    cutoff = time.time() - days * 86400
    for root in roots(workspace):
        reports = root / "test-results"
        if not reports.is_dir() or reports.is_symlink(): continue
        children = sorted(reports.iterdir(), key=lambda path: path.lstat().st_mtime, reverse=True)
        for path in children[keep:]:
            if max(entry.lstat().st_mtime for entry in entries(path)) < cutoff: yield path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--path", action="append", default=[], help="Exact artifact child to remove; repeatable")
    parser.add_argument("--reports", action="store_true", help="Prune old test reports (also the default selection)")
    parser.add_argument("--keep", type=int, default=5)
    parser.add_argument("--days", type=int, default=14)
    parser.add_argument("--pin", action="append", default=[], help="Protect a retained directory until its .sunder-keep file is removed")
    parser.add_argument("--apply", action="store_true", help="Delete the previewed eligible selections")
    args = parser.parse_args()
    if args.keep < 0 or args.days < 0: parser.error("Retention must be nonnegative")
    workspace = args.root.resolve(strict=True)
    for requested in args.pin:
        path = validate_path(workspace, requested)
        if not path.is_dir(): raise ValueError("Pin requires an existing directory")
        (path / PIN).write_text("Retained input: remove this pin only when its qualification/release is complete.\n")
        print("PIN " + str(path))
    selected = [validate_path(workspace, path) for path in args.path]
    if args.reports or not (args.path or args.pin): selected += list(report_candidates(workspace, args.keep, args.days))
    active = active_references(workspace)
    total = 0
    for path in sorted(set(selected)):
        path = validate_path(workspace, path)
        if not path.exists(): continue
        reason = protected(path, workspace, active)
        if reason:
            print(f"KEEP {path}: {reason}")
            continue
        ignored_only(path)
        size = sum(getattr(info, "st_blocks", (info.st_size + 511) // 512) * 512 for info in (entry.lstat() for entry in entries(path)))
        print(f"{'REMOVE' if args.apply else 'WOULD REMOVE'} {size / 1024**2:.1f} MiB {path}", flush=True)
        total += size
        if args.apply: remove(path)
    print(f"{'Removed' if args.apply else 'Eligible'}: {total / 1024**3:.2f} GiB")


if __name__ == "__main__":
    main()
