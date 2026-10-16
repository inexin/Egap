namespace Egap.Documents;

/// <summary>One text column of a section with unequal columns: its width and the space after it, in points.</summary>
/// <param name="Width">The column's width in points.</param>
/// <param name="SpaceAfter">The space between this column and the next, in points.</param>
public sealed record TextColumn(double Width, double SpaceAfter = 0);

public sealed partial class RunFormat
{
    /// <summary>
    /// The font for every script: reads <see cref="FontAscii"/>; setting it sets all four script fonts and clears their theme
    /// fonts, which would otherwise win.
    /// </summary>
    public string? FontName
    {
        get => FontAscii;
        set
        {
            FontAscii = value;
            FontHighAnsi = value;
            FontEastAsia = value;
            FontComplexScript = value;
            ThemeFontAscii = null;
            ThemeFontHighAnsi = null;
            ThemeFontEastAsia = null;
            ThemeFontComplexScript = null;
        }
    }
}
