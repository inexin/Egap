namespace Egap.Spreadsheets;

/// <summary>What <see cref="Worksheet.Find(string, FindOptions?)"/> searches in each cell, as Excel's "Look in".</summary>
public enum FindLookIn
{
    /// <summary>
    /// What the formula bar shows: a formula with its leading <c>=</c>, in Excel's stored English form, and a value as typed,
    /// numbers in full with <see cref="Workbook.Culture"/>'s decimal separator and dates in its short date form. Excel's default.
    /// </summary>
    Formulas,

    /// <summary>The text a cell shows, formatted as <see cref="Cell.Text"/> gives it.</summary>
    Values,

    /// <summary>The text of a cell's note.</summary>
    Notes,
}

/// <summary>The order <see cref="Worksheet.Find(string, FindOptions?)"/> visits cells in.</summary>
public enum FindOrder
{
    /// <summary>Row by row, left to right. Excel's default.</summary>
    ByRows,

    /// <summary>Column by column, top to bottom.</summary>
    ByColumns,
}

/// <summary>
/// How <see cref="Worksheet.Find(string, FindOptions?)"/> and <see cref="Worksheet.Replace(string, string, FindOptions?)"/>
/// match, as the options of Excel's Find and Replace dialog.
/// </summary>
public sealed class FindOptions
{
    /// <summary>Whether case must match. Default false.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Whether the whole of a cell's text must match, instead of any part of it. Default false.</summary>
    public bool MatchEntireCell { get; init; }

    /// <summary>What to search. Default <see cref="FindLookIn.Formulas"/>; replacing works only there.</summary>
    public FindLookIn LookIn { get; init; }

    /// <summary>The order cells are visited in. Default <see cref="FindOrder.ByRows"/>.</summary>
    public FindOrder Order { get; init; }
}
