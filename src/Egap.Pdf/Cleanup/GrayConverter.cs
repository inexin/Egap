using Egami;
using Egami.Pixels;

using Egap.Pdf.Images;
using Egap.Pdf.Optimizing;

namespace Egap.Pdf.Cleanup;

/// <summary>
/// Grayscale conversion (Phase 7 design 7.5 and Q14, Build Plan task 17): every colour the document paints becomes
/// DeviceGray, the luma of its sRGB colour (Rec. 601 weights), and vectors stay vectors. Content streams of pages, forms,
/// patterns, Type 3 glyphs, soft masks and annotation appearances have their colour operators rewritten (<c>rg</c>,
/// <c>k</c>, <c>sc</c> and <c>scn</c> in any space, Separation and DeviceN through their alternates); shadings get a sampled
/// gray function, or gray vertex colours for meshes; images are decoded and written as gray, Indexed images keep their
/// samples under a gray palette; annotations' colours become gray. Each object is converted once however often it is used.
/// </summary>
internal sealed class GrayConverter
{
    private readonly PdfDocument document;
    private readonly HashSet<PdfObject> done = new(ReferenceEqualityComparer.Instance);
    private readonly List<PdfWarning> warnings = [];

    private GrayConverter(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>Converts the whole document; what could not be converted, as warnings.</summary>
    public static IReadOnlyList<PdfWarning> Run(PdfDocument document)
    {
        GrayConverter converter = new(document);
        foreach (PdfPage page in document.Pages)
        {
            converter.Page(page);
        }

        return converter.warnings;
    }

    /// <summary>The gray a colour shows: the luma of its sRGB value, 0 to 1.</summary>
    internal static double Luma(ImageColorSpace space, ReadOnlySpan<double> components)
    {
        Span<double> rgb = stackalloc double[3];
        space.ToRgb(components, rgb);
        return Math.Clamp((0.299 * rgb[0]) + (0.587 * rgb[1]) + (0.114 * rgb[2]), 0, 1);
    }

    private void Page(PdfPage page)
    {
        PdfDictionary resources = page.Resources ?? [];
        try
        {
            PdfContent content = page.GetContent();
            if (Rewrite(content, resources) is PdfContent gray)
            {
                page.SetContent(gray);
            }
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A page's content could not be converted to gray: {e.Message}"));
        }

        Resources(page.Resources);
        foreach (PdfAnnotation annotation in page.Annotations)
        {
            GrayArray(annotation.Dictionary, "C");
            GrayArray(annotation.Dictionary, "IC");
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

    /// <summary>A colour array of three (RGB) or four (CMYK) numbers made one gray number.</summary>
    private static void GrayArray(PdfDictionary dictionary, string key)
    {
        if (dictionary.Get<PdfArray>(key) is not PdfArray color || color.Count is not (3 or 4) || color.Any(c => c.Resolve() is not (PdfInteger or PdfReal)))
        {
            return;
        }

        double[] c = [.. color.Select((_, i) => color.GetNumber(i) ?? 0)];
        ImageColorSpace space = ImageColorSpace.Read(PdfName.Get(c.Length == 3 ? "DeviceRGB" : "DeviceCMYK"), null, null);
        dictionary[key] = new PdfArray { new PdfReal(Luma(space, c)) };
    }

    /// <summary>Every resource an XObject, pattern, shading, soft mask or Type 3 font names.</summary>
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

        foreach (PdfDictionary state in (resources.Get<PdfDictionary>("ExtGState")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfDictionary>().ToList())
        {
            if (state.Get("SMask")?.Resolve() is PdfDictionary mask && done.Add(mask))
            {
                if (mask.Get("G")?.Resolve() is PdfStream group)
                {
                    Form(group);
                }

                if (mask.Get<PdfArray>("BC") is { Count: 3 or 4 })
                {
                    GrayArray(mask, "BC");
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

        if (form.Dictionary.Get<PdfDictionary>("Group") is PdfDictionary group && group.ContainsKey("CS"))
        {
            group["CS"] = PdfName.Get("DeviceGray");
        }

        Resources(form.Dictionary.Get<PdfDictionary>("Resources"));
    }

    private void Stream(PdfStream stream, PdfDictionary resources)
    {
        try
        {
            if (Rewrite(PdfContent.Read(stream), resources) is PdfContent gray)
            {
                stream.Dictionary.Remove("DecodeParms");
                stream.Dictionary["Filter"] = PdfName.Get("FlateDecode");
                stream.SetDecodedBytes(gray.ToBytes());
            }
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A content stream could not be converted to gray: {e.Message}"));
        }
    }

    /// <summary>The content with every colour operator in gray, or null when nothing changed.</summary>
    private PdfContent? Rewrite(PdfContent content, PdfDictionary resources)
    {
        PdfContent result = [];
        bool changed = false;
        ColorState state = new(Gray, Gray, null, null);
        Stack<ColorState> saved = new();
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
                case "rg" or "RG" or "k" or "K":
                    {
                        bool fill = op.Operator is "rg" or "k";
                        ImageColorSpace space = ImageColorSpace.Read(PdfName.Get(op.Operator is "rg" or "RG" ? "DeviceRGB" : "DeviceCMYK"), null, null);
                        result.Add(new PdfOperation(fill ? "g" : "G", [new PdfReal(Luma(space, Numbers(op)))]));
                        state = fill ? state with { Fill = Gray, FillPattern = null } : state with { Stroke = Gray, StrokePattern = null };
                        changed = true;
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
                            // A pattern keeps its space: the pattern itself is converted; an uncoloured one's base becomes gray.
                            ImageColorSpace? under = value.Resolve() is PdfArray { Count: > 1 } withBase ? ImageColorSpace.Read(withBase[1], resources, null) : null;
                            state = fill ? state with { FillPattern = under ?? Gray } : state with { StrokePattern = under ?? Gray };
                            if (under is not null && under.Family != ColorFamily.Gray)
                            {
                                PdfName patternGray = PdfName.Get("EgPatternGray");
                                if (resources.Get<PdfDictionary>("ColorSpace") is not PdfDictionary spaces)
                                {
                                    resources["ColorSpace"] = spaces = [];
                                }

                                spaces[patternGray] = new PdfArray { PdfName.Get("Pattern"), PdfName.Get("DeviceGray") };
                                result.Add(new PdfOperation(op.Operator, [patternGray]));
                                changed = true;
                                continue;
                            }

                            break;
                        }

                        ImageColorSpace read;
                        try
                        {
                            read = ImageColorSpace.Read(value, resources, null);
                        }
                        catch (Exception e) when (e is PdfException or InvalidDataException)
                        {
                            break;
                        }

                        state = fill ? state with { Fill = read, FillPattern = null } : state with { Stroke = read, StrokePattern = null };
                        if (read.Family == ColorFamily.Gray && read.Components == 1)
                        {
                            break;
                        }

                        // The space's initial colour: full ink for Separation and DeviceN, else zero.
                        double[] initial = new double[read.Components];
                        Array.Fill(initial, read.Family == ColorFamily.Separation ? 1 : 0);
                        result.Add(new PdfOperation(op.Operator, [PdfName.Get("DeviceGray")]));
                        result.Add(new PdfOperation(fill ? "sc" : "SC", [new PdfReal(Luma(read, initial))]));
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
                            if (pattern.Family != ColorFamily.Gray && op.Operands.Count > 1 && op.Operands[^1] is PdfName patternName)
                            {
                                double[] tint = [.. op.Operands.Take(op.Operands.Count - 1).Select(o => o is PdfInteger i ? i.Value : o is PdfReal r ? r.Value : 0)];
                                result.Add(new PdfOperation(op.Operator, [new PdfReal(Luma(pattern, tint)), patternName]));
                                changed = true;
                                continue;
                            }

                            break;
                        }

                        if (space.Family == ColorFamily.Gray && space.Components == 1)
                        {
                            break;
                        }

                        result.Add(new PdfOperation(fill ? "sc" : "SC", [new PdfReal(Luma(space, Numbers(op)))]));
                        changed = true;
                        continue;
                    }

                case "BI" when op is PdfInlineImage inline:
                    if (InlineImage(inline, resources) is PdfInlineImage grayImage)
                    {
                        result.Add(grayImage);
                        changed = true;
                        continue;
                    }

                    break;
            }

            result.Add(op);
        }

        return changed ? result : null;
    }

    private static readonly ImageColorSpace Gray = ImageColorSpace.Read(PdfName.Get("DeviceGray"), null, null);

    private static double[] Numbers(PdfOperation op)
    {
        return [.. op.Operands.Select(o => o switch { PdfInteger i => (double)i.Value, PdfReal r => r.Value, _ => 0 })];
    }

    private readonly record struct ColorState(ImageColorSpace Fill, ImageColorSpace Stroke, ImageColorSpace? FillPattern, ImageColorSpace? StrokePattern);

    /// <summary>An image XObject in gray: decoded and written as 8-bit gray, or an Indexed image's palette made gray.</summary>
    private void Image(PdfStream stream, PdfDictionary resources)
    {
        if (!done.Add(stream) || stream.Dictionary.GetBool("ImageMask") == true)
        {
            return;
        }

        PdfImage image = new(stream, resources, document);
        ImageColorSpace space;
        try
        {
            space = ImageColorSpace.Read(stream.Dictionary.Get("ColorSpace"), resources, null);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            // JPEG 2000 with a colour space of its own.
            space = Gray;
            if (stream.Dictionary.Get("ColorSpace") is not null)
            {
                return;
            }
        }

        if (stream.Dictionary.Get("ColorSpace") is not null && space.Family == ColorFamily.Gray && space.Components == 1)
        {
            return;
        }

        if (stream.Dictionary.Get("ColorSpace")?.Resolve() is PdfArray { Count: 4 } indexed && indexed.GetName(0)?.Value is "Indexed" or "I"
            && ImageColorSpace.Read(indexed[1], resources, null) is ImageColorSpace baseSpace && IndexedPalette(indexed, baseSpace) is PdfArray grayPalette)
        {
            stream.Dictionary["ColorSpace"] = grayPalette;
            return;
        }

        try
        {
            using Raster<Rgba8> raster = image.ToRaster<Rgba8>(new PdfImageOptions { ApplyMasks = stream.Dictionary.Get("Mask") is PdfArray });
            byte[] gray = new byte[raster.Width * raster.Height];
            byte[]? alpha = stream.Dictionary.Get("Mask") is PdfArray ? new byte[gray.Length] : null;
            for (int y = 0; y < raster.Height; y++)
            {
                Span<Rgba8> row = raster.Row(y);
                for (int x = 0; x < raster.Width; x++)
                {
                    Rgba8 p = row[x];
                    gray[(y * raster.Width) + x] = (byte)Math.Round((0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B));
                    if (alpha is not null)
                    {
                        alpha[(y * raster.Width) + x] = p.A;
                    }
                }
            }

            PdfStream encoded = ImageRecompressor.FlateStream(gray, raster.Width, raster.Height, 1);
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

            stream.Dictionary["ColorSpace"] = PdfName.Get("DeviceGray");
            stream.SetEncodedBytes(encoded.GetEncodedBytes());
            if (alpha is not null)
            {
                PdfStream mask = ImageRecompressor.FlateStream(alpha, raster.Width, raster.Height, 1);
                mask.Dictionary["Type"] = PdfName.Get("XObject");
                mask.Dictionary["Subtype"] = PdfName.Get("Image");
                mask.Dictionary["ColorSpace"] = PdfName.Get("DeviceGray");
                stream.Dictionary["SMask"] = document.Objects.Add(mask);
            }
        }
        catch (PdfException e)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"An image could not be converted to gray: {e.Message}"));
        }
    }

    /// <summary>An Indexed space's palette as gray entries: <c>[/Indexed /DeviceGray hival lookup]</c>.</summary>
    private static PdfArray? IndexedPalette(PdfArray indexed, ImageColorSpace baseSpace)
    {
        int hival = (int)(indexed.GetInt(2) ?? -1);
        byte[]? lookup = indexed[3].Resolve() switch
        {
            PdfString s => s.Bytes.ToArray(),
            PdfStream s => s.GetDecodedBytes(),
            _ => null,
        };
        int n = baseSpace.Components;
        if (hival < 0 || lookup is null || lookup.Length < (hival + 1) * n)
        {
            return null;
        }

        double[] range = baseSpace.DefaultDecode(8);
        byte[] gray = new byte[hival + 1];
        double[] components = new double[n];
        for (int i = 0; i <= hival; i++)
        {
            for (int c = 0; c < n; c++)
            {
                components[c] = range[2 * c] + (lookup[(i * n) + c] / 255.0 * (range[(2 * c) + 1] - range[2 * c]));
            }

            gray[i] = (byte)Math.Round(Luma(baseSpace, components) * 255);
        }

        return [PdfName.Get("Indexed"), PdfName.Get("DeviceGray"), new PdfInteger(hival), PdfString.FromBytes(gray, hex: true)];
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
            ImageColorSpace space = ImageColorSpace.Read(image.Parameters.Get("ColorSpace"), resources, null);
            if (space.Family == ColorFamily.Gray && space.Components == 1)
            {
                return null;
            }

            using Raster<Rgba8> raster = image.ToRaster<Rgba8>(new PdfImageOptions { ApplyMasks = false });
            byte[] gray = new byte[raster.Width * raster.Height];
            for (int y = 0; y < raster.Height; y++)
            {
                Span<Rgba8> row = raster.Row(y);
                for (int x = 0; x < raster.Width; x++)
                {
                    gray[(y * raster.Width) + x] = (byte)Math.Round((0.299 * row[x].R) + (0.587 * row[x].G) + (0.114 * row[x].B));
                }
            }

            PdfDictionary parameters = new()
            {
                ["W"] = new PdfInteger(raster.Width),
                ["H"] = new PdfInteger(raster.Height),
                ["CS"] = PdfName.Get("G"),
                ["BPC"] = new PdfInteger(8),
            };
            return new PdfInlineImage(parameters, gray);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.MalformedInlineImage, $"An inline image could not be converted to gray: {e.Message}"));
            return null;
        }
    }

    /// <summary>A shading in gray: a sampled gray function in place of its function, or gray vertex colours for a mesh.</summary>
    private void Shading(PdfObject value, PdfDictionary resources)
    {
        PdfDictionary? d = value switch
        {
            PdfStream s => s.Dictionary,
            PdfDictionary dictionary => dictionary,
            _ => null,
        };
        if (d is null || !done.Add(value))
        {
            return;
        }

        ImageColorSpace space;
        try
        {
            space = ImageColorSpace.Read(d.Get("ColorSpace"), resources, null);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return;
        }

        if (space.Family == ColorFamily.Gray && space.Components == 1)
        {
            return;
        }

        if (d.Get<PdfArray>("Background") is PdfArray background && background.Count == space.Components)
        {
            d["Background"] = new PdfArray { new PdfReal(Luma(space, [.. background.Select((_, i) => background.GetNumber(i) ?? 0)])) };
        }

        int type = (int)(d.GetInt("ShadingType") ?? 0);
        if (d.Get("Function") is PdfObject function)
        {
            if (GrayFunction(function, space, type == 1 ? 2 : 1, d) is PdfStream sampled)
            {
                d["Function"] = document.Objects.Add(sampled);
                d["ColorSpace"] = PdfName.Get("DeviceGray");
            }
            else
            {
                warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, "A shading's function could not be read; it keeps its colour."));
            }

            return;
        }

        if (type is >= 4 and <= 7 && value is PdfStream mesh && GrayMesh(mesh, space, type))
        {
            d["ColorSpace"] = PdfName.Get("DeviceGray");
            return;
        }

        warnings.Add(new PdfWarning(PdfWarningCode.MalformedContent, $"A type {type} shading could not be converted to gray; it keeps its colour."));
    }

    /// <summary>A sampled function (type 0) giving the gray of the colour the original function gives, over its domain.</summary>
    private static PdfStream? GrayFunction(PdfObject function, ImageColorSpace space, int inputs, PdfDictionary shading)
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
        int size = inputs == 1 ? 256 : 33;
        byte[] samples = new byte[(int)Math.Pow(size, inputs)];
        double[] input = new double[inputs];
        double[] output = new double[Math.Max(space.Components, parts.Sum(p => p.Outputs))];
        for (int i = 0; i < samples.Length; i++)
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

            samples[i] = (byte)Math.Round(Luma(space, output.AsSpan(0, space.Components)) * 255);
        }

        PdfStream sampled = new(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Domain"] = new PdfArray([.. domain.Select(v => (PdfObject)new PdfReal(v))]),
            ["Range"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
            ["Size"] = new PdfArray([.. Enumerable.Repeat((PdfObject)new PdfInteger(size), inputs)]),
            ["BitsPerSample"] = new PdfInteger(8),
        });
        sampled.SetDecodedBytes(samples);
        return sampled;
    }

    /// <summary>A mesh shading's vertex colours as one gray component each, coordinates and flags kept bit for bit.</summary>
    private static bool GrayMesh(PdfStream mesh, ImageColorSpace space, int type)
    {
        PdfDictionary d = mesh.Dictionary;
        int coordBits = (int)(d.GetInt("BitsPerCoordinate") ?? 0), componentBits = (int)(d.GetInt("BitsPerComponent") ?? 0);
        int flagBits = type == 5 ? 0 : (int)(d.GetInt("BitsPerFlag") ?? 0);
        double[] decode = [.. (d.Get<PdfArray>("Decode") ?? []).Select((_, i) => d.Get<PdfArray>("Decode")!.GetNumber(i) ?? 0)];
        int n = space.Components;
        if (coordBits is < 1 or > 32 || componentBits is < 1 or > 16 || decode.Length < 4 + (2 * n))
        {
            return false;
        }

        byte[] data = mesh.GetDecodedBytes();
        Bits reader = new(data);
        Bits writer = new([]);
        double top = (1UL << componentBits) - 1;
        double[] c = new double[n];
        void Color()
        {
            for (int i = 0; i < n; i++)
            {
                c[i] = decode[4 + (2 * i)] + (reader.Read(componentBits) * (decode[5 + (2 * i)] - decode[4 + (2 * i)]) / top);
            }

            writer.Write((ulong)Math.Round(Luma(space, c) * top), componentBits);
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

        d["Decode"] = new PdfArray { new PdfReal(decode[0]), new PdfReal(decode[1]), new PdfReal(decode[2]), new PdfReal(decode[3]), new PdfInteger(0), new PdfInteger(1) };
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
