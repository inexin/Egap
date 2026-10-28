# The egap command

`egap` turns HTML, Markdown, DOCX and images into PDF, and inspects, merges, splits, stamps, redacts, fills, signs and
checks PDFs from a shell, a build script or an AI assistant. It is a .NET tool built on Egap's public API, so it does what
the library does, with the same defaults. Every command is listed with its options in the
[command reference](commands.md).

```sh
dotnet tool install --global Egap.Cli
egap info report.pdf
egap convert notes.md -o notes.pdf --page-size A4 --footer "Page {page} of {total-pages}"
egap merge cover.pdf body.pdf#2-z appendix.pdf -o book.pdf
```

## Conventions

Every command follows the same rules, which are those of Egami's `egami` command.

- **Inputs.** Files, globs (`*`, `?`, `[abc]`, `**`) that egap expands itself, so they behave the same in every shell,
  folders with `--recursive`, or `-` for standard input. Several inputs run at once (`--jobs`); a file that fails is
  reported and the rest carry on, unless `--fail-fast`.
- **Outputs.** `-o` takes a file, a folder, a template such as `out/{name}.pdf` (`{name}`, `{ext}`, `{dir}`, `{index}`,
  `{page}`) or `-` for standard output. egap never writes over an input unless you give `--in-place`, and every output
  is written to a temporary file first, so a failed command leaves the old file as it was.
- **Pages.** One syntax everywhere: `1,3,5-10`, `z` for the last page, `r2` for the second to last, `10-1` backwards,
  `:odd` and `:even`, `x` to leave pages out (`1-z,x5`). A file in `merge` takes its pages after a `#`.
- **Lengths and sizes.** `12mm`, `1.5cm`, `1in` or `72pt`; page sizes `A3`, `A4`, `A5`, `Letter`, `Legal`, `Tabloid` or
  `WxH` such as `210mmx297mm`.
- **Passwords.** `--password-env NAME` reads an environment variable and `--password-file path` the first line of a file;
  with neither, egap asks on the terminal. `--password` works too, with a warning, since it lands in shell history.
- **Saving.** An edit is appended as an incremental update, so signatures stay valid where the change allows; a new
  document (convert, merge, split, impose) is written whole. `--rewrite`, `--linearize` and `--compress` override that.
  An edit that would break a signature stops unless `--allow-invalidating-signatures`. Redaction, sanitizing and PDF/A
  and PDF/X conversion always write the whole file, so no earlier revision keeps what they removed.
- **Output and exit codes.** Text on standard output, warnings and errors on standard error (`-q` and `-v` set how much).
  Report commands take `--json`, with a versioned schema in `docs/schemas`. Exit codes: 0 done, 1 a file failed or a
  check found a problem (compare, verify, pdfa check, pdfx check, preflight), 2 a mistake in the command line, 3 a
  safety limit.
- **Safety limits.** `--limit objects=2M,decoded=512MB,ratio=200,depth=64,xref=512` or `EGAP_LIMITS`; `--strict` refuses
  damaged files instead of repairing them.
- **Network.** None, except a timestamp authority and OCSP or CRL fetches while signing, and remote HTML resources with
  `--allow-network`.

## Inspecting

```sh
egap info *.pdf
egap info contract.pdf --all
egap info "scans/**/*.pdf" --json
egap text report.pdf --pages 1-3
egap text report.pdf --json -o words.json
egap render report.pdf -o thumbs/ --format png --dpi 72
egap render slides.pdf -o slide.jpg --fit 1920x1080 --pages 1
egap metadata show report.pdf --xmp --outline
egap form list application.pdf --json
egap attach list invoice.pdf
```

## Converting

`convert` picks the direction from the output's extension. HTML reads its images and style sheets from its own folder
only, unless `--allow-path` or `--allow-network` says otherwise.

