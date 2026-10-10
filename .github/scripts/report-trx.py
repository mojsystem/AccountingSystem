#!/usr/bin/env python3
"""Prints test outcomes and useful diagnostics from TRX files and runner logs."""
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


def report_log_excerpt(path: pathlib.Path) -> None:
    if not path.exists():
        return
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        excerpt = "\n".join(lines[-60:])[-MAX_MESSAGE_CHARS:]
        if excerpt:
            print(f"::error title=Test runner output::{escape_data(excerpt)}")
    except OSError as error:
        print(f"::warning title=Test runner output::could not read {path.name}: {error}")


def main() -> int:
    results_dir = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "TestResults")
    fallback_log = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else None
    trx_files = sorted(results_dir.rglob("*.trx"))
    if not trx_files:
        print(f"::error title=Test results::No .trx file found under {results_dir}")
        if fallback_log is not None:
            report_log_excerpt(fallback_log)
        return 0

    total = 0
    not_passed = 0
    parse_failed = False
    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except (ET.ParseError, OSError) as error:
            parse_failed = True
            message = f"Could not parse {trx}: {error}"
            print(f"::error title=TRX parse::{escape_data(message)}")
            continue

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

    if (total == 0 or parse_failed) and fallback_log is not None:
        report_log_excerpt(fallback_log)
    print(f"::notice title=Test summary::{total - not_passed} of {total} tests passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
