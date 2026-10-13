using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf.Writing;

/// <summary>
/// Writes a whole document again (design 1.4, full rewrite): a header, every object reachable from the trailer, a classic
/// cross-reference table and a trailer. Objects are renumbered densely in the order they are reached (design Q6) unless
/// asked to keep their numbers; references to objects that are not written become <c>null</c>.
/// </summary>
internal sealed class RewriteWriter
{
    /// <summary>Trailer keys the writer sets itself or that belong to the old file's structure.</summary>
    private static readonly HashSet<string> OwnTrailerKeys = ["Size", "Prev", "XRefStm", "ID", "Encrypt", "Type", "W", "Index", "Length", "Filter", "DecodeParms", "DL"];

    private readonly PdfDocument document;
    private readonly PdfSaveOptions options;
    private readonly Dictionary<(int, int), PdfReference> numbers = [];
    private readonly List<(PdfReference Old, PdfReference New)> order = [];
    private Security.StandardSecurityHandler? security;

    /// <summary>A direct /Root or /Info made into an object of its own, as the spec requires of /Root (7.5.5).</summary>
    private readonly Dictionary<string, (PdfReference Reference, PdfDictionary Value)> madeIndirect = [];
    private PdfDictionary? encryptDictionary;
    private PdfReference? encryptReference;
    private bool newEncryption;

    private RewriteWriter(PdfDocument document, PdfSaveOptions options)
    {
        this.document = document;
        this.options = options;
    }

    public static void Write(PdfDocument document, Stream output, PdfSaveOptions options)
    {
        RewriteWriter writer = new(document, options);
        switch (options.Encryption.Kind)
        {
            case PdfEncryptionKind.Keep when document.Security is { } existing:
                writer.security = existing;
                writer.encryptDictionary = Kept(Inline(document.Trailer.Get<PdfDictionary>("Encrypt")!), existing);
                break;
            case PdfEncryptionKind.Aes256:
                PdfEncryption encryption = options.Encryption;
                (writer.security, writer.encryptDictionary) = Security.StandardSecurityHandler.CreateAes256(encryption.UserPassword, encryption.OwnerPassword, encryption.Permissions, new IO.WarningLog(strict: false));
                writer.newEncryption = true;
                break;
        }

        writer.Collect();
        if (options.Compression == PdfCompression.Compact && !options.KeepObjectNumbers)
        {
            writer.MergeIdentical();
        }

        writer.Emit(output);
    }

    /// <summary>Numbers every object to write, breadth first from the trailer.</summary>
    private void Collect()
    {
        Queue<PdfReference> pending = [];
        foreach ((PdfName key, PdfObject value) in document.Trailer.Entries)
        {
            if (!OwnTrailerKeys.Contains(key.Value))
            {
                if (key.Value is "Root" or "Info" && value is PdfDictionary direct)
                {
                    madeIndirect[key.Value] = (null!, direct);
                }

                Visit(value, pending);
            }
        }

        while (pending.TryDequeue(out PdfReference? reference))
        {
            Visit(document.Objects[reference], pending);
        }

        if (!options.RemoveUnused)
        {
            foreach (PdfReference reference in document.Objects)
            {
                if (!numbers.ContainsKey((reference.ObjectNumber, reference.Generation)) && document.Objects[reference] is not PdfNull)
                {
                    Assign(reference);
                    Visit(document.Objects[reference], pending);
                    while (pending.TryDequeue(out PdfReference? reached))
                    {
                        Visit(document.Objects[reached], pending);
                    }
                }
            }
        }

        // The direct dictionaries made into objects take the numbers after every other object's.
        int next = (order.Count == 0 ? 0 : order.Max(o => o.New.ObjectNumber)) + 1;
        foreach (string key in madeIndirect.Keys.ToArray())
        {
            madeIndirect[key] = (new PdfReference(next++, 0), madeIndirect[key].Value);
        }
    }

    /// <summary>
    /// Writes identical objects once (Phase 2 Q4): streams with the same dictionary and encoded bytes, and dictionaries and
    /// arrays with the same content, such as a logo and its fonts in 50 merged invoices. Objects are compared with their
    /// references replaced by the class of the object they lead to, repeated until nothing more merges, so a font whose
    /// descriptor and font file merge merges too. Page tree nodes, pages, annotations, form fields and the catalog stay
    /// apart, since sharing them would change what a document is. The objects left are renumbered densely.
    /// </summary>
    private void MergeIdentical()
    {
        int count = order.Count;
        int[] classOf = [.. Enumerable.Range(0, count)];
        Dictionary<(int, int), int> indexOf = [];
        for (int i = 0; i < count; i++)
        {
            indexOf[(order[i].Old.ObjectNumber, order[i].Old.Generation)] = i;
        }

        bool[] mergeable = new bool[count];
        byte[]?[] dataHash = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            PdfObject value = document.Objects[order[i].Old];
            mergeable[i] = order[i].Old.Generation == 0 && value switch
            {
                PdfStream stream => !Structural(stream.Dictionary),
                PdfDictionary dictionary => !Structural(dictionary),
                PdfArray => true,
                _ => false,
            };
            if (mergeable[i] && value is PdfStream data)
            {
                dataHash[i] = SHA256.HashData(data.GetEncodedBytes());
            }
        }

