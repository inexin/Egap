# egap command reference

Every command, its arguments and its options, as `egap <command> --help` shows them. This page is written from the
command tree by the tests, so it always matches the tool. The [guide](index.md) explains the conventions all commands
share. Options every command takes: `--help`, `--verbose`, `--debug`, `--quiet`, `--limit`, `--strict`.

## egap info

`egap info <inputs>... [options]`

Show what PDFs are: version, pages and sizes, encryption, signatures, PDF/A claim, forms and attachments; with --all, much more.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |
| `--all`, `-a` | Also list the metadata, fonts, page sizes, layers, attachments, signatures and revisions. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-info-1.json) instead of text. |

## egap convert

`egap convert <inputs>... [options]`

Convert between formats by the output's extension: HTML, Markdown, DOCX, text and images to PDF; PDF to DOCX, HTML, Markdown or text; and the flow formats to each other.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | HTML, Markdown, DOCX, text, image or PDF files, globs (\*, ?, \*\*), or folders with --recursive. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--to <format>` | The output format when -o does not say: pdf, docx, html, md, txt. |
| `--page-size <size>` | To PDF: the page size, A3, A4, A5, Letter, Legal, Tabloid or WxH such as 210mmx297mm. |
| `--landscape` | To PDF: turn the page size to landscape. |
| `--margins <lengths>` | To PDF: page margins, one length for all sides, two (top and bottom, then the sides) or four (top, right, bottom, left), such as 2cm or 20mm,15mm. |
| `--header <text>` | To PDF: a text header, left\|center\|right, with {page}, {total-pages}, {label}, {date}, {time}, {title} and {file}. |
| `--footer <text>` | To PDF: a text footer, as --header. |
| `--header-html <html>` | To PDF: an HTML header styled with the document's own CSS. |
| `--footer-html <html>` | To PDF: an HTML footer, as --header-html. |
| `--toc <pages|no-pages>` | To PDF: a table of contents at the start, with page numbers (pages) or without (no-pages). |
| `--pdfa <level>` | To PDF: write PDF/A, one of 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 and 4f. |
| `--tagged` | To PDF: write a tagged PDF with a structure tree, for accessibility. |
| `--set <key=value>` | To PDF, any export setting as key=value: language, links, named-destinations, outline (none, headings, bookmarks), form-fields, ua. |
| `--base-dir <folder>` | HTML and Markdown: the folder images and style sheets are read from; the input's folder by default. |
| `--allow-path <folder>` | HTML: a further folder resources may come from; give it once per folder. |
| `--allow-network` | HTML: fetch http and https resources (10 s and 50 MB each at most). |
| `--mode <flow|exact>` | From PDF: flow (reflowing text, the default) or exact (every line where it stands). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted PDF inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted PDF inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted PDF inputs from the first line of this file. |

## egap text

`egap text <inputs>... [options]`

Write the text of PDF pages: to standard output, or with -o to a file per input. Pages are separated by a form feed.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--pages <range>`, `-p` | The pages to read, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--order <position|content>` | position (reading order, the default) or content (the order the page draws text in). |
| `--json` | Write lines and words with their boxes in points as JSON (schema 1, docs/schemas/egap-text-1.json). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap render

`egap render <inputs>... [options]`

Render PDF pages to images, one file per page, named by a template such as out/{name}-{page}.png.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--pages <range>`, `-p` | The pages to render, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--dpi <dpi>` | Dots per inch; 150 by default. |
| `--fit <size>` | Scale each page to fit a box in pixels instead of --dpi: W, xH or WxH. |
| `--format <format>`, `-f` | The image format when -o does not say: png, jpg, webp, tif. |
| `--transparent` | Leave the page background transparent instead of white (PNG, WebP, TIFF). |
| `--box <box>` | The page box to render: crop (the default), media, bleed, trim or art. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap merge

`egap merge <inputs>... [options]`

Join PDFs into one, in order, each with its own pages: a.pdf b.pdf#2-z c.pdf#1,3 -o all.pdf.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files or globs, in order, each optionally with its pages after a #, such as cover.pdf body.pdf#2-z appendix.pdf#1-3. |
| `--output <path>`, `-o` | The file to write, or - for standard output. |
| `--join-fields` | Join form fields of the same name into one field instead of renaming them apart. |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap split

`egap split <inputs>... [options]`

Split PDFs into one file per page, per range, or every N pages, named by a template such as out/{name}-{page}.pdf.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--range <range>` | A part of its own, such as 1-3 or 4-z:odd; give it once per part. Without --range or --every, each page is a part. |
| `--every <count>` | Parts of this many pages each, the last part taking what is left. |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap pages

`egap pages <inputs>... [options]`

Select, reorder, rotate, delete and insert pages, and set page labels, applied in that order.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--select <range>` | Keep these pages in this order, such as 3,1,2 or 1-z,x5; a page named twice is copied. |
| `--delete <range>` | Remove these pages, such as 1 or 4-6. |
| `--rotate <angle[:range]>` | Turn pages by 90, 180 or 270 degrees (or -90), all of them or those after a colon: 90 or 90:2-4; give it more than once. |
| `--blank <page|end>` | Insert a blank page, the size of its neighbour, before this page number, or at the end with end; give it more than once. |
| `--label <label>` | Number pages from a page on: page:style[:prefix[:first]], style D (1, 2), r (i, ii), R (I, II), a, A or none, such as 1:r or 5:D::1; give it more than once. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap encrypt

`egap encrypt <inputs>... [options]`

