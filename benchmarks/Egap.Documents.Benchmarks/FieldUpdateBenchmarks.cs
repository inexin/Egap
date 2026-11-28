using BenchmarkDotNet.Attributes;

namespace Egap.Documents.Benchmarks;

/// <summary>
/// <see cref="Document.UpdateFields"/> on documents of everyday fields (dates, properties, REF, SEQ, IF and PAGE, no
/// formulas) and, in <see cref="Formulas"/>, on table totals made of formula fields.
/// </summary>
[MemoryDiagnoser]
public class FieldUpdateBenchmarks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private Document fields = new();
    private Document formulas = new();

    [Params(20, 200)]
    public int Sections { get; set; } = 20;

    [GlobalSetup]
    public void Setup()
    {
        fields = new Document();
        fields.Properties.Author = "Benchmark";
        Body body = fields.AddSection().Body;
        for (int i = 0; i < Sections; i++)
        {
            Paragraph paragraph = body.AddParagraph($"Section {i}, ");
            paragraph.Inlines.Add(new BookmarkStart(fields, $"Name{i}"));
            paragraph.AddRun($"name {i}");
            paragraph.Inlines.Add(new BookmarkEnd(fields, $"Name{i}"));
            paragraph.AddField("DATE \\@ \"d MMMM yyyy\"", "x");
            paragraph.AddField("AUTHOR \\* Upper", "x");
            paragraph.AddField($"REF Name{i}", "x");
            paragraph.AddField("SEQ Figure \\* ARABIC", "x");
            paragraph.AddField("IF \"a\" = \"a\" \"same\" \"different\"", "x");
            paragraph.AddField("IF 3 > 2 \"more\" \"less\"", "x");
            paragraph.AddField("PAGE \\# \"0\"", "x");
        }

        formulas = new Document();
        Body sheet = formulas.AddSection().Body;
        for (int i = 0; i < Sections; i++)
        {
            Table table = sheet.AddTable(5, 3);
            for (int r = 0; r < 4; r++)
            {
                table.Rows[r].Cells[0].AddParagraph($"Item {r}");
                table.Rows[r].Cells[1].AddParagraph($"{r + i}");
                table.Rows[r].Cells[2].AddParagraph($"$1,{r}00.50");
            }

            table.Rows[4].Cells[0].AddParagraph("Total");
            table.Rows[4].Cells[1].AddParagraph().AddField("= SUM(ABOVE)", "x");
            table.Rows[4].Cells[2].AddParagraph().AddField("= SUM(C1:C4) * 1.25 \\# \"$#,##0.00\"", "x");
            sheet.AddParagraph("After the table");
        }
    }

    [Benchmark]
    public int Everyday() => fields.UpdateFields(Now);

    [Benchmark]
    public int Formulas() => formulas.UpdateFields(Now);
}
