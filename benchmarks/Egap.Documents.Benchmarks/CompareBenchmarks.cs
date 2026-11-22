using BenchmarkDotNet.Attributes;

using Egap.Documents.Comparing;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// Comparing documents (compare design Q9): a 300-page document against itself with 1 % and 10 % of its paragraphs edited,
/// and two unrelated 1,000-page documents, which the edit-distance guard must finish quickly. <c>CloneInputs</c> copies the
/// two inputs, so its allocations are the inputs' model size the compare's allocations are measured against.
/// </summary>
[MemoryDiagnoser]
public class CompareBenchmarks
{
    private Document original = null!;
    private Document revised = null!;

    [Params("300 pages 1%", "300 pages 10%", "1000 pages unrelated")]
    public string Case { get; set; } = "300 pages 1%";

    [GlobalSetup]
    public void Setup()
    {
        Prepare();
        if (Environment.GetEnvironmentVariable("EGAP_COMPARE_PAGES") == "1")
        {
            Console.WriteLine($"// {Case}: {original.Layout().Pages.Count} and {revised.Layout().Pages.Count} pages");
        }
    }

    private void Prepare()
    {
        switch (Case)
        {
            case "300 pages 1%":
                original = CompareSamples.Make(300, seed: 1);
                revised = CompareSamples.Edit(original, 0.01, seed: 2);
                break;
            case "300 pages 10%":
                original = CompareSamples.Make(300, seed: 1);
                revised = CompareSamples.Edit(original, 0.10, seed: 3);
                break;
            default:
                original = CompareSamples.Make(1000, seed: 4);
                revised = CompareSamples.Make(1000, seed: 5);
                break;
        }
    }

    [Benchmark]
    public int Compare() => DocumentCompare.Run(original, revised).Changes.Count;

    [Benchmark]
    public int CloneInputs() => original.Clone().Sections.Count + revised.Clone().Sections.Count;
}

/// <summary>
/// Generated documents for the compare benchmarks: about 560 words a page (Letter, the default style), in paragraphs of 40
/// to 120 words, with a heading every 12 paragraphs and a five-row table every 30. EGAP_COMPARE_PAGES=1 prints the page
/// counts.
/// </summary>
internal static class CompareSamples
{
    private static readonly string[] Vocabulary =
    [
        "the", "of", "and", "to", "in", "a", "is", "that", "for", "it", "as", "with", "be", "on", "not", "this", "by", "are",
        "or", "at", "from", "which", "but", "have", "all", "any", "may", "shall", "will", "party", "agreement", "notice",
        "term", "section", "contract", "payment", "within", "days", "after", "before", "written", "consent", "provided",
        "under", "each", "other", "such", "time", "price", "goods", "services", "delivery", "period", "date", "law",
        "court", "rights", "obligations", "terminate", "breach", "remedy", "liability", "damages", "loss", "insurance",
        "property", "confidential", "information", "disclose", "employee", "supplier", "customer", "invoice", "amount",
    ];

    /// <summary>A document of about <paramref name="pages"/> pages.</summary>
    public static Document Make(int pages, int seed)
    {
        Random random = new(seed);
        Document document = new();
        Body body = document.AddSection().Body;
        int words = 0;
        for (int p = 0; words < pages * 560; p++)
        {
            if (p % 12 == 0)
            {
                body.AddParagraph($"Section {p / 12 + 1} {Words(random, 3)}");
            }

            if (p % 30 == 29)
            {
                Table table = body.AddTable(5, 3);
                foreach (Cell cell in table.Descendants<Cell>())
                {
                    cell.Paragraphs.First().AddRun(Words(random, 1 + random.Next(4)));
                }
            }

            int count = 40 + random.Next(81);
            body.AddParagraph(Words(random, count));
            words += count;
        }

        return document;
    }

    /// <summary>
    /// A copy with <paramref name="share"/> of its paragraphs edited the way people edit: words replaced, inserted and
    /// deleted, and paragraphs added and removed.
    /// </summary>
    public static Document Edit(Document document, double share, int seed)
    {
        Random random = new(seed);
        Document copy = document.Clone();
        List<Paragraph> paragraphs = [.. copy.Sections[0].Body.Paragraphs];
        int edits = (int)Math.Round(paragraphs.Count * share);
        HashSet<int> chosen = [];
        while (chosen.Count < edits)
        {
            chosen.Add(random.Next(paragraphs.Count));
        }

        foreach (int index in chosen)
        {
            Paragraph paragraph = paragraphs[index];
            string[] text = paragraph.GetText().TrimEnd('\r').Split(' ');
            switch (random.Next(10))
            {
                case < 5:
                    text[random.Next(text.Length)] = Words(random, 1);
                    text[random.Next(text.Length)] = Words(random, 1);
                    Replace(paragraph, string.Join(' ', text));
                    break;
                case < 7:
                    int at = random.Next(text.Length);
                    Replace(paragraph, string.Join(' ', [.. text[..at], Words(random, 3), .. text[at..]]));
                    break;
                case < 8:
                    int from = random.Next(text.Length);
                    Replace(paragraph, string.Join(' ', [.. text[..from], .. text[Math.Min(text.Length, from + 4)..]]));
                    break;
                case < 9:
                    paragraph.Remove();
                    break;
                default:
                    Paragraph added = new(copy);
                    added.AddRun(Words(random, 40 + random.Next(40)));
                    NodeCollection<Block> blocks = copy.Sections[0].Body.Blocks;
                    blocks.Insert(blocks.IndexOf(paragraph) + 1, added);
                    break;
            }
        }

        return copy;
    }

    private static void Replace(Paragraph paragraph, string text)
    {
        paragraph.Inlines.Clear();
        paragraph.AddRun(text);
    }

    private static string Words(Random random, int count)
    {
        return string.Join(' ', Enumerable.Range(0, count).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]));
    }
}
