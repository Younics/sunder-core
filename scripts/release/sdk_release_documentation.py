#!/usr/bin/env python3
"""Bind documentation during the existing SDK pack, never rewrite tested archives."""
import argparse
from contextlib import contextmanager
import os
from pathlib import Path, PurePosixPath
import posixpath
import re
import subprocess
import sys
import tarfile
from urllib.parse import quote, unquote, urlsplit, urlunsplit
import xml.etree.ElementTree as ET
import zipfile

sys.dont_write_bytecode = True
import release_coordinates as c

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = "eng/test-oracles/core-public-packages.json"
MAX_TEXT = 8 * 1024 * 1024
URL = re.compile(r"(?:https?:)?//(?:github\.com/Younics/sunder-core/|raw\.githubusercontent\.com/Younics/sunder-core/)[^\s<>\"'`)]+", re.IGNORECASE)
MARKDOWN_TARGET = re.compile(r"(!?\[[^\]\n]*\]\(\s*<?)([^\s<>)]*)(>?[^)\n]*\))")
REFERENCE_TARGET = re.compile(r"^( {0,3}\[[^\]\n]+\]:\s*<?)([^\s<>]+)(>?[^\n]*)$", re.MULTILINE)
HTML_TARGET = re.compile(r"(\b(?:href|src)=[\"'])([^\"']+)([\"'])", re.IGNORECASE)


class Source:
    def __init__(self, root, commit):
        self.root, self.commit = Path(root), c.sha(commit, 40)
        c.require(commit != "0" * 40 and self.git("rev-parse", "HEAD").decode().strip() == commit,
                  "Documentation source must be the exact checked-out commit, not dirty current-source provenance")
        self.files = set(self.git("ls-tree", "-r", "--name-only", "-z", commit).decode().strip("\0").split("\0"))
        self.directories = {str(parent) for name in self.files for parent in PurePosixPath(name).parents}
        self.blobs = {}

    def git(self, *arguments):
        return c.command("git", *arguments, cwd=self.root)

    def clean(self):
        c.require(self.git("rev-parse", "HEAD").decode().strip() == self.commit
                  and not self.git("status", "--porcelain", "--untracked-files=all").strip(),
                  "Release documentation requires clean source matching the exact repository commit")

    def read(self, path):
        c.require(path in self.files, "Documentation input is absent from the selected commit: " + path)
        if path not in self.blobs:
            self.blobs[path] = self.git("show", f"{self.commit}:{path}")
        c.require(len(self.blobs[path]) <= MAX_TEXT, "Oversized documentation source: " + path)
        return self.blobs[path]

    def target(self, path, tree=False):
        c.require(path in self.files or (tree and path in self.directories),
                  "Documentation target is absent from the selected commit: " + path)


def pin_url(value, source, source_path=None):
    parsed = urlsplit(value)
    parts = unquote(parsed.path).strip("/").split("/")
    own_repository = [part.casefold() for part in parts[:2]] == c.REPOSITORY.lower().split("/")
    if parsed.hostname == "github.com" and own_repository and len(parts) >= 4 and parts[2].lower() in ("blob", "tree", "raw"):
        c.require(parsed.scheme == "https" and parsed.netloc == "github.com" and len(parts) >= 5
                  and parts[3] in ("main", source.commit), "Documentation must name the exact repository commit: " + value)
        source.target("/".join(parts[4:]), tree=parts[2].lower() == "tree")
        parts[:2] = c.REPOSITORY.split("/")
        parts[2] = parts[2].lower()
        parts[3] = source.commit
        return urlunsplit(parsed._replace(path="/" + "/".join(quote(part, safe="-._~") for part in parts)))
    if parsed.hostname == "raw.githubusercontent.com" and own_repository:
        c.require(parsed.scheme == "https" and parsed.netloc == "raw.githubusercontent.com" and len(parts) >= 4
                  and parts[2] in ("main", source.commit), "Documentation must name the exact repository commit: " + value)
        source.target("/".join(parts[3:]))
        parts[:2] = c.REPOSITORY.split("/")
        parts[2] = source.commit
        return urlunsplit(parsed._replace(path="/" + "/".join(quote(part, safe="-._~") for part in parts)))
    if source_path is not None and not parsed.scheme and not parsed.netloc and parsed.path:
        path = posixpath.normpath(unquote(parsed.path).lstrip("/") if parsed.path.startswith("/")
                                 else posixpath.join(str(PurePosixPath(source_path).parent), unquote(parsed.path)))
        source.target(path, tree=True)
        view = "blob" if path in source.files else "tree"
        return urlunsplit(("https", "github.com", f"/{c.REPOSITORY}/{view}/{source.commit}/{quote(path, safe='/-._~')}", parsed.query, parsed.fragment))
    return value


