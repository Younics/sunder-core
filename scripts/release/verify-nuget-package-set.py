#!/usr/bin/env python3

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import xml.etree.ElementTree as ET
import zipfile


PACKAGE_IDS = (
    "Sunder.Sdk",
    "Sunder.Sdk.Avalonia",
    "Sunder.Sdk.Stacks",
    "Sunder.Sdk.Worker",
    "Sunder.Package.Build",
    "Sunder.Package.Templates",
)
DOCUMENTED_SDK_IDS = frozenset(PACKAGE_IDS[:4])
PRERELEASE_IDENTIFIER = r"(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
SEMVER = re.compile(
    rf"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    rf"(?:-{PRERELEASE_IDENTIFIER}(?:\.{PRERELEASE_IDENTIFIER})*)?$"
)
GIT_COMMIT = re.compile(r"^[0-9a-f]{40}$")
REPOSITORY_URL = "https://github.com/Younics/sunder-core"


def fail(message: str) -> None:
    raise ValueError(message)


def child_text(parent: ET.Element, local_name: str) -> str:
    matches = [
        child
        for child in parent
        if child.tag.rsplit("}", 1)[-1] == local_name
    ]
    if len(matches) != 1 or matches[0].text is None or not matches[0].text.strip():
        fail(f"NuSpec metadata must contain exactly one nonempty <{local_name}> element.")
    return matches[0].text.strip()


def verify_archive(
    path: Path,
    package_id: str,
    version: str,
    expected_commit: str,
) -> dict[str, object]:
    if path.is_symlink() or not path.is_file():
        fail(f"NuGet artifact must be a regular file, not a link: {path}")

    try:
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if not entries:
                fail(f"NuGet artifact is empty: {path.name}")
            names = [entry.filename for entry in entries]
            if len(names) != len(set(names)):
                fail(f"NuGet artifact contains duplicate ZIP names: {path.name}")
            if len(names) != len({name.casefold() for name in names}):
                fail(f"NuGet artifact contains case-colliding ZIP names: {path.name}")
            for name in names:
                candidate = PurePosixPath(name)
                if (
                    not name
                    or "\\" in name
                    or candidate.is_absolute()
                    or ".." in candidate.parts
                ):
                    fail(f"NuGet artifact contains an unsafe ZIP path '{name}': {path.name}")
            folded_names = {name.casefold() for name in names}
            documentation_name = f"lib/net10.0/{package_id}.xml" if package_id in DOCUMENTED_SDK_IDS else None
            for name in names:
                candidate = PurePosixPath(name)
                if (
                    candidate.suffix.casefold() == ".xml"
                    and candidate.with_suffix(".dll").as_posix().casefold() in folded_names
                    and name != documentation_name
                ):
                    fail(
                        f"NuGet artifact contains an unexpected XML documentation companion '{name}': {path.name}"
                    )
            if documentation_name is not None:
                assembly_name = documentation_name[:-4] + ".dll"
                if documentation_name not in names or assembly_name not in names:
                    fail(f"SDK artifact must contain its DLL and XML API documentation: {path.name}")
                entry = archive.getinfo(documentation_name)
                if entry.file_size > 8 * 1024 * 1024:
                    fail(f"SDK XML API documentation exceeds its size bound: {path.name}")
                try:
                    documentation = archive.read(documentation_name).decode("utf-8-sig")
                except UnicodeDecodeError:
                    fail(f"SDK XML API documentation must be UTF-8: {path.name}")
                if "<!DOCTYPE" in documentation.upper() or "<!ENTITY" in documentation.upper():
                    fail(f"SDK XML API documentation must not declare entities: {path.name}")
                try:
                    document = ET.fromstring(documentation)
                except ET.ParseError as exception:
                    fail(f"SDK XML API documentation is invalid: {path.name}: {exception}")
                if document.tag != "doc" or document.findtext("assembly/name") != package_id:
                    fail(f"SDK XML API documentation assembly identity is unexpected: {path.name}")
                if not document.findall("members/member"):
                    fail(f"SDK XML API documentation contains no documented members: {path.name}")
            corrupt_entry = archive.testzip()
            if corrupt_entry is not None:
                fail(f"NuGet artifact has a corrupt ZIP entry '{corrupt_entry}': {path.name}")

            expected_nuspec = f"{package_id}.nuspec"
            nuspec_names = [name for name in names if name.lower().endswith(".nuspec")]
            if nuspec_names != [expected_nuspec]:
                fail(
                    f"NuGet artifact must contain exactly the root NuSpec "
                    f"'{expected_nuspec}', found {nuspec_names}: {path.name}"
                )
            for required_name in ("LICENSE", "README.md"):
                if names.count(required_name) != 1:
                    fail(f"NuGet artifact must contain root '{required_name}': {path.name}")

            try:
                root = ET.fromstring(archive.read(expected_nuspec))
            except ET.ParseError as exception:
                fail(f"NuGet artifact contains an invalid NuSpec: {path.name}: {exception}")
            metadata = [
                child
                for child in root
                if child.tag.rsplit("}", 1)[-1] == "metadata"
            ]
            if len(metadata) != 1:
                fail(f"NuSpec must contain exactly one <metadata> element: {path.name}")
            actual_id = child_text(metadata[0], "id")
            actual_version = child_text(metadata[0], "version")
            authors = child_text(metadata[0], "authors")
            if actual_id != package_id:
                fail(f"NuGet artifact identity is '{actual_id}', expected '{package_id}': {path.name}")
            if actual_version != version:
                fail(f"NuGet artifact version is '{actual_version}', expected '{version}': {path.name}")
            if authors != "Younics":
                fail(f"NuGet artifact authors are '{authors}', expected 'Younics': {path.name}")

            repositories = [
                child
                for child in metadata[0]
                if child.tag.rsplit("}", 1)[-1] == "repository"
            ]
            if len(repositories) != 1:
                fail(f"NuSpec must contain exactly one <repository> element: {path.name}")
            repository = repositories[0]
            if (
                repository.get("type") != "git"
                or repository.get("url") != REPOSITORY_URL
                or repository.get("commit") != expected_commit
            ):
                fail(f"NuGet artifact repository identity is unexpected: {path.name}")
    except zipfile.BadZipFile as exception:
        fail(f"NuGet artifact is not a valid ZIP archive: {path.name}: {exception}")

    contents = path.read_bytes()
    return {
        "path": path.name,
        "packageId": package_id,
        "version": version,
        "size": len(contents),
        "sha256": hashlib.sha256(contents).hexdigest(),
    }


