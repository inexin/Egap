namespace Egap.Pdf;

/// <summary>
/// A marked-content sequence or point (14.6): its tag and properties. Properties named in the resources
/// (<c>/OC /MC0 BDC</c>) are looked up there; <see cref="PropertiesName"/> keeps the name.
/// </summary>
/// <param name="Tag">The tag, such as <c>Span</c>, <c>P</c>, <c>Artifact</c> or <c>OC</c>.</param>
/// <param name="Properties">The property list: written inline or looked up in the resources; <see langword="null"/> for <c>BMC</c> and <c>MP</c>.</param>
/// <param name="PropertiesName">The property list's name in the resources' <c>/Properties</c>, when it was given by name.</param>
public sealed record PdfMarkedContent(PdfName Tag, PdfDictionary? Properties, PdfName? PropertiesName = null)
{
    /// <summary>The marked-content identifier (<c>/MCID</c>) that ties the content to the structure tree, or <see langword="null"/>.</summary>
    public int? Mcid => Properties?.GetInt("MCID") is long id and >= 0 and <= int.MaxValue ? (int)id : null;

    /// <summary>The replacement text (<c>/ActualText</c>, 14.9.4), or <see langword="null"/>.</summary>
    public string? ActualText => Properties?.GetText("ActualText");

    /// <summary>
    /// For optional content (<c>/OC</c>, 8.11.3.2), the optional content group or membership dictionary; else
    /// <see langword="null"/>.
    /// </summary>
    public PdfDictionary? OptionalContent => Tag.Value == "OC" ? Properties : null;
}

/// <summary>
/// One glyph shown by a text operator, as a <see cref="PdfContentWalker"/> reports it: the code shown, the font and the text
/// rendering matrix (9.4.4) that maps the glyph from text space, scaled by the font size, to the space the walk started in.
/// </summary>
/// <param name="Code">The character code: one byte for simple fonts, one or more for composite fonts.</param>
/// <param name="CodeLength">How many bytes the code took.</param>
/// <param name="Width">The glyph's horizontal advance in thousandths of a text space unit (its width, before spacing).</param>
/// <param name="Matrix">The text rendering matrix at the glyph's origin: <c>[size·scale 0 0 size 0 rise] × Tm × CTM</c>.</param>
/// <param name="IsWordSpace">Whether word spacing was added after the glyph: a single-byte code 32.</param>
public readonly record struct PdfGlyph(int Code, int CodeLength, double Width, PdfMatrix Matrix, bool IsWordSpace);
