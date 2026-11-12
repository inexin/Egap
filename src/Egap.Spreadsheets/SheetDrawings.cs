using System.Collections;
using System.Xml.Linq;

using Egap.Documents.Charts;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>What a drawing object on a sheet is.</summary>
public enum SheetDrawingKind
{
    /// <summary>A picture.</summary>
    Picture,

    /// <summary>A shape or text box.</summary>
    Shape,

    /// <summary>A chart; see <see cref="SheetDrawing.Chart"/>.</summary>
    Chart,

    /// <summary>A group of objects.</summary>
    Group,

    /// <summary>A connector, an embedded object or anything else, kept as it was.</summary>
    Other,

    /// <summary>A slicer that filters pivot tables; see <see cref="SheetDrawing.Slicer"/>.</summary>
    Slicer,
}

/// <summary>How a drawing object is fixed to the sheet.</summary>
public enum DrawingAnchorKind
{
    /// <summary>To two cells: it moves and sizes with the cells under it.</summary>
    TwoCell,

    /// <summary>To one cell, with a fixed size: it moves with the cell.</summary>
    OneCell,

    /// <summary>To a place on the sheet, with a fixed size.</summary>
    Absolute,
}

/// <summary>A place on a sheet: a cell's top-left corner and an offset from it in EMUs (914,400 an inch, 9,525 a pixel at 96 dpi).</summary>
/// <param name="Cell">The cell.</param>
/// <param name="OffsetX">The offset to the right, in EMUs.</param>
/// <param name="OffsetY">The offset down, in EMUs.</param>
public readonly record struct DrawingPosition(CellAddress Cell, long OffsetX = 0, long OffsetY = 0);

/// <summary>
/// A picture, shape, chart or other object on a sheet's drawing layer. Its place and size and its name and alternative
/// text can be changed, a picture's image replaced and a chart edited; everything else about it is kept as the file had it.
/// </summary>
public sealed class SheetDrawing
{
    private byte[]? imageData;
    private Chart? chart;
    private bool chartRead;

    internal SheetDrawing(SheetDrawingKind kind)
    {
        Kind = kind;
    }

    /// <summary>What the object is.</summary>
    public SheetDrawingKind Kind { get; }

    /// <summary>How the object is fixed to the sheet.</summary>
    public DrawingAnchorKind AnchorKind { get; set; }

    /// <summary>The top-left corner, for objects fixed to cells.</summary>
    public DrawingPosition From { get; set; }

    /// <summary>The bottom-right corner, for objects fixed to two cells.</summary>
    public DrawingPosition To { get; set; }

    /// <summary>The left edge in EMUs from the sheet's corner, for absolutely placed objects.</summary>
    public long X { get; set; }

    /// <summary>The top edge in EMUs from the sheet's corner, for absolutely placed objects.</summary>
    public long Y { get; set; }

    /// <summary>The width in EMUs, for objects fixed to one cell or placed absolutely.</summary>
    public long Width { get; set; }

    /// <summary>The height in EMUs, for objects fixed to one cell or placed absolutely.</summary>
    public long Height { get; set; }

    /// <summary>The object's name, as Excel's selection pane shows it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The alternative text, or null.</summary>
    public string? Description { get; set; }

    /// <summary>A shape's text, or null; read only.</summary>
    public string? Text { get; internal set; }

    /// <summary>A picture's image file: PNG, JPEG, GIF, BMP, TIFF, EMF or WMF; null for other objects.</summary>
    /// <exception cref="ArgumentException">The object is not a picture, or the data is not an image Excel shows.</exception>
    public byte[]? ImageData
    {
        get => imageData;
        set
        {
            if (Kind != SheetDrawingKind.Picture || value is null)
            {
                throw new ArgumentException("Only a picture has an image, and it cannot be removed; remove the picture instead.", nameof(value));
            }

            ImageContentType = SheetDrawingCollection.ImageType(value);
            imageData = value;
            ImageChanged = true;
        }
    }