```sh
egap convert page.html -o page.pdf --page-size Letter --margins 20mm --tagged
egap convert "docs/*.md" -o out/{name}.pdf --toc pages --header "|{title}|"
egap convert letter.docx -o letter.pdf --pdfa 2b
egap convert photo.jpg -o photo.pdf
egap convert report.pdf -o report.docx
egap convert report.pdf -o report.md --mode flow
egap convert draft.docx -o review.pdf --comments annotations   # comments as PDF annotations
egap convert report.docx -o report.pdf --embed-source        # a hybrid PDF: converts back to the DOCX
egap convert notes.md -o notes.html
egap convert page.html -o page.pdf --allow-network
```

## Pages

```sh
egap merge a.pdf b.pdf#2-z c.pdf#1,3 -o all.pdf
egap split book.pdf -o chapters/ --range 1-12 --range 13-40 --range 41-z
egap split scans.pdf -o "out/{name}-{page:000}.pdf" --every 2
egap pages scan.pdf --rotate 90:2-4 --delete 5 -o fixed.pdf
egap pages book.pdf --select 1,3-z --blank end --label 1:r --label 3:D -o book2.pdf
egap impose handout.pdf --nup 2x2 --sheet A4 -o handout-4up.pdf
egap impose zine.pdf --booklet -o zine-booklet.pdf
egap stamp report.pdf --page-numbers -o numbered.pdf
egap stamp draft.pdf --watermark DRAFT --opacity 0.2 -o watermarked.pdf
egap stamp "evidence/*.pdf" --bates ACME- --bates-digits 6 -o stamped/
```

## Editing and protecting

```sh
egap metadata set report.pdf --title "Annual report" --author "Ada Lovelace" --in-place
egap metadata set report.pdf --outline outline.json -o report2.pdf
egap metadata strip report.pdf -o clean.pdf
egap optimize scan.pdf --preset ebook -o small.pdf
egap encrypt report.pdf --owner-password-env OWNER --user-password-env READER --allow print -o locked.pdf
egap decrypt locked.pdf --password-env OWNER -o open.pdf
egap redact statement.pdf --regex "\d{4} \d{4} \d{4} \d{4}" -o redacted.pdf
egap redact form.pdf --rect 1:20mm,250mm,80mm,10mm -o redacted.pdf
egap sanitize upload.pdf --keep metadata -o safe.pdf
egap form fill application.pdf --data answers.json --flatten -o filled.pdf
egap attach add report.pdf --file data.csv --relationship data -o report-with-data.pdf
egap attach extract report.pdf -o attachments/
egap invoice embed invoice.pdf --xml factur-x.xml -o e-invoice.pdf
```

## Signing and checking

`sign` always appends its signature as an incremental update. The certificate comes from a PFX file, a PEM certificate
and key, or the Windows and macOS certificate store.

```sh
egap sign contract.pdf --cert me.pfx --cert-password-env PFX_PASSWORD --reason Approved -o signed.pdf
egap sign contract.pdf --cert me.pem --key me-key.pem --level b-t --timestamp http://timestamp.example.com -o signed.pdf
egap sign contract.pdf --cert-store 3F2A9C1E5B7D4E8A9C217D4E5F6A8B90C1D2E3F4 --visible 1:20mm,250mm,60mm,20mm -o signed.pdf
egap verify signed.pdf --trust root.cer
egap verify "contracts/*.pdf" --system-trust --json
egap compare v1.pdf v2.pdf --markup changes.pdf
egap pdfa check archive.pdf
egap pdfa convert report.pdf --level 2b -o report-a.pdf
egap pdfa check report.pdf --ua
```

## Print production

```sh
egap preflight flyer.pdf --profile sheetfed-coated
egap preflight flyer.pdf --max-ink 300 --bleed 3mm --json
egap pdfx convert flyer.pdf --level x-4 --output-condition FOGRA39 -o flyer-x4.pdf
egap pdfx check flyer-x4.pdf --level x-4
egap separations flyer.pdf -o plates --coverage --json
egap color flyer.pdf --target FOGRA39 -o flyer-cmyk.pdf
```

## Scans and mail merge

