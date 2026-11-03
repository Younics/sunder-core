#!/usr/bin/env python3
"""Generate Full-only feeds for the complete declared App platform inventory."""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ci"))
from metadata import platforms


def generate(directory, channel):
    for row in platforms()["include"]:
        rid = row["rid"]
        output = Path(directory) / ("sunder-app-release-" + rid)
        if not output.is_dir(): raise ValueError("Missing App platform output: " + rid)
        with tempfile.TemporaryDirectory(prefix="sunder-feed-") as target:
            subprocess.run(["vpk", "upload", "local", "--path", target, "--regenerate", "--channel", f"app-{rid}-{channel}",
                            "--outputDir", str(output)], check=True)
            name = f"releases.app-{rid}-{channel}.json"
            shutil.copyfile(Path(target) / name, output / name)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--channel", default="stable")
    args = parser.parse_args()
    generate(args.directory, args.channel)
