using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Synthetic;

/// <summary>
/// The synthetic pages the design's testing plan names: one and two columns, a pull quote between columns, hanging indents,
/// bulleted and numbered lists at three levels, ruled, partly ruled and borderless tables, merged cells, a table across a
/// page break, footnotes, repeated headers and footers, and hyphenated line ends. Each is a fresh document with its truth.
/// </summary>
internal static class SyntheticScenes
{
    private static readonly string[] Vocabulary =
    [
        "the", "a", "of", "and", "to", "in", "is", "for", "on", "with", "as", "by", "at", "from", "that", "this", "each", "every", "which",
        "page", "layout", "column", "reader", "document", "table", "figure", "block", "line", "word", "order", "text", "report", "section",
        "measured", "analysis", "structure", "paragraph", "heading", "footnote", "reconstruction", "interpretation", "characteristics",
        "approximately", "independently", "transformation", "representative", "configuration", "classification", "considerable",
        "shows", "keeps", "joins", "follows", "describes", "contains", "places", "breaks", "repeats", "moves", "returns", "finds",
        "small", "large", "clear", "careful", "simple", "regular", "narrow", "wide", "short", "long", "early", "later", "final",
    ];

    /// <summary>Every scene by name.</summary>
    public static IReadOnlyDictionary<string, Func<SyntheticDocument>> All { get; } = new Dictionary<string, Func<SyntheticDocument>>
    {
        ["one-column"] = () => OneColumn(),
        ["two-columns"] = () => TwoColumns(),
        ["pull-quote"] = () => PullQuote(),
        ["lists"] = () => Lists(),
        ["tables"] = () => Tables(),
        ["table-across-pages"] = () => TableAcrossPages(),
        ["footnotes"] = () => Footnotes(),
        ["headers-and-footers"] = () => HeadersAndFooters(),
        ["hyphenation"] = () => Hyphenation(),
        ["quote-code-caption"] = () => QuoteCodeCaption(),
    };

    /// <summary>Every scene by name, with a structure tree as Word writes one.</summary>
    public static IReadOnlyDictionary<string, Func<SyntheticDocument>> Tagged { get; } = new Dictionary<string, Func<SyntheticDocument>>
    {
        ["one-column"] = () => OneColumn(tagged: true),
        ["two-columns"] = () => TwoColumns(tagged: true),
        ["pull-quote"] = () => PullQuote(tagged: true),
        ["lists"] = () => Lists(tagged: true),
        ["tables"] = () => Tables(tagged: true),
        ["table-across-pages"] = () => TableAcrossPages(tagged: true),
        ["footnotes"] = () => Footnotes(tagged: true),
        ["headers-and-footers"] = () => HeadersAndFooters(tagged: true),
        ["hyphenation"] = () => Hyphenation(tagged: true),
        ["quote-code-caption"] = () => QuoteCodeCaption(tagged: true),
    };

    /// <summary>
    /// <paramref name="count"/> words of sentence-like text, the same for the same <paramref name="seed"/>: sentences of 8 to
    /// 16 words, each starting with a capital and ending with a full stop.
    /// </summary>
    public static string Words(int seed, int count)
    {
        Random random = new(seed);
        List<string> words = [];
        int sentence = 0, length = random.Next(8, 17);
        for (int i = 0; i < count; i++)
        {
            string word = Vocabulary[random.Next(Vocabulary.Length)];
            if (sentence == 0)
            {
                word = char.ToUpperInvariant(word[0]) + word[1..];
            }

            sentence++;
            if (sentence == length || i == count - 1)
            {
                word += ".";
                sentence = 0;
                length = random.Next(8, 17);
            }

            words.Add(word);
        }

        return string.Join(' ', words);
    }

    public static SyntheticDocument OneColumn(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Title("A Report on Page Layout");
        doc.Heading("Introduction");
        for (int i = 0; i < 3; i++)
        {
            doc.Paragraph(Words(i, 90));
        }

        doc.Heading("Method", 2);
        doc.Paragraph(Words(10, 60));
        return doc;
    }

    public static SyntheticDocument TwoColumns(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Title("Two Columns");
        doc.Columns(2);
        doc.Heading("Background");
        for (int i = 0; i < 12; i++)
        {
            doc.Paragraph(Words(100 + i, 110));
        }

        return doc;
    }

    public static SyntheticDocument PullQuote(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Heading("Columns Around a Quote");
        doc.Columns(2);
        doc.PullQuote("Layout is what the reader sees before reading a word.", 330);
        for (int i = 0; i < 9; i++)
        {
            doc.Paragraph(Words(200 + i, 80));
        }

        return doc;
    }

