#!/usr/bin/env python3
"""DMG retry/ownership and real Bash failure paths; no native mounts or Finder."""
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import re
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[3]
BUILDER = ROOT / "scripts/release/macos/create-sunder-dmg.sh"
SPEC = importlib.util.spec_from_file_location("dmg_image", ROOT / "scripts/release/macos/dmg_image.py")
dmg = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(dmg)


class DmgImageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="sunder-dmg-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.image, self.mount = self.root / "image.dmg", self.root / "mount"
        self.mount.mkdir()
        self.entities = [{"dev-entry": "/dev/disk42"},
                         {"dev-entry": "/dev/disk42s1", "mount-point": str(self.mount)}]
        self.images = [{"image-path": str(self.image), "system-entities": self.entities}]
        self.statuses = [0]
        self.calls = []
        self.false_success = False
        native = patch.object(dmg, "run", side_effect=self.run_native)
        self.native = native.start()
        self.addCleanup(native.stop)
        sleep = patch.object(dmg.time, "sleep")
        self.sleep = sleep.start()
        self.addCleanup(sleep.stop)
        which = patch.object(dmg.shutil, "which", return_value="/fixture/lsof")
        which.start()
        self.addCleanup(which.stop)

    def run_native(self, arguments, timeout=30):
        self.calls.append(arguments)
        if arguments[:2] == ["hdiutil", "info"]:
            return subprocess.CompletedProcess(arguments, 0, plistlib.dumps({"images": self.images}), b"")
        if arguments[:2] == ["hdiutil", "detach"]:
            status = self.statuses.pop(0)
            if not status and not self.false_success:
                self.images = []
            return subprocess.CompletedProcess(arguments, status, b"", b"Resource busy" if status == 16 else b"native error")
        if arguments[0] == "lsof":
            return subprocess.CompletedProcess(arguments, 0, b"fixture-holder 123 file\n", b"")
        raise AssertionError(arguments)

    def test_busy_retry_requires_normal_observed_detachment(self):
        self.statuses = [16, 16, 0]
        dmg.detach(self.image, self.mount)
        self.assertEqual([2, 4], [call.args[0] for call in self.sleep.call_args_list])
        self.assertEqual([["hdiutil", "detach", "/dev/disk42"]] * 3,
                         [call for call in self.calls if call[1] == "detach"])
        self.assertFalse(self.images)
        self.assertTrue(any(call[0] == "lsof" for call in self.calls))

    def test_exhausted_and_unexpected_errors_never_force_detach(self):
        for statuses, delays in (([16] * 5, [2, 4, 8, 16]), ([1], [])):
            with self.subTest(statuses=statuses):
                self.calls.clear()
                self.sleep.reset_mock()
                self.statuses = statuses.copy()
                with self.assertRaises(dmg.ImageError):
                    dmg.detach(self.image, self.mount)
                self.assertEqual(len(statuses), sum(call[1] == "detach" for call in self.calls))
                self.assertEqual(delays, [call.args[0] for call in self.sleep.call_args_list])
                self.assertFalse(any("-force" in call for call in self.calls))
                self.assertTrue(self.images)

    def test_refuses_foreign_ambiguous_or_unrecognised_attachments(self):
        foreign = [{"image-path": str(self.root / "foreign.dmg"), "system-entities": self.entities}]
        wrong_mount = [{"image-path": str(self.image), "system-entities": [
            {"dev-entry": "/dev/disk42", "mount-point": str(self.root / "elsewhere")}]}]
        missing_device = [{"image-path": str(self.image), "system-entities": [self.entities[1]]}]
        for images in (foreign, wrong_mount, missing_device, self.images * 2):
            with self.subTest(images=images), patch.object(Path, "is_mount", return_value=True):
                self.images = images
                self.calls.clear()
                with self.assertRaises(dmg.ImageError):
                    dmg.detach(self.image, self.mount, discard=True)
                self.assertFalse(any(call[1] == "detach" for call in self.calls))

    def test_command_success_does_not_override_still_attached_state(self):
        self.false_success = True
        with self.assertRaisesRegex(dmg.ImageError, "still attached"):
            dmg.detach(self.image, self.mount)
        self.assertTrue(self.images)

    def test_discard_checks_identity_and_confirms_detachment(self):
        dmg.detach(self.image, self.mount, discard=True)
        self.assertEqual(["hdiutil", "detach", "/dev/disk42", "-force"], self.calls[1])
        self.assertFalse(self.images)
        self.assertEqual(["hdiutil", "info", "-plist"], self.calls[-1])
        self.sleep.assert_not_called()

    def test_unknown_attachment_state_fails_closed(self):
        for result in (subprocess.CompletedProcess([], 1, b"", b"query failed"),
                       subprocess.CompletedProcess([], 0, b"not a plist", b"")):
            with self.subTest(result=result), patch.object(dmg, "run", return_value=result):
                with self.assertRaises(dmg.ImageError):
                    dmg.detach(self.image, self.mount, discard=True)


