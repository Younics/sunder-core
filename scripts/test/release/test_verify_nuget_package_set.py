#!/usr/bin/env python3

import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile


SCRIPT_PATH = Path(__file__).resolve().parents[2] / "release/verify-nuget-package-set.py"
SPEC = importlib.util.spec_from_file_location("verify_nuget_package_set", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class VerifyNuGetPackageSetTests(unittest.TestCase):
    version = "1.2.3"
    commit = "0123456789012345678901234567890123456789"

    def create_set(self, root: Path) -> None:
        for package_id in MODULE.PACKAGE_IDS:
            self.create_package(root, package_id, package_id, self.version)

    @staticmethod
    def create_package(
        root: Path,
        file_id: str,
        manifest_id: str,
        version: str,
        commit: str = "0123456789012345678901234567890123456789",
    ) -> None:
        nuspec = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{manifest_id}</id>
    <version>{version}</version>
    <authors>Younics</authors>
    <repository type="git" url="https://github.com/Younics/sunder-core" commit="{commit}" />
  </metadata>
</package>
"""
        path = root / f"{file_id}.{version}.nupkg"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(f"{file_id}.nuspec", nuspec)
            archive.writestr("LICENSE", "MIT fixture\n")
            archive.writestr("README.md", "Fixture\n")
            archive.writestr("[Content_Types].xml", "<Types />\n")

    def test_accepts_exact_identity_version_paths_and_contents(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_set(root)

            result = MODULE.verify_package_set(self.version, root, self.commit)

            self.assertEqual(len(MODULE.PACKAGE_IDS), len(result))
            self.assertEqual(set(MODULE.PACKAGE_IDS), {item["packageId"] for item in result})
            self.assertTrue(all(len(item["sha256"]) == 64 for item in result))

    def test_rejects_filename_that_does_not_match_exact_path(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_set(root)
            package = root / f"Sunder.Sdk.{self.version}.nupkg"
            nested = root / "nested"
            nested.mkdir()
            package.rename(nested / package.name)

            with self.assertRaisesRegex(ValueError, "artifact paths"):
                MODULE.verify_package_set(self.version, root, self.commit)

    def test_rejects_manifest_identity_hidden_behind_expected_filename(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_set(root)
            self.create_package(root, "Sunder.Sdk", "Other.Sdk", self.version)

            with self.assertRaisesRegex(ValueError, "identity"):
                MODULE.verify_package_set(self.version, root, self.commit)

    def test_rejects_manifest_version_hidden_behind_expected_filename(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_set(root)
            self.create_package(root, "Sunder.Sdk", "Sunder.Sdk", "9.9.9")
            wrong = root / "Sunder.Sdk.9.9.9.nupkg"
            wrong.rename(root / f"Sunder.Sdk.{self.version}.nupkg")

            with self.assertRaisesRegex(ValueError, "version"):
                MODULE.verify_package_set(self.version, root, self.commit)

    def test_rejects_repository_commit_hidden_behind_expected_filename(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_set(root)
            self.create_package(
                root,
                "Sunder.Sdk",
                "Sunder.Sdk",
                self.version,
                "f" * 40,
            )

            with self.assertRaisesRegex(ValueError, "repository identity"):
                MODULE.verify_package_set(self.version, root, self.commit)

    def test_rejects_xml_documentation_companion_but_allows_package_control_xml(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_set(root)
            package = root / f"Sunder.Sdk.{self.version}.nupkg"
            with zipfile.ZipFile(package, "a") as archive:
                archive.writestr("lib/net10.0/Sunder.Sdk.dll", b"assembly")
                archive.writestr("lib/net10.0/Sunder.Sdk.xml", b"documentation")

            with self.assertRaisesRegex(ValueError, "XML documentation companion"):
                MODULE.verify_package_set(self.version, root, self.commit)

    def test_rejects_numeric_prerelease_identifier_with_leading_zero(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, "strict SemVer"):
                MODULE.verify_package_set("1.2.3-01", Path(temporary), self.commit)

    def test_cli_rejects_symlink_artifact_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts = root / "artifacts"
            artifacts.mkdir()
            link = root / "linked-artifacts"
            os.symlink(artifacts, link, target_is_directory=True)

            result = subprocess.run(
                [sys.executable, SCRIPT_PATH, self.version, link, self.commit],
                check=False,
                capture_output=True,
                text=True,
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("real directory", result.stderr)


if __name__ == "__main__":
    unittest.main()
