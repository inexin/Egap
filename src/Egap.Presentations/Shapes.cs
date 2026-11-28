using System.Collections;
using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// A shape on a slide, layout or master: an <see cref="AutoShape"/>, <see cref="PictureShape"/>, <see cref="GroupShape"/>,
/// <see cref="ConnectorShape"/> or <see cref="GraphicFrame"/>. Its id, name and position are read from the file and written
/// back where they change; everything else the shape holds is kept as it was.
/// </summary>
public abstract class SlideShape
{
    private protected SlideShape(SlideBase owner, GroupShape? group, XElement element)
    {
        Owner = owner;
        Group = group;
        Element = element;
    }

    /// <summary>The slide, layout or master the shape is on.</summary>
    public SlideBase Owner { get; }

    /// <summary>The group the shape is in, or null when it is at the top of its slide's shape tree.</summary>
    public GroupShape? Group { get; }

    /// <summary>The shape's id (<c>p:cNvPr/@id</c>), unique on its slide; connectors and animations refer to shapes by it.</summary>
    public uint Id => PptxNames.UInt(Properties, "id") ?? 0;

    /// <summary>The shape's name (<c>p:cNvPr/@name</c>), as PowerPoint's selection pane shows it.</summary>
    /// <exception cref="ArgumentNullException">The name set is null.</exception>
    public string Name
    {
        get => (string?)Properties?.Attribute("name") ?? "";
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetProperty("name", value, keepEmpty: true);
        }
    }

    /// <summary>The shape's alternative text (<c>p:cNvPr/@descr</c>), read out by screen readers; empty for none.</summary>
    /// <exception cref="ArgumentNullException">The text set is null.</exception>
    public string Description
    {
        get => (string?)Properties?.Attribute("descr") ?? "";
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetProperty("descr", value, keepEmpty: false);
        }
    }

    /// <summary>Whether the shape is hidden (<c>p:cNvPr/@hidden</c>).</summary>
    public bool Hidden => PptxNames.Bool(Properties, "hidden");

    /// <summary>
    /// The placeholder the shape fills (<c>p:nvPr/p:ph</c>), or null when it is not a placeholder. A slide's placeholder takes
    /// what it does not set itself (position, text styles) from the layout's placeholder of the same type and index.
    /// </summary>
    public ShapePlaceholder? Placeholder
    {
        get
        {
            XElement? placeholder = NonVisual?.Element(PptxNames.P + "nvPr")?.Element(PptxNames.P + "ph");
            return placeholder is null ? null : new ShapePlaceholder(PlaceholderTypeOf((string?)placeholder.Attribute("type")), PptxNames.UInt(placeholder, "idx") ?? 0);
        }
    }

    /// <summary>
    /// The shape's position, size, rotation and flips (<c>a:xfrm</c>), in EMUs with points alongside; null when the shape has
    /// none of its own, as a placeholder that takes its layout's position. Inside a group the values are in the group's child
    /// coordinates (<see cref="GroupShape.ChildArea"/>). Setting null removes the shape's own position.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width or height set is negative.</exception>
    public virtual ShapeTransform? Transform
    {
        get => ReadTransform(TransformContainer?.Element(TransformName));
        set
        {
            if (value == Transform)
            {
                return;
            }

            if (value is not { } transform)
            {
                TransformContainer?.Element(TransformName)?.Remove();
                Changed();
                return;
            }

            if (transform.Width < 0 || transform.Height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A shape's width and height must not be negative.");
            }

            XElement container = EnsureTransformContainer();
            XElement? xfrm = container.Element(TransformName);
            if (xfrm is null)
            {
                xfrm = new XElement(TransformName);
                container.AddFirst(xfrm);
                NewTransform(xfrm, transform);
            }

            long rotation = (long)Math.Round(transform.Rotation * 60000);
            xfrm.SetAttributeValue("rot", rotation == 0 ? null : PptxNames.Text(rotation));
            xfrm.SetAttributeValue("flipH", transform.FlipHorizontal ? "1" : null);
            xfrm.SetAttributeValue("flipV", transform.FlipVertical ? "1" : null);
            XElement off = xfrm.Element(PptxNames.A + "off") ?? AddFirst(xfrm, PptxNames.A + "off");
            off.SetAttributeValue("x", PptxNames.Text(transform.X));
            off.SetAttributeValue("y", PptxNames.Text(transform.Y));
            XElement? ext = xfrm.Element(PptxNames.A + "ext");
            if (ext is null)
            {
                ext = new XElement(PptxNames.A + "ext");
                off.AddAfterSelf(ext);
            }

            ext.SetAttributeValue("cx", PptxNames.Text(transform.Width));
            ext.SetAttributeValue("cy", PptxNames.Text(transform.Height));
            Changed();
        }
    }

    internal XElement Element { get; }

    /// <summary>The shape's non-visual properties (<c>p:nvSpPr</c>, <c>p:nvPicPr</c> and the rest), its first child.</summary>
    private protected XElement? NonVisual => Element.Elements().FirstOrDefault(e => e.Name.Namespace == PptxNames.P && e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal));

    /// <summary>The element that holds the shape's <c>a:xfrm</c>: its <c>p:spPr</c>, unless the kind of shape keeps it elsewhere.</summary>
    private protected virtual XElement? TransformContainer => Element.Element(PptxNames.P + "spPr");

    private protected virtual XName TransformName => PptxNames.A + "xfrm";

    private XElement? Properties => NonVisual?.Element(PptxNames.P + "cNvPr");

    /// <summary>Marks the shape's part as changed, so a save writes it again.</summary>
    internal void Changed() => Owner.Xml.MarkChanged();

    /// <summary>The element that holds the transform, made after the non-visual properties when the shape has none.</summary>
    private protected virtual XElement EnsureTransformContainer()
    {
        if (TransformContainer is { } container)
        {
            return container;
        }

        XElement made = new(PptxNames.P + "spPr");
        if (NonVisual is { } nonVisual)
        {
            nonVisual.AddAfterSelf(made);
        }
        else
        {
            Element.AddFirst(made);
        }

        return made;
    }

    /// <summary>What a new transform needs besides the offset and size, such as a group's child coordinates.</summary>
    private protected virtual void NewTransform(XElement xfrm, ShapeTransform transform)
    {
    }

    private protected static XElement AddFirst(XElement parent, XName name)
    {
        XElement child = new(name);
        parent.AddFirst(child);
        return child;
    }

    private protected static ShapeTransform? ReadTransform(XElement? xfrm)
    {
        if (xfrm is null)
        {
            return null;
        }

        XElement? off = xfrm.Element(PptxNames.A + "off");
        XElement? ext = xfrm.Element(PptxNames.A + "ext");
        double rotation = (PptxNames.Long(xfrm, "rot") ?? 0) / 60000.0;
        return new ShapeTransform(PptxNames.Long(off, "x") ?? 0, PptxNames.Long(off, "y") ?? 0, PptxNames.Long(ext, "cx") ?? 0, PptxNames.Long(ext, "cy") ?? 0,
            rotation, PptxNames.Bool(xfrm, "flipH"), PptxNames.Bool(xfrm, "flipV"));
    }

    private protected string? PresetGeometryOf() => (string?)Element.Element(PptxNames.P + "spPr")?.Element(PptxNames.A + "prstGeom")?.Attribute("prst");

    private void SetProperty(string name, string value, bool keepEmpty)
    {
        XElement? properties = Properties;
        if (properties is null || ((string?)properties.Attribute(name) ?? "") == value)
        {
            return;
        }

        properties.SetAttributeValue(name, value.Length == 0 && !keepEmpty ? null : value);
        Changed();
    }

    private static PlaceholderType PlaceholderTypeOf(string? type) => type switch
    {
        "title" => PlaceholderType.Title,
        "body" => PlaceholderType.Body,
        "ctrTitle" => PlaceholderType.CenteredTitle,
        "subTitle" => PlaceholderType.Subtitle,
        "dt" => PlaceholderType.Date,
        "sldNum" => PlaceholderType.SlideNumber,
        "ftr" => PlaceholderType.Footer,
        "hdr" => PlaceholderType.Header,
        "chart" => PlaceholderType.Chart,
        "tbl" => PlaceholderType.Table,
        "clipArt" => PlaceholderType.ClipArt,
        "dgm" => PlaceholderType.Diagram,
        "media" => PlaceholderType.Media,
        "sldImg" => PlaceholderType.SlideImage,
        "pic" => PlaceholderType.Picture,
        _ => PlaceholderType.Content,
    };
}