Encrypt with AES-256: a user password to open the file, an owner password for full rights, and permissions.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--user-password <text>` | The password that opens the file (empty lets anyone open it). Prefer --user-password-env or --user-password-file: a value here lands in shell history. |
| `--user-password-env <NAME>` | Read the password that opens the file (empty lets anyone open it) from this environment variable. |
| `--user-password-file <path>` | Read the password that opens the file (empty lets anyone open it) from the first line of this file. |
| `--owner-password <text>` | The password that unlocks every permission. Prefer --owner-password-env or --owner-password-file: a value here lands in shell history. |
| `--owner-password-env <NAME>` | Read the password that unlocks every permission from this environment variable. |
| `--owner-password-file <path>` | Read the password that unlocks every permission from the first line of this file. |
| `--allow <permissions>` | What someone without the owner password may do: all (the default), none, or a list of print, print-high-quality, modify, assemble, copy-content, extract-for-accessibility, annotate, fill-forms. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap decrypt

`egap decrypt <inputs>... [options]`

Remove the encryption, given the owner password (or the user password when the file allows changes).

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap metadata

Show, set or strip document information, XMP and the outline (bookmarks).

## egap metadata show

`egap metadata show <inputs>... [options]`

Show the document information, and with --xmp and --outline the XMP packet and the bookmarks.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-metadata-1.json) instead of text. |
| `--xmp` | Also show the XMP packet. |
| `--outline` | Also show the outline (bookmarks). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap metadata set

`egap metadata set <inputs>... [options]`

Set document information fields, the XMP packet or the outline (bookmarks).

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--title <text>` | Set the title; an empty value removes it. |
| `--author <text>` | Set the author; an empty value removes it. |
| `--subject <text>` | Set the subject; an empty value removes it. |
| `--keywords <text>` | Set the keywords; an empty value removes it. |
| `--creator <text>` | Set the creator; an empty value removes it. |
| `--producer <text>` | Set the producer; an empty value removes it. |
| `--created <date>` | Set the creation date, ISO 8601 such as 2026-10-22T09:30:00Z. |
| `--modified <date>` | Set the modification date, ISO 8601; without it a change sets it to now. |
| `--xmp <file>` | Replace the XMP packet with this XML file. |
| `--outline <file>` | Replace the outline (bookmarks) with this JSON file, as metadata show --outline --json writes it. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap metadata strip

`egap metadata strip <inputs>... [options]`

Remove the document information and every XMP packet, the document's and each object's.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap optimize

`egap optimize <inputs>... [options]`

Make PDFs smaller: a preset (lossless, print, ebook, screen) and its settings, such as --image-resolution 150.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--preset <preset>` | lossless (the default), print, ebook or screen. |
| `--image-resolution <ppi>` | Downsample images above this many pixels per inch. |
| `--downsample-threshold <factor>` | Downsample only images more than this many times the target resolution, such as 1.5. |
| `--jpeg-quality <quality>` | JPEG quality for recompressed images, 1 to 100. |
| `--lossy-images` | Allow lossy recompression of images (true or false). |
| `--optimize-images` | Recompress and downsample images (true or false). |
| `--subset-fonts` | Keep only the glyphs used of embedded fonts (true or false). |
| `--merge-duplicates` | Store identical images, fonts and other streams once (true or false). |
| `--remove-thumbnails` | Remove page thumbnails (true or false). |
| `--remove-private-data` | Remove private application data (true or false). |
| `--remove-empty-content` | Remove content streams that draw nothing (true or false). |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap impose

`egap impose <inputs>... [options]`

Lay pages out on sheets: --nup 2x1, --booklet, --poster A4, --grid 3 or --fit A4.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--nup <columns x rows>` | Several pages on each sheet, CxR such as 2x1 or 2x2. |
| `--booklet` | Fold-and-staple booklet order, two pages on each side of a sheet. |
| `--poster <size>` | Spread each page over tiles of this size, such as A4. |
| `--grid <columns>` | Pages in a grid of this many columns on one tall sheet. |
| `--fit <size>` | Scale every page to fit this size, such as A4 or Letter. |
| `--sheet <size>` | The sheet size for --nup and --booklet, such as A3; by default the pages' size. |
| `--margin <length>` | The margin around the sheet, such as 10mm. |
| `--gutter <length>` | The space between pages, such as 5mm. |
| `--border` | Draw a thin border around each page. |
| `--order <order>` | The order on a sheet: across (the default), across-rtl, down or down-rtl. |
| `--auto-rotate` | Turn pages to make the best use of the sheet. |
| `--binding <edge>` | The booklet's binding edge: left (the default) or right. |
| `--sheets-per-signature <count>` | Sheets folded together per booklet signature; 0 for one signature. |
| `--overlap <length>` | How far poster tiles overlap, such as 10mm. |
| `--scale <factor>` | How much to enlarge a poster, such as 2. |
| `--cut-marks` | Draw cut marks on poster tiles. |
| `--labels` | Label each poster tile with its row and column. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap stamp

`egap stamp <inputs>... [options]`

Stamp pages with text, page numbers, a watermark, an image, a barcode or Bates numbers; or --remove earlier stamps.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--text <template>` | Text with {page}, {total-pages}, {label}, {date}, {time}, {title} and {file}, such as "Page {page} of {total-pages}". |
| `--page-numbers` | "Page {page} of {total-pages}" at the bottom centre. |
| `--watermark <text>` | Large faint text across the middle of each page, such as DRAFT. |
| `--image <file>` | An image file (PNG, JPEG, SVG and the other formats Egami reads); give --width. |
| `--barcode <format:data>` | A barcode, format:data such as qr:https://example.com or code128:A-123; give --width. |
| `--bates <prefix>` | Bates numbers with this prefix, such as ACME-; numbering goes on across the files of a batch, one file at a time in input order. |
| `--bates-digits <count>` | Digits in a Bates number; 6 by default. |
| `--bates-start <number>` | The first Bates number; 1 by default. |
| `--remove` | Remove the stamps egap and Egap added before. |
| `--anchor <place>` | Where on the page: top-left, top-center, top-right, middle-left, center, middle-right, bottom-left, bottom-center or bottom-right. |
| `--offset <x,y>` | Move from the anchor, x right and y up, such as 10mm,15mm. |
| `--width <length>` | The image's or barcode's width, such as 40mm. |
| `--height <length>` | The image's or barcode's height; by default from its shape. |
| `--font-size <points>` | Text size in points. |
| `--color <color>` | Text color, such as #c00 or #336699. |
| `--opacity <0-1>` | From 0 (invisible) to 1 (solid). |
| `--rotation <degrees>` | Degrees, counterclockwise. |
| `--pages <range>`, `-p` | The pages to stamp, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--under` | Put the stamp under the page's content instead of over it. |
| `--layer <name>` | Put the stamp on a layer of this name, which viewers can hide. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap compare

