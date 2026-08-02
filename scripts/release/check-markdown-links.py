#!/usr/bin/env python3
"""Check local Markdown/HTML links and GitHub-style heading anchors."""

from __future__ import annotations

import argparse
import html
import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit


SKIPPED_DIRECTORIES = {".git", "bin", "node_modules", "obj"}
MARKDOWN_LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
HTML_LINK = re.compile(r"<(?:a|img)\b[^>]*\b(?:href|src)=[\"']([^\"']+)[\"']", re.IGNORECASE)
HTML_ANCHOR = re.compile(r"<a\b[^>]*\b(?:id|name)=[\"']([^\"']+)[\"']", re.IGNORECASE)
HEADING = re.compile(r"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")
HTML_TAG = re.compile(r"<[^>]+>")
MARKDOWN_FORMATTING = re.compile(r"[`*_~]")
GITHUB_PUNCTUATION = re.compile(r"[^\w\- ]", re.UNICODE)


def markdown_files(paths: list[Path]) -> list[Path]:
    files: set[Path] = set()
    for path in paths:
        if path.is_file() and path.suffix.lower() == ".md":
            files.add(path.resolve())
            continue
        if not path.is_dir():
            raise ValueError(f"Path does not exist: {path}")
        for candidate in path.rglob("*.md"):
            if not any(part in SKIPPED_DIRECTORIES for part in candidate.parts):
                files.add(candidate.resolve())
    return sorted(files)


def github_slug(value: str) -> str:
    value = html.unescape(HTML_TAG.sub("", value))
    value = MARKDOWN_FORMATTING.sub("", value).strip().lower()
    value = GITHUB_PUNCTUATION.sub("", value)
    return value.replace(" ", "-")


def anchors(path: Path) -> set[str]:
    result: set[str] = set()
    duplicate_counts: dict[str, int] = {}
    in_fence = False
    fence_marker = ""
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.lstrip()
        if stripped.startswith(("```", "~~~")):
            marker = stripped[:3]
            if not in_fence:
                in_fence = True
                fence_marker = marker
            elif marker == fence_marker:
                in_fence = False
            continue
        if in_fence:
            continue
        for explicit in HTML_ANCHOR.findall(line):
            result.add(unquote(html.unescape(explicit)).lower())
        match = HEADING.match(line)
        if match is None:
            continue
        base = github_slug(match.group(1))
        if not base:
            continue
        count = duplicate_counts.get(base, 0)
        duplicate_counts[base] = count + 1
        result.add(base if count == 0 else f"{base}-{count}")
    return result


def targets(path: Path) -> list[tuple[int, str]]:
    result: list[tuple[int, str]] = []
    in_fence = False
    fence_marker = ""
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        stripped = line.lstrip()
        if stripped.startswith(("```", "~~~")):
            marker = stripped[:3]
            if not in_fence:
                in_fence = True
                fence_marker = marker
            elif marker == fence_marker:
                in_fence = False
            continue
        if in_fence:
            continue
        for raw in MARKDOWN_LINK.findall(line):
            value = raw.strip()
            if value.startswith("<") and ">" in value:
                value = value[1:value.index(">")]
            elif " " in value:
                value = value.split(" ", 1)[0]
            result.append((line_number, html.unescape(value)))
        for raw in HTML_LINK.findall(line):
            result.append((line_number, html.unescape(raw.strip())))
    return result


def check(files: list[Path], repository_root: Path) -> list[str]:
    errors: list[str] = []
    anchor_cache: dict[Path, set[str]] = {}
    for source in files:
        for line_number, raw_target in targets(source):
            parsed = urlsplit(raw_target)
            if parsed.scheme or parsed.netloc:
                continue
            decoded_path = unquote(parsed.path)
            if decoded_path.startswith("/"):
                destination = repository_root / decoded_path.lstrip("/")
            elif decoded_path:
                destination = source.parent / decoded_path
            else:
                destination = source
            destination = destination.resolve()
            if destination.is_dir():
                destination = destination / "README.md"
            location = f"{source.relative_to(repository_root)}:{line_number}"
            if not destination.exists():
                errors.append(f"{location}: missing local link target '{raw_target}'")
                continue
            if parsed.fragment and destination.suffix.lower() in {".md", ".markdown"}:
                expected = unquote(parsed.fragment).lower()
                available = anchor_cache.setdefault(destination, anchors(destination))
                if expected not in available:
                    errors.append(f"{location}: missing anchor '#{parsed.fragment}' in '{destination.relative_to(repository_root)}'")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*", default=["."], help="Markdown files or directories (default: repository root)")
    arguments = parser.parse_args()
    repository_root = Path.cwd().resolve()
    try:
        files = markdown_files([Path(value) for value in arguments.paths])
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2
    errors = check(files, repository_root)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"Checked {len(files)} Markdown files for local links and anchors.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