def absolute_links(text, source):
    def replace(match):
        value = match[0].rstrip(".,;:")
        return pin_url(value, source) + match[0][len(value):]
    return URL.sub(replace, text)


def render(data, source_path, source):
    text = absolute_links(data.decode("utf-8"), source)
    def replace(match):
        return match[1] + pin_url(match[2], source, source_path) + match[3]
    for pattern in (MARKDOWN_TARGET, REFERENCE_TARGET, HTML_TARGET):
        text = pattern.sub(replace, text)
    return text.encode("utf-8")


def packages(source):
    """Use the existing public inventory; do not create another package/pack list."""
    inventory = c.decode(source.read(INVENTORY))
    c.require(inventory["schemaVersion"] == 1, "Unexpected public package inventory")
    version = inventory["coordinatedVersion"]
    result = []
    for kind in ("nuget", "npm"):
        for item in inventory[kind]:
            project = PurePosixPath(item["project"])
            directory = project.parent if kind == "nuget" else project
            documents = {"README.md": str(directory / "README.md")}
            for entry in item["requiredEntries"]:
                if PurePosixPath(entry).suffix.lower() not in (".md", ".markdown"):
                    continue
                relative = "templates/" + entry[len("content/"):] if kind == "nuget" and entry.startswith("content/") else entry
                documents[entry] = str(directory / relative)
            name = item["packageId"] if kind == "nuget" else item["name"].lstrip("@").replace("/", "-")
            result.append(dict(kind=kind, item=item, documents=documents, directory=str(directory),
                               archive=f"{name}.{version}.nupkg" if kind == "nuget" else f"{name}-{version}.tgz"))
    return version, result


def manifest(source, package):
    value = c.decode(source.read(package["directory"] + "/package.json"))
    c.require(value["name"] == package["item"]["name"], "Unexpected npm source identity")
    value["homepage"] = pin_url(value["homepage"], source)
    c.require(value.get("gitHead") in (None, source.commit), "npm source names a different gitHead")
    value["gitHead"] = source.commit
    return value


@contextmanager
def pack_inputs(source):
    """A bounded pack-only overlay, restored byte-for-byte even on command failure.

    npm packs its directory's README/package.json; it has no alternate-readme option.
    Keep the existing pack commands/report authentic instead of patching tarballs or
    manufacturing replacement npm pack digests after tests have consumed them.
    """
    source.clean()
    _, inventory = packages(source)
    replacements = {}
    for package in inventory:
        for path in package["documents"].values():
            replacements[path] = render(source.read(path), path, source)
        if package["kind"] == "npm":
            replacements[package["directory"] + "/package.json"] = c.canonical(manifest(source, package)) + b"\n"
    # Check every source input before modifying any; Git status alone can miss an
    # assume-unchanged/skip-worktree input. Always derive from committed blobs.
    for path in replacements:
        c.require(c.read(source.root / path, MAX_TEXT) == source.read(path),
                  "Working documentation differs from the exact source commit: " + path)
    staged = []
    try:
        for path, data in replacements.items():
            if data != source.read(path):
                staged.append((path, data))
                (source.root / path).write_bytes(data)
        yield
    finally:
        conflicts = []
        for path, data in reversed(staged):
            target = source.root / path
            if target.is_symlink() or not target.is_file() or target.read_bytes() != data:
                conflicts.append(path)
            else:
                target.write_bytes(source.read(path))
        c.require(not conflicts, "Pack changed staged documentation; refusing to overwrite: " + ", ".join(conflicts))
    source.clean()


def pack(root, commit, command):
    # Ordinary CI/local candidate packaging keeps its existing mutable source docs.
    # Only the SDK tag's already-approved release path binds a source identity.
    ref = os.environ.get("GITHUB_REF", "")
    if not ref.startswith("refs/tags/sdk/"):
        subprocess.run(command, cwd=root, check=True)
        return
    c.tag(ref[len("refs/tags/"):], "sdk")
    c.require(os.environ.get("GITHUB_EVENT_NAME") == "push" and os.environ.get("GITHUB_SHA") == commit
              and os.environ.get("GITHUB_REPOSITORY") == c.REPOSITORY, "Source-bound documentation requires the exact SDK tag push")
    source = Source(root, commit)
    version, _ = packages(source)
    c.require(ref == "refs/tags/sdk/v" + version, "Documentation source/version differs from the SDK tag")
    with pack_inputs(source):
        subprocess.run(command, cwd=root, check=True)


