using System.Globalization;
using System.Text;

using Egap.Pdf.Filters;

namespace Egap.Pdf.Tests.Parsing;

/// <summary>Writes small PDF files byte by byte for the tests, recording where each object starts.</summary>
internal sealed class FileBuilder
{
    private readonly List<byte> bytes = [];
    private readonly SortedDictionary<int, (long Offset, int Generation)> section = [];

    public FileBuilder(string header = "%PDF-1.7\n%âãÏÓ\n")
    {
        Raw(header);
    }

    public long Position => bytes.Count;

    public Dictionary<int, long> Offsets { get; } = [];

    public FileBuilder Raw(string text)
    {
        bytes.AddRange(Encoding.Latin1.GetBytes(text));
        return this;
    }

    public FileBuilder Raw(byte[] data)
    {
        bytes.AddRange(data);
        return this;
    }

    public FileBuilder Object(int number, string body, int generation = 0)
    {
        Offsets[number] = Position;
        section[number] = (Position, generation);
        return Raw(Invariant($"{number} {generation} obj\n{body}\nendobj\n"));
    }

    public FileBuilder Stream(int number, string dictionary, byte[] data, int generation = 0)
    {
        Offsets[number] = Position;
        section[number] = (Position, generation);
        Raw(Invariant($"{number} {generation} obj\n<< {dictionary} /Length {data.Length} >>\nstream\n"));
        Raw(data);
        return Raw("\nendstream\nendobj\n");
    }

    /// <summary>An object stream holding the given objects; returns their indexes.</summary>
    public FileBuilder ObjectStream(int number, params (int Number, string Body)[] objects)
    {
        StringBuilder header = new();
        StringBuilder body = new();
        foreach ((int objectNumber, string text) in objects)
        {
            header.Append(Invariant($"{objectNumber} {body.Length} "));
            body.Append(text).Append(' ');
        }

        string content = header.ToString() + body;
        return Stream(number, Invariant($"/Type /ObjStm /N {objects.Length} /First {header.Length} /Filter /FlateDecode"), FlateFilter.Encode(Encoding.Latin1.GetBytes(content)));
    }

    /// <summary>A classic table of the objects written since the last section, plus extra free entries; returns its offset.</summary>
    public long XrefTable(string trailer, bool withZero = true, IEnumerable<(int Number, int Generation)>? free = null)
    {
        SortedDictionary<int, string> entries = [];
        if (withZero)
        {
            entries[0] = "0000000000 65535 f\r\n";
        }

        foreach ((int number, (long offset, int generation)) in section)
        {
            entries[number] = Invariant($"{offset:D10} {generation:D5} n\r\n");
        }

        foreach ((int number, int generation) in free ?? [])
        {
            entries[number] = Invariant($"0000000000 {generation:D5} f\r\n");
        }

        section.Clear();
        long at = Position;
        Raw("xref\n");
        foreach (List<int> run in Runs(entries.Keys))
        {
            Raw(Invariant($"{run[0]} {run.Count}\n"));
            foreach (int number in run)
            {
                Raw(entries[number]);
            }
        }

        Raw($"trailer\n<< {trailer} >>\n");
        return at;
    }

    /// <summary>
    /// A cross-reference stream as object <paramref name="number"/>, listing the objects written since the last section and
    /// the given compressed ones (number, object stream, index), Flate with a PNG Up predictor; returns its offset.
    /// </summary>
    public long XrefStream(int number, string trailer, IEnumerable<(int Number, int Stream, int Index)>? compressed = null, IEnumerable<int>? free = null)
    {
        SortedDictionary<int, (int Type, long Second, int Third)> entries = new() { [0] = (0, 0, 65535) };
        long at = Position;
        foreach ((int objectNumber, (long offset, int generation)) in section)
        {
            entries[objectNumber] = (1, offset, generation);
        }

        entries[number] = (1, at, 0);
        foreach ((int objectNumber, int stream, int position) in compressed ?? [])
        {
            entries[objectNumber] = (2, stream, position);
        }

        foreach (int objectNumber in free ?? [])
        {
            entries[objectNumber] = (0, 0, 1);
        }

        section.Clear();
        List<List<int>> runs = Runs(entries.Keys);
        List<byte> rows = [];
        byte[] previous = new byte[7];
        foreach (int objectNumber in runs.SelectMany(r => r))
        {
            (int type, long second, int third) = entries[objectNumber];
            byte[] row = [(byte)type, (byte)(second >> 24), (byte)(second >> 16), (byte)(second >> 8), (byte)second, (byte)(third >> 8), (byte)third];
            rows.Add(2);
            for (int i = 0; i < 7; i++)
            {
                rows.Add((byte)(row[i] - previous[i]));
            }

            previous = row;
        }

        string index = string.Join(' ', runs.Select(r => Invariant($"{r[0]} {r.Count}")));
        int size = entries.Keys.Max() + 1;
        Stream(number, Invariant($"/Type /XRef /W [1 4 2] /Index [{index}] /Size {size} /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 7 >> {trailer}"), FlateFilter.Encode([.. rows]));
        section.Clear();
        Offsets[number] = at;
        return at;
    }

    public FileBuilder StartXref(long offset)
    {
        return Raw(Invariant($"startxref\n{offset}\n%%EOF\n"));
    }

    public byte[] ToArray()
    {
        return [.. bytes];
    }

    private static List<List<int>> Runs(IEnumerable<int> numbers)
    {
        List<List<int>> runs = [];
        foreach (int number in numbers)
        {
            if (runs.Count > 0 && runs[^1][^1] == number - 1)
            {
                runs[^1].Add(number);
            }
            else
            {
                runs.Add([number]);
            }
        }

        return runs;
    }

    private static string Invariant(FormattableString text)
    {
        return text.ToString(CultureInfo.InvariantCulture);
    }
}
