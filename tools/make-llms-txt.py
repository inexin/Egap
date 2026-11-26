"""Writes docs/llms.txt (https://llmstxt.org) from the DocFX articles: a short summary of Egap, then every article in
docs/articles/toc.yml with its first sentence, linked on the docs site. The docs build publishes it next to the pages.

Also writes the agent guides: docs/agents/<package>.md for each tools/agent-guides/<package>.md, with every
{{snippet guides/page.md#Heading}} line replaced by the first C# block under that heading of docs/articles/guides/page.md
(any page under docs/articles works), and {{shared}} by tools/agent-guides/shared.md. The samples are blocks that
tools/check-doc-snippets.py builds, so a guide shows no untested code. Each package packs its guide as AGENTS.md
(Directory.Build.targets), so coding agents find it in the NuGet package, and the docs build publishes them too.

Usage: python tools/make-llms-txt.py          (from the repo root; rerun after changing an article, a guide or a sample)
       python tools/make-llms-txt.py --check  (exit 1 when docs/llms.txt or an agent guide is out of date)
"""
import glob
import os
import re
import sys

SITE = "https://inexin.github.io/Egap/v2/"
ARTICLES = "docs/articles"
TOC = "docs/articles/toc.yml"
OUT = "docs/llms.txt"
GUIDES = "tools/agent-guides"
GUIDES_OUT = "docs/agents"

# Articles whose first sentence says too little on its own.
DESCRIPTIONS = {
    "getting-started.md": "Install the packages, open, edit and save a first PDF, render a page and turn a Word file into PDF.",
    "guides/pdf-tools.md": "Optimize, stamps and Bates numbers, N-up and booklets, page ranges and lengths, compare, and scan clean-up.",
    "guides/print-production.md": "Preflight, print profiles, ink separations and coverage, colour conversion to CMYK, PDF/X and overprint"
                                  " preview.",
    "guides/signatures.md": "PAdES signatures from B-B to B-LTA with .NET's own cryptography, timestamps, smart cards and HSMs, and"
                            " verifying signatures.",
}

SUMMARY = """# Egap

> Egap is an MIT-licensed, fully managed family of document libraries for .NET 10, with no native code: PDF files, flow
> documents in DOCX, DOC, RTF, ODT, HTML, EPUB and Markdown, and Excel workbooks. It draws, decodes images and reads fonts
> through Egami. The packages are Egap.Pdf (read, edit, render, sign, redact and save PDF; PDF/A, optimize, stamps,
> compare), Egap.Documents (one document model, page layout, PDF export and PDF back to documents), Egap.Documents.Html
> (HTML, MHTML and EPUB), Egap.Documents.Markdown (Markdown), Egap.Spreadsheets (workbooks, formulas, Excel files),
> Egap.Cli (the `egap` command and MCP server) and Egap.Preview (the `egap-preview` live preview tool).

Egap's own names: `PdfDocument` (`PdfDocument.Open`, `Pages`, `Save`) is a PDF file, `Document` (`Document.Load`, `Save`,
`DocumentBuilder`, `SaveAsPdf`) a flow document in any format, `Workbook` and `Worksheet` (`sheet["B2"]` is a `Cell`) a
workbook, and `PdfConvert` turns HTML, Markdown, DOCX and images into PDF in one call. Options are objects such as
`PdfReadOptions`, `PdfExportOptions` and `WorkbookLoadOptions`; damaged input is repaired and reported as warnings.
"""


def toc_entries():
    """Yields (section, name, href) in the order of the table of contents."""
    section, name = "Docs", None
    for line in open(TOC, encoding="utf-8"):
        m = re.match(r"^(\s*)- name: (.+)$", line)
        if m:
            name = m.group(2).strip().strip("\"")
            if m.group(1) == "":
                section = "Docs"
            continue
        m = re.match(r"^\s*items:\s*$", line)
        if m and name:
            section = name
            continue
        m = re.match(r"^\s*href: (.+\.md)\s*$", line)
        if m and name:
            yield section, name, m.group(1).strip()


def first_sentence(path):
    """The first sentence of the article's first paragraph of prose, links reduced to their text."""
    text, started, fenced = [], False, False
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if not started:
            started = line.startswith("# ")
            continue
        if line.lstrip().startswith("```"):
            fenced = not fenced
            if text:
                break
            continue
        if fenced:
            continue
        if not line.strip():
            if text:
                break
            continue
        if line.startswith((" ", "\t")) and not text:
            continue
        if line.lstrip().startswith(("[!", ">", "|", "#", "- ", "<")):
            if text:
                break
            continue
        text.append(line.strip())
    prose = re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", " ".join(text))
    m = re.match(r"(.+?[.:])(\s|$)", prose)
    sentence = (m.group(1) if m else prose).rstrip(":.") + "."
    if len(sentence) > 200:
        cut = sentence[:200]
        sentence = cut[: cut.rfind(" ")].rstrip(",;") + " …"
    return sentence


