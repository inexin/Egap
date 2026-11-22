# Comparing documents

`DocumentCompare.Run` compares two versions of a document, as Word's *Compare* does. It returns a new document that holds
every difference as a tracked change, and a list of those differences. Accepting every change in the result gives the
revised document, and rejecting every change gives the original. The two inputs may come from any format Egap reads, also
from two different ones: a DOCX contract can be compared with the RTF or HTML version someone sent back.

```csharp
using Egap.Documents;
using Egap.Documents.Comparing;

Document original = Document.Load("contract-v1.docx");
Document revised = Document.Load("contract-v2.docx");

DocumentComparison comparison = DocumentCompare.Run(original, revised, new DocumentCompareOptions { Author = "Legal" });
Console.WriteLine(comparison);                                       // "12 changes: 40 words inserted, 25 deleted"
comparison.Document.Save("contract-redline.docx");                   // open it in Word to review the changes
```

Neither input is changed. The result is a copy of the revised document (or of the original, with
`Base = CompareBase.Original`), so it keeps that document's styles, sections and settings.

## What is compared

- **Text.** Paragraphs are lined up first. A paragraph that is only in one document is inserted or deleted whole.
  Paragraphs that share at least half their words are compared word by word. A paragraph split into two to four
  paragraphs, or paragraphs joined into one, is compared as one text: only the paragraph marks between the pieces are
  inserted or deleted, as Word marks them. Word boundaries follow Unicode UAX #29.
  With `Granularity = CompareGranularity.Character`, only the letters that changed are marked. `CaseChanges = false`
  ignores changes of case only, and `Whitespace = false` ignores changes of spacing only.
- **Tables.** Rows are matched by their content. A row that is only in one document is a tracked row insertion or
  deletion. Matched rows have their cells compared word by word, and nested tables in turn.
- **Formatting.** The same text in another look (bold, size, font or colour), or a paragraph with another style or
  alignment, becomes a format change. A format change keeps the old formatting, so rejecting it puts the old look back.
- **Moves.** A run of paragraphs deleted in one place and inserted again elsewhere becomes a move. The run needs at least
  six words, so a short line that repeats is not taken for a move. The source and the destination share a name
  (`Revision.MoveName`), and Word shows them as one move. As in Word, moves inside a paragraph are not looked for.
- **Headers and footers.** Each one is compared with the one of the same kind (first, even or default) in the section at
  the same place.
- **Notes and text boxes.** A footnote or endnote reference, and a shape, is one token in its paragraph. When both
  documents have it, the note's or text box's text is compared as a story of its own. A shape whose kind or size
  changed is deleted and inserted.
- **Fields and pictures.** Each is one token. A field is compared by its code and result, or by its result only with
  `Fields = false`. A picture is compared by its image and size, so a resized picture is deleted and inserted.
- **Comments.** Comments are never tracked changes: the result has the revised document's comments, at the same text.

Each of these has a switch on `DocumentCompareOptions`: `Formatting`, `Moves`, `HeadersFooters`, `Notes`, `TextBoxes`,
`Fields` and `Comments`. All are on by default. A switch that is off keeps the base document's version of that part.

## The list of changes

`Changes` lists the differences in document order. Each change has a kind (`Inserted`, `Deleted`, `Replaced`,
`Formatted` or `Moved`), its old and new text, the story it is in (the body, a header, a note, a text box), and the
tracked changes in the result that make it up. One change can be accepted or rejected on its own. A move is one change:
accepting it removes the source and keeps the destination.

```csharp
using Egap.Documents;
using Egap.Documents.Comparing;

DocumentComparison comparison = DocumentCompare.Run(Document.Load("draft.docx"), Document.Load("final.docx"));
foreach (DocumentChange change in comparison.Changes)
{
    if (change.Kind == CompareChangeKind.Formatted)
    {
        change.Accept();                                                 // take the new look without asking
    }
}

DocumentCompareStatistics counts = comparison.Statistics;
Console.WriteLine($"{counts.InsertedWords} inserted, {counts.DeletedWords} deleted, {counts.MovedWords} moved, "
    + $"{counts.FormattingChanges} format changes in {counts.ParagraphsTouched} paragraphs");
foreach (DocumentCompareWarning warning in comparison.Warnings)
{
    Console.WriteLine(warning);
}
```

