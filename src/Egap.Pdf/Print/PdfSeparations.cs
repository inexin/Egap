using Egami;
using Egami.Colors;
using Egami.Pixels;

namespace Egap.Pdf;

/// <summary>
/// How <see cref="PdfPage.RenderSeparations(PdfRenderOptions?, PdfSeparationOptions?, CancellationToken)"/> makes plates: the
/// press profile behind the process inks, and which spot inks get plates of their own.
/// </summary>
public sealed record PdfSeparationOptions
{
    /// <summary>The default options: the output intent's CMYK profile (FOGRA39 without one), a plate for every spot ink.</summary>
    public static PdfSeparationOptions Default { get; } = new();

    /// <summary>
    /// Gets the CMYK profile of the press: it names the process inks, and RGB, Lab and calibrated colours convert into them
    /// through it. Null takes the document's CMYK output intent, or Egami's bundled FOGRA39 when there is none. Defaults to null.
    /// </summary>
    /// <exception cref="ArgumentException">The profile is not a CMYK profile.</exception>
    public ColorProfile? Profile
    {
        get;
        init => field = value is null or { Model: ColorModel.Cmyk } ? value : throw new ArgumentException("Plates need a CMYK press profile.", nameof(value));
    }

    /// <summary>
    /// Gets the spot inks left without a plate: content in them prints through their alternate colour into the process inks,
    /// as when a job converts those spots to process. Defaults to none.
    /// </summary>
    public IReadOnlyCollection<string> DisabledSpots { get; init; } = [];

    /// <summary>Gets whether every spot ink is converted to process, so only the process plates are made. Defaults to false.</summary>
    public bool SpotsToProcess { get; init; }
}

/// <summary>One ink's plate: its name, whether it is a spot ink, and its raster of how much ink prints where.</summary>
public sealed class PdfInkPlate
{
    internal PdfInkPlate(InkPlate plate, bool isSpot)
    {
        Name = plate.Name;
        IsSpot = isSpot;
        Raster = plate.AsRaster<Gray8>();
    }

    /// <summary>The ink's name: <c>Cyan</c>, <c>Magenta</c>, <c>Yellow</c>, <c>Black</c> or a spot ink such as <c>PANTONE 186 C</c>.</summary>
    public string Name { get; }

    /// <summary>Whether the ink is a spot ink rather than a process ink.</summary>
    public bool IsSpot { get; }

    /// <summary>The plate: 0 for no ink to 255 for full ink. It belongs to the <see cref="PdfSeparations"/> and goes with it.</summary>
    public Raster<Gray8> Raster { get; }

    /// <summary>The plate's name.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A page rendered as ink plates (Phase 8 design Q4, Q5): one gray raster per process and spot ink, drawn by a true
/// multi-ink renderer, so overprint, knockout and blend modes act on the inks as a press prints them. It measures total
/// area coverage (TAC), the sum of every plate in percent, and draws a heat map of it. Dispose it to free the plates.
/// </summary>
/// <example>
/// <code>
/// using PdfSeparations plates = pdf.Pages[0].RenderSeparations(new PdfRenderOptions { Dpi = 150 });
/// foreach (PdfInkPlate plate in plates.Plates)
/// {
///     plate.Raster.Save($"{plate.Name}.png");
/// }
///
/// Console.WriteLine($"max TAC {plates.MaxCoverage:0}%, {plates.ShareOver(300):P1} of the page over 300%");
/// </code>
/// </example>
public sealed class PdfSeparations : IDisposable
{
    private readonly InkRaster raster;

    internal PdfSeparations(InkRaster raster)
    {
        this.raster = raster;
        Plates = [.. raster.Plates.Select(p => new PdfInkPlate(p, p.Colorant?.Kind != ColorantKind.Process))];
    }

    /// <summary>The plates: the process inks first, then the spot inks in the order the page uses them.</summary>
    public IReadOnlyList<PdfInkPlate> Plates { get; }

    /// <summary>The plates as an Egami ink raster, to proof, measure or save as a separated TIFF.</summary>
    public InkRaster Inks => raster;

    /// <summary>The width of every plate in pixels.</summary>
    public int Width => raster.Width;

    /// <summary>The height of every plate in pixels.</summary>
    public int Height => raster.Height;

    /// <summary>The most total ink anywhere on the page, in percent: up to 400 for CMYK, more with spot inks.</summary>
    public double MaxCoverage => Math.Round(raster.MeasureCoverage().MaxTotal * 100, 1);

    /// <summary>The share of the page (0 to 1) whose total ink is over <paramref name="limit"/> percent.</summary>
    public double ShareOver(double limit) => raster.MeasureCoverage((float)(limit / 100)).ShareOver;

    /// <summary>Each plate's average ink over the page, in percent, by ink name.</summary>
    public IReadOnlyDictionary<string, double> CoveragePerPlate => raster.MeasureCoverage().PerPlate.ToDictionary(e => e.Key, e => Math.Round(e.Value * 100, 2));

    /// <summary>
    /// A heat map of total ink: white for paper, darkening gray up to <paramref name="limit"/> percent, and over it red,
    /// brightest at 100 points over.
    /// </summary>
    public Raster<Rgba8> CoverageMap(double limit = 300)
    {
        Raster<Rgba8> map = Raster.Create<Rgba8>(raster.Width, raster.Height);
        float[] total = new float[raster.Width];
        for (int y = 0; y < raster.Height; y++)
        {
            Array.Clear(total);
            foreach (InkPlate plate in raster.Plates)
            {
                Span<byte> row = plate.Row8(y);
                for (int x = 0; x < row.Length; x++)
                {
                    total[x] += row[x] / 2.55f;
                }
            }

            Span<Rgba8> output = map.Row(y);
            for (int x = 0; x < output.Length; x++)
            {
                float t = total[x];
                if (t > limit + 0.5)
                {
                    byte red = (byte)Math.Clamp(160 + ((t - limit) * 0.95), 160, 255);
                    output[x] = new Rgba8(red, 0, 0, 255);
                }
                else
                {
                    byte gray = (byte)Math.Clamp(255 - (t / Math.Max(limit, 1) * 200), 55, 255);
                    output[x] = new Rgba8(gray, gray, gray, 255);
                }
            }
        }

        return map;
    }

    /// <summary>Frees the plates.</summary>
    public void Dispose()
    {
        raster.Dispose();
    }
}
