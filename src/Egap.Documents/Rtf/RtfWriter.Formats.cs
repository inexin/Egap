using System.Globalization;

using Egami.Pixels;

namespace Egap.Documents.Rtf;

/// <summary>The formatting words: character, paragraph, border, shading, frame and section formatting, and the colours and fonts they name.</summary>
internal sealed partial class RtfWriter
{
    private readonly Dictionary<string, int> fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<Rgba8, int> colors = [];
    private readonly List<Rgba8> colorList = [];

    /// <summary>The font table index of <paramref name="name"/>, added when new.</summary>
    private int Font(string name)
    {
        if (!fonts.TryGetValue(name, out int index))
        {
            index = fonts.Count;
            fonts[name] = index;
        }

        return index;
    }

    /// <summary>The colour table index of a colour: 0, the table's empty first entry, for automatic.</summary>
    private int Color(DocumentColor color)
    {
        if (color.Kind == DocumentColorKind.Auto)
        {
            return 0;
        }

        // RTF has no theme colours: a theme colour is written as the colour it is in the document's theme.
        Rgba8 rgb = color.Resolve(document.Theme, new Rgba8(0, 0, 0, 255));
        rgb = new Rgba8(rgb.R, rgb.G, rgb.B, 255);
        if (!colors.TryGetValue(rgb, out int index))
        {
            colorList.Add(rgb);
            index = colorList.Count;
            colors[rgb] = index;
        }

        return index;
    }

    // ---- Character formatting ----

    /// <summary>
    /// The words for a run's formatting. <paramref name="style"/> is the run's character style, written as <c>\cs</c>; the
    /// formatting is the whole formatting, the styles' included.
    /// </summary>
    private void RunWords(RunFormat f, CharacterStyle? style)
    {
        if (style is { IsInDocument: true })
        {
            Word("cs", StyleIndex(style));
        }

        if (f.IsEmpty)
        {
            return;
        }

        FontWords(f);
        if (f.Bold is bool bold)
        {
            Toggle("b", bold);
        }

        if (f.Italic is bool italic)
        {
            Toggle("i", italic);
        }

        if (f.BoldComplexScript is bool boldComplex)
        {
            Toggle("ab", boldComplex);
        }

        if (f.ItalicComplexScript is bool italicComplex)
        {
            Toggle("ai", italicComplex);
        }

        if (f.Strikethrough is bool strike)
        {
            Toggle("strike", strike);
        }

        if (f.DoubleStrikethrough is bool doubleStrike)
        {
            Word("striked", doubleStrike ? 1 : 0);
        }

        if (f.AllCaps is bool caps)
        {
            Toggle("caps", caps);
        }

        if (f.SmallCaps is bool smallCaps)
        {
            Toggle("scaps", smallCaps);
        }

        if (f.Hidden is bool hidden)
        {
            Toggle("v", hidden);
        }

        if (f.Emboss is bool emboss)
        {
            Toggle("embo", emboss);
        }

        if (f.Imprint is bool imprint)
        {
            Toggle("impr", imprint);
        }

        if (f.Outline is bool outline)
        {
            Toggle("outl", outline);
        }

        if (f.Shadow is bool shadow)
        {
            Toggle("shad", shadow);
        }

        if (f.NoProof is bool noProof)
        {
            Toggle("noproof", noProof);
        }

        if (f.Underline is { } underline)
        {
            Word(underline switch
            {
                UnderlineStyle.None => "ulnone",
                UnderlineStyle.Words => "ulw",
                UnderlineStyle.Double => "uldb",
                UnderlineStyle.Thick => "ulth",
                UnderlineStyle.Dotted => "uld",
                UnderlineStyle.DottedHeavy => "ulthd",
                UnderlineStyle.Dash => "uldash",
                UnderlineStyle.DashedHeavy => "ulthdash",
                UnderlineStyle.DashLong => "ulldash",
                UnderlineStyle.DashLongHeavy => "ulthldash",
                UnderlineStyle.DotDash => "uldashd",
                UnderlineStyle.DashDotHeavy => "ulthdashd",
                UnderlineStyle.DotDotDash => "uldashdd",
                UnderlineStyle.DashDotDotHeavy => "ulthdashdd",
                UnderlineStyle.Wave => "ulwave",
                UnderlineStyle.WavyHeavy => "ulhwave",
                UnderlineStyle.WavyDouble => "ululdbwave",
                _ => "ul",
            });
        }

        if (f.UnderlineColor is { } underlineColor)
        {
            Word("ulc", Color(underlineColor));
        }

        if (f.Size is double size && Clamp(size * 2) > 0)
        {
            Word("fs", Math.Min(Clamp(size * 2), 3276));
        }

        if (f.SizeComplexScript is double sizeComplex && Clamp(sizeComplex * 2) > 0)
        {
            Word("afs", Math.Min(Clamp(sizeComplex * 2), 3276));
        }

        if (f.Color is { } color)
        {
            Word("cf", Color(color));
        }

        if (f.Highlight is { } highlight)
        {
            Word("highlight", highlight == HighlightColor.None ? 0 : Color(DocumentColor.FromRgb(HighlightRgb(highlight))));
        }

        if (f.Shading is { } shading)
        {
            ShadingWords(shading, "chcbpat", "chcfpat", "chshdng", "ch");
        }

        if (f.Border is { } border)
        {
            Word("chbrdr");
            BorderWords(border);
        }

        if (f.VerticalPosition is { } vertical)
        {
            Word(vertical switch { VerticalTextPosition.Superscript => "super", VerticalTextPosition.Subscript => "sub", _ => "nosupersub" });
        }

        if (f.Position is double position)
        {
            long halfPoints = Clamp(position * 2);
            Word(halfPoints < 0 ? "dn" : "up", Math.Abs(halfPoints));
        }

        if (f.Spacing is double spacing)
        {
            Word("expndtw", Twips(spacing));
        }

        if (f.Scale is double scale && Clamp(scale) > 0)
        {
            Word("charscalex", Math.Min(Clamp(scale), 600));
        }

        if (f.Kerning is double kerning)
        {
            Word("kerning", Math.Max(0, Clamp(kerning * 2)));
        }

        LanguageWord("lang", f.Language);
        LanguageWord("langfe", f.LanguageEastAsia);
        LanguageWord("alang", f.LanguageBidi);
        if (f.RightToLeft == true)
        {
            Word("rtlch");
        }
    }