        PdfReference? ByClass(PdfReference reference)
        {
            return indexOf.TryGetValue((reference.ObjectNumber, reference.Generation), out int i) ? new PdfReference(classOf[i] + 1, 0) : null;
        }

        for (int round = 0; round < 64; round++)
        {
            Dictionary<string, int> first = [];
            bool changed = false;
            int[] next = (int[])classOf.Clone();
            for (int i = 0; i < count; i++)
            {
                if (!mergeable[i])
                {
                    continue;
                }

                PdfObject value = document.Objects[order[i].Old];
                ByteWriter signature = new();
                // A stream's /Length is written from its data, so it takes no part in the comparison.
                ObjectWriter.Write(value is PdfStream stream ? WithoutLength(stream.Dictionary) : value, signature, ByClass);
                string key = (value is PdfStream ? "S" : "D") + Convert.ToHexString(SHA256.HashData(signature.WrittenSpan)) + (dataHash[i] is byte[] hash ? Convert.ToHexString(hash) : "");
                if (first.TryGetValue(key, out int representative))
                {
                    if (next[i] != classOf[representative])
                    {
                        next[i] = classOf[representative];
                        changed = true;
                    }
                }
                else
                {
                    first[key] = i;
                }
            }

            classOf = next;
            if (!changed)
            {
                break;
            }
        }

        // A class can have merged into an earlier one after its members joined it; follow to the class that stayed.
        for (int i = 0; i < count; i++)
        {
            int root = classOf[i];
            while (classOf[root] != root)
            {
                root = classOf[root];
            }

            classOf[i] = root;
        }

        // Keep the first object of each class, renumbered densely in order; the others write as their class's object.
        List<(PdfReference Old, PdfReference New)> kept = [];
        PdfReference[] newOf = new PdfReference[count];
        for (int i = 0; i < count; i++)
        {
            if (classOf[i] == i)
            {
                newOf[i] = new PdfReference(kept.Count + 1, 0);
                kept.Add((order[i].Old, newOf[i]));
            }
        }

        for (int i = 0; i < count; i++)
        {
            numbers[(order[i].Old.ObjectNumber, order[i].Old.Generation)] = newOf[classOf[i]];
        }

