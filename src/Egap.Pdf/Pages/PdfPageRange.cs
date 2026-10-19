using System.Globalization;

namespace Egap.Pdf;

/// <summary>
/// A choice of pages written as text, such as <c>"1,3,5-10,z"</c>, that is turned into page numbers once the page count is
/// known. Pass it to <see cref="PdfDocument.ExtractPages(PdfPageRange)"/>, <see cref="PdfStamp.PageRange"/> and the
/// page renderers, or turn it into page indices with <see cref="Resolve"/> for
/// <see cref="PdfPageCollection.Import(PdfDocument, IEnumerable{int}?, int?, PdfImportOptions?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The text is a comma-separated list of parts. A part is a page number (1 is the first page), <c>z</c> for the last page,
/// <c>r</c> and a number counting from the end (<c>r1</c> is the last page, <c>r2</c> the one before), or a range of two of
/// these joined by a dash. A range runs backwards when its first page comes after its last (<c>"10-1"</c>). A part can end
/// in <c>:odd</c> or <c>:even</c> to keep every other page of it, counted within the part. A part that starts with
/// <c>x</c> takes its pages out of what the earlier parts chose (<c>"1-z,x3-4"</c>). An empty text or <c>"all"</c> chooses
/// every page.
/// </para>
/// <para>Pages are kept in the order written and may repeat, so <c>"1,1"</c> chooses the first page twice.</para>
/// </remarks>
/// <example>
/// <code>
/// using PdfDocument first = source.ExtractPages(PdfPageRange.Parse("1-3,z"));
/// IReadOnlyList&lt;int&gt; indices = PdfPageRange.Parse("2-z:even").Resolve(source.Pages.Count);
/// </code>
/// </example>
public sealed class PdfPageRange
{
    private readonly Part[] parts;

    private PdfPageRange(string text, Part[] parts)
    {
        Text = text;
        this.parts = parts;
    }

    /// <summary>Gets a range of every page.</summary>
    public static PdfPageRange All { get; } = new("all", []);

    /// <summary>Gets the range as it was written.</summary>
    public string Text { get; }

    /// <summary>Reads a range; see the remarks on <see cref="PdfPageRange"/> for what it may hold.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="FormatException">The text is not a page range.</exception>
    public static PdfPageRange Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out PdfPageRange? range) ? range : throw new FormatException($"'{text}' is not a page range such as 1,3,5-10,z.");
    }

    /// <summary>Reads a range as <see cref="Parse"/> does; false when the text is not one.</summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PdfPageRange? range)
    {
        range = null;
        if (text is null)
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            range = new PdfPageRange(text, []);
            return true;
        }

        List<Part> parts = [];
        foreach (string raw in trimmed.Split(','))
        {
            ReadOnlySpan<char> part = raw.AsSpan().Trim();
            bool exclude = part.Length > 0 && (part[0] == 'x' || part[0] == 'X');
            if (exclude)
            {
                part = part[1..].TrimStart();
            }

            Parity parity = Parity.All;
            int colon = part.IndexOf(':');
            if (colon >= 0)
            {
                ReadOnlySpan<char> modifier = part[(colon + 1)..].Trim();
                if (modifier.Equals("odd", StringComparison.OrdinalIgnoreCase))
                {
                    parity = Parity.Odd;
                }
                else if (modifier.Equals("even", StringComparison.OrdinalIgnoreCase))
                {
                    parity = Parity.Even;
                }
                else
                {
                    return false;
                }

                part = part[..colon].TrimEnd();
            }

            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!TryPage(part, out Page page))
                {
                    return false;
                }

                parts.Add(new Part(page, page, parity, exclude));
            }
            else if (!TryPage(part[..dash].Trim(), out Page from) || !TryPage(part[(dash + 1)..].Trim(), out Page to))
            {
                return false;
            }
            else
            {
                parts.Add(new Part(from, to, parity, exclude));
            }
        }

        if (parts.Count > 0 && parts.TrueForAll(p => p.Exclude))
        {
            // Only exclusions: they take pages out of every page.
            parts.Insert(0, new Part(new Page(1, false), new Page(1, true), Parity.All, false));
        }

        range = new PdfPageRange(text, [.. parts]);
        return true;
    }

    /// <summary>The 0-based indices of the pages chosen in a document of <paramref name="pageCount"/> pages, in order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageCount"/> is negative, or the range names a page the document does not have.
    /// </exception>
    public IReadOnlyList<int> Resolve(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        if (parts.Length == 0)
        {
            return [.. Enumerable.Range(0, pageCount)];
        }

        List<int> chosen = [];
        foreach (Part part in parts)
        {
            int from = part.From.Index(pageCount), to = part.To.Index(pageCount);
            if (from < 0 || from >= pageCount || to < 0 || to >= pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pageCount), pageCount, $"The page range '{Text}' names a page past the document's {pageCount}.");
            }

            int step = to >= from ? 1 : -1;
            List<int> pages = [];
            for (int index = from, n = 0; ; index += step, n++)
            {
                if (part.Parity == Parity.All || (part.Parity == Parity.Odd) == (n % 2 == 0))
                {
                    pages.Add(index);
                }

                if (index == to)
                {
                    break;
                }
            }

            if (part.Exclude)
            {
                HashSet<int> removed = [.. pages];
                chosen.RemoveAll(removed.Contains);
            }
            else
            {
                chosen.AddRange(pages);
            }
        }

        return chosen;
    }

    /// <summary>The range as it was written.</summary>
    public override string ToString() => Text;

    private static bool TryPage(ReadOnlySpan<char> text, out Page page)
    {
        page = default;
        if (text.Equals("z", StringComparison.OrdinalIgnoreCase))
        {
            page = new Page(1, true);
            return true;
        }

        bool fromEnd = text.Length > 1 && (text[0] == 'r' || text[0] == 'R');
        if (fromEnd)
        {
            text = text[1..];
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
        {
            return false;
        }

        page = new Page(number, fromEnd);
        return true;
    }

    private enum Parity
    {
        All,
        Odd,
        Even,
    }

    /// <summary>A page number, counted from the start or (for <c>z</c> and <c>r</c>) from the end.</summary>
    private readonly record struct Page(int Number, bool FromEnd)
    {
        public int Index(int count) => FromEnd ? count - Number : Number - 1;
    }

    private readonly record struct Part(Page From, Page To, Parity Parity, bool Exclude);
}