/// <summary>A shape with a geometry, a fill and an outline, and usually text (<c>p:sp</c>): rectangles, text boxes, placeholders.</summary>
public sealed class AutoShape : SlideShape
{
    private TextBody? text;

    internal AutoShape(SlideBase owner, GroupShape? group, XElement element)
        : base(owner, group, element)
    {
    }

    /// <summary>
    /// The name of the shape's preset geometry (<c>a:prstGeom/@prst</c>), as ECMA-376 names them: <c>rect</c>,
    /// <c>roundRect</c>, <c>ellipse</c>, <c>rightArrow</c> and the rest; null for a custom geometry or one the shape takes
    /// from its layout's placeholder.
    /// </summary>
    public string? PresetGeometry => PresetGeometryOf();

    /// <summary>Whether the shape is a text box (<c>p:cNvSpPr/@txBox</c>).</summary>
    public bool IsTextBox => PptxNames.Bool(NonVisual?.Element(PptxNames.P + "cNvSpPr"), "txBox");

    /// <summary>The shape's text (<c>p:txBody</c>), or null when it has none.</summary>
    public TextBody? TextBody
    {
        get
        {
            XElement? body = Element.Element(PptxNames.P + "txBody");
            if (body is null)
            {
                return null;
            }

            return text is not null && text.Element == body ? text : text = new TextBody(this, body);
        }
    }
}

