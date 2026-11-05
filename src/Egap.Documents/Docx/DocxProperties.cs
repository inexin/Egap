using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents.Word;

namespace Egap.Documents.Docx;

/// <summary>
/// Reads DOCX's property elements (rPr, pPr, tblPr, trPr, tcPr, sectPr) into the model's formats. Children the model does
/// not map go to the <c>unknown</c> callbacks, so the reader keeps them as extension data.
/// </summary>
internal static class DocxProperties
{
    public static void ReadRun(XElement? rPr, RunFormat format, Action<XElement>? unknown = null)
    {
        if (rPr is null)
        {
            return;
        }

        foreach (XElement e in rPr.Elements())
        {
            if (DocxFeatures.IsFeature(e))
            {
                DocxFeatures.Read(e, format);
                continue;
            }

            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "b": Toggle(format, WordRunProperty.Bold, e); break;
                case "bCs": Toggle(format, WordRunProperty.BoldComplexScript, e); break;
                case "i": Toggle(format, WordRunProperty.Italic, e); break;
                case "iCs": Toggle(format, WordRunProperty.ItalicComplexScript, e); break;
                case "caps": Toggle(format, WordRunProperty.AllCaps, e); break;
                case "smallCaps": Toggle(format, WordRunProperty.SmallCaps, e); break;
                case "strike": Toggle(format, WordRunProperty.Strikethrough, e); break;
                case "dstrike": Toggle(format, WordRunProperty.DoubleStrikethrough, e); break;
                case "outline": Toggle(format, WordRunProperty.Outline, e); break;
                case "shadow": Toggle(format, WordRunProperty.Shadow, e); break;
                case "emboss": Toggle(format, WordRunProperty.Emboss, e); break;
                case "imprint": Toggle(format, WordRunProperty.Imprint, e); break;
                case "noProof": Toggle(format, WordRunProperty.NoProof, e); break;
                case "vanish": Toggle(format, WordRunProperty.Hidden, e); break;
                case "specVanish": Toggle(format, WordRunProperty.SpecialHidden, e); break;
                case "rtl": Toggle(format, WordRunProperty.RightToLeft, e); break;
                case "cs": Toggle(format, WordRunProperty.ComplexScript, e); break;
                case "color": WordProperties.Set(format, WordRunColor.Color, Color(e, "val", "themeColor", "themeTint", "themeShade")); break;
                case "spacing": Measure(format, WordRunProperty.Spacing, DocxUnits.Raw(e.Val())); break;
                case "w": Measure(format, WordRunProperty.Scale, Percent(e.Val())); break;
                case "kern": Measure(format, WordRunProperty.Kerning, DocxUnits.Raw(e.Val(), DocxUnits.HalfPoint)); break;
                case "position": Measure(format, WordRunProperty.Position, DocxUnits.Raw(e.Val(), DocxUnits.HalfPoint)); break;
                case "sz": Measure(format, WordRunProperty.Size, DocxUnits.Raw(e.Val(), DocxUnits.HalfPoint)); break;
                case "szCs": Measure(format, WordRunProperty.SizeComplexScript, DocxUnits.Raw(e.Val(), DocxUnits.HalfPoint)); break;
                case "highlight": Measure(format, WordRunProperty.Highlight, (int?)Highlight(e.Val())); break;
                case "u":
                    WordProperties.Set(format, WordRunProperty.Underline, (int)(Underline(e.Val()) ?? UnderlineStyle.Single));
                    if (e.Attribute(Ns.W + "color") is not null || e.Attribute(Ns.W + "themeColor") is not null)
                    {
                        WordProperties.Set(format, WordRunColor.UnderlineColor, Color(e, "color", "themeColor", "themeTint", "themeShade"));
                    }

                    break;
                case "bdr": format.Border = Border(e); break;
                case "shd": format.Shading = Shading(e); break;
                case "vertAlign":
                    WordProperties.Set(format, WordRunProperty.VerticalPosition, (int)(e.Val() switch
                    {
                        "superscript" => VerticalTextPosition.Superscript,
                        "subscript" => VerticalTextPosition.Subscript,
                        _ => VerticalTextPosition.Baseline,
                    }));
                    break;
                case "lang":
                    WordProperties.SetLanguage(format, WordRunProperty.Language, e.Val());
                    WordProperties.SetLanguage(format, WordRunProperty.LanguageEastAsia, e.W("eastAsia"));
                    WordProperties.SetLanguage(format, WordRunProperty.LanguageBidi, e.W("bidi"));
                    break;
                case "rFonts":
                    ReadFonts(e, format);
                    if (e.Attribute(Ns.W + "hint") is not null || e.Attributes().Any(a => a.Name.Namespace != Ns.W && !a.IsNamespaceDeclaration))
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name == Ns.W + "hint" || (a.Name.Namespace != Ns.W && !a.IsNamespaceDeclaration))));
                    }

                    break;
                case "rStyle":
                    break;
                default:
                    unknown?.Invoke(e);
                    break;
            }
        }

        static void Toggle(RunFormat format, WordRunProperty property, XElement e) => WordProperties.Set(format, property, DocxUnits.OnOff(e) ? 1 : 0);

        static void Measure(RunFormat format, WordRunProperty property, double? value)
        {
            if (value is double v)
            {
                WordProperties.Set(format, property, v);
            }
            else
            {
                WordProperties.Clear(format, property);
            }
        }
    }

    public static void ReadParagraph(XElement? pPr, ParagraphFormat format, Action<XElement>? unknown = null)
    {
        if (pPr is null)
        {
            return;
        }

        foreach (XElement e in pPr.Elements())
        {
            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "keepNext": Toggle(format, WordParagraphProperty.KeepWithNext, e); break;
                case "framePr": format.Frame = DocxPositions.ReadFrame(e); break;
                case "keepLines": Toggle(format, WordParagraphProperty.KeepTogether, e); break;
                case "pageBreakBefore": Toggle(format, WordParagraphProperty.PageBreakBefore, e); break;
                case "widowControl": Toggle(format, WordParagraphProperty.WidowControl, e); break;
                case "suppressLineNumbers": Toggle(format, WordParagraphProperty.SuppressLineNumbers, e); break;
                case "suppressAutoHyphens": Toggle(format, WordParagraphProperty.SuppressAutoHyphens, e); break;
                case "bidi": Toggle(format, WordParagraphProperty.Bidi, e); break;
                case "snapToGrid": Toggle(format, WordParagraphProperty.SnapToGrid, e); break;
                case "contextualSpacing": Toggle(format, WordParagraphProperty.ContextualSpacing, e); break;
                case "mirrorIndents": Toggle(format, WordParagraphProperty.MirrorIndents, e); break;
                case "outlineLvl":
                    if (DocxUnits.Int(e.Val()) is int level && level is >= 0 and <= 9)
                    {
                        WordProperties.Set(format, WordParagraphProperty.OutlineLevel, level);
                    }

                    break;
                case "jc":
                    Measure(format, WordParagraphProperty.Alignment, (int?)(e.Val() switch
                    {
                        "left" or "start" => ParagraphAlignment.Left,
                        "center" => ParagraphAlignment.Center,
                        "right" or "end" => ParagraphAlignment.Right,
                        "both" or "lowKashida" or "mediumKashida" or "highKashida" or "thaiDistribute" => ParagraphAlignment.Justify,
                        "distribute" => ParagraphAlignment.Distribute,
                        _ => (ParagraphAlignment?)null,
                    }));

                    // Kashida is a kind of justification: a plain jc of both turns off a style's kashida.
                    Measure(format, WordParagraphProperty.Kashida, (int?)(e.Val() switch
                    {
                        "lowKashida" => KashidaLevel.Low,
                        "mediumKashida" => KashidaLevel.Medium,
                        "highKashida" => KashidaLevel.High,
                        "both" or "thaiDistribute" => KashidaLevel.None,
                        _ => (KashidaLevel?)null,
                    }));
                    break;
                case "spacing":
                    Measure(format, WordParagraphProperty.SpaceBefore, DocxUnits.Raw(e.W("before")));
                    Measure(format, WordParagraphProperty.SpaceAfter, DocxUnits.Raw(e.W("after")));
                    if (e.Attribute(Ns.W + "beforeAutospacing") is { } beforeAuto)
                    {
                        WordProperties.Set(format, WordParagraphProperty.SpaceBeforeAuto, beforeAuto.Value is "1" or "true" or "on" ? 1 : 0);
                    }

                    if (e.Attribute(Ns.W + "afterAutospacing") is { } afterAuto)
                    {
                        WordProperties.Set(format, WordParagraphProperty.SpaceAfterAuto, afterAuto.Value is "1" or "true" or "on" ? 1 : 0);
                    }

                    if (DocxUnits.Raw(e.W("line")) is double line)
                    {
                        WordProperties.SetLineSpacing(format, e.W("lineRule") switch
                        {
                            "exact" => LineSpacingRule.Exactly,
                            "atLeast" => LineSpacingRule.AtLeast,
                            _ => LineSpacingRule.Multiple,
                        }, line);
                    }

                    if (e.Attribute(Ns.W + "beforeLines") is not null || e.Attribute(Ns.W + "afterLines") is not null)
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name.LocalName is "beforeLines" or "afterLines")));
                    }

                    break;
                case "ind":
                    Measure(format, WordParagraphProperty.LeftIndent, DocxUnits.Raw(e.W("start") ?? e.W("left")));
                    Measure(format, WordParagraphProperty.RightIndent, DocxUnits.Raw(e.W("end") ?? e.W("right")));
                    if (DocxUnits.Raw(e.W("hanging")) is double hanging)
                    {
                        WordProperties.Set(format, WordParagraphProperty.FirstLineIndent, hanging == 0 ? 0 : -hanging);
                    }
                    else if (DocxUnits.Raw(e.W("firstLine")) is double first)
                    {
                        WordProperties.Set(format, WordParagraphProperty.FirstLineIndent, first);
                    }

                    if (e.Attributes().Any(a => a.Name.LocalName.EndsWith("Chars", StringComparison.Ordinal)))
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name.LocalName.EndsWith("Chars", StringComparison.Ordinal))));
                    }

                    break;
                case "tabs":
                    WordProperties.SetTabStops(format, e.Elements(Ns.W + "tab").Select(Tab).OfType<TabStop>());
                    break;
                case "pBdr":
                    foreach (XElement side in e.Elements())
                    {
                        Border? border = Border(side);
                        switch (side.Name.LocalName)
                        {
                            case "top": format.BorderTop = border; break;
                            case "bottom": format.BorderBottom = border; break;
                            case "left" or "start": format.BorderLeft = border; break;
                            case "right" or "end": format.BorderRight = border; break;
                            case "between": format.BorderBetween = border; break;
                            case "bar": format.BorderBar = border; break;
                        }
                    }

                    break;
                case "shd": format.Shading = Shading(e); break;
                case "pStyle" or "numPr" or "rPr" or "sectPr" or "pPrChange":
                    break;
                default:
                    unknown?.Invoke(e);
                    break;
            }
        }

        static void Toggle(ParagraphFormat format, WordParagraphProperty property, XElement e) => WordProperties.Set(format, property, DocxUnits.OnOff(e) ? 1 : 0);

        static void Measure(ParagraphFormat format, WordParagraphProperty property, double? value)
        {
            if (value is double v)
            {
                WordProperties.Set(format, property, v);
            }
            else
            {
                WordProperties.Clear(format, property);
            }
        }
    }

    public static void ReadTable(XElement? tblPr, TableFormat format, Action<XElement>? unknown = null)
    {
        if (tblPr is null)
        {
            return;
        }

        foreach (XElement e in tblPr.Elements())
        {
            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "tblW": format.Width = Width(e); break;
                case "jc": Measure(format, WordTableProperty.TableAlignment, (int?)TableAlignmentOf(e.Val())); break;
                case "tblInd": Measure(format, WordTableProperty.Indent, WidthTwips(e)); break;
                case "tblLayout":
                    WordProperties.Set(format, WordTableProperty.Layout, (int)(e.W("type") == "fixed" ? TableLayoutKind.Fixed : TableLayoutKind.Autofit));
                    break;
                case "tblCellSpacing": Measure(format, WordTableProperty.TableCellSpacing, WidthTwips(e)); break;
                case "bidiVisual": WordProperties.Set(format, WordTableProperty.Bidi, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "tblStyleRowBandSize": WordProperties.Set(format, WordTableProperty.RowBandSize, DocxUnits.Int(e.Val()) ?? 0); break;
                case "tblStyleColBandSize": WordProperties.Set(format, WordTableProperty.ColumnBandSize, DocxUnits.Int(e.Val()) ?? 0); break;
                case "shd": format.Shading = Shading(e); break;
                case "tblCellMar":
                    foreach (XElement side in e.Elements())
                    {
                        WordTableProperty? margin = side.Name.LocalName switch
                        {
                            "top" => WordTableProperty.DefaultMarginTop,
                            "bottom" => WordTableProperty.DefaultMarginBottom,
                            "left" or "start" => WordTableProperty.DefaultMarginLeft,
                            "right" or "end" => WordTableProperty.DefaultMarginRight,
                            _ => null,
                        };
                        if (margin is WordTableProperty property)
                        {
                            Measure(format, property, WidthTwips(side));
                        }
                    }

                    break;
                case "tblBorders":
                    foreach (XElement side in e.Elements())
                    {
                        Border? border = Border(side);
                        switch (side.Name.LocalName)
                        {
                            case "top": format.BorderTop = border; break;
                            case "bottom": format.BorderBottom = border; break;
                            case "left" or "start": format.BorderLeft = border; break;
                            case "right" or "end": format.BorderRight = border; break;
                            case "insideH": format.BorderInsideHorizontal = border; break;
                            case "insideV": format.BorderInsideVertical = border; break;
                        }
                    }

                    break;
                case "tblLook":
                    ReadLook(e, format);
                    break;
                case "tblStyle":
                    break;
                case "tblpPr":
                    format.Position = DocxPositions.ReadTable(e, tblPr.Element(Ns.W + "tblOverlap"));
                    break;
                case "tblOverlap" when tblPr.Element(Ns.W + "tblpPr") is not null:
                    break;
                default:
                    unknown?.Invoke(e);
                    break;
            }
        }

        static void Measure(TableFormat format, WordTableProperty property, double? value)
        {
            if (value is double v)
            {
                WordProperties.Set(format, property, v);
            }
            else
            {
                WordProperties.Clear(format, property);
            }
        }
    }

    public static void ReadRow(XElement? trPr, RowFormat format, Action<XElement>? unknown = null)
    {
        if (trPr is null)
        {
            return;
        }

        foreach (XElement e in trPr.Elements())
        {
            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "trHeight":
                    WordTableProperty rule = e.W("hRule") switch
                    {
                        "exact" => WordTableProperty.RowHeightExactly,
                        "auto" => WordTableProperty.RowHeightAuto,
                        _ => WordTableProperty.RowHeightAtLeast,
                    };
                    WordProperties.Set(format, rule, DocxUnits.Raw(e.Val()) ?? 0);
                    if (DocxUnits.Raw(e.Val()) is null)
                    {
                        WordProperties.Clear(format, rule);
                    }

                    break;
                case "tblHeader": WordProperties.Set(format, WordTableProperty.HeaderRow, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "cantSplit": WordProperties.Set(format, WordTableProperty.CantSplit, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "hidden": WordProperties.Set(format, WordTableProperty.RowHidden, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "jc": Measure(format, WordTableProperty.RowAlignment, (int?)TableAlignmentOf(e.Val())); break;
                case "tblCellSpacing": Measure(format, WordTableProperty.RowCellSpacing, WidthTwips(e)); break;
                case "ins" or "del":
                    break;
                default:
                    unknown?.Invoke(e);
                    break;
            }
        }

        static void Measure(RowFormat format, WordTableProperty property, double? value)
        {
            if (value is double v)
            {
                WordProperties.Set(format, property, v);
            }
            else
            {
                WordProperties.Clear(format, property);
            }
        }
    }

    public static void ReadCell(XElement? tcPr, CellFormat format, Action<XElement>? unknown = null)
    {
        if (tcPr is null)
        {
            return;
        }

        foreach (XElement e in tcPr.Elements())
        {
            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "tcW": format.Width = Width(e); break;
                case "gridSpan": WordProperties.Set(format, WordTableProperty.CellGridSpan, DocxUnits.Int(e.Val()) ?? 0); break;
                case "vMerge":
                    WordProperties.Set(format, WordTableProperty.CellVerticalMerge, (int)(e.Val() == "restart" ? VerticalMerge.Restart : VerticalMerge.Continue));
                    break;
                case "vAlign":
                    Measure(format, WordTableProperty.CellVerticalAlignment, (int?)(e.Val() switch
                    {
                        "center" => VerticalAlignment.Center,
                        "bottom" => VerticalAlignment.Bottom,
                        "top" => VerticalAlignment.Top,
                        _ => (VerticalAlignment?)null,
                    }));
                    break;
                case "noWrap": WordProperties.Set(format, WordTableProperty.CellNoWrap, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "tcFitText": WordProperties.Set(format, WordTableProperty.CellFitText, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "hideMark": WordProperties.Set(format, WordTableProperty.CellHideMark, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "shd": format.Shading = Shading(e); break;
                case "textDirection":
                    Measure(format, WordTableProperty.CellTextDirection, (int?)(e.Val() switch
                    {
                        "tbRl" or "tbRlV" => TextDirection.TopToBottom,
                        "btLr" => TextDirection.BottomToTop,
                        "lrTb" => TextDirection.Horizontal,
                        _ => (TextDirection?)null,
                    }));
                    break;
                case "tcMar":
                    foreach (XElement side in e.Elements())
                    {
                        WordTableProperty? margin = side.Name.LocalName switch
                        {
                            "top" => WordTableProperty.CellMarginTop,
                            "bottom" => WordTableProperty.CellMarginBottom,
                            "left" or "start" => WordTableProperty.CellMarginLeft,
                            "right" or "end" => WordTableProperty.CellMarginRight,
                            _ => null,
                        };
                        if (margin is WordTableProperty property)
                        {
                            Measure(format, property, WidthTwips(side));
                        }
                    }

                    break;
                case "tcBorders":
                    foreach (XElement side in e.Elements())
                    {
                        Border? border = Border(side);
                        switch (side.Name.LocalName)
                        {
                            case "top": format.BorderTop = border; break;
                            case "bottom": format.BorderBottom = border; break;
                            case "left" or "start": format.BorderLeft = border; break;
                            case "right" or "end": format.BorderRight = border; break;
                            case "tl2br": format.BorderDiagonalDown = border; break;
                            case "tr2bl": format.BorderDiagonalUp = border; break;
                            default: unknown?.Invoke(new XElement(e.Name, side)); break;
                        }
                    }

                    break;
                default:
                    unknown?.Invoke(e);
                    break;
            }
        }

        static void Measure(CellFormat format, WordTableProperty property, double? value)
        {
            if (value is double v)
            {
                WordProperties.Set(format, property, v);
            }
            else
            {
                WordProperties.Clear(format, property);
            }
        }
    }

    public static void ReadSection(XElement? sectPr, PageSetup setup, Action<XElement>? unknown = null)
    {
        if (sectPr is null)
        {
            return;
        }

        foreach (XElement e in sectPr.Elements())
        {
            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "pgSz":
                    Measure(setup, WordSectionProperty.PageWidth, DocxUnits.Raw(e.W("w")));
                    Measure(setup, WordSectionProperty.PageHeight, DocxUnits.Raw(e.W("h")));
                    if (e.W("orient") is { } orient)
                    {
                        WordProperties.Set(setup, WordSectionProperty.Orientation, (int)(orient == "landscape" ? PageOrientation.Landscape : PageOrientation.Portrait));
                    }

                    break;
                case "pgMar":
                    Measure(setup, WordSectionProperty.MarginTop, DocxUnits.Raw(e.W("top")));
                    Measure(setup, WordSectionProperty.MarginBottom, DocxUnits.Raw(e.W("bottom")));
                    Measure(setup, WordSectionProperty.MarginLeft, DocxUnits.Raw(e.W("left") ?? e.W("start")));
                    Measure(setup, WordSectionProperty.MarginRight, DocxUnits.Raw(e.W("right") ?? e.W("end")));
                    Measure(setup, WordSectionProperty.HeaderDistance, DocxUnits.Raw(e.W("header")));
                    Measure(setup, WordSectionProperty.FooterDistance, DocxUnits.Raw(e.W("footer")));
                    Measure(setup, WordSectionProperty.Gutter, DocxUnits.Raw(e.W("gutter")));
                    break;
                case "type":
                    WordProperties.Set(setup, WordSectionProperty.Start, (int)(e.Val() switch
                    {
                        "continuous" => SectionStart.Continuous,
                        "oddPage" => SectionStart.OddPage,
                        "evenPage" => SectionStart.EvenPage,
                        "nextColumn" => SectionStart.NewColumn,
                        _ => SectionStart.NewPage,
                    }));
                    break;
                case "titlePg": WordProperties.Set(setup, WordSectionProperty.TitlePage, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "bidi": WordProperties.Set(setup, WordSectionProperty.Bidi, DocxUnits.OnOff(e) ? 1 : 0); break;
                case "vAlign":
                    WordProperties.Set(setup, WordSectionProperty.VerticalAlignment, (int)(e.Val() switch
                    {
                        "center" => VerticalAlignment.Center,
                        "bottom" => VerticalAlignment.Bottom,
                        "both" => VerticalAlignment.Justify,
                        _ => VerticalAlignment.Top,
                    }));
                    break;
                case "textDirection":
                    WordProperties.Set(setup, WordSectionProperty.TextDirection, (int)(e.Val() switch
                    {
                        "tbRl" or "tbRlV" => TextDirection.TopToBottom,
                        "btLr" => TextDirection.BottomToTop,
                        _ => TextDirection.Horizontal,
                    }));
                    break;
                case "pgNumType":
                    Measure(setup, WordSectionProperty.PageNumberStart, DocxUnits.Int(e.W("start")));
                    Measure(setup, WordSectionProperty.PageNumberStyle, e.W("fmt") is { } fmt ? (int)NumberFormat(fmt) : null);
                    if (e.Attributes().Any(a => a.Name.LocalName is "chapStyle" or "chapSep"))
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name.LocalName is "chapStyle" or "chapSep")));
                    }

                    break;
                case "cols":
                    if (DocxUnits.Int(e.W("num")) is int count && count > 0)
                    {
                        WordProperties.Set(setup, WordSectionProperty.ColumnCount, count);
                    }

                    Measure(setup, WordSectionProperty.ColumnSpacing, DocxUnits.Raw(e.W("space")));
                    if (e.Attribute(Ns.W + "equalWidth") is { } equal)
                    {
                        WordProperties.Set(setup, WordSectionProperty.EqualColumnWidths, equal.Value is "1" or "true" or "on" ? 1 : 0);
                    }

                    if (e.Attribute(Ns.W + "sep") is { } separator)
                    {
                        WordProperties.Set(setup, WordSectionProperty.ColumnSeparator, separator.Value is "1" or "true" or "on" ? 1 : 0);
                    }

                    List<TextColumn> columns = [.. e.Elements(Ns.W + "col").Select(c => new TextColumn(Math.Max(0, DocxUnits.Points(c.W("w")) ?? 0), Math.Max(0, DocxUnits.Points(c.W("space")) ?? 0)))];
                    if (columns.Count > 0)
                    {
                        setup.Columns = columns;
                    }

                    break;
                case "headerReference" or "footerReference" or "sectPrChange":
                    break;
                default:
                    unknown?.Invoke(e);
                    break;
            }
        }

        static void Measure(PageSetup setup, WordSectionProperty property, double? value)
        {
            if (value is double v)
            {
                WordProperties.Set(setup, property, v);
            }
            else
            {
                WordProperties.Clear(setup, property);
            }
        }
    }

    public static NumberStyle NumberFormat(string? format)
    {
        return format switch
        {
            "decimal" => NumberStyle.Decimal,
            "upperRoman" => NumberStyle.UpperRoman,
            "lowerRoman" => NumberStyle.LowerRoman,
            "upperLetter" => NumberStyle.UpperLetter,
            "lowerLetter" => NumberStyle.LowerLetter,
            "ordinal" => NumberStyle.Ordinal,
            "cardinalText" => NumberStyle.CardinalText,
            "ordinalText" => NumberStyle.OrdinalText,
            "decimalZero" => NumberStyle.DecimalZero,
            "bullet" => NumberStyle.Bullet,
            "none" => NumberStyle.None,
            _ => NumberStyle.Decimal,
        };
    }

    public static DocumentColor? Color(XElement e, string valName, string themeName, string tintName, string shadeName)
    {
        string? themeColor = e.W(themeName);
        Rgba8 stored = ParseHex(e.W(valName)) ?? default;
        if (themeColor is not null && ThemeColorOf(themeColor) is { } theme)
        {
            double tint = e.W(tintName) is { } t && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tv) ? 1 - (Math.Clamp(tv, 0, 255) / 255.0) : 0;
            double shade = e.W(shadeName) is { } s && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int sv) ? 1 - (Math.Clamp(sv, 0, 255) / 255.0) : 0;
            return DocumentColor.FromTheme(theme, tint, shade, stored);
        }

        string? value = e.W(valName);
        if (value is null)
        {
            return null;
        }

        return value == "auto" ? DocumentColor.Auto : ParseHex(value) is { } rgb ? DocumentColor.FromRgb(rgb) : null;
    }

    public static ThemeColor? ThemeColorOf(string name)
    {
        return name switch
        {
            "dark1" or "text1" => ThemeColor.Dark1,
            "light1" or "background1" => ThemeColor.Light1,
            "dark2" or "text2" => ThemeColor.Dark2,
            "light2" or "background2" => ThemeColor.Light2,
            "accent1" => ThemeColor.Accent1,
            "accent2" => ThemeColor.Accent2,
            "accent3" => ThemeColor.Accent3,
            "accent4" => ThemeColor.Accent4,
            "accent5" => ThemeColor.Accent5,
            "accent6" => ThemeColor.Accent6,
            "hyperlink" => ThemeColor.Hyperlink,
            "followedHyperlink" => ThemeColor.FollowedHyperlink,
            _ => null,
        };
    }

    public static Rgba8? ParseHex(string? value)
    {
        return value is { Length: 6 } && Rgba8.TryFromHex(value, out Rgba8 color) ? color : null;
    }

    public static Border? Border(XElement e)
    {
        string? val = e.Val();
        if (val is null)
        {
            return null;
        }

        BorderStyle style = val switch
        {
            "nil" or "none" => BorderStyle.None,
            "single" => BorderStyle.Single,
            "thick" => BorderStyle.Thick,
            "double" => BorderStyle.Double,
            "dotted" => BorderStyle.Dotted,
            "dashed" => BorderStyle.Dashed,
            "dotDash" => BorderStyle.DotDash,
            "dotDotDash" => BorderStyle.DotDotDash,
            "triple" => BorderStyle.Triple,
            "thinThickSmallGap" => BorderStyle.ThinThickSmallGap,
            "thickThinSmallGap" => BorderStyle.ThickThinSmallGap,
            "thinThickThinSmallGap" => BorderStyle.ThinThickThinSmallGap,
            "thinThickMediumGap" => BorderStyle.ThinThickMediumGap,
            "thickThinMediumGap" => BorderStyle.ThickThinMediumGap,
            "thinThickThinMediumGap" => BorderStyle.ThinThickThinMediumGap,
            "thinThickLargeGap" => BorderStyle.ThinThickLargeGap,
            "thickThinLargeGap" => BorderStyle.ThickThinLargeGap,
            "thinThickThinLargeGap" => BorderStyle.ThinThickThinLargeGap,
            "wave" => BorderStyle.Wave,
            "doubleWave" => BorderStyle.DoubleWave,
            "dashSmallGap" => BorderStyle.DashSmallGap,
            "dashDotStroked" => BorderStyle.DashDotStroked,
            "threeDEmboss" => BorderStyle.ThreeDEmboss,
            "threeDEngrave" => BorderStyle.ThreeDEngrave,
            "outset" => BorderStyle.Outset,
            "inset" => BorderStyle.Inset,
            _ => BorderStyle.Single,
        };
        double width = style == BorderStyle.None ? 0 : Math.Clamp((DocxUnits.Int(e.W("sz")) ?? 4) / 8.0, 0.25, 12);
        return new Border(style, width, Color(e, "color", "themeColor", "themeTint", "themeShade") ?? DocumentColor.Auto, Math.Clamp(DocxUnits.Int(e.W("space")) ?? 0, 0, 31),
            e.W("shadow") is "1" or "true" or "on", e.W("frame") is "1" or "true" or "on");
    }

    public static Shading? Shading(XElement e)
    {
        string? val = e.Val();
        ShadingPattern pattern = val switch
        {
            null or "clear" => ShadingPattern.Clear,
            "solid" => ShadingPattern.Solid,
            "nil" => ShadingPattern.Nil,
            "horzStripe" => ShadingPattern.HorizontalStripe,
            "vertStripe" => ShadingPattern.VerticalStripe,
            "reverseDiagStripe" => ShadingPattern.ReverseDiagonalStripe,
            "diagStripe" => ShadingPattern.DiagonalStripe,
            "horzCross" => ShadingPattern.HorizontalCross,
            "diagCross" => ShadingPattern.DiagonalCross,
            "thinHorzStripe" => ShadingPattern.ThinHorizontalStripe,
            "thinVertStripe" => ShadingPattern.ThinVerticalStripe,
            "thinReverseDiagStripe" => ShadingPattern.ThinReverseDiagonalStripe,
            "thinDiagStripe" => ShadingPattern.ThinDiagonalStripe,
            "thinHorzCross" => ShadingPattern.ThinHorizontalCross,
            "thinDiagCross" => ShadingPattern.ThinDiagonalCross,
            _ when val.StartsWith("pct", StringComparison.Ordinal) => ShadingPattern.Percent,
            _ => ShadingPattern.Clear,
        };
        double percent = pattern == ShadingPattern.Percent && double.TryParse(val![3..], NumberStyles.Float, CultureInfo.InvariantCulture, out double p) ? Math.Clamp(p, 0, 100) : 0;
        return new Shading(pattern, Color(e, "fill", "themeFill", "themeFillTint", "themeFillShade") ?? DocumentColor.Auto, Color(e, "color", "themeColor", "themeTint", "themeShade") ?? DocumentColor.Auto, percent);
    }

    public static TableWidth? Width(XElement e)
    {
        string? type = e.W("type");
        string? w = e.W("w");
        return type switch
        {
            "auto" => TableWidth.Auto,
            "nil" => TableWidth.Points(0),
            "pct" when w is not null && w.EndsWith('%') && double.TryParse(w[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) => TableWidth.Percent(Math.Max(0, pct)),
            "pct" when DocxUnits.Int(w) is int fiftieths => TableWidth.Percent(Math.Max(0, fiftieths / 50.0)),
            _ when DocxUnits.Points(w) is double points => TableWidth.Points(Math.Max(0, points)),
            _ => null,
        };
    }

    private static TabStop? Tab(XElement e)
    {
        if (DocxUnits.Points(e.W("pos")) is not double position)
        {
            return null;
        }

        TabAlignment alignment = e.Val() switch
        {
            "center" => TabAlignment.Center,
            "right" or "end" => TabAlignment.Right,
            "decimal" => TabAlignment.Decimal,
            "bar" => TabAlignment.Bar,
            "clear" => TabAlignment.Clear,
            _ => TabAlignment.Left,
        };
        TabLeader leader = e.W("leader") switch
        {
            "dot" => TabLeader.Dot,
            "hyphen" => TabLeader.Hyphen,
            "underscore" => TabLeader.Underscore,
            "heavy" => TabLeader.Heavy,
            "middleDot" => TabLeader.MiddleDot,
            _ => TabLeader.None,
        };
        return new TabStop(position, alignment, leader);
    }

    private static void ReadFonts(XElement e, RunFormat format)
    {
        Font(format, WordFontSlot.Ascii, e.W("ascii"));
        Font(format, WordFontSlot.HighAnsi, e.W("hAnsi"));
        Font(format, WordFontSlot.EastAsia, e.W("eastAsia"));
        Font(format, WordFontSlot.ComplexScript, e.W("cs"));
        format.ThemeFontAscii = ThemeFontOf(e.W("asciiTheme")) ?? format.ThemeFontAscii;
        format.ThemeFontHighAnsi = ThemeFontOf(e.W("hAnsiTheme")) ?? format.ThemeFontHighAnsi;
        format.ThemeFontEastAsia = ThemeFontOf(e.W("eastAsiaTheme")) ?? format.ThemeFontEastAsia;
        format.ThemeFontComplexScript = ThemeFontOf(e.W("cstheme")) ?? format.ThemeFontComplexScript;

        static void Font(RunFormat format, WordFontSlot slot, string? name)
        {
            if (name is not null)
            {
                WordProperties.SetFont(format, slot, name);
            }
        }
    }

    public static ThemeFont? ThemeFontOf(string? name)
    {
        return name switch
        {
            "majorAscii" or "majorHAnsi" => ThemeFont.MajorLatin,
            "majorEastAsia" => ThemeFont.MajorEastAsia,
            "majorBidi" => ThemeFont.MajorComplexScript,
            "minorAscii" or "minorHAnsi" => ThemeFont.MinorLatin,
            "minorEastAsia" => ThemeFont.MinorEastAsia,
            "minorBidi" => ThemeFont.MinorComplexScript,
            _ => null,
        };
    }

    private static void ReadLook(XElement e, TableFormat format)
    {
        // Word 2010+ writes the look as attributes; older files as a hex bit mask in w:val.
        if (e.W("firstRow") is not null || e.W("lastRow") is not null || e.W("noHBand") is not null)
        {
            Flag(format, WordTableProperty.LookFirstRow, e.W("firstRow"));
            Flag(format, WordTableProperty.LookLastRow, e.W("lastRow"));
            Flag(format, WordTableProperty.LookFirstColumn, e.W("firstColumn"));
            Flag(format, WordTableProperty.LookLastColumn, e.W("lastColumn"));
            Flag(format, WordTableProperty.LookNoRowBands, e.W("noHBand"));
            Flag(format, WordTableProperty.LookNoColumnBands, e.W("noVBand"));
        }
        else if (e.Val() is { } hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mask))
        {
            // Word's TLP flags ([MS-DOC] 2.9.328), as DOC stores them too.
            WordProperties.Set(format, WordTableProperty.LookFirstRow, mask & 0x0020);
            WordProperties.Set(format, WordTableProperty.LookLastRow, mask & 0x0040);
            WordProperties.Set(format, WordTableProperty.LookFirstColumn, mask & 0x0080);
            WordProperties.Set(format, WordTableProperty.LookLastColumn, mask & 0x0100);
            WordProperties.Set(format, WordTableProperty.LookNoRowBands, mask & 0x0200);
            WordProperties.Set(format, WordTableProperty.LookNoColumnBands, mask & 0x0400);
        }

        static void Flag(TableFormat format, WordTableProperty property, string? value)
        {
            if (value is null)
            {
                WordProperties.Clear(format, property);
            }
            else
            {
                WordProperties.Set(format, property, value is "1" or "true" or "on" ? 1 : 0);
            }
        }
    }

    private static HighlightColor? Highlight(string? value)
    {
        return value switch
        {
            "none" => HighlightColor.None,
            "black" => HighlightColor.Black,
            "blue" => HighlightColor.Blue,
            "cyan" => HighlightColor.Cyan,
            "green" => HighlightColor.Green,
            "magenta" => HighlightColor.Magenta,
            "red" => HighlightColor.Red,
            "yellow" => HighlightColor.Yellow,
            "white" => HighlightColor.White,
            "darkBlue" => HighlightColor.DarkBlue,
            "darkCyan" => HighlightColor.DarkCyan,
            "darkGreen" => HighlightColor.DarkGreen,
            "darkMagenta" => HighlightColor.DarkMagenta,
            "darkRed" => HighlightColor.DarkRed,
            "darkYellow" => HighlightColor.DarkYellow,
            "darkGray" => HighlightColor.DarkGray,
            "lightGray" => HighlightColor.LightGray,
            _ => null,
        };
    }

    private static UnderlineStyle? Underline(string? value)
    {
        return value switch
        {
            "none" => UnderlineStyle.None,
            null or "single" => UnderlineStyle.Single,
            "words" => UnderlineStyle.Words,
            "double" => UnderlineStyle.Double,
            "thick" => UnderlineStyle.Thick,
            "dotted" => UnderlineStyle.Dotted,
            "dottedHeavy" => UnderlineStyle.DottedHeavy,
            "dash" => UnderlineStyle.Dash,
            "dashedHeavy" => UnderlineStyle.DashedHeavy,
            "dashLong" => UnderlineStyle.DashLong,
            "dashLongHeavy" => UnderlineStyle.DashLongHeavy,
            "dotDash" => UnderlineStyle.DotDash,
            "dashDotHeavy" => UnderlineStyle.DashDotHeavy,
            "dotDotDash" => UnderlineStyle.DotDotDash,
            "dashDotDotHeavy" => UnderlineStyle.DashDotDotHeavy,
            "wave" => UnderlineStyle.Wave,
            "wavyHeavy" => UnderlineStyle.WavyHeavy,
            "wavyDouble" => UnderlineStyle.WavyDouble,
            _ => UnderlineStyle.Single,
        };
    }

    private static double? Positive(double? value)
    {
        return value is double v ? Math.Max(0, v) : null;
    }

    private static double? Percent(string? value)
    {
        string? text = value?.TrimEnd('%');
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && double.IsFinite(percent) ? percent : null;
    }

    private static TableAlignment? TableAlignmentOf(string? value)
    {
        return value switch
        {
            "center" => TableAlignment.Center,
            "right" or "end" => TableAlignment.Right,
            "left" or "start" => TableAlignment.Left,
            _ => null,
        };
    }

    /// <summary>A width given in twips (or nil, for 0), never below 0; null for an automatic or percentage width.</summary>
    private static double? WidthTwips(XElement e)
    {
        return e.W("type") switch
        {
            "auto" or "pct" => null,
            "nil" => 0,
            _ => DocxUnits.Raw(e.W("w")) is double twips ? Math.Max(0, twips) : null,
        };
    }
}
