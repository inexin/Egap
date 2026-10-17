using Egap.Pdf.Filters;
using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;
using Egap.Pdf.Trees;

namespace Egap.Pdf.Fuzz;

/// <summary>The fuzz targets. Each reads one input; see Program.cs for what counts as a crash.</summary>
internal static class Targets
{
    /// <summary>Counts kept by the streams target, which replay prints, to see what a corpus run did.</summary>
    public static Dictionary<string, int> Counts { get; } = [];

    public static IEnumerable<string> Names => ["lexer", "parser", "filters", "streams", "xref", "document", "rewrite", "incremental", "trees", "pages", "pageedits", "navigation", "outlines", "xmp", "metadata", "attachments", "layers", "annotations", "forms", "flatten", "merge", "content", "walker", "fonts", "images", "functions", "shadings", "render", "signatures", "cms", "revocation"];

    public static Action<Stream> Get(string name)
    {
        return name switch
        {
            "lexer" => Lexer,
            "parser" => Parser,
            "filters" => Filters,
            "streams" => Streams,
            "xref" => Xref,
            "document" => Document,
            "rewrite" => Rewrite,
            "incremental" => Incremental,
            "trees" => Trees,
            "pages" => Pages,
            "pageedits" => PageEdits,
            "navigation" => Navigation,
            "outlines" => Outlines,
            "xmp" => Xmp,
            "metadata" => Metadata,
            "attachments" => Attachments,
            "layers" => Layers,
            "annotations" => Annotations,
            "forms" => Forms,
            "flatten" => Flatten,
            "merge" => Merge,
            "content" => Content,
            "walker" => Walker,
            "fonts" => Fonts,
            "images" => Images,
            "functions" => Functions,
            "shadings" => Shadings,
            "render" => Render,
            "signatures" => Signatures,
            "cms" => Cms,
            "revocation" => Revocation,
            "structure" => Structure,
            "pdfa" => PdfA,
            "linearize" => Linearize,
            "hocr" => Hocr,
            "alto" => Alto,
            "layout" => Layout,
            _ => throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name)),
        };
    }

    /// <summary>Fails the run with a message when a Phase 5 invariant does not hold.</summary>
    private static void Check(bool ok, string what)
    {
        if (!ok)
        {
            throw new InvalidOperationException("Broken invariant: " + what + ".");
        }
    }

    private static byte[] ReadAll(Stream input)
    {
        using MemoryStream copy = new();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// A PDF function (Phase 4 task 7) built from the bytes: the first picks the type, the next few its sizes, and the rest is
    /// a type 4 program or a type 0 function's samples; types 2 and 3 take their numbers from the bytes. Reading may throw
    /// only <see cref="InvalidDataException"/>; evaluating and sampling must never throw and must give finite numbers.
    /// </summary>
    private static void Functions(Stream input)
    {
        byte[] bytes = ReadAll(input);
        if (bytes.Length < 6)
        {
            return;
        }

        int inputs = 1 + (bytes[1] % 3), outputs = 1 + (bytes[2] % 4);
        PdfArray domain = [], range = [];
        for (int i = 0; i < inputs; i++)
        {
            domain.Add(new PdfReal(-(bytes[3] % 4)));
            domain.Add(new PdfReal(1 + (bytes[4] % 3)));
        }

        for (int i = 0; i < outputs; i++)
        {
            range.Add(new PdfReal(-2));
            range.Add(new PdfReal(2));
        }

        byte[] rest = bytes[6..];
        PdfDictionary dictionary = new() { ["FunctionType"] = new PdfInteger(bytes[0] % 5), ["Domain"] = domain, ["Range"] = range };
        PdfObject value = dictionary;
        switch (bytes[0] % 5)
        {
            case 0:
                PdfArray size = [];
                for (int i = 0; i < inputs; i++)
                {
                    size.Add(new PdfInteger(1 + ((bytes[5] >> (2 * i)) & 7)));
                }

                dictionary["Size"] = size;
                dictionary["BitsPerSample"] = new PdfInteger(SampleBits[bytes[4] % 8]);
                value = StreamWith(dictionary, rest);
                break;
            case 2:
                dictionary["C0"] = Numbers(rest, outputs, 0);
                dictionary["C1"] = Numbers(rest, outputs, outputs);
                dictionary["N"] = new PdfReal((bytes[5] - 128) / 16.0);
                break;
            case 3:
                PdfDictionary part = new() { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["C0"] = Numbers(rest, outputs, 0), ["C1"] = Numbers(rest, outputs, outputs), ["N"] = new PdfInteger(1 + (bytes[5] % 3)) };
                int parts = 1 + (bytes[5] % 4);
                PdfArray functions = [], bounds = [], encode = [];
                for (int i = 0; i < parts; i++)
                {
                    functions.Add(part.Clone());
                    encode.Add(new PdfReal(rest.Length > i ? rest[i] / 255.0 : 0));
                    encode.Add(new PdfReal(rest.Length > i + 1 ? rest[i + 1] / 255.0 : 1));
                    if (i > 0)
                    {
                        bounds.Add(new PdfReal(rest.Length > i + 2 ? rest[i + 2] / 128.0 : 0.5));
                    }
                }

                dictionary["Functions"] = functions;
                dictionary["Bounds"] = bounds;
                dictionary["Encode"] = encode;
                break;
            default:
                value = StreamWith(dictionary, rest);
                break;
        }

        Egap.Pdf.Images.PdfFunction function;
        try
        {
            function = Egap.Pdf.Images.PdfFunction.Read(value);
        }
        catch (InvalidDataException)
        {
            return;
        }

        double[] arguments = new double[inputs];
        double[] results = new double[Math.Max(function.Outputs, outputs)];
        for (int step = 0; step < 16; step++)
        {
            for (int i = 0; i < inputs; i++)
            {
                arguments[i] = step == 15 ? double.NaN : ((step * 7) + (i * 3)) % 11 / 3.0 - 1;
            }

            function.Evaluate(arguments, results);
            if (results.Any(r => !double.IsFinite(r) && !double.IsNaN(r)))
            {
                throw new InvalidOperationException("A function gave an infinite output.");
            }
        }

        if (function.Inputs == 1 && function.Outputs > 0)
        {
            _ = function.Sample(64, function.Outputs);
        }
    }

    private static readonly int[] SampleBits = [1, 2, 4, 8, 12, 16, 24, 32];

    /// <summary>
    /// PDF bytes (Phase 4 task 17): the first three pages rendered at a small size, leniently with tight limits, then onto a
    /// picture recorder. A lenient render may throw only <see cref="PdfLimitException"/>.
    /// </summary>
    private static void Render(Stream input)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(ReadAll(input), new PdfReadOptions { MaxDecodedStreamBytes = 16 << 20, MaxObjects = 100_000, MaxNestingDepth = 32 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            PdfRenderOptions options = new() { Dpi = 18, MaxOutputPixels = 4_000_000, MaxOperations = 200_000, MaxImagePixels = 4_000_000, ImageCacheLimit = 16 << 20 };
            foreach (PdfPage page in pdf.Pages.Take(3))
            {
                try
                {
                    using Egami.Raster<Egami.Pixels.Rgba8> raster = page.Render(options);
                    Egami.Drawing.PictureRecorder recorder = new(new Egami.Drawing.RectF(0, 0, 100, 100));
                    page.RenderTo(recorder, options with { FitWidth = 100, FitHeight = 100 });
                    _ = recorder.Finish();
                }
                catch (PdfLimitException)
                {
                }
            }
        }
    }

    private static readonly int[] CoordinateBits = [1, 2, 4, 8, 12, 16, 24, 32];

    /// <summary>
    /// A shading built from the bytes (Phase 4 task 13): the first picks the type (1 to 7) and the colour space, the next few
    /// the bit counts, a function or not, and extend flags; the rest is a mesh's data or a type 4 function program. It is drawn
    /// with sh and as a pattern fill onto a 32 by 32 raster, which must never throw: a shading that cannot be read is a warning.
    /// </summary>
    private static void Shadings(Stream input)
    {
        byte[] bytes = ReadAll(input);
        if (bytes.Length < 8)
        {
            return;
        }

        int type = 1 + (bytes[0] % 7);
        string space = (bytes[0] >> 4 & 3) switch { 0 => "DeviceGray", 1 => "DeviceRGB", 2 => "DeviceCMYK", _ => "Lab" };
        int components = space switch { "DeviceGray" => 1, "DeviceCMYK" => 4, _ => 3 };
        bool parametric = (bytes[1] & 1) != 0 || type <= 3;
        byte[] rest = bytes[8..];
        using PdfDocument pdf = PdfDocument.Create();
        PdfDictionary dictionary = new() { ["ShadingType"] = new PdfInteger(type), ["ColorSpace"] = PdfName.Get(space) };
        PdfStream program = StreamWith(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(4),
            ["Domain"] = type == 1 ? new PdfArray { new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1) } : new PdfArray { new PdfInteger(0), new PdfInteger(1) },
            ["Range"] = new PdfArray([.. Enumerable.Range(0, 2 * components).Select(i => (PdfObject)new PdfInteger(i % 2))]),
        }, type <= 3 ? rest : "{ dup dup }"u8.ToArray());
        if (parametric)
        {
            dictionary["Function"] = pdf.Objects.Add(program);
        }

        if (type <= 3)
        {
            dictionary["Coords"] = new PdfArray([.. Enumerable.Range(0, 6).Select(i => (PdfObject)new PdfReal((bytes[2 + i] - 64) / 4.0))]);
            dictionary["Extend"] = new PdfArray { PdfBoolean.Get((bytes[1] & 2) != 0), PdfBoolean.Get((bytes[1] & 4) != 0) };
            dictionary["Matrix"] = new PdfArray { new PdfReal(bytes[2] / 16.0), new PdfInteger(0), new PdfInteger(0), new PdfReal(bytes[3] / 16.0), new PdfInteger(0), new PdfInteger(0) };
        }
        else
        {
            dictionary["BitsPerCoordinate"] = new PdfInteger(CoordinateBits[bytes[2] % 8]);
            dictionary["BitsPerComponent"] = new PdfInteger(SampleBits[bytes[3] % 6]);
            dictionary["BitsPerFlag"] = new PdfInteger(2 << (bytes[4] % 3));
            dictionary["VerticesPerRow"] = new PdfInteger(bytes[5] % 6);
            PdfArray decode = [new PdfInteger(0), new PdfInteger(32), new PdfInteger(0), new PdfInteger(32)];
            for (int i = 0; i < (parametric ? 1 : components); i++)
            {
                decode.Add(new PdfInteger(0));
                decode.Add(new PdfInteger(1));
            }

            dictionary["Decode"] = decode;
        }

        PdfObject shading = type <= 3 ? pdf.Objects.Add(dictionary) : pdf.Objects.Add(StreamWith(dictionary, rest));
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 32, 32));
        page.Resources = new PdfDictionary
        {
            ["Shading"] = new PdfDictionary { ["S"] = shading },
            ["Pattern"] = new PdfDictionary { ["P"] = new PdfDictionary { ["PatternType"] = new PdfInteger(2), ["Shading"] = shading } },
        };
        page.SetContent(PdfContent.Parse("/S sh /Pattern cs /P scn 4 4 24 24 re f"u8));
        using Egami.Raster<Egami.Pixels.Rgba8> raster = page.Render();
    }

    private static PdfArray Numbers(byte[] bytes, int count, int offset)
    {
        PdfArray array = [];
        for (int i = 0; i < count; i++)
        {
            array.Add(new PdfReal(offset + i < bytes.Length ? (bytes[offset + i] - 128) / 64.0 : 0));
        }

        return array;
    }

    private static PdfStream StreamWith(PdfDictionary dictionary, byte[] data)
    {
        PdfStream stream = new(dictionary);
        stream.SetDecodedBytes(data, compress: false);
        return stream;
    }

    /// <summary>
    /// PDF bytes: every image on the first 10 pages listed and decoded with masks and without, colour converted and kept;
    /// decoding may fail only with <see cref="PdfException"/>.
    /// </summary>
    private static void Images(Stream input)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(ReadAll(input), new PdfReadOptions { MaxDecodedStreamBytes = 16 << 20, MaxObjects = 100_000, MaxNestingDepth = 32 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                foreach (PdfPage page in pdf.Pages.Take(10))
                {
                    foreach (PdfPlacedImage placed in page.GetImages().Take(20))
                    {
                        if ((long)placed.Image.Width * placed.Image.Height > 4_000_000)
                        {
                            continue;
                        }

                        foreach (PdfImageOptions options in (PdfImageOptions[])[PdfImageOptions.Default, new PdfImageOptions { ApplyMasks = false, ConvertColor = false }])
                        {
                            try
                            {
                                using Egami.Raster raster = placed.Image.ToRaster(options);
                                if (raster.Width <= 0 || raster.Height <= 0)
                                {
                                    throw new InvalidOperationException("A decoded image has no pixels.");
                                }
                            }
                            catch (PdfException)
                            {
                            }
                        }

                        _ = placed.Image.GetRawBytes();
                    }
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>
    /// A font program or CMap: the first byte picks what the rest is (a Type 1 program, a bare CFF font, a TrueType or
    /// OpenType font, a ToUnicode CMap, or a composite font's encoding CMap). The font must read without throwing: its
    /// typeface loaded or a warning given, every code's glyph, outline, width and text asked for.
    /// </summary>
    private static void Fonts(Stream input)
    {
        byte[] bytes = ReadAll(input);
        if (bytes.Length == 0)
        {
            return;
        }

        using PdfDocument pdf = PdfDocument.Create();
        PdfStream data = new();
        data.SetDecodedBytes(bytes.AsSpan(1));
        PdfReference stream = pdf.Objects.Add(data);
        PdfDictionary font = new() { ["Type"] = PdfName.Get("Font"), ["BaseFont"] = PdfName.Get("Fuzz") };
        PdfDictionary descriptor = new() { ["Type"] = PdfName.Get("FontDescriptor"), ["FontName"] = PdfName.Get("Fuzz"), ["Flags"] = new PdfInteger(bytes[0] >> 4) };
        switch (bytes[0] % 5)
        {
            case 0:
                font["Subtype"] = PdfName.Get("Type1");
                descriptor["FontFile"] = stream;
                break;
            case 1:
                font["Subtype"] = PdfName.Get("Type1");
                data.Dictionary["Subtype"] = PdfName.Get("Type1C");
                descriptor["FontFile3"] = stream;
                break;
            case 2:
                font["Subtype"] = PdfName.Get("TrueType");
                descriptor["FontFile2"] = stream;
                break;
            case 3:
                font["Subtype"] = PdfName.Get("Type1");
                font["BaseFont"] = PdfName.Get("Helvetica");
                font["ToUnicode"] = stream;
                break;
            default:
                font["Subtype"] = PdfName.Get("Type0");
                font["Encoding"] = stream;
                font["DescendantFonts"] = new PdfArray { new PdfDictionary { ["Subtype"] = PdfName.Get("CIDFontType0"), ["W"] = new PdfArray { new PdfInteger(0), new PdfInteger(65535), new PdfInteger(500) } } };
                break;
        }

        if (bytes[0] % 5 is 0 or 1 or 2)
        {
            font["FontDescriptor"] = descriptor;
        }

        PdfFont pdfFont = PdfFont.FromDictionary(font);
        Egami.Fonts.Typeface? face = pdfFont.GetTypeface();
        if (face is null && pdfFont.IsEmbedded && pdfFont.Warnings.Count == 0)
        {
            throw new InvalidOperationException("An embedded program that did not load gave no warning.");
        }

        _ = pdfFont.Flags | (PdfFontFlags)(int)pdfFont.Ascent;
        foreach (int code in pdfFont.GetCodes(bytes.AsSpan(1, Math.Min(bytes.Length - 1, 512))).Concat(Enumerable.Range(0, 256)))
        {
            _ = pdfFont.GetWidth(code);
            _ = pdfFont.ToUnicode(code);
            _ = pdfFont.GetCid(code);
            if (pdfFont.GetGlyphId(code) is ushort glyph && face is not null && glyph < face.GlyphCount)
            {
                face.GetGlyphOutline(glyph, NullOutline.Instance);
                _ = face.GetAdvance(glyph);
            }
        }
    }

    /// <summary>An outline sink that keeps nothing.</summary>
    private sealed class NullOutline : Egami.Fonts.IGlyphOutlineSink
    {
        public static readonly NullOutline Instance = new();

        public void MoveTo(float x, float y)
        {
        }

        public void LineTo(float x, float y)
        {
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
        }

        public void Close()
        {
        }
    }

    /// <summary>
    /// PDF bytes: the first 20 pages walked with every hook counting what it sees, leniently (must not throw except at a
    /// limit) and strictly (may throw only <see cref="PdfException"/>). Marked content must be balanced when each page ends.
    /// </summary>
    /// <summary>
    /// Egap 3.6 task 17: the first 5 pages analysed by layout analysis, built into a document in Flow and Exact mode and saved
    /// as DOCX, then the file auto-tagged and rewritten; the tagged file must reopen and draw as many glyphs as before.
    /// </summary>
    private static void Layout(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 16 << 20, MaxObjects = 100_000, MaxNestingDepth = 32 };
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, options);
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                while (pdf.Pages.Count > 5)
                {
                    pdf.Pages.RemoveAt(pdf.Pages.Count - 1);
                }

                foreach (Egap.Pdf.Layout.PdfPageLayout page in Egap.Pdf.Layout.PdfLayoutAnalyzer.Analyze(pdf))
                {
                    Count("layout pages");
                    Count(page.FromTags ? "layout pages from tags" : "layout pages from geometry");
                }

                foreach (Egap.Pdf.Layout.ReconstructionMode mode in (Egap.Pdf.Layout.ReconstructionMode[])[Egap.Pdf.Layout.ReconstructionMode.Flow, Egap.Pdf.Layout.ReconstructionMode.Exact])
                {
                    Egap.Documents.Document document = Egap.Pdf.Layout.PdfToDocument.Convert(pdf, new Egap.Pdf.Layout.PdfToDocumentOptions { Mode = mode, FigureResolution = 24 });
                    Egap.Documents.DocxFormat.Save(document, Stream.Null);
                }

                List<int> glyphs = [.. Enumerable.Range(0, pdf.Pages.Count).Select(i => Egap.Pdf.Layout.PageReader.Read(pdf, i).Glyphs.Count)];
                if (Egap.Pdf.Layout.PdfAutoTagger.Tag(pdf).Tagged)
                {
                    using MemoryStream saved = new();
                    pdf.Save(saved, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
                    using PdfDocument tagged = PdfDocument.Open(saved.ToArray(), options);
                    List<int> after = [.. Enumerable.Range(0, tagged.Pages.Count).Select(i => Egap.Pdf.Layout.PageReader.Read(tagged, i).Glyphs.Count)];
                    if (!after.SequenceEqual(glyphs))
                    {
                        throw new InvalidOperationException($"Tagging changed the glyphs drawn: {string.Join(",", glyphs)} became {string.Join(",", after)}.");
                    }

                    Count("layout files tagged");
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    private static void Walker(Stream input)
    {
        byte[] bytes = ReadAll(input);
        foreach (bool strict in (bool[])[false, true])
        {
            PdfDocument pdf;
            try
            {
                pdf = PdfDocument.Open(bytes, new PdfReadOptions { Strict = strict, MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000, MaxNestingDepth = 32 });
            }
            catch (PdfException)
            {
                continue;
            }

            using (pdf)
            {
                try
                {
                    foreach (PdfPage page in pdf.Pages.Take(20))
                    {
                        CountingWalker walker = new();
                        walker.Walk(page);
                        if (walker.MarkedContent.Count != 0)
                        {
                            throw new InvalidOperationException("Marked content is still open after the page.");
                        }
                    }
                }
                catch (PdfLimitException)
                {
                }
                catch (PdfException) when (strict)
                {
                }
            }
        }
    }

    /// <summary>A walker that counts what it sees and measures every path.</summary>
    private sealed class CountingWalker : PdfContentWalker
    {
        public int Events { get; private set; }

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            _ = path.GetBounds(State.Matrix);
            Events++;
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            Events++;
        }

        protected override void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
        {
            Events++;
        }

        protected override void OnShading(PdfObject? shading, PdfName name)
        {
            Events++;
        }
    }

    /// <summary>
    /// Reads the input as content stream bytes leniently (must not throw except at a limit) and strictly (may throw only
    /// <see cref="PdfException"/>). The operations written back compactly must read back strictly, without a warning, to the
    /// same operations, and writing those again must give the same bytes.
    /// </summary>
    private static void Content(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxNestingDepth = 64 };
        PdfContent content;
        try
        {
            content = PdfContent.Parse(bytes, options);
        }
        catch (PdfLimitException)
        {
            return;
        }

        try
        {
            PdfContent.Parse(bytes, options with { Strict = true });
        }
        catch (PdfException)
        {
        }

        // Inline image data that holds "EI", or ends in white space, cannot be told from its end without a length; data of
        // another length than its parameters give is read by that length once written back; and an operator made of control
        // or non-ASCII bytes after an image looks like more image data. That is the syntax's ambiguity (8.9.7), not ours, so
        // such content is not round-tripped.
        if (content.OfType<PdfInlineImage>().Any(Ambiguous)
            || (content.Any(o => o is PdfInlineImage) && content.Any(o => o.Operator.Any(c => c is < '!' or > '~'))))
        {
            return;
        }

        // Operations with the wrong number of operands are kept as they are, so they warn again, as does an inline image whose
        // data is not as long as its parameters say; nothing else may.
        byte[] written = content.ToBytes();
        PdfContent again = PdfContent.Parse(written, options);
        if (!Egap.Pdf.Content.ContentEquality.Same(content, again))
        {
            int at = Enumerable.Range(0, Math.Min(content.Count, again.Count)).FirstOrDefault(i => !Egap.Pdf.Content.ContentEquality.Same(content[i], again[i]), Math.Min(content.Count, again.Count));
            throw new InvalidOperationException($"Content written back reads as different operations, from #{at}: {Describe(content, at)} became {Describe(again, at)}.");
        }

        if (again.Warnings.Any(w => w.Code is not (PdfWarningCode.WrongOperandCount or PdfWarningCode.MalformedInlineImage) || w.Message.Contains("no EI", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Content written back reads with new warnings: {string.Join("; ", again.Warnings.Select(w => w.Message))}");
        }

        if (!written.AsSpan().SequenceEqual(again.ToBytes()))
        {
            throw new InvalidOperationException("Content written twice gives different bytes.");
        }

        static string Describe(PdfContent content, int index)
        {
            return index >= content.Count ? "nothing"
                : content[index] is PdfInlineImage image ? $"BI {string.Join(' ', image.Parameters.Keys.Select(k => k.Value))} [{Convert.ToHexString(image.Data)}]"
                : content[index].ToString();
        }

        static bool Ambiguous(PdfInlineImage image)
        {
            ReadOnlySpan<byte> data = image.Data;
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 'E' && data[i + 1] == 'I')
                {
                    return true;
                }
            }

            if (Egap.Pdf.Content.ContentParser.ExpectedLength(image.Parameters) is long expected && expected != data.Length)
            {
                return true;
            }

            return data.Length > 0 && data[^1] is (byte)'\r' or (byte)'\n' or (byte)' ';
        }
    }

    /// <summary>Tokenizes the input leniently (must not throw) and strictly (may throw only <see cref="PdfException"/>).</summary>
    private static void Lexer(Stream input)
    {
        byte[] bytes = ReadAll(input);
        using PdfSource source = PdfSource.FromMemory(bytes);
        Lex(new Lexer(new SourceReader(source), new WarningLog(strict: false)));
        try
        {
            Lex(new Lexer(new SourceReader(source), new WarningLog(strict: true)));
        }
        catch (PdfException)
        {
        }
    }

    private static void Lex(Lexer lexer)
    {
        long last = -1;
        while (lexer.Next() is { Kind: not TokenKind.EndOfData } token)
        {
            if (token.Start <= last)
            {
                throw new InvalidOperationException($"The lexer went backwards at {token.Start}.");
            }

            last = token.Start;
        }
    }

    /// <summary>
    /// Parses every direct object in turn and every indirect object whose header is found, leniently (must not throw except
    /// at a limit) and strictly (may throw only <see cref="PdfException"/>). Each indirect object is written back, and the
    /// written bytes must parse strictly, without a single warning, to an object that writes the same bytes again.
    /// </summary>
    private static void Parser(Stream input)
    {
        byte[] bytes = ReadAll(input);
        using PdfSource source = PdfSource.FromMemory(bytes);
        PdfReadOptions options = new() { MaxNestingDepth = 64 };
        try
        {
            ParseAll(source, new WarningLog(strict: false), options, roundTrip: true);
        }
        catch (PdfLimitException)
        {
        }

        try
        {
            ParseAll(source, new WarningLog(strict: true), options, roundTrip: false);
        }
        catch (PdfException)
        {
        }
    }

    private static void ParseAll(PdfSource source, WarningLog log, PdfReadOptions options, bool roundTrip)
    {
        ObjectParser parser = new(new Lexer(new SourceReader(source), log), options, null);
        while (!parser.Lexer.Reader.AtEnd)
        {
            long before = parser.Lexer.Position;
            parser.ParseObject();
            if (parser.Lexer.Position == before && parser.Lexer.Next().Kind == TokenKind.EndOfData)
            {
                break;
            }
        }

        // Indirect objects: every "n g obj" header the lexer sees.
        Lexer scan = new(new SourceReader(source), new WarningLog(strict: false));
        Token first = default, second = default;
        while (scan.Next() is { Kind: not TokenKind.EndOfData } token)
        {
            if (token.Kind == TokenKind.Keyword && scan.Bytes.SequenceEqual("obj"u8) && first.Kind == TokenKind.Integer && second.Kind == TokenKind.Integer)
            {
                IndirectObject parsed;
                try
                {
                    parsed = parser.ParseIndirectObject(first.Start);
                }
                catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
                {
                    // Not a header after all (such as "0 0 obj"): fine.
                    first = second = default;
                    continue;
                }

                if (roundTrip)
                {
                    RoundTrip(parsed);
                }

                // Go on after the object, so stream data cannot throw the scan out of step.
                scan.Position = Math.Max(scan.Position, parser.Lexer.Position);
                first = second = default;
                continue;
            }

            first = second;
            second = token;
        }
    }

    private static void RoundTrip(IndirectObject parsed)
    {
        ByteWriter output = new();
        output.WriteAscii(FormattableString.Invariant($"{parsed.ObjectNumber} {parsed.Generation} obj\n"));
        ObjectWriter.Write(parsed.Value, output);
        output.WriteAscii("\nendobj\n");
        byte[] written = output.ToArray();

        using PdfSource source = PdfSource.FromMemory(written);
        ObjectParser parser = new(new Lexer(new SourceReader(source), new WarningLog(strict: true)), new PdfReadOptions { MaxNestingDepth = 64 }, null);
        IndirectObject again = parser.ParseIndirectObject(0);
        if (again.ObjectNumber != parsed.ObjectNumber || again.Generation != parsed.Generation)
        {
            throw new InvalidOperationException("The object header did not survive a round trip.");
        }

        byte[] rewritten = ObjectWriter.ToBytes(again.Value);
        if (!rewritten.AsSpan().SequenceEqual(ObjectWriter.ToBytes(parsed.Value)))
        {
            throw new InvalidOperationException("The object did not survive a round trip.");
        }
    }

    private static readonly string[] FilterNames = ["FlateDecode", "LZWDecode", "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode", "DCTDecode"];

    /// <summary>
    /// Raw filter input: the first byte picks one or two filters, the next four the predictor, colors, bits per component and
    /// columns; the rest is the data. Decoding leniently may throw only <see cref="PdfLimitException"/>, strictly only
    /// <see cref="PdfException"/>.
    /// </summary>
    private static void Filters(Stream input)
    {
        byte[] bytes = ReadAll(input);
        if (bytes.Length < 5)
        {
            return;
        }

        PdfArray filters = [PdfName.Get(FilterNames[bytes[0] % FilterNames.Length])];
        if (bytes[0] >= 128)
        {
            filters.Insert(0, PdfName.Get(FilterNames[(bytes[0] >> 3) % FilterNames.Length]));
        }

        int[] predictors = [1, 2, 10, 11, 12, 13, 14, 15];
        PdfDictionary parameters = new()
        {
            ["Predictor"] = predictors[bytes[1] % predictors.Length],
            ["Colors"] = 1 + (bytes[2] % 5),
            ["BitsPerComponent"] = new[] { 1, 2, 4, 8, 16 }[bytes[3] % 5],
            ["Columns"] = 1 + bytes[4],
            ["EarlyChange"] = bytes[1] >> 7,
        };
        PdfDictionary dictionary = new()
        {
            ["Filter"] = filters,
            ["DecodeParms"] = new PdfArray([parameters.Clone(), parameters]),
        };
        byte[] data = bytes[5..];
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 16 << 20 };
        try
        {
            FilterChain.Decode(dictionary, data, new DecodeContext(options, new WarningLog(strict: false), data.Length, 1));
        }
        catch (PdfLimitException)
        {
        }

        try
        {
            FilterChain.Decode(dictionary, data, new DecodeContext(options, new WarningLog(strict: true), data.Length, 1));
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>
    /// PDF bytes: every stream object found by its header is decoded leniently. A stream may fail only with a
    /// <see cref="PdfLimitException"/>, or a <see cref="PdfException"/> for a filter that is not supported (such as a named
    /// crypt filter in an encrypted file).
    /// </summary>
    private static void Streams(Stream input)
    {
        byte[] bytes = ReadAll(input);
        using PdfSource source = PdfSource.FromMemory(bytes);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20 };
        WarningLog log = new(strict: false);
        ObjectParser parser = new(new Lexer(new SourceReader(source), log), options, null);
        Lexer scan = new(new SourceReader(source), new WarningLog(strict: false));
        Token first = default, second = default;
        while (scan.Next() is { Kind: not TokenKind.EndOfData } token)
        {
            if (token.Kind == TokenKind.Keyword && scan.Bytes.SequenceEqual("obj"u8) && first.Kind == TokenKind.Integer && second.Kind == TokenKind.Integer)
            {
                try
                {
                    PdfObject value = parser.ParseIndirectObject(first.Start).Value;
                    scan.Position = Math.Max(scan.Position, parser.Lexer.Position);
                    first = second = default;
                    if (value is PdfStream stream)
                    {
                        long decoded = stream.GetDecodedBytes().Length;
                        Count("streams decoded");
                        Count(stream.UndecodedFilter is PdfName image ? $"left as /{image.Value}" : "fully decoded");
                        Counts["decoded MB"] = Counts.GetValueOrDefault("decoded MB") + (int)(decoded >> 20);
                    }
                }
                catch (PdfException e) when (e is PdfLimitException || e.Code is null)
                {
                    Count($"failed: {System.Text.RegularExpressions.Regex.Replace(e.Message, "[0-9]+", "#")}");
                }

                continue;
            }

            first = second;
            second = token;
        }

        foreach (PdfWarning warning in log.ToArray())
        {
            Count(warning.Code == PdfWarningCode.DamagedFilterData ? $"warning: {System.Text.RegularExpressions.Regex.Replace(warning.Message, "[0-9]+", "#")}" : $"warning: {warning.Code}");
        }
    }

    private static void Count(string key)
    {
        Counts[key] = Counts.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// PDF bytes: the cross-reference is read and every object it lists is loaded, leniently. Reading may throw
    /// <see cref="PdfException"/> when the newest section is unreadable (repair by scanning comes later) or a limit is hit;
    /// loading objects may throw only at a limit. Then the same strictly, where any <see cref="PdfException"/> is fine.
    /// </summary>
    private static void Xref(Stream input)
    {
        byte[] bytes = ReadAll(input);
        using PdfSource source = PdfSource.FromMemory(bytes);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        foreach (bool strict in new[] { false, true })
        {
            WarningLog log = new(strict);
            try
            {
                XrefTable xref;
                try
                {
                    xref = new XrefReader(source, options, log).Read();
                }
                catch (PdfException e) when (!strict && e is not PdfLimitException && e.Code is null)
                {
                    Count($"xref unreadable: {System.Text.RegularExpressions.Regex.Replace(e.Message, "[0-9]+", "#")}");
                    return;
                }

                ObjectLoader loader = new(source, xref, options, log);
                foreach ((int number, XrefEntry entry) in xref.Entries.ToArray())
                {
                    if (entry.Kind == XrefEntryKind.Free)
                    {
                        continue;
                    }

                    PdfObject value = loader.Load(number, entry.Kind == XrefEntryKind.InFile ? entry.Generation : 0);
                    if (!strict)
                    {
                        Count(value is PdfNull ? "objects null" : "objects loaded");
                    }
                }

                if (!strict)
                {
                    Count("files read");
                    foreach (PdfWarning warning in log.ToArray())
                    {
                        Count($"warning: {warning.Code}");
                    }
                }
            }
            catch (PdfLimitException)
            {
                Count("limit");
            }
            catch (PdfException) when (strict)
            {
            }
        }
    }

    /// <summary>The objects of a file that load to something other than null, as "n g".</summary>
    public static IEnumerable<string> ListObjects(byte[] bytes)
    {
        using PdfSource source = PdfSource.FromMemory(bytes);
        WarningLog log = new(strict: false);
        XrefTable xref = new XrefReader(source, PdfReadOptions.Default, log).Read();
        ObjectLoader loader = new(source, xref, PdfReadOptions.Default, log);
        List<string> lines = [];
        foreach ((int number, XrefEntry entry) in xref.Entries.OrderBy(e => e.Key))
        {
            int generation = entry.Kind == XrefEntryKind.InFile ? entry.Generation : 0;
            if (entry.Kind != XrefEntryKind.Free && loader.Load(number, generation) is not PdfNull)
            {
                lines.Add(FormattableString.Invariant($"{number} {generation}"));
            }
        }

        return lines;
    }

    /// <summary>
    /// PDF bytes opened as a <see cref="PdfDocument"/>: every object is loaded, the catalog and version read, and an edit made
    /// so change tracking runs. Opening may throw <see cref="PdfException"/> (no repair by scanning yet); after it opens,
    /// only a <see cref="PdfLimitException"/> may escape. Then the same strictly, where any <see cref="PdfException"/> is fine.
    /// </summary>
    private static void Document(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions lenient = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        foreach (PdfReadOptions options in new[] { lenient, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000, Strict = true } })
        {
            PdfDocument pdf;
            try
            {
                pdf = PdfDocument.Open(bytes, options);
            }
            catch (PdfException)
            {
                continue;
            }

            using (pdf)
            {
                try
                {
                    _ = pdf.Version;
                    foreach (PdfReference reference in pdf.Objects)
                    {
                        if (pdf.Objects[reference] is PdfStream stream && stream.EncodedLength < 1 << 20)
                        {
                            try
                            {
                                stream.GetDecodedBytes();
                            }
                            catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
                            {
                                // An unsupported filter.
                            }
                        }
                    }

                    PdfReference added = pdf.Objects.Add(new PdfDictionary { ["Fuzz"] = true });
                    pdf.Catalog["Fuzz"] = added;
                    if (!pdf.IsChanged || !pdf.Objects.Contains(added))
                    {
                        throw new InvalidOperationException("An edit was not tracked.");
                    }

                    Count(options.Strict ? "strict opened" : "opened");
                }
                catch (PdfLimitException)
                {
                }
                catch (PdfException) when (options.Strict)
                {
                }
            }
        }
    }

    /// <summary>
    /// PDF bytes opened leniently and saved by full rewrite, preserving the input's style and compact: each output must open strictly, without a single warning, and
    /// hold the same objects. Opening may throw <see cref="PdfException"/>; encrypted files are skipped.
    /// </summary>
    private static void Rewrite(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        PdfDocument original;
        try
        {
            original = PdfDocument.Open(bytes, options);
        }
        catch (PdfException)
        {
            return;
        }

        using (original)
        {
            // Encryption is kept in the first two saves, and new AES-256 encryption applied in the third.
            foreach ((PdfCompression compression, PdfEncryption encryption, string? password) in new[]
            {
                (PdfCompression.Preserve, PdfEncryption.Keep, options.Password),
                (PdfCompression.Compact, PdfEncryption.Keep, options.Password),
                (PdfCompression.Compact, PdfEncryption.Aes256("u", "o"), "u"),
            })
            {
                using MemoryStream output = new();
                try
                {
                    original.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = compression, Encryption = encryption, Deterministic = true });
                }
                catch (PdfLimitException)
                {
                    return;
                }

                using PdfDocument rewritten = PdfDocument.Open(output.ToArray(), options with { Strict = true, Password = password });
                if (rewritten.Warnings.Count > 0)
                {
                    throw new InvalidOperationException($"The {compression} rewrite has warnings: {rewritten.Warnings[0]}");
                }

                GraphCheck.AssertSame(original, rewritten);
                Count($"rewritten, {compression}, {encryption.Kind}");
            }
        }
    }

    /// <summary>
    /// PDF bytes opened leniently and saved incrementally. With no edits the output must be the input byte for byte, unless
    /// the file needed structural repair (then it is written whole). After an edit the output must start with the input
    /// (same exception), reopen, and hold the same objects as the edited document.
    /// </summary>
    private static void Incremental(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, options);
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                bool whole = pdf.WritesWhole(PdfSaveOptions.Default);
                using MemoryStream unchanged = new();
                pdf.Save(unchanged);
                if (!whole && !unchanged.ToArray().AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidOperationException("Saving without edits changed the file.");
                }

                pdf.Catalog["EgapFuzz"] = pdf.Objects.Add(new PdfDictionary { ["Edited"] = true });
                using MemoryStream edited = new();
                pdf.Save(edited, new PdfSaveOptions { Deterministic = true });
                byte[] output = edited.ToArray();

                // Warnings appear as objects load, and the edit counts too, so whether the save wrote whole is asked again.
                whole |= pdf.WritesWhole(PdfSaveOptions.Default);
                if (!whole && !output.AsSpan(0, bytes.Length).SequenceEqual(bytes))
                {
                    throw new InvalidOperationException("The update did not keep the original bytes.");
                }

                using PdfDocument reopened = PdfDocument.Open(output, options);
                GraphCheck.AssertSame(pdf, reopened);
                Count(whole ? "written whole" : "updated");
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>
    /// Name and number trees (Phase 2 task 2). Every dictionary with <c>/Names</c>, <c>/Nums</c> or <c>/Kids</c> among the
    /// first objects is read as a tree: a tree read without warnings must find each of its entries by lookup. Then keys are
    /// set and removed by a plan drawn from the input, and the tree must read back strictly, without a warning, holding
    /// exactly what the edits say, with no node over the split size.
    /// </summary>
    private static void Trees(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, options);
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                int seed = bytes.Length;
                foreach (PdfReference reference in pdf.Objects.Take(500))
                {
                    if (pdf.Objects[reference] is not PdfDictionary root)
                    {
                        continue;
                    }

                    seed = (seed * 31) + reference.ObjectNumber;
                    if (root["Nums"] is not null)
                    {
                        Exercise(pdf, r => new NumberTree(r, pdf.Objects, pdf.Log), r => new NumberTree(r, pdf.Objects, new WarningLog(strict: true)), root, n => n, Comparer<long>.Default, seed);
                    }
                    else if (root["Names"] is not null || root["Kids"] is not null)
                    {
                        Exercise(
                            pdf,
                            r => new NameTree(r, pdf.Objects, pdf.Log),
                            r => new NameTree(r, pdf.Objects, new WarningLog(strict: true)),
                            root,
                            n => PdfString.FromBytes(System.Text.Encoding.ASCII.GetBytes($"k{n}")),
                            Comparer<PdfString>.Create((a, b) => a.Bytes.SequenceCompareTo(b.Bytes)),
                            seed);
                    }
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    private static void Exercise<TKey>(PdfDocument pdf, Func<PdfDictionary, PdfTree<TKey>> open, Func<PdfDictionary, PdfTree<TKey>> openStrict, PdfDictionary root, Func<long, TKey> key, IComparer<TKey> comparer, int seed)
        where TKey : notnull
    {
        int warnings = pdf.Warnings.Count;
        PdfTree<TKey> tree = open(root);
        List<KeyValuePair<TKey, PdfObject>> entries = tree.Entries();
        if (pdf.Warnings.Count == warnings)
        {
            foreach (KeyValuePair<TKey, PdfObject> entry in entries.Take(300))
            {
                if (!tree.TryGet(entry.Key, out PdfObject value) || !ReferenceEquals(value, entry.Value))
                {
                    throw new InvalidOperationException($"A well-formed tree did not find its key {entry.Key}.");
                }

                if (!tree.TryGetFloor(entry.Key, out KeyValuePair<TKey, PdfObject> floor) || comparer.Compare(floor.Key, entry.Key) != 0)
                {
                    throw new InvalidOperationException($"A well-formed tree did not find the floor of its key {entry.Key}.");
                }
            }
        }

        // What the tree must hold after the edits, values compared by how they write.
        SortedDictionary<TKey, string> expected = new(comparer);
        foreach (KeyValuePair<TKey, PdfObject> entry in entries)
        {
            expected[entry.Key] = Written(entry.Value);
        }

        Random random = new(seed);
        List<TKey> existing = [.. expected.Keys];
        for (int i = 0; i < 400; i++)
        {
            bool remove = random.Next(3) == 0;
            TKey target = existing.Count > 0 && random.Next(2) == 0 ? existing[random.Next(existing.Count)] : key(random.Next(-50, 2000));
            if (remove)
            {
                if (tree.Remove(target) != expected.Remove(target))
                {
                    throw new InvalidOperationException($"Removing {target} did not match what the tree held.");
                }
            }
            else
            {
                tree.Set(target, new PdfInteger(i));
                expected[target] = Written(new PdfInteger(i));
            }
        }

        List<KeyValuePair<TKey, PdfObject>> after = openStrict(root).Entries();
        if (after.Count != expected.Count || !after.Zip(expected).All(p => comparer.Compare(p.First.Key, p.Second.Key) == 0 && Written(p.First.Value) == p.Second.Value))
        {
            throw new InvalidOperationException($"After the edits the tree holds {after.Count} entries, not the {expected.Count} expected.");
        }

        CheckNodeSizes(root);
    }

    private static string Written(PdfObject value)
    {
        return System.Text.Encoding.Latin1.GetString(ObjectWriter.ToBytes(value, includeStreamData: false));
    }

    private static void CheckNodeSizes(PdfDictionary root)
    {
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<PdfDictionary> pending = new([root]);
        while (pending.TryPop(out PdfDictionary? node))
        {
            if (!seen.Add(node))
            {
                throw new InvalidOperationException("A tree read without warnings has a loop.");
            }

            if (node.Get<PdfArray>("Kids") is PdfArray kids)
            {
                if (kids.Count > PdfTree<long>.MaxNodeSize)
                {
                    throw new InvalidOperationException($"A node has {kids.Count} kids.");
                }

                foreach (PdfObject kid in kids)
                {
                    pending.Push((PdfDictionary)kid.Resolve());
                }
            }
            else if ((node.Get<PdfArray>("Names") ?? node.Get<PdfArray>("Nums")) is { Count: > PdfTree<long>.MaxNodeSize * 2 } leaf)
            {
                throw new InvalidOperationException($"A leaf has {leaf.Count / 2} entries.");
            }
        }
    }

    /// <summary>
    /// The page tree (Phase 2 task 3), read leniently and strictly (which may throw only <see cref="PdfException"/>): every
    /// page's attributes are read, each page must be found again at an index that holds it, the boxes must be well formed
    /// and nested as the spec says, and reading must not count as an edit, so an unchanged save stays byte-identical.
    /// </summary>
    private static void Pages(Stream input)
    {
        byte[] bytes = ReadAll(input);
        foreach (bool strict in new[] { false, true })
        {
            PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000, Strict = strict };
            PdfDocument pdf;
            try
            {
                pdf = PdfDocument.Open(bytes, options);
            }
            catch (PdfException)
            {
                continue;
            }

            using (pdf)
            {
                try
                {
                    ReadPages(pdf);
                    if (pdf.IsChanged)
                    {
                        throw new InvalidOperationException("Reading the pages counted as an edit.");
                    }

                    if (!strict && !pdf.WritesWhole(PdfSaveOptions.Default))
                    {
                        using MemoryStream unchanged = new();
                        pdf.Save(unchanged);
                        if (!unchanged.ToArray().AsSpan().SequenceEqual(bytes))
                        {
                            throw new InvalidOperationException("Saving after reading the pages changed the file.");
                        }
                    }
                }
                catch (PdfException) when (strict)
                {
                }
                catch (PdfLimitException)
                {
                }
            }
        }
    }

    private static void ReadPages(PdfDocument pdf)
    {
        PdfPageCollection pages = pdf.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage page = pages[i];
            int index = pages.IndexOf(page);
            if (index < 0 || index > i || pages[index] != page)
            {
                throw new InvalidOperationException($"Page {i} was found at {index}.");
            }

            PdfRectangle media = page.MediaBox;
            if (media.IsEmpty || !double.IsFinite(media.Width) || !double.IsFinite(media.Height))
            {
                throw new InvalidOperationException($"Page {i} has the media box {media}.");
            }

            foreach (PdfRectangle box in new[] { page.CropBox, page.BleedBox, page.TrimBox, page.ArtBox })
            {
                if (box.IsEmpty || box.Intersect(media) != box)
                {
                    throw new InvalidOperationException($"Page {i} has a box {box} outside its media box {media}.");
                }
            }

            if (page.Rotate is not (0 or 90 or 180 or 270) || !(page.UserUnit > 0))
            {
                throw new InvalidOperationException($"Page {i} has rotation {page.Rotate} and user unit {page.UserUnit}.");
            }

            _ = page.Resources;
            _ = pages.GetLabel(i);
        }

        _ = pages.GetLabelRanges();
    }

    /// <summary>
    /// Editing the page tree (Phase 2 task 4): blank pages are inserted and pages removed and moved by a plan drawn from the
    /// input, and every page must keep the media box, crop box and rotation it had wherever it ends up. The result, written
    /// whole, must reopen strictly without a warning; saved incrementally, it must keep the original bytes and reopen with
    /// the same pages.
    /// </summary>
    private static void PageEdits(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, options);
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                List<(PdfRectangle Media, PdfRectangle Crop, int Rotate)> expected = [.. pdf.Pages.Select(Look)];
                Random random = new(bytes.Length + expected.Count);
                for (int i = 0; i < 60; i++)
                {
                    int count = pdf.Pages.Count;
                    switch (random.Next(4))
                    {
                        case 0 or 1:
                            int at = random.Next(count + 1);
                            PdfRectangle size = new(0, 0, random.Next(1, 2000), random.Next(1, 2000));
                            pdf.Pages.InsertBlank(at, size);
                            expected.Insert(at, (size, size, 0));
                            break;
                        case 2 when count > 0:
                            int removed = random.Next(count);
                            PdfReference? gone = pdf.Pages[removed].Reference;
                            pdf.Pages.RemoveAt(removed);
                            expected.RemoveAt(removed);

                            // Task 20: no link leads to the removed page.
                            if (gone is not null && !pdf.Pages.Any(p => gone.Equals(p.Reference)))
                            {
                                bool Leads(PdfDestination? d)
                                {
                                    return d?.PageReference is PdfReference r && r.Equals(gone);
                                }

                                // Only links are checked: in a damaged file the outline or the name tree can share dictionaries with
                                // the page tree, and then they are left alone, as reading or editing them would break the tree.
                                HashSet<PdfDictionary> pages = new(pdf.Pages.Select(p => p.Dictionary), ReferenceEqualityComparer.Instance);
                                if (pdf.Pages.Take(50).SelectMany(p => p.Annotations.OfType<PdfLinkAnnotation>().Take(200)).Any(link => !pages.Contains(link.Dictionary) && link.Dictionary.Get("Kids") is null && Leads(link.Destination)))
                                {
                                    throw new InvalidOperationException("Something still leads to a removed page.");
                                }
                            }

                            break;
                        case 3 when count > 0:
                            (int from, int to) = (random.Next(count), random.Next(count));
                            pdf.Pages.Move(from, to);
                            (PdfRectangle, PdfRectangle, int) moved = expected[from];
                            expected.RemoveAt(from);
                            expected.Insert(to, moved);
                            break;
                    }
                }

                // Page labels: a few ranges set and removed, then every label must survive the rewrite.
                for (int i = 0; i < 5; i++)
                {
                    int start = random.Next(Math.Max(pdf.Pages.Count, 1));
                    if (random.Next(3) == 0)
                    {
                        pdf.Pages.RemoveLabelRange(start);
                    }
                    else
                    {
                        pdf.Pages.SetLabelRange(new PdfPageLabelRange(start, (PdfPageLabelStyle)random.Next(6), random.Next(2) == 0 ? null : "p", random.Next(1, 100)));
                    }
                }

                List<string> labels = [.. Enumerable.Range(0, pdf.Pages.Count).Select(pdf.Pages.GetLabel)];
                List<(PdfRectangle Media, PdfRectangle Crop, int Rotate)> actual = [.. pdf.Pages.Select(Look)];
                for (int i = 0; i < Math.Max(actual.Count, expected.Count); i++)
                {
                    if (i >= actual.Count || i >= expected.Count || actual[i] != expected[i])
                    {
                        throw new InvalidOperationException($"After the edits page {i} is {(i < actual.Count ? actual[i] : "missing")}, not {(i < expected.Count ? expected[i] : "missing")}.");
                    }
                }

                using MemoryStream whole = new();
                pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                using (PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { Strict = true, MaxObjects = 1_000_000 }))
                {
                    if (!reopened.Pages.Select(Look).SequenceEqual(expected))
                    {
                        throw new InvalidOperationException("The rewritten file has other pages.");
                    }

                    if (!Enumerable.Range(0, reopened.Pages.Count).Select(reopened.Pages.GetLabel).SequenceEqual(labels))
                    {
                        throw new InvalidOperationException("The rewritten file has other page labels.");
                    }
                }

                bool rewritesWhole = pdf.WritesWhole(PdfSaveOptions.Default);
                using MemoryStream update = new();
                pdf.Save(update, new PdfSaveOptions { Deterministic = true });
                byte[] output = update.ToArray();
                if (!rewritesWhole && !output.AsSpan(0, bytes.Length).SequenceEqual(bytes))
                {
                    throw new InvalidOperationException("The update did not keep the original bytes.");
                }

                using PdfDocument updated = PdfDocument.Open(output, options);
                if (!updated.Pages.Select(Look).SequenceEqual(expected))
                {
                    throw new InvalidOperationException("The updated file has other pages.");
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    private static (PdfRectangle Media, PdfRectangle Crop, int Rotate) Look(PdfPage page)
    {
        return (page.MediaBox, page.CropBox, page.Rotate);
    }

    /// <summary>
    /// Destinations, named destinations and actions (Phase 2 task 6), read leniently: every action among the first objects is
    /// typed and its properties read, its <c>/Next</c> sequence must end with each action once, every array that reads as a
    /// destination must write back to an equal one, and every named destination is looked up. Then names are set and
    /// removed, and the file written whole must reopen with exactly the destinations set.
    /// </summary>
    private static void Navigation(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 };
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, options);
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                foreach (PdfReference reference in pdf.Objects.Take(2000))
                {
                    switch (pdf.Objects[reference])
                    {
                        case PdfDictionary dictionary when dictionary["S"] is not null:
                            ReadAction(pdf, PdfAction.FromDictionary(dictionary));
                            break;
                        case PdfArray array when PdfDestination.FromArray(array) is PdfDestination destination:
                            if (PdfDestination.FromArray(destination.ToArray()) != destination)
                            {
                                throw new InvalidOperationException($"The destination {array} did not write back the same.");
                            }

                            _ = destination.GetPageIndex(pdf);
                            break;
                    }
                }

                IReadOnlyList<string> names = pdf.NamedDestinations.GetNames();
                foreach (string name in names.Take(300))
                {
                    _ = pdf.NamedDestinations.TryGet(name, out _);
                }

                if (pdf.Pages.Count == 0)
                {
                    return;
                }

                Dictionary<string, PdfDestination> set = [];
                Random random = new(bytes.Length);
                for (int i = 0; i < 20; i++)
                {
                    string name = random.Next(3) == 0 && names.Count > 0 ? names[random.Next(names.Count)] : $"n{random.Next(50)}";
                    if (random.Next(4) == 0)
                    {
                        pdf.NamedDestinations.Remove(name);
                        set.Remove(name);
                    }
                    else if (pdf.Pages[random.Next(pdf.Pages.Count)] is { Reference: not null } page)
                    {
                        // A page written directly in /Kids has no reference for a destination to name.
                        PdfDestination destination = PdfDestination.ToPage(page, (PdfDestinationFit)random.Next(8)) with { Top = random.Next(1000) };
                        pdf.NamedDestinations.Set(name, destination);
                        set[name] = destination;
                    }
                }

                // A page tree repaired while reading (pages found by scanning, say) may list its pages in another order once
                // written whole, so page indexes are compared only for a sound tree.
                bool soundTree = !pdf.Warnings.Any(w => w.Code == PdfWarningCode.MalformedPageTree);
                using MemoryStream whole = new();
                pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                foreach ((string name, PdfDestination destination) in set)
                {
                    if (!reopened.NamedDestinations.TryGet(name, out PdfDestination found) || found.Fit != destination.Fit || found.PageReference is null
                        || (soundTree && found.GetPageIndex(reopened) != destination.GetPageIndex(pdf)))
                    {
                        throw new InvalidOperationException($"The named destination {name} did not survive the rewrite.");
                    }
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    private static void ReadAction(PdfDocument pdf, PdfAction action)
    {
        IReadOnlyList<PdfAction> sequence = action.GetSequence();
        if (sequence.Count == 0 || sequence[0] != action || sequence.Distinct().Count() != sequence.Count)
        {
            throw new InvalidOperationException("An action sequence did not start with the action, or repeated one.");
        }

        _ = action.ToString();
        switch (action)
        {
            case PdfGoToAction goTo:
                _ = (goTo.Destination, goTo.DestinationName, goTo.ResolveDestination(pdf)?.GetPageIndex(pdf));
                break;
            case PdfGoToRemoteAction remote:
                _ = (remote.FileName, remote.Destination, remote.DestinationName, remote.NewWindow);
                break;
            case PdfGoToEmbeddedAction embedded:
                _ = (embedded.FileName, embedded.Destination, embedded.DestinationName, embedded.Target, embedded.NewWindow);
                break;
            case PdfUriAction uri:
                _ = (uri.Uri, uri.IsMap);
                break;
            case PdfNamedAction named:
                _ = named.Name;
                break;
            case PdfLaunchAction launch:
                _ = (launch.FileName, launch.NewWindow);
                break;
            case PdfSubmitFormAction submit:
                _ = (submit.Url, submit.Fields, submit.Flags);
                break;
            case PdfResetFormAction reset:
                _ = (reset.Fields, reset.Flags);
                break;
            case PdfHideAction hide:
                _ = (hide.Targets, hide.Hide);
                break;
            case PdfSetOcgStateAction state:
                _ = (state.State, state.PreserveRadioButtons);
                break;
        }
    }

    /// <summary>
    /// The outline (Phase 2 task 7): read leniently and strictly (which may throw only <see cref="PdfException"/>), every
    /// item's properties read and each found among its parent's children. Then items are added, removed, moved, opened and
    /// closed by a plan drawn from the input, and the outline written whole must reopen without a repair, with every link
    /// and count right and as many items as the edits leave.
    /// </summary>
    private static void Outlines(Stream input)
    {
        byte[] bytes = ReadAll(input);
        foreach (bool strict in new[] { true, false })
        {
            PdfReadOptions options = new() { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000, Strict = strict };
            PdfDocument pdf;
            try
            {
                pdf = PdfDocument.Open(bytes, options);
            }
            catch (PdfException)
            {
                continue;
            }

            using (pdf)
            {
                try
                {
                    IReadOnlyList<PdfOutlineItem> items = pdf.Outline.GetAllItems();
                    foreach (PdfOutlineItem item in items.Take(500))
                    {
                        // Destination pages are not looked up: reading the page tree repairs it too, and a fuzzed file may use
                        // the same object as a page and an outline item, which the two repairs would pull both ways.
                        _ = (item.Title, item.Destination, item.Action, item.Color, item.IsBold, item.IsItalic, item.IsOpen);
                        if (!item.Parent.Children.Contains(item))
                        {
                            throw new InvalidOperationException($"The outline item {item.Reference} is not among its parent's children.");
                        }
                    }

                    if (!strict)
                    {
                        EditOutline(pdf, items.Count, bytes.Length);
                    }
                }
                catch (PdfException) when (strict)
                {
                }
                catch (PdfLimitException)
                {
                }
            }
        }
    }

    private static void EditOutline(PdfDocument pdf, int count, int seed)
    {
        Random random = new(seed);
        for (int i = 0; i < 30; i++)
        {
            List<PdfOutlineItem> all = [.. pdf.Outline.GetAllItems()];
            PdfOutlineNode parent = all.Count == 0 || random.Next(3) == 0 ? pdf.Outline : all[random.Next(all.Count)];
            switch (random.Next(4))
            {
                case 0 or 1:
                    parent.Insert(random.Next(parent.Children.Count + 1), $"t{i}");
                    count++;
                    break;
                case 2 when parent is PdfOutlineItem item:
                    count -= 1 + Descendants(item);
                    item.Remove();
                    break;
                case 3 when all.Count > 0:
                    PdfOutlineItem moved = all[random.Next(all.Count)];
                    PdfOutlineNode target = all.Count > 1 && random.Next(2) == 0 ? all[random.Next(all.Count)] : pdf.Outline;
                    bool under = false;
                    for (PdfOutlineNode at = target; at is PdfOutlineItem ancestor; at = ancestor.Parent)
                    {
                        under |= ancestor.Equals(moved);
                    }

                    if (under)
                    {
                        moved.IsOpen = !moved.IsOpen;
                    }
                    else
                    {
                        int size = target.Children.Count - (target.Children.Contains(moved) ? 1 : 0);
                        moved.MoveTo(target, random.Next(size + 1));
                    }

                    break;
            }
        }

        using MemoryStream whole = new();
        pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
        using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
        IReadOnlyList<PdfOutlineItem> items = reopened.Outline.GetAllItems();
        if (reopened.Warnings.Any(w => w.Code == PdfWarningCode.MalformedOutline) || items.Count != count)
        {
            throw new InvalidOperationException($"After the edits the outline has {items.Count} items, not {count}, or needed repair.");
        }

        if (reopened.Outline.Dictionary is PdfDictionary root && (root.GetInt("Count") ?? 0) != Shown(root))
        {
            throw new InvalidOperationException("The outline's /Count is wrong after the edits.");
        }

        foreach (PdfOutlineItem item in items)
        {
            int shown = Shown(item.Dictionary);
            long? itemCount = item.Dictionary.GetInt("Count");
            // Many writers give a leaf /Count 0; the spec only needs a /Count where there are children.
            if (shown == 0 ? itemCount is not (null or 0) : Math.Abs(itemCount ?? 0) != shown)
            {
                throw new InvalidOperationException($"The item {item.Title} has /Count {itemCount} with {shown} shown below it.");
            }
        }
    }

    private static int Descendants(PdfOutlineItem item)
    {
        return item.Children.Sum(c => 1 + Descendants(c));
    }

    private static int Shown(PdfDictionary node)
    {
        int shown = 0;
        for (PdfDictionary? child = node.Get<PdfDictionary>("First"); child is not null; child = child.Get<PdfDictionary>("Next"))
        {
            shown += 1 + (child.GetInt("Count") > 0 ? Shown(child) : 0);
        }

        return shown;
    }

    /// <summary>
    /// XMP packets (Phase 2 task 8): any bytes parsed as a packet must not throw; a packet that parses has every mapped
    /// property read, then set, and written back; the written packet must parse again with the values set and every element
    /// of an unknown namespace outside the properties set still there.
    /// </summary>
    private static void Xmp(Stream input)
    {
        byte[] bytes = ReadAll(input);
        if (Egap.Pdf.Metadata.XmpPacket.Parse(bytes) is not Egap.Pdf.Metadata.XmpPacket packet)
        {
            return;
        }

        System.Xml.Linq.XNamespace dc = Egap.Pdf.Metadata.XmpPacket.Dc;
        System.Xml.Linq.XNamespace pdfNs = Egap.Pdf.Metadata.XmpPacket.Pdf;
        System.Xml.Linq.XNamespace xmp = Egap.Pdf.Metadata.XmpPacket.Xmp;
        _ = (packet.GetAlternative(dc + "title"), packet.GetList(dc + "creator"), packet.GetText(pdfNs + "Producer"), packet.GetText(xmp + "CreateDate"));
        _ = Egap.Pdf.Metadata.PdfDate.ParseXmp(packet.GetText(xmp + "ModifyDate"));
        int unknown = Unknown(packet.Document);

        packet.SetAlternative(dc + "title", "Fuzz title");
        packet.SetSequence(dc + "creator", "Fuzz author");
        packet.SetText(pdfNs + "Producer", null);
        packet.SetText(xmp + "ModifyDate", "2026-10-12T18:30:00+02:00");
        byte[] written = packet.ToBytes();
        Egap.Pdf.Metadata.XmpPacket again = Egap.Pdf.Metadata.XmpPacket.Parse(written)
            ?? throw new InvalidOperationException("A written XMP packet did not parse.");
        if (again.GetAlternative(dc + "title") != "Fuzz title" || again.GetList(dc + "creator") != "Fuzz author" || again.GetText(pdfNs + "Producer") is not null
            || again.GetText(xmp + "ModifyDate") != "2026-10-12T18:30:00+02:00")
        {
            throw new InvalidOperationException("A written XMP packet lost a value set.");
        }

        if (Unknown(again.Document) != unknown)
        {
            throw new InvalidOperationException("A written XMP packet lost elements of other namespaces.");
        }
    }

    /// <summary>The elements outside the namespaces the metadata API writes, which must survive a rewrite.</summary>
    private static int Unknown(System.Xml.Linq.XDocument document)
    {
        System.Xml.Linq.XNamespace[] known = [Egap.Pdf.Metadata.XmpPacket.Dc, Egap.Pdf.Metadata.XmpPacket.Pdf, Egap.Pdf.Metadata.XmpPacket.Xmp, Egap.Pdf.Metadata.XmpPacket.Rdf];

        // Elements inside the properties that are set are replaced with them, so they do not count.
        return document.Descendants().Count(e => !known.Contains(e.Name.Namespace) && !e.Ancestors().Any(a => known.Contains(a.Name.Namespace) && a.Name.Namespace != Egap.Pdf.Metadata.XmpPacket.Rdf));
    }

    /// <summary>
    /// Document metadata (Phase 2 task 8): every property read leniently, then the title, author and keywords set and the
    /// document saved with a changed modification date; reopened, it must give back what was set, and an XMP packet that
    /// parsed before must still parse.
    /// </summary>
    private static void Metadata(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                PdfMetadata metadata = pdf.Metadata;
                _ = (metadata.Title, metadata.Author, metadata.Subject, metadata.Keywords, metadata.Creator, metadata.Producer, metadata.CreationDate, metadata.ModificationDate);
                // A PDF 2.0 document gets a packet with its first change (Phase 2 Q8).
                bool hadXmp = metadata.HasXmp || string.CompareOrdinal(pdf.Version, "2.0") >= 0;
                metadata.Title = "Fuzz title";
                metadata.Author = "Fuzz author";
                metadata.Keywords = null;
                pdf.Clock = new FixedClock(new DateTimeOffset(2026, 10, 12, 18, 30, 0, TimeSpan.FromHours(2)));
                using MemoryStream saved = new();
                pdf.Save(saved);
                using PdfDocument reopened = PdfDocument.Open(saved.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                PdfMetadata after = reopened.Metadata;
                if (after.Title != "Fuzz title" || after.Author != "Fuzz author" || after.Keywords is not null || after.HasXmp != hadXmp
                    || (after.Info is not null && after.ModificationDate != new DateTimeOffset(2026, 10, 12, 18, 30, 0, TimeSpan.FromHours(2))))
                {
                    throw new InvalidOperationException("Metadata set did not come back after saving.");
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now.ToUniversalTime();
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Fuzz", now.Offset, "Fuzz", "Fuzz");
    }

    /// <summary>
    /// Embedded files (Phase 2 task 9): every attachment's properties and bytes read leniently; then files are added and
    /// removed by a plan drawn from the input, and the file written whole must reopen with exactly the names the edits leave,
    /// each added file's bytes intact and its checksum right, and no associated file left that is not in the tree.
    /// </summary>
    private static void Attachments(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                foreach (PdfAttachment attachment in pdf.Attachments.Take(200))
                {
                    _ = (attachment.FileName, attachment.Description, attachment.MimeType, attachment.Size, attachment.Checksum, attachment.CreationDate, attachment.ModificationDate, attachment.Relationship);
                    try
                    {
                        _ = attachment.VerifyChecksum();
                    }
                    catch (PdfException e) when (e is not PdfLimitException)
                    {
                        // Damaged or unsupported stream data.
                    }
                }

                SortedDictionary<string, byte[]?> expected = new(StringComparer.Ordinal);
                foreach (PdfAttachment attachment in pdf.Attachments)
                {
                    expected.TryAdd(attachment.Name, null);
                }

                Random random = new(bytes.Length);
                List<string> names = [.. expected.Keys];
                for (int i = 0; i < 20; i++)
                {
                    string name = random.Next(3) == 0 && names.Count > 0 ? names[random.Next(names.Count)] : $"f{random.Next(40)}";
                    if (random.Next(3) == 0)
                    {
                        pdf.Attachments.Remove(name);
                        expected.Remove(name);
                    }
                    else if (name.Length > 0)
                    {
                        // Add takes no empty name, though a file's tree may hold one.
                        byte[] data = new byte[random.Next(64)];
                        random.NextBytes(data);
                        pdf.Attachments.Add(name, data, random.Next(2) == 0 ? "application/octet-stream" : null);
                        expected[name] = data;
                    }
                }

                using MemoryStream whole = new();
                pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });

                // Names the tree held whose text is the same are listed once; set names are checked exactly.
                Dictionary<string, PdfAttachment> found = [];
                foreach (PdfAttachment attachment in reopened.Attachments)
                {
                    found.TryAdd(attachment.Name, attachment);
                }

                foreach ((string name, byte[]? data) in expected)
                {
                    if (!found.TryGetValue(name, out PdfAttachment? attachment) || (data is not null && (!attachment.GetBytes()!.AsSpan().SequenceEqual(data) || attachment.VerifyChecksum() != true)))
                    {
                        throw new InvalidOperationException($"The attachment {name} did not survive the rewrite.");
                    }
                }

                foreach (string name in found.Keys.Where(n => n.StartsWith('f') && !expected.ContainsKey(n) && !names.Contains(n)))
                {
                    throw new InvalidOperationException($"The removed attachment {name} is still there.");
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>
    /// Layers (Phase 2 task 10): every layer read leniently; then layers are shown, hidden, locked, renamed and removed by a
    /// plan drawn from the input, each change read back at once, and the file written whole must reopen with the same layers
    /// in the same state.
    /// </summary>
    private static void Layers(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                foreach (PdfLayer layer in pdf.Layers.Take(200))
                {
                    _ = (layer.Name, layer.Intents, layer.IsVisible, layer.IsLocked);
                }

                Random random = new(bytes.Length);
                for (int i = 0; i < 20 && pdf.Layers.Count > 0; i++)
                {
                    PdfLayer layer = pdf.Layers[random.Next(pdf.Layers.Count)];
                    bool value = random.Next(2) == 0;
                    switch (random.Next(5))
                    {
                        case 3:
                            // Removing a layer's content leaves none of it, so doing it again removes nothing.
                            layer.RemoveContent();
                            Check(layer.RemoveContent() == 0, "content removal");
                            break;
                        case 0:
                            layer.IsVisible = value;
                            Check(layer.IsVisible == value, "visibility");
                            break;
                        case 1:
                            layer.IsLocked = value;
                            Check(layer.IsLocked == value, "lock");
                            break;
                        case 2:
                            layer.Name = $"L{i}";
                            Check(layer.Name == $"L{i}", "name");
                            break;
                        default:
                            Check(pdf.Layers.Remove(layer) && !pdf.Layers.Contains(layer), "removal");
                            break;
                    }
                }

                List<(string, bool, bool)> state = [.. pdf.Layers.Select(l => (l.Name, l.IsVisible, l.IsLocked))];
                using MemoryStream whole = new();
                pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                Check(reopened.Layers.Select(l => (l.Name, l.IsVisible, l.IsLocked)).SequenceEqual(state), "the rewrite");
            }
            catch (PdfLimitException)
            {
            }
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException($"A layer's {what} did not hold.");
            }
        }
    }

    /// <summary>
    /// Annotations (Phase 2 tasks 12 and 13): every public property of every annotation on the first 50 pages read leniently,
    /// and appearances drawn for up to 50 of them; then annotations are added, given popups and removed by a plan drawn from
    /// the input, each change read back at once, and the file written whole must reopen with the same annotations on each
    /// page.
    /// </summary>
    private static void Annotations(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                List<PdfPage> pages = [.. pdf.Pages.Take(50)];
                foreach (PdfPage page in pages)
                {
                    foreach (PdfAnnotation annotation in page.Annotations.Take(500))
                    {
                        ReadProperties(annotation);
                        _ = (annotation.GetAppearance(), annotation.GetAppearance(PdfAppearanceKind.Down), page.Annotations.GetReplies(annotation).Count);
                        if (annotation is PdfFileAttachmentAnnotation { Attachment: PdfAttachment attachment })
                        {
                            try
                            {
                                _ = (attachment.GetBytes(), attachment.VerifyChecksum());
                            }
                            catch (PdfException)
                            {
                                // A damaged or unsupported embedded file stream is an error the caller sees, not a bug.
                            }
                        }
                    }
                }

                // Appearances drawn from whatever the annotations hold must be well formed and leave the annotation readable.
                foreach (PdfAnnotation annotation in pages.SelectMany(p => p.Annotations).Take(50))
                {
                    if (annotation.UpdateAppearance())
                    {
                        Check(annotation.GetAppearance() is PdfStream, "a drawn appearance");
                        ReadProperties(annotation);
                    }
                }

                Random random = new(bytes.Length);
                for (int i = 0; i < 20 && pages.Count > 0; i++)
                {
                    // Pages are looked up afresh, since an edit can rebuild a broken page tree and copy a page listed twice.
                    PdfAnnotationCollection annotations = pdf.Pages[random.Next(pages.Count)].Annotations;
                    PdfRectangle box = new(random.Next(100), random.Next(100), 100 + random.Next(100), 100 + random.Next(100));
                    int before = annotations.Count;
                    switch (random.Next(5))
                    {
                        case 0:
                            PdfSquareAnnotation square = annotations.Add(new PdfSquareAnnotation(box) { Contents = $"S{i}" });
                            Check(annotations.Count == before + 1 && annotations[^1].Equals(square) && square.Contents == $"S{i}", "an added square");
                            break;
                        case 1:
                            annotations.Add(new PdfHighlightAnnotation(PdfTextMarkupAnnotation.Quad(box)));
                            Check(annotations.Count == before + 1 && annotations[^1] is PdfHighlightAnnotation, "an added highlight");
                            break;
                        case 2 when annotations.OfType<PdfMarkupAnnotation>().FirstOrDefault(a => a.Reference is not null) is PdfMarkupAnnotation markup:
                            PdfPopupAnnotation popup = annotations.AddPopup(markup, box);
                            Check(popup.Equals(markup.Popup) && annotations.Contains(popup), "an added popup");
                            break;
                        case 3 when before > 0:
                            PdfAnnotation removed = annotations[random.Next(before)];
                            Check(annotations.Remove(removed) && !annotations.Contains(removed) && annotations.Count < before, "a removal");
                            break;
                        default:
                            annotations.Add(new PdfLinkAnnotation(box, new PdfUriAction($"https://example.com/{i}")));
                            Check(annotations[^1] is PdfLinkAnnotation { Action: PdfUriAction }, "an added link");
                            break;
                    }
                }

                List<List<(string, PdfRectangle)>> state = [.. pdf.Pages.Take(pages.Count).Select(p => p.Annotations.Select(a => (a.Subtype, a.Rectangle)).ToList())];
                using MemoryStream whole = new();
                pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                for (int i = 0; i < pages.Count; i++)
                {
                    Check(reopened.Pages[i].Annotations.Select(a => (a.Subtype, a.Rectangle)).SequenceEqual(state[i]), "the rewrite");
                }
            }
            catch (PdfLimitException)
            {
            }
        }

        static void ReadProperties(PdfAnnotation annotation)
        {
            foreach (System.Reflection.PropertyInfo property in annotation.GetType().GetProperties())
            {
                if (property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true })
                {
                    object? value;
                    try
                    {
                        value = property.GetValue(annotation);
                    }
                    catch (System.Reflection.TargetInvocationException e) when (e.InnerException is PdfLimitException)
                    {
                        continue;
                    }
                    catch (System.Reflection.TargetInvocationException e)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException!);
                        throw;
                    }

                    if (value is System.Collections.IEnumerable items and not string)
                    {
                        foreach (object? item in items)
                        {
                            _ = item?.ToString();
                        }
                    }
                }
            }
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException($"{what} did not hold.");
            }
        }
    }

    /// <summary>
    /// Form reading and filling (Phase 2 tasks 14 and 15): every public property of up to 2,000 fields read leniently, with
    /// their kids and widgets, every widget on the first 50 pages led back to its field, and the XFA data of each field read
    /// (task 17); then up to 20 terminal fields filled, each value read back at once and after the file is written whole and
    /// reopened, where a static XFA form is then converted.
    /// </summary>
    /// <summary>The certificates the revocation target judges responses for: made once, an issuer and the certificate it issued.</summary>
    private static readonly Lazy<(System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate, System.Security.Cryptography.X509Certificates.X509Certificate2 Issuer)> RevocationCertificates = new(() =>
    {
        using System.Security.Cryptography.ECDsa issuerKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using System.Security.Cryptography.ECDsa key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        System.Security.Cryptography.X509Certificates.CertificateRequest root = new("CN=Fuzz Root", issuerKey, System.Security.Cryptography.HashAlgorithmName.SHA256);
        root.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        System.Security.Cryptography.X509Certificates.X509Certificate2 issuer = root.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        System.Security.Cryptography.X509Certificates.CertificateRequest leaf = new("CN=Fuzz Leaf", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = leaf.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), [1, 2, 3]);
        return (certificate, issuer);
    });

    /// <summary>
    /// The OCSP and CRL readers (Phase 5 task 10): any bytes either answer for the certificate or are refused with
    /// <see langword="null"/>; neither reader throws.
    /// </summary>
    private static void Revocation(Stream input)
    {
        byte[] bytes = ReadAll(input);
        (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, System.Security.Cryptography.X509Certificates.X509Certificate2 issuer) = RevocationCertificates.Value;
        _ = global::Egap.Pdf.Signatures.Cms.Ocsp.Read(bytes, certificate, issuer, out _);
        _ = global::Egap.Pdf.Signatures.Cms.Ocsp.Certificates(bytes);
        _ = global::Egap.Pdf.Signatures.Cms.Crl.Read(bytes, certificate, issuer);
    }

    /// <summary>
    /// The CMS reader (Phase 5 task 3): any bytes decode to a SignedData or throw <see cref="PdfException"/>, and every signer
    /// attribute reads without throwing; the old adbe.x509.rsa_sha1 reader the same.
    /// </summary>
    private static void Cms(Stream input)
    {
        byte[] bytes = ReadAll(input);
        try
        {
            global::Egap.Pdf.Signatures.Cms.CmsSignedData cms = global::Egap.Pdf.Signatures.Cms.CmsSignedData.Decode(bytes);
            _ = (cms.Version, cms.DigestAlgorithms.Count, cms.ContentType, cms.Content, cms.Crls.Count);
            foreach (System.Security.Cryptography.X509Certificates.X509Certificate2 certificate in cms.LoadCertificates())
            {
                foreach (global::Egap.Pdf.Signatures.Cms.CmsSignerInfo signer in cms.Signers)
                {
                    _ = signer.FindCertificate([certificate]);
                }

                certificate.Dispose();
            }

            foreach (global::Egap.Pdf.Signatures.Cms.CmsSignerInfo signer in cms.Signers)
            {
                _ = (signer.Version, signer.DigestHash, signer.MessageDigest, signer.ContentType, signer.SigningTime, signer.SigningCertificateHashes.Count,
                    signer.TimestampToken, signer.UsesWeakDigest, global::Egap.Pdf.Signatures.Cms.CmsAlgorithms.SignatureName(signer.SignatureAlgorithm),
                    global::Egap.Pdf.Signatures.Cms.CmsAlgorithms.SignatureHash(signer.SignatureAlgorithm, System.Security.Cryptography.HashAlgorithmName.SHA256));
            }
        }
        catch (PdfException)
        {
            // Not a SignedData Egap reads: the caller sees why.
        }

        try
        {
            _ = global::Egap.Pdf.Signatures.Cms.RsaSha1Signature.Read(bytes);
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>
    /// Revisions and signatures (Phase 5 task 2): every revision found must end inside the file, later ones after earlier
    /// ones, and open read-only; every signature's properties read, its signed bytes stream reads to its length, and its
    /// revision is one of the document's.
    /// </summary>
    private static void Signatures(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                IReadOnlyList<PdfRevision> revisions = pdf.Revisions;
                Check(revisions.Count > 0, "a document has at least one revision");
                for (int i = 0; i < revisions.Count; i++)
                {
                    Check(revisions[i].Index == i && revisions[i].Length <= bytes.Length && (i == 0 || revisions[i].Length > revisions[i - 1].Length), "revisions in order inside the file");
                    Check(revisions[i].IsLatest == (i == revisions.Count - 1), "only the last revision is the latest");
                }

                foreach (PdfRevision revision in revisions.Take(5))
                {
                    try
                    {
                        using PdfDocument earlier = revision.Open();
                        Check(earlier.IsReadOnly, "an earlier revision opens read-only");
                        _ = (earlier.Pages.Count, earlier.Signatures.Count);
                    }
                    catch (PdfException)
                    {
                        // An update may well be the only part of a file that makes it readable.
                    }
                }

                foreach (PdfSignature signature in pdf.Signatures.Take(50))
                {
                    // Verifying reads the CMS, the DSS and every later revision; it reports, it never throws.
                    PdfSignatureReport report = signature.Verify();
                    _ = (report.IsValid, report.Changes.Count, report.Problems.Count);
                    ReadPublicProperties(signature);
                    _ = (signature.Field.Lock, signature.Field.SeedValue);
                    Check(signature.Revision is null || revisions.Contains(signature.Revision), "a signature's revision is the document's");
                    Check(signature.ByteRange.Count > 0 || signature.ByteRangeIssues.HasFlag(PdfByteRangeIssues.Malformed), "an empty byte range is malformed");
                    Stream signed;
                    try
                    {
                        signed = signature.OpenSignedBytes();
                    }
                    catch (InvalidOperationException) when (signature.ByteRangeIssues != PdfByteRangeIssues.None)
                    {
                        // A byte range that cannot be read says so.
                        continue;
                    }

                    using (signed)
                    {
                        byte[] buffer = new byte[64 << 10];
                        long read = 0;
                        for (int n; (n = signed.Read(buffer)) > 0;)
                        {
                            read += n;
                        }

                        Check(read == signed.Length, "the signed bytes stream reads to its length");
                    }
                }
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                // A damaged form or field is an error the caller sees.
            }
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException($"Signature reading: {what} did not hold.");
            }
        }
    }

    private static void Forms(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                PdfForm form = pdf.Form;
                _ = (form.Exists, form.NeedAppearances, form.DefaultAppearance, form.Alignment, form.SignatureFlags, form.HasXfa, form.Count);
                foreach (PdfField field in form.GetAllFields().Take(2000))
                {
                    ReadPublicProperties(field);
                    foreach (PdfWidgetAnnotation widget in field.Widgets.Take(100))
                    {
                        if (widget.Field is not PdfField owner)
                        {
                            throw new InvalidOperationException("A field's widget does not lead back to a field.");
                        }

                        _ = owner.FullName;
                    }
                }

                if (form.GetAllFields() is { Count: > 0 } all && all[0] is PdfField first)
                {
                    Check(form.TryGetField(first.FullName, out PdfField found) && found.FullName == first.FullName, "a field looked up by name");
                }

                foreach (PdfPage page in pdf.Pages.Take(50))
                {
                    foreach (PdfWidgetAnnotation widget in page.Annotations.OfType<PdfWidgetAnnotation>().Take(500))
                    {
                        if (widget.Field is PdfField field)
                        {
                            ReadPublicProperties(field);
                        }
                    }
                }

                // Filling: each of up to 20 terminal fields takes a value of its type, which must read back at once and after
                // the file is written whole and reopened.
                List<(string Name, string? Value)> filled = [];
                foreach (PdfField field in form.GetTerminalFields().DistinctBy(f => f.FullName).Take(20))
                {
                    if (Fill(field) is (string name, var value))
                    {
                        filled.Add((name, value));
                    }
                }

                if (form.Xfa is PdfXfa xfa)
                {
                    try
                    {
                        _ = (xfa.IsDynamic, xfa.PacketNames.Count, xfa.GetDatasets(), xfa.GetPacket("template"));
                    }
                    catch (PdfException)
                    {
                        // A packet in a damaged stream is an error the caller sees, not a bug.
                    }

                    foreach (PdfField field in form.GetTerminalFields().Take(200))
                    {
                        _ = xfa.GetDataValue(field.FullName);
                    }
                }

                if (filled.Count > 0)
                {
                    using MemoryStream whole = new();
                    pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                    using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                    bool rebuilt = pdf.Warnings.Any(w => w.Code == PdfWarningCode.MalformedPageTree && w.Message.Contains("rebuilt", StringComparison.Ordinal));
                    foreach ((string name, string? value) in filled)
                    {
                        // Writing whole first rebuilds a page tree repaired in memory, which can give a field that is also a
                        // page a new /Parent; so the document as saved is what the reopened file must match. A dictionary
                        // reached by two paths through a broken tree can be terminal on one only, so the terminal one counts.
                        string? saved = pdf.Form.GetTerminalFields().FirstOrDefault(f => f.FullName == name) is PdfField before ? ValueOf(before) : null;
                        string? read = reopened.Form.GetTerminalFields().FirstOrDefault(f => f.FullName == name) is PdfField after ? ValueOf(after) : null;
                        Check(saved == read && (saved == value || rebuilt), $"field {name}'s value after the rewrite (filled {value}, saved {saved}, read {read})");
                    }

                    if (reopened.Form.Xfa is PdfXfa converted && converted.ConvertToAcroForm())
                    {
                        Check(!reopened.Form.HasXfa, "an XFA form converted");
                    }
                }
            }
            catch (PdfLimitException)
            {
            }
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException($"{what} did not hold.");
            }
        }

        // A value of the field's type, set and read back; null when the field takes none or its name is not unique.
        static (string, string?)? Fill(PdfField field)
        {
            string name = field.FullName;
            if (field.Document.Form.GetTerminalFields().FirstOrDefault(f => f.FullName == name) is not PdfField first || !first.Equals(field))
            {
                return null;
            }

            switch (field)
            {
                case PdfTextField text:
                    string value = text.MaxLength is int max ? "Egap filled"[..Math.Min(max, 11)] : "Egap filled";
                    text.Value = value;
                    Check(text.Value == value, "a text value");
                    break;
                case PdfCheckBoxField box:
                    box.IsChecked = !box.IsChecked;
                    break;
                case PdfRadioButtonField radio when radio.Options.FirstOrDefault(o => o.Length > 0 && o != "Off") is string option:
                    radio.Value = option;
                    Check(radio.Value == option, "a radio value");
                    break;
                case PdfComboBoxField combo when combo.Options.Count > 0 || combo.IsEditable:
                    string choice = combo.Options.Count > 0 ? combo.Options[^1].ExportValue : "typed";
                    combo.Value = choice;
                    Check(combo.Value == choice, "a combo box value");
                    break;
                case PdfListBoxField list when list.Options.Count > 0:
                    list.Select(list.Options[0].ExportValue);
                    Check(list.Values.SequenceEqual([list.Options[0].ExportValue]), "a list box selection");
                    break;
                default:
                    return null;
            }

            return (name, ValueOf(field));
        }

        static string? ValueOf(PdfField field)
        {
            return field switch
            {
                PdfTextField text => text.Value,
                PdfCheckBoxField box => box.IsChecked.ToString(),
                PdfRadioButtonField radio => radio.Value,
                PdfChoiceField choice => string.Join("|", choice.Values),
                _ => null,
            };
        }
    }

    /// <summary>Reads every public property without index parameters, and the items of those that are lists.</summary>
    private static void ReadPublicProperties(object view)
    {
        foreach (System.Reflection.PropertyInfo property in view.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is not { IsPublic: true })
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(view);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is PdfLimitException)
            {
                continue;
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is PdfException && property.Name is "Value" or "DefaultValue" or "RichValue")
            {
                // A text value in a damaged stream is an error the caller sees, not a bug.
                continue;
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException!);
                throw;
            }

            if (value is System.Collections.IEnumerable items and not string)
            {
                foreach (object? item in items)
                {
                    _ = item?.ToString();
                }
            }
        }
    }

    /// <summary>
    /// Flatten (Phase 2 task 16): the form and the annotations flattened with a filter drawn from the input; the file written
    /// whole must reopen with the same pages, no form, and content that decodes.
    /// </summary>
    private static void Flatten(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                int pages = pdf.Pages.Count;
                pdf.Form.Flatten();
                pdf.FlattenAnnotations((PdfFlattenFilter)(bytes.Length % 3), includeLinks: bytes.Length % 2 == 0);
                if (pdf.Form.Exists)
                {
                    throw new InvalidOperationException("The form is still there after flattening.");
                }

                using MemoryStream whole = new();
                pdf.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Deterministic = true });
                using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                if (reopened.Pages.Count != pages || reopened.Form.Exists)
                {
                    throw new InvalidOperationException("The flattened file lost pages or kept its form.");
                }

                foreach (PdfPage page in reopened.Pages.Take(50))
                {
                    if (page.Dictionary.Get<PdfArray>("Contents") is PdfArray contents)
                    {
                        foreach (PdfObject content in contents)
                        {
                            try
                            {
                                _ = (content.Resolve() as PdfStream)?.GetDecodedBytes();
                            }
                            catch (PdfException)
                            {
                                // A page's own damaged content stays damaged.
                            }
                        }
                    }
                }
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>
    /// Merge (Phase 2 tasks 18 and 19): the input's first 20 pages imported twice into a new document, with their fields,
    /// destinations, layers, labels, attachments and outline, which is written whole with identical objects merged and
    /// reopened; every page must come back with the content and media box of the page it was copied from, and extracting
    /// the second copy must give them again.
    /// </summary>
    /// <summary>Phase 5 task 21: the structure tree read, walked and brought along by page import, then saved and read back.</summary>
    private static void Structure(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                int count = 0;
                foreach (PdfStructureElement element in pdf.Structure.Descendants().Take(100_000))
                {
                    count++;
                    _ = (element.Type, element.StandardType, element.Title, element.AlternateText, element.Language, element.Classes.Count, element.Attributes.Count, element.Parent);
                    foreach (PdfStructureNode child in element.Children)
                    {
                        _ = child;
                    }
                }

                _ = (pdf.Structure.RoleMap.Count, pdf.Structure.ClassMap.Count, pdf.Structure.IsMarked);
                if (pdf.Pages.Count > 0)
                {
                    _ = pdf.Structure.FindMarkedContent(pdf.Pages[0], 0);
                }

                using PdfDocument target = PdfDocument.Create();
                target.Pages.Import(pdf, Enumerable.Range(0, Math.Min(10, pdf.Pages.Count)));
                using MemoryStream saved = new();
                target.Save(saved, new PdfSaveOptions { Deterministic = true });
                using PdfDocument back = PdfDocument.Open(saved.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                Check(back.Pages.Count == Math.Min(10, pdf.Pages.Count), "the imported page count");
                _ = back.Structure.Descendants().Take(100_000).Count();
                _ = count;
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>
    /// Phase 5 task 21: the PDF/A rules checked and, when conversion succeeds, the converted file saved and checked again
    /// with nothing left to fix; a refused conversion leaves the document unchanged.
    /// </summary>
    private static void PdfA(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            try
            {
                _ = PdfAConverter.Check(pdf, PdfAConformance.PdfA1b);
                _ = PdfAConverter.CheckUniversalAccessibility(pdf);
                Check(!pdf.IsChanged, "checking changes nothing");
                PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2u);
                if (!report.Succeeded)
                {
                    return;
                }

                using MemoryStream saved = new();
                pdf.Save(saved, new PdfSaveOptions { Deterministic = true, AllowInvalidatingSignatures = true });
                using PdfDocument back = PdfDocument.Open(saved.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                Check(back.Conformance == PdfAConformance.PdfA2u, "the converted file claims PDF/A-2u");
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>Phase 5 task 21: a linearized save read back with its pages and reported as linearized.</summary>
    private static void Linearize(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (pdf)
        {
            if (pdf.Pages.Count == 0 || pdf.IsEncrypted)
            {
                return;
            }

            try
            {
                using MemoryStream saved = new();
                pdf.Save(saved, new PdfSaveOptions { Linearize = true, Deterministic = true, AllowInvalidatingSignatures = true });
                using PdfDocument back = PdfDocument.Open(saved.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                Check(back.IsLinearized, "a linearized save reads as linearized");
                Check(back.Pages.Count == pdf.Pages.Count, "the linearized page count");
            }
            catch (PdfLimitException)
            {
            }
        }
    }

    /// <summary>Phase 5 task 21: hOCR read and drawn onto a page; a file that is not hOCR is refused with a PdfException.</summary>
    private static void Hocr(Stream input)
    {
        Ocr(input, (page, stream) => PdfOcrLayer.AddHocr(page, stream));
    }

    /// <summary>Phase 5 task 21: ALTO read and drawn onto a page; a file that is not ALTO is refused with a PdfException.</summary>
    private static void Alto(Stream input)
    {
        Ocr(input, (page, stream) => PdfOcrLayer.AddAlto(page, stream));
    }

    private static void Ocr(Stream input, Func<PdfPage, Stream, int> add)
    {
        byte[] bytes = ReadAll(input);
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 612, 792));
        int words;
        try
        {
            using MemoryStream stream = new(bytes);
            words = add(page, stream);
        }
        catch (PdfException)
        {
            return;
        }

        using MemoryStream saved = new();
        pdf.Save(saved, new PdfSaveOptions { Deterministic = true });
        using PdfDocument back = PdfDocument.Open(saved.ToArray());
        string text = back.Pages[0].ExtractText();
        Check(words == 0 || text.Length > 0, "added words come back as text");
    }

    private static void Merge(Stream input)
    {
        byte[] bytes = ReadAll(input);
        PdfDocument source;
        try
        {
            source = PdfDocument.Open(bytes, new PdfReadOptions { MaxDecodedStreamBytes = 64 << 20, MaxObjects = 1_000_000 });
        }
        catch (PdfException)
        {
            return;
        }

        using (source)
        {
            try
            {
                int[] chosen = [.. Enumerable.Range(0, Math.Min(20, source.Pages.Count))];
                List<(string?, PdfRectangle)> expected = [.. chosen.Select(i => Page(source.Pages[i]))];
                using PdfDocument merged = PdfDocument.Merge(source.ExtractPages(chosen), source.ExtractPages(chosen));

                // Task 19: the second copy's top-level fields were renamed, so no two share a name, and the outline reads.
                List<string> names = [.. merged.Form.Select(f => f.PartialName).Where(n => n.Length > 0)];
                Check(names.Count == names.Distinct().Count(), "unique top-level field names");
                _ = (merged.Outline.GetAllItems().Count, merged.NamedDestinations.GetNames().Count, merged.Layers.Count, merged.Attachments.Count);
                using MemoryStream whole = new();
                merged.Save(whole, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact, Deterministic = true });
                using PdfDocument reopened = PdfDocument.Open(whole.ToArray(), new PdfReadOptions { MaxObjects = 1_000_000 });
                Check(reopened.Pages.Count == 2 * chosen.Length, "the merged page count");
                for (int i = 0; i < reopened.Pages.Count; i++)
                {
                    Check(Page(reopened.Pages[i]) == expected[i % chosen.Length], $"merged page {i}");
                }

                using PdfDocument split = reopened.ExtractPages(Enumerable.Range(chosen.Length, chosen.Length));
                Check(split.Pages.Select(Page).SequenceEqual(expected), "the pages split off again");
            }
            catch (PdfLimitException)
            {
            }
        }

        // A page's content decoded and joined, or null when it cannot be decoded, and its media box.
        static (string?, PdfRectangle) Page(PdfPage page)
        {
            try
            {
                // An object whose value is a reference is followed to the end, as the copier follows it.
                string content = Follow(page.Dictionary["Contents"]) switch
                {
                    PdfStream stream => Convert.ToHexString(stream.GetDecodedBytes()),
                    PdfArray array => string.Concat(Enumerable.Range(0, array.Count).Select(i => Follow(array[i]) is PdfStream part ? Convert.ToHexString(part.GetDecodedBytes()) : "-")),
                    _ => "",
                };
                return (content, page.MediaBox);
            }
            catch (PdfException)
            {
                return (null, page.MediaBox);
            }
        }

        // An object whose value is a reference is followed to the end, as the copier follows it.
        static PdfObject? Follow(PdfObject? value)
        {
            for (int hop = 0; hop < 8 && value is PdfReference reference; hop++)
            {
                value = reference.Resolve();
            }

            return value;
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException($"{what} did not hold.");
            }
        }
    }
}