class DmgCommandTests(unittest.TestCase):
    def test_native_commands_use_regular_files_and_a_timeout(self):
        def native(arguments, stdout, stderr, timeout):
            self.assertTrue(stat.S_ISREG(os.fstat(stdout.fileno()).st_mode))
            self.assertTrue(stat.S_ISREG(os.fstat(stderr.fileno()).st_mode))
            self.assertEqual(30, timeout)
            stdout.write(b"output")
            stderr.write(b"error")
            return subprocess.CompletedProcess(arguments, 16)
        with patch.object(dmg.subprocess, "run", side_effect=native):
            result = dmg.run(["hdiutil", "detach", "/dev/disk42"])
        self.assertEqual((16, b"output", b"error"), (result.returncode, result.stdout, result.stderr))
        with patch.object(dmg.subprocess, "run", side_effect=subprocess.TimeoutExpired("hdiutil", 30)):
            with self.assertRaises(dmg.ImageError):
                dmg.run(["hdiutil", "detach", "/dev/disk42"])


TOOLS = r'''#!/usr/bin/env python3
import json, os, pathlib, plistlib, shutil, sys
root = pathlib.Path(os.environ["DMG_FIXTURE"])
name, args = pathlib.Path(sys.argv[0]).name, sys.argv[1:]
with (root / "calls").open("a") as output:
    output.write(json.dumps([name, *args]) + "\n")
state = root / "attached.json"
def plist(entities):
    return plistlib.dumps({"system-entities": entities})
if name == "uname": print("Darwin")
elif name == "ditto": shutil.copytree(args[-2], args[-1])
elif name == "swift": pass
elif name == "tiffutil": pathlib.Path(args[-1]).write_text("fixture artwork")
elif name == "osascript": pass
elif name == "hdiutil":
    operation = args[0]
    if operation == "create": pathlib.Path(args[-1]).touch()
    elif operation == "attach":
        mount = args[args.index("-mountpoint") + 1]
        entities = [{"dev-entry": "/dev/disk42"}, {"dev-entry": "/dev/disk42s1", "mount-point": mount}]
        state.write_text(json.dumps({"image-path": args[-1], "system-entities": entities}))
        pathlib.Path(mount, ".DS_Store").write_text("fixture layout")
        if os.environ.get("DMG_PARTIAL_ATTACH") == "1":
            print("attach failed after device creation", file=sys.stderr)
            sys.exit(63)
        sys.stdout.buffer.write(plist(entities))
    elif operation == "info":
        sys.stdout.buffer.write(plistlib.dumps({"images": [json.loads(state.read_text())] if state.exists() else []}))
    elif operation == "detach":
        if "-force" in args and os.environ["DMG_CLEANUP_SUCCEEDS"] == "1": state.unlink()
        else:
            print("fixture detach failure", file=sys.stderr)
            sys.exit(1)
    elif operation == "convert": pathlib.Path(root, "converted").touch()
    else: sys.exit("Unexpected hdiutil operation: " + operation)
else: sys.exit("Unexpected fixture tool: " + name)
'''


