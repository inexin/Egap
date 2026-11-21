using Egap.Documents.Layout;

namespace Egap.Documents.Markdown.Tests;

/// <summary>Tracked changes written as Markdown in the three views (6.19 Build Plan task 12).</summary>
public class RevisionViewMarkdownTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);

    /// <summary>"Keep [new ]{Ann}[old ]{Bob}**text.**", a paragraph Bob deleted whole, a bold word inserted, a plain paragraph.</summary>
    private static Document Tracked()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Paragraph first = body.AddParagraph();
        first.AddRun("Keep ");
        first.AddRun("new ").Revision = new Revision(RevisionKind.Insertion, "Ann", When);
        first.AddRun("old ").Revision = new Revision(RevisionKind.Deletion, "Bob", When);
        first.AddRun("text.").Format.Bold = true;
        Paragraph gone = body.AddParagraph();
        gone.AddRun("Gone ").Revision = new Revision(RevisionKind.Deletion, "Bob", When);
        gone.AddRun("whole.").Revision = new Revision(RevisionKind.Deletion, "Bob", When);
        gone.MarkRevision = new Revision(RevisionKind.Deletion, "Bob", When);
        Paragraph emphasis = body.AddParagraph();
        emphasis.AddRun("A ");
        Run strong = emphasis.AddRun("strong");
        strong.Format.Bold = true;
        strong.Revision = new Revision(RevisionKind.Insertion, "Ann", When);
        emphasis.AddRun(" word.");
        body.AddParagraph("Last.");
        return document;
    }

    [Fact]
    public void FinalIsAsBeforeWithTheWarning()
    {
        string markdown = Tracked().ToMarkdown(out IReadOnlyList<ConversionWarning> warnings);

        Assert.Equal("Keep new **text.**\n\nA **strong** word.\n\nLast.\n", markdown);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
    }

    [Fact]
    public void MarkupWritesInsAndDel()
    {
        string markdown = Tracked().ToMarkdown(out IReadOnlyList<ConversionWarning> warnings, new MarkdownSaveOptions { RevisionView = RevisionView.Markup });

        Assert.Equal("Keep <ins>new </ins><del>old </del>**text.**\n\n<del>Gone whole.</del>\n\nA <ins>**strong**</ins> word.\n\nLast.\n", markdown);
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
    }

    [Fact]
    public void MarkupCanWriteCriticMarkup()
    {
        string markdown = Tracked().ToMarkdown(new MarkdownSaveOptions { RevisionView = RevisionView.Markup, RevisionStyle = MarkdownRevisionStyle.CriticMarkup });

        Assert.Equal("Keep {++new ++}{--old --}**text.**\n\n{--Gone whole.--}\n\nA {++**strong**++} word.\n\nLast.\n", markdown);
    }

    [Fact]
    public void OriginalRejectsEveryChangeAndKeepsTheDocument()
    {
        Document document = Tracked();
        int changes = document.Revisions.Count;
        string markdown = document.ToMarkdown(out IReadOnlyList<ConversionWarning> warnings, new MarkdownSaveOptions { RevisionView = RevisionView.Original });

        Assert.Equal("Keep old **text.**\n\nGone whole.\n\nA  word.\n\nLast.\n", markdown);
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.ChangesAccepted);
        Assert.Equal(changes, document.Revisions.Count);
    }

    [Fact]
    public void AnInsertedHyperlinkIsMarkedAroundIt()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        paragraph.AddRun("See ");
        Hyperlink link = new(document) { Address = "https://example.com/" };
        link.Inlines.Add(new Run(document, "the site"));
        link.Revision = new Revision(RevisionKind.Insertion, "Ann");
        paragraph.Inlines.Add(link);

        string markdown = document.ToMarkdown(new MarkdownSaveOptions { RevisionView = RevisionView.Markup });

        Assert.Equal("See <ins>[the site](https://example.com/)</ins>\n", markdown);
    }
}