/// <summary>A picture (<c>p:pic</c>): an image from a part of the file, cropped and framed.</summary>
public sealed class PictureShape : SlideShape
{
    internal PictureShape(SlideBase owner, GroupShape? group, XElement element)
        : base(owner, group, element)
    {
    }

    /// <summary>The name of the image's part, such as <c>/ppt/media/image1.png</c>, or null when the picture is linked or its part is missing.</summary>
    public string? ImagePartName => ImagePart?.Name;

    /// <summary>The image part's content type, such as <c>image/png</c>, or null when the picture has no image part.</summary>
    public string? ImageContentType => ImagePart?.ContentType;

    /// <summary>A copy of the image's bytes as the file holds them, or null when the picture has no image part.</summary>
    public byte[]? GetImageData() => ImagePart?.Data.ToArray();

    private OpcPart? ImagePart
    {
        get
        {
            string? id = (string?)Element.Element(PptxNames.P + "blipFill")?.Element(PptxNames.A + "blip")?.Attribute(PptxNames.R + "embed");
            return id is not null && Owner.Xml.Part.GetRelationship(id) is { External: false } relationship
                ? Owner.Presentation.Package.Opc.GetPart(relationship.TargetName)
                : null;
        }
    }
}

/// <summary>
/// A group of shapes (<c>p:grpSp</c>), moved and sized as one. Its shapes are placed in its child coordinates
/// (<see cref="ChildArea"/>), which map onto the group's own <see cref="SlideShape.Transform"/>.
/// </summary>
public sealed class GroupShape : SlideShape
{
    internal GroupShape(SlideBase owner, GroupShape? group, XElement element)
        : base(owner, group, element)
    {
        Shapes = new SlideShapeCollection(owner, element, this);
    }

    /// <summary>The shapes in the group, in the order they are drawn.</summary>
    public SlideShapeCollection Shapes { get; }