`egap compare <old> <new> [options]`

Show what changed between two PDFs, in text and in how pages look; exit code 1 when they differ.

| Argument or option | What it does |
| --- | --- |
| `<old>` | The earlier PDF. |
| `<new>` | The later PDF. |
| `--text-only` | Compare the text only. |
| `--visual-only` | Compare how the pages look only. |
| `--dpi <dpi>` | The resolution pages are compared at; 72 by default. |
| `--tolerance <level>` | How much a pixel's channels may differ and still count as the same, 0 to 255. |
| `--min-changed-pixels <count>` | The fewest changed pixels that make a page differ. |
| `--markup <file>` | Write the new PDF with the changes marked to this file. |
| `--diff-images <folder>` | Write a difference image per changed page into this folder. |
| `--json` | Write the changes as JSON (schema 1, docs/schemas/egap-compare-1.json). |
| `--password <text>` | The password that opens the old file (and the new one, unless --new-password is given). Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens the old file (and the new one, unless --new-password is given) from this environment variable. |
| `--password-file <path>` | Read the password that opens the old file (and the new one, unless --new-password is given) from the first line of this file. |
| `--new-password <text>` | The password that opens the new file. Prefer --new-password-env or --new-password-file: a value here lands in shell history. |
| `--new-password-env <NAME>` | Read the password that opens the new file from this environment variable. |
| `--new-password-file <path>` | Read the password that opens the new file from the first line of this file. |

## egap redact

`egap redact <inputs>... [options]`

Remove text by phrase or pattern, and areas by rectangle, for real; the file is written whole.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--text <phrase>` | A phrase to remove; give it more than once. |
| `--regex <pattern>` | A .NET regular expression to remove, such as \d{3}-\d{2}-\d{4}; give it more than once. |
| `--rect <page:x,y,w,h>` | An area to remove, page:left,bottom,width,height in lengths from the page's lower left corner, such as 1:20mm,250mm,80mm,10mm; give it more than once. |
| `--match-case` | Match --text with the case as written. |
| `--whole-word` | Match --text only as whole words. |
| `--pages <range>`, `-p` | The pages to search, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--mark-only` | Only mark the areas with redaction annotations, for review; remove nothing yet. |
| `--images <how>` | Image parts under an area: pixels (blacked out, the default), remove (the whole image) or none. |
| `--paths <how>` | Vector paths: covered (removed when fully covered, the default), touched (removed when touched) or none. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap sanitize

`egap sanitize <inputs>... [options]`

Strip hidden data: JavaScript and actions, attachments, metadata, hidden layers, comments, form data, thumbnails and private data.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--keep <kinds>` | What to leave in, a list of metadata, javascript, attachments, hidden-layers, comments, form-data, thumbnails, private-data. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap form

List, fill, flatten and reset the form fields of PDFs.

## egap form list

`egap form list <inputs>... [options]`

List the form fields: name, kind, value, choices, and whether each is read-only or required.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-form-1.json); its values can be edited and given to form fill. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap form fill

`egap form fill <inputs>... [options]`

Fill form fields from a JSON file or --set name=value; --flatten fixes the values into the page.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--data <file>` | A JSON file of values: { "name": "Ada", "subscribe": true, "colours": [ "red" ] }, or the output of form list --json. |
| `--set <name=value>` | One value, name=value; give it more than once. |
| `--flatten` | Flatten the form after filling it, so the values become page content. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap form flatten

`egap form flatten <inputs>... [options]`

Draw form fields into the page and remove them from the form, so the values can no longer change.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--field <name>` | Flatten only this field; give it more than once. All fields by default. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap form reset

`egap form reset <inputs>... [options]`

Set form fields back to their default values.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--field <name>` | Reset only this field; give it more than once. All fields by default. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap attach

List, add, extract and remove the files embedded in PDFs.

## egap attach list

`egap attach list <inputs>... [options]`

List the embedded files: name, size, type, relationship and description.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap attach add

`egap attach add <inputs>... [options]`

Embed files in PDFs; --relationship makes them PDF/A-3 associated files.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--file <path>` | A file to embed; give it more than once. |
| `--name <name>` | The name to embed a single file under; its own name by default. |
| `--description <text>` | A description shown by viewers. |
| `--mime-type <type>` | The file's media type, such as text/csv. |
| `--relationship <kind>` | Make it a PDF/A-3 associated file: source, data, alternative, supplement, encrypted-payload, form-data, schema or unspecified. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap attach extract

`egap attach extract <inputs>... [options]`

Write embedded files out, each under its own name, byte for byte.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <folder>`, `-o` | The folder to write into; with several inputs, a template such as out/{name}/. |
| `--name <name>` | Extract only this attachment; give it more than once. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap attach remove

`egap attach remove <inputs>... [options]`

Remove embedded files by name.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--name <name>` | The attachment to remove; give it more than once. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap invoice

Embed or read a ZUGFeRD or Factur-X e-invoice: the XML inside a PDF/A-3.

## egap invoice embed

`egap invoice embed <inputs>... [options]`

Embed a ZUGFeRD or Factur-X e-invoice XML and the PDF/A-3 metadata it needs.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--xml <file>` | The invoice XML (Cross Industry Invoice). |
| `--standard <standard>` | factur-x (the default), zugferd20 or zugferd10. |
| `--profile <profile>` | minimum, basic-wl, basic, en16931, extended or xrechnung; read from the XML by default. |
| `--pdfa <level>` | The PDF/A-3 level to claim: 3b (the default), 3u or 3a. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap invoice read

`egap invoice read <input> [options]`

Write the e-invoice XML a PDF carries, and say which standard and profile it follows.

| Argument or option | What it does |
| --- | --- |
| `<input>` | The PDF. |
| `--output <file>`, `-o` | Write the XML to this file instead of standard output. |
| `--password <text>` | The password that opens an encrypted input. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens an encrypted input from this environment variable. |
| `--password-file <path>` | Read the password that opens an encrypted input from the first line of this file. |