`ocr` makes scans searchable through an OCR program you install, such as Tesseract: it runs the program on each page
image and adds the words as invisible text. The output is PDF/A-2b unless `--pdfa none`. Pages that already have text are
left alone; `--mode redo` replaces an earlier OCR layer and `--mode force` reads every page.

```sh
egap ocr scan.pdf --engine-command "tesseract {image} {output} -l {langs} --dpi {dpi} hocr" --lang eng -o searchable.pdf
egap ocr "scans/*.pdf" --engine-command "tesseract {image} {output} -l {langs} hocr" --lang eng+nor --mode redo -o "ocr/{name}.pdf"
egap ocr-layer scan.pdf --hocr "ocr/page-{page}.hocr" -o searchable.pdf
egap clean scan.pdf --deskew --remove-blank-pages --grayscale -o tidy.pdf
egap merge-data letter.docx --data people.json -o letters.pdf
egap merge-data letter.docx --data people.csv --each --name-field Name -o "letters/{name}.pdf"
```

## Pipelines and workflows

`run` chains steps over each input, joined by a bare `+`. Every edit command is a step with the same options, `append`
adds the pages of other files, and `save` writes; there may be several saves, and the last step must be one.
`egap run --list` lists the steps.

```sh
egap run scan.pdf pages --rotate 90:1 + clean --deskew + stamp --page-numbers + save out.pdf --linearize
egap run "in/*.pdf" --jobs 8 stamp --watermark CONFIDENTIAL + save "out/{name}.pdf" + encrypt --owner-password-env OWNER + save "locked/{name}.pdf"
egap run cover.pdf append body.pdf#2-z + metadata --title Book + save book.pdf
egap run report.pdf --dry-run pages --delete 2 + save "out/{name}.pdf"
egap run --save-workflow print.json --dry-run pdfx --level x-4 --output-condition FOGRA39 + save "print/{name}.pdf"
egap run "jobs/*.pdf" --workflow print.json
```

A workflow file holds the same steps as JSON, each step's options named without their dashes:

```json
{
  "$schema": "https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-workflow-1.json",
  "egap": 1,
  "steps": [
    { "op": "pages", "rotate": [ "90:1" ] },
    { "op": "stamp", "page-numbers": true },
    { "op": "save", "path": "out/{name}.pdf", "linearize": true }
  ]
}
```

## AI assistants

`egap mcp` serves every command as a tool over the Model Context Protocol, for Claude, GitHub Copilot, Cursor and other
assistants. Tools read and write only inside the folders the assistant opened and those given with `--allow`; sign,
encrypt and decrypt are offered only with `--allow-sign`, and passwords come only from environment variables. `ocr` is
never offered, since it runs the program the call names.

```sh
egap mcp --allow ~/Documents/pdfs
egap mcp --allow ./contracts --allow-sign
```

To add it to an assistant, register the command `egap` with the argument `mcp`, or, without installing the tool, `dnx`
with `Egap.Cli --yes -- mcp`.

## More tools

`egap <name>` runs a program called `egap-<name>` from the PATH, as git and dotnet do. `egap preview` therefore starts
[egap-preview](../guides/index.md) when the Egap.Preview tool is installed:

```sh
egap preview letter.docx
```

## JSON schemas

| Command | Schema |
| --- | --- |
| `info --json` | [egap-info-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-info-1.json) |
| `text --json` | [egap-text-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-text-1.json) |
| `metadata show --json` | [egap-metadata-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-metadata-1.json) |
| `form list --json` | [egap-form-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-form-1.json) |
| `compare --json` | [egap-compare-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-compare-1.json) |
| `verify --json` | [egap-verify-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-verify-1.json) |
| `pdfa check --json`, `pdfx check --json` | [egap-pdfa-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-pdfa-1.json) |
| `preflight --json` | [egap-preflight-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-preflight-1.json) |
| `separations --json` | [egap-separations-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-separations-1.json) |
| `run --workflow` | [egap-workflow-1.json](https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-workflow-1.json) |