class DmgBuilderTests(unittest.TestCase):
    def test_failed_or_partial_attachment_never_publishes_and_retains_uncertain_mount(self):
        for partial in (False, True):
            for cleanup_succeeds in (False, True):
                with self.subTest(partial=partial, cleanup=cleanup_succeeds), tempfile.TemporaryDirectory(prefix="dmg-builder-") as directory:
                    root = Path(directory).resolve()
                    tools = root / "bin"
                    tools.mkdir()
                    mock = tools / "mock"
                    mock.write_text(TOOLS)
                    mock.chmod(0o755)
                    for tool in ("uname", "ditto", "swift", "tiffutil", "osascript", "hdiutil"):
                        (tools / tool).symlink_to(mock)
                    app = root / "Sunder.app/Contents"
                    app.mkdir(parents=True)
                    (app / "Info.plist").write_text("fixture app")
                    output = root / "Sunder.dmg"
                    output.write_bytes(b"previous output")
                    result = subprocess.run(["bash", str(BUILDER), "--app", str(app.parent), "--output", str(output)],
                        env={"PATH": str(tools) + os.pathsep + os.environ["PATH"], "HOME": str(root),
                             "TMPDIR": str(root), "DMG_FIXTURE": str(root), "PYTHONDONTWRITEBYTECODE": "1",
                             "DMG_PARTIAL_ATTACH": str(int(partial)), "DMG_CLEANUP_SUCCEEDS": str(int(cleanup_succeeds))},
                        text=True, capture_output=True, timeout=15)
                    self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                    self.assertEqual(b"previous output", output.read_bytes())
                    self.assertFalse((root / "converted").exists())
                    calls = [json.loads(line) for line in (root / "calls").read_text().splitlines()]
                    detach = [call for call in calls if call[:2] == ["hdiutil", "detach"]]
                    self.assertIn(["hdiutil", "detach", "/dev/disk42", "-force"], detach)
                    self.assertEqual(not partial, any("-force" not in call for call in detach))
                    self.assertEqual(not cleanup_succeeds, (root / "attached.json").exists())
                    self.assertEqual(not cleanup_succeeds, bool(list(root.glob("sunder-dmg-layout.*"))))
                    if not cleanup_succeeds:
                        self.assertIn("retaining staging files", result.stderr)

    def test_builder_and_standalone_installer_cleanup_preserve_original_status(self):
        for source, root_variable in ((BUILDER, "work_root"), (ROOT / "scripts/install/install.sh", "tmp_dir")):
            match = re.search(r"^cleanup\(\) \{\n.*?^\}", source.read_text(), re.M | re.S)
            self.assertIsNotNone(match)
            for status, cleanup_status in ((23, 0), (23, 1), (0, 1), (0, 0)):
                with self.subTest(source=source.name, status=status, cleanup=cleanup_status), tempfile.TemporaryDirectory(prefix="dmg-cleanup-") as directory:
                    root = Path(directory) / "owned"
                    root.mkdir()
                    script = match.group(0) + '''
python3() { return "$CLEANUP_STATUS"; }
hdiutil() { return "$CLEANUP_STATUS"; }
mounted=true
read_write_dmg=unused image_tool=unused mount_root=unused mount_dir=unused
trap cleanup EXIT
exit "$ORIGINAL_STATUS"
'''
                    result = subprocess.run(["bash", "-euo", "pipefail", "-c", script],
                        env={"PATH": os.environ["PATH"], root_variable: str(root),
                             "CLEANUP_STATUS": str(cleanup_status), "ORIGINAL_STATUS": str(status)},
                        text=True, capture_output=True, timeout=5)
                    self.assertEqual(status or cleanup_status, result.returncode, result.stdout + result.stderr)
                    self.assertEqual(bool(cleanup_status), root.exists())


if __name__ == "__main__":
    unittest.main()
