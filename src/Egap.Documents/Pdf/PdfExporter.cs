using System.Text;

using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>One export of a layout to a new PDF (Build Plan task 22).</summary>
internal sealed class PdfExporter(DocumentLayout layout, PdfExportOptions options)
{
    private readonly Document document = layout.Document;
    private readonly List<PdfPage> pages = [];
    private readonly Dictionary<PageLayout, PdfPage> pageOf = [];
    private PdfDocument pdf = null!;
    private PdfTagger? tagger;
    private MarginComments? margin;

    public PdfDocument Export()
    {
        pdf = PdfDocument.Create(new PdfCreateOptions { Conformance = options.Conformance, UniversalAccessibility = options.UniversalAccessibility });
        try
        {
            string? language = options.Language ?? document.Styles.DefaultRunFormat.Language;
            if (options.IsTagged)
            {
                tagger = new PdfTagger(pdf, document, language);
            }

            if (options.Comments == PdfCommentMode.Margin)
            {
                if (options.FormFields)
                {
                    throw new ArgumentException("Form fields cannot be written with comments in the margin: the fields would not follow the scaled page.", nameof(options));
                }

                margin = new MarginComments(layout, options.Layout, tagger, options.IncludeResolvedComments);
            }

            foreach (PageLayout page in layout.Pages)
            {
                PdfPage pdfPage = pdf.Pages.AddBlank(new PdfRectangle(0, 0, page.Width, page.Height));
                pages.Add(pdfPage);
                pageOf[page] = pdfPage;
                using PdfCanvas canvas = new(pdfPage);
                if (margin is not null)
                {
                    margin.DrawPage(page, canvas, tagger?.ObserverFor(canvas));
                }
                else
                {
                    page.Draw(canvas, tagger?.ObserverFor(canvas));
                }
            }

            margin?.AddOverflow(pdf);

            if (options.FormFields)
            {
                FormFieldExport.Add(layout, pdf, pageOf);
            }

            WriteMetadata(language);
            if (options.EmbedSource)
            {
                HybridPdf.Embed(pdf, document, options.Conformance);
            }

            Dictionary<string, PdfDestination> bookmarks = Bookmarks();
            if (options.NamedDestinations)
            {
                foreach ((string name, PdfDestination destination) in bookmarks)
                {
                    pdf.NamedDestinations.Set(name, destination);
                }
            }

            if (options.Links)
            {
                AddLinks(bookmarks);
            }

            if (options.Comments == PdfCommentMode.Annotations)
            {
                new CommentExport(layout, pageOf, tagger, options.IncludeResolvedComments).Write();
            }

            switch (options.Outline)
            {
                case OutlineFrom.Headings:
                    OutlineFromHeadings();
                    break;
                case OutlineFrom.Bookmarks:
                    foreach ((string name, PdfDestination destination) in bookmarks.Where(b => !b.Key.StartsWith('_')))
                    {
                        pdf.Outline.Add(name, destination);
                    }

                    break;
            }

            return pdf;
        }
        catch
        {
            pdf.Dispose();
            throw;
        }
    }

