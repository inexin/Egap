using System.Collections;
using System.Xml.Linq;

using Egap.Spreadsheets.Pivots;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>The pivot tables of a worksheet, those read from its file and those made in code.</summary>
public sealed class PivotTableCollection : IReadOnlyList<PivotTable>
{
    private readonly Worksheet sheet;

    internal PivotTableCollection(Worksheet sheet) => this.sheet = sheet;

    private IReadOnlyList<PivotTable> Tables => sheet.Workbook.Pivots.Tables(sheet);

    /// <summary>How many pivot tables the sheet has.</summary>
    public int Count => Tables.Count;

    /// <summary>The pivot table at a position.</summary>
    public PivotTable this[int index] => Tables[index];

    /// <summary>The pivot table of a name, case ignored.</summary>
    /// <exception cref="KeyNotFoundException">The sheet has no pivot table of that name.</exception>
    public PivotTable this[string name] =>
        Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"The sheet has no pivot table named {name}.");

    /// <summary>
    /// Makes a pivot table of a source range, an Excel table or a defined name, and lays it out from the source's values as
    /// Excel does when it makes one. The table's top left cell is <paramref name="location"/>, or, with filters, the filters
    /// start there and the table starts a row under the last of them. The cells it takes are written over.
    /// </summary>
    /// <param name="location">Where the pivot table goes.</param>
    /// <param name="source">
    /// The source with its header row: a range such as <c>Data!A1:G61</c> or <c>A1:G61</c> on this sheet, the name of an
    /// Excel table, or a defined name of one range. Each column is a field, named by its header cell.
    /// </param>
    /// <param name="options">The fields on rows, columns and filters, the values and how the table looks.</param>
    /// <param name="name">The pivot table's name, or null for the next free <c>PivotTableN</c>.</param>
    /// <exception cref="ArgumentException">
    /// The source is not a range, table or name of the workbook, a header cell is empty, the options name a field the source
    /// lacks or put one on two axes, or the name is taken on the sheet.
    /// </exception>
    /// <exception cref="InvalidOperationException">The table would overlap another pivot table or go off the sheet.</exception>
    public PivotTable Add(CellAddress location, string source, PivotTableOptions options, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(options);
        Workbook book = sheet.Workbook;
        string text = source.Trim();
        foreach (Worksheet other in book.Worksheets)
        {
            foreach (SheetTable table in other.Tables)
            {
                if (table.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    return Add(location, table, options, name);
                }
            }
        }

        if (Reference(text) is ({ } refSheet, RangeAddress range))
        {
            return Create(location, new XElement(XlsxNames.Main + "worksheetSource", new XAttribute("ref", range.ToString()), new XAttribute("sheet", XlsxNames.Encode(refSheet.Name))), refSheet, range, options, name);
        }

        DefinedName? defined = book.Names
            .Where(n => n.Name.Equals(text, StringComparison.OrdinalIgnoreCase) && (n.Sheet is null || n.Sheet == sheet))
            .OrderBy(n => n.Sheet is null ? 1 : 0)
            .FirstOrDefault();
        if (defined is not null && PivotCache.NameRange(book, defined) is ({ } nameSheet, RangeAddress nameRange))
        {
            return Create(location, new XElement(XlsxNames.Main + "worksheetSource", new XAttribute("name", XlsxNames.Encode(defined.Name))), nameSheet, nameRange, options, name);
        }

        throw new ArgumentException($"The source {source} is not a range, an Excel table or a defined name of one range in the workbook.", nameof(source));
    }

    /// <summary>Makes a pivot table of a source given as text; see <see cref="Add(CellAddress, string, PivotTableOptions, string?)"/>.</summary>
    /// <exception cref="FormatException">The location is not an A1 cell reference.</exception>
    /// <exception cref="ArgumentException">The source, options or name are not ones Excel takes.</exception>
    /// <exception cref="InvalidOperationException">The table would overlap another pivot table or go off the sheet.</exception>
    public PivotTable Add(string location, string source, PivotTableOptions options, string? name = null) => Add(CellAddress.Parse(location), source, options, name);

    /// <summary>
    /// Makes a pivot table of an Excel table, which it reads by name, so it follows the table as it grows; see
    /// <see cref="Add(CellAddress, string, PivotTableOptions, string?)"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The table has no header row, or the options or name are not ones Excel takes.</exception>
    /// <exception cref="InvalidOperationException">The table would overlap another pivot table or go off the sheet.</exception>
    public PivotTable Add(CellAddress location, SheetTable source, PivotTableOptions options, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.ShowHeaderRow)
        {
            throw new ArgumentException("A pivot table's source table needs its header row.", nameof(source));
        }

        RangeAddress range = source.Range;
        if (source.ShowTotalsRow && range.RowCount > 1)
        {
            range = new RangeAddress(range.First.Row, range.First.Column, range.Last.Row - 1, range.Last.Column);
        }

        return Create(location, new XElement(XlsxNames.Main + "worksheetSource", new XAttribute("name", XlsxNames.Encode(source.Name))), source.Worksheet, range, options, name);
    }

    /// <summary>
    /// Makes a pivot table of a range of cells, header row included; see <see cref="Add(CellAddress, string, PivotTableOptions, string?)"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The range or options, or the name, are not ones Excel takes.</exception>
    /// <exception cref="InvalidOperationException">The table would overlap another pivot table or go off the sheet.</exception>
    public PivotTable Add(CellAddress location, CellRange source, PivotTableOptions options, string? name = null)
    {
        Worksheet from = source.Worksheet ?? throw new ArgumentException("The range has no sheet.", nameof(source));
        if (from.Workbook != sheet.Workbook)
        {
            throw new ArgumentException("A pivot table's source is in its own workbook.", nameof(source));
        }

        return Create(location, new XElement(XlsxNames.Main + "worksheetSource", new XAttribute("ref", source.Address.ToString()), new XAttribute("sheet", XlsxNames.Encode(from.Name))), from, source.Address, options, name);
    }

    private PivotTable Create(CellAddress location, XElement worksheetSource, Worksheet sourceSheet, RangeAddress range, PivotTableOptions options, string? name)
    {
        if (sheet.Tables.Find(location) is { } table)
        {
            throw new InvalidOperationException($"A pivot table cannot go in the Excel table {table.Name}.");
        }

        return sheet.Workbook.Pivots.Create(sheet, location, worksheetSource, sourceSheet, range, options, name);
    }

    /// <summary>A reference such as <c>Data!A1:G61</c>, <c>'My data'!$A$1:$G$61</c> or <c>A1:G61</c> on this sheet.</summary>
    private (Worksheet Sheet, RangeAddress Range)? Reference(string text)
    {
        Worksheet target = sheet;
        string range = text;
        int bang = text.LastIndexOf('!');
        if (bang >= 0)
        {
            string sheetName = text[..bang];
            if (sheetName.Length >= 2 && sheetName[0] == '\'' && sheetName[^1] == '\'')
            {
                sheetName = sheetName[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            if (!sheet.Workbook.Worksheets.TryGet(sheetName, out Worksheet? found))
            {
                return null;
            }

            target = found;
            range = text[(bang + 1)..];
        }

        return RangeAddress.TryParse(range.Replace("$", "", StringComparison.Ordinal), out RangeAddress address) ? (target, address) : null;
    }

    /// <inheritdoc/>
    public IEnumerator<PivotTable> GetEnumerator() => Tables.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