## egap sign

`egap sign <inputs>... [options]`

Sign PDFs: PAdES B-B to B-LTA from a PFX, a PEM certificate and key, or the certificate store; always an incremental update.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--cert <file>` | The certificate: a PFX (.pfx, .p12) with its key, or a PEM certificate with --key. |
| `--key <file>` | The PEM private key that goes with a PEM --cert. |
| `--cert-store <thumbprint>` | Sign with the certificate of this SHA-1 thumbprint from the user's store (Windows and macOS). |
| `--chain <file>` | A PEM or DER certificate to include in the chain; give it more than once. |
| `--level <level>` | PAdES level: b-b (the default), b-t (with a timestamp), b-lt (plus revocation data) or b-lta (plus an archive timestamp). |
| `--timestamp <url>` | The RFC 3161 timestamp authority's URL, needed for b-t, b-lt and b-lta. |
| `--reason <text>` | Why it was signed. |
| `--location <text>` | Where it was signed. |
| `--contact <text>` | How to reach the signer. |
| `--name <text>` | The signer's name as shown; the certificate's by default. |
| `--field <name>` | The signature field to sign; a new invisible one, Signature<n>, by default. |
| `--visible <page:x,y,w,h>` | Show the signature: page:x,y,width,height in lengths from the page's top left, such as 1:20mm,250mm,60mm,20mm. |
| `--image <file>` | A picture shown at the left of a visible signature, such as a scanned signature. |
| `--certify <allowed>` | Make it a certification signature that allows no-changes, form-filling or annotations afterwards. |
| `--pkcs7` | Write an adbe.pkcs7.detached signature instead of PAdES (ETSI.CAdES.detached). |
| `--digest <algorithm>` | The digest: sha256 (the default), sha384 or sha512. |
| `--rsa-pss` | Sign with RSASSA-PSS instead of PKCS #1 v1.5 (RSA keys). |
| `--allow-invalidating-signatures` | Sign even when the change breaks an earlier signature's permissions. |
| `--cert-password <text>` | The password of the PFX file or the encrypted PEM key. Prefer --cert-password-env or --cert-password-file: a value here lands in shell history. |
| `--cert-password-env <NAME>` | Read the password of the PFX file or the encrypted PEM key from this environment variable. |
| `--cert-password-file <path>` | Read the password of the PFX file or the encrypted PEM key from the first line of this file. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap verify

`egap verify <inputs>... [options]`

Check every signature of PDFs: integrity, chain, timestamp, revocation and what changed after it; exit code 1 when one fails or a file has none.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--trust <file>` | A root certificate to trust, PEM or DER; give it more than once. |
| `--system-trust` | Also trust the operating system's root certificates. |
| `--extra <file>` | A certificate that may complete a chain the file leaves out; give it more than once. |
| `--online` | Fetch OCSP responses and CRLs the file does not carry (network). |
| `--at <time>` | Check as of this time instead of now, ISO 8601. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-verify-1.json). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap pdfa

Check PDFs against PDF/A or PDF/UA, or convert them to PDF/A.

## egap pdfa check

`egap pdfa check <inputs>... [options]`

Check PDFs against PDF/A or, with --ua, PDF/UA; exit code 1 when a clause is not met.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--level <level>` | The PDF/A level to check against: 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 or 4f; the file's claim by default. |
| `--ua` | Check PDF/UA (accessibility) instead of PDF/A. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-pdfa-1.json). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap pdfa convert

`egap pdfa convert <inputs>... [options]`

Convert to PDF/A: embed fonts, add an output intent and XMP, remove what PDF/A forbids; fails the file when a clause cannot be met.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--level <level>` | 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 or 4f; the best the file can meet by default. |
| `--icc <file>` | The output intent's ICC profile; sRGB by default. |
| `--output-condition <name>` | The output condition's name, such as FOGRA39. |
| `--allow-font-substitution` | Embed a stand-in for a font the file does not embed. |
| `--system-fonts` | Look for fonts the file does not embed among the system's fonts. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap ocr

`egap ocr <inputs>... [options]`

Make scanned pages searchable: run an OCR program (Tesseract or any that writes hOCR or ALTO) on each page and add its words as invisible text; PDF/A-2b by default.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--engine-command <command>` | The OCR program and its arguments, run once per page: {image} is the page image (PNG), {output} the result file without extension, {langs} the languages, {dpi} the resolution, {page} the page from 0. It must write hOCR or ALTO, such as "tesseract {image} {output} -l {langs} --dpi {dpi} hocr". |
| `--lang <languages>`, `-l` | The languages to read, as the program names them, joined by + or commas, such as eng+nor. |
| `--mode <skip|redo|force>` | skip (the default) leaves pages with text alone; redo replaces an earlier invisible text layer; force reads every page. |
| `--pdfa <level>` | The PDF/A level the output meets: 2b by default, or 1b, 2a, 2u, 3a, 3b, 3u, 4, 4f, or none to leave it as it is. |
| `--page-jobs <count>` | How many pages of a file the program reads at once; 0 (the default) for one per processor. |
| `--dpi <dpi>` | The resolution pages are rendered at when they are not one scanned image; 300 by default. |
| `--min-confidence <fraction>` | Leave out words the program is less sure of, from 0 to 1. |
| `--deskew` | Straighten the image the program reads; the page is not changed. |
| `--rotate-pages` | Turn pages the program finds sideways or upside down. |
| `--engine-timeout <seconds>` | Seconds one run of the program may take; 300 by default. |
| `--pages <range>`, `-p` | The pages to read, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap ocr-layer

`egap ocr-layer <inputs>... [options]`

Add an invisible, searchable text layer from hOCR or ALTO files (from Tesseract, ABBYY and the like), one per page.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--hocr <template>` | hOCR files, one per page, named by a template such as scan-{page}.hocr ({page} from 1). |
| `--alto <template>` | ALTO XML files, one per page, named as --hocr. |
| `--pages <range>`, `-p` | The pages to add text to, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap clean

`egap clean <inputs>... [options]`

