using System.Xml.Linq;

using Egami;
using Egami.Formats;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>Adding and removing shapes.</summary>
public sealed partial class SlideShapeCollection
{
    /// <summary>EMUs in a pixel at 96 pixels an inch.</summary>
    private const long EmusPerPixel = 9525;

    /// <summary>
    /// Adds a shape with the preset geometry <paramref name="presetGeometry"/> (<c>rect</c>, <c>roundRect</c>,
    /// <c>ellipse</c>, <c>rightArrow</c> and the rest, as ECMA-376 names them), filled and outlined in the theme's first
    /// accent colour as PowerPoint draws a new shape, with <paramref name="text"/> centred in it.
    /// </summary>
    /// <exception cref="ArgumentException">The geometry is null or empty.</exception>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is negative.</exception>
    public AutoShape AddShape(string presetGeometry, ShapeTransform transform, string text = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(presetGeometry);
        ArgumentNullException.ThrowIfNull(text);
        uint id = NewId(transform);
        XElement body = TextBody.NewElement(text, bodyProperties: new XElement(PptxNames.A + "bodyPr", new XAttribute("rtlCol", "0"), new XAttribute("anchor", "ctr")));
        foreach (XElement paragraph in body.Elements(PptxNames.A + "p"))
        {
            paragraph.AddFirst(new XElement(PptxNames.A + "pPr", new XAttribute("algn", "ctr")));
        }

        return (AutoShape)Append(new XElement(PptxNames.P + "sp",
            NonVisualElement("nvSpPr", "cNvSpPr", id, "Shape " + PptxNames.Text(id - 1)),
            new XElement(PptxNames.P + "spPr", Xfrm(transform), Geometry(presetGeometry)),
            Style("2", "1", "lt1", shade: true),
            body));
    }

    /// <summary>Adds a text box: <paramref name="text"/> in a frame with no fill or outline that grows to fit it.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is negative.</exception>
    public AutoShape AddTextBox(ShapeTransform transform, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint id = NewId(transform);
        XElement nonVisual = NonVisualElement("nvSpPr", "cNvSpPr", id, "TextBox " + PptxNames.Text(id - 1));
        nonVisual.Element(PptxNames.P + "cNvSpPr")!.SetAttributeValue("txBox", "1");
        return (AutoShape)Append(new XElement(PptxNames.P + "sp",
            nonVisual,
            new XElement(PptxNames.P + "spPr", Xfrm(transform), Geometry("rect"), new XElement(PptxNames.A + "noFill")),
            TextBody.NewElement(text, bodyProperties: new XElement(PptxNames.A + "bodyPr", new XAttribute("wrap", "square"), new XAttribute("rtlCol", "0"),
                new XElement(PptxNames.A + "spAutoFit")))));
    }

    /// <summary>
    /// Adds a picture of <paramref name="image"/>: a PNG, JPEG, GIF, BMP or TIFF file, its kind found from its header. With
    /// no <paramref name="transform"/> it is placed at the top left at its size at 96 pixels an inch. An image the
    /// presentation already holds byte for byte is shared, not stored twice.
    /// </summary>
    /// <exception cref="ArgumentException">The bytes are not an image of a kind PowerPoint shows.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is negative.</exception>
    public PictureShape AddPicture(ReadOnlyMemory<byte> image, ShapeTransform? transform = null)
    {
        (string contentType, string extension, int width, int height) = Probe(image);
        ShapeTransform place = transform ?? new ShapeTransform(0, 0, width * EmusPerPixel, height * EmusPerPixel);
        uint id = NewId(place);
        PptxPackage package = owner.Presentation.Package;
        string partName = package.Parts.FirstOrDefault(p => p.ContentType == contentType && p.Name.StartsWith("/ppt/media/", StringComparison.OrdinalIgnoreCase)
            && p.Data.AsSpan().SequenceEqual(image.Span))?.Name
            ?? package.AddPart(package.UniqueName("/ppt/media/image", "." + extension), contentType, image.ToArray()).Name;
        string rid = package.AddRelationship(owner.PartName, PptxNames.ImageRelationship, partName);
        XElement nonVisual = NonVisualElement("nvPicPr", "cNvPicPr", id, "Picture " + PptxNames.Text(id - 1));
        nonVisual.Element(PptxNames.P + "cNvPicPr")!.Add(new XElement(PptxNames.A + "picLocks", new XAttribute("noChangeAspect", "1")));
        return (PictureShape)Append(new XElement(PptxNames.P + "pic",
            nonVisual,
            new XElement(PptxNames.P + "blipFill", new XElement(PptxNames.A + "blip", new XAttribute(PptxNames.R + "embed", rid)),
                new XElement(PptxNames.A + "stretch", new XElement(PptxNames.A + "fillRect"))),
            new XElement(PptxNames.P + "spPr", Xfrm(place), Geometry("rect"))));
    }

