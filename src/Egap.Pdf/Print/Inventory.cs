using System.Globalization;
using System.Runtime.CompilerServices;

using Egami.Colors;

namespace Egap.Pdf.Print;

/// <summary>
/// The colour inventory behind preflight (Phase 8 design 8.1, Build Plan tasks 3 and 4): walks each page's content, its
/// forms, tiling patterns and the appearances of annotations that print, and counts what it paints with. Colour spaces come
/// with their ICC profile descriptions and spot names, a device space standing for the resources' Default space where there
/// is one; images with their effective resolution where placed largest; fonts with whether they are embedded; transparency,
/// overprint and strokes thinner than a limit. Soft masks are noted as transparency, not as colours painted.
/// </summary>
internal sealed class Inventory
{
    private const int MaxDepth = 16;

    private readonly PdfDocument document;
    private readonly double hairline;
    private readonly ConditionalWeakTable<PdfStream, string?> profileNames = [];
    private readonly HashSet<PdfDictionary> fontsSeen = new(ReferenceEqualityComparer.Instance);

    /// <summary>Each spot ink's first Separation or DeviceN space, by name, in the order found.</summary>
    private readonly Dictionary<string, PdfArray> spotSpaces = new(StringComparer.Ordinal);
    private readonly List<string> spotOrder = [];

    /// <summary>The page's items by key, in the order first found.</summary>
    private readonly Dictionary<string, Builder> page = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private int pageNumber;

    public Inventory(PdfDocument document, double hairline)
    {
        this.document = document;
        this.hairline = hairline;
    }

    /// <summary>Every item: output intents, then each page's.</summary>
    public List<PdfPreflightItem> Run(CancellationToken cancellationToken)
    {
        List<PdfPreflightItem> items = [.. OutputIntents()];
        for (int i = 0; i < document.Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(Page(document.Pages[i], i + 1));
        }

        return items;
    }

    /// <summary>
    /// The spot inks a page paints with, in the order found, each with the first Separation or DeviceN space that names it:
    /// the plates a separation render needs beside the process inks.
    /// </summary>
    internal static List<(string Name, PdfArray Space)> Spots(PdfPage page)
    {
        Inventory inventory = new(page.Document, DefaultHairlineForSpots);
        inventory.Page(page, 1);
        return [.. inventory.spotOrder.Select(n => (n, inventory.spotSpaces[n]))];
    }

    private const double DefaultHairlineForSpots = 0;

    /// <summary>The document's output intents, each with its condition and profile.</summary>
    private IEnumerable<PdfPreflightItem> OutputIntents()
    {
        foreach (PdfObject value in document.Catalog.Get<PdfArray>("OutputIntents") ?? [])
        {
            if (value.Resolve() is not PdfDictionary intent)
            {
                continue;
            }

            string subtype = intent.GetName("S")?.Value ?? "?";
            string? condition = intent.Get<PdfString>("OutputConditionIdentifier")?.ToText();
            string? info = intent.Get<PdfString>("Info")?.ToText() ?? intent.Get<PdfString>("OutputCondition")?.ToText();
            PdfStream? profile = intent.Get<PdfStream>("DestOutputProfile");
            string? profileName = profile is null ? null : ProfileName(profile);
            PdfColorSpaceKinds kind = profile is null ? PdfColorSpaceKinds.None : IccKind(profile);
            string text = $"{subtype} output intent {condition ?? "(no condition)"}{(info is null || info == condition ? "" : $" ({info})")}"
                + (profile is null ? ", no profile" : $", {KindName(kind)} profile \"{profileName ?? "unnamed"}\"");
            yield return new PdfPreflightItem(PdfPreflightItemKind.OutputIntent, null, text)
            {
                Name = condition,
                ColorSpace = kind,
                ColorSpaceName = profile is null ? null : "ICCBased " + KindName(kind),
                ProfileName = profileName,
            };
        }
    }