def verify_package_set(
    version: str,
    directory: Path,
    expected_commit: str,
) -> list[dict[str, object]]:
    if SEMVER.fullmatch(version) is None:
        fail(f"Expected a strict SemVer package version, received '{version}'.")
    if GIT_COMMIT.fullmatch(expected_commit) is None:
        fail(f"Expected a lowercase 40-character repository commit, received '{expected_commit}'.")
    if directory.is_symlink() or not directory.is_dir():
        fail(f"NuGet artifact directory must be a real directory: {directory}")

    expected_paths = {f"{package_id}.{version}.nupkg" for package_id in PACKAGE_IDS}
    actual_paths: set[str] = set()
    for root, directories, files in os.walk(directory, followlinks=False):
        root_path = Path(root)
        for name in directories:
            candidate = root_path / name
            if candidate.is_symlink():
                fail(f"NuGet artifact directory contains a linked directory: {candidate}")
        for name in files:
            if not name.lower().endswith(".nupkg"):
                continue
            relative = (root_path / name).relative_to(directory).as_posix()
            actual_paths.add(relative)
    if actual_paths != expected_paths:
        missing = sorted(expected_paths - actual_paths)
        unexpected = sorted(actual_paths - expected_paths)
        fail(f"NuGet artifact paths do not match the coordinated set; missing={missing}, unexpected={unexpected}.")

    return [
        verify_archive(
            directory / f"{package_id}.{version}.nupkg",
            package_id,
            version,
            expected_commit,
        )
        for package_id in PACKAGE_IDS
    ]


def main() -> int:
    if len(sys.argv) != 4:
        print(
            "Usage: verify-nuget-package-set.py <version> <artifact-directory> <repository-commit>",
            file=sys.stderr,
        )
        return 2
    try:
        result = verify_package_set(sys.argv[1], Path(sys.argv[2]).absolute(), sys.argv[3])
    except (OSError, ValueError) as exception:
        print(str(exception), file=sys.stderr)
        return 1
    print(json.dumps({"packages": result}, separators=(",", ":"), sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
