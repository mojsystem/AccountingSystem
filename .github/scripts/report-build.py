#!/usr/bin/env python3
"""Shows compiler and restore diagnostics of `dotnet restore|build|publish|test` as annotations.

The full job log is not always available, so every error found in the log is also written as a
GitHub Actions ::error annotation. The annotations are visible on the run page and through the
check-run annotations API, which makes compile failures diagnosable without the raw log.

Usage: python3 .github/scripts/report-build.py <log-file> [<log-file> ...]
The logs are produced with `dotnet ... 2>&1 | tee <log-file>`.
This script never fails the job by itself; the build step's own exit code decides the result.
"""
import pathlib
import re
import sys

MAX_ANNOTATIONS = 50

# C:\path\File.cs(12,34): error CS1002: ; expected [C:\path\Project.csproj]
LOCATED = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+)(?:,(?P<col>\d+))?\)\s*:\s*(?P<kind>error|warning)\s+"
    r"(?P<code>[A-Za-z]+\d+)\s*:\s*(?P<msg>.*)$"
)

# error NU1101: Unable to find package ...   /   MSBUILD : error MSB1009: Project file does not exist.
UNLOCATED = re.compile(
    r"^(?:MSBUILD\s*:\s*)?(?P<kind>error|warning)\s+(?P<code>[A-Za-z]+\d+)\s*:\s*(?P<msg>.*)$"
)

PROJECT_SUFFIX = re.compile(r"\s+\[[^\]]*\]$")


def escape_data(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text: str) -> str:
    return escape_data(text).replace(":", "%3A").replace(",", "%2C")


def report_log(path: pathlib.Path, state: dict) -> None:
    if not path.exists():
        print(f"::notice title=Build log::{path.name} was not written")
        return

    text = path.read_text(encoding="utf-8", errors="replace")
    errors = 0
    warnings = 0
    for raw in text.splitlines():
        line = raw.strip()
        match = LOCATED.match(line) or UNLOCATED.match(line)
        if match is None:
            continue

        kind = match.group("kind")
        code = match.group("code")
        message = PROJECT_SUFFIX.sub("", match.group("msg")).strip()
        if kind == "warning":
            warnings += 1
            continue

        # MSBuild repeats the same diagnostic for every project that reports it.
        if line in state["seen"]:
            continue
        state["seen"].add(line)
        errors += 1
        if state["annotated"] >= MAX_ANNOTATIONS:
            continue
        state["annotated"] += 1

        properties = [f"title={escape_property(code)}"]
        if match.groupdict().get("file"):
            properties.append(f"file={escape_property(match.group('file'))}")
            properties.append(f"line={match.group('line')}")
            if match.group("col"):
                properties.append(f"col={match.group('col')}")
        print(f"::error {','.join(properties)}::{escape_data(code + ': ' + message)}")

    state["errors"] += errors
    state["warnings"] += warnings
    print(f"{path.name}: {errors} error(s), {warnings} warning(s)")


def main() -> int:
    state = {"seen": set(), "annotated": 0, "errors": 0, "warnings": 0}
    for name in sys.argv[1:]:
        try:
            report_log(pathlib.Path(name), state)
        except Exception as error:  # the reporter must not break the build
            print(f"::warning title=Build report::could not read {name}: {error}")

    if state["errors"] > 0:
        print(
            f"::notice title=Build summary::{state['errors']} error(s) and {state['warnings']} warning(s); "
            f"{state['annotated']} error annotation(s) written"
        )
    elif len(sys.argv) > 1:
        print(f"::notice title=Build summary::no errors found ({state['warnings']} warning(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
