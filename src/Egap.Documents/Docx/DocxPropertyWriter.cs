using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

namespace Egap.Documents.Docx;

/// <summary>
/// Writes the model's formats as DOCX property elements (rPr, pPr, tblPr, trPr, tcPr, sectPr) in schema order, with the
/// children and attributes the reader kept from the original element merged back in their schema place.
/// </summary>
internal static class DocxPropertyWriter
{
    private static readonly string[] RunOrder =
    [
        "ins", "del", "moveFrom", "moveTo", "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow",
        "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz", "szCs", "highlight",
        "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath", "rPrChange",
    ];

    // Word 2010's run properties, after the w: ones: the text effects, then the OpenType features (w14 CT_RPr extension).
    private static readonly string[] W14RunOrder =
    [
        "glow", "shadow", "reflection", "textOutline", "textFill", "scene3d", "props3d", "ligatures", "numForm", "numSpacing", "stylisticSets",
        "cntxtAlts",
    ];

    private static readonly string[] ParagraphOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers", "pBdr", "shd", "tabs",
        "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd",
        "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection", "textAlignment",
        "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr", "sectPr", "pPrChange",
    ];

    private static readonly string[] TableOrder =
    [
        "tblStyle", "tblpPr", "tblOverlap", "bidiVisual", "tblStyleRowBandSize", "tblStyleColBandSize", "tblW", "jc", "tblCellSpacing", "tblInd",
        "tblBorders", "shd", "tblLayout", "tblCellMar", "tblLook", "tblCaption", "tblDescription", "tblPrChange",
    ];

    private static readonly string[] RowOrder =
    [
        "cnfStyle", "divId", "gridBefore", "gridAfter", "wBefore", "wAfter", "cantSplit", "trHeight", "tblHeader", "tblCellSpacing", "jc", "hidden",
        "ins", "del", "trPrChange",
    ];

    private static readonly string[] CellOrder =
    [
        "cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar", "textDirection", "tcFitText", "vAlign", "hideMark",
        "headers", "cellIns", "cellDel", "cellMerge", "tcPrChange",
    ];

    private static readonly string[] SectionOrder =
    [
        "headerReference", "footerReference", "footnotePr", "endnotePr", "type", "pgSz", "pgMar", "paperSrc", "pgBorders", "lnNumType", "pgNumType",
        "cols", "formProt", "vAlign", "noEndnote", "titlePg", "textDirection", "bidi", "rtlGutter", "docGrid", "printerSettings", "sectPrChange",
    ];

    private static readonly string[] SideOrder = ["top", "left", "start", "bottom", "right", "end", "between", "bar", "insideH", "insideV", "tl2br", "tr2bl"];

    public static XElement? Run(RunFormat format, CharacterStyle? style, IEnumerable<XElement>? kept, params XElement?[] extra)
    {
        List<XElement> children = [.. extra.OfType<XElement>()];
        if (style is not null)
        {
            children.Add(Val("rStyle", style.Id));
        }

        AddRun(format, children);
        return Merge("rPr", children, kept, RunOrder);
    }

    private static void AddRun(RunFormat f, List<XElement> c)
    {
        XElement fonts = new(Ns.W + "rFonts");
        SetAttribute(fonts, "ascii", f.FontAscii);
        SetAttribute(fonts, "hAnsi", f.FontHighAnsi);
        SetAttribute(fonts, "eastAsia", f.FontEastAsia);
        SetAttribute(fonts, "cs", f.FontComplexScript);
        SetAttribute(fonts, "asciiTheme", ThemeFontName(f.ThemeFontAscii, ascii: true));
        SetAttribute(fonts, "hAnsiTheme", ThemeFontName(f.ThemeFontHighAnsi, ascii: false));
        SetAttribute(fonts, "eastAsiaTheme", ThemeFontName(f.ThemeFontEastAsia, ascii: false));
        SetAttribute(fonts, "cstheme", ThemeFontName(f.ThemeFontComplexScript, ascii: false));
        if (fonts.HasAttributes)
        {
            c.Add(fonts);
        }

        OnOff(c, "b", f.Bold);
        OnOff(c, "bCs", f.BoldComplexScript);
        OnOff(c, "i", f.Italic);
        OnOff(c, "iCs", f.ItalicComplexScript);
        OnOff(c, "caps", f.AllCaps);
        OnOff(c, "smallCaps", f.SmallCaps);
        OnOff(c, "strike", f.Strikethrough);
        OnOff(c, "dstrike", f.DoubleStrikethrough);
        OnOff(c, "outline", f.Outline);
        OnOff(c, "shadow", f.Shadow);
        OnOff(c, "emboss", f.Emboss);
        OnOff(c, "imprint", f.Imprint);
        OnOff(c, "noProof", f.NoProof);
        OnOff(c, "vanish", f.Hidden);
        if (f.Color is { } color)
        {
            c.Add(Color(new XElement(Ns.W + "color"), color, "val", "themeColor", "themeTint", "themeShade"));
        }

        if (f.Spacing is double spacing)
        {
            c.Add(Val("spacing", DocxUnits.Twips(spacing)));
        }

        if (f.Scale is double scale)
        {
            c.Add(Val("w", Math.Round(scale).ToString(CultureInfo.InvariantCulture)));
        }

        if (f.Kerning is double kerning)
        {
            c.Add(Val("kern", DocxUnits.HalfPoints(kerning)));
        }

        if (f.Position is double position)
        {
            c.Add(Val("position", DocxUnits.HalfPoints(position)));
        }

        if (f.Size is double size)
        {
            c.Add(Val("sz", DocxUnits.HalfPoints(size)));
        }

        if (f.SizeComplexScript is double sizeCs)
        {
            c.Add(Val("szCs", DocxUnits.HalfPoints(sizeCs)));
        }

        if (f.Highlight is { } highlight)
        {
            c.Add(Val("highlight", HighlightName(highlight)));
        }

        if (f.Underline is { } underline)
        {
            XElement u = Val("u", UnderlineName(underline));
            if (f.UnderlineColor is { } underlineColor)
            {
                Color(u, underlineColor, "color", "themeColor", "themeTint", "themeShade");
            }

            c.Add(u);
        }

        if (f.Border is { } border)
        {
            c.Add(Border("bdr", border));
        }

        if (f.Shading is { } shading)
        {
            c.Add(Shading(shading));
        }

        if (f.VerticalPosition is { } vertical)
        {
            c.Add(Val("vertAlign", vertical switch
            {
                VerticalTextPosition.Superscript => "superscript",
                VerticalTextPosition.Subscript => "subscript",
                _ => "baseline",
            }));
        }

        OnOff(c, "rtl", f.RightToLeft);
        OnOff(c, "cs", f.ComplexScript);
        if (f.Language is not null || f.LanguageEastAsia is not null || f.LanguageBidi is not null)
        {
            XElement lang = new(Ns.W + "lang");
            SetAttribute(lang, "val", f.Language);
            SetAttribute(lang, "eastAsia", f.LanguageEastAsia);
            SetAttribute(lang, "bidi", f.LanguageBidi);
            c.Add(lang);
        }

        OnOff(c, "specVanish", f.SpecialHidden);

        DocxFeatures.Write(f.Features, c);
    }

    public static XElement? Paragraph(ParagraphFormat f, ParagraphStyle? style, IEnumerable<XElement>? kept, params XElement?[] extra)
    {
        List<XElement> c = [.. extra.OfType<XElement>()];
        if (style is not null)
        {
            c.Add(Val("pStyle", style.Id));
        }

        AddParagraph(f, c);
        return Merge("pPr", c, kept, ParagraphOrder);
    }

    private static void AddParagraph(ParagraphFormat f, List<XElement> c)
    {
        OnOff(c, "keepNext", f.KeepWithNext);
        OnOff(c, "keepLines", f.KeepTogether);
        OnOff(c, "pageBreakBefore", f.PageBreakBefore);
        OnOff(c, "widowControl", f.WidowControl);
        if (f.List is not null || f.ListLevel is not null)
        {
            XElement numPr = new(Ns.W + "numPr");
            if (f.ListLevel is int level)
            {
                numPr.Add(Val("ilvl", level.ToString(CultureInfo.InvariantCulture)));
            }

            if (f.List is { } list)
            {
                numPr.Add(Val("numId", list.Id.ToString(CultureInfo.InvariantCulture)));
            }

            c.Add(numPr);
        }

        OnOff(c, "suppressLineNumbers", f.SuppressLineNumbers);
        XElement borders = Sides("pBdr", ("top", f.BorderTop), ("left", f.BorderLeft), ("bottom", f.BorderBottom), ("right", f.BorderRight), ("between", f.BorderBetween), ("bar", f.BorderBar));
        if (borders.HasElements)
        {
            c.Add(borders);
        }

        if (f.Shading is { } shading)
        {
            c.Add(Shading(shading));
        }

        if (f.TabStops is { } tabs)
        {
            c.Add(new XElement(Ns.W + "tabs", tabs.Select(Tab)));
        }

        OnOff(c, "suppressAutoHyphens", f.SuppressAutoHyphens);
        OnOff(c, "bidi", f.Bidi);
        OnOff(c, "snapToGrid", f.SnapToGrid);
        XElement spacing = new(Ns.W + "spacing");
        SetAttribute(spacing, "before", f.SpaceBefore is double before ? DocxUnits.Twips(before) : null);
        SetAttribute(spacing, "beforeAutospacing", f.SpaceBeforeAuto is bool beforeAuto ? (beforeAuto ? "1" : "0") : null);
        SetAttribute(spacing, "after", f.SpaceAfter is double after ? DocxUnits.Twips(after) : null);
        SetAttribute(spacing, "afterAutospacing", f.SpaceAfterAuto is bool afterAuto ? (afterAuto ? "1" : "0") : null);
        if (f.LineSpacing is double line)
        {
            LineSpacingRule rule = f.LineSpacingRule ?? LineSpacingRule.Multiple;
            spacing.SetAttributeValue(Ns.W + "line", rule == LineSpacingRule.Multiple ? Math.Round(line * 240).ToString(CultureInfo.InvariantCulture) : DocxUnits.Twips(line));
            spacing.SetAttributeValue(Ns.W + "lineRule", rule switch
            {
                LineSpacingRule.Exactly => "exact",
                LineSpacingRule.AtLeast or LineSpacingRule.AtLeastCentered => "atLeast",
                _ => "auto",
            });
        }

        if (spacing.HasAttributes)
        {
            c.Add(spacing);
        }

        XElement ind = new(Ns.W + "ind");
        SetAttribute(ind, "left", f.LeftIndent is double left ? DocxUnits.Twips(left) : null);
        SetAttribute(ind, "right", f.RightIndent is double right ? DocxUnits.Twips(right) : null);
        if (f.FirstLineIndent is double first)
        {
            ind.SetAttributeValue(Ns.W + (first < 0 ? "hanging" : "firstLine"), DocxUnits.Twips(Math.Abs(first)));
        }

        if (ind.HasAttributes)
        {
            c.Add(ind);
        }

        OnOff(c, "contextualSpacing", f.ContextualSpacing);
        OnOff(c, "mirrorIndents", f.MirrorIndents);
        if (f.Alignment is { } alignment)
        {
            c.Add(Val("jc", alignment switch
            {
                ParagraphAlignment.Center => "center",
                ParagraphAlignment.Right => "right",
                ParagraphAlignment.Justify => f.Kashida switch
                {
                    KashidaLevel.Low => "lowKashida",
                    KashidaLevel.Medium => "mediumKashida",
                    KashidaLevel.High => "highKashida",
                    _ => "both",
                },
                ParagraphAlignment.Distribute => "distribute",
                _ => "left",
            }));
        }

        if (f.OutlineLevel is int outline)
        {
            c.Add(Val("outlineLvl", outline.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public static XElement? Table(TableFormat f, TableStyle? style, IEnumerable<XElement>? kept)
    {
        List<XElement> c = [];
        if (style is not null)
        {
            c.Add(Val("tblStyle", style.Id));
        }

        OnOff(c, "bidiVisual", f.Bidi);
        if (f.RowBandSize is int rowBand)
        {
            c.Add(Val("tblStyleRowBandSize", rowBand.ToString(CultureInfo.InvariantCulture)));
        }

        if (f.ColumnBandSize is int columnBand)
        {
            c.Add(Val("tblStyleColBandSize", columnBand.ToString(CultureInfo.InvariantCulture)));
        }

        if (f.Width is { } width)
        {
            c.Add(Width("tblW", width));
        }

        if (f.Alignment is { } alignment)
        {
            c.Add(Val("jc", TableAlignmentName(alignment)));
        }

        if (f.CellSpacing is double cellSpacing)
        {
            c.Add(Width("tblCellSpacing", TableWidth.Points(cellSpacing)));
        }

        if (f.Indent is double indent)
        {
            c.Add(Width("tblInd", TableWidth.Points(indent)));
        }

        XElement borders = Sides("tblBorders", ("top", f.BorderTop), ("left", f.BorderLeft), ("bottom", f.BorderBottom), ("right", f.BorderRight),
            ("insideH", f.BorderInsideHorizontal), ("insideV", f.BorderInsideVertical));
        if (borders.HasElements)
        {
            c.Add(borders);
        }

        if (f.Shading is { } shading)
        {
            c.Add(Shading(shading));
        }

        if (f.Layout is { } layout)
        {
            c.Add(new XElement(Ns.W + "tblLayout", new XAttribute(Ns.W + "type", layout == TableLayoutKind.Fixed ? "fixed" : "autofit")));
        }

        XElement margins = Margins("tblCellMar", f.CellMarginTop, f.CellMarginLeft, f.CellMarginBottom, f.CellMarginRight);
        if (margins.HasElements)
        {
            c.Add(margins);
        }

        if (f.LookFirstRow is not null || f.LookLastRow is not null || f.LookFirstColumn is not null || f.LookLastColumn is not null
            || f.LookNoRowBands is not null || f.LookNoColumnBands is not null)
        {
            int mask = (f.LookFirstRow == true ? 0x20 : 0) | (f.LookLastRow == true ? 0x40 : 0) | (f.LookFirstColumn == true ? 0x80 : 0)
                | (f.LookLastColumn == true ? 0x100 : 0) | (f.LookNoRowBands == true ? 0x200 : 0) | (f.LookNoColumnBands == true ? 0x400 : 0);
            XElement look = Val("tblLook", mask.ToString("X4", CultureInfo.InvariantCulture));
            SetAttribute(look, "firstRow", Flag(f.LookFirstRow));
            SetAttribute(look, "lastRow", Flag(f.LookLastRow));
            SetAttribute(look, "firstColumn", Flag(f.LookFirstColumn));
            SetAttribute(look, "lastColumn", Flag(f.LookLastColumn));
            SetAttribute(look, "noHBand", Flag(f.LookNoRowBands));
            SetAttribute(look, "noVBand", Flag(f.LookNoColumnBands));
            c.Add(look);
        }

        return Merge("tblPr", c, kept, TableOrder);

        static string? Flag(bool? value)
        {
            return value is bool b ? (b ? "1" : "0") : null;
        }
    }

    public static XElement? Row(RowFormat f, IEnumerable<XElement>? kept, params XElement?[] extra)
    {
        List<XElement> c = [.. extra.OfType<XElement>()];

        // The row flags are ST_OnOffOnly in the schema: "on" and "off", not "1" and "0".
        OnOff(c, "cantSplit", f.CantSplit, onOffOnly: true);
        if (f.Height is double height)
        {
            XElement h = Val("trHeight", DocxUnits.Twips(height));
            if (f.HeightRule is { } rule && rule != RowHeightRule.AtLeast)
            {
                h.SetAttributeValue(Ns.W + "hRule", rule == RowHeightRule.Exactly ? "exact" : "auto");
            }

            c.Add(h);
        }

        OnOff(c, "tblHeader", f.IsHeader, onOffOnly: true);
        if (f.CellSpacing is double cellSpacing)
        {
            c.Add(Width("tblCellSpacing", TableWidth.Points(cellSpacing)));
        }

        if (f.Alignment is { } alignment)
        {
            c.Add(Val("jc", TableAlignmentName(alignment)));
        }

        OnOff(c, "hidden", f.Hidden, onOffOnly: true);
        return Merge("trPr", c, kept, RowOrder);
    }

    public static XElement? Cell(CellFormat f, IEnumerable<XElement>? kept)
    {
        List<XElement> c = [];
        if (f.Width is { } width)
        {
            c.Add(Width("tcW", width));
        }

        if (f.GridSpan is int span)
        {
            c.Add(Val("gridSpan", span.ToString(CultureInfo.InvariantCulture)));
        }

        if (f.VerticalMerge is { } merge)
        {
            c.Add(merge == VerticalMerge.Restart ? Val("vMerge", "restart") : new XElement(Ns.W + "vMerge"));
        }

        XElement borders = Sides("tcBorders", ("top", f.BorderTop), ("left", f.BorderLeft), ("bottom", f.BorderBottom), ("right", f.BorderRight),
            ("tl2br", f.BorderDiagonalDown), ("tr2bl", f.BorderDiagonalUp));
        if (borders.HasElements)
        {
            c.Add(borders);
        }

        if (f.Shading is { } shading)
        {
            c.Add(Shading(shading));
        }

        OnOff(c, "noWrap", f.NoWrap);
        XElement margins = Margins("tcMar", f.MarginTop, f.MarginLeft, f.MarginBottom, f.MarginRight);
        if (margins.HasElements)
        {
            c.Add(margins);
        }

        if (f.TextDirection is { } direction)
        {
            c.Add(Val("textDirection", DirectionName(direction)));
        }

        OnOff(c, "tcFitText", f.FitText);
        if (f.VerticalAlignment is { } vertical)
        {
            c.Add(Val("vAlign", vertical switch
            {
                VerticalAlignment.Center => "center",
                VerticalAlignment.Bottom => "bottom",
                _ => "top",
            }));
        }

        OnOff(c, "hideMark", f.HideMark);
        return Merge("tcPr", c, kept, CellOrder);
    }

    public static XElement Section(PageSetup s, IEnumerable<XElement>? kept, IEnumerable<XElement> references)
    {
        List<XElement> c = [.. references];
        if (s.SectionStart is { } start)
        {
            c.Add(Val("type", start switch
            {
                SectionStart.Continuous => "continuous",
                SectionStart.OddPage => "oddPage",
                SectionStart.EvenPage => "evenPage",
                SectionStart.NewColumn => "nextColumn",
                _ => "nextPage",
            }));
        }

        if (s.PageWidth is not null || s.PageHeight is not null || s.Orientation is not null)
        {
            XElement size = new(Ns.W + "pgSz");
            SetAttribute(size, "w", s.PageWidth is double w ? DocxUnits.Twips(w) : null);
            SetAttribute(size, "h", s.PageHeight is double h ? DocxUnits.Twips(h) : null);
            SetAttribute(size, "orient", s.Orientation switch
            {
                PageOrientation.Landscape => "landscape",
                PageOrientation.Portrait => "portrait",
                _ => null,
            });
            c.Add(size);
        }

        XElement margins = new(Ns.W + "pgMar");
        SetAttribute(margins, "top", s.MarginTop is double top ? DocxUnits.Twips(top) : null);
        SetAttribute(margins, "right", s.MarginRight is double right ? DocxUnits.Twips(right) : null);
        SetAttribute(margins, "bottom", s.MarginBottom is double bottom ? DocxUnits.Twips(bottom) : null);
        SetAttribute(margins, "left", s.MarginLeft is double left ? DocxUnits.Twips(left) : null);
        SetAttribute(margins, "header", s.HeaderDistance is double header ? DocxUnits.Twips(header) : null);
        SetAttribute(margins, "footer", s.FooterDistance is double footer ? DocxUnits.Twips(footer) : null);
        SetAttribute(margins, "gutter", s.Gutter is double gutter ? DocxUnits.Twips(gutter) : null);
        if (margins.HasAttributes)
        {
            c.Add(margins);
        }

        if (s.PageNumberStart is not null || s.PageNumberStyle is not null)
        {
            XElement numbers = new(Ns.W + "pgNumType");
            SetAttribute(numbers, "fmt", s.PageNumberStyle is { } style ? NumberFormatName(style) : null);
            SetAttribute(numbers, "start", s.PageNumberStart?.ToString(CultureInfo.InvariantCulture));
            c.Add(numbers);
        }

        if (s.ColumnCount is not null || s.ColumnSpacing is not null || s.EqualColumnWidths is not null || s.Columns is not null || s.ColumnSeparator is not null)
        {
            XElement cols = new(Ns.W + "cols");
            SetAttribute(cols, "equalWidth", s.EqualColumnWidths is bool equal ? (equal ? "1" : "0") : null);
            SetAttribute(cols, "space", s.ColumnSpacing is double spacing ? DocxUnits.Twips(spacing) : null);
            SetAttribute(cols, "num", s.ColumnCount?.ToString(CultureInfo.InvariantCulture));
            SetAttribute(cols, "sep", s.ColumnSeparator is bool separator ? (separator ? "1" : "0") : null);
            foreach (TextColumn column in s.Columns ?? [])
            {
                cols.Add(new XElement(Ns.W + "col", new XAttribute(Ns.W + "w", DocxUnits.Twips(column.Width)), new XAttribute(Ns.W + "space", DocxUnits.Twips(column.SpaceAfter))));
            }

            c.Add(cols);
        }

        if (s.VerticalAlignment is { } vertical)
        {
            c.Add(Val("vAlign", vertical switch
            {
                VerticalAlignment.Center => "center",
                VerticalAlignment.Bottom => "bottom",
                VerticalAlignment.Justify => "both",
                _ => "top",
            }));
        }

        OnOff(c, "titlePg", s.DifferentFirstPage);
        if (s.TextDirection is { } direction)
        {
            c.Add(Val("textDirection", DirectionName(direction)));
        }

        OnOff(c, "bidi", s.Bidi);
        return Merge("sectPr", c, kept, SectionOrder) ?? new XElement(Ns.W + "sectPr");
    }

    /// <summary>
    /// The property element <paramref name="name"/> with <paramref name="generated"/> children and the <paramref name="kept"/>
    /// ones the reader did not map, in schema order: a kept element whose name a generated one has adds its attributes to it.
    /// </summary>
    public static XElement? Merge(string name, List<XElement> generated, IEnumerable<XElement>? kept, string[] order)
    {
        if (kept is not null)
        {
            foreach (XElement extra in kept)
            {
                if (generated.Find(g => g.Name == extra.Name) is { } same)
                {
                    foreach (XAttribute attribute in extra.Attributes())
                    {
                        if (same.Attribute(attribute.Name) is null)
                        {
                            same.Add(new XAttribute(attribute));
                        }
                    }

                    // Sides of a border or margin set the model does not name (a cell's insideH) join the generated ones.
                    foreach (XElement child in extra.Elements())
                    {
                        if (same.Element(child.Name) is null)
                        {
                            same.Add(new XElement(child));
                        }
                    }

                    if (extra.HasElements)
                    {
                        List<XElement> sides = [.. same.Elements().OrderBy(e => Array.IndexOf(SideOrder, e.Name.LocalName) is int at && at >= 0 ? at : SideOrder.Length)];
                        same.ReplaceNodes(sides);
                    }
                }
                else
                {
                    generated.Add(new XElement(extra));
                }
            }
        }

        if (generated.Count == 0)
        {
            return null;
        }

        // A stable sort: schema order for the w: elements, then Word 2010's w14 run properties in their schema order, other
        // namespaces after them in the order they came.
        List<XElement> sorted = [.. generated.Select((e, i) => (e, i)).OrderBy(x => Rank(x.e, order)).ThenBy(x => x.i).Select(x => x.e)];
        return new XElement(Ns.W + name, sorted);
    }

    private static int Rank(XElement element, string[] order)
    {
        if (element.Name.Namespace == Ns.W14 && Array.IndexOf(W14RunOrder, element.Name.LocalName) is int w14 && w14 >= 0)
        {
            return order.Length + w14;
        }

        if (element.Name.Namespace != Ns.W)
        {
            return order.Length + W14RunOrder.Length;
        }

        int index = Array.IndexOf(order, element.Name.LocalName);
        return index < 0 ? order.Length + W14RunOrder.Length : index;
    }

    public static XElement Val(string name, string value)
    {
        return new XElement(Ns.W + name, new XAttribute(Ns.W + "val", value));
    }

    private static void OnOff(List<XElement> children, string name, bool? value, bool onOffOnly = false)
    {
        if (value is bool on)
        {
            children.Add(on ? new XElement(Ns.W + name) : Val(name, onOffOnly ? "off" : "0"));
        }
    }

    private static void SetAttribute(XElement element, string name, string? value)
    {
        if (value is not null)
        {
            element.SetAttributeValue(Ns.W + name, value);
        }
    }

    public static XElement Color(XElement element, DocumentColor color, string valName, string themeName, string tintName, string shadeName)
    {
        switch (color.Kind)
        {
            case DocumentColorKind.Rgb:
                element.SetAttributeValue(Ns.W + valName, Hex(color.Rgb));
                break;
            case DocumentColorKind.Theme:
                element.SetAttributeValue(Ns.W + valName, color.Rgb == default ? "auto" : Hex(color.Rgb));
                element.SetAttributeValue(Ns.W + themeName, ThemeColorName(color.Theme));
                if (color.Tint > 0)
                {
                    element.SetAttributeValue(Ns.W + tintName, ((int)Math.Round((1 - color.Tint) * 255)).ToString("X2", CultureInfo.InvariantCulture));
                }

                if (color.Shade > 0)
                {
                    element.SetAttributeValue(Ns.W + shadeName, ((int)Math.Round((1 - color.Shade) * 255)).ToString("X2", CultureInfo.InvariantCulture));
                }

                break;
            default:
                element.SetAttributeValue(Ns.W + valName, "auto");
                break;
        }

        return element;
    }

    public static string Hex(Rgba8 rgb)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}");
    }

    public static string ThemeColorName(ThemeColor color)
    {
        return color switch
        {
            ThemeColor.Dark1 => "text1",
            ThemeColor.Light1 => "background1",
            ThemeColor.Dark2 => "text2",
            ThemeColor.Light2 => "background2",
            ThemeColor.Accent1 => "accent1",
            ThemeColor.Accent2 => "accent2",
            ThemeColor.Accent3 => "accent3",
            ThemeColor.Accent4 => "accent4",
            ThemeColor.Accent5 => "accent5",
            ThemeColor.Accent6 => "accent6",
            ThemeColor.Hyperlink => "hyperlink",
            _ => "followedHyperlink",
        };
    }

    private static string? ThemeFontName(ThemeFont? font, bool ascii)
    {
        string latin = ascii ? "Ascii" : "HAnsi";
        return font switch
        {
            ThemeFont.MajorLatin => "major" + latin,
            ThemeFont.MinorLatin => "minor" + latin,
            ThemeFont.MajorEastAsia => "majorEastAsia",
            ThemeFont.MinorEastAsia => "minorEastAsia",
            ThemeFont.MajorComplexScript => "majorBidi",
            ThemeFont.MinorComplexScript => "minorBidi",
            _ => null,
        };
    }

    public static XElement Border(string name, Border border)
    {
        XElement e = Val(name, border.Style switch
        {
            BorderStyle.None => "nil",
            BorderStyle.Single => "single",
            BorderStyle.Thick => "thick",
            BorderStyle.Double => "double",
            BorderStyle.Dotted => "dotted",
            BorderStyle.Dashed => "dashed",
            BorderStyle.DotDash => "dotDash",
            BorderStyle.DotDotDash => "dotDotDash",
            BorderStyle.Triple => "triple",
            BorderStyle.ThinThickSmallGap => "thinThickSmallGap",
            BorderStyle.ThickThinSmallGap => "thickThinSmallGap",
            BorderStyle.ThinThickThinSmallGap => "thinThickThinSmallGap",
            BorderStyle.ThinThickMediumGap => "thinThickMediumGap",
            BorderStyle.ThickThinMediumGap => "thickThinMediumGap",
            BorderStyle.ThinThickThinMediumGap => "thinThickThinMediumGap",
            BorderStyle.ThinThickLargeGap => "thinThickLargeGap",
            BorderStyle.ThickThinLargeGap => "thickThinLargeGap",
            BorderStyle.ThinThickThinLargeGap => "thinThickThinLargeGap",
            BorderStyle.Wave => "wave",
            BorderStyle.DoubleWave => "doubleWave",
            BorderStyle.DashSmallGap => "dashSmallGap",
            BorderStyle.DashDotStroked => "dashDotStroked",
            BorderStyle.ThreeDEmboss => "threeDEmboss",
            BorderStyle.ThreeDEngrave => "threeDEngrave",
            BorderStyle.Outset => "outset",
            BorderStyle.Inset => "inset",
            _ => "single",
        });
        // A border of no style keeps what the file gave it, so it reads back the same.
        if (border.Style != BorderStyle.None || border.Width != 0)
        {
            e.SetAttributeValue(Ns.W + "sz", Math.Round(border.Width * 8).ToString(CultureInfo.InvariantCulture));
        }

        e.SetAttributeValue(Ns.W + "space", Math.Round(border.Space).ToString(CultureInfo.InvariantCulture));
        Color(e, border.Color, "color", "themeColor", "themeTint", "themeShade");
        if (border.Shadow)
        {
            e.SetAttributeValue(Ns.W + "shadow", "1");
        }

        if (border.Frame)
        {
            e.SetAttributeValue(Ns.W + "frame", "1");
        }

        return e;
    }

    public static XElement Shading(Shading shading)
    {
        XElement e = Val("shd", shading.Pattern switch
        {
            ShadingPattern.Solid => "solid",
            ShadingPattern.Nil => "nil",
            ShadingPattern.HorizontalStripe => "horzStripe",
            ShadingPattern.VerticalStripe => "vertStripe",
            ShadingPattern.ReverseDiagonalStripe => "reverseDiagStripe",
            ShadingPattern.DiagonalStripe => "diagStripe",
            ShadingPattern.HorizontalCross => "horzCross",
            ShadingPattern.DiagonalCross => "diagCross",
            ShadingPattern.ThinHorizontalStripe => "thinHorzStripe",
            ShadingPattern.ThinVerticalStripe => "thinVertStripe",
            ShadingPattern.ThinReverseDiagonalStripe => "thinReverseDiagStripe",
            ShadingPattern.ThinDiagonalStripe => "thinDiagStripe",
            ShadingPattern.ThinHorizontalCross => "thinHorzCross",
            ShadingPattern.ThinDiagonalCross => "thinDiagCross",
            ShadingPattern.Percent => "pct" + Math.Round(shading.Percent).ToString(CultureInfo.InvariantCulture),
            _ => "clear",
        });
        Color(e, shading.PatternColor, "color", "themeColor", "themeTint", "themeShade");
        Color(e, shading.Fill, "fill", "themeFill", "themeFillTint", "themeFillShade");
        return e;
    }

    public static XElement Width(string name, TableWidth width)
    {
        (string w, string type) = width.Unit switch
        {
            WidthUnit.Auto => ("0", "auto"),
            WidthUnit.Percent => (Math.Round(width.Value * 50).ToString(CultureInfo.InvariantCulture), "pct"),
            _ => (DocxUnits.Twips(width.Value), "dxa"),
        };
        return new XElement(Ns.W + name, new XAttribute(Ns.W + "w", w), new XAttribute(Ns.W + "type", type));
    }

    private static XElement Sides(string name, params (string Side, Border? Border)[] sides)
    {
        XElement e = new(Ns.W + name);
        foreach ((string side, Border? border) in sides)
        {
            if (border is not null)
            {
                e.Add(Border(side, border));
            }
        }

        return e;
    }

    private static XElement Margins(string name, double? top, double? left, double? bottom, double? right)
    {
        XElement e = new(Ns.W + name);
        foreach ((string side, double? value) in (ReadOnlySpan<(string, double?)>)[("top", top), ("left", left), ("bottom", bottom), ("right", right)])
        {
            if (value is double points)
            {
                e.Add(Width(side, TableWidth.Points(points)));
            }
        }

        return e;
    }

    private static XElement Tab(TabStop tab)
    {
        XElement e = Val("tab", tab.Alignment switch
        {
            TabAlignment.Center => "center",
            TabAlignment.Right => "right",
            TabAlignment.Decimal => "decimal",
            TabAlignment.Bar => "bar",
            TabAlignment.Clear => "clear",
            _ => "left",
        });
        if (tab.Leader != TabLeader.None)
        {
            e.SetAttributeValue(Ns.W + "leader", tab.Leader switch
            {
                TabLeader.Dot => "dot",
                TabLeader.Hyphen => "hyphen",
                TabLeader.Underscore => "underscore",
                TabLeader.Heavy => "heavy",
                _ => "middleDot",
            });
        }

        e.SetAttributeValue(Ns.W + "pos", DocxUnits.Twips(tab.Position));
        return e;
    }

    private static string TableAlignmentName(TableAlignment alignment)
    {
        return alignment switch
        {
            TableAlignment.Center => "center",
            TableAlignment.Right => "right",
            _ => "left",
        };
    }

    private static string DirectionName(TextDirection direction)
    {
        return direction switch
        {
            TextDirection.TopToBottom => "tbRl",
            TextDirection.BottomToTop => "btLr",
            _ => "lrTb",
        };
    }

    public static string NumberFormatName(NumberStyle style)
    {
        return style switch
        {
            NumberStyle.UpperRoman => "upperRoman",
            NumberStyle.LowerRoman => "lowerRoman",
            NumberStyle.UpperLetter => "upperLetter",
            NumberStyle.LowerLetter => "lowerLetter",
            NumberStyle.Ordinal => "ordinal",
            NumberStyle.CardinalText => "cardinalText",
            NumberStyle.OrdinalText => "ordinalText",
            NumberStyle.DecimalZero => "decimalZero",
            NumberStyle.Bullet => "bullet",
            NumberStyle.None => "none",
            _ => "decimal",
        };
    }

    private static string HighlightName(HighlightColor color)
    {
        string name = color.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string UnderlineName(UnderlineStyle style)
    {
        string name = style.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
