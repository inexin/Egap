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

    public static IEnumerable<string> Names => ["lexer", "parser", "filters", "streams", "xref", "document", "rewrite", "incremental", "trees", "pages", "pageedits", "navigation", "outlines", "xmp", "metadata", "attachments", "layers", "annotations", "forms"];

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
            _ => throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name)),
        };
    }

    private static byte[] ReadAll(Stream input)
    {
        using MemoryStream copy = new();
        input.CopyTo(copy);
        return copy.ToArray();
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
                            pdf.Pages.RemoveAt(removed);
                            expected.RemoveAt(removed);
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
                    switch (random.Next(4))
                    {
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
    /// Form reading (Phase 2 task 14): every public property of up to 2,000 fields read leniently, with their kids and widgets,
    /// and every widget on the first 50 pages led back to its field.
    /// </summary>
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
}
