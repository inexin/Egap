using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

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
            if (e.Name.Namespace != Ns.W)
            {
                unknown?.Invoke(e);
                continue;
            }

            switch (e.Name.LocalName)
            {
                case "b": format.Bold = DocxUnits.OnOff(e); break;
                case "bCs": format.BoldComplexScript = DocxUnits.OnOff(e); break;
                case "i": format.Italic = DocxUnits.OnOff(e); break;
                case "iCs": format.ItalicComplexScript = DocxUnits.OnOff(e); break;
                case "caps": format.AllCaps = DocxUnits.OnOff(e); break;
                case "smallCaps": format.SmallCaps = DocxUnits.OnOff(e); break;
                case "strike": format.Strikethrough = DocxUnits.OnOff(e); break;
                case "dstrike": format.DoubleStrikethrough = DocxUnits.OnOff(e); break;
                case "outline": format.Outline = DocxUnits.OnOff(e); break;
                case "shadow": format.Shadow = DocxUnits.OnOff(e); break;
                case "emboss": format.Emboss = DocxUnits.OnOff(e); break;
                case "imprint": format.Imprint = DocxUnits.OnOff(e); break;
                case "noProof": format.NoProof = DocxUnits.OnOff(e); break;
                case "vanish": format.Hidden = DocxUnits.OnOff(e); break;
                case "specVanish": format.SpecialHidden = DocxUnits.OnOff(e); break;
                case "rtl": format.RightToLeft = DocxUnits.OnOff(e); break;
                case "cs": format.ComplexScript = DocxUnits.OnOff(e); break;
                case "color": format.Color = Color(e, "val", "themeColor", "themeTint", "themeShade"); break;
                case "spacing": format.Spacing = DocxUnits.Points(e.Val()); break;
                case "w": format.Scale = PercentOrNull(e.Val()); break;
                case "kern": format.Kerning = DocxUnits.Points(e.Val(), DocxUnits.HalfPoint); break;
                case "position": format.Position = DocxUnits.Points(e.Val(), DocxUnits.HalfPoint); break;
                case "sz": format.Size = Positive(DocxUnits.Points(e.Val(), DocxUnits.HalfPoint)); break;
                case "szCs": format.SizeComplexScript = Positive(DocxUnits.Points(e.Val(), DocxUnits.HalfPoint)); break;
                case "highlight": format.Highlight = Highlight(e.Val()); break;
                case "u":
                    format.Underline = Underline(e.Val());
                    if (e.Attribute(Ns.W + "color") is not null || e.Attribute(Ns.W + "themeColor") is not null)
                    {
                        format.UnderlineColor = Color(e, "color", "themeColor", "themeTint", "themeShade");
                    }

                    break;
                case "bdr": format.Border = Border(e); break;
                case "shd": format.Shading = Shading(e); break;
                case "vertAlign":
                    format.VerticalPosition = e.Val() switch
                    {
                        "superscript" => VerticalTextPosition.Superscript,
                        "subscript" => VerticalTextPosition.Subscript,
                        _ => VerticalTextPosition.Baseline,
                    };
                    break;
                case "lang":
                    format.Language = e.Val();
                    format.LanguageEastAsia = e.W("eastAsia");
                    format.LanguageBidi = e.W("bidi");
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
                case "keepNext": format.KeepWithNext = DocxUnits.OnOff(e); break;
                case "keepLines": format.KeepTogether = DocxUnits.OnOff(e); break;
                case "pageBreakBefore": format.PageBreakBefore = DocxUnits.OnOff(e); break;
                case "widowControl": format.WidowControl = DocxUnits.OnOff(e); break;
                case "suppressLineNumbers": format.SuppressLineNumbers = DocxUnits.OnOff(e); break;
                case "suppressAutoHyphens": format.SuppressAutoHyphens = DocxUnits.OnOff(e); break;
                case "bidi": format.Bidi = DocxUnits.OnOff(e); break;
                case "snapToGrid": format.SnapToGrid = DocxUnits.OnOff(e); break;
                case "contextualSpacing": format.ContextualSpacing = DocxUnits.OnOff(e); break;
                case "mirrorIndents": format.MirrorIndents = DocxUnits.OnOff(e); break;
                case "outlineLvl":
                    if (DocxUnits.Int(e.Val()) is int level && level is >= 0 and <= 9)
                    {
                        format.OutlineLevel = level;
                    }

                    break;
                case "jc":
                    format.Alignment = e.Val() switch
                    {
                        "left" or "start" => ParagraphAlignment.Left,
                        "center" => ParagraphAlignment.Center,
                        "right" or "end" => ParagraphAlignment.Right,
                        "both" or "lowKashida" or "mediumKashida" or "highKashida" or "thaiDistribute" => ParagraphAlignment.Justify,
                        "distribute" => ParagraphAlignment.Distribute,
                        _ => null,
                    };
                    break;
                case "spacing":
                    format.SpaceBefore = Positive(DocxUnits.Points(e.W("before")));
                    format.SpaceAfter = Positive(DocxUnits.Points(e.W("after")));
                    if (e.Attribute(Ns.W + "beforeAutospacing") is { } beforeAuto)
                    {
                        format.SpaceBeforeAuto = beforeAuto.Value is "1" or "true" or "on";
                    }

                    if (e.Attribute(Ns.W + "afterAutospacing") is { } afterAuto)
                    {
                        format.SpaceAfterAuto = afterAuto.Value is "1" or "true" or "on";
                    }

                    if (DocxUnits.Points(e.W("line")) is double line)
                    {
                        switch (e.W("lineRule"))
                        {
                            case "exact":
                                format.LineSpacingRule = LineSpacingRule.Exactly;
                                format.LineSpacing = Math.Abs(line);
                                break;
                            case "atLeast":
                                format.LineSpacingRule = LineSpacingRule.AtLeast;
                                format.LineSpacing = Math.Abs(line);
                                break;
                            default:
                                format.LineSpacingRule = LineSpacingRule.Multiple;
                                format.LineSpacing = Math.Abs(line * 20 / 240);
                                break;
                        }
                    }

                    if (e.Attribute(Ns.W + "beforeLines") is not null || e.Attribute(Ns.W + "afterLines") is not null)
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name.LocalName is "beforeLines" or "afterLines")));
                    }

                    break;
                case "ind":
                    format.LeftIndent = DocxUnits.Points(e.W("start") ?? e.W("left"));
                    format.RightIndent = DocxUnits.Points(e.W("end") ?? e.W("right"));
                    if (DocxUnits.Points(e.W("hanging")) is double hanging)
                    {
                        format.FirstLineIndent = hanging == 0 ? 0 : -hanging;
                    }
                    else if (DocxUnits.Points(e.W("firstLine")) is double first)
                    {
                        format.FirstLineIndent = first;
                    }

                    if (e.Attributes().Any(a => a.Name.LocalName.EndsWith("Chars", StringComparison.Ordinal)))
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name.LocalName.EndsWith("Chars", StringComparison.Ordinal))));
                    }

                    break;
                case "tabs":
                    format.TabStops = [.. e.Elements(Ns.W + "tab").Select(Tab).OfType<TabStop>()];
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
                case "jc":
                    format.Alignment = e.Val() switch
                    {
                        "center" => TableAlignment.Center,
                        "right" or "end" => TableAlignment.Right,
                        "left" or "start" => TableAlignment.Left,
                        _ => null,
                    };
                    break;
                case "tblInd": format.Indent = Width(e)?.Unit == WidthUnit.Points ? Width(e)!.Value.Value : null; break;
                case "tblLayout": format.Layout = e.W("type") == "fixed" ? TableLayoutKind.Fixed : TableLayoutKind.Autofit; break;
                case "tblCellSpacing": format.CellSpacing = Width(e)?.Unit == WidthUnit.Points ? Width(e)!.Value.Value : null; break;
                case "bidiVisual": format.Bidi = DocxUnits.OnOff(e); break;
                case "tblStyleRowBandSize": format.RowBandSize = DocxUnits.Int(e.Val()) is int r && r > 0 ? r : null; break;
                case "tblStyleColBandSize": format.ColumnBandSize = DocxUnits.Int(e.Val()) is int c && c > 0 ? c : null; break;
                case "shd": format.Shading = Shading(e); break;
                case "tblCellMar":
                    foreach (XElement side in e.Elements())
                    {
                        double? value = Width(side)?.Unit == WidthUnit.Points ? Width(side)!.Value.Value : null;
                        switch (side.Name.LocalName)
                        {
                            case "top": format.CellMarginTop = value; break;
                            case "bottom": format.CellMarginBottom = value; break;
                            case "left" or "start": format.CellMarginLeft = value; break;
                            case "right" or "end": format.CellMarginRight = value; break;
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
                default:
                    unknown?.Invoke(e);
                    break;
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
                    format.Height = Positive(DocxUnits.Points(e.Val()));
                    format.HeightRule = e.W("hRule") switch
                    {
                        "exact" => RowHeightRule.Exactly,
                        "auto" => RowHeightRule.Auto,
                        _ => RowHeightRule.AtLeast,
                    };
                    break;
                case "tblHeader": format.IsHeader = DocxUnits.OnOff(e); break;
                case "cantSplit": format.CantSplit = DocxUnits.OnOff(e); break;
                case "hidden": format.Hidden = DocxUnits.OnOff(e); break;
                case "jc":
                    format.Alignment = e.Val() switch
                    {
                        "center" => TableAlignment.Center,
                        "right" or "end" => TableAlignment.Right,
                        "left" or "start" => TableAlignment.Left,
                        _ => null,
                    };
                    break;
                case "tblCellSpacing": format.CellSpacing = Width(e)?.Unit == WidthUnit.Points ? Width(e)!.Value.Value : null; break;
                case "ins" or "del":
                    break;
                default:
                    unknown?.Invoke(e);
                    break;
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
                case "gridSpan": format.GridSpan = DocxUnits.Int(e.Val()) is int span && span > 1 ? span : null; break;
                case "vMerge": format.VerticalMerge = e.Val() == "restart" ? VerticalMerge.Restart : VerticalMerge.Continue; break;
                case "vAlign":
                    format.VerticalAlignment = e.Val() switch
                    {
                        "center" => VerticalAlignment.Center,
                        "bottom" => VerticalAlignment.Bottom,
                        "top" => VerticalAlignment.Top,
                        _ => null,
                    };
                    break;
                case "noWrap": format.NoWrap = DocxUnits.OnOff(e); break;
                case "tcFitText": format.FitText = DocxUnits.OnOff(e); break;
                case "hideMark": format.HideMark = DocxUnits.OnOff(e); break;
                case "shd": format.Shading = Shading(e); break;
                case "textDirection":
                    format.TextDirection = e.Val() switch
                    {
                        "tbRl" or "tbRlV" => TextDirection.TopToBottom,
                        "btLr" => TextDirection.BottomToTop,
                        "lrTb" => TextDirection.Horizontal,
                        _ => null,
                    };
                    break;
                case "tcMar":
                    foreach (XElement side in e.Elements())
                    {
                        double? value = Width(side)?.Unit == WidthUnit.Points ? Width(side)!.Value.Value : null;
                        switch (side.Name.LocalName)
                        {
                            case "top": format.MarginTop = value; break;
                            case "bottom": format.MarginBottom = value; break;
                            case "left" or "start": format.MarginLeft = value; break;
                            case "right" or "end": format.MarginRight = value; break;
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
                    setup.PageWidth = Positive(DocxUnits.Points(e.W("w")));
                    setup.PageHeight = Positive(DocxUnits.Points(e.W("h")));
                    if (e.W("orient") is { } orient)
                    {
                        setup.Orientation = orient == "landscape" ? PageOrientation.Landscape : PageOrientation.Portrait;
                    }

                    break;
                case "pgMar":
                    setup.MarginTop = DocxUnits.Points(e.W("top"));
                    setup.MarginBottom = DocxUnits.Points(e.W("bottom"));
                    setup.MarginLeft = Positive(DocxUnits.Points(e.W("left") ?? e.W("start")));
                    setup.MarginRight = Positive(DocxUnits.Points(e.W("right") ?? e.W("end")));
                    setup.HeaderDistance = Positive(DocxUnits.Points(e.W("header")));
                    setup.FooterDistance = Positive(DocxUnits.Points(e.W("footer")));
                    setup.Gutter = Positive(DocxUnits.Points(e.W("gutter")));
                    break;
                case "type":
                    setup.SectionStart = e.Val() switch
                    {
                        "continuous" => SectionStart.Continuous,
                        "oddPage" => SectionStart.OddPage,
                        "evenPage" => SectionStart.EvenPage,
                        "nextColumn" => SectionStart.NewColumn,
                        _ => SectionStart.NewPage,
                    };
                    break;
                case "titlePg": setup.DifferentFirstPage = DocxUnits.OnOff(e); break;
                case "bidi": setup.Bidi = DocxUnits.OnOff(e); break;
                case "vAlign":
                    setup.VerticalAlignment = e.Val() switch
                    {
                        "center" => VerticalAlignment.Center,
                        "bottom" => VerticalAlignment.Bottom,
                        "both" => VerticalAlignment.Justify,
                        _ => VerticalAlignment.Top,
                    };
                    break;
                case "textDirection":
                    setup.TextDirection = e.Val() switch
                    {
                        "tbRl" or "tbRlV" => TextDirection.TopToBottom,
                        "btLr" => TextDirection.BottomToTop,
                        _ => TextDirection.Horizontal,
                    };
                    break;
                case "pgNumType":
                    setup.PageNumberStart = DocxUnits.Int(e.W("start"));
                    setup.PageNumberStyle = e.W("fmt") is { } fmt ? NumberFormat(fmt) : null;
                    if (e.Attributes().Any(a => a.Name.LocalName is "chapStyle" or "chapSep"))
                    {
                        unknown?.Invoke(new XElement(e.Name, e.Attributes().Where(a => a.Name.LocalName is "chapStyle" or "chapSep")));
                    }

                    break;
                case "cols":
                    if (DocxUnits.Int(e.W("num")) is int count && count > 0)
                    {
                        setup.ColumnCount = Math.Min(count, 45);
                    }

                    setup.ColumnSpacing = Positive(DocxUnits.Points(e.W("space")));
                    if (e.Attribute(Ns.W + "equalWidth") is { } equal)
                    {
                        setup.EqualColumnWidths = equal.Value is "1" or "true" or "on";
                    }

                    if (e.Attribute(Ns.W + "sep") is { } separator)
                    {
                        setup.ColumnSeparator = separator.Value is "1" or "true" or "on";
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
        format.FontAscii = e.W("ascii") ?? format.FontAscii;
        format.FontHighAnsi = e.W("hAnsi") ?? format.FontHighAnsi;
        format.FontEastAsia = e.W("eastAsia") ?? format.FontEastAsia;
        format.FontComplexScript = e.W("cs") ?? format.FontComplexScript;
        format.ThemeFontAscii = ThemeFontOf(e.W("asciiTheme")) ?? format.ThemeFontAscii;
        format.ThemeFontHighAnsi = ThemeFontOf(e.W("hAnsiTheme")) ?? format.ThemeFontHighAnsi;
        format.ThemeFontEastAsia = ThemeFontOf(e.W("eastAsiaTheme")) ?? format.ThemeFontEastAsia;
        format.ThemeFontComplexScript = ThemeFontOf(e.W("cstheme")) ?? format.ThemeFontComplexScript;
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
            format.LookFirstRow = Flag(e.W("firstRow"));
            format.LookLastRow = Flag(e.W("lastRow"));
            format.LookFirstColumn = Flag(e.W("firstColumn"));
            format.LookLastColumn = Flag(e.W("lastColumn"));
            format.LookNoRowBands = Flag(e.W("noHBand"));
            format.LookNoColumnBands = Flag(e.W("noVBand"));
        }
        else if (e.Val() is { } hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mask))
        {
            format.LookFirstRow = (mask & 0x0020) != 0;
            format.LookLastRow = (mask & 0x0040) != 0;
            format.LookFirstColumn = (mask & 0x0080) != 0;
            format.LookLastColumn = (mask & 0x0100) != 0;
            format.LookNoRowBands = (mask & 0x0200) != 0;
            format.LookNoColumnBands = (mask & 0x0400) != 0;
        }

        static bool? Flag(string? value)
        {
            return value is null ? null : value is "1" or "true" or "on";
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

    private static double? PercentOrNull(string? value)
    {
        string? text = value?.TrimEnd('%');
        return text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && double.IsFinite(percent) ? Math.Clamp(percent, 1, 600) : null;
    }
}
