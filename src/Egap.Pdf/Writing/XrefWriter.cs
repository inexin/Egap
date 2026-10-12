using System.Globalization;

using Egap.Pdf.IO;

namespace Egap.Pdf.Writing;

/// <summary>A cross-reference entry as a stream stores it: type, then offset or object stream, then generation or index.</summary>
/// <param name="Type">0 free, 1 in the file, 2 in an object stream.</param>
/// <param name="Second">The next free number, the offset, or the object stream's number.</param>
/// <param name="Third">The generation, or the index in the object stream.</param>
internal readonly record struct XrefWriterEntry(int Type, long Second, int Third);

/// <summary>Writes cross-reference sections, as tables (7.5.4) or streams (7.5.8), for whichever numbers are given.</summary>
internal static class XrefWriter
{
    /// <summary>
    /// The entries for 0 to <paramref name="size"/> - 1, with the unused numbers made free entries linked through their
    /// second field from object 0 (7.5.4).
    /// </summary>
    public static SortedDictionary<int, XrefWriterEntry> WithFreeList(IReadOnlyDictionary<int, XrefWriterEntry> entries, int size)
    {
        SortedDictionary<int, XrefWriterEntry> all = new(entries.ToDictionary());
        List<int> free = [.. Enumerable.Range(1, size - 1).Where(n => !entries.ContainsKey(n))];
        all[0] = new XrefWriterEntry(0, free.Count > 0 ? free[0] : 0, 65535);
        for (int i = 0; i < free.Count; i++)
        {
            all[free[i]] = new XrefWriterEntry(0, i + 1 < free.Count ? free[i + 1] : 0, 1);
        }

        return all;
    }

    /// <summary>A table with one subsection per run of consecutive numbers.</summary>
    public static byte[] Table(SortedDictionary<int, XrefWriterEntry> entries)
    {
        ByteWriter table = new(32 + (entries.Count * 20));
        table.WriteAscii("xref\n");
        foreach ((int start, int count) in Runs(entries.Keys))
        {
            table.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{start} {count}\n"));
            for (int number = start; number < start + count; number++)
            {
                XrefWriterEntry entry = entries[number];
                string type = entry.Type == 0 ? "f" : "n";
                table.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{entry.Second:D10} {entry.Third:D5} {type}\r\n"));
            }
        }

        return table.ToArray();
    }

    /// <summary>
    /// A cross-reference stream with the smallest field widths, Flate with the PNG Up predictor, and an /Index unless the
    /// entries run from 0 to /Size - 1. The given dictionary (the trailer's keys) gets the stream's own keys added.
    /// </summary>
    public static PdfStream Stream(SortedDictionary<int, XrefWriterEntry> entries, PdfDictionary dictionary)
    {
        int second = Width(entries.Values.Max(e => e.Second));
        int third = Width(entries.Values.Max(e => e.Third));
        int row = 1 + second + third;
        byte[] rows = new byte[entries.Count * (row + 1)];
        byte[] previous = new byte[row];
        byte[] current = new byte[row];
        int at = 0;
        foreach (XrefWriterEntry entry in entries.Values)
        {
            current[0] = (byte)entry.Type;
            for (int i = 0; i < second; i++)
            {
                current[1 + i] = (byte)(entry.Second >> (8 * (second - 1 - i)));
            }

            for (int i = 0; i < third; i++)
            {
                current[1 + second + i] = (byte)(entry.Third >> (8 * (third - 1 - i)));
            }

            rows[at++] = 2;
            for (int i = 0; i < row; i++)
            {
                rows[at++] = (byte)(current[i] - previous[i]);
            }

            (previous, current) = (current, previous);
        }

        dictionary.Add("Type", PdfName.Get("XRef"));
        dictionary.Add("W", new PdfArray([1, second, third]));
        List<(int Start, int Count)> runs = Runs(entries.Keys);
        if (runs is not [(0, int count)] || count != dictionary.GetInt("Size"))
        {
            dictionary.Add("Index", new PdfArray(runs.SelectMany(r => new PdfObject[] { r.Start, r.Count })));
        }

        dictionary.Add("Filter", PdfName.Get("FlateDecode"));
        dictionary.Add("DecodeParms", new PdfDictionary { ["Predictor"] = 12, ["Columns"] = row });
        PdfStream stream = new(dictionary);
        stream.SetEncodedBytes(Filters.FlateFilter.Encode(rows));
        return stream;
    }

    private static List<(int Start, int Count)> Runs(IEnumerable<int> numbers)
    {
        List<(int Start, int Count)> runs = [];
        foreach (int number in numbers)
        {
            if (runs.Count > 0 && runs[^1].Start + runs[^1].Count == number)
            {
                runs[^1] = (runs[^1].Start, runs[^1].Count + 1);
            }
            else
            {
                runs.Add((number, 1));
            }
        }

        return runs;
    }

    private static int Width(long value)
    {
        int width = 1;
        while (width < 8 && value >= 1L << (8 * width))
        {
            width++;
        }

        return width;
    }
}
