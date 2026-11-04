import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "ci"))
import artifacts


class ArtifactCleanupTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        (self.root / ".gitignore").write_text("artifacts/\n")
        self.path = self.root / "artifacts/old"
        self.path.mkdir(parents=True)

    def test_pinned_release_and_active_worktree_cannot_be_deleted_via_parent(self):
        (self.path / artifacts.PIN).write_text("release pending")
        self.assertIsNotNone(artifacts.protected(self.path, self.root))
        self.assertIsNotNone(artifacts.protected(self.path.parent, self.root))
        (self.path / artifacts.PIN).unlink()
        self.assertIsNotNone(artifacts.protected(self.path, self.root, ["dotnet " + str(self.path / "worker.dll")]))

    def test_symlink_selection_and_nonartifact_paths_are_rejected_but_cleanup_does_not_follow_links(self):
        outside = self.root / "source"
        outside.mkdir()
        (outside / "keep").write_text("source")
        (self.path / "link").symlink_to(outside, target_is_directory=True)
        for path in (outside, self.path.parent, self.path / "link/keep"):
            with self.assertRaises(ValueError): artifacts.validate_path(self.root, path)
        artifacts.remove(self.path)
        self.assertEqual("source", (outside / "keep").read_text())

    def test_tracked_or_unignored_data_is_rejected_and_readonly_generated_data_is_removable(self):
        file = self.path / "generated"
        file.write_text("generated")
        artifacts.ignored_only(self.path)
        subprocess.run(["git", "add", "-f", str(file)], cwd=self.root, check=True)
        with self.assertRaises(ValueError): artifacts.ignored_only(self.path)
        subprocess.run(["git", "rm", "--cached", "-q", str(file)], cwd=self.root, check=True)
        file.chmod(0o400)
        self.path.chmod(0o500)
        artifacts.remove(self.path)
        self.assertFalse(self.path.exists())

    def test_report_retention_keeps_recent_files_inside_old_directories(self):
        reports = self.root / "artifacts/test-results"
        reports.mkdir()
        for index in range(4):
            directory = reports / str(index)
            directory.mkdir()
            (directory / "result.trx").write_text("report")
            for path in (directory / "result.trx", directory): os.utime(path, (1 + index, 1 + index))
        os.utime(reports / "0/result.trx", None)
        self.assertEqual({"1", "2"}, {path.name for path in artifacts.report_candidates(self.root, 1, 14)})


if __name__ == "__main__": unittest.main()
