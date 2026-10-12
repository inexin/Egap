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

    private void Emit(Stream output)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long position = 0;
        void Put(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            md5.AppendData(bytes);
            position += bytes.Length;
        }

        Put(Encoding.ASCII.GetBytes($"%PDF-{document.Version}\n"));
        Put([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        Dictionary<int, (long Offset, int Generation)> offsets = [];
        foreach ((PdfReference old, PdfReference renumbered) in order)
        {
            offsets[renumbered.ObjectNumber] = (position, renumbered.Generation);
            ByteWriter buffer = new();
            buffer.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{renumbered.ObjectNumber} {renumbered.Generation} obj\n"));
            ObjectWriter.Write(document.Objects[old], buffer, Remap);
            buffer.WriteAscii("\nendobj\n");
            Put(buffer.WrittenSpan);
        }

        int size = offsets.Count == 0 ? 1 : offsets.Keys.Max() + 1;
        long xref = position;
        ByteWriter table = new(32 + (size * 20));
        table.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {size}\n"));

        // Free entries form a list through their offsets, starting at object 0 and ending back at 0 (7.5.4).
        List<int> free = [.. Enumerable.Range(1, size - 1).Where(n => !offsets.ContainsKey(n))];
        table.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{(free.Count > 0 ? free[0] : 0):D10} 65535 f\r\n"));
        int nextFree = 0;
        for (int number = 1; number < size; number++)
        {
            if (offsets.TryGetValue(number, out (long Offset, int Generation) entry))
            {
                table.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{entry.Offset:D10} {entry.Generation:D5} n\r\n"));
            }
            else
            {
                nextFree++;
                int next = nextFree < free.Count ? free[nextFree] : 0;
                table.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{next:D10} 00001 f\r\n"));
            }
        }

        Put(table.WrittenSpan);
        byte[] contentHash = md5.GetHashAndReset();

        ByteWriter trailer = new();
        trailer.WriteAscii("trailer\n");
        ObjectWriter.Write(Trailer(size, contentHash), trailer, Remap);
        trailer.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"\nstartxref\n{xref}\n%%EOF\n"));
        output.Write(trailer.WrittenSpan);
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
