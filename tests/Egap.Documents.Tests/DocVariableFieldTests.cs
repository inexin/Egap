namespace Egap.Documents.Tests;

/// <summary>
/// Document variables and DOCVARIABLE fields. tests/files/fields/docvariable.docx holds each field's result by Word's
/// documented rules; once tools/update-field-fixtures.ps1 has run, docvariable.word.docx holds the results Word itself
/// computed (and docvariable.word.doc and .rtf the variables as Word writes them), and the tests compare with those.
/// </summary>
public sealed class DocVariableFieldTests
{
    private static readonly (string Name, string Value)[] FixtureVariables =
        [("Customer", "Acme AS"), ("Amount", "12.5"), ("Owner", "\u00C6rlig \u00C5se"), ("Spaced", "  two  spaces  ")];

    [Fact]
    public void FixtureFieldsGiveWordsResults()
    {
        string word = Fixture("docvariable.word.docx");
        Document document = Document.Load(File.Exists(word) ? word : Fixture("docvariable.docx"));
        List<(FieldStart Start, string Code, string Expected)> expected = [.. document.GetFields().Select(f => (f.Start, f.Code, f.Result))];
        Assert.True(expected.Count >= 12, $"{expected.Count} fields");
        foreach ((FieldStart start, _, _) in expected)
        {
            FieldUpdater.SetResult(start.GetField()!, "");
        }

        document.UpdateFields();

        List<string> failures = [];
        foreach ((FieldStart start, string code, string result) in expected)
        {
            string actual = start.GetField()!.Result;
            if (actual != result)
            {
                failures.Add($"{{{code.Trim()}}}: Word \"{result}\", Egap \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {expected.Count} fields differ:\n" + string.Join('\n', failures));
    }

    [Theory]
    [InlineData("docvariable.docx")]
    [InlineData("docvariable.word.docx")]
    [InlineData("docvariable.word.doc")]
    [InlineData("docvariable.word.rtf")]
    public void FilesCarryTheVariables(string name)
    {
        string path = Fixture(name);
        if (!File.Exists(path))
        {
            // Word's copies come with tools/update-field-fixtures.ps1.
            Assert.Skip(name + " is made by tools/update-field-fixtures.ps1 and has not been yet.");
        }

        Document document = Document.Load(path);
        Assert.Equal(FixtureVariables, document.Variables.Select(v => (v.Key, v.Value)).OrderBy(v => Array.FindIndex(FixtureVariables, f => f.Name == v.Key)));
    }

    [Fact]
    public void NamesIgnoreCaseAndKeepTheirOrder()
    {
        Document document = new();
        document.Variables["Zeta"] = "1";
        document.Variables["alpha"] = "2";
        document.Variables["ZETA"] = "3";
        Assert.Equal(["Zeta", "alpha"], document.Variables.Keys);
        Assert.Equal("3", document.Variables["zeta"]);
        Assert.True(document.Variables.ContainsKey("Alpha"));
        Assert.False(document.Variables.TryGetValue("beta", out _));
        Assert.Throws<KeyNotFoundException>(() => document.Variables["beta"]);
        Assert.True(document.Variables.Remove("ALPHA"));
        Assert.False(document.Variables.Remove("alpha"));
        Assert.Equal(["Zeta"], document.Variables.Keys);
        document.Variables.Clear();
        Assert.Empty(document.Variables);
    }

    [Fact]
    public void ChangesRaiseChanged()
    {
        Document document = new();
        List<string?> changes = [];
        document.Changed += (_, e) => changes.Add(e.Property);
        document.Variables["Customer"] = "Acme";
        document.Variables["Customer"] = "Acme";
        document.Variables.Remove("Customer");
        document.Variables.Clear();
        Assert.Equal(["Variables.Customer", "Variables.Customer"], changes);
    }

    [Theory]
    [InlineData(" DOCVARIABLE Customer ", "Acme AS")]
    [InlineData(" DOCVARIABLE customer \\* Upper ", "ACME AS")]
    [InlineData(" DOCVARIABLE \"Customer\" \\* MERGEFORMAT ", "Acme AS")]
    [InlineData(" DOCVARIABLE Amount \\# \"0.00\" ", "12.50")]
    [InlineData(" DOCVARIABLE Missing ", "Error! No document variable supplied.")]
    [InlineData(" DOCVARIABLE ", "Error! No document variable supplied.")]
    public void FieldsShowTheValue(string code, string result)
    {
        Document document = new();
        document.Variables["Customer"] = "Acme AS";
        document.Variables["Amount"] = "12.5";
        Field field = document.AddSection().Body.AddParagraph().AddField(code, "old");
        Assert.Equal(FieldKind.DocVariable, field.Kind);
        Assert.True(field.Update());
        Assert.Equal(result, field.Start.GetField()!.Result);
    }

    [Fact]
    public void AddFieldWritesDocVariable()
    {
        Document document = new();
        Field field = document.AddSection().Body.AddParagraph().AddField(FieldKind.DocVariable);
        Assert.Equal(" DOCVARIABLE ", field.Code);
    }

    /// <summary>The flow documents guide's DOCVARIABLE sample.</summary>
    [Fact]
    public void GuideSampleFillsAVariable()
    {
        Document letter = new();
        letter.Variables["Customer"] = "Acme AS";
        Paragraph greeting = letter.AddSection().Body.AddParagraph("Dear ");
        Field customer = greeting.AddField("DOCVARIABLE Customer");
        letter.UpdateFields();
        Assert.Equal("Acme AS", customer.Start.GetField()!.Result);
    }

    [Fact]
    public void DocxRoundTripsTheVariables()
    {
        Document document = WithVariables();
        Document read = RoundTrip(document, DocumentFormatKind.Docx);
        Assert.Equal(document.Variables, read.Variables);

        // Changing one rewrites w:docVars; removing them all drops it.
        read.Variables["Customer"] = "Bolt & Co <AS>";
        read.Variables.Remove("Empty");
        Document again = RoundTrip(read, DocumentFormatKind.Docx);
        Assert.Equal(read.Variables, again.Variables);
        again.Variables.Clear();
        Assert.Empty(RoundTrip(again, DocumentFormatKind.Docx).Variables);
    }

    [Fact]
    public void DocxKeepsAnUntouchedSettingsPart()
    {
        byte[] original = File.ReadAllBytes(Fixture("docvariable.docx"));
        Document document = Document.Load(new MemoryStream(original));
        using MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        Assert.Equal(Part(original, "word/settings.xml"), Part(saved.ToArray(), "word/settings.xml"));
    }

    [Fact]
    public void RtfRoundTripsTheVariables()
    {
        Document document = WithVariables();
        Document read = RoundTrip(document, DocumentFormatKind.Rtf);
        Assert.Equal(document.Variables, read.Variables);
    }

    [Fact]
    public void RtfReadsDocvarGroups()
    {
        Document document = RtfFormat.Parse(@"{\rtf1\ansi{\*\docvar {Customer}{Acme \'c6S}}{\*\docvar {Empty}{}}\pard Text\par}");
        Assert.Equal([new KeyValuePair<string, string>("Customer", "Acme \u00C6S"), new("Empty", "")], document.Variables);
    }

    [Fact]
    public void OdtWarnsThatVariablesAreDropped()
    {
        using MemoryStream stream = new();
        IReadOnlyList<ConversionWarning> warnings = OdtFormat.Save(WithVariables(), stream);
        Assert.Contains(warnings, w => w.Message.Contains("document variables", StringComparison.Ordinal));
        using MemoryStream plain = new();
        Assert.DoesNotContain(OdtFormat.Save(new Document(), plain), w => w.Message.Contains("document variables", StringComparison.Ordinal));
    }

    [Fact]
    public void CloneAndMailMergeCopyTheVariables()
    {
        Document template = WithVariables();
        template.Sections[0].Body.AddParagraph().AddField("MERGEFIELD Name", "x");
        Assert.Equal(template.Variables, template.Clone().Variables);
        Document letter = template.MailMerge([MergeData.From(new Dictionary<string, object?> { ["Name"] = "Kari" })]).Single();
        Assert.Equal(template.Variables, letter.Variables);
        template.Variables["Customer"] = "changed";
        Assert.Equal("Acme AS", letter.Variables["Customer"]);
    }

    [Fact]
    public void ComparisonKeepsTheBasesVariables()
    {
        Document original = WithVariables();
        Document revised = WithVariables();
        revised.Variables["Customer"] = "Bolt AS";
        Assert.Equal("Bolt AS", Egap.Documents.Comparing.DocumentCompare.Run(original, revised).Document.Variables["Customer"]);
        Egap.Documents.Comparing.DocumentCompareOptions onOriginal = new() { Base = Egap.Documents.Comparing.CompareBase.Original };
        Assert.Equal("Acme AS", Egap.Documents.Comparing.DocumentCompare.Run(original, revised, onOriginal).Document.Variables["Customer"]);
    }

    private static Document WithVariables()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("Text");
        document.Variables["Customer"] = "Acme AS";
        document.Variables["Owner"] = "\u00C6rlig \u00C5se {braces} \\ back";
        document.Variables["Empty"] = "";
        return document;
    }

    private static Document RoundTrip(Document document, DocumentFormatKind kind)
    {
        using MemoryStream stream = new();
        if (kind == DocumentFormatKind.Rtf)
        {
            RtfFormat.Save(document, stream);
        }
        else
        {
            DocxFormat.Save(document, stream);
        }

        stream.Position = 0;
        return kind == DocumentFormatKind.Rtf ? RtfFormat.Load(stream) : DocxFormat.Load(stream);
    }

    private static string Part(byte[] package, string name)
    {
        using System.IO.Compression.ZipArchive zip = new(new MemoryStream(package));
        using StreamReader reader = new(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "files", "fields", name);
}
