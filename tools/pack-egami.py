"""Packs Egami, Egami.Fonts, Egami.Drawing, Egami.Metafiles and Egami.Css from an Egami commit into a local NuGet folder feed (Phase 3 design Q2).

Usage: python tools/pack-egami.py [--egami ../Egami] [--commit HEAD] [--feed ../egami-feed] [--update] [--projects Egami.Svg ...]

The commit is exported with git archive, so uncommitted changes in the Egami checkout never reach a package. Each package
keeps its project's version with the commit as a prerelease suffix (1.0.0-local.g74c5d73), so a build always names the exact
Egami it was made against. --update writes the versions into Directory.Packages.props. When Egami is on nuget.org, the feed
in nuget.config is swapped for nuget.org and the versions for released ones; nothing else changes.
"""

import argparse
import io
import pathlib
import re
import subprocess
import sys
import tarfile
import tempfile

PROJECTS = ["Egami", "Egami.Fonts", "Egami.Drawing", "Egami.Metafiles", "Egami.Css"]
ROOT = pathlib.Path(__file__).resolve().parent.parent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--egami", default=str(ROOT.parent / "Egami"))
    parser.add_argument("--commit", default="HEAD")
    parser.add_argument("--feed", default=str(ROOT.parent / "egami-feed"))
    parser.add_argument("--update", action="store_true")
    parser.add_argument("--projects", nargs="+", default=PROJECTS, help="other Egami projects, such as Egami.Svg for egap-compare")
    args = parser.parse_args()

    egami = pathlib.Path(args.egami)
    sha = subprocess.check_output(["git", "-C", str(egami), "rev-parse", "--short=7", args.commit], text=True).strip()
    suffix = f"local.g{sha}"
    feed = pathlib.Path(args.feed).resolve()
    feed.mkdir(parents=True, exist_ok=True)

    paths = ["src", "Directory.Build.props", "Directory.Packages.props", "global.json", ".editorconfig"]
    archive = subprocess.check_output(["git", "-C", str(egami), "archive", "--format=tar", args.commit, *paths])
    versions = {}
    with tempfile.TemporaryDirectory() as temp:
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
            tar.extractall(temp, filter="data")
        for project in args.projects:
            csproj = pathlib.Path(temp, "src", project, f"{project}.csproj")
            match = re.search(r"<Version>([^<]+)</Version>", csproj.read_text(encoding="utf-8"))
            versions[project] = f"{match.group(1) if match else '1.0.0'}-{suffix}"
            subprocess.check_call([
                "dotnet", "pack", str(csproj), "-c", "Release", "-o", str(feed), "--nologo", "-v", "q",
                f"-p:EgamiLocalSuffix={suffix}",
                f"-p:CustomAfterMicrosoftCommonTargets={ROOT / 'tools' / 'egami-local.targets'}",
            ])

    for project, version in versions.items():
        if not (feed / f"{project}.{version}.nupkg").exists():
            sys.exit(f"{project} {version} was not written to {feed}")
        print(f"{project} {version}")

    if args.update:
        props = ROOT / "Directory.Packages.props"
        text = props.read_text(encoding="utf-8")
        for project, version in versions.items():
            text = re.sub(rf'(<PackageVersion Include="{re.escape(project)}" Version=")[^"]+(")', rf"\g<1>{version}\g<2>", text)
        props.write_text(text, encoding="utf-8")
        print(f"Updated {props.name}")


if __name__ == "__main__":
    main()
