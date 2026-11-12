using System.Xml.Linq;

using Egap.Spreadsheets.Pivots;

namespace Egap.Spreadsheets;

/// <summary>
/// A slicer: buttons for the items of a pivot table field, which filter the pivot tables it is connected to. Selecting items
/// hides the others in those tables and refreshes them, as clicking the slicer in Excel does. A slicer sits on a sheet's
/// drawing layer: see <see cref="SheetDrawingCollection.AddSlicer(PivotTable, string, RangeAddress)"/> and
/// <see cref="SheetDrawing.Slicer"/>.
/// </summary>
/// <remarks>
/// Egap reads the slicers of pivot tables from .xlsx files, keeps them when it saves, draws them when it prints and makes new
/// ones. Slicers of Excel tables and of cubes are kept and drawn as the file left them, but cannot be changed.
/// </remarks>
public sealed class Slicer
{
    private string caption;
    private string? styleName;
    private int columnCount;
    private bool showHeader;

    internal Slicer(SlicerCache cache, Worksheet sheet, string name, string caption, XElement? source)
    {
        Cache = cache;
        Worksheet = sheet;
        Name = name;
        this.caption = caption;
        Source = source;
        styleName = (string?)source?.Attribute("style");
        columnCount = Math.Max(1, Xlsx.XlsxNames.Int((string?)source?.Attribute("columnCount"), 1));
        showHeader = Xlsx.XlsxNames.Bool((string?)source?.Attribute("showCaption"), true);
        RowHeight = source?.Attribute("rowHeight") is { } height && long.TryParse(height.Value, out long emus) && emus > 0 ? emus : DefaultRowHeight;
    }

    /// <summary>The height of a row of buttons Excel gives a new slicer in the Calibri 11 normal font, in EMUs.</summary>
    internal const long DefaultRowHeight = 257175;

    /// <summary>The slicer's name, unique in the workbook, such as <c>Region</c>.</summary>
    public string Name { get; }

    /// <summary>The sheet the slicer is on.</summary>
    public Worksheet Worksheet { get; }

    /// <summary>The name of the field whose items the slicer shows, as the pivot cache names it.</summary>
    public string Field => Cache.SourceName;

    /// <summary>The text of the slicer's header; the field's name to start with.</summary>
    /// <exception cref="InvalidOperationException">The slicer is not a pivot table's and cannot be changed.</exception>
    public string Caption
    {
        get => caption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Change();
            caption = value;
        }
    }

    /// <summary>
    /// The slicer's style, one of Excel's built-in styles <c>SlicerStyleLight1</c> to <c>SlicerStyleLight6</c>,
    /// <c>SlicerStyleOther1</c> and <c>SlicerStyleOther2</c> and <c>SlicerStyleDark1</c> to <c>SlicerStyleDark6</c>, or a style of
    /// the workbook; null for the workbook's default slicer style.
    /// </summary>
    /// <exception cref="InvalidOperationException">The slicer is not a pivot table's and cannot be changed.</exception>
    public string? StyleName
    {
        get => styleName;
        set
        {
            Change();
            styleName = value;
        }
    }

    /// <summary>How many columns of buttons the slicer shows, from 1 to 20,000.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is outside 1 to 20,000.</exception>
    /// <exception cref="InvalidOperationException">The slicer is not a pivot table's and cannot be changed.</exception>
    public int ColumnCount
    {
        get => columnCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 20000);
            Change();
            columnCount = value;
        }
    }

    /// <summary>Whether the slicer shows its header with its <see cref="Caption"/>.</summary>
    /// <exception cref="InvalidOperationException">The slicer is not a pivot table's and cannot be changed.</exception>
    public bool ShowHeader
    {
        get => showHeader;
        set
        {
            Change();
            showHeader = value;
        }
    }

    /// <summary>The pivot tables the slicer filters.</summary>
    public IReadOnlyList<PivotTable> PivotTables => Cache.Tables;

    /// <summary>The items the slicer shows a button for, in its order, as the pivot tables label them.</summary>
    public IReadOnlyList<string> Items => [.. Cache.Items().Select(i => i.Label)];

    /// <summary>The items selected, which the pivot tables show.</summary>
    public IReadOnlyList<string> SelectedItems => [.. Cache.Items().Where(i => i.Selected).Select(i => i.Label)];

    internal SlicerCache Cache { get; }

    /// <summary>The slicer's element as the file had it, or null for a slicer made in memory.</summary>
    internal XElement? Source { get; }

    /// <summary>The drawing object that shows the slicer.</summary>
    internal SheetDrawing? Drawing { get; set; }

    /// <summary>The height of a row of buttons, in EMUs.</summary>
    internal long RowHeight { get; }

    /// <summary>Whether a property changed since the slicer was read, so a save writes its part again.</summary>
    internal bool Changed { get; private set; }

    /// <summary>
    /// Selects the given items, by their labels as <see cref="Items"/> has them, and clears the others; the pivot tables then
    /// show only the selected items and are refreshed.
    /// </summary>
    /// <exception cref="ArgumentException">No item, or an item the slicer does not have, is given.</exception>
    /// <exception cref="InvalidOperationException">
    /// The slicer is not a pivot table's, or a pivot table cannot be refreshed (see <see cref="PivotTable.CanRefresh"/>).
    /// Nothing changes.
    /// </exception>
    public void Select(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Cache.Select([.. items]);
    }

    /// <summary>Selects every item, so the pivot tables show them all, and refreshes the pivot tables.</summary>
    /// <exception cref="InvalidOperationException">
    /// The slicer is not a pivot table's, or a pivot table cannot be refreshed (see <see cref="PivotTable.CanRefresh"/>).
    /// </exception>
    public void SelectAll() => Cache.Select(null);

    /// <summary>
    /// Connects the slicer to another pivot table of the same cache, which then shows only the selected items; the table is
    /// refreshed.
    /// </summary>
    /// <exception cref="ArgumentException">The pivot table does not share the slicer's pivot cache.</exception>
    /// <exception cref="InvalidOperationException">
    /// The slicer is not a pivot table's, or the pivot table cannot be refreshed (see <see cref="PivotTable.CanRefresh"/>).
    /// </exception>
    public void Connect(PivotTable pivotTable)
    {
        ArgumentNullException.ThrowIfNull(pivotTable);
        Cache.Connect(pivotTable);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Worksheet.Name}, {Field})";

    private void Change()
    {
        if (!Cache.CanChange)
        {
            throw new InvalidOperationException("Egap changes only the slicers of pivot tables.");
        }

        Changed = true;
    }
}