    private List<PdfPreflightItem> Page(PdfPage pdfPage, int number)
    {
        page.Clear();
        order.Clear();
        fontsSeen.Clear();
        pageNumber = number;
        Boxes(pdfPage);
        if (pdfPage.Dictionary.Get<PdfDictionary>("Group") is PdfDictionary group && group.GetName("S")?.Value == "Transparency")
        {
            Add(PdfPreflightItemKind.Transparency, "group:page", "the page is a transparency group");
        }

        Walker walker = new(this);
        try
        {
            walker.Walk(pdfPage.GetContent(), pdfPage.Resources, PdfMatrix.Identity, document.Options);
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            // Unreadable content paints nothing a printer sees either.
        }

        foreach (PdfAnnotation annotation in pdfPage.Annotations)
        {
            if ((annotation.Flags & PdfAnnotationFlags.Print) == 0 || (annotation.Flags & PdfAnnotationFlags.Hidden) != 0
                || annotation.GetAppearance() is not PdfStream appearance)
            {
                continue;
            }

            try
            {
                new Walker(this).Walk(PdfContent.Read(appearance), appearance.Dictionary.Get<PdfDictionary>("Resources") ?? pdfPage.Resources,
                    AppearanceMatrix(appearance, annotation.Rectangle), document.Options);
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                // As for the page.
            }
        }

        return [.. order.Select(k => page[k].ToItem(number))];
    }

    /// <summary>The trim and bleed boxes, and the bleed between them in millimetres.</summary>
    private void Boxes(PdfPage pdfPage)
    {
        bool hasTrim = pdfPage.Dictionary.ContainsKey("TrimBox");
        PdfRectangle trim = pdfPage.TrimBox.Normalize(), bleed = pdfPage.BleedBox.Normalize();
        double? mm = null;
        string text;
        if (hasTrim)
        {
            double points = Math.Min(Math.Min(trim.Left - bleed.Left, trim.Bottom - bleed.Bottom), Math.Min(bleed.Right - trim.Right, bleed.Top - trim.Top));
            mm = Math.Max(0, Math.Round(points * 25.4 / 72, 2));
            text = string.Create(CultureInfo.InvariantCulture, $"trim box {trim.Width:0.##} x {trim.Height:0.##} pt, bleed {mm:0.##} mm");
        }
        else
        {
            text = "no trim box";
        }

        Add(PdfPreflightItemKind.PageBoxes, "boxes", text, b => b.Bleed = mm);
    }

