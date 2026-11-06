"""Writes docs/articles/guides/spreadsheet-functions.md, the report of which of Excel's functions are built in.

The report comes from FunctionRegistry.BuiltInFunctions and tests/Egap.Spreadsheets.Tests/Oracle/excel-functions.txt, and
FunctionCoverageTests fails when it is out of date.

Usage, from the repo root: python tools/update-function-coverage.py
"""
import os
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


def main() -> int:
    return subprocess.run(
        ["dotnet", "test", "tests/Egap.Spreadsheets.Tests", "-c", "Release", "--filter", "FullyQualifiedName~FunctionCoverageTests.TheReportIsUpToDate"],
        cwd=ROOT,
        env={**os.environ, "EGAP_WRITE_FUNCTION_COVERAGE": "1"},
    ).returncode


if __name__ == "__main__":
    sys.exit(main())
