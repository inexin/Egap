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

    public static IEnumerable<string> Names => ["lexer", "parser", "filters", "streams", "xref", "document", "rewrite", "incremental", "trees"];

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
}
