
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>Whether <see cref="PdfConvert"/> gives the PDF a table of contents built from the headings.</summary>
public enum TableOfContentsMode
{
    /// <summary>None is added; tables of contents the input has are kept as they are.</summary>
    None,

    /// <summary>A table of contents with page numbers, each entry linked to its heading.</summary>
    WithPageNumbers,

    /// <summary>A table of contents of linked entries, without page numbers.</summary>
    WithoutPageNumbers,
}

/// <summary>Puts a table of contents in a document for conversion.</summary>
internal static class TableOfContentsPlacement
{
    /// <summary>
    /// Builds the document's tables of contents, adding one where there is none: at a paragraph that reads [TOC] (as
    /// Markdown writes the placeholder), else at the start. HTML's nav data-toc is read as one already.
    /// </summary>
    public static void Apply(Document document, TableOfContentsMode mode)
    {
        if (mode == TableOfContentsMode.None)
        {
            return;
        }

        List<Field> fields = [.. document.GetFields().Where(f => f.Kind == FieldKind.Toc)];
        if (fields.Count == 0)
        {
            DocumentBuilder builder = new(document);
            Paragraph? marker = document.Sections.SelectMany(s => s.Body.Descendants<Paragraph>())
                .FirstOrDefault(p => p.GetText().Trim().Equals("[TOC]", StringComparison.OrdinalIgnoreCase));
            if (marker is not null)
            {
                foreach (Inline inline in marker.Inlines.ToList())
                {
                    inline.Remove();
                }

                builder.MoveTo(marker);
            }
            else
            {
                builder.MoveToDocumentStart();
            }

            Field field = builder.InsertField("TOC \\o \"1-3\" \\h \\z \\u");
            if (marker is null && builder.CurrentNode is not null)
            {
                // The entries go in a paragraph of their own, ahead of the text that was first.
                builder.InsertParagraph();
            }

            fields.Add(field);
        }

        foreach (Field toc in fields)
        {
            Field current = toc.Start.GetField() ?? toc;
            bool hasNoPages = current.Code.Contains("\\n", StringComparison.OrdinalIgnoreCase);
            if (mode == TableOfContentsMode.WithoutPageNumbers && !hasNoPages && current.CodeNodes.OfType<Run>().LastOrDefault() is { } last)
            {
                last.Text = last.Text.TrimEnd() + " \\n ";
            }

            current = current.Start.GetField() ?? current;
            current.Update();
        }
    }
}
