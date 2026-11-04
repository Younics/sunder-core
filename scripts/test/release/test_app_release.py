"""App-only release inventory; shared asset integrity is tested with the transport."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import github_release


class AppReleaseTests(unittest.TestCase):
    def test_app_inventory_contains_all_native_packages_and_installers(self):
        self.assertEqual(18, len(github_release.core_names("app", "1.0.0")))


if __name__ == "__main__": unittest.main()