    /// <summary>The matrix that maps an appearance's form space onto the annotation's rectangle (12.5.5).</summary>
    private static PdfMatrix AppearanceMatrix(PdfStream appearance, PdfRectangle rectangle)
    {
        PdfMatrix matrix = PdfMatrix.FromArray(appearance.Dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity;
        PdfRectangle box = (PdfRectangle.FromArray(appearance.Dictionary.Get<PdfArray>("BBox")) ?? rectangle).Normalize();
        PdfRectangle placed = matrix.Transform(box).Normalize(), r = rectangle.Normalize();
        double sx = placed.Width > 0 ? r.Width / placed.Width : 1, sy = placed.Height > 0 ? r.Height / placed.Height : 1;
        return matrix * new PdfMatrix(sx, 0, 0, sy, r.Left - (placed.Left * sx), r.Bottom - (placed.Bottom * sy));
    }

    /// <summary>Counts one use of an item, made with <paramref name="setup"/> the first time.</summary>
    private void Add(PdfPreflightItemKind kind, string key, string description, Action<Builder>? setup = null, Action<Builder>? use = null)
    {
        if (!page.TryGetValue(key, out Builder? builder))
        {
            builder = new Builder(kind, description);
            setup?.Invoke(builder);
            page[key] = builder;
            order.Add(key);
        }
        else
        {
            builder.Count++;
        }

        use?.Invoke(builder);
    }

    /// <summary>A colour space painted with: its kind, name, profile and spots, counted per use.</summary>
    private void Color(PdfObject space, PdfDictionary? resources, string use)
    {
        if (Describe(space, resources, 0) is not Space described)
        {
            return;
        }

        Add(PdfPreflightItemKind.ColorSpace, $"space:{described.Key}:{use}", $"{described.Name} for {use}", b =>
        {
            b.ColorSpace = described.Kind;
            b.ColorSpaceName = described.Name;
            b.ProfileName = described.Profile;
            b.Spots = described.Spots;
            b.Name = described.ResourceName;
        });
    }

    /// <summary>What a colour space is; null for one that cannot be read.</summary>
    internal Space? Describe(PdfObject? value, PdfDictionary? resources, int depth)
    {
        if (value is null || depth > MaxDepth)
        {
            return null;
        }

        string? resourceName = null;
        PdfObject space = value.Resolve();
        if (space is PdfName named && named.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK" or "CalRGB" or "CalGray" or "Lab")
            && resources?.Get<PdfDictionary>("ColorSpace")?.Get(named)?.Resolve() is PdfObject found)
        {
            resourceName = named.Value;
            space = found;
        }

        Space? result = space switch
        {
            PdfName { Value: "DeviceGray" or "G" } => Device("DeviceGray", "DefaultGray", PdfColorSpaceKinds.DeviceGray, resources, depth),
            PdfName { Value: "DeviceRGB" or "RGB" } => Device("DeviceRGB", "DefaultRGB", PdfColorSpaceKinds.DeviceRgb, resources, depth),
            PdfName { Value: "DeviceCMYK" or "CMYK" } => Device("DeviceCMYK", "DefaultCMYK", PdfColorSpaceKinds.DeviceCmyk, resources, depth),
            PdfArray { Count: > 0 } array => Family(array, resources, depth),
            _ => null,
        };
        return result is null ? null : result with { ResourceName = resourceName };
    }

    /// <summary>A device space, or the Default space the resources put in its place (8.6.5.6).</summary>
    private Space Device(string name, string defaultKey, PdfColorSpaceKinds kind, PdfDictionary? resources, int depth)
    {
        if (resources?.Get<PdfDictionary>("ColorSpace")?.Get(defaultKey) is PdfObject replacement && Describe(replacement, null, depth + 1) is Space standIn)
        {
            return standIn with { Name = $"{name} as {defaultKey} ({standIn.Name})", Key = $"{name}>{standIn.Key}" };
        }

        return new Space(kind, name, null, [], name);
    }

    private Space? Family(PdfArray array, PdfDictionary? resources, int depth)
    {
        string family = array.GetName(0)?.Value ?? "";
        switch (family)
        {
            case "ICCBased" when array.Count > 1 && array[1].Resolve() is PdfStream profile:
                {
                    PdfColorSpaceKinds kind = IccKind(profile);
                    string? description = ProfileName(profile);
                    string name = "ICCBased " + KindName(kind);
                    return new Space(kind, name, description, [], $"{name}:{RuntimeHelpers.GetHashCode(profile)}");
                }

            case "CalGray":
                return new Space(PdfColorSpaceKinds.CalibratedGray, "CalGray", null, [], "CalGray");
            case "CalRGB":
                return new Space(PdfColorSpaceKinds.CalibratedRgb, "CalRGB", null, [], "CalRGB");
            case "Lab":
                return new Space(PdfColorSpaceKinds.Lab, "Lab", null, [], "Lab");
            case "Indexed" or "I" when array.Count > 1 && Describe(array[1], resources, depth + 1) is Space baseSpace:
                return baseSpace with { Name = "Indexed", Key = "Indexed>" + baseSpace.Key };
            case "Pattern" when array.Count > 1 && Describe(array[1], resources, depth + 1) is Space under:
                return under;
            case "Separation" when array.Count > 1 && array.GetName(1)?.Value is string colorant:
                Note(array, [colorant]);
                return colorant switch
                {
                    "None" => null,
                    "All" => new Space(PdfColorSpaceKinds.DeviceCmyk, "Separation", null, [], "Separation:All"),
                    _ => Colorants("Separation", [colorant]),
                };
            case "DeviceN" when array.Count > 1 && array[1].Resolve() is PdfArray names:
                Note(array, [.. names.Select(n => n.Resolve()).OfType<PdfName>().Select(n => n.Value)]);
                return Colorants("DeviceN", [.. names.Select(n => n.Resolve()).OfType<PdfName>().Select(n => n.Value)]);
            default:
                return null;
        }
    }

    /// <summary>Keeps the first space that names each spot ink.</summary>
    private void Note(PdfArray space, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (name is not ("Cyan" or "Magenta" or "Yellow" or "Black" or "None" or "All") && spotSpaces.TryAdd(name, space))
            {
                spotOrder.Add(name);
            }
        }
    }

