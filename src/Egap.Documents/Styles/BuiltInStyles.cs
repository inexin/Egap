namespace Egap.Documents;

/// <summary>Styles Word knows by name, which <see cref="StyleCollection.AddBuiltIn"/> adds with Word 2013+'s definitions.</summary>
public enum BuiltInStyle
{
    /// <summary>Normal, the default paragraph style.</summary>
    Normal,

    /// <summary>Default Paragraph Font, the default character style.</summary>
    DefaultParagraphFont,

    /// <summary>Normal Table, the default table style.</summary>
    TableNormal,

    /// <summary>Heading 1.</summary>
    Heading1,

    /// <summary>Heading 2.</summary>
    Heading2,

    /// <summary>Heading 3.</summary>
    Heading3,

    /// <summary>Heading 4.</summary>
    Heading4,

    /// <summary>Heading 5.</summary>
    Heading5,

    /// <summary>Heading 6.</summary>
    Heading6,

    /// <summary>Heading 7.</summary>
    Heading7,

    /// <summary>Heading 8.</summary>
    Heading8,

    /// <summary>Heading 9.</summary>
    Heading9,

    /// <summary>Title.</summary>
    Title,

    /// <summary>Subtitle.</summary>
    Subtitle,

    /// <summary>Quote.</summary>
    Quote,

    /// <summary>List Paragraph.</summary>
    ListParagraph,

    /// <summary>Caption.</summary>
    Caption,

    /// <summary>No Spacing.</summary>
    NoSpacing,

    /// <summary>Header.</summary>
    Header,

    /// <summary>Footer.</summary>
    Footer,

    /// <summary>Footnote Text.</summary>
    FootnoteText,

    /// <summary>Footnote Reference (character).</summary>
    FootnoteReference,

    /// <summary>Endnote Text.</summary>
    EndnoteText,

    /// <summary>Endnote Reference (character).</summary>
    EndnoteReference,

    /// <summary>Comment Text.</summary>
    CommentText,

    /// <summary>Comment Reference (character).</summary>
    CommentReference,

    /// <summary>Hyperlink (character).</summary>
    Hyperlink,

    /// <summary>FollowedHyperlink (character).</summary>
    FollowedHyperlink,

    /// <summary>Strong (character, bold).</summary>
    Strong,

    /// <summary>Emphasis (character, italic).</summary>
    Emphasis,

    /// <summary>HTML Code (character, monospaced).</summary>
    HtmlCode,

    /// <summary>HTML Preformatted (paragraph, monospaced).</summary>
    HtmlPreformatted,

    /// <summary>Table Grid (table with single borders).</summary>
    TableGrid,

    /// <summary>TOC 1, the first table of contents level; TOC 2 to TOC 9 follow.</summary>
    Toc1,

    /// <summary>TOC 2.</summary>
    Toc2,

    /// <summary>TOC 3.</summary>
    Toc3,

    /// <summary>TOC 4.</summary>
    Toc4,

    /// <summary>TOC 5.</summary>
    Toc5,

    /// <summary>TOC 6.</summary>
    Toc6,

    /// <summary>TOC 7.</summary>
    Toc7,

    /// <summary>TOC 8.</summary>
    Toc8,

    /// <summary>TOC 9.</summary>
    Toc9,
}

/// <summary>Word 2013+'s definitions of the built-in styles, as its Normal template has them.</summary>
internal static class BuiltInStyles
{
    /// <summary>The style id Word uses for each built-in style.</summary>
    public static string Id(BuiltInStyle style)
    {
        return style switch
        {
            >= BuiltInStyle.Toc1 and <= BuiltInStyle.Toc9 => "TOC" + (style - BuiltInStyle.Toc1 + 1),
            BuiltInStyle.HtmlCode => "HTMLCode",
            BuiltInStyle.HtmlPreformatted => "HTMLPreformatted",
            _ => style.ToString(),
        };
    }