    /// <summary>The image's content type, such as <c>image/png</c>; null for other objects.</summary>
    public string? ImageContentType { get; private set; }

    /// <summary>The chart, for a chart object; null for other objects, and for a chart whose part cannot be read, which stays as it was.</summary>
    public Chart? Chart
    {
        get
        {
            if (!chartRead)
            {
                chartRead = true;
                chart = ChartPart is { } part ? Chart.Read(part) : null;
            }

            return chart;
        }
    }

    /// <summary>The chart as it was read, if anyone has asked for it, so a chart nobody touched is not even parsed at save time.</summary>
    internal Chart? LoadedChart => chartRead ? chart : null;

    /// <summary>The slicer the object shows, or null for an object that is not a slicer.</summary>
    public Slicer? Slicer => Kind == SheetDrawingKind.Slicer && Sheet is { } sheet ? sheet.Workbook.Slicers.Find(sheet, this) : null;

    /// <summary>The name of the slicer the object shows, as its graphic frame names it.</summary>
    internal string? SlicerName { get; set; }

    /// <summary>The part a file's chart is kept in.</summary>
    internal OpcPart? ChartPart { get; set; }

    /// <summary>The sheet the object is on.</summary>
    internal Worksheet? Sheet { get; set; }

    /// <summary>
    /// Reads the cells a chart's references name into the values the chart keeps. Saving does this for a chart that changed;
    /// call it when cells changed and you read <see cref="Chart"/>'s values yourself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The object is not a chart, or is no longer on a sheet.</exception>
    public void RefreshChart()
    {
        if (Chart is not { } own || Sheet is null)
        {
            throw new InvalidOperationException("Only a chart on a sheet reads cells.");
        }

        ChartCells.Fill(own, Sheet.Workbook);
    }

    /// <summary>The file's anchor element, for everything the model does not hold; null for a picture made in code.</summary>
    internal XElement? Source { get; set; }

    /// <summary>The relationship id the file's picture used for its image.</summary>
    internal string? ImageRelationshipId { get; set; }

    internal bool ImageChanged { get; set; }

    /// <summary>What the model held when the file was read, to tell whether the part can be written back as it was.</summary>
    internal string? ReadState { get; set; }

    /// <summary>Makes this a new chart.</summary>
    internal void SetChart(Chart made)
    {
        chart = made;
        chartRead = true;
    }

    /// <summary>Sets the image a file's picture showed, as it was.</summary>
    internal void SetImageRead(byte[] data, string contentType)
    {
        imageData = data;
        ImageContentType = contentType;
    }
}

/// <summary>A sheet's drawing objects, back to front.</summary>
public sealed class SheetDrawingCollection : IReadOnlyList<SheetDrawing>
{
    private readonly List<SheetDrawing> drawings = [];
    private readonly Worksheet sheet;

    internal SheetDrawingCollection(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>How many objects there are.</summary>
    public int Count => drawings.Count;

    /// <summary>The object at a position, back to front.</summary>
    public SheetDrawing this[int index] => drawings[index];

    /// <summary>Whether an object was added or removed since the file was read.</summary>
    internal bool Changed { get; set; }

    /// <summary>
    /// Adds a picture that fills a range of cells and moves and sizes with them. The image is a PNG, JPEG, GIF, BMP, TIFF,
    /// EMF or WMF file.
    /// </summary>
    /// <exception cref="ArgumentException">The data is not an image Excel shows.</exception>
    public SheetDrawing AddPicture(byte[] image, RangeAddress cells)
    {
        SheetDrawing picture = NewPicture(image);
        picture.AnchorKind = DrawingAnchorKind.TwoCell;
        picture.From = new DrawingPosition(cells.First);
        picture.To = new DrawingPosition(new CellAddress(cells.Last.Row + 1, cells.Last.Column + 1));
        return picture;
    }

    /// <summary>Adds a picture of a size in pixels (at 96 dpi) with its top-left corner at a cell; it moves with the cell.</summary>
    /// <exception cref="ArgumentException">The data is not an image Excel shows.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public SheetDrawing AddPicture(byte[] image, CellAddress cell, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        SheetDrawing picture = NewPicture(image);
        picture.AnchorKind = DrawingAnchorKind.OneCell;
        picture.From = new DrawingPosition(cell);
        picture.Width = width * 9525L;
        picture.Height = height * 9525L;
        return picture;
    }

