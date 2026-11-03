import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "ci"))
import metadata
import core_ci


class CiMetadataTests(unittest.TestCase):
    def test_toolchain_changes_follow_the_owners_without_rewriting_workflows(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("global.json", "eng/toolchains.json", "eng/release/tooling.json", "languages/csharp/dotnet/Directory.Packages.props"):
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(metadata.ROOT / name, target)
            policy = json.loads((root / "eng/toolchains.json").read_text())
            policy.update(dotnetSdk="11.0.100", node="26.1.0", npm="12.0.0")
            (root / "eng/toolchains.json").write_text(json.dumps(policy))
            with self.assertRaisesRegex(ValueError, "global.json"): metadata.toolchains(root)
            (root / "global.json").write_text(json.dumps(dict(sdk=dict(version="11.0.100"))))
            central = root / "languages/csharp/dotnet/Directory.Packages.props"
            central.write_text('<Project><ItemGroup><PackageVersion Include="Velopack" Version="2.3.4" /></ItemGroup></Project>')
            result = metadata.toolchains(root)
            self.assertEqual(("11.0.100", "26.1.0", "12.0.0", "2.3.4"), tuple(result[key] for key in ("dotnet", "node", "npm", "velopack")))

    def test_native_qualification_and_signed_packaging_use_the_same_platform_set(self):
        native = metadata.platforms()["include"]
        app = metadata.platforms(profile="app")["include"]
        self.assertEqual([row["rid"] for row in native], [row["rid"] for row in app])
        self.assertEqual(6, len(native))
        self.assertEqual("windows-11-arm", next(row["os"] for row in native if row["rid"] == "win-arm64"))
        self.assertEqual("windows-2025", next(row["os"] for row in app if row["rid"] == "win-arm64"))

    def test_fixture_version_changes_select_current_host_leaf_and_preserve_all_rids(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = root / "fixtures/runtime-backed-worker"
            fixture.mkdir(parents=True)
            (fixture / "sunder.package.json").write_text(json.dumps(dict(id="example.worker", version="3.7.2")))
            rids = [row["rid"] for row in metadata.platforms()["include"]]
            for rid in rids:
                (fixture / "dist/targets" / rid).mkdir(parents=True)
            for rid in rids:
                with self.subTest(rid=rid), patch.object(core_ci, "NODE", root), patch.object(core_ci, "run") as run, \
                     patch.object(core_ci, "target", return_value=Path("cli.dll")), \
                     patch.object(core_ci, "property_value", return_value=rid), patch.dict(os.environ, {}, clear=True):
                    values = core_ci.module_inputs()
                    self.assertEqual(fixture / "dist/example.worker.3.7.2.sunderpkg", values["SUNDER_NODE_MODULE_PACKAGE_PATH"])
                    self.assertEqual(rids, values["SUNDER_NODE_MODULE_EXPECTED_RIDS"].split(","))
                    self.assertEqual(fixture / "dist/targets" / rid, values["SUNDER_NODE_MODULE_LEAF_PATH"])
                    self.assertIn(values["SUNDER_NODE_MODULE_PACKAGE_PATH"], run.call_args.args)
                    # Other target leaves cannot substitute for the executing Host.
                    values["SUNDER_NODE_MODULE_LEAF_PATH"].rmdir()
                    with self.assertRaisesRegex(ValueError, "Missing current-RID"):
                        core_ci.module_inputs()


if __name__ == "__main__": unittest.main()
