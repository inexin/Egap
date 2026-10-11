"""Brings src/Egap.Pdf/PublicAPI.Unshipped.txt in line with the code.

Adds every symbol the public API analyzer reports as undeclared (RS0016) and removes every declared one it can no longer find
(RS0017), such as a renamed or internalized member. PublicAPI.Shipped.txt holds released API and is edited by hand only at a
release.

Usage, from the repo root: python tools/update-public-api.py
"""
import re
import subprocess

PROJECT = "src/Egap.Pdf/Egap.Pdf.csproj"
API_FILE = "src/Egap.Pdf/PublicAPI.Unshipped.txt"

build = subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "--no-incremental"], capture_output=True, text=True).stdout
added = set(re.findall(r"error RS0016: Symbol '(.+?)' is not part of the declared public API", build))
gone = set(re.findall(r"error RS0017: Symbol '(.+?)' is part of the declared API, but is either not public or could not be found", build))

with open(API_FILE, encoding="utf-8") as f:
    lines = [line.rstrip("\n") for line in f if line.strip()]
header = [line for line in lines if line.startswith("#")]
entries = (set(line for line in lines if not line.startswith("#")) - gone) | added

with open(API_FILE, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(header + sorted(entries, key=str.lower)) + "\n")
print(f"Added {len(added)} symbols, removed {len(gone)}.")
