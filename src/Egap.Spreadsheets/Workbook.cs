using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets;

/// <summary>
/// A spreadsheet workbook: its worksheets and the text and styles they share. A workbook is not safe to change from more
/// than one thread at a time.
/// </summary>
/// <example>
/// <code>
/// Workbook book = Workbook.Create();
/// Worksheet sheet = book.Worksheets[0];
/// sheet["A1"].Value = "Total";
/// sheet["B1"].Value = 42;
/// </code>
/// </example>
public sealed class Workbook
{
    private Workbook()
    {
        Worksheets = new WorksheetCollection(this);
    }

    /// <summary>The worksheets, in tab order.</summary>
    public WorksheetCollection Worksheets { get; }

    /// <summary>The text values of every sheet, each kept once.</summary>
    internal SharedStringTable SharedStrings { get; } = new();

    /// <summary>The cell styles of every sheet, each kept once.</summary>
    internal StyleTable Styles { get; } = new();

    /// <summary>A new workbook with one empty sheet, <c>Sheet1</c>, as Excel starts one.</summary>
    public static Workbook Create()
    {
        Workbook book = new();
        book.Worksheets.Add();
        return book;
    }

    /// <summary>A new workbook with no sheets; add one before saving, as a workbook file needs at least one.</summary>
    public static Workbook CreateEmpty() => new();
}
