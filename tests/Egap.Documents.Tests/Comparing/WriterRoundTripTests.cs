using System.IO.Compression;

using Egap.Documents.Comparing;
using Egap.Documents.Docx;
using Egap.Documents.Odf;
using Egap.Documents.Rtf;

namespace Egap.Documents.Tests.Comparing;

/// <summary>
/// Comparison results saved to DOCX, RTF and ODT and read back (6.19 Build Plan task 10): accepting every change gives the
/// revised document and rejecting every change gives the original, each as the same writer and reader carry it.
/// </summary>
public sealed class WriterRoundTripTests
{
    /// <summary>The document saved in <paramref name="format"/> and read back, with the writer's warnings.</summary>
    internal static Document Through(Document document, string format, out IReadOnlyList<ConversionWarning> warnings)
    {
        using MemoryStream stream = new();
        warnings = format switch
        {
            "docx" => DocxFormat.Save(document, stream),
            "rtf" => RtfFormat.Save(document, stream),
            _ => OdtFormat.Save(document, stream),
        };
        stream.Position = 0;
        return format switch
        {
            "docx" => DocxFormat.Load(stream),
            "rtf" => RtfFormat.Load(stream),
            _ => OdtFormat.Load(stream),
        };
    }

    internal static Document Through(Document document, string format)
    {
        return Through(document, format, out _);
    }

    /// <summary>Checks the result saved in <paramref name="format"/> both ways, against the inputs saved the same way.</summary>
    internal static void Check(Document original, Document revised, Document result, string format)
    {
        Document back = Through(result, format);
        Document accepted = back.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = back.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(RoundTrip.Blocks(Through(Settled(revised), format)), RoundTrip.Blocks(Through(accepted, format)));
        Assert.Equal(RoundTrip.Blocks(Through(Settled(original), format)), RoundTrip.Blocks(Through(rejected, format)));
    }

    /// <summary>A copy with the document's own tracked changes accepted, as the comparison reads it.</summary>
    private static Document Settled(Document document)
    {
        Document copy = document.Clone();
        copy.Revisions.AcceptAll();
        return copy;
    }

    [Theory]
    [MemberData(nameof(CompareCorpusTests.Corpus), MemberType = typeof(CompareCorpusTests))]
    public void MutatedCorpusRoundTripsThroughDocx(string name)
    {
        MutatedCorpusRoundTrips(name, "docx");
    }

    [Theory]
    [MemberData(nameof(CompareCorpusTests.Corpus), MemberType = typeof(CompareCorpusTests))]
    public void MutatedCorpusRoundTripsThroughRtf(string name)
    {
        MutatedCorpusRoundTrips(name, "rtf");
    }

    [Theory]
    [MemberData(nameof(CompareCorpusTests.Corpus), MemberType = typeof(CompareCorpusTests))]
    public void MutatedCorpusRoundTripsThroughOdt(string name)
    {
        MutatedCorpusRoundTrips(name, "odt");
    }

    private static void MutatedCorpusRoundTrips(string name, string format)
    {
        Document original = CompareCorpusTests.Load(name);
        for (int seed = 0; seed < 6; seed++)
        {
            Document revised = new EditMutator(300 + seed).Mutate(original, 1 + (seed * 4));
            if (format != "docx" && !(FieldsNest(original) && FieldsNest(revised)))
            {
                // The mutator can leave a field that starts in one paragraph and ends in a table cell or another story; RTF
                // and ODT hold fields only as groups and elements that nest, so such a document does not round trip.
                continue;
            }

            foreach (CompareBase baseDocument in (CompareBase[])[CompareBase.Revised, CompareBase.Original])
            {
                Document result = DocumentCompare.Run(original, revised, new DocumentCompareOptions { Base = baseDocument }).Document;
                if (format == "odt" && (SplitToc(result) || FlattenedToc(result)))
                {
                    // An OpenDocument index is a run of body paragraphs that holds its settings, not a code: a table of
                    // contents the mutator ended in a table cell, or whose code or edges it changed, is written as its text.
                    continue;
                }

                Check(original, revised, result, format);
            }
        }
    }

