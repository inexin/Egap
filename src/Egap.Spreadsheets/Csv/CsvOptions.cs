using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets;

/// <summary>
/// How <see cref="Workbook.LoadCsv(string, CsvOptions?)"/> reads and <see cref="Worksheet.SaveCsv(string, CsvOptions?)"/>
/// writes comma- and tab-separated text (RFC 4180): the separator, encoding, culture and type detection.
/// </summary>
/// <example>
/// <code>
/// Workbook book = Workbook.LoadCsv("prices.csv", new CsvOptions { Separator = ';', Culture = CultureInfo.GetCultureInfo("de-DE") });
/// book.Worksheets[0].SaveCsv("prices.tsv");
/// </code>
/// </example>
public sealed class CsvOptions
{
    /// <summary>
    /// The field separator, or null to choose one: a <c>sep=</c> first line if the file has one, a tab for a <c>.tsv</c> or
    /// <c>.tab</c> path, otherwise whichever of comma, semicolon and tab the first line holds most of. Writing with null uses
    /// a tab for a <c>.tsv</c> or <c>.tab</c> path, otherwise the culture's list separator, as Excel does: a comma in English,
    /// a semicolon where the comma is the decimal separator.
    /// </summary>
    public char? Separator { get; set; }

    /// <summary>
    /// The text encoding, or null for UTF-8. Reading honours a byte order mark over this setting; writing UTF-8 adds one, as
    /// Excel needs to tell UTF-8 from the system code page.
    /// </summary>
    public Encoding? Encoding { get; set; }

    /// <summary>
    /// The culture numbers and dates are read and written in: <c>1.234,5</c> and <c>15.03.2024</c> in German. The invariant
    /// culture, the default, reads and writes them as Excel in English does.
    /// </summary>
    /// <exception cref="ArgumentNullException">The culture set is null.</exception>
    public CultureInfo Culture
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = CultureInfo.InvariantCulture;

    /// <summary>
    /// Whether reading turns fields into numbers, percentages, TRUE and FALSE, dates, times and errors, with a number format
    /// that shows them as written; when false, every field is text. A number with a leading zero, such as <c>007</c>, stays
    /// text, and so does a field that starts with <c>=</c>: reading never makes formulas. Default true.
    /// </summary>
    public bool DetectTypes { get; set; } = true;

    /// <summary>
    /// Whether writing shows each value as its cell shows it (<see cref="Cell.Text"/>), as Excel saves CSV; when false,
    /// numbers are written in full, in <see cref="Culture"/>, and dates as their serial numbers. Default true.
    /// </summary>
    public bool WriteFormattedText { get; set; } = true;

    /// <summary>Whether writing starts with a <c>sep=</c> line naming the separator, which Excel reads. Default false.</summary>
    public bool WriteSeparatorHint { get; set; }

    /// <summary>The line break written after each row. Default <c>\r\n</c>, as RFC 4180 has it.</summary>
    /// <exception cref="ArgumentException">The line break set is not <c>\r\n</c>, <c>\n</c> or <c>\r</c>.</exception>
    public string NewLine
    {
        get;
        set => field = value is "\r\n" or "\n" or "\r" ? value : throw new ArgumentException("A line break is \\r\\n, \\n or \\r.", nameof(value));
    } = "\r\n";

    /// <summary>The name of the sheet reading makes. Default <c>Sheet1</c>.</summary>
    public string SheetName { get; set; } = "Sheet1";

    /// <summary>The separator to use for a file at <paramref name="path"/>, or null to detect it on reading.</summary>
    internal char? SeparatorFor(string? path) =>
        Separator ?? (path is not null && Path.GetExtension(path).ToUpperInvariant() is ".TSV" or ".TAB" ? '\t' : null);
}