    /// <summary>The name Word shows for each built-in style.</summary>
    public static string Name(BuiltInStyle style)
    {
        return style switch
        {
            BuiltInStyle.Normal => "Normal",
            BuiltInStyle.DefaultParagraphFont => "Default Paragraph Font",
            BuiltInStyle.TableNormal => "Normal Table",
            >= BuiltInStyle.Heading1 and <= BuiltInStyle.Heading9 => "heading " + (style - BuiltInStyle.Heading1 + 1),
            BuiltInStyle.ListParagraph => "List Paragraph",
            BuiltInStyle.Caption => "caption",
            BuiltInStyle.NoSpacing => "No Spacing",
            BuiltInStyle.Header => "header",
            BuiltInStyle.Footer => "footer",
            BuiltInStyle.FootnoteText => "footnote text",
            BuiltInStyle.FootnoteReference => "footnote reference",
            BuiltInStyle.EndnoteText => "endnote text",
            BuiltInStyle.EndnoteReference => "endnote reference",
            BuiltInStyle.CommentText => "annotation text",
            BuiltInStyle.CommentReference => "annotation reference",
            BuiltInStyle.HtmlCode => "HTML Code",
            BuiltInStyle.HtmlPreformatted => "HTML Preformatted",
            BuiltInStyle.TableGrid => "Table Grid",
            >= BuiltInStyle.Toc1 and <= BuiltInStyle.Toc9 => "toc " + (style - BuiltInStyle.Toc1 + 1),
            _ => style.ToString(),
        };
    }

    /// <summary>Gives a new document Word's defaults and its three default styles.</summary>
    public static void InitializeNewDocument(StyleCollection styles)
    {
        RunFormat run = styles.DefaultRunFormat;
        run.ThemeFontAscii = ThemeFont.MinorLatin;
        run.ThemeFontHighAnsi = ThemeFont.MinorLatin;
        run.ThemeFontEastAsia = ThemeFont.MinorEastAsia;
        run.ThemeFontComplexScript = ThemeFont.MinorComplexScript;
        run.Size = 11;
        run.SizeComplexScript = 11;
        run.Language = "en-US";
        run.LanguageEastAsia = "en-US";
        run.LanguageBidi = "ar-SA";
        ParagraphFormat paragraph = styles.DefaultParagraphFormat;
        paragraph.SpaceAfter = 8;
        paragraph.LineSpacing = 259 / 240.0;
        paragraph.LineSpacingRule = LineSpacingRule.Multiple;
        styles.SetDefault(GetOrAdd(styles, BuiltInStyle.Normal));
        styles.SetDefault(GetOrAdd(styles, BuiltInStyle.DefaultParagraphFont));
        styles.SetDefault(GetOrAdd(styles, BuiltInStyle.TableNormal));
    }

    /// <summary>The built-in style, added when missing (with the styles it is based on).</summary>
    public static Style GetOrAdd(StyleCollection styles, BuiltInStyle style)
    {
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }

        string id = Id(style);
        if (styles[id] is { } existing)
        {
            return existing;
        }

        string name = Name(style);
        switch (style)
        {
            case BuiltInStyle.Normal:
                {
                    ParagraphStyle s = styles.AddParagraphStyle(id, name);
                    s.IsPrimary = true;
                    return s;
                }

            case BuiltInStyle.DefaultParagraphFont:
                {
                    CharacterStyle s = styles.AddCharacterStyle(id, name);
                    s.Hidden = true;
                    s.Priority = 1;
                    return s;
                }

            case BuiltInStyle.TableNormal:
                {
                    TableStyle s = styles.AddTableStyle(id, name);
                    s.Hidden = true;
                    s.Priority = 99;
                    s.TableFormat.Indent = 0;
                    s.TableFormat.CellMarginTop = 0;
                    s.TableFormat.CellMarginBottom = 0;
                    s.TableFormat.CellMarginLeft = 5.4;
                    s.TableFormat.CellMarginRight = 5.4;
                    return s;
                }

            case >= BuiltInStyle.Heading1 and <= BuiltInStyle.Heading9:
                return Heading(styles, id, name, style - BuiltInStyle.Heading1);

            case BuiltInStyle.Title:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.IsPrimary = true;
                    s.ParagraphFormat.SpaceAfter = 0;
                    s.ParagraphFormat.LineSpacing = 1;
                    s.ParagraphFormat.ContextualSpacing = true;
                    MajorFonts(s.RunFormat);
                    s.RunFormat.Spacing = -0.5;
                    s.RunFormat.Kerning = 14;
                    s.RunFormat.Size = 28;
                    s.RunFormat.SizeComplexScript = 28;
                    return s;
                }

