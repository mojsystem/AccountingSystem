#!/usr/bin/env python3
"""Prints the outcome of every test in the TRX files under a directory.

Each test that did not pass is written as a GitHub Actions ::error annotation that
contains the failure message, so the reason is visible on the run page even when the
full job log is not available.

Usage: python3 .github/scripts/report-trx.py <directory with .trx files>
"""
import pathlib
import sys
import xml.etree.ElementTree as ET

TEAM_TEST_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
MAX_MESSAGE_CHARS = 1500


def escape_data(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text: str) -> str:
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


def child_text(element: ET.Element, path: str) -> str:
    node = element.find(path)
    return (node.text or "").strip() if node is not None else ""


def main() -> int:
    results_dir = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "TestResults")
    trx_files = sorted(results_dir.rglob("*.trx"))
    if not trx_files:
        print(f"::error title=Test results::No .trx file found under {results_dir}")
        return 0

    total = 0
    not_passed = 0
    for trx in trx_files:
        root = ET.parse(trx).getroot()
        for result in root.iter(f"{TEAM_TEST_NS}UnitTestResult"):
            total += 1
            outcome = result.get("outcome", "Unknown")
            if outcome == "Passed":
                continue

            not_passed += 1
            name = result.get("testName", "unknown test")
            message = child_text(result, f"{TEAM_TEST_NS}Output/{TEAM_TEST_NS}ErrorInfo/{TEAM_TEST_NS}Message")
            stack = child_text(result, f"{TEAM_TEST_NS}Output/{TEAM_TEST_NS}ErrorInfo/{TEAM_TEST_NS}StackTrace")
            details = message or "(no error message)"
            if stack:
                details += "\n" + "\n".join(stack.splitlines()[:5])
            details = details[:MAX_MESSAGE_CHARS]

            print(f"{outcome}: {name}")
            print(details)
            title = escape_property(f"{outcome}: {name}")
            print(f"::error title={title}::{escape_data(details)}")

    print(f"::notice title=Test summary::{total - not_passed} of {total} tests passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
