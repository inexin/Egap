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

SEEDS = {"lexer": "tests/fuzz-seeds/lexer/*", "parser": "tests/fuzz-seeds/parser/*", "filters": "tests/fuzz-seeds/filters/*", "streams": "tests/fuzz-seeds/parser/*", "xref": "tests/fuzz-seeds/xref/*", "document": "tests/fuzz-seeds/xref/* tests/fuzz-seeds/document/*", "rewrite": "tests/fuzz-seeds/xref/* tests/fuzz-seeds/document/*", "incremental": "tests/fuzz-seeds/xref/* tests/fuzz-seeds/document/*", "trees": "tests/fuzz-seeds/trees/*", "pages": "tests/fuzz-seeds/pages/* tests/fuzz-seeds/xref/*", "pageedits": "tests/fuzz-seeds/pages/* tests/fuzz-seeds/xref/*", "navigation": "tests/fuzz-seeds/navigation/* tests/fuzz-seeds/xref/*", "outlines": "tests/fuzz-seeds/outlines/* tests/fuzz-seeds/xref/*", "xmp": "tests/fuzz-seeds/xmp/*", "metadata": "tests/fuzz-seeds/metadata/* tests/fuzz-seeds/xref/*", "attachments": "tests/fuzz-seeds/attachments/* tests/fuzz-seeds/xref/*", "layers": "tests/fuzz-seeds/layers/* tests/fuzz-seeds/xref/*", "annotations": "tests/fuzz-seeds/annotations/* tests/fuzz-seeds/xref/*", "forms": "tests/fuzz-seeds/forms/* tests/fuzz-seeds/xref/*", "flatten": "tests/fuzz-seeds/forms/* tests/fuzz-seeds/annotations/*", "merge": "tests/fuzz-seeds/merge/* tests/fuzz-seeds/xref/*", "content": "tests/fuzz-seeds/content/*", "walker": "tests/fuzz-seeds/walker/* tests/fuzz-seeds/xref/*", "fonts": "tests/fuzz-seeds/fonts/*", "images": "tests/fuzz-seeds/images/* tests/fuzz-seeds/walker/*", "functions": "tests/fuzz-seeds/functions/*", "shadings": "tests/fuzz-seeds/shadings/*", "render": "tests/fuzz-seeds/render/* tests/fuzz-seeds/walker/* tests/fuzz-seeds/images/* tests/fuzz-seeds/layers/*.pdf tests/fuzz-seeds/annotations/annotations.pdf", "signatures": "tests/fuzz-seeds/signatures/*", "cms": "tests/fuzz-seeds/cms/*", "revocation": "tests/fuzz-seeds/revocation/*", "structure": "tests/fuzz-seeds/structure/*", "pdfa": "tests/fuzz-seeds/pdfa/*", "linearize": "tests/fuzz-seeds/structure/* tests/fuzz-seeds/pdfa/*", "hocr": "tests/fuzz-seeds/hocr/*", "alto": "tests/fuzz-seeds/alto/*", "markdown": "tests/fuzz-seeds/markdown/*", "package": "tests/fuzz-seeds/package/*", "docx": "tests/fuzz-seeds/docx/*", "html": "tests/fuzz-seeds/html/*", "layout": "tests/fuzz-seeds/html/* tests/fuzz-seeds/docx/own-*", "pdflayout": "tests/fuzz-seeds/layout/*", "optimize": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/images/*", "impose": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/pages/*", "stamp": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/pages/*", "compare": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/walker/*", "cleanup": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/shadings/* tests/fuzz-seeds/render/*", "findtext": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/walker/*", "newfields": "tests/fuzz-seeds/tools/* tests/fuzz-seeds/forms/*", "invoice": "tests/fuzz-seeds/invoice/* tests/fuzz-seeds/attachments/*", "pagemedia": "tests/fuzz-seeds/pagemedia/* tests/fuzz-seeds/html/own-basic.html", "mailmerge": "tests/fuzz-seeds/mailmerge/* tests/fuzz-seeds/docx/own-fields.xml"}

# Targets of Egap.Documents.Fuzz; the others are Egap.Pdf.Fuzz's. "layout" is Egap.Documents' page layout, "pdflayout"
# Egap.Pdf.Conversion's analysis of PDF pages.
DOCUMENTS = {"markdown", "package", "docx", "html", "layout", "pagemedia", "mailmerge"}

# Libraries SharpFuzz instruments for a target, when not just the harness's own library.
LIBRARIES = {"pdflayout": ["Egap.Pdf", "Egap.Pdf.Conversion"]}

SCRIPT = """
set -e
mkdir -p /build/tests
cp -r /src/src /build/
cp -r /src/tests/{harness} /build/tests/
cp /src/Directory.Build.props /src/Directory.Packages.props /src/global.json /src/nuget.config /build/
cp /src/.editorconfig /build/ 2>/dev/null || true
find /build -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {{}} +
dotnet publish /build/tests/{harness} -c Release -o /out -p:TreatWarningsAsErrors=false > /tmp/publish.log
{instrument}
mkdir -p /seeds && cp {seeds} /seeds/
afl-fuzz -V {seconds} -i /seeds -o /findings/{target} {role} -t 5000 -m none -- /out/{harness} {target}
"""


def main():
    hours = float(sys.argv[1]) if len(sys.argv) > 1 else 0.17
    targets = sys.argv[2:] or list(SEEDS)
    repo = os.getcwd()
    findings = os.path.join(repo, ".cache", "afl")
    os.makedirs(findings, exist_ok=True)
    for spec in targets:
        target, _, count = spec.partition(":")
        instances = int(count or 1)
        for i in range(instances):
            name = f"egap-afl-{target}" + (f"-{i}" if instances > 1 else "")
            role = "" if instances == 1 else "-M main" if i == 0 else f"-S secondary{i}"
            subprocess.run(["docker", "rm", "-f", name], capture_output=True)
            harness = "Egap.Documents.Fuzz" if target in DOCUMENTS else "Egap.Pdf.Fuzz"
            instrument = "\n".join(f"sharpfuzz /out/{library}.dll" for library in LIBRARIES.get(target, [harness[:-5]]))
            script = SCRIPT.format(harness=harness, instrument=instrument, seeds=" ".join("/src/" + pattern for pattern in SEEDS[target].split()), seconds=int(hours * 3600), target=target, role=role)
            # AFL_AUTORESUME carries on from an earlier run's queue in the same findings directory.
            subprocess.run(["docker", "run", "-d", "--name", name, "-e", "AFL_AUTORESUME=1", "-v", f"{repo}:/src:ro", "-v", f"{findings}:/findings",
                            "egap-fuzz", "bash", "-c", script], check=True)
            print(f"Started {name} for {hours} h.")


if __name__ == "__main__":
    main()
