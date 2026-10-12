using System.Globalization;
using System.Security.Cryptography;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf.Writing;

/// <summary>
/// Builds an incremental update (design 1.4, hub Q4): the changed and new objects, free entries for removed ones, and a
/// cross-reference section in the same style as the file's newest one, whose trailer points back with <c>/Prev</c>. The
/// original bytes are never touched; a document with no changes gets no update at all, so it saves byte for byte.
/// </summary>
internal static class IncrementalWriter
{
    private static readonly HashSet<string> OwnTrailerKeys = ["Size", "Prev", "XRefStm", "ID", "Type", "W", "Index", "Length", "Filter", "DecodeParms", "DL"];

    /// <summary>The bytes to append and where their cross-reference section starts, or <see langword="null"/> when nothing changed.</summary>
    /// <param name="document">The document.</param>
    /// <param name="baseLength">The length of the file the update goes after.</param>
    /// <param name="previous">Where that file's newest cross-reference section starts, and whether it is a stream.</param>
    /// <param name="endsWithNewline">Whether the file ends with an end-of-line marker; if not, one is added first.</param>
    /// <param name="options">The save options.</param>
    public static (byte[] Bytes, long Xref)? Update(PdfDocument document, long baseLength, (long Offset, bool IsStream) previous, bool endsWithNewline, PdfSaveOptions options)
    {
        List<IndirectEntry> changed = [.. document.Objects.Entries.Where(e => e.State is EntryState.Changed or EntryState.New or EntryState.Deleted)];
        if (changed.Count == 0 && !document.TrailerChanged)
        {
            return null;
        }

        ByteWriter output = new();
        if (!endsWithNewline)
        {
            output.Write((byte)'\n');
        }

        SortedDictionary<int, XrefWriterEntry> entries = [];
        foreach (IndirectEntry entry in changed.OrderBy(e => e.Number))
        {
            if (entry.State == EntryState.Deleted)
            {
                // A removed object becomes free with the next generation, so old references to it no longer match (7.5.4).
                entries[entry.Number] = new XrefWriterEntry(0, 0, Math.Min(entry.Generation + 1, 65535));
                continue;
            }

            entries[entry.Number] = new XrefWriterEntry(1, baseLength + output.Length, entry.Generation);
            output.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{entry.Number} {entry.Generation} obj\n"));
            // An encrypted document's update is encrypted with its key, all but the encryption dictionary (7.6.2).
            PdfObject value = document.Security is { } security && entry.Number != document.EncryptNumber
                ? Security.ObjectEncryption.EncryptedCopy(entry.Value!, entry.Number, entry.Generation, security, options.Deterministic)
                : entry.Value!;
            ObjectWriter.Write(value, output);
            output.WriteAscii("\nendobj\n");
        }

        // Past every number in use or named by a reference, so the update's own cross-reference stream takes none of them.
        int highest = document.Objects.Entries.Select(e => e.Number).DefaultIfEmpty(0).Max();
        int size = (int)Math.Max(Math.Max(highest + 1L, document.Objects.PeekNextNumber()), Math.Min(document.Trailer.GetInt("Size") ?? 0, int.MaxValue));
        long xref;
        if (previous.IsStream)
        {
            // The cross-reference stream is an object too: the next number.
            int number = size++;
            long offset = baseLength + output.Length;
            xref = offset;
            entries[number] = new XrefWriterEntry(1, offset, 0);
            PdfStream stream = XrefWriter.Stream(entries, Trailer(document, size, previous.Offset, output, options));
            output.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
            ObjectWriter.Write(stream, output);
            output.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"\nendobj\nstartxref\n{offset}\n%%EOF\n"));
        }
        else
        {
            long offset = baseLength + output.Length;
            xref = offset;
            output.Write(XrefWriter.Table(entries));
            PdfDictionary trailer = Trailer(document, size, previous.Offset, output, options);
            output.WriteAscii("trailer\n");
            ObjectWriter.Write(trailer, output);
            output.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"\nstartxref\n{offset}\n%%EOF\n"));
        }

        return (output.ToArray(), xref);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "The file ID is an identifier, not security; the spec suggests MD5 (14.4).")]
    private static PdfDictionary Trailer(PdfDocument document, int size, long previous, ByteWriter written, PdfSaveOptions options)
    {
        PdfDictionary trailer = new() { ["Size"] = size };
        foreach ((PdfName key, PdfObject value) in document.Trailer.Entries)
        {
            if (!OwnTrailerKeys.Contains(key.Value))
            {
                trailer[key] = value is PdfArray array ? array.Clone() : value is PdfDictionary dictionary ? dictionary.Clone() : value;
            }
        }

        trailer["Prev"] = previous;

        // The ID keeps its first part and gets a new second part on every save (14.4).
        byte[] content = MD5.HashData(written.WrittenSpan);
        byte[] second = options.Deterministic ? content : MD5.HashData([.. content, .. Guid.NewGuid().ToByteArray(), .. BitConverter.GetBytes(DateTime.UtcNow.Ticks)]);
        PdfString first = document.Trailer.Get<PdfArray>("ID") is { Count: 2 } id && id.Get<PdfString>(0) is PdfString existing
            ? PdfString.FromBytes(existing.Bytes, hex: true)
            : PdfString.FromBytes(second, hex: true);
        trailer["ID"] = new PdfArray([first, PdfString.FromBytes(second, hex: true)]);
        return trailer;
    }
}
