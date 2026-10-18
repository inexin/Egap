using System.Security.Cryptography;
using System.Text;

namespace Egap.Pdf.Optimizing;

/// <summary>
/// Duplicate streams (Phase 7 design 7.1, Build Plan task 6): streams whose decoded bytes and dictionaries are the same,
/// whatever filters store them, such as one logo saved twice with different Flate settings, the same font program embedded
/// by every merged file, or a form drawn on each page. Each group is given the smallest encoding among its copies, so the
/// copies become byte for byte identical and the compact rewrite writes them once with every reference pointed at that one
/// object (Phase 2 Q4), as qpdf and pdfcpu merge them. Images, fonts, form XObjects, ICC profiles and content streams all
/// take part; page objects and other structure never merge.
/// </summary>
internal static class DuplicateStreams
{
    /// <summary>Entries that say how a stream is stored rather than what it holds.</summary>
    private static readonly HashSet<string> StorageKeys = ["Length", "Filter", "DecodeParms", "DL"];

    /// <summary>Makes duplicates identical; how many streams now duplicate another and the stored bytes they no longer need.</summary>
    public static (int Merged, long Bytes) Run(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Dictionary<long, List<PdfStream>> byLength = [];
        HashSet<PdfStream> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfReference reference in document.Objects)
        {
            if (reference.Generation != 0 || document.Objects[reference] is not PdfStream stream || !seen.Add(stream)
                || stream.Dictionary.GetName("Type")?.Value is "XRef" or "ObjStm")
            {
                continue;
            }

            long decoded;
            try
            {
                decoded = stream.GetDecodedBytes().LongLength;
            }
            catch (PdfException)
            {
                continue;
            }

            if (!byLength.TryGetValue(decoded, out List<PdfStream>? list))
            {
                byLength[decoded] = list = [];
            }

            list.Add(stream);
        }

        int merged = 0;
        long bytes = 0;
        foreach (List<PdfStream> sameLength in byLength.Values.Where(l => l.Count > 1))
        {
            foreach (IGrouping<string, PdfStream> group in sameLength.GroupBy(Key).Where(g => g.Count() > 1))
            {
                PdfStream smallest = group.MinBy(s => s.EncodedLength)!;
                byte[] data = smallest.GetEncodedBytes();
                foreach (PdfStream copy in group)
                {
                    merged += ReferenceEquals(copy, smallest) ? 0 : 1;
                    bytes += ReferenceEquals(copy, smallest) ? 0 : copy.EncodedLength;
                    if (ReferenceEquals(copy, smallest))
                    {
                        continue;
                    }

                    // The same entries in the same order as the smallest copy, so the rewrite sees one object: its own values
                    // where they say what the stream holds, the smallest's where they say how it is stored.
                    Dictionary<string, PdfObject> own = copy.Dictionary.Entries.ToDictionary(e => e.Key.Value, e => e.Value, StringComparer.Ordinal);
                    foreach (string key in own.Keys)
                    {
                        copy.Dictionary.Remove(key);
                    }

                    foreach (KeyValuePair<PdfName, PdfObject> entry in smallest.Dictionary.Entries)
                    {
                        if (entry.Key.Value == "Length")
                        {
                            continue;
                        }

                        PdfObject value = StorageKeys.Contains(entry.Key.Value) || !own.TryGetValue(entry.Key.Value, out PdfObject? mine) ? entry.Value : mine;
                        copy.Dictionary[entry.Key] = ReferenceEquals(value, entry.Value) ? value switch
                        {
                            PdfDictionary d => d.Clone(),
                            PdfArray a => a.Clone(),
                            _ => value,
                        } : value;
                    }

                    copy.SetEncodedBytes(data);
                }
            }
        }

        return (merged, bytes);
    }

    /// <summary>A hash of a stream's decoded bytes and of its dictionary without the entries that only say how it is stored.</summary>
    private static string Key(PdfStream stream)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(stream.GetDecodedBytes());
        StringBuilder dictionary = new();
        foreach (KeyValuePair<PdfName, PdfObject> entry in stream.Dictionary.Entries.Where(e => !StorageKeys.Contains(e.Key.Value)).OrderBy(e => e.Key.Value, StringComparer.Ordinal))
        {
            dictionary.Append('/').Append(entry.Key.Value).Append(' ').Append(entry.Value).Append('\n');
        }

        hash.AppendData(Encoding.UTF8.GetBytes(dictionary.ToString()));

        // An image codec's input is what GetDecodedBytes gives, so the codec and its parameters are part of what the stream
        // holds: two CCITT streams with the same data and another K are other pictures.
        if (stream.UndecodedFilter is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{stream.Dictionary.Get("Filter")}\n{stream.Dictionary.Get("DecodeParms")}"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
