#!/usr/bin/env python3
"""Exact numeric asset lookup for the Core platform-catalog release handoff."""
from pathlib import Path

import release_coordinates as c

HELPERS = Path(__file__).resolve().parent


def asset(value, name):
    matches = [item for item in value["assets"] if item["name"] == name]
    c.require(len(matches) == 1, "Missing/ambiguous retained asset: " + name)
    return matches[0]
