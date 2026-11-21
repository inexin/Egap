using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Tracked changes written as HTML in the three views (6.19 Build Plan task 12).</summary>
public class RevisionViewHtmlTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// "Keep [new ]{Ann}[old ]{Bob}text." with "text." made bold by Ann, then a paragraph Bob deleted whole, then a plain
    /// one.
    /// </summary>
    internal static Document Tracked()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph first = body.AddParagraph();
        first.AddRun("Keep ");
        first.AddRun("new ").Revision = new Revision(RevisionKind.Insertion, "Ann", When);
        first.AddRun("old ").Revision = new Revision(RevisionKind.Deletion, "Bob", When);
        Run bold = first.AddRun("text.");
        bold.Format.Bold = true;
        RunFormat old = new() { Bold = false, Size = 12 };
        bold.FormatRevision = new FormatRevision("Ann", When, old);
        Paragraph gone = body.AddParagraph();
        gone.AddRun("Gone & forgotten.").Revision = new Revision(RevisionKind.Deletion, "Bob", When);
        gone.MarkRevision = new Revision(RevisionKind.Deletion, "Bob", When);
        body.AddParagraph("Last.");
        return document;
    }

    private static string Body(string html)
    {
        int start = html.IndexOf("<body>\n", StringComparison.Ordinal) + 7;
        return html[start..html.IndexOf("</body>", StringComparison.Ordinal)].Replace(" class=\"p-Normal\"", "", StringComparison.Ordinal);
    }

    [Fact]
    public void FinalIsAsBeforeWithTheWarning()
    {
        string html = Tracked().ToHtml(out IReadOnlyList<ConversionWarning> warnings);

        // As before this change: the deleted paragraph's mark is not removed, so an empty paragraph stays.
        Assert.Equal("<p>Keep new <strong>text.</strong></p>\n<p><br></p>\n<p>Last.</p>\n", Body(html));
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
        Assert.DoesNotContain("egap-author", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkupMarksEachChangeWithItsAuthorAndDate()
    {
        string html = Tracked().ToHtml(out IReadOnlyList<ConversionWarning> warnings, new HtmlSaveOptions { RevisionView = RevisionView.Markup });

        Assert.Equal(
            "<p>Keep <ins class=\"egap-author-1\" data-author=\"Ann\" datetime=\"2026-11-21T09:30:00+00:00\">new </ins>"
            + "<del class=\"egap-author-2\" data-author=\"Bob\" datetime=\"2026-11-21T09:30:00+00:00\">old </del>"
            + "<span class=\"egap-format-change egap-author-1\" data-author=\"Ann\" title=\"Formatted. Was: not bold, 12 pt\"><strong>text.</strong></span></p>\n"
            + "<p><del class=\"egap-author-2\" data-author=\"Bob\" datetime=\"2026-11-21T09:30:00+00:00\">Gone &amp; forgotten.</del></p>\n"
            + "<p>Last.</p>\n",
            Body(html));
        Assert.Contains(".egap-format-change { text-decoration: underline dotted; }\n", html, StringComparison.Ordinal);
        Assert.Contains(".egap-author-1 { color: #c00000; text-decoration-color: #c00000; }\n", html, StringComparison.Ordinal);
        Assert.Contains(".egap-author-2 { color: #0046be; text-decoration-color: #0046be; }\n", html, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
    }

    [Fact]
    public void OriginalRejectsEveryChangeAndKeepsTheDocument()
    {
        Document document = Tracked();
        string html = document.ToHtml(out IReadOnlyList<ConversionWarning> warnings, new HtmlSaveOptions { RevisionView = RevisionView.Original });

        // "text." loses its bold and gets back the 12 pt it had.
        Assert.Equal("<p>Keep old <span style=\"font-size: 12pt; font-weight: normal;\">text.</span></p>\n<p>Gone &amp; forgotten.</p>\n<p>Last.</p>\n", Body(html));
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
        Assert.Equal(5, document.Revisions.Count);
    }

    [Fact]
    public void AChangedHyperlinkNestsItsMarks()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        Hyperlink link = new(document) { Address = "https://example.com/" };
        link.Inlines.Add(new Run(document, "site"));
        link.Revision = new Revision(RevisionKind.Insertion, "Ann");
        paragraph.Inlines.Add(link);

        string html = document.ToHtml(new HtmlSaveOptions { RevisionView = RevisionView.Markup });

        Assert.Equal("<p><ins class=\"egap-author-1\" data-author=\"Ann\"><a href=\"https://example.com/\">site</a></ins></p>\n", Body(html));
    }

    [Theory]
    [InlineData(RevisionView.Markup, "<del")]
    [InlineData(RevisionView.Original, "Gone &amp; forgotten.")]
    public void MhtmlAndEpubFollowTheView(RevisionView view, string expected)
    {
        Document document = Tracked();
        string mhtml = document.ToMhtml(new HtmlSaveOptions { RevisionView = view });
        Assert.Contains(expected, QuotedPrintableText(mhtml), StringComparison.Ordinal);

        using MemoryStream stream = new();
        EpubFormat.Save(document, stream, new EpubSaveOptions { RevisionView = view });
        stream.Position = 0;
        using System.IO.Compression.ZipArchive zip = new(stream);
        string chapters = string.Concat(zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.Ordinal)).Select(e =>
        {
            using StreamReader reader = new(e.Open());
            return reader.ReadToEnd();
        }));
        Assert.Contains(expected, chapters, StringComparison.Ordinal);
    }

    private static string QuotedPrintableText(string mhtml)
    {
        return mhtml.Replace("=\r\n", "", StringComparison.Ordinal).Replace("=3D", "=", StringComparison.Ordinal);
    }
}
