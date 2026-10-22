using Egap.Pdf.Images;
using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Cleanup;

/// <summary>
/// The colour conversion pass (Phase 7 task 17, generalised by Phase 8 task 10): every colour the document paints in a
/// space the <see cref="ColorTarget"/> converts is rewritten in the target's device space, and vectors stay vectors. Content
/// streams of pages, forms, patterns, Type 3 glyphs, soft masks and annotation appearances have their colour operators
/// rewritten (<c>rg</c>, <c>k</c>, <c>sc</c> and <c>scn</c> in any space, Separation and DeviceN through their alternates);
/// shadings get a sampled function in the target, or target vertex colours for meshes; images are decoded and written in
/// the target, Indexed images keep their samples under a converted palette; annotations' colours are converted. Spot
/// colours the target keeps keep their spaces with the alternate rewritten into the target. Each object is converted once
/// however often it is used.
/// </summary>
internal sealed class ColorRewriter
{
    private readonly PdfDocument document;
    private readonly ColorTarget target;
    private readonly HashSet<PdfObject> done = new(ReferenceEqualityComparer.Instance);
    private readonly List<PdfWarning> warnings = [];

    private ColorRewriter(PdfDocument document, ColorTarget target)
    {
        this.document = document;
        this.target = target;
    }

    /// <summary>What a run changed.</summary>
    public sealed class Counts
    {
        public int Colors { get; set; }

        public int Images { get; set; }

        public int Shadings { get; set; }

        public int Alternates { get; set; }

        public int PureBlack { get; set; }

        public int Annotations { get; set; }
    }

    public Counts Changed { get; } = new();

    public IReadOnlyList<PdfWarning> Warnings => warnings;

    /// <summary>Converts the whole document.</summary>
    public static ColorRewriter Run(PdfDocument document, ColorTarget target, CancellationToken cancellationToken = default)
    {
        ColorRewriter rewriter = new(document, target);
        foreach (PdfPage page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rewriter.Page(page);
        }

        return rewriter;
    }

    private static readonly ImageColorSpace Gray = ImageColorSpace.Read(PdfName.Get("DeviceGray"), null, null);

    private static readonly ImageColorSpace[] Devices = [Gray, ImageColorSpace.Read(PdfName.Get("DeviceRGB"), null, null), ImageColorSpace.Read(PdfName.Get("DeviceCMYK"), null, null)];

    private static ImageColorSpace Device(int components) => Devices[components switch { 1 => 0, 3 => 1, _ => 2 }];

    private void Page(PdfPage page)
    {
        PdfDictionary resources = page.Resources ?? [];
        try
        {
            PdfContent content = page.GetContent();
            if (Rewrite(content, resources) is PdfContent converted)
            {
                page.SetContent(converted);
            }
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A page's content could not be converted: {e.Message}"));
        }

        Group(page.Dictionary, page.Resources);
        Resources(page.Resources);
        foreach (PdfAnnotation annotation in page.Annotations)
        {
            Array(annotation.Dictionary, "C", annotation: true);
            Array(annotation.Dictionary, "IC", annotation: true);
            if (annotation.Dictionary.Get<PdfDictionary>("AP") is PdfDictionary ap)
            {
                foreach (PdfObject value in ap.Entries.Select(e => e.Value.Resolve()))
                {
                    IEnumerable<PdfStream> streams = value switch
                    {
                        PdfStream single => [single],
                        PdfDictionary states => states.Entries.Select(s => s.Value.Resolve()).OfType<PdfStream>(),
                        _ => [],
                    };
                    foreach (PdfStream stream in streams)
                    {
                        Form(stream);
                    }
                }
            }
        }
    }

    /// <summary>A colour array of one (gray), three (RGB) or four (CMYK) numbers in the target, when the target converts that device space.</summary>
    private void Array(PdfDictionary dictionary, string key, bool annotation = false)
    {
        if (dictionary.Get<PdfArray>(key) is not PdfArray color || color.Count is not (1 or 3 or 4) || color.Any(c => c.Resolve() is not (PdfInteger or PdfReal)))
        {
            return;
        }

        double[] c = [.. color.Select((_, i) => color.GetNumber(i) ?? 0)];
        ImageColorSpace space = Device(c.Length);
        if (!target.Converts(space))
        {
            return;
        }

        dictionary[key] = Numbers(target.Convert(space, c));
        if (annotation)
        {
            Changed.Annotations++;
        }
    }

    private static PdfArray Numbers(double[] values) => new([.. values.Select(v => (PdfObject)new PdfReal(v))]);

