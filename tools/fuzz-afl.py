"""Runs the AFL++ fuzz targets in Docker, one container per target instance, on any machine with Docker.

Each container copies the sources from the repository (mounted read-only), publishes tests/Egap.Pdf.Fuzz, instruments
Egap.Pdf.dll with SharpFuzz and runs afl-fuzz on the target's seeds for the given time. Findings go to .cache/afl/<target>:
crashes in <target>/<instance>/crashes and hangs in <target>/<instance>/hangs, the instance being "default" for one instance,
else "main", "secondary1" and so on.

Usage, from the repo root:
  docker build -t egap-fuzz -f tests/Egap.Pdf.Fuzz/Dockerfile tests/Egap.Pdf.Fuzz
  python tools/fuzz-afl.py 0.17 lexer          hours (0.17 is the 10-minute daytime run), then targets; "lexer:3" runs three
                                               instances sharing the target's findings
  docker logs -f egap-afl-lexer                to follow one
"""
import os
import subprocess
import sys

SEEDS = {"lexer": "tests/fuzz-seeds/lexer/*", "parser": "tests/fuzz-seeds/parser/*", "filters": "tests/fuzz-seeds/filters/*", "streams": "tests/fuzz-seeds/parser/*", "xref": "tests/fuzz-seeds/xref/*", "document": "tests/fuzz-seeds/xref/* tests/fuzz-seeds/document/*", "rewrite": "tests/fuzz-seeds/xref/* tests/fuzz-seeds/document/*", "incremental": "tests/fuzz-seeds/xref/* tests/fuzz-seeds/document/*", "trees": "tests/fuzz-seeds/trees/*", "pages": "tests/fuzz-seeds/pages/* tests/fuzz-seeds/xref/*", "pageedits": "tests/fuzz-seeds/pages/* tests/fuzz-seeds/xref/*", "navigation": "tests/fuzz-seeds/navigation/* tests/fuzz-seeds/xref/*", "outlines": "tests/fuzz-seeds/outlines/* tests/fuzz-seeds/xref/*", "xmp": "tests/fuzz-seeds/xmp/*", "metadata": "tests/fuzz-seeds/metadata/* tests/fuzz-seeds/xref/*", "attachments": "tests/fuzz-seeds/attachments/* tests/fuzz-seeds/xref/*", "layers": "tests/fuzz-seeds/layers/* tests/fuzz-seeds/xref/*", "annotations": "tests/fuzz-seeds/annotations/* tests/fuzz-seeds/xref/*", "forms": "tests/fuzz-seeds/forms/* tests/fuzz-seeds/xref/*", "flatten": "tests/fuzz-seeds/forms/* tests/fuzz-seeds/annotations/*", "merge": "tests/fuzz-seeds/merge/* tests/fuzz-seeds/xref/*", "content": "tests/fuzz-seeds/content/*", "walker": "tests/fuzz-seeds/walker/* tests/fuzz-seeds/xref/*", "fonts": "tests/fuzz-seeds/fonts/*", "images": "tests/fuzz-seeds/images/* tests/fuzz-seeds/walker/*", "functions": "tests/fuzz-seeds/functions/*", "shadings": "tests/fuzz-seeds/shadings/*", "render": "tests/fuzz-seeds/render/* tests/fuzz-seeds/walker/* tests/fuzz-seeds/images/* tests/fuzz-seeds/layers/*.pdf tests/fuzz-seeds/annotations/annotations.pdf", "signatures": "tests/fuzz-seeds/signatures/*"}

SCRIPT = """
set -e
mkdir -p /build/tests
cp -r /src/src /build/
cp -r /src/tests/Egap.Pdf.Fuzz /build/tests/
cp /src/Directory.Build.props /src/Directory.Packages.props /src/global.json /src/nuget.config /build/
cp /src/.editorconfig /build/ 2>/dev/null || true
find /build -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {{}} +
dotnet publish /build/tests/Egap.Pdf.Fuzz -c Release -o /out -p:TreatWarningsAsErrors=false > /tmp/publish.log
sharpfuzz /out/Egap.Pdf.dll
mkdir -p /seeds && cp {seeds} /seeds/
afl-fuzz -V {seconds} -i /seeds -o /findings/{target} {role} -t 5000 -m none -- /out/Egap.Pdf.Fuzz {target}
"""


def main():
    hours = float(sys.argv[1]) if len(sys.argv) > 1 else 0.17
    targets = sys.argv[2:] or list(SEEDS)
    repo = os.getcwd()
    findings = os.path.join(repo, ".cache", "afl")
    # Egami's packages come from the folder feed beside the repository (nuget.config), mounted where /build/../egami-feed is.
    feed = os.path.join(os.path.dirname(repo), "egami-feed")
    os.makedirs(findings, exist_ok=True)
    for spec in targets:
        target, _, count = spec.partition(":")
        instances = int(count or 1)
        for i in range(instances):
            name = f"egap-afl-{target}" + (f"-{i}" if instances > 1 else "")
            role = "" if instances == 1 else "-M main" if i == 0 else f"-S secondary{i}"
            subprocess.run(["docker", "rm", "-f", name], capture_output=True)
            script = SCRIPT.format(seeds=" ".join("/src/" + pattern for pattern in SEEDS[target].split()), seconds=int(hours * 3600), target=target, role=role)
            # AFL_AUTORESUME carries on from an earlier run's queue in the same findings directory.
            subprocess.run(["docker", "run", "-d", "--name", name, "-e", "AFL_AUTORESUME=1", "-v", f"{repo}:/src:ro", "-v", f"{findings}:/findings", "-v", f"{feed}:/egami-feed:ro",
                            "egap-fuzz", "bash", "-c", script], check=True)
            print(f"Started {name} for {hours} h.")


if __name__ == "__main__":
    main()