    /// <summary>
    /// The four font slots. <c>\f</c> names the Latin font (both its low and high halves), <c>\hich\af</c> a different
    /// high-ANSI font, <c>\dbch\af</c> the East Asian one and <c>\rtlch\af</c> the complex-script one, as Word writes them.
    /// </summary>
    private void FontWords(RunFormat f)
    {
        string? ascii = FontOf(f.FontAscii, f.ThemeFontAscii);
        string? high = FontOf(f.FontHighAnsi, f.ThemeFontHighAnsi);
        string? eastAsia = FontOf(f.FontEastAsia, f.ThemeFontEastAsia);
        string? complex = FontOf(f.FontComplexScript, f.ThemeFontComplexScript);
        if (complex is not null)
        {
            Word("rtlch");
            Word("af", Font(complex));
            Word("ltrch");
        }

        if (ascii is not null && high is not null)
        {
            Word("f", Font(ascii));
            if (high != ascii)
            {
                Word("hich");
                Word("af", Font(high));
            }
        }
        else if (ascii is not null)
        {
            Word("loch");
            Word("af", Font(ascii));
        }
        else if (high is not null)
        {
            Word("hich");
            Word("af", Font(high));
        }

        if (eastAsia is not null)
        {
            Word("dbch");
            Word("af", Font(eastAsia));
        }

        if (ascii is not null || high is not null || eastAsia is not null)
        {
            Word("loch");
        }
    }

    /// <summary>A font slot's font: the theme font's name in the document's theme when the slot names one, else its own name.</summary>
    private string? FontOf(string? name, ThemeFont? theme)
    {
        string? font = theme is { } t ? document.Theme.GetFont(t) ?? name : name;
        return string.IsNullOrEmpty(font) ? null : font;
    }

    private void LanguageWord(string word, string? language)
    {
        if (Lcid(language) is int lcid)
        {
            Word(word, lcid);
        }
    }

    /// <summary>A language tag's Windows language id, or null when it has none.</summary>
    private static int? Lcid(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return null;
        }

