using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>What a drawing object on a sheet is.</summary>
public enum SheetDrawingKind
{
    /// <summary>A picture.</summary>
    Picture,

    /// <summary>A shape or text box.</summary>
    Shape,

    /// <summary>A chart, kept as Excel saved it.</summary>
    Chart,

    /// <summary>A group of objects.</summary>
    Group,

    /// <summary>A connector, an embedded object or anything else, kept as it was.</summary>
    Other,
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
/// text can be changed, and a picture's image replaced; everything else about it is kept as the file had it.
/// </summary>
public sealed class SheetDrawing
{
    private byte[]? imageData;

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

    /// <summary>The file's anchor element, for everything the model does not hold; null for a picture made in code.</summary>
    internal XElement? Source { get; set; }

    /// <summary>The relationship id the file's picture used for its image.</summary>
    internal string? ImageRelationshipId { get; set; }

    internal bool ImageChanged { get; set; }

    /// <summary>What the model held when the file was read, to tell whether the part can be written back as it was.</summary>
    internal string? ReadState { get; set; }

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

    internal SheetDrawingCollection()
    {
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

    internal void AddRead(SheetDrawing drawing) => drawings.Add(drawing);

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
