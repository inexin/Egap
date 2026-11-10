using System.Xml.Linq;

using Egami.Pixels;

namespace Egap.Documents;

/// <summary>
/// A document theme: twelve colours and the major (headings) and minor (body) fonts, which runs and styles name instead of
/// fixed values, so changing the theme restyles the document. A new document has Word's Office theme (2013 to 2022).
/// </summary>
public sealed class Theme
{
    private readonly Rgba8[] colors = new Rgba8[12];
    private string name = "Office Theme";
    private string majorLatin = "Calibri Light";
    private string minorLatin = "Calibri";
    private string majorEastAsia = "";
    private string minorEastAsia = "";
    private string majorComplexScript = "";
    private string minorComplexScript = "";

    internal Theme(Document document)
    {
        Document = document;
        Colors = new ThemeColors(this);
        string[] office = ["000000", "FFFFFF", "44546A", "E7E6E6", "4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47", "0563C1", "954F72"];
        for (int i = 0; i < office.Length; i++)
        {
            colors[i] = Rgba8.FromHex(office[i]);
        }
    }

    /// <summary>The document this theme belongs to.</summary>
    public Document Document { get; }

    /// <summary>The theme's name.</summary>
    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    /// <summary>The theme's colours, by <see cref="ThemeColor"/>.</summary>
    public ThemeColors Colors { get; }

    /// <summary>The major (headings) font for Latin text.</summary>
    public string MajorLatinFont
    {
        get => majorLatin;
        set => Set(ref majorLatin, value);
    }

    /// <summary>The minor (body) font for Latin text.</summary>
    public string MinorLatinFont
    {
        get => minorLatin;
        set => Set(ref minorLatin, value);
    }

    /// <summary>The major font for East Asian text; empty for none (the run's own font is used).</summary>
    public string MajorEastAsiaFont
    {
        get => majorEastAsia;
        set => Set(ref majorEastAsia, value);
    }

    /// <summary>The minor font for East Asian text; empty for none.</summary>
    public string MinorEastAsiaFont
    {
        get => minorEastAsia;
        set => Set(ref minorEastAsia, value);
    }

    /// <summary>The major font for complex scripts; empty for none.</summary>
    public string MajorComplexScriptFont
    {
        get => majorComplexScript;
        set => Set(ref majorComplexScript, value);
    }

    /// <summary>The minor font for complex scripts; empty for none.</summary>
    public string MinorComplexScriptFont
    {
        get => minorComplexScript;
        set => Set(ref minorComplexScript, value);
    }

    /// <summary>The font a <see cref="ThemeFont"/> names, or null when the theme leaves it empty.</summary>
    public string? GetFont(ThemeFont font)
    {
        string value = font switch
        {
            ThemeFont.MajorLatin => majorLatin,
            ThemeFont.MajorEastAsia => majorEastAsia,
            ThemeFont.MajorComplexScript => majorComplexScript,
            ThemeFont.MinorLatin => minorLatin,
            ThemeFont.MinorEastAsia => minorEastAsia,
            ThemeFont.MinorComplexScript => minorComplexScript,
            _ => throw new ArgumentOutOfRangeException(nameof(font)),
        };
        return value.Length == 0 ? null : value;
    }