    /// <summary>
    /// The rectangle of the group's child coordinates (<c>a:chOff</c> and <c>a:chExt</c>) that maps onto the group's
    /// <see cref="SlideShape.Transform"/>; rotation and flips are not used. Null when the group has no transform.
    /// </summary>
    public ShapeTransform? ChildArea
    {
        get
        {
            XElement? xfrm = TransformContainer?.Element(TransformName);
            if (xfrm is null)
            {
                return null;
            }

            XElement? off = xfrm.Element(PptxNames.A + "chOff");
            XElement? ext = xfrm.Element(PptxNames.A + "chExt");
            return new ShapeTransform(PptxNames.Long(off, "x") ?? 0, PptxNames.Long(off, "y") ?? 0, PptxNames.Long(ext, "cx") ?? 0, PptxNames.Long(ext, "cy") ?? 0);
        }
    }

    private protected override XElement? TransformContainer => Element.Element(PptxNames.P + "grpSpPr");

    private protected override XElement EnsureTransformContainer()
    {
        if (TransformContainer is { } container)
        {
            return container;
        }

        XElement made = new(PptxNames.P + "grpSpPr");
        if (NonVisual is { } nonVisual)
        {
            nonVisual.AddAfterSelf(made);
        }
        else
        {
            Element.AddFirst(made);
        }

        return made;
    }

    private protected override void NewTransform(XElement xfrm, ShapeTransform transform)
    {
        // A new transform's child coordinates are the group's own, so its shapes stay where they are.
        xfrm.Add(
            new XElement(PptxNames.A + "chOff", new XAttribute("x", PptxNames.Text(transform.X)), new XAttribute("y", PptxNames.Text(transform.Y))),
            new XElement(PptxNames.A + "chExt", new XAttribute("cx", PptxNames.Text(transform.Width)), new XAttribute("cy", PptxNames.Text(transform.Height))));
    }
}

/// <summary>A connector (<c>p:cxnSp</c>): a line or elbow between two shapes, which follows them when they move.</summary>
public sealed class ConnectorShape : SlideShape
{
    internal ConnectorShape(SlideBase owner, GroupShape? group, XElement element)
        : base(owner, group, element)
    {
    }

    /// <summary>The name of the connector's preset geometry, such as <c>line</c>, <c>straightConnector1</c> or <c>bentConnector3</c>.</summary>
    public string? PresetGeometry => PresetGeometryOf();

    /// <summary>The shape and connection site the connector starts at (<c>a:stCxn</c>), or null when its start is free.</summary>
    public ShapeConnection? Start => Connection("stCxn");

    /// <summary>The shape and connection site the connector ends at (<c>a:endCxn</c>), or null when its end is free.</summary>
    public ShapeConnection? End => Connection("endCxn");

    private ShapeConnection? Connection(string name)
    {
        XElement? connection = NonVisual?.Element(PptxNames.P + "cNvCxnSpPr")?.Element(PptxNames.A + name);
        return PptxNames.UInt(connection, "id") is uint id ? new ShapeConnection(id, PptxNames.UInt(connection, "idx") ?? 0) : null;
    }
}

/// <summary>
/// A frame for a table, chart, SmartArt diagram or embedded object (<c>p:graphicFrame</c>). What it holds is kept as it is
/// and written back; <see cref="Kind"/> says what it is.
/// </summary>
public sealed class GraphicFrame : SlideShape
{
    internal GraphicFrame(SlideBase owner, GroupShape? group, XElement element)
        : base(owner, group, element)
    {
    }

    /// <summary>What the frame holds.</summary>
    public GraphicFrameKind Kind => StrictNamespaces.ToTransitional(GraphicDataUri) switch
    {
        PptxNames.TableUri => GraphicFrameKind.Table,
        PptxNames.ChartUri or PptxNames.ChartExUri => GraphicFrameKind.Chart,
        PptxNames.DiagramUri => GraphicFrameKind.Diagram,
        PptxNames.OleUri => GraphicFrameKind.OleObject,
        _ => GraphicFrameKind.Other,
    };

    /// <summary>The URI that names the kind of content (<c>a:graphicData/@uri</c>), as the file spells it.</summary>
    public string GraphicDataUri => (string?)Element.Element(PptxNames.A + "graphic")?.Element(PptxNames.A + "graphicData")?.Attribute("uri") ?? "";