Tidy scans and prints: --grayscale, --remove-blank-pages, --deskew and --text-to-outlines, in that order.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--grayscale` | Turn every color to gray. |
| `--remove-blank-pages` | Remove pages with next to no ink. |
| `--blank-ink <fraction>` | The most ink, as a fraction of the page, that still counts as blank, such as 0.002. |
| `--deskew` | Straighten scanned pages that are a little crooked. |
| `--max-angle <degrees>` | The largest tilt --deskew corrects, in degrees. |
| `--text-to-outlines` | Turn text into outlines, so it prints the same everywhere and needs no fonts (it can no longer be searched). |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap merge-data

`egap merge-data <template> [options]`

Mail merge a DOCX template with JSON or CSV records, into one PDF or DOCX, or one file per record.

| Argument or option | What it does |
| --- | --- |
| `<template>` | The DOCX template, with MERGEFIELD fields and regions. |
| `--data <file>` | The records: a JSON array of objects (nested arrays fill regions) or a CSV file with a header row. |
| `--output <path>`, `-o` | The file to write (.pdf or .docx); with --each, a template such as out/letter-{index}.pdf. |
| `--each` | Write one file per record; {index} counts records from 0. |
| `--name-field <field>` | With --each, name each file by this field of its record, in place of {name}. |
| `--remove-empty-paragraphs` | Drop paragraphs left empty by fields without a value. |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |

## egap preflight

`egap preflight <inputs>... [options]`

Check PDFs for print: output intent, color spaces, image resolution, fonts, transparency, overprint, hairlines, bleed and ink coverage; exit code 1 when a check fails.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--profile <profile>` | The checks: pdf-x4 (the default), sheetfed-coated, web-offset or newspaper. |
| `--max-ink <percent>` | The most total ink coverage allowed, in percent, such as 300. |
| `--min-image-dpi <dpi>` | The lowest image resolution allowed. |
| `--min-line-width <length>` | The thinnest line allowed, such as 0.25pt. |
| `--bleed <length>` | The bleed every page needs, such as 3mm. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-preflight-1.json). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap pdfx

Check PDFs against PDF/X, or convert them to PDF/X.

## egap pdfx check

`egap pdfx check <inputs>... [options]`

Check PDFs against a PDF/X level; exit code 1 when a clause is not met.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--level <level>` | The PDF/X level: x-1a, x-3, x-4 (the default), x-4p, x-5g, x-5pg, x-5n, x-6, x-6p or x-6n. |
| `--json` | Write JSON (schema 1, docs/schemas/egap-pdfa-1.json, which pdfx shares). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap pdfx convert

`egap pdfx convert <inputs>... [options]`

Convert to PDF/X: an output intent, colors in its CMYK where the level needs it, embedded fonts, page boxes; fails the file when a clause cannot be met.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--level <level>` | The PDF/X level: x-1a, x-3, x-4 (the default), x-4p, x-5g, x-5pg, x-5n, x-6, x-6p or x-6n. |
| `--output-condition <name>` | The printing condition: FOGRA39, FOGRA51, FOGRA52, GRACoL2013, SWOP2013 or newspaper. |
| `--icc <file>` | The output intent's ICC profile, for a condition egap does not bundle. |
| `--bleed <length>` | Add a bleed box this far around the trim box, such as 3mm. |
| `--trapped` | Say the file has been trapped. |
| `--allow-font-substitution` | Embed a stand-in for a font the file does not embed. |
| `--system-fonts` | Look for fonts the file does not embed among the system's fonts. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap separations

`egap separations <inputs>... [options]`

Render each page as one gray image per ink (cyan, magenta, yellow, black and each spot color), with the total area coverage.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <folder>`, `-o` | The folder for the plates, written as <name>-<page>-<plate>.png. |
| `--pages <range>`, `-p` | The pages to separate, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--dpi <dpi>` | The resolution of the plates; 150 by default. |
| `--coverage` | Also write a heat map of the total area coverage, <name>-<page>-coverage.png. |
| `--limit-tac <percent>` | The total area coverage the heat map marks as too much, in percent; 300 by default. |
| `--no-plates` | Write no plate images, only the coverage (with --coverage or --json). |
| `--spots-to-process` | Print spot colors with process inks instead of plates of their own. |
| `--disable-spot <name>` | Leave out this spot plate; give it more than once. |
| `--json` | Write each page's coverage per plate and its total area coverage as JSON (schema 1, docs/schemas/egap-separations-1.json). |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap color

`egap color <inputs>... [options]`

Convert RGB, Lab, gray and ICC colors to the output intent's CMYK, or another CMYK; spot colors on request.

| Argument or option | What it does |
| --- | --- |
| `<inputs>` | PDF files, globs (\*, ?, \*\*) expanded the same way in every shell, folders with --recursive, or - for standard input. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs, at any depth. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 (the default) for one per processor. |
| `--fail-fast` | Stop at the first file that fails. |
| `--output <path>`, `-o` | Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output. |
| `--in-place` | Write each output over its input. |
| `--target <condition|file>` | The CMYK to convert to: an output condition (FOGRA39, GRACoL2013, ...) or an ICC file; the file's output intent by default. |
| `--intent <intent>` | relative (the default), perceptual, saturation or absolute. |
| `--black-point-compensation` | Map black to black (true or false). |
| `--black-text <how>` | Black text and thin rules: pure (100% K, the default) or colorimetric. |
| `--spots <how>` | Spot colors: keep (the default), convert, or convert-but-kept with --keep-spot. |
| `--keep-spot <name>` | A spot color to keep with --spots convert-but-kept; give it more than once. |
| `--retarget-cmyk` | Also convert CMYK that was made for another printing condition. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
| `--password <text>` | The password that opens encrypted inputs. Prefer --password-env or --password-file: a value here lands in shell history. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable. |
| `--password-file <path>` | Read the password that opens encrypted inputs from the first line of this file. |

## egap run

`egap run <inputs-and-steps>... [options]`

Run steps over PDFs, separated by +: 'egap run in.pdf pages --rotate 90:1 + metadata --title Report + save out.pdf --linearize'. Every edit command is a step ('egap run --list'); save writes, and may come more than once.