    /// <summary>Takes the colours and fonts of a DrawingML theme part (<c>a:theme</c>), as a Word or Excel file has it.</summary>
    internal void Read(XElement root)
    {
        if ((string?)root.Attribute("name") is { } name)
        {
            Name = name;
        }

        XElement? elements = root.Element(Docx.Ns.A + "themeElements");
        if (elements?.Element(Docx.Ns.A + "clrScheme") is { } scheme)
        {
            (string Name, ThemeColor Color)[] slots =
            [
                ("dk1", ThemeColor.Dark1), ("lt1", ThemeColor.Light1), ("dk2", ThemeColor.Dark2), ("lt2", ThemeColor.Light2),
                ("accent1", ThemeColor.Accent1), ("accent2", ThemeColor.Accent2), ("accent3", ThemeColor.Accent3),
                ("accent4", ThemeColor.Accent4), ("accent5", ThemeColor.Accent5), ("accent6", ThemeColor.Accent6),
                ("hlink", ThemeColor.Hyperlink), ("folHlink", ThemeColor.FollowedHyperlink),
            ];
            foreach ((string slot, ThemeColor color) in slots)
            {
                XElement? value = scheme.Element(Docx.Ns.A + slot)?.Elements().FirstOrDefault();
                string? hex = value?.Name.LocalName switch
                {
                    "srgbClr" => (string?)value.Attribute("val"),
                    "sysClr" => (string?)value.Attribute("lastClr"),
                    _ => null,
                };
                if (Docx.DocxProperties.ParseHex(hex) is Rgba8 rgb)
                {
                    SetColor(color, rgb);
                }
            }
        }

        // The fill, line and effect styles shapes refer to by number (lnRef, fillRef, effectRef); kept as read, for the shape engine.
        FormatScheme = elements?.Element(Docx.Ns.A + "fmtScheme") ?? FormatScheme;

        if (elements?.Element(Docx.Ns.A + "fontScheme") is { } fonts)
        {
            XElement? major = fonts.Element(Docx.Ns.A + "majorFont");
            XElement? minor = fonts.Element(Docx.Ns.A + "minorFont");
            MajorLatinFont = Typeface(major, "latin") ?? MajorLatinFont;
            MinorLatinFont = Typeface(minor, "latin") ?? MinorLatinFont;
            MajorEastAsiaFont = Typeface(major, "ea") ?? MajorEastAsiaFont;
            MinorEastAsiaFont = Typeface(minor, "ea") ?? MinorEastAsiaFont;
            MajorComplexScriptFont = Typeface(major, "cs") ?? MajorComplexScriptFont;
            MinorComplexScriptFont = Typeface(minor, "cs") ?? MinorComplexScriptFont;
        }

        static string? Typeface(XElement? font, string script)
        {
            return (string?)font?.Element(Docx.Ns.A + script)?.Attribute("typeface");
        }
    }

    /// <summary>The theme's <c>a:fmtScheme</c> as read, or null when the document has none.</summary>
    internal XElement? FormatScheme { get; private set; }

    internal Rgba8 GetColor(ThemeColor color)
    {
        return colors[(int)color];
    }

    internal void SetColor(ThemeColor color, Rgba8 value)
    {
        if (!colors[(int)color].Equals(value))
        {
            colors[(int)color] = value;
            Document.NotifyFormattingChanged("Theme.Colors." + color);
        }
    }

    internal void CopyFrom(Theme source)
    {
        Array.Copy(source.colors, colors, colors.Length);
        name = source.name;
        majorLatin = source.majorLatin;
        minorLatin = source.minorLatin;
        majorEastAsia = source.majorEastAsia;
        minorEastAsia = source.minorEastAsia;
        majorComplexScript = source.majorComplexScript;
        minorComplexScript = source.minorComplexScript;
    }

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        if (field != value)
        {
            field = value;
            Document.NotifyFormattingChanged("Theme." + property);
        }
    }
}

/// <summary>A theme's twelve colours.</summary>
public sealed class ThemeColors
{
    private readonly Theme theme;

    internal ThemeColors(Theme theme)
    {
        this.theme = theme;
    }

    /// <summary>The colour for <paramref name="color"/>.</summary>
    public Rgba8 this[ThemeColor color]
    {
        get => Enum.IsDefined(color) ? theme.GetColor(color) : throw new ArgumentOutOfRangeException(nameof(color));
        set
        {
            if (!Enum.IsDefined(color))
            {
                throw new ArgumentOutOfRangeException(nameof(color));
            }

            theme.SetColor(color, value);
        }
    }
}
