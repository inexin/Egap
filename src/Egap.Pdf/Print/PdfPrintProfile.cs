namespace Egap.Pdf;

/// <summary>Kinds of colour space, as a print profile allows them and a preflight report lists them.</summary>
[Flags]
public enum PdfColorSpaceKinds
{
    /// <summary>No colour space.</summary>
    None = 0,

    /// <summary>DeviceGray.</summary>
    DeviceGray = 1,

    /// <summary>DeviceRGB.</summary>
    DeviceRgb = 2,

    /// <summary>DeviceCMYK.</summary>
    DeviceCmyk = 4,

    /// <summary>Gray tied to a profile: CalGray or an ICCBased space with one component.</summary>
    CalibratedGray = 8,

    /// <summary>RGB tied to a profile: CalRGB or an ICCBased space with three components.</summary>
    CalibratedRgb = 16,

    /// <summary>CMYK tied to a profile: an ICCBased space with four components.</summary>
    CalibratedCmyk = 32,

    /// <summary>CIE L*a*b*.</summary>
    Lab = 64,

    /// <summary>Spot colours: Separation (other than All and None) and DeviceN.</summary>
    Spot = 128,

    /// <summary>Any gray: <see cref="DeviceGray"/> or <see cref="CalibratedGray"/>.</summary>
    Gray = DeviceGray | CalibratedGray,

    /// <summary>Any RGB: <see cref="DeviceRgb"/> or <see cref="CalibratedRgb"/>.</summary>
    Rgb = DeviceRgb | CalibratedRgb,

    /// <summary>Any CMYK: <see cref="DeviceCmyk"/> or <see cref="CalibratedCmyk"/>.</summary>
    Cmyk = DeviceCmyk | CalibratedCmyk,

    /// <summary>Every kind.</summary>
    All = Gray | Rgb | Cmyk | Lab | Spot,
}

/// <summary>
/// What a print job accepts, as <see cref="PdfPreflight.Run(PdfDocument, PdfPrintProfile?, CancellationToken)"/> checks it: the most ink, the
/// least image resolution, the colour spaces allowed, the bleed past the trim box, the thinnest rule and whether fonts must
/// be embedded. A preset fills in the values for a common job; each can be changed with <c>with</c>. A value left null is
/// not checked.
/// </summary>
/// <example>
/// <code>
/// PdfPrintProfile profile = PdfPrintProfile.WebOffset with { MinImageDpi = 250 };
/// PdfPreflightReport report = PdfPreflight.Run(pdf, profile);
/// </code>
/// </example>
public sealed record PdfPrintProfile
{
    /// <summary>PDF/X-4 basics: fonts embedded, no DeviceRGB (it needs an RGB output intent), images at 150 dpi or more.</summary>
    public static PdfPrintProfile PdfX4 { get; } = new()
    {
        Name = "PDF/X-4",
        MinImageDpi = 150,
        AllowedColorSpaces = PdfColorSpaceKinds.All & ~PdfColorSpaceKinds.DeviceRgb,
    };

    /// <summary>Sheetfed offset on coated paper: 330 % ink, 250 dpi, CMYK, gray and spots, 3 mm bleed, rules of 0.25 pt or more.</summary>
    public static PdfPrintProfile SheetfedCoated { get; } = new()
    {
        Name = "Sheetfed coated",
        MaxInkCoverage = 330,
        MinImageDpi = 250,
        AllowedColorSpaces = PdfColorSpaceKinds.Cmyk | PdfColorSpaceKinds.Gray | PdfColorSpaceKinds.Spot,
        RequiredBleed = 3,
        MinLineWidth = 0.25,
    };

    /// <summary>Heatset web offset: 300 % ink, 200 dpi, CMYK, gray and spots, 3 mm bleed, rules of 0.25 pt or more.</summary>
    public static PdfPrintProfile WebOffset { get; } = new()
    {
        Name = "Web offset",
        MaxInkCoverage = 300,
        MinImageDpi = 200,
        AllowedColorSpaces = PdfColorSpaceKinds.Cmyk | PdfColorSpaceKinds.Gray | PdfColorSpaceKinds.Spot,
        RequiredBleed = 3,
        MinLineWidth = 0.25,
    };

    /// <summary>Coldset newspaper: 240 % ink, 150 dpi, CMYK and gray only, no bleed, rules of 0.3 pt or more.</summary>
    public static PdfPrintProfile Newspaper { get; } = new()
    {
        Name = "Newspaper",
        MaxInkCoverage = 240,
        MinImageDpi = 150,
        AllowedColorSpaces = PdfColorSpaceKinds.Cmyk | PdfColorSpaceKinds.Gray,
        MinLineWidth = 0.3,
    };

    /// <summary>Gets the profile's name, shown in reports. Defaults to "Custom".</summary>
    public string Name { get; init; } = "Custom";

    /// <summary>
    /// Gets the most total ink allowed anywhere on a page, in percent (the sum of every plate, so up to 400 for CMYK), or null
    /// for no limit. Defaults to null.
    /// </summary>
    public double? MaxInkCoverage { get; init; }

    /// <summary>
    /// Gets the least effective resolution an image may have where it is placed largest, in pixels per inch, or null for no
    /// limit. Images of one bit per pixel (line art) are held to twice this. Defaults to null.
    /// </summary>
    public double? MinImageDpi { get; init; }

    /// <summary>Gets the colour spaces content may paint with. Defaults to <see cref="PdfColorSpaceKinds.All"/>.</summary>
    public PdfColorSpaceKinds AllowedColorSpaces { get; init; } = PdfColorSpaceKinds.All;

    /// <summary>
    /// Gets the bleed each page needs past its trim box on every side, in millimetres, or null when bleed is not checked.
    /// Defaults to null.
    /// </summary>
    public double? RequiredBleed { get; init; }

    /// <summary>
    /// Gets the thinnest stroke allowed, in points at the page's scale (a width of 0, the thinnest line a device can draw,
    /// is always thinner), or null for no limit. Defaults to null.
    /// </summary>
    public double? MinLineWidth { get; init; }

    /// <summary>
    /// Gets the resolution, in pixels per inch, at which pages are separated to measure their ink when
    /// <see cref="MaxInkCoverage"/> is set. Coverage is an average over each pixel, so a lower resolution smooths small
    /// peaks. Defaults to 36.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public double InkCoverageDpi
    {
        get;
        init => field = value > 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The resolution must be a positive number.");
    } = 36;

    /// <summary>Gets whether every font that draws visible text must be embedded. Defaults to true.</summary>
    public bool RequireEmbeddedFonts { get; init; } = true;

    /// <summary>Gets whether transparency (groups, soft masks, alpha, blend modes) is allowed. Defaults to true.</summary>
    public bool AllowTransparency { get; init; } = true;
}