| Argument or option | What it does |
| --- | --- |
| `<inputs-and-steps>` | Inputs (files, globs, -), then steps separated by +. |
| `--workflow <file>`, `-w` | Take the steps from a JSON workflow file (egap-workflow-1.json). |
| `--save-workflow <file>` | Write the steps to a JSON workflow file, then run them unless --dry-run. |
| `--dry-run` | Print the steps and the files each input would write, and stop. |
| `--list` | List every step with its options; --list <step> shows one. |
| `--recursive`, `-r` | Take every file egap reads in folders given as inputs. |
| `--jobs <count>`, `-j` | How many files to work on at once; 0 for one per processor. |
| `--in-place` | Allow a save to replace its input. |
| `--fail-fast` | Stop at the first file that fails. |
| `--password-env <NAME>` | Read the password that opens encrypted inputs from this environment variable; --password-file and --password work too. |

## egap mcp

`egap mcp [options]`

Serve egap's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.

| Argument or option | What it does |
| --- | --- |
| `--allow <folder>` | A folder the tools may read and write, besides the folders the assistant opened; give it once per folder. With neither, the working folder. |
| `--max-inputs <count>` | The most files one tool call may take; 1000 by default. |
| `--allow-sign` | Offer the sign, encrypt and decrypt tools, and the encrypt and decrypt steps of run. |

## run steps

The steps `egap run` chains with `+`, each with the options of its command; a workflow file names them the same way,
without the dashes.

### pages

Select, reorder, rotate, delete and insert pages, and set page labels, applied in that order.

| Argument or option | What it does |
| --- | --- |
| `--select <range>` | Keep these pages in this order, such as 3,1,2 or 1-z,x5; a page named twice is copied. |
| `--delete <range>` | Remove these pages, such as 1 or 4-6. |
| `--rotate <angle[:range]>` | Turn pages by 90, 180 or 270 degrees (or -90), all of them or those after a colon: 90 or 90:2-4; give it more than once. |
| `--blank <page|end>` | Insert a blank page, the size of its neighbour, before this page number, or at the end with end; give it more than once. |
| `--label <label>` | Number pages from a page on: page:style[:prefix[:first]], style D (1, 2), r (i, ii), R (I, II), a, A or none, such as 1:r or 5:D::1; give it more than once. |

### append

Add the pages of other PDFs at the end, each with its own pages after a #, as merge takes them.

| Argument or option | What it does |
| --- | --- |
| `<files>` | PDF files or globs, each optionally with its pages after a #, such as appendix.pdf#1-3. |
| `--join-fields` | Join form fields of the same name into one field instead of renaming them apart. |

### metadata

Set document information fields, the XMP packet or the outline (bookmarks).

| Argument or option | What it does |
| --- | --- |
| `--title <text>` | Set the title; an empty value removes it. |
| `--author <text>` | Set the author; an empty value removes it. |
| `--subject <text>` | Set the subject; an empty value removes it. |
| `--keywords <text>` | Set the keywords; an empty value removes it. |
| `--creator <text>` | Set the creator; an empty value removes it. |
| `--producer <text>` | Set the producer; an empty value removes it. |
| `--created <date>` | Set the creation date, ISO 8601 such as 2026-10-22T09:30:00Z. |
| `--modified <date>` | Set the modification date, ISO 8601; without it a change sets it to now. |
| `--xmp <file>` | Replace the XMP packet with this XML file. |
| `--outline <file>` | Replace the outline (bookmarks) with this JSON file, as metadata show --outline --json writes it. |

### strip-metadata

Remove the document information and every XMP packet, the document's and each object's.

### encrypt

Encrypt with AES-256: a user password to open the file, an owner password for full rights, and permissions.

| Argument or option | What it does |
| --- | --- |
| `--user-password <text>` | The password that opens the file (empty lets anyone open it). Prefer --user-password-env or --user-password-file: a value here lands in shell history. |
| `--user-password-env <NAME>` | Read the password that opens the file (empty lets anyone open it) from this environment variable. |
| `--user-password-file <path>` | Read the password that opens the file (empty lets anyone open it) from the first line of this file. |
| `--owner-password <text>` | The password that unlocks every permission. Prefer --owner-password-env or --owner-password-file: a value here lands in shell history. |
| `--owner-password-env <NAME>` | Read the password that unlocks every permission from this environment variable. |
| `--owner-password-file <path>` | Read the password that unlocks every permission from the first line of this file. |
| `--allow <permissions>` | What someone without the owner password may do: all (the default), none, or a list of print, print-high-quality, modify, assemble, copy-content, extract-for-accessibility, annotate, fill-forms. |

### decrypt

Remove the encryption, given the owner password (or the user password when the file allows changes).

### optimize

Make PDFs smaller: a preset (lossless, print, ebook, screen) and its settings, such as --image-resolution 150.

| Argument or option | What it does |
| --- | --- |
| `--preset <preset>` | lossless (the default), print, ebook or screen. |
| `--image-resolution <ppi>` | Downsample images above this many pixels per inch. |
| `--downsample-threshold <factor>` | Downsample only images more than this many times the target resolution, such as 1.5. |
| `--jpeg-quality <quality>` | JPEG quality for recompressed images, 1 to 100. |
| `--lossy-images` | Allow lossy recompression of images (true or false). |
| `--optimize-images` | Recompress and downsample images (true or false). |
| `--subset-fonts` | Keep only the glyphs used of embedded fonts (true or false). |
| `--merge-duplicates` | Store identical images, fonts and other streams once (true or false). |
| `--remove-thumbnails` | Remove page thumbnails (true or false). |
| `--remove-private-data` | Remove private application data (true or false). |
| `--remove-empty-content` | Remove content streams that draw nothing (true or false). |

### impose

Lay pages out on sheets: --nup 2x1, --booklet, --poster A4, --grid 3 or --fit A4.