    [Theory]
    [InlineData(CompareBase.Revised, "Introduction|Methods|Results", "Introduction|Method and scope|Results")]
    [InlineData(CompareBase.Original, "Introduction|Methods|Results", "Introduction|Method and scope|Results")]
    [InlineData(CompareBase.Revised, "Introduction|Methods|Results", "Introduction|Methods|Results|Appendix")]
    [InlineData(CompareBase.Original, "Introduction|Methods|Results|Appendix", "Introduction|Methods|Results")]
    [InlineData(CompareBase.Revised, "Introduction|Methods~2|Results|Appendix", "Introduction|Methods~pending|Results")]
    public void ATableOfContentsWithChangedEntriesKeepsItsCodeThroughOdt(CompareBase baseDocument, string before, string after)
    {
        Document original = Toc(before.Split('|'));
        Document revised = Toc(after.Split('|'));

        Document result = DocumentCompare.Run(original, revised, new DocumentCompareOptions { Base = baseDocument }).Document;

        // As LibreOffice writes it: the changes inside the index, its code kept as the index's settings.
        Check(original, revised, result, "odt");
        Document back = Through(result, "odt", out IReadOnlyList<ConversionWarning> warnings);
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.FieldsFlattened);
        Assert.Contains(back.Descendants<FieldStart>(), start => start.GetField()?.Code.TrimStart().StartsWith("TOC", StringComparison.Ordinal) == true);
        Assert.Contains(back.Descendants<Inline>(), inline => inline.Revision is not null);

