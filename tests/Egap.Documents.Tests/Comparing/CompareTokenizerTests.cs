using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

public sealed class CompareTokenizerTests
{
    [Fact]
    public void WordsSpacesAndPunctuationAreTokens()
    {
        Paragraph paragraph = Paragraph("Hello, ", "wor", "ld 3.5 times.");
        ParagraphTokens tokens = ParagraphTokens.Create(paragraph, new TokenTable(false, false, false));

        Assert.Equal(["Hello", ",", " ", "world", " ", "3.5", " ", "times", "."], Texts(tokens));
        Assert.Equal([true, false, false, true, false, true, false, true, false], tokens.Words);
    }

    [Fact]
    public void ThaiWithoutSpacesSplitsIntoDictionaryWords()
    {
        // "Thai language is a language": a change of one word is a change of one token, not of the whole run.
        ParagraphTokens tokens = ParagraphTokens.Create(Paragraph("ภาษาไทยเป็นภาษา"), new TokenTable(false, false, false));

        Assert.Equal(["ภาษา", "ไทย", "เป็น", "ภาษา"], Texts(tokens));
        Assert.Equal([true, true, true, true], tokens.Words);
        Assert.Equal(tokens.Ids[0], tokens.Ids[3]);
    }

    [Fact]
    public void AtomsStandAloneAndFieldsAreOneToken()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("a");
        paragraph.AddTab();
        paragraph.AddRun("b");
        paragraph.AddField("PAGE", "3");
        paragraph.Inlines.Add(new BookmarkStart(document, "mark"));
        paragraph.AddBreak();
        paragraph.AddRun("c");
        ParagraphTokens tokens = ParagraphTokens.Create(paragraph, new TokenTable(false, false, false));

        Assert.Equal(6, tokens.Count);
        Assert.Equal("a\uFFFCb\uFFFC\uFFFCc", tokens.Text);
        Assert.Equal(PieceKind.Marker, tokens.Pieces.Single(p => p.First is BookmarkStart).Kind);
        Assert.Equal(5, tokens.Pieces.Single(p => p.First is FieldStart).Count);
    }

    [Fact]
    public void EqualTextGetsEqualIdsAcrossParagraphs()
    {
        TokenTable table = new(false, false, false);
        ParagraphTokens a = ParagraphTokens.Create(Paragraph("one two"), table);
        ParagraphTokens b = ParagraphTokens.Create(Paragraph("two one"), table);

        Assert.Equal(a.Ids[0], b.Ids[2]);
        Assert.Equal(a.Ids[2], b.Ids[0]);
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void CaseAndWhitespaceCanBeFolded()
    {
        TokenTable table = new(false, ignoreCase: true, ignoreWhitespace: true);
        ParagraphTokens a = ParagraphTokens.Create(Paragraph("One  two"), table);
        ParagraphTokens b = ParagraphTokens.Create(Paragraph("one two"), table);

        Assert.Equal(a.Ids, b.Ids);
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void CharacterGranularityUsesGraphemes()
    {
        ParagraphTokens tokens = ParagraphTokens.Create(Paragraph("ne\u0301e"), new TokenTable(true, false, false));

        Assert.Equal(["n", "e\u0301", "e"], Texts(tokens));
    }

    private static Paragraph Paragraph(params string[] runs)
    {
        Paragraph paragraph = new Document().AddSection().Body.AddParagraph();
        foreach (string run in runs)
        {
            paragraph.AddRun(run);
        }

        return paragraph;
    }

    private static string[] Texts(ParagraphTokens tokens)
    {
        return [.. Enumerable.Range(0, tokens.Count).Select(t => tokens.Text[tokens.Starts[t]..tokens.Starts[t + 1]])];
    }
}