def render():
    sections = {}
    for section, name, href in toc_entries():
        url = SITE + "articles/" + href[:-3] + ".html"
        about = DESCRIPTIONS.get(href) or first_sentence(os.path.join(ARTICLES, href))
        sections.setdefault(section, []).append(f"- [{name}]({url}): {about}")
    out = [SUMMARY]
    for section, items in sections.items():
        out.append(f"\n## {section}\n")
        out.extend(items)
    out.append("\n## Optional\n")
    out.append(f"- [API reference]({SITE}api/Egap.Pdf.html): every public type and member of every package.")
    for package in guide_packages():
        out.append(f"- [{package} agent guide]({SITE}agents/{package}.md): the names, entry points, samples and common mistakes"
                   f" of {package}, as packed in its NuGet package as AGENTS.md.")
    out.append("- [Source and changelog](https://github.com/inexin/Egap): the repository, CHANGELOG.md and CONTRIBUTING.md.")
    return "\n".join(out) + "\n"


def guide_packages():
    """The packages with an agent guide, Egap.Pdf first."""
    names = [os.path.basename(p)[:-3] for p in glob.glob(f"{GUIDES}/*.md") if os.path.basename(p) != "shared.md"]
    return sorted(names, key=lambda n: (n != "Egap.Pdf", n))


def snippet(reference, guide):
    """The first ```csharp block under the heading, before the next heading, in a page under docs/articles."""
    page, _, heading = reference.partition("#")
    path = os.path.normpath(os.path.join(ARTICLES, page))
    if not heading or not path.startswith(os.path.normpath(ARTICLES) + os.sep) or not os.path.isfile(path):
        sys.exit(f"{guide}: {{{{snippet {reference}}}}} names no page under {ARTICLES} and heading")
    under, fence, block = False, None, None
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if fence is None:
            if re.match(r"#{1,6} ", line):
                if under:
                    break
                under = line.lstrip("#").strip() == heading
            elif line.startswith("```"):
                fence = line[3:].strip()
                if under and fence == "csharp":
                    block = []
        elif line.startswith("```"):
            if block is not None:
                return "```csharp\n" + "\n".join(block) + "\n```"
            fence = None
        elif block is not None:
            block.append(line)
    sys.exit(f"{guide}: no ```csharp block under the heading '{heading}' in {path}")


def render_guide(package):
    shared = open(f"{GUIDES}/shared.md", encoding="utf-8").read().strip()
    source = f"{GUIDES}/{package}.md"
    out = [f"<!-- Generated by tools/make-llms-txt.py from {source} and the C# samples in docs/articles. -->"]
    for line in open(source, encoding="utf-8"):
        line = line.rstrip("\n")
        m = re.fullmatch(r"\{\{snippet (.+)\}\}", line.strip())
        if m:
            out.append(snippet(m.group(1), source))
        elif line.strip() == "{{shared}}":
            out.append(shared)
        elif "{{" in line:
            sys.exit(f"{source}: unknown placeholder in: {line.strip()}")
        else:
            out.append(line)
    return "\n".join(out).rstrip() + "\n"


def outputs():
    """Every generated file and its text."""
    files = {OUT: render()}
    for package in guide_packages():
        files[f"{GUIDES_OUT}/{package}.md"] = render_guide(package)
    return files


def main():
    files = outputs()
    stale = [p.replace(os.sep, "/") for p in glob.glob(f"{GUIDES_OUT}/*.md")]
    stale = [p for p in stale if p not in files]
    if "--check" in sys.argv:
        bad = [p for p, text in files.items()
               if (open(p, encoding="utf-8").read() if os.path.exists(p) else "") != text]
        for path in bad + stale:
            print(f"{path} is out of date: run python tools/make-llms-txt.py")
        if bad or stale:
            sys.exit(1)
        print(f"{OUT} and {len(files) - 1} agent guides are up to date")
        return
    os.makedirs(GUIDES_OUT, exist_ok=True)
    for path in stale:
        os.remove(path)
    for path, text in files.items():
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    print(f"wrote {OUT} and {len(files) - 1} agent guides")


if __name__ == "__main__":
    main()