    /// <summary>
    /// Adds a chart with no series that fills a range of cells and moves and sizes with them. Add series through
    /// <see cref="SheetDrawing.Chart"/>'s plots.
    /// </summary>
    /// <param name="type">What the chart draws.</param>
    /// <param name="cells">The cells it covers.</param>
    /// <param name="threeD">Draws it in 3D: column, bar, line, area, pie and surface charts.</param>
    /// <exception cref="ArgumentException">The type has no such form.</exception>
    public SheetDrawing AddChart(ChartType type, RangeAddress cells, bool threeD = false)
    {
        SheetDrawing drawing = new(SheetDrawingKind.Chart)
        {
            AnchorKind = DrawingAnchorKind.TwoCell,
            From = new DrawingPosition(cells.First),
            To = new DrawingPosition(new CellAddress(cells.Last.Row + 1, cells.Last.Column + 1)),
            Name = "Chart " + (drawings.Count + 1),
            Sheet = sheet,
        };
        drawing.SetChart(Chart.Create(type, threeD));
        drawings.Add(drawing);
        Changed = true;
        return drawing;
    }

    /// <summary>
    /// Adds a chart of the cells of <paramref name="data"/>, with a series to each column. A first row of text gives the names;
    /// the first columns, while they hold text, give the categories, and a scatter or bubble chart takes its X values from the
    /// first column whatever it holds. A bubble chart reads its columns in threes: X, Y and size.
    /// </summary>
    /// <param name="type">What the chart draws.</param>
    /// <param name="cells">The cells it covers.</param>
    /// <param name="data">The cells it reads, on this sheet.</param>
    /// <param name="threeD">Draws it in 3D: column, bar, line, area, pie and surface charts.</param>
    /// <exception cref="ArgumentException">The type has no such form.</exception>
    public SheetDrawing AddChart(ChartType type, RangeAddress cells, RangeAddress data, bool threeD = false)
    {
        SheetDrawing drawing = AddChart(type, cells, threeD);
        Chart chart = drawing.Chart!;
        int firstRow = data.First.Row;
        bool names = Enumerable.Range(data.First.Column, data.ColumnCount).Any(c => sheet[firstRow, c].ValueKind == CellValueKind.Text);
        if (names)
        {
            firstRow++;
        }

        if (firstRow > data.Last.Row)
        {
            firstRow = data.First.Row;
            names = false;
        }

        int labels = 0;
        if (type is ChartType.Scatter or ChartType.Bubble)
        {
            labels = data.ColumnCount > 1 ? 1 : 0;
        }
        else
        {
            while (labels < data.ColumnCount - 1 && sheet[firstRow, data.First.Column + labels].ValueKind is CellValueKind.Text or CellValueKind.Empty)
            {
                labels++;
            }
        }

        string? categories = labels == 0 ? null : ChartCells.Reference(sheet, new RangeAddress(firstRow, data.First.Column, data.Last.Row, data.First.Column + labels - 1));
        string Column(int column) => ChartCells.Reference(sheet, new RangeAddress(firstRow, column, data.Last.Row, column));
        ChartPlot plot = chart.Plots[0];
        bool single = type is ChartType.Histogram or ChartType.Pareto or ChartType.Treemap or ChartType.Sunburst or ChartType.Waterfall or ChartType.Funnel or ChartType.Pie or ChartType.PieOfPie or ChartType.BarOfPie;
        int step = type == ChartType.Bubble ? 2 : 1;
        int added = 0;
        for (int column = data.First.Column + labels; column + (step - 1) <= data.Last.Column; column += step)
        {
            string? name = names ? ChartCells.Reference(sheet, new RangeAddress(data.First.Row, column, data.First.Row, column)) : null;
            ChartSeries series = added == 0 && chart.IsExtended ? plot.Series[0] : plot.Series.Add(Column(column), categories, name, step == 2 ? Column(column + 1) : null);
            if (added == 0 && chart.IsExtended)
            {
                if (name is not null)
                {
                    series.NameReference = name;
                }

                if (categories is not null)
                {
                    series.Categories.Reference = categories;
                }

                series.Values.Reference = Column(column);
            }

            added++;
            if (single)
            {
                break;
            }
        }

        ChartCells.Fill(chart, sheet.Workbook);
        return drawing;
    }

