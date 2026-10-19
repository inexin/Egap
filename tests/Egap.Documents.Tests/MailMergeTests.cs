using System.Globalization;

namespace Egap.Documents.Tests;

/// <summary>Merge data sources and filling merge fields.</summary>
public sealed class MailMergeTests
{
    [Fact]
    public void FieldsAreFilledWithTheirSwitchesAndKeepTheirFormatting()
    {
        Document document = Template(out DocumentBuilder builder);
        builder.Write("Dear ");
        builder.Font.Bold = true;
        builder.InsertField("MERGEFIELD Name \\* Upper", "«Name»");
        builder.Font.Bold = false;
        builder.Write(", you owe ");
        builder.InsertField("MERGEFIELD Amount \\# \"#,##0.00\"", "«Amount»");
        builder.Write(" by ");
        builder.InsertField("MERGEFIELD Due \\@ \"d MMMM yyyy\"", "«Due»");
        builder.InsertField("MERGEFIELD Note \\b \" (\" \\f \")\"", "«Note»");
        builder.InsertField("MERGEFIELD Missing", "«Missing»");
        builder.Write(".");

        IReadOnlyList<ConversionWarning> warnings = document.FillMergeFields(MergeData.From(new Dictionary<string, object?>
        {
            ["name"] = "Kari Nordmann",
            ["Amount"] = 1234.5m,
            ["Due"] = new DateOnly(2026, 11, 1),
            ["Note"] = "",
        }));

        Paragraph paragraph = document.Sections[0].Body.Paragraphs.First();
        Assert.Equal("Dear KARI NORDMANN, you owe 1,234.50 by 1 November 2026.", paragraph.GetText().TrimEnd('\r'));
        Assert.Empty(document.GetFields());
        Assert.True(paragraph.Inlines.OfType<Run>().Single(r => r.Text == "KARI NORDMANN").Format.Bold);
        ConversionWarning warning = Assert.Single(warnings);
        Assert.Equal(ConversionWarningKind.MergeFieldsRemoved, warning.Kind);
        Assert.Contains("Missing", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RowsObjectsAndDottedNamesAreSources()
    {
        IMergeData[] rows = [.. MergeData.From(["First", "City"], [["Ola", "Oslo"], ["Kari", "Bergen"]])];
        Assert.True(rows[1].TryGetValue("city", out object? city));
        Assert.Equal("Bergen", city);
        Assert.False(rows[0].TryGetValue("Country", out _));

        IMergeData order = MergeData.FromObject(new Order("A-118", 3, new Dictionary<string, object?> { ["Name"] = "ACME" }));
        Document document = Template(out DocumentBuilder builder);
        builder.InsertField("MERGEFIELD Number");
        builder.Write(" for ");
        builder.InsertField("MERGEFIELD Customer.Name");
        builder.Write(": ");
        builder.InsertField("MERGEFIELD Quantity");
        builder.Write(" ");
        builder.InsertField("IF { MERGEFIELD Quantity } > 2 \"bulk\" \"single\"");

        // The IF field's nested MERGEFIELD is written out as a field, as Word stores it.
        Field condition = document.GetFields().Single(f => f.Kind == FieldKind.If);
        Run code = condition.CodeNodes.OfType<Run>().First();
        string text = code.Text;
        int open = text.IndexOf('{', StringComparison.Ordinal);
        int close = text.IndexOf('}', StringComparison.Ordinal);
        code.Text = text[..open];
        DocumentBuilder nested = new(document);
        nested.MoveTo(code.NextSibling!);
        nested.InsertField(text[(open + 1)..close].Trim(), "0");
        nested.Write(text[(close + 1)..]);

        Assert.Empty(document.FillMergeFields(order));
        Assert.Equal("A-118 for ACME: 3 bulk", document.Sections[0].Body.Paragraphs.First().GetText().TrimEnd('\r'));
    }

    [Fact]
    public void CultureAndEmptyParagraphs()
    {
        Document document = Template(out DocumentBuilder builder);
        builder.InsertField("MERGEFIELD Total");
        builder.InsertParagraph();
        builder.InsertField("MERGEFIELD Line2");
        builder.InsertParagraph();
        builder.Write("End");

        document.FillMergeFields(MergeData.From(new Dictionary<string, object?> { ["Total"] = 1234.5, ["Line2"] = null }),
            new MailMergeOptions { Culture = CultureInfo.GetCultureInfo("nb-NO"), RemoveEmptyParagraphs = true });

        Assert.Equal(["1234,5", "End"], document.Sections[0].Body.Paragraphs.Select(p => p.GetText().TrimEnd('\r')));
    }

    private static Document Template(out DocumentBuilder builder)
    {
        Document document = new();
        builder = new DocumentBuilder(document);
        return document;
    }

    private sealed record Order(string Number, int Quantity, IReadOnlyDictionary<string, object?> Customer);
}