    /// <summary>Adds an empty group whose child coordinates are its own position; add shapes to its <see cref="GroupShape.Shapes"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is negative.</exception>
    public GroupShape AddGroup(ShapeTransform transform)
    {
        uint id = NewId(transform);
        XElement xfrm = Xfrm(transform);
        xfrm.Add(
            new XElement(PptxNames.A + "chOff", new XAttribute("x", PptxNames.Text(transform.X)), new XAttribute("y", PptxNames.Text(transform.Y))),
            new XElement(PptxNames.A + "chExt", new XAttribute("cx", PptxNames.Text(transform.Width)), new XAttribute("cy", PptxNames.Text(transform.Height))));
        return (GroupShape)Append(new XElement(PptxNames.P + "grpSp",
            NonVisualElement("nvGrpSpPr", "cNvGrpSpPr", id, "Group " + PptxNames.Text(id - 1)),
            new XElement(PptxNames.P + "grpSpPr", xfrm)));
    }

    /// <summary>
    /// Adds a connector: a line from the top left of <paramref name="transform"/> to its bottom right (flip it for the
    /// other diagonal), drawn in the theme's text colour. Attach its ends with <see cref="ConnectorShape.Start"/> and
    /// <see cref="ConnectorShape.End"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The geometry is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is negative.</exception>
    public ConnectorShape AddConnector(ShapeTransform transform, string presetGeometry = "straightConnector1")
    {
        ArgumentException.ThrowIfNullOrEmpty(presetGeometry);
        uint id = NewId(transform);
        return (ConnectorShape)Append(new XElement(PptxNames.P + "cxnSp",
            NonVisualElement("nvCxnSpPr", "cNvCxnSpPr", id, "Connector " + PptxNames.Text(id - 1)),
            new XElement(PptxNames.P + "spPr", Xfrm(transform), Geometry(presetGeometry)),
            Style("1", "0", "tx1", shade: false)));
    }

