using Egap.Pdf.IO;

namespace Egap.Pdf.Parsing;

/// <summary>
/// Finds every <c>n g obj</c> header in a file by scanning its bytes, for rebuilding a cross-reference that cannot be read
/// and for finding objects that are not where it says. Later headers for the same number win, as later bytes are newer.
/// </summary>
internal sealed class ObjectScanner
{
    private const int Window = 64 * 1024;

    private ObjectScanner(Dictionary<int, (long Offset, int Generation)> objects, List<long> trailers)
    {
        Objects = objects;
        Trailers = trailers;
    }

    /// <summary>The newest header for each object number: where it starts and its generation.</summary>
    public Dictionary<int, (long Offset, int Generation)> Objects { get; }

    /// <summary>Where each <c>trailer</c> keyword is, in file order.</summary>
    public List<long> Trailers { get; }

    public static ObjectScanner Scan(PdfSource source, PdfReadOptions options)
    {
        Dictionary<int, (long, int)> objects = [];
        List<long> trailers = [];
        byte[] buffer = new byte[Window + 64];
        for (long start = 0; start < source.Length; start += Window)
        {
            // Windows overlap by 64 bytes so a header across the boundary is seen whole; matches are taken only from the
            // window's own part.
            long from = Math.Max(0, start - 64);
            int count = source.Read(from, buffer.AsSpan(0, (int)Math.Min(buffer.Length, source.Length - from)));
            Span<byte> span = buffer.AsSpan(0, count);
            int skip = (int)(start - from);
            for (int at = span[skip..].IndexOf("obj"u8); at >= 0;)
            {
                int position = skip + at;
                if (Header(span, position) is (long offset, int number, int generation))
                {
                    if (objects.Count >= options.MaxObjects && !objects.ContainsKey(number))
                    {
                        throw new PdfLimitException(nameof(PdfReadOptions.MaxObjects), $"The file holds more than {options.MaxObjects} objects.");
                    }

                    objects[number] = (from + offset, generation);
                }

                int next = span[(position + 3)..].IndexOf("obj"u8);
                at = next < 0 ? -1 : position + 3 + next - skip;
            }

            for (int at = span[skip..].IndexOf("trailer"u8); at >= 0;)
            {
                int position = skip + at;
                if (position == 0 || !CharClass.IsRegular(span[position - 1]))
                {
                    trailers.Add(from + position);
                }

                int next = span[(position + 7)..].IndexOf("trailer"u8);
                at = next < 0 ? -1 : position + 7 + next - skip;
            }
        }

        return new ObjectScanner(objects, trailers);
    }

    /// <summary>
    /// The highest object number that any <c>n g R</c> in the file names, objects and stream data alike, so new objects
    /// can take numbers no reference already names (a reference to a missing object would otherwise come to point at one).
    /// Stream data can hold false matches, which only make the number higher.
    /// </summary>
    public static int HighestReference(PdfSource source)
    {
        int highest = 0;
        byte[] buffer = new byte[Window + 64];
        for (long start = 0; start < source.Length; start += Window)
        {
            long from = Math.Max(0, start - 64);
            int count = source.Read(from, buffer.AsSpan(0, (int)Math.Min(buffer.Length, source.Length - from)));
            Span<byte> span = buffer.AsSpan(0, count);
            for (int at = (int)(start - from); at < span.Length; at++)
            {
                if (span[at] != 'R' || (at + 1 < span.Length && CharClass.IsRegular(span[at + 1])))
                {
                    continue;
                }

                // Back over white space, the generation, white space and the number.
                int i = at - 1;
                while (i >= 0 && CharClass.IsWhiteSpace(span[i]))
                {
                    i--;
                }

                int generationEnd = i;
                while (i >= 0 && CharClass.IsDigit(span[i]))
                {
                    i--;
                }

                if (i == generationEnd || generationEnd - i > 5 || i < 0 || !CharClass.IsWhiteSpace(span[i]))
                {
                    continue;
                }

                while (i >= 0 && CharClass.IsWhiteSpace(span[i]))
                {
                    i--;
                }

                int numberEnd = i;
                while (i >= 0 && CharClass.IsDigit(span[i]))
                {
                    i--;
                }

                if (i == numberEnd || numberEnd - i > 10)
                {
                    continue;
                }

                long number = 0;
                for (int d = i + 1; d <= numberEnd; d++)
                {
                    number = (number * 10) + (span[d] - '0');
                }

                if (number <= int.MaxValue - 1)
                {
                    highest = Math.Max(highest, (int)number);
                }
            }
        }

        return highest;
    }

    /// <summary>The header ending in the "obj" at <paramref name="at"/>: where it starts, the number and the generation.</summary>
    private static (long Offset, int Number, int Generation)? Header(Span<byte> span, int at)
    {
        if (at + 3 < span.Length && CharClass.IsRegular(span[at + 3]))
        {
            return null;
        }

        int i = at - 1;
        while (i >= 0 && CharClass.IsWhiteSpace(span[i]))
        {
            i--;
        }

        int generationEnd = i + 1;
        while (i >= 0 && CharClass.IsDigit(span[i]))
        {
            i--;
        }

        int generationStart = i + 1;
        if (generationEnd == generationStart || generationEnd - generationStart > 5 || i < 0 || !CharClass.IsWhiteSpace(span[i]))
        {
            return null;
        }

        while (i >= 0 && CharClass.IsWhiteSpace(span[i]))
        {
            i--;
        }

        int numberEnd = i + 1;
        while (i >= 0 && CharClass.IsDigit(span[i]))
        {
            i--;
        }

        int numberStart = i + 1;
        if (numberEnd == numberStart || numberEnd - numberStart > 10)
        {
            return null;
        }

        long number = Parse(span[numberStart..numberEnd]);
        long generation = Parse(span[generationStart..generationEnd]);
        if (number is < 1 or > int.MaxValue || generation > 65535)
        {
            return null;
        }

        return (numberStart, (int)number, (int)generation);
    }

    private static long Parse(Span<byte> digits)
    {
        long value = 0;
        foreach (byte digit in digits)
        {
            value = (value * 10) + (digit - '0');
        }

        return value;
    }
}