        static Document Toc(params string[] headings)
        {
            Document document = DocumentCompareTests.Make("Contents");
            Body body = document.Sections[0].Body;
            for (int i = 0; i < headings.Length; i++)
            {
                Paragraph entry = body.AddParagraph();
                if (i == 0)
                {
                    entry.Inlines.Add(new FieldStart(document));
                    entry.AddRun(" TOC \\o \"1-3\" \\h ");
                    entry.Inlines.Add(new FieldSeparator(document));
                }

                // Entries as Word writes them: links to their headings.
                string page = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string[] parts = headings[i].Split('~');
                entry.AddHyperlink(parts[0] + "\t" + page, "").Anchor = "_Toc" + page;
                if (parts.Length > 1)
                {
                    entry.AddRun(parts[1]);
                }

                if (i == headings.Length - 1)
                {
                    entry.Inlines.Add(new FieldEnd(document));
                }
            }

            document.Sections[0].Body.AddParagraph("Body text.");
            return document;
        }
    }

    private const string Moved = "This whole paragraph moves to the end of the document.";

    [Theory]
    [InlineData("rtf")]
    [InlineData("odt")]
    public void MovesAreWrittenAsDeletionAndInsertionWithAWarning(string format)
    {
        Document original = DocumentCompareTests.Make(Moved, "The second paragraph stays.", "The third one stays too.");
        Document revised = DocumentCompareTests.Make("The second paragraph stays.", "The third one stays too.", Moved);
        Document result = DocumentCompare.Run(original, revised).Document;

        Document back = Through(result, format, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.MovesSplit);
        Assert.DoesNotContain(back.Descendants<Inline>(), i => i.Revision?.Kind is RevisionKind.MoveFrom or RevisionKind.MoveTo);
        Assert.Contains(back.Descendants<Run>(), r => r.Revision?.Kind == RevisionKind.Deletion && r.Text.Contains("whole", StringComparison.Ordinal));
        Assert.Contains(back.Descendants<Run>(), r => r.Revision?.Kind == RevisionKind.Insertion && r.Text.Contains("whole", StringComparison.Ordinal));
        Check(original, revised, result, format);
    }

    [Fact]
    public void DocxWritesMovesWithoutAWarning()
    {
        Document result = DocumentCompare.Run(
            DocumentCompareTests.Make(Moved, "The second paragraph stays."), DocumentCompareTests.Make("The second paragraph stays.", Moved)).Document;

        Through(result, "docx", out IReadOnlyList<ConversionWarning> warnings);

        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.MovesSplit);
    }

    private static Document WithBold(bool bold)
    {
        Document document = DocumentCompareTests.Make();
        Paragraph paragraph = document.Sections[0].Body.AddParagraph();
        paragraph.AddRun("Some ");
        paragraph.AddRun("important").Format.Bold = bold;
        paragraph.AddRun(" words.");
        return document;
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("odt")]
    public void RtfKeepsFormatChangesAndOdtDropsThemWithAWarning(string format)
    {
        Document result = DocumentCompare.Run(WithBold(false), WithBold(true)).Document;
        Assert.Contains(result.Descendants<Run>(), r => r.FormatRevision is not null);

        Document back = Through(result, format, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal(format == "rtf", back.Descendants<Run>().Any(r => r.FormatRevision is not null));
        Assert.Equal(format == "odt", warnings.Any(w => w.Kind == ConversionWarningKind.ChangesAccepted));
        Assert.True(back.Descendants<Run>().Single(r => r.Text == "important").GetEffectiveFormat().Bold);
    }

    [Theory]
    [InlineData("rtf", CompareBase.Revised)]
    [InlineData("rtf", CompareBase.Original)]
    [InlineData("odt", CompareBase.Revised)]
    [InlineData("odt", CompareBase.Original)]
    public void InsertedAndDeletedRowsRoundTrip(string format, CompareBase baseDocument)
    {
        Document original = TableCompareTests.WithTable("Name|Value", "pears|two crates", "plums|five");
        Document revised = TableCompareTests.WithTable("Name|Value", "plums|five", "apples|three boxes");
        Document result = DocumentCompare.Run(original, revised, new DocumentCompareOptions { Base = baseDocument }).Document;
        Assert.Equal(2, result.Descendants<Row>().Count(r => r.Revision is not null));

        Document back = Through(result, format);

        Assert.Equal(result.Descendants<Row>().Select(r => r.Revision?.Kind), back.Descendants<Row>().Select(r => r.Revision?.Kind));
        Check(original, revised, result, format);
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("odt")]
    public void AnEmptyInsertedRowRoundTrips(string format)
    {
        Document original = TableCompareTests.WithTable("Name|Value", "plums|five");
        Document revised = TableCompareTests.WithTable("Name|Value", "|", "plums|five");
        Document result = DocumentCompare.Run(original, revised).Document;
        Assert.Equal(RevisionKind.Insertion, result.Descendants<Row>().ElementAt(1).Revision?.Kind);

        Document back = Through(result, format);

        Assert.Equal(RevisionKind.Insertion, back.Descendants<Row>().ElementAt(1).Revision?.Kind);
        Check(original, revised, result, format);
    }

    [Fact]
    public void OdtMarksTrackedRowsAsLibreOfficeDoes()
    {
        Document result = DocumentCompare.Run(TableCompareTests.WithTable("Name|Value"), TableCompareTests.WithTable("Name|Value", "apples|three")).Document;

        Assert.Equal(1, CountOf(OdtContent(result), "loext:text-changes-only=\"false\""));
    }

    [Fact]
    public void AnOdtRowWhoseTextIsAllInsertedIsNotARowInsertion()
    {
        // An existing row whose empty cells were filled in: the text is inserted, the row is not.
        Document original = TableCompareTests.WithTable("Name|Value", "|");
        Document revised = TableCompareTests.WithTable("Name|Value", "apples|three");
        Document result = DocumentCompare.Run(original, revised).Document;
        Assert.All(result.Descendants<Row>(), r => Assert.Null(r.Revision));

        Document back = Through(result, "odt");

        Assert.All(back.Descendants<Row>(), r => Assert.Null(r.Revision));
        Check(original, revised, result, "odt");
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("odt")]
    public void ALastParagraphInsertedOrDeletedRoundTrips(string format)
    {
        Document shorter = DocumentCompareTests.Make("Hello there.");
        Document longer = DocumentCompareTests.Make("Hello there.", "A new last paragraph.");

        Check(shorter, longer, DocumentCompare.Run(shorter, longer).Document, format);
        Check(longer, shorter, DocumentCompare.Run(longer, shorter).Document, format);
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("odt")]
    public void ParagraphsReplacedAtTheEndRoundTrip(string format)
    {
        Document original = DocumentCompareTests.Make("First stays.", "The old ending of the text.");
        Document revised = DocumentCompareTests.Make("First stays.", "Something else entirely here.", "And one more.");

        Check(original, revised, DocumentCompare.Run(original, revised).Document, format);
        Check(revised, original, DocumentCompare.Run(revised, original).Document, format);
    }

    [Fact]
    public void OdtWritesADeletedParagraphBeforeATableAsADeletionBetweenBlocks()
    {
        Document original = TableCompareTests.WithTable("Name|Value");
        Document revised = TableCompareTests.WithTable("Name|Value");
        revised.Sections[0].Body.Paragraphs.First().Remove();
        Document result = DocumentCompare.Run(original, revised).Document;

        Assert.Matches("</text:tracked-changes><text:change text:change-id=", OdtContent(result));
        Check(original, revised, result, "odt");
    }

    [Fact]
    public void OdtKeepsADeletedPageBreak()
    {
        Document original = DocumentCompareTests.Make();
        Paragraph paragraph = original.Sections[0].Body.AddParagraph("Before the break.");
        paragraph.Inlines.Add(new Break(original, BreakKind.Page));
        paragraph.AddRun("After the break.");
        Document revised = DocumentCompareTests.Make("Something else.");
        Document result = DocumentCompare.Run(original, revised).Document;

        Check(original, revised, result, "odt");
        Document rejected = Through(result, "odt");
        rejected.Revisions.RejectAll();
        Assert.Equal(2, rejected.Sections[0].Body.Paragraphs.Count());
    }

    private static string OdtContent(Document document)
    {
        using MemoryStream stream = new();
        OdtFormat.Save(document, stream);
        stream.Position = 0;
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        using StreamReader reader = new(zip.GetEntry("content.xml")!.Open());
        return reader.ReadToEnd();
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A table of contents whose field, code or edges changed, which OpenDocument writes as its text.</summary>
    private static bool FlattenedToc(Document document)
    {
        return document.Descendants<FieldStart>().Any(start => start.GetField() is { } field && field.Code.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase)
            && (start.Revision is not null || field.End.Revision is not null || field.End.Paragraph?.MarkRevision is not null
                || start.Paragraph?.PreviousSibling is Paragraph { MarkRevision: not null }
                || CodeOf(start).Any(inline => inline.Revision is not null)));

        static IEnumerable<Inline> CodeOf(FieldStart start)
        {
            for (Node? node = start.NextSibling; node is Inline inline; node = node.NextSibling)
            {
                yield return inline;
                if (inline is FieldSeparator)
                {
                    yield break;
                }
            }
        }
    }

    private static bool SplitToc(Document document)
    {
        return document.Descendants<FieldStart>().Any(start => start.GetField() is { } field && field.Code.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase)
            && field.End.Paragraph?.Parent != start.Paragraph?.Parent);
    }

    /// <summary>True when every field starts and ends in the same table cell or story, nested properly.</summary>
    internal static bool FieldsNest(Document document)
    {
        return document.Sections.All(section => Nest(section.Body.Blocks));

        static bool Nest(IEnumerable<Block> blocks)
        {
            int depth = 0;
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (Inline inline in paragraph.Descendants<Inline>())
                        {
                            depth += inline is FieldStart ? 1 : inline is FieldEnd ? -1 : 0;
                            if (depth < 0)
                            {
                                return false;
                            }
                        }

                        break;
                    case Table table:
                        if (depth > 0 || !table.Rows.SelectMany(r => r.Cells).All(c => Nest(c.Blocks)))
                        {
                            return false;
                        }

                        break;
                }
            }

            return depth == 0;
        }
    }
}