    /// <summary>
    /// Adds a table of <paramref name="rows"/> by <paramref name="columns"/> empty cells filling <paramref name="transform"/>,
    /// in PowerPoint's default table style (<see cref="SlideTable.DefaultStyleId"/>) with a header row and banded rows.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">There are fewer than one row or column, or the width or height is negative.</exception>
    public GraphicFrame AddTable(int rows, int columns, ShapeTransform transform)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        uint id = NewId(transform);
        XElement nonVisual = NonVisualElement("nvGraphicFramePr", "cNvGraphicFramePr", id, "Table " + PptxNames.Text(id - 1));
        nonVisual.Element(PptxNames.P + "cNvGraphicFramePr")!.Add(new XElement(PptxNames.A + "graphicFrameLocks", new XAttribute("noGrp", "1")));
        return (GraphicFrame)Append(new XElement(PptxNames.P + "graphicFrame",
            nonVisual,
            Xfrm(transform, PptxNames.P + "xfrm"),
            new XElement(PptxNames.A + "graphic", new XElement(PptxNames.A + "graphicData", new XAttribute("uri", PptxNames.TableUri),
                SlideTable.NewElement(rows, columns, transform.Width, transform.Height)))));
    }

    /// <summary>
    /// Removes <paramref name="shape"/> and its animations (and, for a group, those of the shapes in it). Connectors attached
    /// to it stay where they are.
    /// </summary>
    /// <returns>Whether the shape was here.</returns>
    /// <exception cref="ArgumentNullException">The shape is null.</exception>
    public bool Remove(SlideShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!shapes.Remove(shape))
        {
            return false;
        }

        XElement element = shape.Element;
        XElement? alternate = element.Ancestors().FirstOrDefault(MarkupCompatibility.IsAlternateContent);
        owner.RemoveAnimationsOf(element);
        (alternate ?? element).Remove();
        owner.Xml.MarkChanged();
        return true;
    }

    /// <summary>Adds a ready-made shape element at the end of the tree, the front of the slide.</summary>
    internal SlideShape Append(XElement element)
    {
        tree ??= owner.ShapeTree;
        if (tree.Element(PptxNames.P + "extLst") is { } extensions)
        {
            extensions.AddBeforeSelf(element);
        }
        else
        {
            tree.Add(element);
        }

        owner.Xml.MarkChanged();
        Add(owner, element, group);
        return shapes[^1];
    }

    internal uint NewId(ShapeTransform transform)
    {
        if (transform.Width < 0 || transform.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transform), "A shape's width and height must not be negative.");
        }

        return owner.NextShapeId();
    }

    internal static XElement NonVisualElement(string name, string drawing, uint id, string shapeName) => new(PptxNames.P + name,
        new XElement(PptxNames.P + "cNvPr", new XAttribute("id", PptxNames.Text(id)), new XAttribute("name", shapeName)),
        new XElement(PptxNames.P + drawing),
        new XElement(PptxNames.P + "nvPr"));

    internal static XElement Xfrm(ShapeTransform transform, XName? name = null)
    {
        XElement xfrm = new(name ?? PptxNames.A + "xfrm");
        long rotation = (long)Math.Round(transform.Rotation * 60000);
        xfrm.SetAttributeValue("rot", rotation == 0 ? null : PptxNames.Text(rotation));
        xfrm.SetAttributeValue("flipH", transform.FlipHorizontal ? "1" : null);
        xfrm.SetAttributeValue("flipV", transform.FlipVertical ? "1" : null);
        xfrm.Add(
            new XElement(PptxNames.A + "off", new XAttribute("x", PptxNames.Text(transform.X)), new XAttribute("y", PptxNames.Text(transform.Y))),
            new XElement(PptxNames.A + "ext", new XAttribute("cx", PptxNames.Text(transform.Width)), new XAttribute("cy", PptxNames.Text(transform.Height))));
        return xfrm;
    }

    /// <summary>The content type, extension and pixel size of an image PowerPoint shows.</summary>
    private static (string ContentType, string Extension, int Width, int Height) Probe(ReadOnlyMemory<byte> image)
    {
        RasterInfo info;
        try
        {
            info = Raster.Probe(image, Imaging.Context);
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            throw new ArgumentException("The bytes are not an image Egap can read.", nameof(image), e);
        }

        string[] shown = ["image/png", "image/jpeg", "image/gif", "image/bmp", "image/tiff"];
        string contentType = info.Format.MimeTypes.FirstOrDefault(m => shown.Contains(m, StringComparer.OrdinalIgnoreCase))?.ToLowerInvariant()
            ?? throw new ArgumentException($"PowerPoint does not show {info.Format.Name} pictures; use PNG, JPEG, GIF, BMP or TIFF.", nameof(image));
        string extension = contentType["image/".Length..];
        return (contentType, extension, info.Width, info.Height);
    }

    private static XElement Geometry(string preset) => new(PptxNames.A + "prstGeom", new XAttribute("prst", preset), new XElement(PptxNames.A + "avLst"));

    /// <summary>The shape style (<c>p:style</c>) of a new shape: the theme's line, fill and effect in accent 1, and a font colour.</summary>
    private static XElement Style(string line, string fill, string font, bool shade)
    {
        XElement lineColor = Accent();
        if (shade)
        {
            lineColor.Add(new XElement(PptxNames.A + "shade", new XAttribute("val", "50000")));
        }

        return new XElement(PptxNames.P + "style",
            new XElement(PptxNames.A + "lnRef", new XAttribute("idx", line), lineColor),
            new XElement(PptxNames.A + "fillRef", new XAttribute("idx", fill), Accent()),
            new XElement(PptxNames.A + "effectRef", new XAttribute("idx", "0"), Accent()),
            new XElement(PptxNames.A + "fontRef", new XAttribute("idx", "minor"), new XElement(PptxNames.A + "schemeClr", new XAttribute("val", font))));

        static XElement Accent() => new(PptxNames.A + "schemeClr", new XAttribute("val", "accent1"));
    }
}
