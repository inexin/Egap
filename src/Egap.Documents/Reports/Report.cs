using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Reports;

/// <summary>
/// Builds a document from code with nested lambdas: pages with a header, a footer and content, and in them columns, rows,
/// tables, grids, text, images and barcodes, each with its box and paging settings. The result is an ordinary
/// <see cref="Document"/>, so the same report saves as PDF, DOCX or HTML and lays out with the same page flow.
/// </summary>
/// <example>
/// <code language="csharp">
/// Document invoice = Report.Create(r => r.Page(p =>
/// {
///     p.Size(PdfPageSize.A4).Margins(Length.Mm(15));
///     p.Header().Row(row =>
///     {
///         row.Relative().Text("Acme Ltd").FontSize(20).Bold();
///         row.Auto().AlignRight().Text("Invoice 2026-118");
///     });
///     p.Content().Column(col =>
///     {
///         col.Spacing(6);
///         col.Item().Text("Thank you for your order.");
///         col.Item().Table(t => t.Columns(3).Header("Item", "Qty", "Price").Rows(lines, l => [l.Name, l.Qty, l.Price]));
///     });
///     p.Footer().AlignCenter().Text(t =>
///     {
///         t.Span("Page ");
///         t.CurrentPageNumber();
///         t.Span(" of ");
///         t.TotalPages();
///     });
/// }));
/// invoice.SaveAsPdf("invoice.pdf");
/// </code>
/// </example>
public static class Report
{
    /// <summary>
    /// A new document built by <paramref name="compose"/>. Its layout is strict and uses only portable fonts unless
    /// <paramref name="layout"/> says otherwise (<see cref="Document.DefaultLayoutOptions"/>), so a report that does not fit
    /// fails loudly and prints the same on every machine.
    /// </summary>
    /// <param name="compose">Adds the pages.</param>
    /// <param name="layout">The layout options the document keeps; strict with portable fonts when null.</param>
    public static Document Create(Action<ReportBuilder> compose, LayoutOptions? layout = null)
    {
        ArgumentNullException.ThrowIfNull(compose);
        Document document = new() { DefaultLayoutOptions = layout ?? new LayoutOptions { Strict = true, Fonts = FontSettings.Portable } };
        ReportBuilder builder = new(document);
        compose(builder);
        if (document.Sections.Count == 0)
        {
            document.AddSection().Body.AddParagraph();
        }

        return document;
    }
}

/// <summary>A reusable part of a report: it composes itself into the container it is given.</summary>
public interface IComponent
{
    /// <summary>Adds the component's content to <paramref name="container"/>.</summary>
    void Compose(Container container);
}

/// <summary>What <see cref="Report.Create"/> gives the composing code: the document's properties, styles and pages.</summary>
public sealed class ReportBuilder
{
    internal ReportBuilder(Document document)
    {
        Document = document;
    }

    /// <summary>The document being built.</summary>
    public Document Document { get; }

    internal TextStyle BaseStyle { get; private set; } = TextStyle.Default;

    internal Dictionary<string, TextStyle> Styles { get; } = new(StringComparer.Ordinal);

    /// <summary>Sets the document's title.</summary>
    public ReportBuilder Title(string title)
    {
        Document.Properties.Title = title;
        return this;
    }

    /// <summary>Sets the document's author.</summary>
    public ReportBuilder Author(string author)
    {
        Document.Properties.Author = author;
        return this;
    }

    /// <summary>The text style every page starts from.</summary>
    public ReportBuilder DefaultTextStyle(Func<TextStyle, TextStyle> style)
    {
        ArgumentNullException.ThrowIfNull(style);
        BaseStyle = style(BaseStyle);
        return this;
    }