    /// <summary>A transparency group's colour space made the target's when its colours are converted.</summary>
    private void Group(PdfDictionary owner, PdfDictionary? resources)
    {
        if (owner.Get<PdfDictionary>("Group") is PdfDictionary group && group.Get("CS") is PdfObject cs && Read(cs, resources) is ImageColorSpace space && target.Converts(space))
        {
            group["CS"] = target.Space;
        }
    }

    /// <summary>Every resource an XObject, pattern, shading, colour space, soft mask or Type 3 font names.</summary>
    private void Resources(PdfDictionary? resources)
    {
        if (resources is null || !done.Add(resources))
        {
            return;
        }

        foreach (PdfObject value in (resources.Get<PdfDictionary>("XObject")?.Entries ?? []).Select(e => e.Value.Resolve()).ToList())
        {
            if (value is PdfStream xobject)
            {
                switch (xobject.Dictionary.GetName("Subtype")?.Value)
                {
                    case "Image":
                        Image(xobject, resources);
                        break;
                    case "Form":
                        Form(xobject);
                        break;
                }
            }
        }

        foreach (PdfObject value in (resources.Get<PdfDictionary>("Pattern")?.Entries ?? []).Select(e => e.Value.Resolve()).ToList())
        {
            switch (value)
            {
                case PdfStream tiling:
                    Form(tiling);
                    break;
                case PdfDictionary shadingPattern when shadingPattern.Get("Shading")?.Resolve() is PdfObject shading:
                    Shading(shading, resources);
                    break;
            }
        }

        foreach (PdfObject value in (resources.Get<PdfDictionary>("Shading")?.Entries ?? []).Select(e => e.Value.Resolve()).ToList())
        {
            Shading(value, resources);
        }

        // Spot spaces the target keeps get their alternates in the target, wherever content names them.
        foreach (PdfObject value in (resources.Get<PdfDictionary>("ColorSpace")?.Entries ?? []).Select(e => e.Value.Resolve()).ToList())
        {
            Alternate(value, resources);
        }

        foreach (PdfDictionary state in (resources.Get<PdfDictionary>("ExtGState")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfDictionary>().ToList())
        {
            if (state.Get("SMask")?.Resolve() is PdfDictionary mask && done.Add(mask))
            {
                if (mask.Get("G")?.Resolve() is PdfStream group)
                {
                    Form(group);
                }

                if (mask.Get<PdfArray>("BC") is { Count: 1 or 3 or 4 })
                {
                    Array(mask, "BC");
                }
            }
        }

        foreach (PdfDictionary font in (resources.Get<PdfDictionary>("Font")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfDictionary>().ToList())
        {
            if (font.GetName("Subtype")?.Value == "Type3" && font.Get<PdfDictionary>("CharProcs") is PdfDictionary procs && done.Add(font))
            {
                PdfDictionary fontResources = font.Get<PdfDictionary>("Resources") ?? resources;
                foreach (PdfStream glyph in procs.Entries.Select(e => e.Value.Resolve()).OfType<PdfStream>())
                {
                    Stream(glyph, fontResources);
                }

                Resources(font.Get<PdfDictionary>("Resources"));
            }
        }
    }

    /// <summary>A form XObject, tiling pattern or appearance: its content, its group's colour space and its resources.</summary>
    private void Form(PdfStream form)
    {
        if (!done.Add(form))
        {
            return;
        }

        PdfDictionary resources = form.Dictionary.Get<PdfDictionary>("Resources") ?? [];
        Stream(form, resources);
        if (form.Dictionary.ContainsKey("PatternType"))
        {
            Rendering.PageRenderer.ForgetPattern(form);
        }

        Group(form.Dictionary, resources);
        Resources(form.Dictionary.Get<PdfDictionary>("Resources"));
    }

    private void Stream(PdfStream stream, PdfDictionary resources)
    {
        try
        {
            if (Rewrite(PdfContent.Read(stream), resources) is PdfContent converted)
            {
                stream.Dictionary.Remove("DecodeParms");
                stream.Dictionary["Filter"] = PdfName.Get("FlateDecode");
                stream.SetDecodedBytes(converted.ToBytes());
            }
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A content stream could not be converted: {e.Message}"));
        }
    }

    private static ImageColorSpace? Read(PdfObject? value, PdfDictionary? resources)
    {
        try
        {
            return ImageColorSpace.Read(value, resources, null);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
    }

    private static double[] Numbers(PdfOperation op)
    {
        return [.. op.Operands.Select(o => o switch { PdfInteger i => (double)i.Value, PdfReal r => r.Value, _ => 0 })];
    }

    private PdfOperation SetColor(bool fill, double[] values)
    {
        return new PdfOperation(target.Operator(fill), [.. values.Select(v => (PdfObject)new PdfReal(v))]);
    }

    /// <summary>
    /// The colour state the rewrite tracks: each side's source space and colour, the pattern base when a pattern space is
    /// set, and whether the side was switched to pure black for text or a thin rule.
    /// </summary>
    private readonly record struct ColorState(
        ImageColorSpace Fill,
        ImageColorSpace Stroke,
        ImageColorSpace? FillPattern,
        ImageColorSpace? StrokePattern,
        double[] FillValues,
        double[] StrokeValues,
        bool FillPure,
        bool StrokePure,
        double LineWidth,
        double Scale);

    /// <summary>The content with every converted colour operator in the target, or null when nothing changed.</summary>
    private PdfContent? Rewrite(PdfContent content, PdfDictionary resources)
    {
        PdfContent result = [];
        bool changed = false;
        ColorState state = new(Gray, Gray, null, null, [0], [0], false, false, 1, 1);
        Stack<ColorState> saved = new();
        bool inText = false;

        // Pure black for neutral text and thin rules: the colorimetric colour comes back when text or the rule ends.
        void Pure(bool fill)
        {
            ImageColorSpace space = fill ? state.Fill : state.Stroke;
            if ((fill ? state.FillPure : state.StrokePure) || (fill ? state.FillPattern : state.StrokePattern) is not null || !target.Converts(space)
                || target.PureBlack(space, fill ? state.FillValues : state.StrokeValues) is not double[] black)
            {
                return;
            }

            result.Add(SetColor(fill, black));
            state = fill ? state with { FillPure = true } : state with { StrokePure = true };
            changed = true;
            Changed.PureBlack++;
        }

        void Colorimetric(bool fill)
        {
            if (!(fill ? state.FillPure : state.StrokePure))
            {
                return;
            }

            result.Add(SetColor(fill, target.Convert(fill ? state.Fill : state.Stroke, fill ? state.FillValues : state.StrokeValues)));
            state = fill ? state with { FillPure = false } : state with { StrokePure = false };
        }

        foreach (PdfOperation op in content)
        {
            switch (op.Operator)
            {
                case "q":
                    saved.Push(state);
                    break;
                case "Q":
                    if (saved.Count > 0)
                    {
                        state = saved.Pop();
                    }

                    break;
                case "w" when op.Operands.Count > 0:
                    state = state with { LineWidth = Numbers(op)[0] };
                    break;
                case "cm" when op.Operands.Count == 6:
                    double[] m = Numbers(op);
                    state = state with { Scale = state.Scale * Math.Sqrt(Math.Abs((m[0] * m[3]) - (m[1] * m[2]))) };
                    break;
                case "BT":
                    inText = true;
                    break;
                case "ET":
                    inText = false;
                    result.Add(op);
                    Colorimetric(fill: true);
                    Colorimetric(fill: false);
                    continue;
                case "Tj" or "TJ" or "'" or "\"" when inText:
                    Pure(fill: true);
                    Pure(fill: false);
                    break;
                case "S" or "s" or "b" or "b*" or "B" or "B*" when !inText && state.LineWidth * state.Scale <= target.ThinRule:
                    {
                        Pure(fill: false);
                        bool pure = state.StrokePure;
                        result.Add(op);
                        if (pure)
                        {
                            Colorimetric(fill: false);
                        }

                        continue;
                    }

                case "g" or "G" or "rg" or "RG" or "k" or "K":
                    {
                        bool fill = op.Operator is "g" or "rg" or "k";
                        ImageColorSpace space = Device(op.Operator is "g" or "G" ? 1 : op.Operator is "rg" or "RG" ? 3 : 4);
                        double[] values = Numbers(op);
                        state = fill
                            ? state with { Fill = space, FillPattern = null, FillValues = values, FillPure = false }
                            : state with { Stroke = space, StrokePattern = null, StrokeValues = values, StrokePure = false };
                        if (!target.Converts(space))
                        {
                            break;
                        }

                        result.Add(SetColor(fill, target.Convert(space, values)));
                        Changed.Colors++;
                        changed = true;
                        if (inText)
                        {
                            Pure(fill);
                        }

                        continue;
                    }

                case "cs" or "CS":
                    {
                        bool fill = op.Operator == "cs";
                        PdfObject? named = op.Operands.Count > 0 ? op.Operands[0] : null;
                        PdfObject? value = named is PdfName name && name.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK")
                            ? resources.Get<PdfDictionary>("ColorSpace")?.Get(name.Value) ?? named : named;
                        if (value?.Resolve() is PdfName { Value: "Pattern" } || value?.Resolve() is PdfArray { Count: > 0 } pa && pa.GetName(0)?.Value == "Pattern")
                        {
                            // A pattern keeps its space: the pattern itself is converted; an uncoloured one's base goes to the target.
                            ImageColorSpace? under = value.Resolve() is PdfArray { Count: > 1 } withBase ? Read(withBase[1], resources) : null;
                            state = fill ? state with { FillPattern = under ?? Gray, FillPure = false } : state with { StrokePattern = under ?? Gray, StrokePure = false };
                            if (under is not null && target.Converts(under))
                            {
                                PdfName patternSpace = PdfName.Get(target.Components == 1 ? "EgPatternGray" : "EgPattern" + target.Space.Value[6..]);
                                if (resources.Get<PdfDictionary>("ColorSpace") is not PdfDictionary spaces)
                                {
                                    resources["ColorSpace"] = spaces = [];
                                }

                                spaces[patternSpace] = new PdfArray { PdfName.Get("Pattern"), target.Space };
                                result.Add(new PdfOperation(op.Operator, [patternSpace]));
                                changed = true;
                                continue;
                            }

                            break;
                        }

                        if (Read(value, resources) is not ImageColorSpace read)
                        {
                            break;
                        }

                        // The space's initial colour: full ink for Separation and DeviceN, else zero.
                        double[] initial = new double[read.Components];
                        System.Array.Fill(initial, read.Family == ColorFamily.Separation ? 1 : 0);
                        state = fill
                            ? state with { Fill = read, FillPattern = null, FillValues = initial, FillPure = false }
                            : state with { Stroke = read, StrokePattern = null, StrokeValues = initial, StrokePure = false };
                        if (!target.Converts(read))
                        {
                            if (read.Family == ColorFamily.Separation && value is not null)
                            {
                                Alternate(value, resources);
                            }

                            break;
                        }

                        result.Add(new PdfOperation(op.Operator, [target.Space]));
                        result.Add(new PdfOperation(fill ? "sc" : "SC", [.. target.Convert(read, initial).Select(v => (PdfObject)new PdfReal(v))]));
                        Changed.Colors++;
                        changed = true;
                        continue;
                    }

                case "sc" or "scn" or "SC" or "SCN":
                    {
                        bool fill = op.Operator is "sc" or "scn";
                        ImageColorSpace? pattern = fill ? state.FillPattern : state.StrokePattern;
                        ImageColorSpace space = fill ? state.Fill : state.Stroke;
                        if (pattern is not null)
                        {
                            if (target.Converts(pattern) && op.Operands.Count > 1 && op.Operands[^1] is PdfName patternName)
                            {
                                double[] tint = [.. op.Operands.Take(op.Operands.Count - 1).Select(o => o is PdfInteger i ? i.Value : o is PdfReal r ? r.Value : 0)];
                                result.Add(new PdfOperation(op.Operator, [.. target.Convert(pattern, tint).Select(v => (PdfObject)new PdfReal(v)), patternName]));
                                changed = true;
                                continue;
                            }

                            break;
                        }

                        double[] values = Numbers(op);
                        state = fill ? state with { FillValues = values, FillPure = false } : state with { StrokeValues = values, StrokePure = false };
                        if (!target.Converts(space))
                        {
                            break;
                        }

                        result.Add(new PdfOperation(fill ? "sc" : "SC", [.. target.Convert(space, values).Select(v => (PdfObject)new PdfReal(v))]));
                        Changed.Colors++;
                        changed = true;
                        if (inText)
                        {
                            Pure(fill);
                        }

                        continue;
                    }

                case "BI" when op is PdfInlineImage inline:
                    if (InlineImage(inline, resources) is PdfInlineImage converted)
                    {
                        result.Add(converted);
                        changed = true;
                        continue;
                    }

                    break;
            }

            result.Add(op);
        }

        return changed ? result : null;
    }

    /// <summary>
    /// A Separation or DeviceN space the target keeps, its alternate rewritten into the target: the alternate becomes the
    /// target's device space and the tint transform a sampled function giving the converted alternate colour, so the spot
    /// prints on its plate as before and looks the same where it has none. A DeviceN space's own Separation spaces too.
    /// </summary>
    private void Alternate(PdfObject value, PdfDictionary? resources)
    {
        if (!target.RetargetsAlternates || value.Resolve() is not PdfArray { Count: >= 4 } array || !done.Add(array))
        {
            return;
        }

        string family = array.GetName(0)?.Value ?? "";
        if (family is "Indexed" or "I")
        {
            Alternate(array[1], resources);
            return;
        }

        if (family is not ("Separation" or "DeviceN") || Read(array, resources) is not ImageColorSpace space || space.Alternate is not ImageColorSpace alternate
            || !target.Converts(alternate) || target.Converts(space))
        {
            return;
        }

        if (family == "DeviceN" && array.Count > 4 && array.Get<PdfDictionary>(4)?.Get<PdfDictionary>("Colorants") is PdfDictionary colorants)
        {
            foreach (PdfObject own in colorants.Entries.Select(e => e.Value).ToList())
            {
                Alternate(own, resources);
            }
        }

        int inputs = space.Components;
        int size = inputs switch { 1 => 256, 2 => 33, 3 => 17, 4 => 9, 5 => 7, 6 => 5, _ => 0 };
        if (size == 0)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A DeviceN space of {inputs} colorants keeps its alternate."));
            return;
        }

        int n = target.Components;
        long count = (long)Math.Pow(size, inputs);
        byte[] samples = new byte[count * n];
        double[] tints = new double[inputs], alt = new double[alternate.Components], output = new double[n];
        for (long i = 0; i < count; i++)
        {
            long index = i;
            for (int k = 0; k < inputs; k++)
            {
                tints[k] = (index % size) / (double)(size - 1);
                index /= size;
            }

            space.ToAlternate(tints, alt);
            target.Convert(alternate, alt, output);
            for (int c = 0; c < n; c++)
            {
                samples[(i * n) + c] = (byte)Math.Round(output[c] * 255);
            }
        }

        PdfStream function = new(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Domain"] = new PdfArray([.. Enumerable.Range(0, inputs).SelectMany(_ => new PdfObject[] { new PdfInteger(0), new PdfInteger(1) })]),
            ["Range"] = new PdfArray([.. Enumerable.Range(0, n).SelectMany(_ => new PdfObject[] { new PdfInteger(0), new PdfInteger(1) })]),
            ["Size"] = new PdfArray([.. Enumerable.Repeat((PdfObject)new PdfInteger(size), inputs)]),
            ["BitsPerSample"] = new PdfInteger(8),
        });
        function.SetDecodedBytes(samples);
        array[2] = target.Space;
        array[3] = document.Objects.Add(function);
        Changed.Alternates++;
    }

    /// <summary>An image XObject in the target: decoded and written at 8 bits, or an Indexed image's palette converted.</summary>
    private void Image(PdfStream stream, PdfDictionary resources)
    {
        if (!done.Add(stream) || stream.Dictionary.GetBool("ImageMask") == true)
        {
            return;
        }

        PdfImage image = new(stream, resources, document);
        PdfObject? spaceObject = stream.Dictionary.Get("ColorSpace");
        ImageColorSpace? space = Read(spaceObject, resources);
        if (space is null)
        {
            // JPEG 2000 with a colour space of its own converts from its sRGB pixels; an unreadable space stays.
            if (spaceObject is not null || target is not GrayTarget && image.ImageCodec?.Value != "JPXDecode")
            {
                return;
            }

            space = ImageColorSpace.Read(PdfName.Get("DeviceRGB"), null, null);
        }
        else if (!target.Converts(space))
        {
            if (space.Family is ColorFamily.Separation or ColorFamily.Indexed && spaceObject is not null)
            {
                Alternate(spaceObject, resources);
            }

            return;
        }

        if (spaceObject?.Resolve() is PdfArray { Count: 4 } indexed && indexed.GetName(0)?.Value is "Indexed" or "I"
            && Read(indexed[1], resources) is ImageColorSpace baseSpace && Palette(indexed, baseSpace) is PdfArray palette)
        {
            stream.Dictionary["ColorSpace"] = palette;
            Changed.Images++;
            return;
        }

        try
        {
            bool colorKey = stream.Dictionary.Get("Mask") is PdfArray;
            PdfImageOptions options = new() { ApplyMasks = colorKey };
            byte[] alpha = colorKey ? Alpha(image) : [];
            byte[]? samples = target.ConvertImage(image, space, options, out int width, out int height);
            if (samples is null)
            {
                return;
            }

            PdfStream encoded = Encode(image, samples, width, height);
            foreach (string key in new[] { "Filter", "DecodeParms", "Decode", "Mask", "SMaskInData", "Length" })
            {
                stream.Dictionary.Remove(key);
            }

            foreach (PdfName key in encoded.Dictionary.Entries.Select(e => e.Key).ToList())
            {
                PdfObject value = encoded.Dictionary[key]!;
                encoded.Dictionary.Remove(key);
                stream.Dictionary[key] = value;
            }

            stream.Dictionary["ColorSpace"] = target.Space;
            stream.SetEncodedBytes(encoded.GetEncodedBytes());
            if (colorKey && alpha.Length == width * height)
            {
                PdfStream mask = ImageRecompressor.FlateStream(alpha, width, height, 1);
                mask.Dictionary["Type"] = PdfName.Get("XObject");
                mask.Dictionary["Subtype"] = PdfName.Get("Image");
                mask.Dictionary["ColorSpace"] = PdfName.Get("DeviceGray");
                stream.Dictionary["SMask"] = document.Objects.Add(mask);
            }

            Changed.Images++;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or Egami.EgamiException or ArgumentException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"An image could not be converted: {e.Message}"));
        }
    }

    /// <summary>A colour key mask as alpha, from the image decoded with its masks.</summary>
    private static byte[] Alpha(PdfImage image)
    {
        using Egami.Raster<Egami.Pixels.Rgba8> raster = image.ToRaster<Egami.Pixels.Rgba8>(new PdfImageOptions { ApplyMasks = true });
        byte[] alpha = new byte[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Egami.Pixels.Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                alpha[(y * raster.Width) + x] = row[x].A;
            }
        }

        return alpha;
    }

    /// <summary>
    /// Converted samples as an image stream: a JPEG photo stays a JPEG (CMYK or gray) at the quality it had, so the file
    /// does not grow; anything else is Flate.
    /// </summary>
    private PdfStream Encode(PdfImage image, byte[] samples, int width, int height)
    {
        if (image.ImageCodec?.Value == "DCTDecode" && target.Components is 1 or 4 && target is IccTarget)
        {
            int quality = JpegOptimizer.Quality(image.GetRawBytes()) ?? 85;
            using Egami.Raster raster = target.Components == 1 ? Raster<Egami.Pixels.Gray8>(samples, width, height) : Raster<Egami.Pixels.Cmyk8>(samples, width, height);
            return ImageRecompressor.Jpeg(raster, quality);
        }

        return ImageRecompressor.FlateStream(samples, width, height, target.Components);
    }

    private static Egami.Raster<TPixel> Raster<TPixel>(byte[] samples, int width, int height)
        where TPixel : unmanaged, Egami.Pixels.IPixelType<TPixel>
    {
        Egami.Raster<TPixel> raster = Egami.Raster.Create<TPixel>(width, height);
        Span<TPixel> all = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, TPixel>(samples.AsSpan());
        for (int y = 0; y < height; y++)
        {
            all.Slice(y * width, width).CopyTo(raster.Row(y));
        }

        return raster;
    }

    /// <summary>An Indexed space's palette in the target: <c>[/Indexed target hival lookup]</c>.</summary>
    private PdfArray? Palette(PdfArray indexed, ImageColorSpace baseSpace)
    {
        int hival = (int)(indexed.GetInt(2) ?? -1);
        byte[]? lookup = indexed[3].Resolve() switch
        {
            PdfString s => s.Bytes.ToArray(),
            PdfStream s => s.GetDecodedBytes(),
            _ => null,
        };
        int n = baseSpace.Components, m = target.Components;
        if (hival < 0 || lookup is null || lookup.Length < (hival + 1) * n)
        {
            return null;
        }

        double[] range = baseSpace.DefaultDecode(8);
        byte[] converted = new byte[(hival + 1) * m];
        double[] components = new double[n], output = new double[m];
        for (int i = 0; i <= hival; i++)
        {
            for (int c = 0; c < n; c++)
            {
                components[c] = range[2 * c] + (lookup[(i * n) + c] / 255.0 * (range[(2 * c) + 1] - range[2 * c]));
            }

            target.Convert(baseSpace, components, output);
            for (int c = 0; c < m; c++)
            {
                converted[(i * m) + c] = (byte)Math.Round(output[c] * 255);
            }
        }

        return [PdfName.Get("Indexed"), target.Space, new PdfInteger(hival), PdfString.FromBytes(converted, hex: true)];
    }

    private PdfInlineImage? InlineImage(PdfInlineImage inline, PdfDictionary resources)
    {
        PdfImage image = new(inline, resources);
        if (image.IsStencilMask)
        {
            return null;
        }

        try
        {
            if (Read(image.Parameters.Get("ColorSpace"), resources) is not ImageColorSpace space || !target.Converts(space))
            {
                return null;
            }

            byte[]? samples = target.ConvertImage(image, space, new PdfImageOptions { ApplyMasks = false }, out int width, out int height);
            if (samples is null)
            {
                return null;
            }

            PdfDictionary parameters = new()
            {
                ["W"] = new PdfInteger(width),
                ["H"] = new PdfInteger(height),
                ["CS"] = target.InlineSpace,
                ["BPC"] = new PdfInteger(8),
            };
            Changed.Images++;
            return new PdfInlineImage(parameters, samples);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or Egami.EgamiException or ArgumentException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedInlineImage, $"An inline image could not be converted: {e.Message}"));
            return null;
        }
    }

    /// <summary>A shading in the target: a sampled function in place of its function, or target vertex colours for a mesh.</summary>
    private void Shading(PdfObject value, PdfDictionary resources)
    {
        PdfDictionary? d = value switch
        {
            PdfStream s => s.Dictionary,
            PdfDictionary dictionary => dictionary,
            _ => null,
        };
        if (d is null || !done.Add(value) || Read(d.Get("ColorSpace"), resources) is not ImageColorSpace space)
        {
            return;
        }

        if (!target.Converts(space))
        {
            if (space.Family is ColorFamily.Separation && d.Get("ColorSpace") is PdfObject kept)
            {
                Alternate(kept, resources);
            }

            return;
        }

        if (d.Get<PdfArray>("Background") is PdfArray background && background.Count == space.Components)
        {
            d["Background"] = Numbers(target.Convert(space, [.. background.Select((_, i) => background.GetNumber(i) ?? 0)]));
        }

        int type = (int)(d.GetInt("ShadingType") ?? 0);
        if (d.Get("Function") is PdfObject function)
        {
            if (SampledFunction(function, space, type == 1 ? 2 : 1, d) is PdfStream sampled)
            {
                d["Function"] = document.Objects.Add(sampled);
                d["ColorSpace"] = target.Space;
                Changed.Shadings++;
            }
            else
            {
                warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, "A shading's function could not be read; it keeps its colour."));
            }

            return;
        }

        if (type is >= 4 and <= 7 && value is PdfStream mesh && Mesh(mesh, space, type))
        {
            d["ColorSpace"] = target.Space;
            Changed.Shadings++;
            return;
        }

        warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A type {type} shading could not be converted; it keeps its colour."));
    }

    /// <summary>A sampled function (type 0) giving the target colour of what the original function gives, over its domain.</summary>
    private PdfStream? SampledFunction(PdfObject function, ImageColorSpace space, int inputs, PdfDictionary shading)
    {
        PdfFunction[] parts;
        try
        {
            parts = function.Resolve() is PdfArray array ? [.. array.Select(PdfFunction.Read)] : [PdfFunction.Read(function)];
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }

        // The inputs the shading gives its function: its Domain, a mesh's parametric range from its Decode, else 0 to 1.
        PdfArray? given = shading.Get<PdfArray>("Domain") ?? (shading.GetInt("ShadingType") >= 4 && shading.Get<PdfArray>("Decode") is { Count: >= 6 } meshDecode
            ? [new PdfReal(meshDecode.GetNumber(4) ?? 0), new PdfReal(meshDecode.GetNumber(5) ?? 1)]
            : null);
        double[] domain = new double[2 * inputs];
        for (int k = 0; k < inputs; k++)
        {
            domain[2 * k] = given?.GetNumber(2 * k) ?? 0;
            domain[(2 * k) + 1] = given?.GetNumber((2 * k) + 1) ?? 1;
        }

        int size = inputs == 1 ? 256 : 33, n = target.Components;
        int count = (int)Math.Pow(size, inputs);
        byte[] samples = new byte[count * n];
        double[] input = new double[inputs];
        double[] output = new double[Math.Max(space.Components, parts.Sum(p => p.Outputs))];
        double[] converted = new double[n];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            for (int k = 0; k < inputs; k++)
            {
                int step = index % size;
                index /= size;
                input[k] = domain[2 * k] + (step * (domain[(2 * k) + 1] - domain[2 * k]) / (size - 1));
            }

            int at = 0;
            foreach (PdfFunction part in parts)
            {
                part.Evaluate(input, output.AsSpan(at, part.Outputs));
                at += part.Outputs;
            }

            target.Convert(space, output.AsSpan(0, space.Components), converted);
            for (int c = 0; c < n; c++)
            {
                samples[(i * n) + c] = (byte)Math.Round(converted[c] * 255);
            }
        }

        PdfStream sampled = new(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Domain"] = new PdfArray([.. domain.Select(v => (PdfObject)new PdfReal(v))]),
            ["Range"] = new PdfArray([.. Enumerable.Range(0, n).SelectMany(_ => new PdfObject[] { new PdfInteger(0), new PdfInteger(1) })]),
            ["Size"] = new PdfArray([.. Enumerable.Repeat((PdfObject)new PdfInteger(size), inputs)]),
            ["BitsPerSample"] = new PdfInteger(8),
        });
        sampled.SetDecodedBytes(samples);
        return sampled;
    }

    /// <summary>A mesh shading's vertex colours in the target, coordinates and flags kept bit for bit.</summary>
    private bool Mesh(PdfStream mesh, ImageColorSpace space, int type)
    {
        PdfDictionary d = mesh.Dictionary;
        int coordBits = (int)(d.GetInt("BitsPerCoordinate") ?? 0), componentBits = (int)(d.GetInt("BitsPerComponent") ?? 0);
        int flagBits = type == 5 ? 0 : (int)(d.GetInt("BitsPerFlag") ?? 0);
        double[] decode = [.. (d.Get<PdfArray>("Decode") ?? []).Select((_, i) => d.Get<PdfArray>("Decode")!.GetNumber(i) ?? 0)];
        int n = space.Components, m = target.Components;
        if (coordBits is < 1 or > 32 || componentBits is < 1 or > 16 || decode.Length < 4 + (2 * n))
        {
            return false;
        }

        byte[] data = mesh.GetDecodedBytes();
        Bits reader = new(data);
        Bits writer = new([]);
        double top = (1UL << componentBits) - 1;
        double[] c = new double[n], output = new double[m];
        void Color()
        {
            for (int i = 0; i < n; i++)
            {
                c[i] = decode[4 + (2 * i)] + (reader.Read(componentBits) * (decode[5 + (2 * i)] - decode[4 + (2 * i)]) / top);
            }

            target.Convert(space, c, output);
            for (int i = 0; i < m; i++)
            {
                writer.Write((ulong)Math.Round(output[i] * top), componentBits);
            }
        }

        int guard = 0;
        if (type is 4 or 5)
        {
            long vertexBits = flagBits + (2L * coordBits) + ((long)n * componentBits);
            while (reader.Remaining >= vertexBits && guard++ < (1 << 22))
            {
                if (flagBits > 0)
                {
                    writer.Write(reader.Read(flagBits), flagBits);
                }

                writer.Write(reader.Read(coordBits), coordBits);
                writer.Write(reader.Read(coordBits), coordBits);
                Color();
                reader.Align();
                writer.Align();
            }
        }
        else
        {
            int total = type == 7 ? 16 : 12;
            while (reader.Remaining >= flagBits && guard++ < (1 << 20))
            {
                ulong flag = reader.Read(flagBits);
                int points = flag == 0 ? total : total - 4, colors = flag == 0 ? 4 : 2;
                if (reader.Remaining < ((long)points * 2 * coordBits) + ((long)colors * n * componentBits))
                {
                    break;
                }

                writer.Write(flag, flagBits);
                for (int k = 0; k < 2 * points; k++)
                {
                    writer.Write(reader.Read(coordBits), coordBits);
                }

                for (int k = 0; k < colors; k++)
                {
                    Color();
                }

                reader.Align();
                writer.Align();
            }
        }

        d["Decode"] = new PdfArray([new PdfReal(decode[0]), new PdfReal(decode[1]), new PdfReal(decode[2]), new PdfReal(decode[3]),
            .. Enumerable.Range(0, m).SelectMany(_ => new PdfObject[] { new PdfInteger(0), new PdfInteger(1) })]);
        d.Remove("DecodeParms");
        d["Filter"] = PdfName.Get("FlateDecode");
        mesh.SetDecodedBytes(writer.ToArray());
        return true;
    }

    /// <summary>Big-endian bit fields read from or written to bytes.</summary>
    private sealed class Bits(byte[] data)
    {
        private readonly List<byte> written = [];
        private long position;

        public long Remaining => (8L * data.Length) - position;

        public ulong Read(int bits)
        {
            ulong value = 0;
            for (int i = 0; i < bits; i++)
            {
                long at = position >> 3;
                int bit = at < data.Length ? (data[at] >> (7 - (int)(position & 7))) & 1 : 0;
                value = (value << 1) | (uint)bit;
                position++;
            }

            return value;
        }

        public void Write(ulong value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if ((position & 7) == 0)
                {
                    written.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    written[^1] |= (byte)(0x80 >> (int)(position & 7));
                }

                position++;
            }
        }

        public void Align() => position = (position + 7) & ~7L;

        public byte[] ToArray() => [.. written];
    }
}
