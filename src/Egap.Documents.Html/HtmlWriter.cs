using System.Globalization;
using System.Net;
using System.Text;

using Egami.Pixels;

namespace Egap.Documents.Html;

/// <summary>
/// Writes the model as semantic HTML5 (Phase 6 Build Plan task 26, design Q16): headings, paragraphs, lists, tables, links
/// and pictures; one style sheet with a class per style the document uses and inline styles only for direct formatting;
/// pictures as data URLs or through the caller's callback; the page setup and simple headers and footers as @page rules.
/// </summary>
internal sealed class HtmlWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly Document document;
    private readonly HtmlSaveOptions options;
    private readonly WarningCollector warnings = new();
    private readonly StringBuilder body = new();
    private readonly List<Footnote> notes = [];
    private readonly HashSet<Style> used = [];
    private readonly Dictionary<Style, string> classNames = [];

    private HtmlWriter(Document document, HtmlSaveOptions options)
    {
        this.document = document;
        this.options = options;
    }

    public static string Write(Document document, HtmlSaveOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        HtmlWriter writer = new(document, options);
        string html = writer.WriteDocument();
        warnings = writer.warnings.ToList();
        return html;
    }

    private string WriteDocument()
    {
        foreach (Section section in document.Sections)
        {
            if (section != document.Sections[0])
            {
                body.Append("<hr style=\"break-before: page; visibility: hidden; margin: 0\">\n");
            }

            WriteBlocks(section.Body.Blocks, 0);
        }

        WriteNotes();
        StringBuilder html = new();
        string? language = document.Styles.DefaultRunFormat.Language;
        html.Append("<!DOCTYPE html>\n<html").Append(language is null ? "" : $" lang=\"{Attribute(language)}\"").Append(">\n<head>\n<meta charset=\"utf-8\">\n");
        if (document.Properties.Title is { Length: > 0 } title)
        {
            html.Append("<title>").Append(Text(title)).Append("</title>\n");
        }

        if (document.Properties.Author is { Length: > 0 } author)
        {
            html.Append("<meta name=\"author\" content=\"").Append(Attribute(author)).Append("\">\n");
        }

        html.Append("<style>\n").Append(StyleSheet()).Append("</style>\n</head>\n<body>\n").Append(body).Append("</body>\n</html>\n");
        return html.ToString();
    }

    // ---- Style sheet --------------------------------------------------------------------------------------------------

    private string StyleSheet()
    {
        StringBuilder css = new();
        if (options.WritePageSetup && document.Sections.Count > 0)
        {
            PageSetup setup = document.Sections[0].PageSetup;
            css.Append("@page {");
            if (setup.PageWidth is { } width && setup.PageHeight is { } height)
            {
                css.Append(Invariant, $" size: {Points(width)} {Points(height)};");
            }

            css.Append(Invariant, $" margin: {Points(setup.MarginTop ?? 72)} {Points(setup.MarginRight ?? 72)} {Points(setup.MarginBottom ?? 72)} {Points(setup.MarginLeft ?? 72)};");
            HeaderFooterBoxes(document.Sections[0], css);
            css.Append(" }\n");
            FirstPageBoxes(document.Sections[0], css);
        }

        // The style classes carry everything; the browser's own heading and paragraph defaults are reset.
        css.Append("p, h1, h2, h3, h4, h5, h6, li, pre { margin: 0; }\nh1, h2, h3, h4, h5, h6 { font-size: inherit; font-weight: inherit; }\n");
        css.Append("ul, ol { margin: 0; }\ntable { border-collapse: collapse; }\ntd, th { vertical-align: top; text-align: inherit; font-weight: inherit; }\n");
        css.Append("body {").Append(Declarations(RunCss(document.Styles.DefaultRunFormat, null))).Append(" }\n");
        foreach (Style style in used.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            List<(string, string)> declarations = style switch
            {
                ParagraphStyle p => [.. ParagraphCss(Merged(p, s => (s as ParagraphStyle)?.ParagraphFormat, document.Styles.DefaultParagraphFormat)), .. RunCss(Merged(p, s => (s as ParagraphStyle)?.RunFormat, null), null)],
                CharacterStyle c => RunCss(Merged(c, s => (s as CharacterStyle)?.RunFormat, null), null),
                _ => [],
            };
            css.Append('.').Append(ClassOf(style)).Append(" {").Append(Declarations(declarations)).Append(" }\n");
        }

        return css.ToString();
    }

    /// <summary>A style's format with what its BasedOn chain sets beneath it, the first set value winning.</summary>
    private static T Merged<T>(Style style, Func<Style, T?> formatOf, T? defaults)
        where T : FormatBase, new()
    {
        T merged = new();
        foreach (Style link in style.BasedOnChain().Reverse())
        {
            if (formatOf(link) is { } format)
            {
                merged.Apply(format);
            }
        }

        if (defaults is not null)
        {
            T withDefaults = new();
            withDefaults.Apply(defaults);
            withDefaults.Apply(merged);
            return withDefaults;
        }

        return merged;
    }

    private string ClassOf(Style style)
    {
        if (!classNames.TryGetValue(style, out string? name))
        {
            StringBuilder clean = new(style is ParagraphStyle ? "p-" : "c-");
            foreach (char c in style.Id)
            {
                clean.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
            }

            name = clean.ToString();
            classNames[style] = name;
        }

        return name;
    }

    private void HeaderFooterBoxes(Section section, StringBuilder css)
    {
        Dictionary<FieldStart, FieldKind> kinds = document.GetFields().ToDictionary(f => f.Start, f => f.Kind);
        foreach ((HeaderFooterKind kind, MarginBoxPosition position) in (ReadOnlySpan<(HeaderFooterKind, MarginBoxPosition)>)[(HeaderFooterKind.HeaderPrimary, MarginBoxPosition.TopCenter), (HeaderFooterKind.FooterPrimary, MarginBoxPosition.BottomCenter)])
        {
            if (section.HeadersFooters[kind] is not { } story || section.MarginBoxes[position] is not null)
            {
                continue;
            }

            // A header or footer of text and page numbers becomes a margin box; anything more is left out.
            string text = ContentOf(story, kinds);
            if (text.Length > 0)
            {
                css.Append(' ').Append('@').Append(BoxName(position)).Append(" { content: ").Append(text).Append("; }");
            }

            if (story.Descendants<Picture>().Any() || story.Descendants<Table>().Any())
            {
                warnings.Add(ConversionWarningKind.PageLayoutDropped, "pictures and tables in headers and footers");
            }
        }

        foreach (MarginBox box in section.MarginBoxes.Where(b => !b.FirstPage))
        {
            MarginBoxRule(box, kinds, css);
        }
    }

    /// <summary>The first page's own margin boxes, as an @page :first rule.</summary>
    private void FirstPageBoxes(Section section, StringBuilder css)
    {
        if (!section.MarginBoxes.Any(b => b.FirstPage))
        {
            return;
        }

        Dictionary<FieldStart, FieldKind> kinds = document.GetFields().ToDictionary(f => f.Start, f => f.Kind);
        css.Append("@page :first {");
        foreach (MarginBox box in section.MarginBoxes.Where(b => b.FirstPage))
        {
            MarginBoxRule(box, kinds, css);
        }

        css.Append(" }\n");
    }

    private void MarginBoxRule(MarginBox box, Dictionary<FieldStart, FieldKind> kinds, StringBuilder css)
    {
        string text = ContentOf(box, kinds);
        css.Append(" @").Append(BoxName(box.Position)).Append(" { content: ").Append(text.Length > 0 ? text : box.Blocks.Count > 0 ? "\"\"" : "none").Append(';');
        if (box.Paragraphs.FirstOrDefault() is { } paragraph && paragraph.Format.Alignment is { } alignment && alignment != MarginBox.DefaultAlignment(box.Position))
        {
            css.Append(" text-align: ").Append(alignment switch
            {
                ParagraphAlignment.Center => "center",
                ParagraphAlignment.Right => "right",
                ParagraphAlignment.Justify => "justify",
                _ => "left",
            }).Append(';');
        }

        if (box.Paragraphs.Skip(1).Any())
        {
            // The paragraphs are joined by line feeds, which break lines only where white space is kept.
            css.Append(" white-space: pre;");
        }

        if (box.Paragraphs.SelectMany(p => p.Inlines).OfType<Run>().FirstOrDefault() is { } run)
        {
            css.Append(Declarations(RunCss(run.Format, null)));
        }

        css.Append(" }");
        if (box.Descendants<Picture>().Any() || box.Descendants<Table>().Any())
        {
            warnings.Add(ConversionWarningKind.PageLayoutDropped, "pictures and tables in margin boxes");
        }
    }

    /// <summary>A story's text and page numbers as a CSS content value, paragraphs joined by line breaks.</summary>
    private static string ContentOf(Story story, Dictionary<FieldStart, FieldKind> kinds)
    {
        StringBuilder content = new();
        bool firstParagraph = true;
        foreach (Paragraph paragraph in story.Paragraphs)
        {
            if (!firstParagraph)
            {
                content.Append(" \"\\A\"");
            }

            firstParagraph = false;
            FieldWalk(paragraph.Inlines, content, kinds);
        }

        return content.ToString().Trim();
    }

    private static string BoxName(MarginBoxPosition position) => position switch
    {
        MarginBoxPosition.TopLeftCorner => "top-left-corner",
        MarginBoxPosition.TopLeft => "top-left",
        MarginBoxPosition.TopCenter => "top-center",
        MarginBoxPosition.TopRight => "top-right",
        MarginBoxPosition.TopRightCorner => "top-right-corner",
        MarginBoxPosition.RightTop => "right-top",
        MarginBoxPosition.RightMiddle => "right-middle",
        MarginBoxPosition.RightBottom => "right-bottom",
        MarginBoxPosition.BottomRightCorner => "bottom-right-corner",
        MarginBoxPosition.BottomRight => "bottom-right",
        MarginBoxPosition.BottomCenter => "bottom-center",
        MarginBoxPosition.BottomLeft => "bottom-left",
        MarginBoxPosition.BottomLeftCorner => "bottom-left-corner",
        MarginBoxPosition.LeftBottom => "left-bottom",
        MarginBoxPosition.LeftMiddle => "left-middle",
        _ => "left-top",
    };

    /// <summary>A header's inlines as a CSS content value: strings, with PAGE and NUMPAGES fields as counters.</summary>
    private static void FieldWalk(NodeCollection<Inline> inlines, StringBuilder content, Dictionary<FieldStart, FieldKind> kinds)
    {
        int code = 0;
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case FieldStart start:
                    code++;
                    FieldKind kind = kinds.GetValueOrDefault(start);
                    if (kind == FieldKind.Page)
                    {
                        content.Append(" counter(page)");
                    }
                    else if (kind is FieldKind.NumPages or FieldKind.SectionPages)
                    {
                        content.Append(" counter(pages)");
                    }

                    break;
                case FieldSeparator:
                    code--;
                    code = Math.Max(0, code);
                    break;
                case FieldEnd:
                    break;
                case Run run when code == 0 && !InPageField(run):
                    content.Append(" \"").Append(run.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                    break;
                case Tab:
                    content.Append(" \"  \"");
                    break;
            }
        }
    }

    private static bool InPageField(Run run)
    {
        return run.Ancestors().OfType<Paragraph>().FirstOrDefault() is { } paragraph && paragraph.Document.GetFields()
            .Any(f => f.Kind is FieldKind.Page or FieldKind.NumPages or FieldKind.SectionPages && f.ResultNodes.Contains(run));
    }

    // ---- Blocks -------------------------------------------------------------------------------------------------------

    private void WriteBlocks(IEnumerable<Block> blocks, int depth)
    {
        List<Paragraph> list = [];
        foreach (Block block in blocks)
        {
            if (block is Paragraph { ListLabel: not null } item)
            {
                list.Add(item);
                continue;
            }

            FlushList(list);
            switch (block)
            {
                case Paragraph paragraph:
                    WriteParagraph(paragraph);
                    break;
                case Table table:
                    WriteTable(table, depth);
                    break;
                case ContentControl control:
                    warnings.Add(ConversionWarningKind.ContentControlsDropped, "content controls, written as their content");
                    WriteBlocks(control.Blocks, depth);
                    break;
                case BlockIsland island when island.Format == "html":
                    body.Append(island.Markup).Append('\n');
                    break;
                case BlockIsland island:
                    warnings.Add(ConversionWarningKind.DrawingDropped, "equations, charts and other kept objects");
                    if (island.GetText() is { Length: > 0 } islandText)
                    {
                        body.Append("<p>").Append(Text(islandText)).Append("</p>\n");
                    }

                    break;
            }
        }

        FlushList(list);
    }

    private void WriteParagraph(Paragraph paragraph)
    {
        ResolvedParagraphFormat effective = paragraph.GetEffectiveFormat();
        string tag = effective.OutlineLevel is >= 0 and < 6 ? "h" + (effective.OutlineLevel + 1).ToString(Invariant) : "p";
        if (paragraph.Extensions.Get<HtmlExtension>(HtmlExtension.Format) is { Tag: "pre" or "blockquote" or "address" or "figcaption" } extension && tag == "p")
        {
            tag = extension.Tag == "pre" ? "p" : extension.Tag;
        }

        body.Append('<').Append(tag);
        Attributes(paragraph, ParagraphCss(paragraph.Format), paragraph.Style ?? document.Styles.GetDefault(StyleType.Paragraph));
        body.Append('>');
        int before = body.Length;
        WriteInlines(paragraph.Inlines);
        if (body.Length == before)
        {
            // An empty paragraph keeps its line, as it does in Word.
            body.Append("<br>");
        }

        body.Append("</").Append(tag).Append(">\n");
    }

    /// <summary>The class, the id of a bookmark starting the paragraph, and the direct formatting as a style attribute.</summary>
    private void Attributes(Paragraph? paragraph, List<(string, string)> direct, Style? style)
    {
        if (style is not null)
        {
            used.Add(style);
            body.Append(" class=\"").Append(ClassOf(style)).Append('"');
        }

        if (paragraph?.Inlines.FirstOrDefault(i => i is BookmarkStart) is BookmarkStart bookmark)
        {
            body.Append(" id=\"").Append(Attribute(bookmark.Name)).Append('"');
        }

        if (direct.Count > 0)
        {
            body.Append(" style=\"").Append(Attribute(Declarations(direct).Trim())).Append('"');
        }
    }

    private void FlushList(List<Paragraph> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        Stack<(int Level, string Tag)> open = new();
        foreach (Paragraph item in items)
        {
            ResolvedParagraphFormat format = item.GetEffectiveFormat();
            int level = format.ListLevel;
            ListLevel definition = format.List!.Definition.Levels[Math.Clamp(level, 0, 8)];
            string tag = definition.NumberStyle == NumberStyle.Bullet ? "ul" : "ol";
            while (open.Count > 0 && (open.Peek().Level > level || (open.Peek().Level == level && open.Peek().Tag != tag)))
            {
                body.Append("</li></").Append(open.Pop().Tag).Append(">\n");
            }

            if (open.Count == 0 || open.Peek().Level < level)
            {
                body.Append('<').Append(tag);
                List<(string, string)> listCss = [("list-style-type", ListStyle(definition.NumberStyle, definition.LevelText))];
                if (tag == "ol" && definition.StartAt != 1)
                {
                    body.Append(Invariant, $" start=\"{definition.StartAt}\"");
                }

                body.Append(" style=\"").Append(Attribute(Declarations(listCss).Trim())).Append("\">\n");
                open.Push((level, tag));
            }
            else
            {
                body.Append("</li>\n");
            }

            body.Append("<li");
            List<(string, string)> direct = ParagraphCss(item.Format);
            direct.RemoveAll(d => d.Item1 is "margin-left" or "text-indent");
            Attributes(item, direct, item.Style ?? document.Styles.GetDefault(StyleType.Paragraph));
            body.Append('>');
            WriteInlines(item.Inlines);
        }

        while (open.Count > 0)
        {
            body.Append("</li></").Append(open.Pop().Tag).Append(">\n");
        }

        items.Clear();
    }

    private static string ListStyle(NumberStyle style, string text)
    {
        return style switch
        {
            NumberStyle.Bullet => text switch
            {
                "o" => "circle",
                "▪" or "■" or "" => "square",
                _ => "disc",
            },
            NumberStyle.LowerLetter => "lower-alpha",
            NumberStyle.UpperLetter => "upper-alpha",
            NumberStyle.LowerRoman => "lower-roman",
            NumberStyle.UpperRoman => "upper-roman",
            NumberStyle.DecimalZero => "decimal-leading-zero",
            NumberStyle.None => "none",
            _ => "decimal",
        };
    }

    private void WriteTable(Table table, int depth)
    {
        TableFormat format = table.GetEffectiveFormat();
        List<(string, string)> css = [];
        if (format.Width is { Unit: WidthUnit.Percent } percent)
        {
            css.Add(("width", percent.Value.ToString("0.##", Invariant) + "%"));
        }
        else if (format.Width is { Unit: WidthUnit.Points } points && points.Value > 0)
        {
            css.Add(("width", Points(points.Value)));
        }

        if (format.Alignment == TableAlignment.Center)
        {
            css.Add(("margin-left", "auto"));
            css.Add(("margin-right", "auto"));
        }
        else if (format.Indent is { } indent && indent != 0)
        {
            css.Add(("margin-left", Points(indent)));
        }

        AddBorders(css, format.BorderTop, format.BorderRight, format.BorderBottom, format.BorderLeft);
        body.Append("<table");
        if (css.Count > 0)
        {
            body.Append(" style=\"").Append(Attribute(Declarations(css).Trim())).Append('"');
        }

        body.Append(">\n");
        int headerRows = table.Rows.TakeWhile(r => r.Format.IsHeader == true).Count();
        for (int r = 0; r < table.Rows.Count; r++)
        {
            if (r == 0 && headerRows > 0)
            {
                body.Append("<thead>\n");
            }

            if (r == headerRows && headerRows > 0)
            {
                body.Append("</thead>\n<tbody>\n");
            }

            Row row = table.Rows[r];
            body.Append("<tr>");
            int column = 0;
            foreach (Cell cell in row.Cells)
            {
                int span = cell.Format.GridSpan ?? 1;
                if (cell.Format.VerticalMerge == VerticalMerge.Continue)
                {
                    column += span;
                    continue;
                }

                string tag = r < headerRows ? "th" : "td";
                body.Append('<').Append(tag);
                if (span > 1)
                {
                    body.Append(Invariant, $" colspan=\"{span}\"");
                }

                if (cell.Format.VerticalMerge == VerticalMerge.Restart && RowSpan(table, r, column) is var rows && rows > 1)
                {
                    body.Append(Invariant, $" rowspan=\"{rows}\"");
                }

                List<(string, string)> cellCss = CellCss(cell, format);
                if (cellCss.Count > 0)
                {
                    body.Append(" style=\"").Append(Attribute(Declarations(cellCss).Trim())).Append('"');
                }

                body.Append('>');
                WriteBlocks(cell.Blocks, depth + 1);
                body.Append("</").Append(tag).Append('>');
                column += span;
            }

            body.Append("</tr>\n");
        }

        if (headerRows > 0)
        {
            body.Append(headerRows == table.Rows.Count ? "</thead>\n" : "</tbody>\n");
        }

        body.Append("</table>\n");
        if (table.Descendants<Table>().Any())
        {
            // Nested tables are written as they are; HTML holds them.
        }
    }

    private static int RowSpan(Table table, int row, int column)
    {
        int rows = 1;
        for (int r = row + 1; r < table.Rows.Count; r++)
        {
            int at = 0;
            Cell? below = null;
            foreach (Cell cell in table.Rows[r].Cells)
            {
                if (at == column)
                {
                    below = cell;
                    break;
                }

                at += cell.Format.GridSpan ?? 1;
            }

            if (below?.Format.VerticalMerge != VerticalMerge.Continue)
            {
                break;
            }

            rows++;
        }

        return rows;
    }

    private List<(string, string)> CellCss(Cell cell, TableFormat table)
    {
        CellFormat format = cell.GetEffectiveFormat();
        List<(string, string)> css = [];
        AddBorders(css, format.BorderTop ?? table.BorderInsideHorizontal ?? table.BorderTop, format.BorderRight ?? table.BorderInsideVertical ?? table.BorderRight,
            format.BorderBottom ?? table.BorderInsideHorizontal ?? table.BorderBottom, format.BorderLeft ?? table.BorderInsideVertical ?? table.BorderLeft);
        double top = format.MarginTop ?? table.CellMarginTop ?? 0;
        double right = format.MarginRight ?? table.CellMarginRight ?? 5.4;
        double bottom = format.MarginBottom ?? table.CellMarginBottom ?? 0;
        double left = format.MarginLeft ?? table.CellMarginLeft ?? 5.4;
        css.Add(("padding", $"{Points(top)} {Points(right)} {Points(bottom)} {Points(left)}"));
        if (format.Shading is { } shading && Color(shading.Fill) is { } fill)
        {
            css.Add(("background-color", fill));
        }

        if (format.VerticalAlignment is VerticalAlignment.Center or VerticalAlignment.Bottom)
        {
            css.Add(("vertical-align", format.VerticalAlignment == VerticalAlignment.Center ? "middle" : "bottom"));
        }

        if (format.Width is { Unit: WidthUnit.Points } width && width.Value > 0)
        {
            css.Add(("width", Points(width.Value)));
        }
        else if (format.Width is { Unit: WidthUnit.Percent } percent)
        {
            css.Add(("width", percent.Value.ToString("0.##", Invariant) + "%"));
        }

        return css;
    }

    // ---- Inlines ------------------------------------------------------------------------------------------------------

    private void WriteInlines(NodeCollection<Inline> inlines)
    {
        int fieldCode = 0;
        Stack<bool> fields = new();
        foreach (Inline inline in inlines)
        {
            if (inline.Revision is { } revision)
            {
                warnings.Add(ConversionWarningKind.ChangesAccepted, "tracked changes, written as if accepted");
                if (revision.Removes)
                {
                    continue;
                }
            }

            if (fieldCode > 0 && inline is not (FieldStart or FieldSeparator or FieldEnd))
            {
                continue;
            }

            switch (inline)
            {
                case Run run:
                    WriteRun(run);
                    break;
                case Tab:
                    body.Append("<span style=\"white-space: pre\">\t</span>");
                    break;
                case Break b:
                    if (b.Kind == BreakKind.Line)
                    {
                        body.Append("<br>");
                    }
                    else
                    {
                        body.Append("<br style=\"break-after: ").Append(b.Kind == BreakKind.Column ? "column" : "page").Append("\">");
                    }

                    break;
                case Hyperlink link:
                    string href = link.Address is { } address ? address + (link.Anchor is { } a ? "#" + a : "") : "#" + link.Anchor;
                    body.Append("<a href=\"").Append(Attribute(href)).Append('"');
                    if (link.Tooltip is { } tooltip)
                    {
                        body.Append(" title=\"").Append(Attribute(tooltip)).Append('"');
                    }

                    body.Append('>');
                    WriteInlines(link.Inlines);
                    body.Append("</a>");
                    break;
                case InlineContentControl control:
                    warnings.Add(ConversionWarningKind.ContentControlsDropped, "content controls, written as their content");
                    WriteInlines(control.Inlines);
                    break;
                case Picture picture:
                    WritePicture(picture);
                    break;
                case BookmarkStart bookmark when inlines.IndexOf(bookmark) > 0 || bookmark.Parent is not Paragraph:
                    body.Append("<a id=\"").Append(Attribute(bookmark.Name)).Append("\"></a>");
                    break;
                case Footnote note:
                    notes.Add(note);
                    string number = notes.Count.ToString(Invariant);
                    body.Append("<sup><a href=\"#fn").Append(number).Append("\" id=\"fnref").Append(number).Append("\">").Append(Text(note.Mark)).Append("</a></sup>");
                    break;
                case Comment:
                    warnings.Add(ConversionWarningKind.CommentsDropped, "comments");
                    break;
                case Island island when island.Format == "html":
                    body.Append(island.Markup);
                    break;
                case Island island:
                    warnings.Add(ConversionWarningKind.DrawingDropped, "equations, charts and other kept objects" + (island.Text.Length > 0 ? ", written as their text" : ""));
                    body.Append(Text(island.Text));
                    break;
                case Shape shape:
                    if (shape.TextBox is { } story)
                    {
                        warnings.Add(ConversionWarningKind.DrawingDropped, "shapes, with text boxes written as their text");
                        foreach (Paragraph paragraph in story.Paragraphs)
                        {
                            body.Append("<span>");
                            WriteInlines(paragraph.Inlines);
                            body.Append("</span> ");
                        }
                    }
                    else
                    {
                        warnings.Add(ConversionWarningKind.DrawingDropped, "shapes");
                    }

                    break;
                case FieldStart:
                    fieldCode++;
                    fields.Push(true);
                    warnings.Add(ConversionWarningKind.FieldsFlattened, "fields, written as their results");
                    break;
                case FieldSeparator when fields.Count > 0:
                    fields.Pop();
                    fields.Push(false);
                    fieldCode--;
                    break;
                case FieldEnd when fields.Count > 0:
                    if (fields.Pop())
                    {
                        fieldCode--;
                    }

                    break;
            }
        }
    }

    private void WriteRun(Run run)
    {
        if (run.Text.Length == 0)
        {
            return;
        }

        List<(string, string)> css = RunCss(run.Format, run.Style);
        if (run.Style is { } style)
        {
            used.Add(style);
        }

        bool bold = run.Format.Bold == true && !css.Exists(c => c.Item1 == "font-weight" && c.Item2 != "bold");
        bool italic = run.Format.Italic == true;
        css.RemoveAll(c => (bold && c == ("font-weight", "bold")) || (italic && c == ("font-style", "italic")));
        string? tag = run.Format.VerticalPosition switch
        {
            VerticalTextPosition.Superscript => "sup",
            VerticalTextPosition.Subscript => "sub",
            _ => null,
        };
        css.RemoveAll(c => tag is not null && c.Item1 == "vertical-align");
        string text = Text(run.Text);
        if (bold)
        {
            text = "<strong>" + text + "</strong>";
        }

        if (italic)
        {
            text = "<em>" + text + "</em>";
        }

        if (tag is not null)
        {
            text = $"<{tag}>{text}</{tag}>";
        }

        if (css.Count == 0 && run.Style is null)
        {
            body.Append(text);
            return;
        }

        body.Append("<span");
        if (run.Style is { } characterStyle)
        {
            body.Append(" class=\"").Append(ClassOf(characterStyle)).Append('"');
        }

        if (css.Count > 0)
        {
            body.Append(" style=\"").Append(Attribute(Declarations(css).Trim())).Append('"');
        }

        body.Append('>').Append(text).Append("</span>");
    }

    private void WritePicture(Picture picture)
    {
        string? url = options.ImageWriter?.Invoke(picture);
        if (url is null && options.EmbedImages && picture.Image.Bytes.Length > 0)
        {
            url = $"data:{picture.Image.ContentType};base64,{Convert.ToBase64String(picture.Image.Bytes.Span)}";
        }

        url ??= picture.SourceUrl;
        if (url is null)
        {
            warnings.Add(ConversionWarningKind.PictureDropped, "pictures without an address (give HtmlSaveOptions.ImageWriter or EmbedImages)");
            return;
        }

        body.Append("<img src=\"").Append(Attribute(url)).Append("\" alt=\"").Append(Attribute(picture.Description ?? "")).Append('"');
        if (picture.Title is { } title)
        {
            body.Append(" title=\"").Append(Attribute(title)).Append('"');
        }

        List<(string, string)> css = [("width", Points(picture.Width)), ("height", Points(picture.Height))];
        if (picture.Floating is { } floating)
        {
            css.Add(("float", floating.HorizontalAlignment == FloatingAlignment.End ? "right" : "left"));
        }

        body.Append(" style=\"").Append(Attribute(Declarations(css).Trim())).Append("\">");
    }

    private void WriteNotes()
    {
        if (notes.Count == 0)
        {
            return;
        }

        body.Append("<section class=\"footnotes\">\n<hr>\n<ol>\n");
        for (int i = 0; i < notes.Count; i++)
        {
            string number = (i + 1).ToString(Invariant);
            body.Append("<li id=\"fn").Append(number).Append("\">");
            foreach (Paragraph paragraph in notes[i].Story.Paragraphs)
            {
                WriteInlines(paragraph.Inlines);
                body.Append(' ');
            }

            body.Append("<a href=\"#fnref").Append(number).Append("\">↩</a></li>\n");
        }

        body.Append("</ol>\n</section>\n");
    }

    // ---- CSS ----------------------------------------------------------------------------------------------------------

    private List<(string, string)> RunCss(RunFormat format, CharacterStyle? style)
    {
        _ = style;
        List<(string, string)> css = [];
        string? family = format.FontAscii ?? (format.ThemeFontAscii is { } theme ? document.Theme.GetFont(theme) : null);
        if (family is not null)
        {
            css.Add(("font-family", FontFamily(family)));
        }

        if (format.Size is { } size)
        {
            css.Add(("font-size", Points(size)));
        }

        if (format.Bold is { } bold)
        {
            css.Add(("font-weight", bold ? "bold" : "normal"));
        }

        if (format.Italic is { } italic)
        {
            css.Add(("font-style", italic ? "italic" : "normal"));
        }

        List<string> lines = [];
        if (format.Underline is { } underline)
        {
            lines.Add(underline == UnderlineStyle.None ? "" : "underline");
        }

        if (format.Strikethrough == true || format.DoubleStrikethrough == true)
        {
            lines.Add("line-through");
        }

        if (format.Overline == true)
        {
            lines.Add("overline");
        }

        if (lines.Count > 0)
        {
            string value = string.Join(' ', lines.Where(l => l.Length > 0));
            css.Add(("text-decoration", value.Length == 0 ? "none" : value));
        }

        if (format.Color is { } color && Color(color) is { } text)
        {
            css.Add(("color", text));
        }

        if (format.Shading is { } shading && Color(shading.Fill) is { } fill)
        {
            css.Add(("background-color", fill));
        }
        else if (format.Highlight is { } highlight && highlight != HighlightColor.None)
        {
            css.Add(("background-color", highlight.ToString().ToLowerInvariant() switch
            {
                "darkblue" => "darkblue",
                "darkcyan" => "darkcyan",
                "darkgreen" => "darkgreen",
                "darkmagenta" => "darkmagenta",
                "darkred" => "darkred",
                "darkyellow" => "olive",
                "darkgray" => "gray",
                "lightgray" => "lightgray",
                var named => named,
            }));
        }

        switch (format.VerticalPosition)
        {
            case VerticalTextPosition.Superscript:
                css.Add(("vertical-align", "super"));
                break;
            case VerticalTextPosition.Subscript:
                css.Add(("vertical-align", "sub"));
                break;
        }

        if (format.AllCaps == true)
        {
            css.Add(("text-transform", "uppercase"));
        }

        if (format.SmallCaps == true)
        {
            css.Add(("font-variant", "small-caps"));
        }

        if (format.Spacing is { } spacing && spacing != 0)
        {
            css.Add(("letter-spacing", Points(spacing)));
        }

        if (format.WordSpacing is { } wordSpacing && wordSpacing != 0)
        {
            css.Add(("word-spacing", Points(wordSpacing)));
        }

        if (format.Features is { Count: > 0 } features)
        {
            css.Add(("font-feature-settings", string.Join(", ", features.Select(f => $"'{f.Tag.TrimEnd()}' {f.Value.ToString(Invariant)}"))));
        }

        if (format.Hidden == true)
        {
            css.Add(("display", "none"));
        }

        return css;
    }

    private List<(string, string)> ParagraphCss(ParagraphFormat format)
    {
        List<(string, string)> css = [];
        if (format.Alignment is { } alignment)
        {
            css.Add(("text-align", alignment switch
            {
                ParagraphAlignment.Center => "center",
                ParagraphAlignment.Right => "right",
                ParagraphAlignment.Justify or ParagraphAlignment.Distribute => "justify",
                _ => "left",
            }));
        }

        if (format.LeftIndent is { } left)
        {
            css.Add(("margin-left", Points(left)));
        }

        if (format.RightIndent is { } right)
        {
            css.Add(("margin-right", Points(right)));
        }

        if (format.FirstLineIndent is { } first)
        {
            css.Add(("text-indent", Points(first)));
        }

        if (format.SpaceBefore is { } before)
        {
            css.Add(("margin-top", Points(before)));
        }

        if (format.SpaceAfter is { } after)
        {
            css.Add(("margin-bottom", Points(after)));
        }

        if (format.LineSpacing is { } spacing && format.LineSpacingRule is { } rule)
        {
            css.Add(("line-height", rule == LineSpacingRule.Multiple ? (spacing * 1.15).ToString("0.###", Invariant) : Points(spacing)));
        }

        if (format.MaxLines is int maxLines and > 0)
        {
            css.Add(("display", "-webkit-box"));
            css.Add(("-webkit-box-orient", "vertical"));
            css.Add(("-webkit-line-clamp", maxLines.ToString(Invariant)));
            css.Add(("overflow", "hidden"));
        }

        switch (format.WordBreak)
        {
            case WordBreak.BreakAll:
                css.Add(("word-break", "break-all"));
                break;
            case WordBreak.KeepAll:
                css.Add(("word-break", "keep-all"));
                break;
            case WordBreak.Anywhere:
                css.Add(("overflow-wrap", "anywhere"));
                break;
        }

        AddBorders(css, format.BorderTop, format.BorderRight, format.BorderBottom, format.BorderLeft);
        foreach ((Border? border, string side) in (ReadOnlySpan<(Border?, string)>)[(format.BorderTop, "top"), (format.BorderBottom, "bottom"), (format.BorderLeft, "left"), (format.BorderRight, "right")])
        {
            if (border is { IsVisible: true, Space: > 0 })
            {
                css.Add(("padding-" + side, Points(border.Space)));
            }
        }

        if (format.Shading is { } shading && Color(shading.Fill) is { } fill)
        {
            css.Add(("background-color", fill));
        }

        if (format.PageBreakBefore == true)
        {
            css.Add(("break-before", "page"));
        }

        if (format.KeepWithNext == true)
        {
            css.Add(("break-after", "avoid"));
        }

        if (format.KeepTogether == true)
        {
            css.Add(("break-inside", "avoid"));
        }

        return css;
    }

    private void AddBorders(List<(string, string)> css, Border? top, Border? right, Border? bottom, Border? left)
    {
        foreach ((Border? border, string side) in (ReadOnlySpan<(Border?, string)>)[(top, "top"), (right, "right"), (bottom, "bottom"), (left, "left")])
        {
            if (border is not { IsVisible: true })
            {
                continue;
            }

            string style = border.Style switch
            {
                BorderStyle.Dotted => "dotted",
                BorderStyle.Dashed or BorderStyle.DashSmallGap or BorderStyle.DotDash or BorderStyle.DotDotDash => "dashed",
                BorderStyle.Double => "double",
                _ => "solid",
            };
            css.Add(("border-" + side, $"{Points(Math.Max(border.Width, style == "double" ? 2.25 : 0.25))} {style} {Color(border.Color) ?? "black"}"));
        }
    }

    private string? Color(DocumentColor color)
    {
        if (color.Kind == DocumentColorKind.Auto)
        {
            return null;
        }

        Rgba8 rgb = color.Resolve(document.Theme, new Rgba8(0, 0, 0, 255));
        return $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}";
    }

    private static string FontFamily(string family)
    {
        string quoted = family.Contains(' ', StringComparison.Ordinal) || family.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ? $"\"{family.Replace("\"", "", StringComparison.Ordinal)}\"" : family;
        string generic = family switch
        {
            "Courier New" or "Consolas" or "Lucida Console" or "Courier" => "monospace",
            "Times New Roman" or "Georgia" or "Cambria" or "Garamond" or "Book Antiqua" or "Palatino Linotype" => "serif",
            _ => "sans-serif",
        };
        return quoted + ", " + generic;
    }

    private static string Points(double points)
    {
        return Math.Round(points, 2).ToString("0.##", Invariant) + "pt";
    }

    private static string Declarations(List<(string Name, string Value)> declarations)
    {
        StringBuilder css = new();
        foreach ((string name, string value) in declarations)
        {
            css.Append(' ').Append(name).Append(": ").Append(value).Append(';');
        }

        return css.ToString();
    }

    private static string Text(string text)
    {
        return WebUtility.HtmlEncode(Printable(text)).Replace("&#39;", "'", StringComparison.Ordinal);
    }

    /// <summary>Word's hyphen controls as Unicode's (U+001E non-breaking, U+001F optional); other control characters dropped.</summary>
    private static string Printable(string text)
    {
        if (!text.Any(c => char.IsControl(c) && c is not ('\t' or '\n')))
        {
            return text;
        }

        StringBuilder printable = new(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\u001E':
                    printable.Append('\u2011');
                    break;
                case '\u001F':
                    printable.Append('\u00AD');
                    break;
                case '\t' or '\n':
                    printable.Append(c);
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        printable.Append(c);
                    }

                    break;
            }
        }

        return printable.ToString();
    }

    private static string Attribute(string text)
    {
        return Printable(text).Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
    }
}
