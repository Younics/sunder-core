"""Source-bound documentation fixtures only: no builds, npm pack, Git writes or feeds."""
from contextlib import nullcontext
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import sdk_release_documentation as d

COMMIT = "a" * 40
URL = "https://github.com/Younics/sunder-core"
CSHARP = "languages/csharp/dotnet/packages/"
NODE = "languages/typescript/node/packages/"


class SdkReleaseDocumentationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="sdk-release-docs-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.head, self.status = COMMIT, b""
        inventory = dict(schemaVersion=1, coordinatedVersion="1.2.3", nuget=[
            dict(packageId="Sunder.Sdk", project=CSHARP + "Sunder.Sdk/Sunder.Sdk.csproj", maximumBytes=1024 * 1024, requiredEntries=[]),
            dict(packageId="Sunder.Package.Templates", project=CSHARP + "Sunder.Package.Templates/Sunder.Package.Templates.csproj",
                 maximumBytes=1024 * 1024, requiredEntries=["content/sample/README.md"]),
        ], npm=[
            dict(name="@sunder/sdk", project=NODE + "sdk", maximumBytes=1024 * 1024, requiredEntries=[]),
            dict(name="create-sunder-package", project=NODE + "create-sunder-package", maximumBytes=1024 * 1024,
                 requiredEntries=["template/README.md", "template-react-node/README.md"]),
        ])
        self.files = {d.INVENTORY: json.dumps(inventory).encode(), "docs/policy.md": b"# Policy\n"}
        readme = b"> UNFROZEN / UNRELEASED\n" + f"[Policy]({URL}/blob/main/docs/policy.md#policy)\n".encode()
        for item in inventory["nuget"]:
            directory = item["project"].rsplit("/", 1)[0]
            self.files[directory + "/README.md"] = readme
            for entry in item["requiredEntries"]:
                self.files[directory + "/templates/" + entry[len("content/"):]] = readme
        self.files[CSHARP + "Sunder.Sdk/README.md"] += b"[Relative](../../../../../docs/policy.md#policy)\n"
        for item in inventory["npm"]:
            directory = item["project"]
            self.files[directory + "/README.md"] = readme
            for entry in item["requiredEntries"]:
                self.files[directory + "/" + entry] = readme
            self.files[directory + "/package.json"] = json.dumps(dict(
                name=item["name"], version="1.2.3", repository=dict(type="git", url="git+" + URL + ".git", directory=directory),
                homepage=URL + "/tree/main/" + directory + "#readme",
            ), indent=2).encode() + b"\n"
        for path, data in self.files.items():
            target = self.root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        self.git_mock = patch.object(d.c, "command", side_effect=self.git)
        self.git_mock.start()
        self.addCleanup(self.git_mock.stop)
        self.source = d.Source(self.root, COMMIT)
        self.packages = d.packages(self.source)[1]

    def git(self, *arguments, **kwargs):
        self.assertEqual(self.root, kwargs["cwd"])
        if arguments == ("git", "rev-parse", "HEAD"):
            return (self.head + "\n").encode()
        if arguments == ("git", "status", "--porcelain", "--untracked-files=all"):
            return self.status
        if arguments == ("git", "ls-tree", "-r", "--name-only", "-z", COMMIT):
            return ("\0".join(self.files) + "\0").encode()
        if arguments[:2] == ("git", "show"):
            commit, path = arguments[2].split(":", 1)
            self.assertEqual(COMMIT, commit)
            return self.files[path]
        self.fail("Unexpected Git read: " + repr(arguments))

    def assert_restored(self):
        self.assertEqual(self.files, {path: (self.root / path).read_bytes() for path in self.files})

    def contents(self, package):
        contents = {name: (self.root / path).read_bytes() for name, path in package["documents"].items()}
        if package["kind"] == "npm":
            contents["package.json"] = (self.root / package["directory"] / "package.json").read_bytes()
        else:
            contents[package["item"]["packageId"] + ".nuspec"] = (
                f'<package><metadata><repository type="git" url="{URL}" commit="{COMMIT}" /></metadata></package>').encode()
        return contents

    def write_archive(self, package, contents):
        path = self.root / "artifacts" / package["archive"]
        path.parent.mkdir(exist_ok=True)
        if package["kind"] == "nuget":
            with zipfile.ZipFile(path, "w") as archive:
                for name, data in contents.items():
                    archive.writestr(name, data)
        else:
            with tarfile.open(path, "w:gz") as archive:
                for name, data in contents.items():
                    info = tarfile.TarInfo("package/" + name)
                    info.size = len(data)
                    archive.addfile(info, io.BytesIO(data))

    def create_archives(self):
        contents = {}
        with d.pack_inputs(self.source):
            for package in self.packages:
                contents[package["archive"]] = self.contents(package)
                self.write_archive(package, contents[package["archive"]])
        return contents

    def test_pins_absolute_relative_reference_html_and_raw_links_without_changing_status(self):
        source_path = CSHARP + "Sunder.Sdk/README.md"
        original = ("> UNFROZEN / UNRELEASED\n"
                    f"[absolute]({URL}/blob/main/docs/policy.md#policy)\n"
                    "[relative](../../../../../docs/policy.md#policy)\n"
                    "[reference]: <../../../../../docs/policy.md> \"Policy\"\n"
                    '<a href="../../../../../docs/policy.md#policy">policy</a>\n'
                    "https://raw.githubusercontent.com/younics/sunder-core/main/docs/policy.md\n"
                    "[anchor](#local) [external](https://example.invalid/main/docs)\n"
                    "External URL syntax example: http://localhost[:nondefault-port]/\n").encode()
        rendered = d.render(original, source_path, self.source).decode()
        self.assertEqual(4, rendered.count(URL + "/blob/" + COMMIT + "/docs/policy.md"))
        self.assertIn("https://raw.githubusercontent.com/Younics/sunder-core/" + COMMIT + "/docs/policy.md", rendered)
        self.assertIn("> UNFROZEN / UNRELEASED\n", rendered)
        self.assertIn("[anchor](#local) [external](https://example.invalid/main/docs)", rendered)
        self.assertIn("http://localhost[:nondefault-port]/", rendered)
        for target in (URL + "/blob/dev/docs/policy.md", URL + "/blob/" + "b" * 40 + "/docs/policy.md",
                       URL + "/blob/main/docs/uncommitted.md", "../../../../../../outside.md"):
            with self.subTest(target=target), self.assertRaises(ValueError):
                d.render(f"[bad]({target})".encode(), source_path, self.source)

    def test_pack_overlay_is_byte_restored_on_success_and_command_failure(self):
        for failing in (False, True):
            with self.subTest(failing=failing), self.assertRaisesRegex(RuntimeError, "pack failed") if failing else nullcontext():
                with d.pack_inputs(self.source):
                    for package in self.packages:
                        for path in package["documents"].values():
                            data = (self.root / path).read_bytes()
                            self.assertNotIn(b"/main/", data)
                            self.assertIn(b"UNFROZEN / UNRELEASED", data)
                        if package["kind"] == "npm":
                            value = json.loads((self.root / package["directory"] / "package.json").read_bytes())
                            self.assertEqual(COMMIT, value["gitHead"])
                            self.assertIn("/tree/" + COMMIT + "/", value["homepage"])
                    if failing:
                        raise RuntimeError("pack failed")
            self.assert_restored()

    def test_dirty_wrong_or_hidden_modified_source_is_rejected_before_staging(self):
        self.head = "b" * 40
        with self.assertRaisesRegex(ValueError, "exact checked-out commit"):
            d.Source(self.root, COMMIT)
        self.head, self.status = COMMIT, b" M docs/policy.md\n"
        with self.assertRaisesRegex(ValueError, "clean source"), d.pack_inputs(self.source):
            self.fail("Dirty source must not be staged")
        self.assert_restored()
        self.status = b""
        path = NODE + "sdk/README.md"
        (self.root / path).write_bytes(b"uncommitted input hidden from Git status\n")
        with self.assertRaisesRegex(ValueError, "differs from the exact source commit"), d.pack_inputs(self.source):
            self.fail("Hidden dirty input must not be staged")
        self.assertEqual(b"uncommitted input hidden from Git status\n", (self.root / path).read_bytes())
        self.assertEqual(self.files[CSHARP + "Sunder.Sdk/README.md"], (self.root / CSHARP / "Sunder.Sdk/README.md").read_bytes())

    def test_restoration_never_discards_unexpected_changes(self):
        path = NODE + "sdk/README.md"
        with self.assertRaisesRegex(ValueError, "refusing to overwrite"):
            with d.pack_inputs(self.source):
                (self.root / path).write_bytes(b"concurrent change\n")
        self.assertEqual(b"concurrent change\n", (self.root / path).read_bytes())
        for other in set(self.files) - {path}:
            self.assertEqual(self.files[other], (self.root / other).read_bytes())

    def test_local_and_non_sdk_ci_pack_keep_existing_development_behavior(self):
        for ref in ("", "refs/heads/main", "refs/tags/cli/v1.2.3"):
            with self.subTest(ref=ref), patch.dict(os.environ, GITHUB_REF=ref), patch.object(d, "Source") as source, \
                 patch.object(d.subprocess, "run") as run:
                d.pack(self.root, COMMIT, ["existing-pack-command"])
                source.assert_not_called()
                run.assert_called_once_with(["existing-pack-command"], cwd=self.root, check=True)
        self.assert_restored()

    def test_tag_pack_uses_exact_source_then_restores_inputs_when_command_fails(self):
        env = dict(GITHUB_REF="refs/tags/sdk/v1.2.3", GITHUB_EVENT_NAME="push", GITHUB_SHA=COMMIT,
                   GITHUB_REPOSITORY="Younics/sunder-core")
        def failed_command(*args, **kwargs):
            self.assertNotIn(b"/main/", (self.root / NODE / "sdk/README.md").read_bytes())
            raise subprocess.CalledProcessError(2, args[0])
        with patch.dict(os.environ, env), patch.object(d.subprocess, "run", side_effect=failed_command):
            with self.assertRaises(subprocess.CalledProcessError):
                d.pack(self.root, COMMIT, ["existing-pack-command"])
        self.assert_restored()
        for changed in (dict(GITHUB_SHA="b" * 40), dict(GITHUB_REF="refs/tags/sdk/v9.9.9"), dict(GITHUB_EVENT_NAME="workflow_dispatch")):
            with self.subTest(changed=changed), patch.dict(os.environ, dict(env, **changed)), patch.object(d.subprocess, "run") as run:
                with self.assertRaises(ValueError):
                    d.pack(self.root, COMMIT, ["existing-pack-command"])
                run.assert_not_called()

    def test_verifies_both_formats_and_nested_readmes_without_rewriting_tested_archives(self):
        self.create_archives()
        before = {path.name: path.read_bytes() for path in (self.root / "artifacts").iterdir()}
        d.verify(self.root, [self.root / "artifacts"], COMMIT)
        self.assertEqual(before, {path.name: path.read_bytes() for path in (self.root / "artifacts").iterdir()})
        self.assert_restored()

    def test_release_validation_rejects_main_wrong_commit_and_uncommitted_readme_bytes(self):
        for package in self.packages:
            for name, source_path in package["documents"].items():
                for mutation in ("main", "commit", "uncommitted"):
                    with self.subTest(package=package["archive"], name=name, mutation=mutation):
                        contents = self.create_archives()[package["archive"]]
                        contents[name] = (self.files[source_path] if mutation == "main" else
                                          contents[name].replace(COMMIT.encode(), b"b" * 40) if mutation == "commit" else
                                          contents[name] + b"new uncommitted wording\n")
                        self.write_archive(package, contents)
                        with self.assertRaisesRegex(ValueError, "not bound to the exact repository commit"):
                            d.verify(self.root, [self.root / "artifacts"], COMMIT)

    def test_release_validation_binds_npm_homepage_git_head_and_nuget_repository_commit(self):
        for package in self.packages:
            fields = ("homepage", "gitHead") if package["kind"] == "npm" else ("RepositoryCommit",)
            for field in fields:
                with self.subTest(package=package["archive"], field=field):
                    contents = self.create_archives()[package["archive"]]
                    if package["kind"] == "npm":
                        value = json.loads(contents["package.json"])
                        value[field] = value[field].replace(COMMIT, "main" if field == "homepage" else "b" * 40)
                        contents["package.json"] = json.dumps(value).encode()
                    else:
                        name = package["item"]["packageId"] + ".nuspec"
                        contents[name] = contents[name].replace(COMMIT.encode(), b"b" * 40)
                    self.write_archive(package, contents)
                    with self.assertRaisesRegex(ValueError, "homepage/gitHead|RepositoryCommit"):
                        d.verify(self.root, [self.root / "artifacts"], COMMIT)

    def test_release_documentation_rejects_nuspec_entities_regardless_of_encoding(self):
        package = self.packages[0]
        name = package["item"]["packageId"] + ".nuspec"
        for encoding in ("utf-8", "utf-16"):
            with self.subTest(encoding=encoding):
                contents = self.create_archives()[package["archive"]]
                contents[name] = (f'<!DOCTYPE package [<!ENTITY commit "{COMMIT}">]>'
                    '<package><metadata><repository commit="&commit;" /></metadata></package>').encode(encoding)
                self.write_archive(package, contents)
                with self.assertRaises(ValueError):
                    d.verify(self.root, [self.root / "artifacts"], COMMIT)

    def test_release_validation_requires_closed_document_inventory_and_no_mutable_metadata(self):
        for mutation in ("missing", "extra", "metadata", "api-xml", "jsdoc", "template-json"):
            with self.subTest(mutation=mutation):
                contents = self.create_archives()
                package = self.packages[1]
                data = contents[package["archive"]]
                if mutation == "missing":
                    del data["content/sample/README.md"]
                elif mutation == "extra":
                    data["content/EXTRA.md"] = b"unreviewed documentation"
                elif mutation == "metadata":
                    name = package["item"]["packageId"] + ".nuspec"
                    data[name] = data[name].replace(b"<metadata>", f"<metadata><projectUrl>{URL}/blob/main/docs/policy.md</projectUrl>".encode())
                else:
                    name = {"api-xml": "lib/net10.0/Api.xml", "jsdoc": "types/api.d.ts", "template-json": "content/template/package.json"}[mutation]
                    data[name] = f"{URL}/blob/main/docs/policy.md".encode()
                self.write_archive(package, data)
                with self.assertRaisesRegex(ValueError, "documentation inventory|mutable documentation"):
                    d.verify(self.root, [self.root / "artifacts"], COMMIT)

    def test_ci_binds_before_pack_and_artifact_consumption_using_the_existing_commands(self):
        ci = (d.ROOT / ".github/workflows/sunder-ci.yml").read_text()
        wrapper = ci.index('sdk_release_documentation.py pack "$SOURCE_SHA" -- bash -euo pipefail')
        validation = ci.index('sdk_release_documentation.py verify "$SOURCE_SHA" artifacts/sdk artifacts/npm')
        self.assertLess(wrapper, ci.index('dotnet pack "$project"'))
        self.assertLess(wrapper, ci.index("npm pack --prefix"))
        self.assertLess(ci.index("verify-node-package-artifacts.mjs"), validation)
        self.assertLess(validation, ci.index("name: Source catalog and generated .NET template variants"))
        self.assertLess(validation, ci.index("name: Retain the tested candidate feed"))
        self.assertIn('if [[ "$GITHUB_REF" == refs/tags/sdk/* ]]; then', ci)
        block = ci[ci.rfind("        run: |", 0, wrapper):ci.index("      - name:", validation)]
        script = "\n".join(line[10:] for line in block.splitlines()[1:])
        script = re.sub(r"\$\{\{.*?\}\}", "GITHUB_EXPRESSION", script)
        subprocess.run(["bash", "-n"], input=script, text=True, check=True, capture_output=True)


if __name__ == "__main__":
    unittest.main()
