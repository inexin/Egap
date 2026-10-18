"""Compiles every C# block in docs/ against the Egap projects, so no guide shows code that does not build.

Usage: python tools/check-doc-snippets.py

Each ```csharp block becomes the body of its own method; its using directives move to the top of the file. The blocks are
compiled, not run, so they may name files that do not exist. Prints the errors and exits with 1 when a block does not build.
"""

import pathlib
import re
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
USING = re.compile(r"using [A-Za-z][A-Za-z0-9.]*;")


def main() -> int:
    usings: set[str] = set()
    methods: list[str] = []
    for page in sorted((ROOT / "docs").rglob("*.md")):
        if "_site" in page.parts:
            continue
        for index, block in enumerate(re.findall(r"```csharp\n(.*?)```", page.read_text(encoding="utf-8"), re.S)):
            body = []
            for line in block.splitlines():
                if USING.fullmatch(line.strip()):
                    usings.add(line.strip())
                else:
                    body.append("        " + line)
            name = re.sub(r"\W", "_", str(page.relative_to(ROOT))) + f"_{index}"
            methods.append(f"    // {page.relative_to(ROOT)}, block {index + 1}\n    static async Task {name}()\n    {{\n"
                           "        await Task.Yield();\n" + "\n".join(body) + "\n    }\n")

    source = "\n".join(sorted(usings)) + "\n\n#pragma warning disable CS0168, CS0219, CS1998\ninternal static class Snippets\n{\n"
    source += "\n".join(methods) + "}\n"
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        (work / "Snippets.cs").write_text(source, encoding="utf-8")
        (work / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
        (work / "Directory.Packages.props").write_text("<Project />\n", encoding="utf-8")
        (work / "Snippets.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            "  <PropertyGroup>\n"
            "    <TargetFramework>net10.0</TargetFramework>\n"
            "    <Nullable>enable</Nullable>\n"
            "    <ImplicitUsings>enable</ImplicitUsings>\n"
            "  </PropertyGroup>\n"
            "  <ItemGroup>\n"
            f'    <ProjectReference Include="{ROOT / "src" / "Egap.Pdf.Converter" / "Egap.Pdf.Converter.csproj"}" />\n'
            "  </ItemGroup>\n"
            "</Project>\n", encoding="utf-8")
        result = subprocess.run(["dotnet", "build", str(work / "Snippets.csproj"), "-nologo", "-v", "q"],
                                capture_output=True, text=True)
        errors = sorted({line for line in result.stdout.splitlines() if ": error " in line})
        if result.returncode != 0:
            lines = source.splitlines()
            for error in errors:
                match = re.search(r"Snippets\.cs\((\d+),", error)
                where = ""
                if match:
                    number = int(match.group(1))
                    where = next((l.strip() for l in reversed(lines[:number]) if l.strip().startswith("// docs")), "")
                print(f"{where}: {error.split(': error ', 1)[1].rsplit(' [', 1)[0]}")
            if not errors:
                print(result.stdout + result.stderr)
            return 1

    print(f"{len(methods)} blocks build.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
