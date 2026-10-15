"""Brings each src project's PublicAPI.Unshipped.txt in line with its code.

Adds every symbol the public API analyzer reports as undeclared (RS0016) and removes every declared one it can no longer find
(RS0017), such as a renamed or internalized member, in the project the analyzer reports it for. PublicAPI.Shipped.txt holds
released API and is edited by hand only at a release.

Usage, from the repo root: python tools/update-public-api.py
"""
import pathlib
import re
import subprocess

PROJECTS = sorted(pathlib.Path("src").glob("*/*.csproj"))

for project in PROJECTS:
    api_file = project.parent / "PublicAPI.Unshipped.txt"
    build = subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--no-incremental"], capture_output=True, text=True).stdout
    # Only this project's own diagnostics: a referenced project's errors end with that project's path.
    mine = "\n".join(line for line in build.splitlines() if line.rstrip().lower().endswith(f"{project.name.lower()}]"))
    added = set(re.findall(r"error RS0016: Symbol '(.+?)' is not part of the declared public API", mine))
    gone = set(re.findall(r"error RS0017: Symbol '(.+?)' is part of the declared API, but is either not public or could not be found", mine))

    with open(api_file, encoding="utf-8") as f:
        lines = [line.rstrip("\n") for line in f if line.strip()]
    header = [line for line in lines if line.startswith("#")]
    entries = (set(line for line in lines if not line.startswith("#")) - gone) | added

    with open(api_file, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(header + sorted(entries, key=str.lower)) + "\n")
    print(f"{project.parent.name}: added {len(added)} symbols, removed {len(gone)}.")
