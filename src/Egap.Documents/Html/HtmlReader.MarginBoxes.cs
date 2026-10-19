using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>The @page margin boxes, read into the section's margin boxes.</summary>
internal sealed partial class HtmlReader
{
    private void ReadMarginBoxes(Section section, string? name, HtmlStyle rootStyle)
    {
        CssPageStyle every = cascade.CascadePage(new CssPage(name, IsFirst: false, IsLeft: false));
        CssPageStyle first = cascade.CascadePage(new CssPage(name, IsFirst: true, IsLeft: false));
        if (every.MarginBoxes.Count == 0 && first.MarginBoxes.Count == 0)
        {
            return;
        }

        HtmlStyle everyPage = new(cascade.Compute(every.Page, rootStyle.Computed), rootStyle, rootStyle.RootSize);
        HtmlStyle firstPage = new(cascade.Compute(first.Page, rootStyle.Computed), rootStyle, rootStyle.RootSize);
        foreach (CssMarginBox css in Enum.GetValues<CssMarginBox>())
        {
            MarginBoxPosition position = (MarginBoxPosition)(int)css;
            List<CssComponentValue>? content = Content(every, css, everyPage, out HtmlStyle? style);
            if (content is not null)
            {
                Fill(section.MarginBoxes.GetOrAdd(position), content, style!);
            }

            // The first page gets a box of its own only where its rules make it differ: other content, or none at all.
            List<CssComponentValue>? firstContent = Content(first, css, firstPage, out HtmlStyle? firstStyle);
            if (!SameBox(content, style, firstContent, firstStyle))
            {
                MarginBox box = section.MarginBoxes.GetOrAdd(position, firstPage: true);
                if (firstContent is not null)
                {
                    Fill(box, firstContent, firstStyle!);
                }
            }
        }
    }

    /// <summary>A margin box's computed content and style, or null when the box is not generated.</summary>
    private List<CssComponentValue>? Content(CssPageStyle page, CssMarginBox css, HtmlStyle pageStyle, out HtmlStyle? style)
    {
        style = null;
        if (!page.MarginBoxes.TryGetValue(css, out CssCascadedStyle? cascaded))
        {
            return null;
        }

        style = new HtmlStyle(cascade.Compute(cascaded, pageStyle.Computed), pageStyle, pageStyle.RootSize, cascaded);
        if (!style.Computed.TryGetValue("content", out IReadOnlyList<CssComponentValue>? value))
        {
            return null;
        }

        List<CssComponentValue> items = [.. value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
        if (items.Count == 0 || (items.Count == 1 && items[0] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } keyword } && keyword.Value.ToLowerInvariant() is "none" or "normal") || style.Keyword("display") == "none")
        {
            return null;
        }

        return items;
    }

    private bool SameBox(List<CssComponentValue>? a, HtmlStyle? aStyle, List<CssComponentValue>? b, HtmlStyle? bStyle)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.SequenceEqual(b) && aStyle!.Keyword("text-align") == bStyle!.Keyword("text-align") && RunFormatOf(aStyle).RawEquals(RunFormatOf(bStyle));
    }

    /// <summary>
    /// Writes a margin box's content: strings as text, counter(page) and counter(pages) as PAGE and NUMPAGES fields; what
    /// else the content holds is left out with a warning.
    /// </summary>
    private void Fill(MarginBox box, List<CssComponentValue> content, HtmlStyle style)
    {
        DocumentBuilder builder = new(document);
        builder.MoveToMarginBox(box.Position, box.FirstPage);
        builder.CurrentParagraph.Format.Alignment = style.FromUserAgent("text-align") ? MarginBox.DefaultAlignment(box.Position) : style.Keyword("text-align") switch
        {
            "center" or "-webkit-center" => ParagraphAlignment.Center,
            "right" or "end" or "-webkit-right" => ParagraphAlignment.Right,
            "justify" => ParagraphAlignment.Justify,
            _ => ParagraphAlignment.Left,
        };
        builder.Font.Apply(RunFormatOf(style));
        foreach (CssComponentValue item in content)
        {
            switch (item)
            {
                case CssTokenValue { Token: { Kind: CssTokenKind.String } token }:
                    builder.Write(token.Value);
                    break;
                case CssFunction function when function.NameIs("counter") && CounterName(function) is { } counter:
                    if (counter == "page")
                    {
                        builder.InsertField("PAGE", "1");
                    }
                    else if (counter == "pages")
                    {
                        builder.InsertField("NUMPAGES", "1");
                    }
                    else
                    {
                        Warn(ConversionWarningKind.PageLayoutDropped, $"The counter {counter} in a page margin box");
                    }

                    break;
                default:
                    Warn(ConversionWarningKind.PageLayoutDropped, $"The content {item} in a page margin box");
                    break;
            }
        }
    }

    private static string? CounterName(CssFunction function)
    {
        return function.Arguments.FirstOrDefault(a => a is CssTokenValue { Token.Kind: CssTokenKind.Ident }) is CssTokenValue { Token: { } token }
            ? token.Value.ToLowerInvariant()
            : null;
    }
}