    private void WriteMetadata(string? language)
    {
        DocumentProperties properties = document.Properties;
        PdfMetadata metadata = pdf.Metadata;
        metadata.Title = Clean(properties.Title);
        metadata.Author = Clean(properties.Author);
        metadata.Subject = Clean(properties.Subject);
        metadata.Keywords = Clean(properties.Keywords);
        metadata.Creator = "Egap.Documents";
        metadata.CreationDate = properties.Created;
        metadata.ModificationDate = properties.Saved;
        if (!string.IsNullOrWhiteSpace(language))
        {
            pdf.Catalog["Lang"] = PdfString.FromText(language);
        }

        static string? Clean(string? text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }

    /// <summary>Each bookmark's destination: the top of the first line of the paragraph it starts in.</summary>
    private Dictionary<string, PdfDestination> Bookmarks()
    {
        Dictionary<string, PdfDestination> destinations = new(StringComparer.Ordinal);
        foreach (BookmarkStart bookmark in document.Descendants<BookmarkStart>())
        {
            if (!destinations.ContainsKey(bookmark.Name) && bookmark.Ancestors().OfType<Paragraph>().FirstOrDefault() is { } paragraph
                && Destination(paragraph) is { } destination)
            {
                destinations[bookmark.Name] = destination;
            }
        }

        return destinations;
    }

    private PdfDestination? Destination(Paragraph paragraph)
    {
        foreach ((PageLayout page, LayoutBox box) in layout.FindBoxes(paragraph))
        {
            if (box is LineBox line)
            {
                LayoutRect at = margin is null ? line.Bounds : MarginComments.Map(page, line.Bounds);
                return PdfDestination.ToPage(pageOf[page], PdfDestinationFit.Xyz) with { Left = at.X, Top = page.Height - at.Y };
            }
        }

        return null;
    }

    private void AddLinks(Dictionary<string, PdfDestination> bookmarks)
    {
        foreach (Hyperlink link in document.Descendants<Hyperlink>())
        {
            if (Action(link.Address, link.Anchor, bookmarks) is { } action)
            {
                AddLink(link, layout.GetTextRects(link), action, link.Tooltip ?? link.Address ?? link.Anchor);
            }
        }

        // Word writes table of contents entries and many links as HYPERLINK fields.
        foreach (Field field in document.GetFields().Where(f => f.Kind == FieldKind.Hyperlink))
        {
            (string? address, string? anchor) = ParseHyperlink(field.Code);
            if (Action(address, anchor, bookmarks) is { } action)
            {
                List<PageRect> rects = [.. field.ResultNodes.OfType<Inline>().SelectMany(layout.GetTextRects)];
                AddLink(field.Start, Merge(rects), action, address ?? anchor);
            }
        }
    }

    private Func<PdfAction>? Action(string? address, string? anchor, Dictionary<string, PdfDestination> bookmarks)
    {
        if (!string.IsNullOrEmpty(address))
        {
            string uri = string.IsNullOrEmpty(anchor) ? address : address + "#" + anchor;
            return () => new PdfUriAction(uri);
        }

        if (!string.IsNullOrEmpty(anchor) && bookmarks.TryGetValue(anchor, out PdfDestination? destination))
        {
            return options.NamedDestinations ? () => new PdfGoToAction(anchor) : () => new PdfGoToAction(destination);
        }

        return null;
    }

    private void AddLink(Inline node, IReadOnlyList<PageRect> rects, Func<PdfAction> action, string? description)
    {
        foreach (PageRect rect in rects)
        {
            if (rect.Bounds.Width <= 0)
            {
                continue;
            }

            PdfPage page = pageOf[rect.Page];
            double height = rect.Page.Height;
            LayoutRect b = margin is null ? rect.Bounds : MarginComments.Map(rect.Page, rect.Bounds);
            // Each annotation gets its own action dictionary: a PDF object lives in one container.
            PdfLinkAnnotation annotation = new(new PdfRectangle(b.X, height - b.Bottom, b.Right, height - b.Y), action())
            {
                BorderWidth = 0,
                Contents = description,
            };
            page.Annotations.Add(annotation);
            tagger?.TagLink(node, page, annotation);
        }
    }

    /// <summary>Rectangles side by side on one line (a field's result runs) joined into one.</summary>
    private static List<PageRect> Merge(List<PageRect> rects)
    {
        List<PageRect> merged = [];
        foreach (PageRect rect in rects)
        {
            if (merged.Count > 0 && merged[^1] is { } last && last.Page == rect.Page && Math.Abs(last.Bounds.Y - rect.Bounds.Y) < 0.5
                && rect.Bounds.X <= last.Bounds.Right + 0.5 && rect.Bounds.Right >= last.Bounds.X - 0.5)
            {
                double x = Math.Min(last.Bounds.X, rect.Bounds.X);
                double right = Math.Max(last.Bounds.Right, rect.Bounds.Right);
                merged[^1] = last with { Bounds = new LayoutRect(x, last.Bounds.Y, right - x, Math.Max(last.Bounds.Height, rect.Bounds.Height)) };
            }
            else
            {
                merged.Add(rect);
            }
        }

        return merged;
    }

    /// <summary>The address and the <c>\l</c> location of a HYPERLINK field's code.</summary>
    internal static (string? Address, string? Anchor) ParseHyperlink(string code)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool quoted = false;
        bool any = false;
        foreach (char c in code)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }

        Flush();
        string? address = null;
        string? anchor = null;
        for (int i = 1; i < words.Count; i++)
        {
            if (words[i].Equals("\\l", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count)
            {
                anchor = words[++i];
            }
            else if (words[i].StartsWith('\\'))
            {
                // \o, \t and \n take an argument or none; only \o and \t's arguments are skipped.
                if (words[i] is "\\o" or "\\t" or "\\O" or "\\T")
                {
                    i++;
                }
            }
            else
            {
                address ??= words[i];
            }
        }

        return (address, anchor);

        void Flush()
        {
            if (any)
            {
                words.Add(word.ToString());
            }

            word.Clear();
            any = false;
        }
    }

    private void OutlineFromHeadings()
    {
        Stack<(int Level, PdfOutlineNode Node)> open = new();
        foreach (Paragraph paragraph in document.Descendants<Paragraph>())
        {
            int level = paragraph.GetEffectiveFormat().OutlineLevel;
            if (level >= 9 || !paragraph.Ancestors().OfType<Body>().Any() || Destination(paragraph) is not { } destination)
            {
                continue;
            }

            string title = Title(paragraph);
            if (title.Length == 0)
            {
                continue;
            }

            while (open.Count > 0 && open.Peek().Level >= level)
            {
                open.Pop();
            }

            PdfOutlineNode parent = open.Count > 0 ? open.Peek().Node : pdf.Outline;
            open.Push((level, parent.Add(title, destination)));
        }
    }

    /// <summary>A heading's text as laid out (list label and field results included), on one line.</summary>
    private string Title(Paragraph paragraph)
    {
        StringBuilder text = new();
        foreach ((_, LayoutBox box) in layout.FindBoxes(paragraph))
        {
            if (box is LineBox line)
            {
                text.Append(line.Text).Append(' ');
            }
        }

        StringBuilder title = new(text.Length);
        bool space = false;
        foreach (char c in text.ToString())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = title.Length > 0;
            }
            else
            {
                if (space)
                {
                    title.Append(' ');
                    space = false;
                }

                title.Append(c);
            }
        }

        return title.ToString();
    }
}
