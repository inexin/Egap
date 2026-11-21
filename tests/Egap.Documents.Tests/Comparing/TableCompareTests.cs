using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

/// <summary>Tables compared row by row and cell by cell (6.19 Build Plan task 4).</summary>
public sealed class TableCompareTests
{
    /// <summary>A document with a paragraph, a table of the given rows (cells split on '|') and a paragraph after it.</summary>
    internal static Document WithTable(params string[] rows)
    {
        Document document = DocumentCompareTests.Make("Before.");
        Body body = document.Sections[0].Body;
        Table table = body.AddTable(0, 0);
        foreach (string row in rows)
        {
            Row added = table.AddRow();
            foreach (string text in row.Split('|'))
            {
                added.AddCell().Paragraphs.First().AddRun(text);
            }
        }

        body.AddParagraph("After.");
        return document;
    }

    private static Table TableOf(DocumentComparison comparison)
    {
        return comparison.Document.Sections[0].Body.Tables.Single();
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AnEditedCellIsComparedWordByWord(CompareBase baseDocument)
    {
        Document a = WithTable("Name|Value", "apples|three boxes");
        Document b = WithTable("Name|Value", "apples|four boxes");

        DocumentComparison comparison = RoundTrip.Check(a, b, new DocumentCompareOptions { Base = baseDocument });

        Table table = TableOf(comparison);
        Assert.All(table.Rows, row => Assert.Null(row.Revision));
        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal((CompareChangeKind.Replaced, "three", "four"), (change.Kind, change.OldText, change.NewText));
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AddedAndDroppedRowsAreTrackedAsRows(CompareBase baseDocument)
    {
        Document a = WithTable("Name|Value", "apples|3", "pears|5");
        Document b = WithTable("Name|Value", "plums|7", "lemons|9", "pears|5");

        DocumentComparison comparison = RoundTrip.Check(a, b, new DocumentCompareOptions { Base = baseDocument });

        Table table = TableOf(comparison);
        Assert.Equal(5, table.Rows.Count);
        Assert.Equal(
            [null, RevisionKind.Deletion, RevisionKind.Insertion, RevisionKind.Insertion, null],
            table.Rows.Select(r => r.Revision?.Kind));
        Assert.Equal([CompareChangeKind.Deleted, CompareChangeKind.Inserted, CompareChangeKind.Inserted], comparison.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void NestedTablesAreComparedInTurn()
    {
        Document a = WithTable("outer|cell");
        Table innerA = a.Sections[0].Body.Tables.Single().Rows[0].Cells[1].AddTable(1, 2);
        innerA.Rows[0].Cells[0].Paragraphs.First().AddRun("inner old text");
        Document b = WithTable("outer|cell");
        Table innerB = b.Sections[0].Body.Tables.Single().Rows[0].Cells[1].AddTable(1, 2);
        innerB.Rows[0].Cells[0].Paragraphs.First().AddRun("inner new text");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        DocumentChange change = Assert.Single(comparison.Changes);
        Assert.Equal(("old", "new"), (change.OldText, change.NewText));
        Assert.Single(comparison.Document.Sections[0].Body.Tables.Single().Descendants<Table>());
    }

    [Theory]
    [InlineData(CompareBase.Revised)]
    [InlineData(CompareBase.Original)]
    public void AColumnAddedReplacesTheRowsThatChanged(CompareBase baseDocument)
    {
        Document a = WithTable("Title spanning", "apples|3", "pears|5");
        Document b = WithTable("Title spanning", "apples|3|green", "pears|5|yellow");

        DocumentComparison comparison = RoundTrip.Check(a, b, new DocumentCompareOptions { Base = baseDocument });

        Table table = TableOf(comparison);
        Assert.Null(table.Rows[0].Revision);
        Assert.Equal(5, table.Rows.Count);
        Assert.Equal(2, table.Rows.Count(r => r.Revision?.Kind == RevisionKind.Deletion));
        Assert.Equal(2, table.Rows.Count(r => r.Revision?.Kind == RevisionKind.Insertion));
    }

    [Fact]
    public void TablesWithNoRowInCommonAreReplacedWhole()
    {
        Document a = WithTable("one|two", "three|four");
        Document b = WithTable("five|six|seven");

        DocumentComparison comparison = RoundTrip.Check(a, b);

        Assert.Equal(2, comparison.Document.Sections[0].Body.Tables.Count());
        Assert.Equal([CompareChangeKind.Deleted, CompareChangeKind.Inserted], comparison.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void EachRowChangeAcceptsAndRejectsOnItsOwn()
    {
        Document a = WithTable("a|1", "b|2");
        Document b = WithTable("a|1", "b|2", "c|3");

        DocumentComparison comparison = DocumentCompare.Run(a, b);
        DocumentChange added = Assert.Single(comparison.Changes);
        added.Reject();

        Assert.Equal(RoundTrip.Blocks(a), RoundTrip.Blocks(comparison.Document));
    }

    [Fact]
    public void RowChangesSaveToDocxAndReadBack()
    {
        // RTF and ODT keep no row changes yet (Build Plan task 10).
        Document a = WithTable("Name|Value", "apples|3", "pears|5");
        Document b = WithTable("Name|Value", "apples|4", "plums|7", "pears|5");
        DocumentComparison comparison = DocumentCompare.Run(a, b);

        using MemoryStream stream = new();
        DocxFormat.Save(comparison.Document, stream);
        stream.Position = 0;
        Document back = DocxFormat.Load(stream);

        RoundTrip.Check(a, b, back);
        Assert.Equal(comparison.Document.Revisions.Count, back.Revisions.Count);
    }
}