| Argument or option | What it does |
| --- | --- |
| `--nup <columns x rows>` | Several pages on each sheet, CxR such as 2x1 or 2x2. |
| `--booklet` | Fold-and-staple booklet order, two pages on each side of a sheet. |
| `--poster <size>` | Spread each page over tiles of this size, such as A4. |
| `--grid <columns>` | Pages in a grid of this many columns on one tall sheet. |
| `--fit <size>` | Scale every page to fit this size, such as A4 or Letter. |
| `--sheet <size>` | The sheet size for --nup and --booklet, such as A3; by default the pages' size. |
| `--margin <length>` | The margin around the sheet, such as 10mm. |
| `--gutter <length>` | The space between pages, such as 5mm. |
| `--border` | Draw a thin border around each page. |
| `--order <order>` | The order on a sheet: across (the default), across-rtl, down or down-rtl. |
| `--auto-rotate` | Turn pages to make the best use of the sheet. |
| `--binding <edge>` | The booklet's binding edge: left (the default) or right. |
| `--sheets-per-signature <count>` | Sheets folded together per booklet signature; 0 for one signature. |
| `--overlap <length>` | How far poster tiles overlap, such as 10mm. |
| `--scale <factor>` | How much to enlarge a poster, such as 2. |
| `--cut-marks` | Draw cut marks on poster tiles. |
| `--labels` | Label each poster tile with its row and column. |

### stamp

Stamp pages with text, page numbers, a watermark, an image, a barcode or Bates numbers; or --remove earlier stamps.