    /// <summary>A Separation or DeviceN space: spot unless it names only process colorants.</summary>
    private static Space Colorants(string family, IReadOnlyList<string> names)
    {
        string[] spots = [.. names.Where(n => n is not ("Cyan" or "Magenta" or "Yellow" or "Black" or "None" or "All")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        PdfColorSpaceKinds kind = spots.Length > 0 ? PdfColorSpaceKinds.Spot : PdfColorSpaceKinds.DeviceCmyk;
        return new Space(kind, family, null, spots, $"{family}:{string.Join('|', names)}");
    }

    /// <summary>The kind of an ICC profile by its header's colour space, or its /N.</summary>
    private static PdfColorSpaceKinds IccKind(PdfStream profile)
    {
        string? signature = null;
        try
        {
            byte[] data = profile.GetDecodedBytes();
            signature = data.Length >= 20 ? PdfA.ColorScan.ProfileSpace(data) : null;
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            // Read the component count instead.
        }

        return (signature, profile.Dictionary.GetInt("N")) switch
        {
            ("GRAY", _) or (null, 1) => PdfColorSpaceKinds.CalibratedGray,
            ("CMYK", _) or (null, 4) => PdfColorSpaceKinds.CalibratedCmyk,
            ("Lab", _) => PdfColorSpaceKinds.Lab,
            ("RGB", _) or (null, 3) => PdfColorSpaceKinds.CalibratedRgb,
            _ => profile.Dictionary.GetInt("N") switch
            {
                1 => PdfColorSpaceKinds.CalibratedGray,
                4 => PdfColorSpaceKinds.CalibratedCmyk,
                _ => PdfColorSpaceKinds.CalibratedRgb,
            },
        };
    }

    private static string KindName(PdfColorSpaceKinds kind)
    {
        return kind switch
        {
            PdfColorSpaceKinds.CalibratedGray or PdfColorSpaceKinds.DeviceGray => "Gray",
            PdfColorSpaceKinds.CalibratedCmyk or PdfColorSpaceKinds.DeviceCmyk => "CMYK",
            PdfColorSpaceKinds.Lab => "Lab",
            _ => "RGB",
        };
    }

    /// <summary>An ICC profile's description, read once per stream; null when Egami cannot read it.</summary>
    private string? ProfileName(PdfStream profile)
    {
        return profileNames.GetValue(profile, s =>
        {
            try
            {
                return ColorProfile.FromIcc(s.GetDecodedBytes()).Name;
            }
            catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidDataException or PdfException)
            {
                return null;
            }
        });
    }

    /// <summary>A font that draws visible text, once per page.</summary>
    private void Font(PdfDictionary font)
    {
        string name = font.GetName("BaseFont")?.Value ?? "(unnamed)";
        string subtype = font.GetName("Subtype")?.Value ?? "?";
        bool embedded = IsEmbedded(font);
        Add(PdfPreflightItemKind.Font, $"font:{RuntimeHelpers.GetHashCode(font)}", $"{name} ({subtype}), {(embedded ? "embedded" : "not embedded")}", b =>
        {
            b.Name = name;
            b.Embedded = embedded;
        });
    }

    /// <summary>Whether a font's program is in the file: always for Type 3, else its (descendant's) descriptor's font file.</summary>
    internal static bool IsEmbedded(PdfDictionary font)
    {
        PdfDictionary target = font.GetName("Subtype")?.Value == "Type0" && font.Get<PdfArray>("DescendantFonts") is { Count: > 0 } descendants
            && descendants[0].Resolve() is PdfDictionary descendant ? descendant : font;
        if (target.GetName("Subtype")?.Value == "Type3")
        {
            return true;
        }

        PdfDictionary? descriptor = target.Get<PdfDictionary>("FontDescriptor");
        return descriptor is not null && (descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3"));
    }

    /// <summary>A colour space as the inventory describes it.</summary>
    internal sealed record Space(PdfColorSpaceKinds Kind, string Name, string? Profile, IReadOnlyList<string> Spots, string Key)
    {
        public string? ResourceName { get; init; }
    }

    /// <summary>An item as it is counted on the page.</summary>
    private sealed class Builder(PdfPreflightItemKind kind, string description)
    {
        public int Count { get; set; } = 1;

        public string? Name { get; set; }

        public PdfColorSpaceKinds ColorSpace { get; set; }

        public string? ColorSpaceName { get; set; }

        public string? ProfileName { get; set; }

        public IReadOnlyList<string> Spots { get; set; } = [];

        public double? Dpi { get; set; }

        public double? LineWidth { get; set; }

        public double? Bleed { get; set; }

        public bool? Embedded { get; set; }

        public int? BitsPerComponent { get; set; }

        public PdfPreflightItem ToItem(int number)
        {
            string text = description;
            if (kind == PdfPreflightItemKind.Image && Dpi is double dpi)
            {
                text += string.Create(CultureInfo.InvariantCulture, $", {Math.Round(dpi)} dpi");
            }

            if (Count > 1)
            {
                text += string.Create(CultureInfo.InvariantCulture, $", {Count} times");
            }

            return new PdfPreflightItem(kind, number, text)
            {
                Name = Name,
                ColorSpace = ColorSpace,
                ColorSpaceName = ColorSpaceName,
                ProfileName = ProfileName,
                Spots = Spots,
                Dpi = Dpi is double d ? Math.Round(d, 1) : null,
                LineWidth = LineWidth,
                Bleed = Bleed,
                Embedded = Embedded,
                BitsPerComponent = BitsPerComponent,
                Count = Count,
            };
        }
    }

    /// <summary>The walk over one content stream and what it draws.</summary>
    private sealed class Walker(Inventory inventory) : PdfContentWalker
    {
        private int depth;

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            bool fill = (paint & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0, stroke = (paint & PdfPathPaint.Stroke) != 0;
            if (fill)
            {
                Paint(State.FillColor, "fills", fill: true);
            }

            if (stroke)
            {
                Paint(State.StrokeColor, "strokes", fill: false);
                double width = State.LineWidth * Scale(State.Matrix);
                if (width < inventory.hairline)
                {
                    double rounded = Math.Round(width, 3);
                    inventory.Add(PdfPreflightItemKind.Hairline, string.Create(CultureInfo.InvariantCulture, $"hairline:{rounded}"),
                        string.Create(CultureInfo.InvariantCulture, $"stroke {rounded:0.###} pt wide"), b => b.LineWidth = rounded);
                }
            }
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            int mode = State.TextRenderMode;
            if (mode is 3 or 7)
            {
                return;
            }

            if (State.Font is PdfDictionary font)
            {
                inventory.Font(font);
            }

            if (mode is 0 or 2 or 4 or 6)
            {
                Paint(State.FillColor, "text", fill: true);
            }

            if (mode is 1 or 2 or 5 or 6)
            {
                Paint(State.StrokeColor, "text outlines", fill: false);
            }
        }

        protected override void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
        {
            PdfImage pdfImage = image is not null ? new PdfImage(image, Resources, inventory.document) : new PdfImage(inlineImage!, Resources);
            Transparency(fill: true);
            if (pdfImage.IsStencilMask)
            {
                Paint(State.FillColor, "image masks", fill: true);
                return;
            }

            if (pdfImage.Parameters.ContainsKey("SMask") || (pdfImage.Parameters.GetInt("SMaskInData") ?? 0) != 0)
            {
                inventory.Add(PdfPreflightItemKind.Transparency, "image-smask", "image with a soft mask");
            }

            Space? space = inventory.Describe(pdfImage.Parameters.Get("ColorSpace"), Resources, 0);
            if (space is null && pdfImage.ImageCodec?.Value == "JPXDecode")
            {
                space = new Space(PdfColorSpaceKinds.CalibratedRgb, "JPEG 2000 own colour space", null, [], "JPX");
            }

            PdfMatrix m = State.Matrix;
            double placedWidth = Math.Sqrt((m.A * m.A) + (m.B * m.B)), placedHeight = Math.Sqrt((m.C * m.C) + (m.D * m.D));
            double? dpi = placedWidth > 0 && placedHeight > 0 ? Math.Min(pdfImage.Width * 72 / placedWidth, pdfImage.Height * 72 / placedHeight) : null;
            string label = name is null ? "inline image" : $"image /{name.Value}";
            string key = image is not null ? $"image:{RuntimeHelpers.GetHashCode(image)}" : $"inline:{pdfImage.Width}x{pdfImage.Height}:{space?.Key}";
            string bits = pdfImage.BitsPerComponent == 1 ? ", 1 bit" : "";
            inventory.Add(PdfPreflightItemKind.Image, key, $"{label}, {pdfImage.Width} x {pdfImage.Height}, {space?.Name ?? "unknown colour space"}{bits}", b =>
            {
                b.Name = name?.Value;
                b.ColorSpace = space?.Kind ?? PdfColorSpaceKinds.None;
                b.ColorSpaceName = space?.Name;
                b.ProfileName = space?.Profile;
                b.Spots = space?.Spots ?? [];
                b.BitsPerComponent = pdfImage.BitsPerComponent;
            }, b => b.Dpi = dpi is double d && (b.Dpi is null || d < b.Dpi) ? d : b.Dpi);
            if (space is not null)
            {
                inventory.Add(PdfPreflightItemKind.ColorSpace, $"space:{space.Key}:images", $"{space.Name} for images", b =>
                {
                    b.ColorSpace = space.Kind;
                    b.ColorSpaceName = space.Name;
                    b.ProfileName = space.Profile;
                    b.Spots = space.Spots;
                    b.Name = space.ResourceName;
                });
            }
        }

        protected override void OnShading(PdfObject? shading, PdfName name)
        {
            Transparency(fill: true);
            Shading(shading?.Resolve());
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            if (form.Dictionary.Get<PdfDictionary>("Group") is PdfDictionary group && group.GetName("S")?.Value == "Transparency")
            {
                inventory.Add(PdfPreflightItemKind.Transparency, "group:form", "a form is a transparency group");
            }

            return true;
        }

        private void Shading(PdfObject? shading)
        {
            PdfDictionary? d = shading as PdfDictionary ?? (shading as PdfStream)?.Dictionary;
            if (d is not null)
            {
                Color(d.Get("ColorSpace"), "shadings");
            }
        }

        private void Paint(PdfColor color, string use, bool fill)
        {
            Transparency(fill);
            Overprint(color, fill);
            if (color.Family != "Pattern")
            {
                Color(color.Space, use);
                return;
            }

            if (color.Pattern is not PdfName patternName || Resources?.Get<PdfDictionary>("Pattern")?.Get(patternName)?.Resolve() is not PdfObject pattern)
            {
                return;
            }

            switch (pattern)
            {
                case PdfStream tiling when tiling.Dictionary.GetInt("PaintType") == 2:
                    // An uncoloured pattern paints in the base space given with the colour.
                    if (color.Space.Resolve() is PdfArray { Count: > 1 } withBase)
                    {
                        Color(withBase[1], use);
                    }

                    break;
                case PdfStream tiling when depth < MaxDepth:
                    Walker inner = new(inventory) { depth = depth + 1 };
                    try
                    {
                        PdfMatrix matrix = PdfMatrix.FromArray(tiling.Dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity;
                        inner.Walk(PdfContent.Read(tiling), tiling.Dictionary.Get<PdfDictionary>("Resources") ?? Resources, matrix, inventory.document.Options);
                    }
                    catch (PdfException e) when (e is not PdfLimitException)
                    {
                        // An unreadable pattern paints nothing.
                    }

                    break;
                case PdfDictionary shadingPattern:
                    Shading(shadingPattern.Get("Shading")?.Resolve());
                    break;
            }
        }

        private void Color(PdfObject? space, string use)
        {
            if (space is not null)
            {
                inventory.Color(space, Resources, use);
            }
        }

        private void Overprint(PdfColor color, bool fill)
        {
            if (!(fill ? State.FillOverprint : State.StrokeOverprint))
            {
                return;
            }

            int mode = State.OverprintMode;
            string what = fill ? "fills" : "strokes";
            inventory.Add(PdfPreflightItemKind.Overprint, $"overprint:{what}:{mode}", $"overprint on {what}, mode {mode}");
        }

        private void Transparency(bool fill)
        {
            double alpha = fill ? State.FillAlpha : State.StrokeAlpha;
            if (alpha < 1)
            {
                string value = alpha.ToString("0.###", CultureInfo.InvariantCulture);
                inventory.Add(PdfPreflightItemKind.Transparency, $"alpha:{value}", $"constant alpha {value}");
            }

            if (State.BlendMode.Value is not ("Normal" or "Compatible"))
            {
                inventory.Add(PdfPreflightItemKind.Transparency, $"blend:{State.BlendMode.Value}", $"blend mode {State.BlendMode.Value}");
            }

            if (State.SoftMask is not null)
            {
                inventory.Add(PdfPreflightItemKind.Transparency, "smask", "soft mask");
            }
        }

        /// <summary>How far a unit length in user space reaches on the page: the square root of the matrix's area scale.</summary>
        private static double Scale(PdfMatrix m)
        {
            return Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        }
    }
}