    public static SyntheticDocument Lists(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Heading("Lists");
        doc.Paragraph(Words(300, 40));
        doc.ListItem(1, "\u2022", Words(301, 30));
        doc.ListItem(2, "\u2013", Words(302, 25));
        doc.ListItem(3, "o", Words(303, 12));
        doc.ListItem(3, "o", Words(304, 14));
        doc.ListItem(2, "\u2013", Words(305, 10));
        doc.ListItem(1, "\u2022", Words(306, 20));
        doc.Paragraph(Words(307, 30));
        doc.ListItem(1, "1.", Words(308, 22));
        doc.ListItem(2, "a)", Words(309, 18));
        doc.ListItem(3, "i.", Words(310, 9));
        doc.ListItem(3, "ii.", Words(311, 11));
        doc.ListItem(2, "b)", Words(312, 14));
        doc.ListItem(1, "2.", Words(313, 26));
        doc.ListItem(1, "3.", Words(314, 8));
        return doc;
    }

    public static SyntheticDocument Tables(bool tagged = false, bool spanAttributes = true)
    {
        SyntheticDocument doc = new(tagged: tagged) { SpanAttributes = spanAttributes };
        doc.Heading("Ruled, with merged cells");
        doc.Table(
        [
            ["Region", "2024", "2025", "Change"],
            ["North", "120", "135", "+15"],
            ["South", "98", "n/a", ""],
            ["East", "143", "151", "+8"],
            ["Total", "361", "not yet reported", ""],
        ], [120, 70, 70, 70], TruthRules.All, merges: [(2, 2, 1, 2), (4, 2, 1, 2)]);
        doc.Caption("Table 1. Sales by region, ruled on every edge.");
        doc.Paragraph(Words(400, 40));
        doc.Heading("Partly ruled");
        doc.Table(
        [
            ["Item", "Size", "Weight", "Price"],
            ["Small box", "20 cm", "0.4 kg", "12.00"],
            ["Large box", "45 cm", "1.1 kg", "19.50"],
            ["Crate", "80 cm", "6.0 kg", "48.00"],
        ], [130, 70, 70, 70], TruthRules.Partial);
        doc.Caption("Table 2. Rules only above, under the header and below.");
        doc.Heading("Borderless");
        doc.Table(
        [
            ["Name", "Role", "Since"],
            ["Ada", "Editor", "2019"],
            ["Ben", "Designer", "2021"],
            ["Cleo", "Reviewer", "2023"],
            ["Dan", "Writer", "2024"],
        ], [130, 110, 60], TruthRules.None);
        doc.Caption("Table 3. No rules at all.");
        return doc;
    }

    public static SyntheticDocument TableAcrossPages(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Heading("A Long Table");
        doc.Paragraph(Words(500, 160));
        string[][] rows = [["#", "Sample", "Value", "Unit"], .. Enumerable.Range(1, 60).Select(i => (string[])[$"{i}", $"Sample {i}", $"{i * 3.5:0.0}", "mg"])];
        doc.Table(rows, [40, 160, 80, 60], TruthRules.All);
        doc.Paragraph(Words(501, 40));
        return doc;
    }

    public static SyntheticDocument Footnotes(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Heading("Notes at the Foot");
        doc.Paragraph(Words(600, 70), footnote: ("1", Words(601, 18)));
        doc.Paragraph(Words(602, 60));
        doc.Paragraph(Words(603, 50), footnote: ("2", Words(604, 34)));
        return doc;
    }

    public static SyntheticDocument HeadersAndFooters(bool tagged = false)
    {
        SyntheticDocument doc = new(header: "Annual Report 2025", pageNumbers: true, tagged: tagged);
        doc.Title("Running Headers");
        for (int i = 0; i < 16; i++)
        {
            doc.Paragraph(Words(700 + i, 120));
        }

        return doc;
    }

    public static SyntheticDocument Hyphenation(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Heading("Hyphenated Line Ends");
        doc.Columns(3);
        for (int i = 0; i < 6; i++)
        {
            doc.Paragraph(Words(800 + i, 70), hyphenate: true);
        }

        return doc;
    }

    public static SyntheticDocument QuoteCodeCaption(bool tagged = false)
    {
        SyntheticDocument doc = new(tagged: tagged);
        doc.Heading("Quotes and Code");
        doc.Paragraph(Words(900, 50));
        doc.Quote(Words(901, 45));
        doc.Paragraph(Words(902, 30));
        doc.Code("foreach (var block in page.Blocks)", "{", "    Console.WriteLine(block.Text);", "}");
        doc.Caption("Listing 1. Printing the blocks of a page.");
        doc.Paragraph(Words(903, 40));
        return doc;
    }
}