    /// <summary>
    /// Adds a slicer of a pivot table's field that fills a range of cells and moves with them: buttons for the field's items,
    /// all selected, which filter the pivot table. The slicer is named after the field, and the pivot table is refreshed so the
    /// field lists its items; see <see cref="SheetDrawing.Slicer"/> to select items and connect other pivot tables.
    /// </summary>
    /// <param name="pivotTable">A pivot table of this workbook.</param>
    /// <param name="field">The name of a field of the pivot table's source, on its axes or not.</param>
    /// <param name="cells">The cells the slicer covers.</param>
    /// <exception cref="ArgumentException">The pivot table is another workbook's, or it has no such field.</exception>
    /// <exception cref="InvalidOperationException">The pivot table cannot be refreshed (see <see cref="PivotTable.CanRefresh"/>).</exception>
    public SheetDrawing AddSlicer(PivotTable pivotTable, string field, RangeAddress cells) =>
        sheet.Workbook.Slicers.Create(sheet, pivotTable, field, cells);

    /// <summary>Adds a drawing object Egap made, such as a slicer, fixed to two cells.</summary>
    internal SheetDrawing AddMade(SheetDrawingKind kind, RangeAddress cells, string name)
    {
        SheetDrawing drawing = new(kind)
        {
            AnchorKind = DrawingAnchorKind.TwoCell,
            From = new DrawingPosition(cells.First),
            To = new DrawingPosition(new CellAddress(cells.Last.Row + 1, cells.Last.Column + 1)),
            Name = name,
            Sheet = sheet,
        };
        drawings.Add(drawing);
        Changed = true;
        return drawing;
    }

    /// <summary>Removes an object.</summary>
    /// <returns>Whether it was on the sheet.</returns>
    public bool Remove(SheetDrawing drawing)
    {
        bool removed = drawings.Remove(drawing);
        Changed |= removed;
        return removed;
    }

    /// <inheritdoc/>
    public IEnumerator<SheetDrawing> GetEnumerator() => drawings.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(SheetDrawing drawing)
    {
        drawing.Sheet = sheet;
        drawings.Add(drawing);
    }

    /// <summary>The content type of an image file by its signature.</summary>
    /// <exception cref="ArgumentException">The data is not an image Excel shows.</exception>
    internal static string ImageType(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ReadOnlySpan<byte> d = data;
        return d switch
        {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
            [(byte)'B', (byte)'M', ..] => "image/bmp",
            [(byte)'I', (byte)'I', 42, 0, ..] or [(byte)'M', (byte)'M', 0, 42, ..] => "image/tiff",
            [1, 0, 0, 0, ..] when d.Length > 44 && d[40] == ' ' && d[41] == 'E' && d[42] == 'M' && d[43] == 'F' => "image/x-emf",
            [0xD7, 0xCD, 0xC6, 0x9A, ..] or [1, 0, 9, 0, ..] or [2, 0, 9, 0, ..] => "image/x-wmf",
            _ => throw new ArgumentException("The data is not a PNG, JPEG, GIF, BMP, TIFF, EMF or WMF image.", nameof(data)),
        };
    }

    private SheetDrawing NewPicture(byte[] image)
    {
        SheetDrawing picture = new(SheetDrawingKind.Picture) { ImageData = image };
        picture.Name = "Picture " + (drawings.Count + 1);
        drawings.Add(picture);
        Changed = true;
        return picture;
    }
}