    /// <summary>
    /// The frame's position and size (<c>p:xfrm</c>), in EMUs with points alongside. A frame always has one; setting null
    /// is not allowed.
    /// </summary>
    /// <exception cref="ArgumentNullException">The transform set is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width or height set is negative.</exception>
    public override ShapeTransform? Transform
    {
        get => base.Transform;
        set => base.Transform = value ?? throw new ArgumentNullException(nameof(value), "A graphic frame needs a position and size.");
    }

    private protected override XElement? TransformContainer => Element;

    private protected override XName TransformName => PptxNames.P + "xfrm";

    private protected override XElement EnsureTransformContainer() => Element;

    private protected override void NewTransform(XElement xfrm, ShapeTransform transform)
    {
        // p:xfrm goes after the non-visual properties, not first.
        xfrm.Remove();
        if (NonVisual is { } nonVisual)
        {
            nonVisual.AddAfterSelf(xfrm);
        }
        else
        {
            Element.AddFirst(xfrm);
        }
    }
}

/// <summary>What a <see cref="GraphicFrame"/> holds.</summary>
public enum GraphicFrameKind
{
    /// <summary>Something else, named by <see cref="GraphicFrame.GraphicDataUri"/>.</summary>
    Other,

    /// <summary>A table (<c>a:tbl</c>).</summary>
    Table,

    /// <summary>A chart, in a chart part of its own.</summary>
    Chart,

    /// <summary>A SmartArt diagram, in diagram parts of its own.</summary>
    Diagram,

    /// <summary>An embedded or linked OLE object, such as a workbook.</summary>
    OleObject,
}

/// <summary>The shapes of a slide, layout, master or group, in the order they are drawn: the first is at the back.</summary>
public sealed class SlideShapeCollection : IReadOnlyList<SlideShape>
{
    private readonly List<SlideShape> shapes = [];

    internal SlideShapeCollection(SlideBase owner, XElement? tree, GroupShape? group = null)
    {
        if (tree is null)
        {
            return;
        }

        foreach (XElement element in tree.Elements())
        {
            if (MarkupCompatibility.IsAlternateContent(element))
            {
                // A shape PowerPoint wrote two ways, for newer and older readers: the branch this reader understands.
                foreach (XElement chosen in MarkupCompatibility.Choose(element, PptxNames.Understood))
                {
                    Add(owner, chosen, group);
                }
            }
            else
            {
                Add(owner, element, group);
            }
        }
    }

    /// <summary>The number of shapes.</summary>
    public int Count => shapes.Count;

    /// <summary>The shape at <paramref name="index"/>, from 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no shape at that position.</exception>
    public SlideShape this[int index] => shapes[index];

