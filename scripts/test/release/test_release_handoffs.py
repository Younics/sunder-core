"""Source, numeric asset and JSON boundaries shared by release transports."""
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "release"))
import github_release as assets
import release_coordinates as c

SOURCE, OTHER = "a" * 40, "b" * 40


class ReleaseHandoffTests(unittest.TestCase):
    def test_dirty_or_wrong_checkout_is_not_release_source(self):
        for head, status in ((OTHER, b""), (SOURCE, b" M input.json\n"), (SOURCE, b"?? input.json\n")):
            with self.subTest(head=head, status=status), patch.object(c, "command", side_effect=[head.encode(), status]):
                with self.assertRaises(ValueError): c.local_checkout(SOURCE)

    def test_annotated_tag_requires_exact_source(self):
        with patch.object(c, "gh", side_effect=[dict(object=dict(type="tag", sha=OTHER)), dict(object=dict(type="commit", sha=SOURCE))]):
            c.remote_tag("nuget-sunder-sdk/v1.1.0", SOURCE)
        with patch.object(c, "gh", return_value=dict(object=dict(type="commit", sha=OTHER))):
            with self.assertRaises(ValueError): c.remote_tag("nuget-sunder-sdk/v1.1.0", SOURCE)

    def test_closed_json_assets_and_coordinates_reject_ambiguous_inputs(self):
        for raw in (b'{"id":1,"id":2}', b'{"id":1,"ID":2}', b'{"id":NaN}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError): c.decode(raw)
        for value in (True, 0, "01", "-1", "1e2", "9007199254740992"):
            with self.subTest(value=value), self.assertRaises(ValueError): c.number(value)
        for entries in ([], [dict(name="catalog"), dict(name="catalog")]):
            with self.subTest(entries=entries), self.assertRaises(ValueError): assets.asset(dict(assets=entries), "catalog")


if __name__ == "__main__": unittest.main()