    /// <summary>
    /// A named text style that text takes with <see cref="TextDescriptor.Style(string)"/>; it is also a character style in the
    /// document, so DOCX keeps it by name.
    /// </summary>
    public ReportBuilder Style(string name, Func<TextStyle, TextStyle> style)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(style);
        TextStyle made = style(TextStyle.Default);
        Styles[name] = made;
        CharacterStyle character = Document.Styles[name] as CharacterStyle ?? Document.Styles.AddCharacterStyle(name);
        made.ApplyTo(character.RunFormat, TextStyle.Default);
        return this;
    }

    /// <summary>Adds a page, a section of the document: its size and margins, header, footer and content.</summary>
    public ReportBuilder Page(Action<PageDescriptor> page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Section section = Document.AddSection();
        PageDescriptor descriptor = new(this, section);
        page(descriptor);
        descriptor.Finish();
        return this;
    }
}

/// <summary>One page of a report, repeated as the content needs: size, margins, header, footer and content.</summary>
public sealed class PageDescriptor
{
    private readonly ReportBuilder report;
    private readonly Section section;
    private TextStyle style;

    internal PageDescriptor(ReportBuilder report, Section section)
    {
        this.report = report;
        this.section = section;
        style = report.BaseStyle;
        Size(PdfPageSize.A4);
        Margins(Length.Mm(20));
    }

    /// <summary>Sets the page size.</summary>
    public PageDescriptor Size(PdfPageSize size)
    {
        section.PageSetup.PageWidth = size.Width;
        section.PageSetup.PageHeight = size.Height;
        section.PageSetup.Orientation = size.Width > size.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
        return this;
    }

    /// <summary>Sets the page size.</summary>
    public PageDescriptor Size(Length width, Length height)
    {
        return Size(new PdfPageSize(width, height));
    }

    /// <summary>Turns the page so it is wider than tall.</summary>
    public PageDescriptor Landscape()
    {
        double width = section.PageSetup.PageWidth ?? 612, height = section.PageSetup.PageHeight ?? 792;
        return width >= height ? this : Size(new PdfPageSize(height, width));
    }

    /// <summary>The same margin on every side.</summary>
    public PageDescriptor Margins(Length all)
    {
        return Margins(all, all, all, all);
    }

    /// <summary>The margins top and bottom, and left and right.</summary>
    public PageDescriptor Margins(Length vertical, Length horizontal)
    {
        return Margins(vertical, horizontal, vertical, horizontal);
    }

    /// <summary>The margins on each side; the header sits in the top one's place and the footer in the bottom one's.</summary>
    public PageDescriptor Margins(Length top, Length right, Length bottom, Length left)
    {
        PageSetup setup = section.PageSetup;
        setup.MarginTop = top;
        setup.MarginRight = right;
        setup.MarginBottom = bottom;
        setup.MarginLeft = left;
        setup.HeaderDistance = top;
        setup.FooterDistance = bottom;
        return this;
    }

    /// <summary>The text style the page's content starts from.</summary>
    public PageDescriptor DefaultTextStyle(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        style = change(style);
        return this;
    }

    /// <summary>
    /// The header, at the top of every page; the content starts below it. What goes in it is added after what is there
    /// already, so the size, margins and text style come first.
    /// </summary>
    public Container Header()
    {
        HeaderFooter story = section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary);
        return new Container(Slot.Flow(report, story.Blocks, style, Width));
    }

    /// <summary>The footer, at the foot of every page; the content stops above it.</summary>
    public Container Footer()
    {
        HeaderFooter story = section.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterPrimary);
        return new Container(Slot.Flow(report, story.Blocks, style, Width));
    }

    /// <summary>The content, flowed onto as many pages as it takes.</summary>
    public Container Content()
    {
        return new Container(Slot.Flow(report, section.Body.Blocks, style, Width));
    }

    private double Width => (section.PageSetup.PageWidth ?? 612) - (section.PageSetup.MarginLeft ?? 72) - (section.PageSetup.MarginRight ?? 72);

    internal void Finish()
    {
        // Word wants a paragraph in every story; an empty content still makes a page.
        if (section.Body.Blocks.Count == 0)
        {
            section.Body.AddParagraph();
        }

        foreach (HeaderFooter story in section.HeadersFooters)
        {
            if (story.Blocks.Count == 0)
            {
                story.AddParagraph();
            }
        }
    }
}