`Statistics` counts inserted, deleted and moved words, format changes and the paragraphs touched. `Warnings` says what
the comparison could not show exactly, with how many times it happened.

## Inputs with tracked changes or from different formats

An input that already holds tracked changes is compared as if they were accepted. Egap accepts them on a copy, and the
warning `ExistingRevisionsAccepted` says how many there were. With `ExistingRevisions = ExistingRevisionHandling.Throw`,
`Run` throws an `InvalidOperationException` instead.

`Document.Load` records the format it read in `Document.SourceFormat`. When the two inputs come from different formats,
formatting is not compared and the warning `FormattingNotCompared` says so: a DOCX file and an HTML page of the same text
differ in nearly every run's formatting. Set `Formatting` to `true` to compare it anyway, or to `false` to never compare
it.

```csharp
using Egap.Documents;
using Egap.Documents.Comparing;

Document sent = Document.Load("offer.docx");
Document returned = Document.Load("offer-returned.html");            // needs Egap.Documents.Html and HtmlFormat.Register()

DocumentComparison comparison = DocumentCompare.Run(sent, returned, new DocumentCompareOptions
{
    Base = CompareBase.Original,                                     // keep the DOCX styles and page setup
    ExistingRevisions = ExistingRevisionHandling.Throw,              // refuse inputs that are still being reviewed
});
```

## Showing the result

Save the result to DOCX to review it in Word or LibreOffice. Moves are written with named move ranges, so Word pairs
their source and destination. To print a redline without a word processor, lay the result out with
`RevisionView.Markup`: inserted text is underlined and deleted text struck through, in a colour per author, with change
bars. The HTML, EPUB and Markdown writers take the same views.

```csharp
using Egap.Documents;
using Egap.Documents.Comparing;
using Egap.Documents.Layout;
using Egap.Documents.Pdf;

DocumentComparison comparison = DocumentCompare.Run(Document.Load("v1.docx"), Document.Load("v2.docx"));
comparison.Document.SaveAsPdf("redline.pdf", new PdfExportOptions { RevisionView = RevisionView.Markup });
```

RTF and ODT hold the result's tracked changes too, and Egap reads them back the same, so accepting all gives the
revised document and rejecting all the original. Neither format has moves: a move is written as a deletion and an
insertion, with the warning `MovesSplit`. Inserted and deleted table rows are kept: RTF marks the row's end as Word does,
and ODT marks the row as LibreOffice does, which shows it as an inserted or deleted row. ODT records no old formatting,
so format changes are written with their new formatting only, with a `ChangesAccepted` warning.

```csharp
using Egap.Documents;
using Egap.Documents.Comparing;
using Egap.Documents.Odf;

DocumentComparison comparison = DocumentCompare.Run(Document.Load("v1.docx"), Document.Load("v2.docx"));
using FileStream file = File.Create("redline.odt");
foreach (ConversionWarning warning in OdtFormat.Save(comparison.Document, file))
{
    Console.WriteLine(warning);                                      // what ODT could not hold, such as moves
}
```

## From the command line

`egap compare` runs the same comparison on two files and writes the result with tracked changes or as a redline; `egap
mcp` offers it to AI assistants as the `compare` tool. See [the egap command](../cli/index.md#comparing-documents).

```sh
egap compare contract-v1.docx contract-v2.docx --output contract-redline.docx --author Legal
egap compare contract-v1.docx contract-v2.docx --redline contract-redline.pdf --json
```

## Limits

- Moves inside a paragraph are not found; a moved word is a deletion and an insertion.
- A paragraph split into more than four pieces, or with a table between its pieces, is not taken as a split: one piece
  is compared with it and the others are inserted whole (and the same for a join).
- Cells inserted into or deleted from a row are not tracked changes, as the model tracks whole rows only. A table with no
  row in common is replaced whole.
- A resized picture is deleted and inserted, as Word does not track picture resizing.
- In ODT, a table of contents with changes is written as its text, as an OpenDocument index holds no changes.
- Content that cannot be a tracked change, such as a content control that could not be matched, is kept as in the base
  document; the warning `ContentNotTracked` counts these.
