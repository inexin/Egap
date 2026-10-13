namespace Egap.Pdf;

/// <summary>
/// A colour as content sets it (8.6): its colour space, as named in the content and resolved through the resources (a name
/// such as <c>DeviceRGB</c>, or an array such as <c>[/ICCBased 7 0 R]</c>), its components, and for a pattern the pattern's
/// resource name.
/// </summary>
/// <param name="Space">The colour space: a name for the device spaces and <c>Pattern</c>, else the space's array.</param>
/// <param name="Components">The components, as many as the space takes; for a pattern, those of an uncoloured pattern's underlying space.</param>
/// <param name="Pattern">For the <c>Pattern</c> space, the pattern's name in the resources.</param>
public sealed record PdfColor(PdfObject Space, IReadOnlyList<double> Components, PdfName? Pattern = null)
{
    /// <summary>Black in <c>DeviceGray</c>, the initial colour (8.4.1).</summary>
    public static PdfColor Black { get; } = new(PdfName.Get("DeviceGray"), [0.0]);

    /// <summary>The colour space's family: <c>DeviceRGB</c>, <c>ICCBased</c>, <c>Pattern</c> and so on.</summary>
    public string Family => Space switch
    {
        PdfName name => name.Value,
        PdfArray { Count: > 0 } array when array[0] is PdfName name => name.Value,
        _ => "",
    };

    /// <summary>The colour as its components separated by spaces, with the space's family.</summary>
    public override string ToString()
    {
        return $"{Family} [{string.Join(' ', Components.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]{(Pattern is null ? "" : " /" + Pattern.Value)}";
    }
}

/// <summary>A clip the content set (8.5.4): a path, the transformation it was set under, and its fill rule.</summary>
/// <param name="Path">The clipping path, in the user space it was built in.</param>
/// <param name="Matrix">The current transformation matrix when the clip was set.</param>
/// <param name="EvenOdd">Whether the even-odd rule decides what is inside (<c>W*</c>) rather than the nonzero rule (<c>W</c>).</param>
public sealed record PdfClipPath(PdfPath Path, PdfMatrix Matrix, bool EvenOdd);

/// <summary>
/// The graphics state (8.4) as a <see cref="PdfContentWalker"/> tracks it: what <c>q</c> saves and <c>Q</c> restores. The
/// text matrices are not in it; they belong to the text object (9.4.2) and are on the walker.
/// </summary>
public sealed class PdfGraphicsState
{
    private static readonly PdfName Normal = PdfName.Get("Normal");
    private static readonly PdfName RelativeColorimetric = PdfName.Get("RelativeColorimetric");

    internal PdfGraphicsState(PdfMatrix matrix)
    {
        Matrix = matrix;
    }

    /// <summary>The current transformation matrix, from user space to the space the walk started in.</summary>
    public PdfMatrix Matrix { get; internal set; }

    /// <summary>The clips set so far, outermost first; what is drawn is clipped to all of them.</summary>
    public IReadOnlyList<PdfClipPath> Clips { get; internal set; } = [];

    /// <summary>The line width (<c>w</c>, <c>/LW</c>), in user space units; 0 is the thinnest line the device can draw.</summary>
    public double LineWidth { get; internal set; } = 1;

    /// <summary>The line cap (<c>J</c>, <c>/LC</c>): 0 butt, 1 round, 2 projecting square.</summary>
    public int LineCap { get; internal set; }

    /// <summary>The line join (<c>j</c>, <c>/LJ</c>): 0 miter, 1 round, 2 bevel.</summary>
    public int LineJoin { get; internal set; }

    /// <summary>The miter limit (<c>M</c>, <c>/ML</c>).</summary>
    public double MiterLimit { get; internal set; } = 10;

    /// <summary>The dash lengths (<c>d</c>, <c>/D</c>); empty for solid lines.</summary>
    public IReadOnlyList<double> DashArray { get; internal set; } = [];

    /// <summary>Where in the dash pattern lines start.</summary>
    public double DashPhase { get; internal set; }

