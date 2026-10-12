using System.Text;

using Egap.Pdf.Objects;

namespace Egap.Pdf.Fuzz;

/// <summary>Checks that two documents hold the same objects reachable from their trailers, whatever their numbers.</summary>
internal static class GraphCheck
{
    public static void AssertSame(PdfDocument expected, PdfDocument actual)
    {
        Dictionary<(int, int), (int, int)> seen = [];
        Stack<(PdfObject A, PdfObject B, string Path)> pending = new();
        foreach ((PdfName key, PdfObject value) in expected.Trailer.Entries)
        {
            if (key.Value is not ("Size" or "Prev" or "XRefStm" or "ID" or "Encrypt" or "Type" or "W" or "Index" or "Length" or "Filter" or "DecodeParms" or "DL"))
            {
                pending.Push((value, actual.Trailer[key] ?? PdfNull.Instance, "/" + key.Value));
            }
        }

        while (pending.TryPop(out (PdfObject A, PdfObject B, string Path) item))
        {
            (PdfObject a, PdfObject b, string path) = item;

            // A rewrite makes a direct /Root or /Info an object of its own.
            if (a is not PdfReference && b is PdfReference indirect)
            {
                b = indirect.Resolve();
            }

            if (a is PdfReference ra)
            {
                PdfObject target = ra.Resolve();
                if (target is PdfNull)
                {
                    Check(b is PdfNull || b.Resolve() is PdfNull, path, $"{b} for a missing object");
                    continue;
                }

                if (b is not PdfReference rb)
                {
                    throw new InvalidOperationException($"{path}: a reference became {b}.");
                }

                if (seen.TryGetValue((ra.ObjectNumber, ra.Generation), out (int, int) mapped))
                {
                    Check(mapped == (rb.ObjectNumber, rb.Generation), path, "a reference points elsewhere");
                    continue;
                }

                seen[(ra.ObjectNumber, ra.Generation)] = (rb.ObjectNumber, rb.Generation);
                pending.Push((target, rb.Resolve(), path + "*"));
                continue;
            }

            switch (a)
            {
                case PdfArray arrayA when b is PdfArray arrayB && arrayA.Count == arrayB.Count:
                    for (int i = 0; i < arrayA.Count; i++)
                    {
                        pending.Push((arrayA[i], arrayB[i], $"{path}[{i}]"));
                    }

                    break;
                case PdfDictionary dictionaryA when b is PdfDictionary dictionaryB:
                    Dictionaries(dictionaryA, dictionaryB, path, pending, skipLength: false);
                    break;
                case PdfStream streamA when b is PdfStream streamB:
                    Check(streamA.GetEncodedBytes().AsSpan().SequenceEqual(streamB.GetEncodedBytes()), path, "stream data differs");
                    Dictionaries(streamA.Dictionary, streamB.Dictionary, path, pending, skipLength: true);
                    break;
                case PdfArray or PdfDictionary or PdfStream:
                    throw new InvalidOperationException($"{path}: {Describe(a)} became {Describe(b)}.");
                default:
                    Check(Describe(a) == Describe(b), path, $"{Describe(a)} became {Describe(b)}");
                    break;
            }
        }
    }

    private static void Dictionaries(PdfDictionary a, PdfDictionary b, string path, Stack<(PdfObject, PdfObject, string)> pending, bool skipLength)
    {
        // New AES-256 encryption declares an extension level in the catalog's /Extensions before PDF 2.0, so that key is not compared.
        List<PdfName> keys = [.. a.Keys.Where(k => !(skipLength && k.Value == "Length") && k.Value != "Extensions")];
        Check(keys.SequenceEqual(b.Keys.Where(k => !(skipLength && k.Value == "Length") && k.Value != "Extensions")), path, "keys differ");
        foreach (PdfName key in keys)
        {
            pending.Push((a[key]!, b[key]!, $"{path}/{key.Value}"));
        }
    }

    private static void Check(bool same, string path, string message)
    {
        if (!same)
        {
            throw new InvalidOperationException($"{path}: {message}.");
        }
    }

    private static string Describe(PdfObject value)
    {
        string text = Encoding.Latin1.GetString(ObjectWriter.ToBytes(value, includeStreamData: false));
        return text.Length > 80 ? text[..80] : text;
    }
}
