using Egami.Colors;

namespace Egap.Pdf;

/// <summary>A PDF/X version a file is checked against or converted to (ISO 15930; Phase 8 design Q9).</summary>
public enum PdfXConformance
{
    /// <summary>No PDF/X claim.</summary>
    None,

    /// <summary>PDF/X-1a:2003 (ISO 15930-4, PDF 1.4): CMYK, gray and spot colours only, no transparency, everything embedded.</summary>
    PdfX1a,

    /// <summary>PDF/X-3:2003 (ISO 15930-6, PDF 1.4): PDF/X-1a with colour-managed (ICC, calibrated, Lab) colours allowed.</summary>
    PdfX3,

    /// <summary>PDF/X-4 (ISO 15930-7, PDF 1.6): colour-managed colours, live transparency and optional content.</summary>
    PdfX4,

    /// <summary>PDF/X-4p: PDF/X-4 whose output intent profile is referenced, not embedded. Checked only.</summary>
    PdfX4p,

    /// <summary>PDF/X-5g (ISO 15930-8): PDF/X-4 that may reference external graphical content. Checked only.</summary>
    PdfX5g,

    /// <summary>PDF/X-5pg: PDF/X-5g with a referenced output intent profile. Checked only.</summary>
    PdfX5pg,

    /// <summary>PDF/X-5n: PDF/X-4 printed with an n-colorant characterization (more than CMYK).</summary>
    PdfX5n,

    /// <summary>PDF/X-6 (ISO 15930-9, PDF 2.0): PDF/X-4's rules on PDF 2.0.</summary>
    PdfX6,

    /// <summary>PDF/X-6p: PDF/X-6 with a referenced output intent profile. Checked only.</summary>
    PdfX6p,

    /// <summary>PDF/X-6n: PDF/X-6 printed with an n-colorant characterization.</summary>
    PdfX6n,
}

/// <summary>
/// A registered print characterization (design Q10): the identifier PDF/X output intents name, such as <c>FOGRA39</c>, how
/// it is described, and the ICC profile that is embedded with it. <see cref="Fogra39"/> carries Egami's bundled profile;
/// the others need one from the caller (<see cref="WithProfile"/>), since their profiles may not be redistributed freely.
/// </summary>
/// <param name="Identifier">The output condition identifier, as registered at the ICC (<c>FOGRA39</c>, <c>CGATS21_CRPC6</c>).</param>
/// <param name="Description">What the condition is, written as the intent's /OutputCondition and /Info.</param>
/// <param name="Profile">The characterization's ICC profile, or null when the caller must give it.</param>
public sealed record PdfOutputCondition(string Identifier, string Description, ColorProfile? Profile = null)
{
    /// <summary>The registry the identifiers are kept in.</summary>
    public const string Registry = "http://www.color.org";

    /// <summary>Offset on coated paper, ISO 12647-2:2004 (FOGRA39), with Egami's bundled profile.</summary>
    public static PdfOutputCondition Fogra39 { get; } = new("FOGRA39", "Coated FOGRA39 (ISO 12647-2:2004)", ColorProfile.Fogra39);

    /// <summary>Offset on premium coated paper, ISO 12647-2:2013 (FOGRA51); the profile comes from the caller.</summary>
    public static PdfOutputCondition Fogra51 { get; } = new("FOGRA51", "Offset printing, premium coated paper (ISO 12647-2:2013)");

    /// <summary>Offset on wood-free uncoated paper, ISO 12647-2:2013 (FOGRA52); the profile comes from the caller.</summary>
    public static PdfOutputCondition Fogra52 { get; } = new("FOGRA52", "Offset printing, wood-free uncoated paper (ISO 12647-2:2013)");

    /// <summary>GRACoL 2013 on grade 1 coated paper (CGATS.21-2, CRPC6); the profile comes from the caller.</summary>
    public static PdfOutputCondition Gracol2013 { get; } = new("CGATS21_CRPC6", "GRACoL 2013, grade 1 coated (CGATS.21-2 CRPC6)");

    /// <summary>SWOP 2013 on grade 3 coated paper (CGATS.21-2, CRPC5); the profile comes from the caller.</summary>
    public static PdfOutputCondition Swop2013 { get; } = new("CGATS21_CRPC5", "SWOP 2013, grade 3 coated (CGATS.21-2 CRPC5)");

    /// <summary>Coldset newspaper, ISO 12647-3 (IFRA26); the profile comes from the caller.</summary>
    public static PdfOutputCondition Newspaper { get; } = new("IFRA26", "Coldset offset newspaper (ISO 12647-3:2013)");

    /// <summary>The same condition with this ICC profile embedded.</summary>
    public PdfOutputCondition WithProfile(ColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return this with { Profile = profile };
    }

    /// <summary>The conditions Egap knows by identifier.</summary>
    public static IReadOnlyList<PdfOutputCondition> Known { get; } = [Fogra39, Fogra51, Fogra52, Gracol2013, Swop2013, Newspaper];

    /// <summary>A known condition by its identifier, ignoring case; null for an unknown one.</summary>
    public static PdfOutputCondition? Find(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return Known.FirstOrDefault(c => string.Equals(c.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Options for <see cref="PdfXConverter.Convert(PdfDocument, PdfXConformance, PdfXConvertOptions?, CancellationToken)"/>.</summary>
public sealed record PdfXConvertOptions
{
    /// <summary>The default options: FOGRA39, no bleed added, fonts from this machine, no substitution.</summary>
    public static PdfXConvertOptions Default { get; } = new();

    /// <summary>
    /// Gets the output condition the file is printed for, embedded as its output intent. Null keeps an output intent the file
    /// has, else uses <see cref="PdfOutputCondition.Fogra39"/>. Defaults to null.
    /// </summary>
    public PdfOutputCondition? OutputCondition { get; init; }

    /// <summary>
    /// Gets the bleed, in millimetres, the bleed box reaches past the trim box when conversion sets the boxes, within the
    /// media box. A page without a trim box gets its crop box as the trim box. Defaults to 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public double Bleed
    {
        get;
        init => field = value >= 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bleed must be zero or more.");
    }

    /// <summary>Gets how colours are converted into the output condition's profile. Defaults to <see cref="PdfColorConversionOptions.Default"/>.</summary>
    public PdfColorConversionOptions ColorConversion { get; init; } = PdfColorConversionOptions.Default;

    /// <summary>Gets whether fonts that are not embedded may be embedded from this machine's fonts. Defaults to true.</summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Gets whether a font that is not embedded and not found may be replaced by a bundled substitute with its widths kept;
    /// each substitution is reported. Defaults to false.
    /// </summary>
    public bool AllowFontSubstitution { get; init; }

    /// <summary>Gets the value written for /Trapped when the file does not say: false, the usual answer for files not trapped. Defaults to false.</summary>
    public bool Trapped { get; init; }
}