        order.Clear();
        order.AddRange(kept);
    }

    private static PdfDictionary WithoutLength(PdfDictionary dictionary)
    {
        if (dictionary.Get("Length") is null)
        {
            return dictionary;
        }

        PdfDictionary copy = dictionary.Clone();
        copy.Remove("Length");
        return copy;
    }

    /// <summary>Whether a dictionary is part of the document's structure, which merging must not share.</summary>
    private static bool Structural(PdfDictionary dictionary)
    {
        return dictionary.GetName("Type")?.Value is "Catalog" or "Pages" or "Page" or "Annot" or "Outlines" or "Sig"
            || dictionary.Get("Parent") is not null || dictionary.Get("Kids") is not null || dictionary.Get("FT") is not null
            || dictionary.Get("T") is not null || dictionary.Get("Subtype") is PdfName { Value: "Widget" };
    }

    /// <summary>Finds the references in a value, numbering each new one and queuing its object.</summary>
    private void Visit(PdfObject root, Queue<PdfReference> pending)
    {
        Stack<PdfObject> values = new([root]);
        while (values.TryPop(out PdfObject? value))
        {
            switch (value)
            {
                case PdfReference reference when !numbers.ContainsKey((reference.ObjectNumber, reference.Generation)):
                    // A reference to a missing object, or to an object that is null, is written as null.
                    if (document.Objects[reference] is not PdfNull)
                    {
                        Assign(reference);
                        pending.Enqueue(reference);
                    }

                    break;
                case PdfArray array:
                    for (int i = array.Count - 1; i >= 0; i--)
                    {
                        values.Push(array[i]);
                    }

                    break;
                case PdfDictionary dictionary:
                    PushEntries(dictionary, values, skipLength: false);
                    break;
                case PdfStream stream:
                    // The writer gives every stream a direct /Length, so an indirect length object is not reached through it.
                    PushEntries(stream.Dictionary, values, skipLength: true);
                    break;
            }
        }
    }

    private static void PushEntries(PdfDictionary dictionary, Stack<PdfObject> values, bool skipLength)
    {
        foreach ((PdfName key, PdfObject item) in dictionary.Entries.Reverse())
        {
            if (!(skipLength && key.Value == "Length"))
            {
                values.Push(item);
            }
        }
    }

    private void Assign(PdfReference reference)
    {
        PdfReference renumbered = options.KeepObjectNumbers ? new PdfReference(reference.ObjectNumber, reference.Generation) : new PdfReference(order.Count + 1, 0);
        numbers[(reference.ObjectNumber, reference.Generation)] = renumbered;
        order.Add((reference, renumbered));
    }

    private PdfReference? Remap(PdfReference reference)
    {
        if (ReferenceEquals(reference, encryptReference) || madeIndirect.Values.Any(m => ReferenceEquals(m.Reference, reference)))
        {
            return reference;
        }

        return numbers.GetValueOrDefault((reference.ObjectNumber, reference.Generation));
    }

    /// <summary>The most objects put in one object stream.</summary>
    public const int ObjectsPerStream = 100;

    private void Emit(Stream output)
    {
        (bool objectStreams, bool xrefStream) = Style();
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long position = 0;
        void Put(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            md5.AppendData(bytes);
            position += bytes.Length;
        }

        // Object and cross-reference streams need PDF 1.5; the version is raised only when they are written (hub Q11).
        string version = document.Version;
        if ((objectStreams || xrefStream) && string.CompareOrdinal(version, "1.5") < 0)
        {
            version = "1.5";
        }

        // AES-256 is PDF 2.0, or PDF 1.7 with Adobe's extension level 8 declared in the catalog, as qpdf and Acrobat write it.
        bool declareExtension = newEncryption && string.CompareOrdinal(version, "2.0") < 0;
        if (declareExtension && string.CompareOrdinal(version, "1.7") < 0)
        {
            version = "1.7";
        }

        int? rootNumber = document.Trailer["Root"] is PdfReference root ? Remap(root)?.ObjectNumber : null;

        Put(Encoding.ASCII.GetBytes($"%PDF-{version}\n"));
        Put([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        Dictionary<int, XrefWriterEntry> entries = [];
        List<(PdfReference Number, PdfObject Value)> compressible = [];
        int next = order.Count == 0 ? 1 : order.Max(o => o.New.ObjectNumber) + 1;
        next += madeIndirect.Count;
        if (madeIndirect.TryGetValue("Root", out (PdfReference Reference, PdfDictionary Value) madeRoot))
        {
            rootNumber = madeRoot.Reference.ObjectNumber;
        }

        foreach ((PdfReference reference, PdfDictionary made) in madeIndirect.Values)
        {
            PdfDictionary value = declareExtension && reference.ObjectNumber == rootNumber ? WithExtension(made) : made;
            entries[reference.ObjectNumber] = new XrefWriterEntry(1, position, 0);
            Put(Indirect(reference.ObjectNumber, 0, Encrypted(value, reference.ObjectNumber, 0)));
        }

        foreach ((PdfReference old, PdfReference renumbered) in order)
        {
            PdfObject value = document.Objects[old];
            if (declareExtension && renumbered.ObjectNumber == rootNumber && value is PdfDictionary catalog)
            {
                value = WithExtension(catalog);
            }

            // Streams and objects with a generation above 0 cannot go in an object stream (7.5.7).
            if (objectStreams && value is not PdfStream && renumbered.Generation == 0)
            {
                compressible.Add((renumbered, value));
                continue;
            }

            entries[renumbered.ObjectNumber] = new XrefWriterEntry(1, position, renumbered.Generation);
            Put(Indirect(renumbered.ObjectNumber, renumbered.Generation, Encrypted(value, renumbered.ObjectNumber, renumbered.Generation)));
        }

        // The encryption dictionary is an object of its own, never encrypted.
        if (encryptDictionary is not null)
        {
            encryptReference = new PdfReference(next++, 0);
            entries[encryptReference.ObjectNumber] = new XrefWriterEntry(1, position, 0);
            Put(Indirect(encryptReference.ObjectNumber, 0, encryptDictionary));
        }

        for (int start = 0; start < compressible.Count; start += ObjectsPerStream)
        {
            int streamNumber = next++;
            List<(PdfReference Number, PdfObject Value)> group = compressible.GetRange(start, Math.Min(ObjectsPerStream, compressible.Count - start));
            ByteWriter header = new();
            ByteWriter body = new();
            for (int index = 0; index < group.Count; index++)
            {
                header.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{group[index].Number.ObjectNumber} {body.Length} "));
                ObjectWriter.Write(group[index].Value, body, Remap);
                body.Write((byte)'\n');
                entries[group[index].Number.ObjectNumber] = new XrefWriterEntry(2, streamNumber, index);
            }

            PdfStream stream = new(new PdfDictionary { ["Type"] = PdfName.Get("ObjStm"), ["N"] = group.Count, ["First"] = header.Length, ["Filter"] = PdfName.Get("FlateDecode") });
            stream.SetEncodedBytes(Filters.FlateFilter.Encode([.. header.WrittenSpan, .. body.WrittenSpan]));
            entries[streamNumber] = new XrefWriterEntry(1, position, 0);

            // Objects in an object stream are not encrypted on their own; the object stream is (7.6.2).
            Put(Indirect(streamNumber, 0, Encrypted(stream, streamNumber, 0)));
        }

        if (xrefStream)
        {
            int number = next++;
            entries[number] = new XrefWriterEntry(1, position, 0);
            long xref = position;
            byte[] contentHash = md5.GetHashAndReset();
            PdfStream stream = XrefWriter.Stream(XrefWriter.WithFreeList(entries, number + 1), Trailer(number + 1, contentHash));
            output.Write(Indirect(number, 0, stream));
            output.Write(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"startxref\n{xref}\n%%EOF\n")));
        }
        else
        {
            int size = entries.Count == 0 ? 1 : entries.Keys.Max() + 1;
            long xref = position;
            Put(XrefWriter.Table(XrefWriter.WithFreeList(entries, size)));
            byte[] contentHash = md5.GetHashAndReset();
            ByteWriter trailer = new();
            trailer.WriteAscii("trailer\n");
            ObjectWriter.Write(Trailer(size, contentHash), trailer, Remap);
            trailer.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"\nstartxref\n{xref}\n%%EOF\n"));
            output.Write(trailer.WrittenSpan);
        }
    }

    /// <summary>Whether to write object streams and a cross-reference stream (design Q7).</summary>
    private (bool ObjectStreams, bool XrefStream) Style()
    {
        switch (options.Compression)
        {
            case PdfCompression.Compact:
                return (true, true);
            case PdfCompression.None:
                return (false, false);
            case PdfCompression.Preserve when document.IsNew:
                return (true, true);
            default:
                // As the input was: object streams if it had any, a cross-reference stream if its newest section was one.
                bool hadObjectStreams = document.Xref.Entries.Any(e => e.Value.Kind == Parsing.XrefEntryKind.InObjectStream);
                bool hadXrefStream = document.Xref.Sections.Count > 0 && document.Xref.Sections[0].IsStream;
                return (hadObjectStreams, hadObjectStreams || hadXrefStream);
        }
    }

    private PdfObject Encrypted(PdfObject value, int number, int generation)
    {
        return security is null ? value : Security.ObjectEncryption.EncryptedCopy(value, number, generation, security, options.Deterministic);
    }

    /// <summary>
    /// A copy of the catalog declaring Adobe's extension level 8, which AES-256 needs before PDF 2.0; a higher level already
    /// declared is kept.
    /// </summary>
    private static PdfDictionary WithExtension(PdfDictionary catalog)
    {
        if (catalog.Get<PdfDictionary>("Extensions")?.Get<PdfDictionary>("ADBE")?.GetInt("ExtensionLevel") >= 8)
        {
            return catalog;
        }

        PdfDictionary copy = catalog.Clone();
        PdfDictionary extensions = copy.Get<PdfDictionary>("Extensions")?.Clone() ?? [];
        extensions["ADBE"] = new PdfDictionary { ["BaseVersion"] = PdfName.Get("1.7"), ["ExtensionLevel"] = 8 };
        copy["Extensions"] = extensions;
        return copy;
    }

    /// <summary>
    /// A copy of the encryption dictionary with every reference in it replaced by what it points to: the dictionary is
    /// written on its own, unencrypted, so nothing it uses may be another (encrypted, renumbered) object.
    /// </summary>
    /// <summary>
    /// The kept encryption dictionary with the key length that opening used, so a length that needed repair reads the same
    /// way again without a warning.
    /// </summary>
    private static PdfDictionary Kept(PdfDictionary encrypt, Security.StandardSecurityHandler handler)
    {
        if (handler.Revision <= 4)
        {
            encrypt["Length"] = handler.KeyLength;
            foreach ((_, PdfObject value) in encrypt.Get<PdfDictionary>("CF")?.Entries ?? [])
            {
                if (value is PdfDictionary { } definition && definition.ContainsKey("Length"))
                {
                    definition["Length"] = handler.KeyLength;
                }
            }
        }

        return encrypt;
    }

    /// <summary>
    /// The file ID for a save: the first part kept and a new second part (14.4). Kept encryption of revisions 2 to 4 made its
    /// key from the first part as the file was opened, so that part stays exactly, even when the /ID array was malformed.
    /// </summary>
    internal static PdfArray FileId(PdfDocument document, Security.StandardSecurityHandler? keptSecurity, byte[] second)
    {
        PdfString first = keptSecurity is { Revision: <= 4 }
            ? PdfString.FromBytes(keptSecurity.FirstId, hex: true)
            : document.Trailer.Get<PdfArray>("ID") is { Count: 2 } id && id.Get<PdfString>(0) is PdfString existing
                ? PdfString.FromBytes(existing.Bytes, hex: true)
                : PdfString.FromBytes(second, hex: true);
        return new PdfArray([first, PdfString.FromBytes(second, hex: true)]);
    }

    /// <summary>How deep the inlined encryption dictionary may nest.</summary>
    private const int MaxInlineDepth = 16;

    private static PdfDictionary Inline(PdfDictionary encrypt)
    {
        PdfDictionary copy = encrypt.Clone();
        Stack<(PdfObject Value, int Depth)> pending = new([(copy, 1)]);
        int resolved = 0;
        while (pending.TryPop(out (PdfObject Value, int Depth) next))
        {
            if (next.Value is PdfDictionary dictionary)
            {
                foreach ((PdfName key, PdfObject item) in dictionary.Entries.ToArray())
                {
                    PdfObject direct = Direct(item, next.Depth, ref resolved);
                    if (!ReferenceEquals(direct, item))
                    {
                        dictionary[key] = direct;
                    }

                    pending.Push((direct, next.Depth + 1));
                }
            }
            else if (next.Value is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    PdfObject direct = Direct(array[i], next.Depth, ref resolved);
                    if (!ReferenceEquals(direct, array[i]))
                    {
                        array[i] = direct;
                    }

                    pending.Push((direct, next.Depth + 1));
                }
            }
        }

        return copy;
    }

    private static PdfObject Direct(PdfObject item, int depth, ref int resolved)
    {
        // The handler reads nothing deeper than a crypt filter's entries; below that, containers and references are dropped,
        // so inlining references never nests the dictionary deeper than a reader allows.
        if (depth > MaxInlineDepth && item is PdfReference or PdfArray or PdfDictionary)
        {
            return PdfNull.Instance;
        }

        if (item is not PdfReference)
        {
            return item;
        }

        // A guard against references that loop: the dictionary is small, so a few hundred are plenty.
        if (++resolved > 256)
        {
            return PdfNull.Instance;
        }

        return item.Resolve() switch
        {
            PdfDictionary dictionary => dictionary.Clone(),
            PdfArray array => array.Clone(),
            PdfStream => PdfNull.Instance,
            PdfObject other => other,
        };
    }

    private byte[] Indirect(int number, int generation, PdfObject value)
    {
        ByteWriter buffer = new();
        buffer.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{number} {generation} obj\n"));
        ObjectWriter.Write(value, buffer, Remap);
        buffer.WriteAscii("\nendobj\n");
        return buffer.ToArray();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "The file ID is an identifier, not security; the spec suggests MD5 (14.4).")]
    private PdfDictionary Trailer(int size, byte[] contentHash)
    {
        PdfDictionary trailer = new() { ["Size"] = size };
        foreach ((PdfName key, PdfObject value) in document.Trailer.Entries)
        {
            if (madeIndirect.TryGetValue(key.Value, out (PdfReference Reference, PdfDictionary Value) made))
            {
                trailer[key] = made.Reference;
            }
            else if (!OwnTrailerKeys.Contains(key.Value))
            {
                trailer[key] = value is PdfArray array ? array.Clone() : value is PdfDictionary dictionary ? dictionary.Clone() : value;
            }
        }

        // The ID keeps its first part and gets a new second part on every save (14.4).
        byte[] second = options.Deterministic ? contentHash : MD5.HashData([.. contentHash, .. Guid.NewGuid().ToByteArray(), .. BitConverter.GetBytes(DateTime.UtcNow.Ticks)]);
        trailer["ID"] = FileId(document, newEncryption ? null : security, second);
        if (encryptReference is not null)
        {
            trailer["Encrypt"] = encryptReference;
        }

        return trailer;
    }
}
