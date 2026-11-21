using Egap.Documents.Comparing;
using Egap.Documents.Docx;
using Egap.Documents.Odf;
using Egap.Documents.Rtf;

namespace Egap.Documents.Tests.Comparing;

/// <summary>The round trip on real documents from the test corpus, each against seeded random edits of itself.</summary>
public sealed class CompareCorpusTests
{
    public static TheoryData<string> Corpus =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "rtf"), "*.rtf").Select(f => "rtf/" + Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "odt")).Select(f => "odt/" + Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-xml"), "*.xml").Select(f => "docx-xml/" + Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-package"), "*.docx").Select(f => "docx-package/" + Path.GetFileName(f)),
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void MutatedCorpusRoundTrips(string name)
    {
        Document original = Load(name);
        for (int seed = 0; seed < 12; seed++)
        {
            Document revised = new EditMutator(seed).Mutate(original, 1 + (seed * 4));
            DocumentComparison forward = RoundTrip.Check(original, revised);
            RoundTrip.Check(revised, original);
            RoundTrip.Check(original, revised, new DocumentCompareOptions { Base = CompareBase.Original });
            RoundTrip.Check(original, revised, new DocumentCompareOptions { Granularity = CompareGranularity.Character });
            Assert.Equal(RoundTrip.Blocks(original).SequenceEqual(RoundTrip.Blocks(revised)), !forward.HasChanges);
        }
    }

    [Fact]
    public void MutatedGeneratedDocumentRoundTrips()
    {
        Document original = new();
        Body body = original.AddSection().Body;
        Random random = new(42);
        for (int i = 0; i < 60; i++)
        {
            body.AddParagraph(string.Join(" ", Enumerable.Range(0, 5 + random.Next(30)).Select(_ => "w" + random.Next(200))));
            if (i % 15 == 7)
            {
                Table table = body.AddTable(3, 2);
                foreach (Cell cell in table.Descendants<Cell>())
                {
                    cell.Paragraphs.First().AddRun("cell " + random.Next(50));
                }
            }
        }

        for (int seed = 0; seed < 40; seed++)
        {
            Document revised = new EditMutator(1000 + seed).Mutate(original, 1 + seed);
            RoundTrip.Check(original, revised);
            RoundTrip.Check(revised, original, new DocumentCompareOptions { Base = CompareBase.Original });
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ReformattedCorpusRoundTripsByLook(string name)
    {
        Document original = Load(name);
        for (int seed = 0; seed < 6; seed++)
        {
            Document revised = new EditMutator(500 + seed, formatting: true).Mutate(original, 1 + (seed * 3));
            RoundTrip.CheckFormatting(original, revised);
            RoundTrip.CheckFormatting(original, revised, new DocumentCompareOptions { Base = CompareBase.Original });
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void StoryEditedCorpusRoundTrips(string name)
    {
        Document original = Load(name);
        for (int seed = 0; seed < 6; seed++)
        {
            Document revised = new EditMutator(700 + seed, stories: true).Mutate(original, 1 + (seed * 3));
            RoundTrip.CheckStories(original, revised);
            RoundTrip.CheckStories(original, revised, new DocumentCompareOptions { Base = CompareBase.Original });
        }
    }

    internal static Document Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "files", name);
        return name.Split('/')[0] switch
        {
            "rtf" => RtfFormat.Load(path),
            "odt" => OdtFormat.Load(path),
            "docx-xml" => DocxFormat.Load(DocxSeedPackage.Wrap(File.ReadAllBytes(path))),
            _ => DocxFormat.Load(path),
        };
    }
}
