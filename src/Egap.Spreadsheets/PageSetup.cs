using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>Which way a sheet prints on the paper.</summary>
public enum PrintOrientation
{
    /// <summary>The printer's default.</summary>
    Default,

    /// <summary>Upright.</summary>
    Portrait,

    /// <summary>Sideways.</summary>
    Landscape,
}

/// <summary>The order in which a sheet too big for one page prints its pages.</summary>
public enum PrintPageOrder
{
    /// <summary>Down the rows first, then over to the next columns, as Excel does by default.</summary>
    DownThenOver,

    /// <summary>Across the columns first, then down.</summary>
    OverThenDown,
}

/// <summary>
/// How a sheet prints: paper, orientation, scaling, margins, centring, gridlines, headings, page order and the header and
/// footer. The print area and the rows and columns repeated on each page are defined names, <c>_xlnm.Print_Area</c> and
/// <c>_xlnm.Print_Titles</c>, in <see cref="Workbook.Names"/>.
/// </summary>
public sealed class PageSetup
{
    internal PageSetup()
    {
    }

    /// <summary>Which way the sheet prints.</summary>
    public PrintOrientation Orientation { get; set; }

    /// <summary>The paper size as SpreadsheetML numbers them (1 Letter, 9 A4, 5 Legal and so on), or null for the default.</summary>
    public int? PaperSize { get; set; }

    /// <summary>The print scale in per cent, from 10 to 400, or null for 100; not used when <see cref="FitToPage"/> is set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The scale is outside 10 to 400.</exception>
    public int? Scale
    {
        get;
        set => field = value is null or (>= 10 and <= 400) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A print scale is from 10 to 400 per cent.");
    }

    /// <summary>Whether the sheet is scaled to fit <see cref="FitToWidth"/> pages across and <see cref="FitToHeight"/> down.</summary>
    public bool FitToPage { get; set; }

    /// <summary>How many pages wide the sheet fits when <see cref="FitToPage"/> is set; 0 for as many as it needs. 1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public int FitToWidth
    {
        get;
        set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    } = 1;

    /// <summary>How many pages tall the sheet fits when <see cref="FitToPage"/> is set; 0 for as many as it needs. 1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public int FitToHeight
    {
        get;
        set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    } = 1;

    /// <summary>The number of the first page, or null to start at 1.</summary>
    public int? FirstPageNumber { get; set; }

    /// <summary>The order pages print in.</summary>
    public PrintPageOrder PageOrder { get; set; }

    /// <summary>The left margin in inches; Excel's default is 0.7.</summary>
    public double LeftMargin { get; set; } = 0.7;

    /// <summary>The right margin in inches; Excel's default is 0.7.</summary>
    public double RightMargin { get; set; } = 0.7;

    /// <summary>The top margin in inches; Excel's default is 0.75.</summary>
    public double TopMargin { get; set; } = 0.75;

    /// <summary>The bottom margin in inches; Excel's default is 0.75.</summary>
    public double BottomMargin { get; set; } = 0.75;

    /// <summary>The header's distance from the top edge in inches; Excel's default is 0.3.</summary>
    public double HeaderMargin { get; set; } = 0.3;

    /// <summary>The footer's distance from the bottom edge in inches; Excel's default is 0.3.</summary>
    public double FooterMargin { get; set; } = 0.3;

    /// <summary>Whether the sheet is centred across the page.</summary>
    public bool CenterHorizontally { get; set; }

    /// <summary>Whether the sheet is centred down the page.</summary>
    public bool CenterVertically { get; set; }

    /// <summary>Whether gridlines print.</summary>
    public bool PrintGridLines { get; set; }

    /// <summary>Whether row and column headings print.</summary>
    public bool PrintHeadings { get; set; }

    /// <summary>
    /// The header on each page in Excel's header code, such as <c>&amp;L&amp;D&amp;CReport&amp;RPage &amp;P of &amp;N</c>, or
    /// null for none.
    /// </summary>
    public string? Header { get; set; }

    /// <summary>The footer on each page in Excel's header code, or null for none.</summary>
    public string? Footer { get; set; }

    internal XElement? SourcePageSetup { get; set; }

    internal XElement? SourceMargins { get; set; }

    internal XElement? SourcePrintOptions { get; set; }

    internal XElement? SourceHeaderFooter { get; set; }
}