    /// <summary>The rendering intent (<c>ri</c>, <c>/RI</c>).</summary>
    public PdfName RenderingIntent { get; internal set; } = RelativeColorimetric;

    /// <summary>The flatness tolerance (<c>i</c>, <c>/FL</c>).</summary>
    public double Flatness { get; internal set; } = 1;

    /// <summary>Whether strokes are adjusted to the device's pixels (<c>/SA</c>).</summary>
    public bool StrokeAdjustment { get; internal set; }

    /// <summary>The stroking colour (<c>CS</c>, <c>SC</c>, <c>SCN</c>, <c>G</c>, <c>RG</c>, <c>K</c>).</summary>
    public PdfColor StrokeColor { get; internal set; } = PdfColor.Black;

    /// <summary>The non-stroking colour (<c>cs</c>, <c>sc</c>, <c>scn</c>, <c>g</c>, <c>rg</c>, <c>k</c>).</summary>
    public PdfColor FillColor { get; internal set; } = PdfColor.Black;

    /// <summary>The stroking constant alpha (<c>/CA</c>).</summary>
    public double StrokeAlpha { get; internal set; } = 1;

    /// <summary>The non-stroking constant alpha (<c>/ca</c>).</summary>
    public double FillAlpha { get; internal set; } = 1;

    /// <summary>The blend mode (<c>/BM</c>); from an array, the first mode the spec defines.</summary>
    public PdfName BlendMode { get; internal set; } = Normal;

    /// <summary>The soft mask (<c>/SMask</c>): its dictionary, or <see langword="null"/> for none.</summary>
    public PdfDictionary? SoftMask { get; internal set; }

    /// <summary>The matrix in effect when the soft mask was set, which its group is drawn under (11.6.5.2).</summary>
    public PdfMatrix SoftMaskMatrix { get; internal set; } = PdfMatrix.Identity;

    /// <summary>Whether alpha is shape (<c>/AIS</c>).</summary>
    public bool AlphaIsShape { get; internal set; }

    /// <summary>The text knockout flag (<c>/TK</c>).</summary>
    public bool TextKnockout { get; internal set; } = true;

    /// <summary>Overprint for stroking (<c>/OP</c>).</summary>
    public bool StrokeOverprint { get; internal set; }

    /// <summary>Overprint for everything else (<c>/op</c>, which defaults to <c>/OP</c>).</summary>
    public bool FillOverprint { get; internal set; }

    /// <summary>The overprint mode (<c>/OPM</c>).</summary>
    public int OverprintMode { get; internal set; }

    /// <summary>Character spacing (<c>Tc</c>), in unscaled text space units.</summary>
    public double CharacterSpacing { get; internal set; }

    /// <summary>Word spacing (<c>Tw</c>), added for each single-byte code 32.</summary>
    public double WordSpacing { get; internal set; }

    /// <summary>Horizontal scaling (<c>Tz</c>), in percent.</summary>
    public double HorizontalScaling { get; internal set; } = 100;

    /// <summary>Leading (<c>TL</c>), what <c>T*</c> moves down.</summary>
    public double Leading { get; internal set; }

    /// <summary>The font's name in the resources (<c>Tf</c>, or <c>/Font</c> in a graphics state dictionary), or <see langword="null"/> before one is set.</summary>
    public PdfName? FontName { get; internal set; }

    /// <summary>The font dictionary, or <see langword="null"/> when none is set or the name is not in the resources.</summary>
    public PdfDictionary? Font { get; internal set; }

    /// <summary>The font size (<c>Tf</c>).</summary>
    public double FontSize { get; internal set; }

    /// <summary>The text rendering mode (<c>Tr</c>): 0 fill, 1 stroke, 2 both, 3 invisible, 4 to 7 the same and clip.</summary>
    public int TextRenderMode { get; internal set; }

    /// <summary>Text rise (<c>Ts</c>).</summary>
    public double TextRise { get; internal set; }

    internal PdfGraphicsState Clone()
    {
        return (PdfGraphicsState)MemberwiseClone();
    }

    internal void AddClip(PdfClipPath clip)
    {
        Clips = [.. Clips, clip];
    }
}
