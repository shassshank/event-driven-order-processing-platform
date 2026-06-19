#!/usr/bin/env python3
"""Print a final pass/fail summary for dotnet test TRX results."""
from __future__ import annotations

import sys
import textwrap
import xml.etree.ElementTree as ET
from pathlib import Path


def local_name(tag: str) -> str:
    return tag.rsplit('}', 1)[-1]


def child_text(element: ET.Element, names: tuple[str, ...]) -> str:
    current = element
    for name in names:
        found = None
        for child in current:
            if local_name(child.tag) == name:
                found = child
                break
        if found is None:
            return ""
        current = found
    return (current.text or "").strip()


def find_build_errors(log_file: Path | None) -> list[str]:
    if log_file is None or not log_file.exists():
        return []

    lines = log_file.read_text(errors="replace").splitlines()
    build_error_lines: list[str] = []
    for line in lines:
        stripped = line.strip()
        if ": error " in stripped or stripped.startswith("error ") or "Build FAILED" in stripped:
            if stripped not in build_error_lines:
                build_error_lines.append(stripped)
    return build_error_lines


def summarize(results_dir: Path, dotnet_exit_code: int, log_file: Path | None = None) -> int:
    trx_files = sorted(results_dir.rglob("*.trx"))
    total = passed = failed = skipped = other = 0
    failed_tests: list[tuple[str, str, str, str]] = []
    build_errors = find_build_errors(log_file)

    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except ET.ParseError as ex:
            failed += 1
            failed_tests.append(("<invalid TRX>", str(trx), f"Could not parse TRX: {ex}", ""))
            continue

        for element in root.iter():
            if local_name(element.tag) != "UnitTestResult":
                continue

            total += 1
            outcome = element.attrib.get("outcome", "")
            test_name = element.attrib.get("testName", "<unknown test>")
            if outcome == "Passed":
                passed += 1
            elif outcome == "Failed":
                failed += 1
                message = child_text(element, ("Output", "ErrorInfo", "Message"))
                stack = child_text(element, ("Output", "ErrorInfo", "StackTrace"))
                failed_tests.append((test_name, str(trx), message, stack))
            elif outcome in {"NotExecuted", "Warning"}:
                skipped += 1
            else:
                other += 1

    print()
    print("=" * 72)
    print("TEST SUMMARY")
    print("=" * 72)

    if not trx_files:
        print(f"No TRX result files were found in: {results_dir}")
        if dotnet_exit_code != 0:
            print(f"dotnet test exited with code {dotnet_exit_code}. This usually means restore/build/test discovery failed before results were written.")
            if build_errors:
                print()
                print("BUILD / INFRASTRUCTURE ERRORS")
                for index, line in enumerate(build_errors, start=1):
                    print(f"{index}. {line}")
        print("=" * 72)
        return dotnet_exit_code or 1

    print(f"Result files: {len(trx_files)}")
    print(f"Total: {total} | Passed: {passed} | Failed: {failed} | Skipped: {skipped} | Other: {other}")

    if failed_tests:
        print()
        print("FAILED TESTS")
        for index, (test_name, trx, message, stack) in enumerate(failed_tests, start=1):
            print(f"{index}. {test_name}")
            print(f"   Result file: {trx}")
            if message:
                print("   Message:")
                print(textwrap.indent(message, "     "))
            if stack:
                print("   Stack trace:")
                print(textwrap.indent(stack, "     "))
    else:
        print()
        print("Failed tests: none")

    if dotnet_exit_code != 0 and failed == 0:
        print()
        print(f"dotnet test exited with code {dotnet_exit_code}, but no failed UnitTestResult entries were found.")
        if build_errors:
            print()
            print("BUILD / INFRASTRUCTURE ERRORS")
            for index, line in enumerate(build_errors, start=1):
                print(f"{index}. {line}")
        else:
            print("Check the console output above for build errors, test host crashes, or infrastructure failures.")

    print("=" * 72)
    return 1 if failed > 0 or dotnet_exit_code != 0 else 0


def main() -> int:
    if len(sys.argv) not in {3, 4}:
        print("Usage: summarize-test-results.py <results-dir> <dotnet-exit-code> [dotnet-test-log]", file=sys.stderr)
        return 2

    log_file = Path(sys.argv[3]) if len(sys.argv) == 4 else None
    return summarize(Path(sys.argv[1]), int(sys.argv[2]), log_file)


if __name__ == "__main__":
    raise SystemExit(main())