| Argument or option | What it does |
| --- | --- |
| `--text <template>` | Text with {page}, {total-pages}, {label}, {date}, {time}, {title} and {file}, such as "Page {page} of {total-pages}". |
| `--page-numbers` | "Page {page} of {total-pages}" at the bottom centre. |
| `--watermark <text>` | Large faint text across the middle of each page, such as DRAFT. |
| `--image <file>` | An image file (PNG, JPEG, SVG and the other formats Egami reads); give --width. |
| `--barcode <format:data>` | A barcode, format:data such as qr:https://example.com or code128:A-123; give --width. |
| `--bates <prefix>` | Bates numbers with this prefix, such as ACME-; numbering goes on across the files of a batch, one file at a time in input order. |
| `--bates-digits <count>` | Digits in a Bates number; 6 by default. |
| `--bates-start <number>` | The first Bates number; 1 by default. |
| `--remove` | Remove the stamps egap and Egap added before. |
| `--anchor <place>` | Where on the page: top-left, top-center, top-right, middle-left, center, middle-right, bottom-left, bottom-center or bottom-right. |
| `--offset <x,y>` | Move from the anchor, x right and y up, such as 10mm,15mm. |
| `--width <length>` | The image's or barcode's width, such as 40mm. |
| `--height <length>` | The image's or barcode's height; by default from its shape. |
| `--font-size <points>` | Text size in points. |
| `--color <color>` | Text color, such as #c00 or #336699. |
| `--opacity <0-1>` | From 0 (invisible) to 1 (solid). |
| `--rotation <degrees>` | Degrees, counterclockwise. |
| `--pages <range>`, `-p` | The pages to stamp, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--under` | Put the stamp under the page's content instead of over it. |
| `--layer <name>` | Put the stamp on a layer of this name, which viewers can hide. |

### redact

Remove text by phrase or pattern, and areas by rectangle, for real; the file is written whole.

| Argument or option | What it does |
| --- | --- |
| `--text <phrase>` | A phrase to remove; give it more than once. |
| `--regex <pattern>` | A .NET regular expression to remove, such as \d{3}-\d{2}-\d{4}; give it more than once. |
| `--rect <page:x,y,w,h>` | An area to remove, page:left,bottom,width,height in lengths from the page's lower left corner, such as 1:20mm,250mm,80mm,10mm; give it more than once. |
| `--match-case` | Match --text with the case as written. |
| `--whole-word` | Match --text only as whole words. |
| `--pages <range>`, `-p` | The pages to search, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |
| `--mark-only` | Only mark the areas with redaction annotations, for review; remove nothing yet. |
| `--images <how>` | Image parts under an area: pixels (blacked out, the default), remove (the whole image) or none. |
| `--paths <how>` | Vector paths: covered (removed when fully covered, the default), touched (removed when touched) or none. |

### sanitize

Strip hidden data: JavaScript and actions, attachments, metadata, hidden layers, comments, form data, thumbnails and private data.

| Argument or option | What it does |
| --- | --- |
| `--keep <kinds>` | What to leave in, a list of metadata, javascript, attachments, hidden-layers, comments, form-data, thumbnails, private-data. |

### form-fill

Fill form fields from a JSON file or --set name=value; --flatten fixes the values into the page.

| Argument or option | What it does |
| --- | --- |
| `--data <file>` | A JSON file of values: { "name": "Ada", "subscribe": true, "colours": [ "red" ] }, or the output of form list --json. |
| `--set <name=value>` | One value, name=value; give it more than once. |
| `--flatten` | Flatten the form after filling it, so the values become page content. |

### form-flatten

Draw form fields into the page and remove them from the form, so the values can no longer change.

| Argument or option | What it does |
| --- | --- |
| `--field <name>` | Flatten only this field; give it more than once. All fields by default. |

### form-reset

Set form fields back to their default values.

| Argument or option | What it does |
| --- | --- |
| `--field <name>` | Reset only this field; give it more than once. All fields by default. |

### attach

Embed files in PDFs; --relationship makes them PDF/A-3 associated files.

| Argument or option | What it does |
| --- | --- |
| `--file <path>` | A file to embed; give it more than once. |
| `--name <name>` | The name to embed a single file under; its own name by default. |
| `--description <text>` | A description shown by viewers. |
| `--mime-type <type>` | The file's media type, such as text/csv. |
| `--relationship <kind>` | Make it a PDF/A-3 associated file: source, data, alternative, supplement, encrypted-payload, form-data, schema or unspecified. |

### detach

Remove embedded files by name.

| Argument or option | What it does |
| --- | --- |
| `--name <name>` | The attachment to remove; give it more than once. |

### invoice

Embed a ZUGFeRD or Factur-X e-invoice XML and the PDF/A-3 metadata it needs.

| Argument or option | What it does |
| --- | --- |
| `--xml <file>` | The invoice XML (Cross Industry Invoice). |
| `--standard <standard>` | factur-x (the default), zugferd20 or zugferd10. |
| `--profile <profile>` | minimum, basic-wl, basic, en16931, extended or xrechnung; read from the XML by default. |
| `--pdfa <level>` | The PDF/A-3 level to claim: 3b (the default), 3u or 3a. |

### pdfa

Convert to PDF/A: embed fonts, add an output intent and XMP, remove what PDF/A forbids; fails the file when a clause cannot be met.

| Argument or option | What it does |
| --- | --- |
| `--level <level>` | 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 or 4f; the best the file can meet by default. |
| `--icc <file>` | The output intent's ICC profile; sRGB by default. |
| `--output-condition <name>` | The output condition's name, such as FOGRA39. |
| `--allow-font-substitution` | Embed a stand-in for a font the file does not embed. |
| `--system-fonts` | Look for fonts the file does not embed among the system's fonts. |

### ocr

Make scanned pages searchable: run an OCR program (Tesseract or any that writes hOCR or ALTO) on each page and add its words as invisible text; PDF/A-2b by default.

| Argument or option | What it does |
| --- | --- |
| `--engine-command <command>` | The OCR program and its arguments, run once per page: {image} is the page image (PNG), {output} the result file without extension, {langs} the languages, {dpi} the resolution, {page} the page from 0. It must write hOCR or ALTO, such as "tesseract {image} {output} -l {langs} --dpi {dpi} hocr". |
| `--lang <languages>`, `-l` | The languages to read, as the program names them, joined by + or commas, such as eng+nor. |
| `--mode <skip|redo|force>` | skip (the default) leaves pages with text alone; redo replaces an earlier invisible text layer; force reads every page. |
| `--pdfa <level>` | The PDF/A level the output meets: 2b by default, or 1b, 2a, 2u, 3a, 3b, 3u, 4, 4f, or none to leave it as it is. |
| `--page-jobs <count>` | How many pages of a file the program reads at once; 0 (the default) for one per processor. |
| `--dpi <dpi>` | The resolution pages are rendered at when they are not one scanned image; 300 by default. |
| `--min-confidence <fraction>` | Leave out words the program is less sure of, from 0 to 1. |
| `--deskew` | Straighten the image the program reads; the page is not changed. |
| `--rotate-pages` | Turn pages the program finds sideways or upside down. |
| `--engine-timeout <seconds>` | Seconds one run of the program may take; 300 by default. |
| `--pages <range>`, `-p` | The pages to read, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |

### ocr-layer

Add an invisible, searchable text layer from hOCR or ALTO files (from Tesseract, ABBYY and the like), one per page.

| Argument or option | What it does |
| --- | --- |
| `--hocr <template>` | hOCR files, one per page, named by a template such as scan-{page}.hocr ({page} from 1). |
| `--alto <template>` | ALTO XML files, one per page, named as --hocr. |
| `--pages <range>`, `-p` | The pages to add text to, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default. |

### clean

Tidy scans and prints: --grayscale, --remove-blank-pages, --deskew and --text-to-outlines, in that order.

| Argument or option | What it does |
| --- | --- |
| `--grayscale` | Turn every color to gray. |
| `--remove-blank-pages` | Remove pages with next to no ink. |
| `--blank-ink <fraction>` | The most ink, as a fraction of the page, that still counts as blank, such as 0.002. |
| `--deskew` | Straighten scanned pages that are a little crooked. |
| `--max-angle <degrees>` | The largest tilt --deskew corrects, in degrees. |
| `--text-to-outlines` | Turn text into outlines, so it prints the same everywhere and needs no fonts (it can no longer be searched). |

### pdfx

Convert to PDF/X: an output intent, colors in its CMYK where the level needs it, embedded fonts, page boxes; fails the file when a clause cannot be met.

| Argument or option | What it does |
| --- | --- |
| `--level <level>` | The PDF/X level: x-1a, x-3, x-4 (the default), x-4p, x-5g, x-5pg, x-5n, x-6, x-6p or x-6n. |
| `--output-condition <name>` | The printing condition: FOGRA39, FOGRA51, FOGRA52, GRACoL2013, SWOP2013 or newspaper. |
| `--icc <file>` | The output intent's ICC profile, for a condition egap does not bundle. |
| `--bleed <length>` | Add a bleed box this far around the trim box, such as 3mm. |
| `--trapped` | Say the file has been trapped. |
| `--allow-font-substitution` | Embed a stand-in for a font the file does not embed. |
| `--system-fonts` | Look for fonts the file does not embed among the system's fonts. |

### color

Convert RGB, Lab, gray and ICC colors to the output intent's CMYK, or another CMYK; spot colors on request.

| Argument or option | What it does |
| --- | --- |
| `--target <condition|file>` | The CMYK to convert to: an output condition (FOGRA39, GRACoL2013, ...) or an ICC file; the file's output intent by default. |
| `--intent <intent>` | relative (the default), perceptual, saturation or absolute. |
| `--black-point-compensation` | Map black to black (true or false). |
| `--black-text <how>` | Black text and thin rules: pure (100% K, the default) or colorimetric. |
| `--spots <how>` | Spot colors: keep (the default), convert, or convert-but-kept with --keep-spot. |
| `--keep-spot <name>` | A spot color to keep with --spots convert-but-kept; give it more than once. |
| `--retarget-cmyk` | Also convert CMYK that was made for another printing condition. |

### save

Write the document as it stands, to a file or a template such as out/{name}.pdf; it may come more than once.

| Argument or option | What it does |
| --- | --- |
| `<path>` | Where to write: a file, a template with {name}, {ext}, {dir} and {index}, or -. |
| `--rewrite` | Write the whole file anew instead of appending the changes (breaks signatures). |
| `--linearize` | Lay the file out for fast web view; writes the whole file. |
| `--compress` | Pack objects into compressed object streams; writes the whole file. |
| `--allow-invalidating-signatures` | Save even when the change breaks a digital signature in the file. |
| `--deterministic` | Derive the file ID from the content, so the same input always gives the same bytes. |