    /// <summary>The shape with the id <paramref name="id"/> here or in a group inside, or null.</summary>
    public SlideShape? FindById(uint id)
    {
        foreach (SlideShape shape in shapes)
        {
            if (shape.Id == id)
            {
                return shape;
            }

            if (shape is GroupShape group && group.Shapes.FindById(id) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IEnumerator<SlideShape> GetEnumerator() => shapes.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Add(SlideBase owner, XElement element, GroupShape? group)
    {
        if (element.Name.Namespace != PptxNames.P)
        {
            return;
        }

        SlideShape? shape = element.Name.LocalName switch
        {
            "sp" => new AutoShape(owner, group, element),
            "pic" => new PictureShape(owner, group, element),
            "grpSp" => new GroupShape(owner, group, element),
            "cxnSp" => new ConnectorShape(owner, group, element),
            "graphicFrame" => new GraphicFrame(owner, group, element),
            _ => null,
        };
        if (shape is not null)
        {
            shapes.Add(shape);
        }
    }
}

/// <summary>
/// A shape's position, size, rotation and flips, in EMUs (English Metric Units: 914,400 to the inch, 12,700 to the point),
/// with the position and size in points alongside.
/// </summary>
/// <param name="X">The left edge in EMUs, before rotation.</param>
/// <param name="Y">The top edge in EMUs, before rotation.</param>
/// <param name="Width">The width in EMUs.</param>
/// <param name="Height">The height in EMUs.</param>
/// <param name="Rotation">The clockwise rotation in degrees, about the centre.</param>
/// <param name="FlipHorizontal">Whether the shape is mirrored left to right.</param>
/// <param name="FlipVertical">Whether the shape is mirrored top to bottom.</param>
public readonly record struct ShapeTransform(long X, long Y, long Width, long Height, double Rotation = 0, bool FlipHorizontal = false, bool FlipVertical = false)
{
    /// <summary>The EMUs in a point.</summary>
    public const int EmusPerPoint = 12_700;

    /// <summary>The left edge in points.</summary>
    public double XPoints => X / (double)EmusPerPoint;

    /// <summary>The top edge in points.</summary>
    public double YPoints => Y / (double)EmusPerPoint;

    /// <summary>The width in points.</summary>
    public double WidthPoints => Width / (double)EmusPerPoint;

    /// <summary>The height in points.</summary>
    public double HeightPoints => Height / (double)EmusPerPoint;

    /// <summary>A position and size given in points, rounded to whole EMUs, with no rotation or flips.</summary>
    public static ShapeTransform FromPoints(double x, double y, double width, double height) => new(ToEmus(x), ToEmus(y), ToEmus(width), ToEmus(height));

    internal static long ToEmus(double points) => (long)Math.Round(points * EmusPerPoint, MidpointRounding.AwayFromZero);

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X}, {Y}) {Width} x {Height} EMU, {Rotation}°{(FlipHorizontal ? ", flipped horizontally" : "")}{(FlipVertical ? ", flipped vertically" : "")}");
}

/// <summary>The placeholder a shape fills: its type and index, which match it to its layout's and master's placeholders.</summary>
/// <param name="Type">The kind of placeholder (<c>p:ph/@type</c>); <see cref="PlaceholderType.Content"/> when the file names none.</param>
/// <param name="Index">The placeholder's index (<c>p:ph/@idx</c>), 0 when the file gives none.</param>
public sealed record ShapePlaceholder(PlaceholderType Type, uint Index);

/// <summary>The kinds of placeholder (ECMA-376 Part 1, 19.7.10, ST_PlaceholderType).</summary>
public enum PlaceholderType
{
    /// <summary>Any content (<c>obj</c>), the default.</summary>
    Content,

    /// <summary>A title (<c>title</c>).</summary>
    Title,

    /// <summary>Body text (<c>body</c>).</summary>
    Body,

    /// <summary>A title slide's centred title (<c>ctrTitle</c>).</summary>
    CenteredTitle,

    /// <summary>A subtitle (<c>subTitle</c>).</summary>
    Subtitle,

    /// <summary>The date and time (<c>dt</c>).</summary>
    Date,

    /// <summary>The slide number (<c>sldNum</c>).</summary>
    SlideNumber,

    /// <summary>The footer (<c>ftr</c>).</summary>
    Footer,

    /// <summary>The header, on notes and handouts (<c>hdr</c>).</summary>
    Header,

    /// <summary>A chart (<c>chart</c>).</summary>
    Chart,

    /// <summary>A table (<c>tbl</c>).</summary>
    Table,

    /// <summary>Clip art (<c>clipArt</c>).</summary>
    ClipArt,

    /// <summary>A SmartArt diagram (<c>dgm</c>).</summary>
    Diagram,

    /// <summary>Audio or video (<c>media</c>).</summary>
    Media,

    /// <summary>The slide's picture on a notes page (<c>sldImg</c>).</summary>
    SlideImage,

    /// <summary>A picture (<c>pic</c>).</summary>
    Picture,
}

/// <summary>Where a connector end is attached: a shape's id and the connection site on it, numbered from 0.</summary>
/// <param name="ShapeId">The id of the shape (<see cref="SlideShape.Id"/>).</param>
/// <param name="Site">The connection site on the shape, from 0, as its geometry numbers them.</param>
public readonly record struct ShapeConnection(uint ShapeId, uint Site);
