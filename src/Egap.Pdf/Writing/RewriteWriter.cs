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

    private RewriteWriter(PdfDocument document, PdfSaveOptions options)
    {
        this.document = document;
        this.options = options;
    }

    public static void Write(PdfDocument document, Stream output, PdfSaveOptions options)
    {
        if (document.Trailer.ContainsKey("Encrypt"))
        {
            throw new NotSupportedException("Saving an encrypted document is not supported yet.");
        }

        RewriteWriter writer = new(document, options);
        writer.Collect();
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

        Put(Encoding.ASCII.GetBytes($"%PDF-{version}\n"));
        Put([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        Dictionary<int, XrefWriterEntry> entries = [];
        List<(PdfReference Number, PdfObject Value)> compressible = [];
        int next = order.Count == 0 ? 1 : order.Max(o => o.New.ObjectNumber) + 1;
        foreach ((PdfReference old, PdfReference renumbered) in order)
        {
            PdfObject value = document.Objects[old];

            // Streams and objects with a generation above 0 cannot go in an object stream (7.5.7).
            if (objectStreams && value is not PdfStream && renumbered.Generation == 0)
            {
                compressible.Add((renumbered, value));
                continue;
            }

            entries[renumbered.ObjectNumber] = new XrefWriterEntry(1, position, renumbered.Generation);
            Put(Indirect(renumbered.ObjectNumber, renumbered.Generation, value));
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
            Put(Indirect(streamNumber, 0, stream));
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
            default:
                // As the input was: object streams if it had any, a cross-reference stream if its newest section was one.
                bool hadObjectStreams = document.Xref.Entries.Any(e => e.Value.Kind == Parsing.XrefEntryKind.InObjectStream);
                bool hadXrefStream = document.Xref.Sections.Count > 0 && document.Xref.Sections[0].IsStream;
                return (hadObjectStreams, hadObjectStreams || hadXrefStream);
        }
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
            if (!OwnTrailerKeys.Contains(key.Value))
            {
                trailer[key] = value is PdfArray array ? array.Clone() : value is PdfDictionary dictionary ? dictionary.Clone() : value;
            }
        }

        // The ID keeps its first part and gets a new second part on every save (14.4).
        byte[] second = options.Deterministic ? contentHash : MD5.HashData([.. contentHash, .. Guid.NewGuid().ToByteArray(), .. BitConverter.GetBytes(DateTime.UtcNow.Ticks)]);
        PdfString first = document.Trailer.Get<PdfArray>("ID") is { Count: 2 } id && id.Get<PdfString>(0) is PdfString existing
            ? PdfString.FromBytes(existing.Bytes, hex: true)
            : PdfString.FromBytes(second, hex: true);
        trailer["ID"] = new PdfArray([first, PdfString.FromBytes(second, hex: true)]);
        return trailer;
    }
}