def archive_texts(path, package):
    c.require(path.is_file() and not path.is_symlink() and path.stat().st_size <= package["item"]["maximumBytes"],
              "Missing/bounded regular SDK documentation artifact required: " + str(path))
    texts = {}
    def keep(name, size, reader):
        # API XML/JSDoc and template JSON can also acquire documentation URLs.
        # They are not rewritten; a mutable source link there blocks release.
        wanted = PurePosixPath(name).suffix.lower() in (".md", ".markdown", ".nuspec", ".xml", ".json") or name.endswith(".d.ts")
        if wanted:
            c.require(size <= MAX_TEXT, "Oversized artifact documentation: " + name)
            texts[name] = reader()
    if package["kind"] == "nuget":
        with zipfile.ZipFile(path) as archive:
            names = [entry.filename for entry in archive.infolist()]
            c.require(len(names) == len({name.casefold() for name in names}), "Ambiguous documentation archive entries")
            for entry in archive.infolist():
                keep(entry.filename, entry.file_size, lambda: archive.read(entry))
    else:
        with tarfile.open(path, "r:gz") as archive:
            names, total = set(), 0
            for entry in archive:
                total += entry.size
                c.require(entry.isfile() and entry.name.startswith("package/") and entry.name.casefold() not in names
                          and len(names) < 4096 and total <= 128 * 1024 * 1024, "Unsafe/oversized documentation tarball")
                names.add(entry.name.casefold())
                keep(entry.name[len("package/"):], entry.size, lambda: archive.extractfile(entry).read())
    return texts


def verify(root, directories, commit):
    source = Source(root, commit)
    _, inventory = packages(source)
    for directory in directories:
        c.require(Path(directory).is_dir() and not Path(directory).is_symlink(), "Documentation artifact directory must be real")
    for package in inventory:
        paths = [Path(directory) / package["archive"] for directory in directories]
        matches = [path for path in paths if path.exists()]
        c.require(len(matches) == 1, "Expected exactly one source-bound documentation artifact: " + package["archive"])
        texts = archive_texts(matches[0], package)
        documents = {name: data for name, data in texts.items() if PurePosixPath(name).suffix.lower() in (".md", ".markdown")}
        c.require(documents.keys() == package["documents"].keys(), "Artifact documentation inventory differs: " + package["archive"])
        for name, path in package["documents"].items():
            c.require(documents[name] == render(source.read(path), path, source),
                      "Artifact documentation is not bound to the exact repository commit (mutable /main/ links are forbidden): "
                      + package["archive"] + ":" + name)
        if package["kind"] == "npm":
            actual, expected = c.decode(texts["package.json"]), manifest(source, package)
            c.require(all(actual.get(key) == expected[key] for key in ("name", "version", "repository", "homepage", "gitHead")),
                      "npm documentation homepage/gitHead differs from the exact source commit: " + package["archive"])
        else:
            data = texts[package["item"]["packageId"] + ".nuspec"].decode("utf-8-sig")
            c.require("<!DOCTYPE" not in data.upper() and "<!ENTITY" not in data.upper(), "Unsupported NuSpec entities")
            repositories = [item for item in ET.fromstring(data).iter() if item.tag.rsplit("}", 1)[-1] == "repository"]
            c.require(len(repositories) == 1 and repositories[0].get("commit") == commit,
                      "NuGet documentation differs from RepositoryCommit: " + package["archive"])
        for name, data in texts.items():
            text = data.decode("utf-8")
            c.require(absolute_links(text, source) == text,
                      "Artifact text contains mutable documentation links: " + package["archive"] + ":" + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="action", required=True)
    pack_parser = subparsers.add_parser("pack")
    pack_parser.add_argument("commit")
    pack_parser.add_argument("command", nargs=argparse.REMAINDER)
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("commit")
    verify_parser.add_argument("directories", nargs="+", type=Path)
    args = parser.parse_args()
    if args.action == "pack":
        command = args.command[1:] if args.command[:1] == ["--"] else args.command
        c.require(command, "An existing pack command is required")
        pack(ROOT, args.commit, command)
    else:
        verify(ROOT, args.directories, args.commit)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError, zipfile.BadZipFile, tarfile.TarError, ET.ParseError) as error:
        raise SystemExit(str(error))
