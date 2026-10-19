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

    [Fact]
    public void TableStartAndTableEndRepeatRowsAndNestedRegions()
    {
        Document document = Template(out DocumentBuilder builder);
        builder.InsertField("MERGEFIELD Number");
        builder.Writeln();
        Table table = document.Sections[0].Body.AddTable(3, 2);
        Write(table.Rows[0].Cells[0], "Item");
        Write(table.Rows[0].Cells[1], "Qty");
        Cell name = table.Rows[1].Cells[0];
        Fields(name, "TableStart:Lines", "Name", " (", "Number", ")");
        Fields(table.Rows[1].Cells[1], "Qty", "TableEnd:Lines");
        Write(table.Rows[2].Cells[0], "End");

        // A region of paragraphs, with a region nested in it.
        Paragraph start = document.Sections[0].Body.AddParagraph();
        Fields(start, "TableStart:Notes", "Text");
        Paragraph inner = document.Sections[0].Body.AddParagraph();
        Fields(inner, "TableStart:Tags", "Tag", " ", "TableEnd:Tags");
        Paragraph end = document.Sections[0].Body.AddParagraph();
        Fields(end, "TableEnd:Notes");

        IMergeData order = MergeData.From(new Dictionary<string, object?>
        {
            ["Number"] = "A-118",
            ["Lines"] = MergeData.FromObjects([new Line("Bolts", 4), new Line("Nuts", 8)]),
            ["Notes"] = new List<Dictionary<string, object?>>
            {
                new() { ["Text"] = "Urgent", ["Tags"] = new List<Dictionary<string, object?>> { new() { ["Tag"] = "red" }, new() { ["Tag"] = "hot" } } },
            },
        });

        IReadOnlyList<ConversionWarning> warnings = document.FillMergeFields(order);

        Assert.Empty(warnings);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("Bolts (A-118)", table.Rows[1].Cells[0].GetText().Trim());
        Assert.Equal("8", table.Rows[2].Cells[1].GetText().Trim());
        Assert.Equal("End", table.Rows[3].Cells[0].GetText().Trim());
        string[] paragraphs = [.. document.Sections[0].Body.Paragraphs.Skip(2).Select(p => p.GetText().TrimEnd('\r'))];
        Assert.Equal(["Urgent", "red ", "hot ", ""], paragraphs);
        Assert.Empty(document.GetFields());
    }

    [Fact]
    public void ARowOfRegionFieldsRepeatsAndAMissingRegionIsRemoved()
    {
        Document document = Template(out _);
        Table table = document.Sections[0].Body.AddTable(2, 2);
        Fields(table.Rows[0].Cells[0], "Lines.Name");
        Fields(table.Rows[0].Cells[1], "Lines.Qty");
        Fields(table.Rows[1].Cells[0], "TableStart:Missing", "Name", "TableEnd:Missing");

        IReadOnlyList<ConversionWarning> warnings = document.FillMergeFields(MergeData.FromObject(new Invoice([new Line("Bolts", 4), new Line("Nuts", 8), new Line("Washers", 2)])));

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["Bolts", "Nuts", "Washers"], table.Rows.Select(r => r.Cells[0].GetText().Trim()));
        Assert.Contains(warnings, w => w.Message.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void MailMergeMakesADocumentPerRecordAndMailMergeAllOneOfAll()
    {
        Document template = Template(out DocumentBuilder builder);
        builder.Write("Dear ");
        builder.InsertField("MERGEFIELD Name");
        IMergeData[] people = [.. MergeData.From(["Name"], [["Ola"], ["Kari"], ["Per"]])];
        List<ConversionWarning> reported = [];

        Document[] letters = [.. template.MailMerge(people)];
        Document all = template.MailMergeAll(people, new MailMergeOptions { Separator = SectionStart.Continuous, OnWarning = reported.Add });

        Assert.Equal(["Dear Ola", "Dear Kari", "Dear Per"], letters.Select(d => d.Sections[0].Body.GetText().Trim()));
        Assert.Single(template.GetFields());
        Assert.Equal(3, all.Sections.Count);
        Assert.Equal(SectionStart.Continuous, all.Sections[1].PageSetup.SectionStart);
        Assert.Equal("Dear Per", all.Sections[2].Body.GetText().Trim());
        Assert.Empty(reported);

        template.MailMergeAll([MergeData.From(new Dictionary<string, object?>())], new MailMergeOptions { OnWarning = reported.Add });
        Assert.Single(reported);
        Assert.Single(template.MailMergeAll([]).Sections);
    }

    private static void Write(Cell cell, string text) => cell.Paragraphs.First().AddRun(text);

    private static void Fields(BlockContainer cell, params string[] parts) => Fields(cell.Paragraphs.First(), parts);

    /// <summary>Writes merge fields for names, and text for parts that start with a space or a bracket.</summary>
    private static void Fields(Paragraph paragraph, params string[] parts)
    {
        DocumentBuilder builder = new(paragraph.Document);
        builder.MoveTo(paragraph);
        foreach (string part in parts)
        {
            if (part.StartsWith(' ') || part.StartsWith(')'))
            {
                builder.Write(part);
            }
            else
            {
                builder.InsertField("MERGEFIELD " + part, "«" + part + "»");
            }
        }
    }

    private static Document Template(out DocumentBuilder builder)
    {
        Document document = new();
        builder = new DocumentBuilder(document);
        return document;
    }

    private sealed record Line(string Name, int Qty);

    private sealed record Invoice(IEnumerable<IMergeData> Lines)
    {
        public Invoice(Line[] lines)
            : this(MergeData.FromObjects(lines))
        {
        }
    }

    private sealed record Order(string Number, int Quantity, IReadOnlyDictionary<string, object?> Customer);
}
