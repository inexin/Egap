# DOCX

Egap.Documents reads and writes Word's files: documents (`.docx`), templates (`.dotx`) and their macro-enabled forms
(`.docm`, `.dotm`), strict and transitional. It needs no add-on package.

## Reading

A Word file reads into the model with Word's style model, lists numbered as Word numbers them, tables, fields, comments
and tracked changes; the [flow documents guide](../guides/flow-documents.md) shows what you can do with them. What the model
does not name travels along in each node's extension data. Macros are kept and never run. An old binary Word file (`.doc`)
is recognized and refused with an error that says so; a file encrypted with a password opens with it (see
[Passwords](#passwords)). `DocxLoadOptions.Limits` keeps untrusted files in bounds (see
[Limits](index.md#limits)).

```csharp
using Egap.Documents;

Document report = Document.LoadDocx("report.docx", out IReadOnlyList<ConversionWarning> warnings);
DocxKind? kind = DocxFormat.KindOf(report);                       // Document, Template or a macro-enabled kind
report.SaveAsDocx("report-copy.docx");                            // what the model does not name is kept
DocxFormat.Save(report, "report.dotx", new DocxSaveOptions { Kind = DocxKind.Template });
```

## Passwords

A Word file saved with a password to open it is encrypted: its package sits, encrypted, in a compound file.
`Document.Identify` calls it `Encrypted`, so a program can ask for the password before it loads the file. Give the password
in `DocxLoadOptions.Password`, or in `DocumentLoadOptions.Password` for `Document.Load`; `PasswordCallback` is asked when
the password is missing or wrong, with the attempt's number, until it gives the right one or null. Without the right
password, loading throws `DocumentPasswordException`, whose `WrongPassword` says whether a password was given.

Egap reads the encryption Word 2010 and later write (agile encryption, AES with SHA-1 to SHA-512, its integrity check
included) and Word 2007's (standard encryption, AES with SHA-1), which LibreOffice also writes. `DocxSaveOptions.Password`
encrypts the file as Word 2013 and later do: AES-256 with SHA-512 and 100,000 key spins. Files protected with rights
management (IRM), which needs a rights server, or encrypted for a certificate are refused with an error that says so.

```csharp
using Egap.Documents;

if (Document.Identify("contract.docx") == DocumentFormatKind.Encrypted)
{
    Document contract = Document.Load("contract.docx", new DocumentLoadOptions
    {
        PasswordCallback = attempt => attempt <= 3 ? AskUser($"Password for contract.docx (try {attempt} of 3)") : null,
    });
    contract.SaveAsDocx("contract-copy.docx", new DocxSaveOptions { Password = "new password" });
}

static string? AskUser(string prompt) => null;                    // your program's own password prompt
```

A password protects the file, not the document: a document loaded with a password saves without one unless
`DocxSaveOptions.Password` is set again.

## Editing restrictions

Word's *Restrict Editing* keeps a document read only, or lets readers only add comments, only make tracked changes or
only fill in forms. `Document.Protection` holds the restriction: its `Kind`, whether it `IsEnforced`, and the hash of the
password that lifts it. `Protect(kind, password)` sets one, hashed as Word hashes it (SHA-512 with a random salt and
100,000 spins), `Unprotect()` removes it, and `Verify(password)` says whether a password lifts it. Without a password,
anyone may switch the restriction off.

Editable ranges are the exceptions: an `EditableRangeStart` and an `EditableRangeEnd` with the same `Id` mark text that
stays editable, for everyone or a group (`EditorGroup`), or for one person (`Editor`, such as `DOMAIN\user` or an e-mail
address). `DocumentBuilder.StartEditableRange` and `EndEditableRange` write them.

```csharp
using Egap.Documents;

Document form = new();
DocumentBuilder builder = new(form);
builder.Writeln("Terms and conditions, not to be changed.");
builder.Write("Your name: ");
EditableRangeStart answer = builder.StartEditableRange(EditorGroup.Everyone);
builder.Write("(type here)");
builder.EndEditableRange(answer);
form.Protection.Protect(ProtectionKind.ReadOnly, "owner password");
form.SaveAsDocx("form.docx");

Document loaded = Document.Load("form.docx");
bool canLift = loaded.Protection.Verify("owner password");      // true
```

Egap does not enforce the restriction: code can change any part of a protected document. It reads it, writes it back
and keeps it through conversion between DOCX and RTF, so that Word enforces it when the file is opened. The hash is read
from Word's attributes and from ECMA-376's newer ones (`algorithmName`, `hashValue`), and written back as it came.

## Word compatibility

Word lays a file out by the rules of the Word version that made it: `DocumentSettings.CompatibilityMode` says which (15
for Word 2013 and later, 14 for 2010, 12 for 2007, 11 and below for older files), and the settings part's compatibility
options switch single layout rules on or off. Egap reads both and lays the document out by the same rules.

`DocumentSettings.WordCompatibility` names each option Egap acts on. A flag is what the file or your code set, else the
default for the compatibility mode, so changing the mode changes every flag nobody set:

| Flag | When on |
| --- | --- |
| `AllowSpaceOfSameStyleInTable` | Contextual spacing is ignored in table cells. |
| `DoNotUseIndentAsNumberingTabStop` | The tab after a list label skips the hanging indent and goes to the next tab stop. |
| `SplitPageBreakAndParagraphMark` | After a page break that ends a paragraph, the paragraph mark takes a line on the next page. |
| `OverrideTableStyleFontSizeAndJustification` | A table style's font size and alignment win over an 11 or 12 point, left-aligned default paragraph style. |
| `EnableOpenTypeFeatures` | Runs' ligatures, contextual alternates, stylistic sets and number forms are used. |
| `DoNotBreakWrappedTables` | A floating table stays on one page. |
| `AllowTextAfterFloatingTableBreak` | Text may follow a floating table on the page where it breaks. |
| `TableIndentFromCellText` | A table's indent is measured to its first cell's text (Word before 2013; not saved to DOCX). |

A new document has Word 2013's options, and saving it writes them. A file's options go back as they were read; a flag
you set is written, and the options Egap does not act on stay in the file and are listed in `OtherSettings`.

```csharp
using Egap.Documents;

Document old = Document.LoadDocx("word-2007.docx");
WordCompatibility compat = old.Settings.WordCompatibility;
bool legacyTables = !compat.OverrideTableStyleFontSizeAndJustification;
string? ulTrailSpace = compat.OtherSettings.GetValueOrDefault("ulTrailSpace");   // "1" when the file has it

compat.EnableOpenTypeFeatures = true;          // shape with the runs' ligatures and number forms
old.Settings.CompatibilityMode = 15;           // the flags nobody set follow Word 2013's rules
compat.ResetToModeDefaults();                  // forget the file's values too
```

## Writing

DOCX to DOCX is lossless: parts and paragraphs you did not edit are written as they were read. Saving to a path takes the
kind from the extension, so `report.dotx` is a template; saving to a stream keeps the kind the document was read as (a new
document is a `.docx`). `DocxSaveOptions.Kind` overrides both. A kind that holds no macros drops the macros a `.docm` or
`.dotm` had.

A document read from another format saves as DOCX with what Word can hold. Some things the model has Word does not:

- Word has no word spacing or overline; it keeps the OpenType features it knows (ligatures, number forms and spacing,
  stylistic sets, contextual alternates). See [Text options](../guides/flow-documents.md#text-options).
- Paging settings on groups of blocks, clamped lines and balanced columns are not saved; the content is. See
  [Page layout](../guides/flow-documents.md#page-layout).
- Margin boxes at the top and bottom of the page are saved as a three-part table in the header or footer; those in the
  corners and at the sides are left out with a warning.
- Flex and grid containers are saved as borderless tables with their items in the cells, and a group with a box as a
  one-cell table; the cells keep the items' backgrounds, borders and padding, not rounded corners or shadows. See
  [Flex and grid layout](../guides/flow-documents.md#flex-and-grid-layout).
- An SVG picture is saved with the PNG stand-in in `Picture.Fallback`, as Word stores SVG pictures, so older readers show
  it too. [Barcodes](../guides/flow-documents.md#barcodes) get one at 300 pixels per inch.