            case BuiltInStyle.Subtitle:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.IsPrimary = true;
                    s.RunFormat.Color = DocumentColor.FromTheme(ThemeColor.Dark1, tint: 1 - (0xA5 / 255.0));
                    s.RunFormat.Spacing = 0.75;
                    return s;
                }

            case BuiltInStyle.Quote:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.IsPrimary = true;
                    s.ParagraphFormat.SpaceBefore = 10;
                    s.ParagraphFormat.LeftIndent = 43.2;
                    s.ParagraphFormat.RightIndent = 43.2;
                    s.ParagraphFormat.Alignment = ParagraphAlignment.Center;
                    s.RunFormat.Italic = true;
                    s.RunFormat.Color = DocumentColor.FromTheme(ThemeColor.Dark1, tint: 1 - (0xBF / 255.0));
                    return s;
                }

            case BuiltInStyle.ListParagraph:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.IsPrimary = true;
                    s.ParagraphFormat.LeftIndent = 36;
                    s.ParagraphFormat.ContextualSpacing = true;
                    return s;
                }

            case BuiltInStyle.Caption:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.ParagraphFormat.SpaceAfter = 10;
                    s.ParagraphFormat.LineSpacing = 1;
                    s.RunFormat.Italic = true;
                    s.RunFormat.Color = DocumentColor.FromTheme(ThemeColor.Dark2);
                    s.RunFormat.Size = 9;
                    s.RunFormat.SizeComplexScript = 9;
                    return s;
                }

            case BuiltInStyle.NoSpacing:
                {
                    ParagraphStyle s = styles.AddParagraphStyle(id, name);
                    s.IsPrimary = true;
                    s.ParagraphFormat.SpaceAfter = 0;
                    s.ParagraphFormat.LineSpacing = 1;
                    return s;
                }

            case BuiltInStyle.Header or BuiltInStyle.Footer:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.ParagraphFormat.TabStops = [new TabStop(234, TabAlignment.Center), new TabStop(468, TabAlignment.Right)];
                    s.ParagraphFormat.SpaceAfter = 0;
                    s.ParagraphFormat.LineSpacing = 1;
                    return s;
                }

            case BuiltInStyle.FootnoteText or BuiltInStyle.EndnoteText or BuiltInStyle.CommentText:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.ParagraphFormat.SpaceAfter = 0;
                    s.ParagraphFormat.LineSpacing = 1;
                    s.RunFormat.Size = 10;
                    s.RunFormat.SizeComplexScript = 10;
                    return s;
                }

            case BuiltInStyle.FootnoteReference or BuiltInStyle.EndnoteReference:
                {
                    CharacterStyle s = Character(styles, id, name);
                    s.RunFormat.VerticalPosition = VerticalTextPosition.Superscript;
                    return s;
                }

            case BuiltInStyle.CommentReference:
                {
                    CharacterStyle s = Character(styles, id, name);
                    s.RunFormat.Size = 8;
                    s.RunFormat.SizeComplexScript = 8;
                    return s;
                }

            case BuiltInStyle.Hyperlink or BuiltInStyle.FollowedHyperlink:
                {
                    CharacterStyle s = Character(styles, id, name);
                    s.RunFormat.Color = DocumentColor.FromTheme(style == BuiltInStyle.Hyperlink ? ThemeColor.Hyperlink : ThemeColor.FollowedHyperlink);
                    s.RunFormat.Underline = UnderlineStyle.Single;
                    return s;
                }

            case BuiltInStyle.Strong:
                {
                    CharacterStyle s = Character(styles, id, name);
                    s.IsPrimary = true;
                    s.RunFormat.Bold = true;
                    s.RunFormat.BoldComplexScript = true;
                    return s;
                }

            case BuiltInStyle.Emphasis:
                {
                    CharacterStyle s = Character(styles, id, name);
                    s.IsPrimary = true;
                    s.RunFormat.Italic = true;
                    s.RunFormat.ItalicComplexScript = true;
                    return s;
                }

            case BuiltInStyle.HtmlCode:
                {
                    CharacterStyle s = Character(styles, id, name);
                    s.RunFormat.FontName = "Consolas";
                    s.RunFormat.Size = 10;
                    s.RunFormat.SizeComplexScript = 10;
                    return s;
                }

            case BuiltInStyle.HtmlPreformatted:
                {
                    ParagraphStyle s = Paragraph(styles, id, name);
                    s.ParagraphFormat.SpaceAfter = 0;
                    s.ParagraphFormat.LineSpacing = 1;
                    s.RunFormat.FontName = "Consolas";
                    s.RunFormat.Size = 10;
                    s.RunFormat.SizeComplexScript = 10;
                    return s;
                }

            case BuiltInStyle.TableGrid:
                {
                    TableStyle s = styles.AddTableStyle(id, name);
                    s.BasedOn = GetOrAdd(styles, BuiltInStyle.TableNormal);
                    s.Priority = 39;
                    s.ParagraphFormat.SpaceAfter = 0;
                    s.ParagraphFormat.LineSpacing = 1;
                    Border line = new(BorderStyle.Single, 0.5);
                    s.TableFormat.BorderTop = line;
                    s.TableFormat.BorderLeft = line;
                    s.TableFormat.BorderBottom = line;
                    s.TableFormat.BorderRight = line;
                    s.TableFormat.BorderInsideHorizontal = line;
                    s.TableFormat.BorderInsideVertical = line;
                    return s;
                }

            default:
                {
                    // TOC 1 to TOC 9: 5 pt after, each level indented 11 pt more.
                    ParagraphStyle s = Paragraph(styles, id, name);
                    int level = style - BuiltInStyle.Toc1;
                    s.ParagraphFormat.SpaceAfter = 5;
                    if (level > 0)
                    {
                        s.ParagraphFormat.LeftIndent = 11 * level;
                    }

                    return s;
                }
        }
    }

    private static ParagraphStyle Heading(StyleCollection styles, string id, string name, int level)
    {
        ParagraphStyle s = Paragraph(styles, id, name);
        s.IsPrimary = true;
        s.Priority = 9;
        s.ParagraphFormat.KeepWithNext = true;
        s.ParagraphFormat.KeepTogether = true;
        s.ParagraphFormat.SpaceBefore = level == 0 ? 12 : 2;
        s.ParagraphFormat.SpaceAfter = 0;
        s.ParagraphFormat.OutlineLevel = level;
        MajorFonts(s.RunFormat);
        double shadeBf = 1 - (0xBF / 255.0);
        double shade7F = 1 - (0x7F / 255.0);
        double tintD8 = 1 - (0xD8 / 255.0);
        (double? Size, bool Italic, DocumentColor Color) spec = level switch
        {
            0 => ((double?)16, false, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shadeBf)),
            1 => (13, false, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shadeBf)),
            2 => (12, false, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shade7F)),
            3 => (null, true, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shadeBf)),
            4 => (null, false, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shadeBf)),
            5 => (null, false, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shade7F)),
            6 => (null, true, DocumentColor.FromTheme(ThemeColor.Accent1, shade: shade7F)),
            7 => (10.5, false, DocumentColor.FromTheme(ThemeColor.Dark1, tint: tintD8)),
            _ => (10.5, true, DocumentColor.FromTheme(ThemeColor.Dark1, tint: tintD8)),
        };
        (double? size, bool italic, DocumentColor color) = spec;
        s.RunFormat.Color = color;
        if (size is { } points)
        {
            s.RunFormat.Size = points;
            s.RunFormat.SizeComplexScript = points;
        }

        if (italic)
        {
            s.RunFormat.Italic = true;
            s.RunFormat.ItalicComplexScript = true;
        }

        return s;
    }

    private static ParagraphStyle Paragraph(StyleCollection styles, string id, string name)
    {
        ParagraphStyle s = styles.AddParagraphStyle(id, name);
        ParagraphStyle normal = (ParagraphStyle)GetOrAdd(styles, BuiltInStyle.Normal);
        s.BasedOn = normal;
        s.Next = normal;
        return s;
    }

    private static CharacterStyle Character(StyleCollection styles, string id, string name)
    {
        CharacterStyle s = styles.AddCharacterStyle(id, name);
        s.BasedOn = GetOrAdd(styles, BuiltInStyle.DefaultParagraphFont);
        return s;
    }

    private static void MajorFonts(RunFormat run)
    {
        run.ThemeFontAscii = ThemeFont.MajorLatin;
        run.ThemeFontHighAnsi = ThemeFont.MajorLatin;
        run.ThemeFontEastAsia = ThemeFont.MajorEastAsia;
        run.ThemeFontComplexScript = ThemeFont.MajorComplexScript;
    }
}