        try
        {
            int lcid = CultureInfo.GetCultureInfo(language).LCID;
            return lcid is > 0 and not 0x1000 and not 0x0C00 and not 0x007F and < 0x10000 ? lcid : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static Rgba8 HighlightRgb(HighlightColor color)
    {
        uint rgb = color switch
        {
            HighlightColor.Black => 0x000000,
            HighlightColor.Blue => 0x0000FF,
            HighlightColor.Cyan => 0x00FFFF,
            HighlightColor.Green => 0x00FF00,
            HighlightColor.Magenta => 0xFF00FF,
            HighlightColor.Red => 0xFF0000,
            HighlightColor.White => 0xFFFFFF,
            HighlightColor.DarkBlue => 0x000080,
            HighlightColor.DarkCyan => 0x008080,
            HighlightColor.DarkGreen => 0x008000,
            HighlightColor.DarkMagenta => 0x800080,
            HighlightColor.DarkRed => 0x800000,
            HighlightColor.DarkYellow => 0x808000,
            HighlightColor.DarkGray => 0x808080,
            HighlightColor.LightGray => 0xC0C0C0,
            _ => 0xFFFF00,
        };
        return new Rgba8((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }

    // ---- Paragraph formatting ----

    private void ParagraphWords(ParagraphFormat f)
    {
        if (f.IsEmpty)
        {
            return;
        }

        if (f.Alignment is { } alignment)
        {
            if (alignment == ParagraphAlignment.Justify && f.Kashida is { } kashida)
            {
                Word("qk", kashida switch { KashidaLevel.Low => 0, KashidaLevel.Medium => 10, _ => 20 });
            }
            else
            {
                Word(alignment switch
                {
                    ParagraphAlignment.Center => "qc",
                    ParagraphAlignment.Right => "qr",
                    ParagraphAlignment.Justify => "qj",
                    ParagraphAlignment.Distribute => "qd",
                    _ => "ql",
                });
            }
        }

        if (f.LeftIndent is double left)
        {
            Word("li", Twips(left));
            Word("lin", Twips(left));
        }

        if (f.RightIndent is double right)
        {
            Word("ri", Twips(right));
            Word("rin", Twips(right));
        }

        if (f.FirstLineIndent is double first)
        {
            Word("fi", Twips(first));
        }

        if (f.SpaceBefore is double before)
        {
            Word("sb", Math.Max(0, Twips(before)));
        }

        if (f.SpaceAfter is double after)
        {
            Word("sa", Math.Max(0, Twips(after)));
        }

        if (f.SpaceBeforeAuto is bool beforeAuto)
        {
            Word("sbauto", beforeAuto ? 1 : 0);
        }

        if (f.SpaceAfterAuto is bool afterAuto)
        {
            Word("saauto", afterAuto ? 1 : 0);
        }

        if (f.LineSpacing is double lineSpacing)
        {
            switch (f.LineSpacingRule)
            {
                case LineSpacingRule.Multiple:
                    Word("sl", Math.Max(1, Clamp(lineSpacing * 240)));
                    Word("slmult", 1);
                    break;
                case LineSpacingRule.Exactly:
                    Word("sl", -Math.Max(1, Math.Abs(Twips(lineSpacing))));
                    Word("slmult", 0);
                    break;
                default:
                    Word("sl", Math.Max(1, Math.Abs(Twips(lineSpacing))));
                    Word("slmult", 0);
                    break;
            }
        }

        if (f.KeepTogether is bool keep)
        {
            Toggle("keep", keep);
        }

        if (f.KeepWithNext is bool keepNext)
        {
            Toggle("keepn", keepNext);
        }

        if (f.PageBreakBefore is bool pageBreak)
        {
            Toggle("pagebb", pageBreak);
        }

        if (f.WidowControl is bool widows)
        {
            Word(widows ? "widctlpar" : "nowidctlpar");
        }

        if (f.OutlineLevel is int outline)
        {
            Word("outlinelevel", Math.Clamp(outline, 0, 9));
        }

        if (f.ContextualSpacing is bool contextual)
        {
            Toggle("contextualspace", contextual);
        }

        if (f.Bidi is bool bidi)
        {
            Word(bidi ? "rtlpar" : "ltrpar");
        }

        if (f.SuppressLineNumbers == true)
        {
            Word("noline");
        }

        if (f.SuppressAutoHyphens is bool noHyphens)
        {
            Word("hyphpar", noHyphens ? 0 : 1);
        }

        if (f.SnapToGrid == false)
        {
            Word("nosnaplinegrid");
        }

        if (f.MirrorIndents is bool mirror)
        {
            Toggle("indmirror", mirror);
        }

        if (f.TabStops is { Count: > 0 } tabs)
        {
            TabWords(tabs);
        }

        BorderSide("brdrt", f.BorderTop);
        BorderSide("brdrb", f.BorderBottom);
        BorderSide("brdrl", f.BorderLeft);
        BorderSide("brdrr", f.BorderRight);
        BorderSide("brdrbtw", f.BorderBetween);
        BorderSide("brdrbar", f.BorderBar);
        if (f.Shading is { } shading)
        {
            ShadingWords(shading, "cbpat", "cfpat", "shading", "");
        }

        if (f.Frame is { } frame)
        {
            FrameWords(frame);
        }

        if (f.List is { } list && list != List.None && document.Lists.Contains(list) && list.Id > 0)
        {
            Word("ls", list.Id);
            Word("ilvl", Math.Clamp(f.ListLevel ?? 0, 0, 8));
        }
    }

    private void TabWords(IReadOnlyList<TabStop> tabs)
    {
        // Word keeps at most 64 tab stops a paragraph; the reader does too.
        foreach (TabStop stop in tabs.Where(t => t.Alignment != TabAlignment.Clear).OrderBy(t => t.Position).Take(64))
        {
            if (stop.Alignment == TabAlignment.Bar)
            {
                Word("tb", Math.Max(0, Twips(stop.Position)));
                continue;
            }

            switch (stop.Alignment)
            {
                case TabAlignment.Center:
                    Word("tqc");
                    break;
                case TabAlignment.Right:
                    Word("tqr");
                    break;
                case TabAlignment.Decimal:
                    Word("tqdec");
                    break;
            }

            switch (stop.Leader)
            {
                case TabLeader.Dot:
                    Word("tldot");
                    break;
                case TabLeader.Hyphen:
                    Word("tlhyph");
                    break;
                case TabLeader.Underscore:
                    Word("tlul");
                    break;
                case TabLeader.Heavy:
                    Word("tlth");
                    break;
                case TabLeader.MiddleDot:
                    Word("tlmdot");
                    break;
            }

            Word("tx", Math.Max(0, Twips(stop.Position)));
        }
    }

    private void FrameWords(FramePosition frame)
    {
        if (frame.Width is double width && Twips(width) > 0)
        {
            Word("absw", Twips(width));
        }

        if (frame.Height is double height && Twips(height) != 0)
        {
            long twips = Math.Abs(Twips(height));
            Word("absh", frame.HeightRule == RowHeightRule.Exactly ? -twips : twips);
        }

        Word(frame.HorizontalAnchor switch { HorizontalAnchor.Margin => "phmrg", HorizontalAnchor.Page => "phpg", _ => "phcol" });
        if (frame.HorizontalOffset != 0 || frame.HorizontalAlignment == FloatingAlignment.None)
        {
            Word(frame.HorizontalOffset < 0 ? "posnegx" : "posx", Twips(frame.HorizontalOffset));
        }

        switch (frame.HorizontalAlignment)
        {
            case FloatingAlignment.Start:
                Word("posxl");
                break;
            case FloatingAlignment.Center:
                Word("posxc");
                break;
            case FloatingAlignment.End:
                Word("posxr");
                break;
            case FloatingAlignment.Inside:
                Word("posxi");
                break;
            case FloatingAlignment.Outside:
                Word("posxo");
                break;
        }

        Word(frame.VerticalAnchor switch { VerticalAnchor.Margin => "pvmrg", VerticalAnchor.Page => "pvpg", _ => "pvpara" });
        if (frame.VerticalOffset != 0 || frame.VerticalAlignment == FloatingAlignment.None)
        {
            Word(frame.VerticalOffset < 0 ? "posnegy" : "posy", Twips(frame.VerticalOffset));
        }

        switch (frame.VerticalAlignment)
        {
            case FloatingAlignment.Start:
                Word("posyt");
                break;
            case FloatingAlignment.Center:
                Word("posyc");
                break;
            case FloatingAlignment.End:
                Word("posyb");
                break;
            case FloatingAlignment.Inside:
                Word("posyin");
                break;
            case FloatingAlignment.Outside:
                Word("posyout");
                break;
        }

        if (frame.DistanceHorizontal != 0)
        {
            Word("dfrmtxtx", Twips(frame.DistanceHorizontal));
        }

        if (frame.DistanceVertical != 0)
        {
            Word("dfrmtxty", Twips(frame.DistanceVertical));
        }

        switch (frame.Wrap)
        {
            case FrameWrap.NotBeside:
                Word("nowrap");
                break;
            case FrameWrap.Around:
                Word("wraparound");
                break;
            case FrameWrap.Tight:
                Word("wraptight");
                break;
            case FrameWrap.Through:
                Word("wrapthrough");
                break;
        }

        if (frame.DropCap != DropCap.None)
        {
            Word("dropcapli", Math.Clamp(frame.DropCapLines, 1, 10));
            Word("dropcapt", frame.DropCap == DropCap.Margin ? 2 : 1);
        }
        else if (frame.DropCapLines != 1)
        {
            Word("dropcapli", Math.Clamp(frame.DropCapLines, 1, 10));
        }

        if (frame.AnchorLock)
        {
            Word("abslock");
        }
    }

    // ---- Borders and shading ----

    private void BorderSide(string word, Border? border)
    {
        if (border is not null)
        {
            Word(word);
            BorderWords(border);
        }
    }

    private void BorderWords(Border border)
    {
        Word(border.Style switch
        {
            BorderStyle.None => "brdrnone",
            BorderStyle.Thick => "brdrth",
            BorderStyle.Double => "brdrdb",
            BorderStyle.Dotted => "brdrdot",
            BorderStyle.Dashed => "brdrdash",
            BorderStyle.DotDash => "brdrdashd",
            BorderStyle.DotDotDash => "brdrdashdd",
            BorderStyle.Triple => "brdrtriple",
            BorderStyle.ThinThickSmallGap => "brdrtnthsg",
            BorderStyle.ThickThinSmallGap => "brdrthtnsg",
            BorderStyle.ThinThickThinSmallGap => "brdrtnthtnsg",
            BorderStyle.ThinThickMediumGap => "brdrtnthmg",
            BorderStyle.ThickThinMediumGap => "brdrthtnmg",
            BorderStyle.ThinThickThinMediumGap => "brdrtnthtnmg",
            BorderStyle.ThinThickLargeGap => "brdrtnthlg",
            BorderStyle.ThickThinLargeGap => "brdrthtnlg",
            BorderStyle.ThinThickThinLargeGap => "brdrtnthtnlg",
            BorderStyle.Wave => "brdrwavy",
            BorderStyle.DoubleWave => "brdrwavydb",
            BorderStyle.DashSmallGap => "brdrdashsm",
            BorderStyle.DashDotStroked => "brdrdashdotstr",
            BorderStyle.ThreeDEmboss => "brdremboss",
            BorderStyle.ThreeDEngrave => "brdrengrave",
            BorderStyle.Outset => "brdroutset",
            BorderStyle.Inset => "brdrinset",
            _ => "brdrs",
        });
        if (border.Style != BorderStyle.None || border.Width != 0)
        {
            Word("brdrw", Math.Clamp(Twips(border.Width), 0, 255));
        }

        if (border.Space != 0)
        {
            Word("brsp", Math.Clamp(Twips(border.Space), 0, 620));
        }

        if (border.Color.Kind != DocumentColorKind.Auto)
        {
            Word("brdrcf", Color(border.Color));
        }

        if (border.Shadow)
        {
            Word("brdrsh");
        }

        if (border.Frame)
        {
            Word("brdrframe");
        }
    }

    /// <summary>
    /// A shading's words: the fill (<paramref name="fill"/>), the pattern colour, then the share of pattern colour for a
    /// clear, solid or percentage shading, or the pattern's own word after <paramref name="patternPrefix"/>. A table has no
    /// pattern words (null).
    /// </summary>
    private void ShadingWords(Shading shading, string fill, string patternColor, string percent, string? patternPrefix)
    {
        bool any = false;
        if (shading.Fill.Kind != DocumentColorKind.Auto)
        {
            Word(fill, Color(shading.Fill));
            any = true;
        }

        if (shading.PatternColor.Kind != DocumentColorKind.Auto)
        {
            Word(patternColor, Color(shading.PatternColor));
            any = true;
        }

        long share = Math.Clamp(Clamp(shading.Percent * 100), 0, 10000);
        string? pattern = shading.Pattern switch
        {
            ShadingPattern.ThinHorizontalStripe => "bghoriz",
            ShadingPattern.ThinVerticalStripe => "bgvert",
            ShadingPattern.ThinReverseDiagonalStripe => "bgfdiag",
            ShadingPattern.ThinDiagonalStripe => "bgbdiag",
            ShadingPattern.ThinHorizontalCross => "bgcross",
            ShadingPattern.ThinDiagonalCross => "bgdcross",
            ShadingPattern.HorizontalStripe => "bgdkhoriz",
            ShadingPattern.VerticalStripe => "bgdkvert",
            ShadingPattern.ReverseDiagonalStripe => "bgdkfdiag",
            ShadingPattern.DiagonalStripe => "bgdkbdiag",
            ShadingPattern.HorizontalCross => "bgdkcross",
            ShadingPattern.DiagonalCross => "bgdkdcross",
            _ => null,
        };
        if (pattern is not null && patternPrefix is not null)
        {
            if (share != 0)
            {
                Word(percent, share);
            }

            Word(patternPrefix + pattern);
            return;
        }

        switch (shading.Pattern)
        {
            case ShadingPattern.Solid:
                Word(percent, 10000);
                break;
            case ShadingPattern.Percent when share is > 0 and < 10000:
                Word(percent, share);
                break;
            default:
                if (!any)
                {
                    // A clear shading with nothing else to say still makes one.
                    Word(percent, 0);
                }

                break;
        }
    }

    // ---- Sections ----

    private void SectionWords(PageSetup s)
    {
        if (s.SectionStart is { } start)
        {
            Word(start switch
            {
                SectionStart.Continuous => "sbknone",
                SectionStart.NewColumn => "sbkcol",
                SectionStart.EvenPage => "sbkeven",
                SectionStart.OddPage => "sbkodd",
                _ => "sbkpage",
            });
        }

        if (s.PageWidth is double width && Twips(width) > 0)
        {
            Word("pgwsxn", Twips(width));
        }

        if (s.PageHeight is double height && Twips(height) > 0)
        {
            Word("pghsxn", Twips(height));
        }

        if (s.MarginLeft is double left)
        {
            Word("marglsxn", Math.Max(0, Twips(left)));
        }

        if (s.MarginRight is double right)
        {
            Word("margrsxn", Math.Max(0, Twips(right)));
        }

        if (s.MarginTop is double top)
        {
            Word("margtsxn", Twips(top));
        }

        if (s.MarginBottom is double bottom)
        {
            Word("margbsxn", Twips(bottom));
        }

        if (s.Gutter is double gutter)
        {
            Word("guttersxn", Math.Max(0, Twips(gutter)));
        }

        if (s.HeaderDistance is double header)
        {
            Word("headery", Math.Max(0, Twips(header)));
        }

        if (s.FooterDistance is double footer)
        {
            Word("footery", Math.Max(0, Twips(footer)));
        }

        if (s.Orientation == PageOrientation.Landscape)
        {
            Word("lndscpsxn");
        }

        if (s.DifferentFirstPage == true)
        {
            Word("titlepg");
        }

        if (s.ColumnCount is int columns)
        {
            Word("cols", Math.Clamp(columns, 1, 45));
        }

        if (s.ColumnSpacing is double spacing)
        {
            Word("colsx", Math.Max(0, Twips(spacing)));
        }

        if (s.Columns is { Count: > 0 } list)
        {
            for (int i = 0; i < Math.Min(list.Count, 45); i++)
            {
                Word("colno", i + 1);
                Word("colw", Math.Max(0, Twips(list[i].Width)));
                if (list[i].SpaceAfter != 0)
                {
                    Word("colsr", Math.Max(0, Twips(list[i].SpaceAfter)));
                }
            }
        }

        if (s.ColumnSeparator == true)
        {
            Word("linebetcol");
        }

        if (s.PageNumberStart is int pageStart)
        {
            Word("pgnstarts", Math.Max(0, pageStart));
            Word("pgnrestart");
        }

        if (s.PageNumberStyle is { } numbers)
        {
            Word(numbers switch
            {
                NumberStyle.UpperRoman => "pgnucrm",
                NumberStyle.LowerRoman => "pgnlcrm",
                NumberStyle.UpperLetter => "pgnucltr",
                NumberStyle.LowerLetter => "pgnlcltr",
                _ => "pgndec",
            });
        }

        if (s.VerticalAlignment is { } vertical)
        {
            Word(vertical switch
            {
                VerticalAlignment.Center => "vertalc",
                VerticalAlignment.Bottom => "vertalb",
                VerticalAlignment.Justify => "vertalj",
                _ => "vertalt",
            });
        }

        if (s.Bidi == true)
        {
            Word("rtlsect");
        }
    }
}
