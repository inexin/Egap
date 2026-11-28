using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.DrawingML;
using Egap.Documents.Layout;
using Egap.Presentations.Pptx;

namespace Egap.Presentations.Drawing;

/// <summary>
/// Draws one slide, layout or master (or a notes page) onto a canvas in points: its background as the slide, layout or master
/// gives it, the master's and layout's own shapes where <c>showMasterSp</c> lets them show, then the page's shapes back to front.
/// Shapes are painted by Egap.Documents' DrawingML shape engine; text by <see cref="TextEngine"/>, with the placeholder,
/// master and presentation text styles it inherits.
/// </summary>
internal sealed class SlideRenderer
{
    private static readonly XNamespace A = PptxNames.A;
    private static readonly XNamespace P = PptxNames.P;

    private readonly SlideBase target;
    private readonly Canvas canvas;
    private readonly Presentation presentation;
    private readonly Dictionary<string, ShapePaintContext> contexts = new(StringComparer.Ordinal);
    private readonly SlideBase? layout;
    private readonly SlideBase? master;
    private readonly PackagePart? themePart;

    public SlideRenderer(SlideBase target, Canvas canvas, SlideRenderOptions options)
    {
        this.target = target;
        this.canvas = canvas;
        presentation = target.Presentation;
        (layout, master) = target switch
        {
            Slide slide => ((SlideBase?)slide.Layout, (SlideBase?)slide.Layout?.Master),
            SlideLayout own => ((SlideBase?)null, (SlideBase?)own.Master),
            NotesSlide notes => ((SlideBase?)null, (SlideBase?)notes.Master),
            _ => ((SlideBase?)null, (SlideBase?)null),
        };

        SlideBase themeOwner = master ?? target;
        themePart = presentation.Package.Related(themeOwner.PartName, PptxNames.ThemeRelationship);
        Theme = presentation.RenderCache.Theme(themePart, target.ColorMap);
        Fonts = presentation.RenderCache.Fonts(options.Fonts);
        Fields = new FieldValues(target is Slide s ? SlideNumber(s) : null, options.Date ?? DateTime.Now);
    }

    /// <summary>The theme with the page's colour map applied.</summary>
    public Theme Theme { get; }

    /// <summary>The font chain.</summary>
    public SlideFonts Fonts { get; }

    /// <summary>What fields show on this page.</summary>
    public FieldValues Fields { get; }

    public void Draw()
    {
        (double width, double height) = target.SizePoints;
        RectF page = new(0, 0, (float)width, (float)height);
        int saved = canvas.Save();
        try
        {
            canvas.ClipRect(page);
            DrawBackground(page);

            bool showMaster = Shows(target) && (target is not Slide || layout is null || Shows(layout));
            if (showMaster && master is not null && master != target)
            {
                DrawTree(master.Shapes, Points, null, inheritedOnly: true);
            }

            if (layout is not null && Shows(target))
            {
                DrawTree(layout.Shapes, Points, null, inheritedOnly: true);
            }

            DrawTree(target.Shapes, Points, null, inheritedOnly: false);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>A slide's number as its fields show it: its place in the show plus the presentation's first number, less one.</summary>
    private int SlideNumber(Slide slide)
    {
        int first = (int)(PptxNames.Long(presentation.Main.Root, "firstSlideNum") ?? 1);
        int index = presentation.Slides.IndexOf(slide);
        return index < 0 ? first : first + index;
    }

    /// <summary>Whether a page lets the shapes of the master (and for a slide, the layout) behind it show (<c>showMasterSp</c>).</summary>
    private static bool Shows(SlideBase page) => PptxNames.Bool(page.Xml.Root, "showMasterSp", fallback: true);

    private static RectF Points(double x, double y, double width, double height) =>
        new((float)(x / ShapeTransform.EmusPerPoint), (float)(y / ShapeTransform.EmusPerPoint), (float)(width / ShapeTransform.EmusPerPoint), (float)(height / ShapeTransform.EmusPerPoint));

    private void DrawBackground(RectF page)
    {
        XElement? bg = Background(target) ?? (layout is null ? null : Background(layout)) ?? (master is null ? null : Background(master));
        if (bg is null)
        {
            return;
        }

        string owner = bg.Document == target.Xml.Root.Document ? target.PartName
            : layout is not null && bg.Document == layout.Xml.Root.Document ? layout.PartName
            : master?.PartName ?? target.PartName;

        if (bg.Element(P + "bgPr") is { } properties)
        {
            if (properties.Elements().FirstOrDefault(DrawingFills.IsFill) is { } fill)
            {
                Fill(page, fill, null, owner);
            }
        }
        else if (bg.Element(P + "bgRef") is { } reference)
        {
            Rgba8? color = DrawingColors.Resolve(reference, Theme, null);
            int index = (int)(PptxNames.Long(reference, "idx") ?? 0);
            XElement? scheme = Theme.FormatScheme;
            XElement? fill = index switch
            {
                >= 1001 => scheme?.Element(A + "bgFillStyleLst")?.Elements().ElementAtOrDefault(index - 1001),
                > 0 => scheme?.Element(A + "fillStyleLst")?.Elements().ElementAtOrDefault(index - 1),
                _ => null,
            };
            if (fill is not null)
            {
                // A picture in the theme's background styles is the theme part's.
                Fill(page, fill, color, themePart?.Name ?? owner);
            }
            else if (color is { } plain)
            {
                canvas.FillRect(page, plain);
            }
        }

        static XElement? Background(SlideBase page) => page.Xml.Root.Element(P + "cSld")?.Element(P + "bg");
    }

    private void Fill(RectF rect, XElement fill, Rgba8? placeholder, string owner)
    {
        if (fill.Name == A + "blipFill")
        {
            PictureFill.Draw(canvas, rect, fill, id => Picture(owner, id));
            return;
        }

        if (ShapePainter.PaintOf(fill, rect, Theme, placeholder) is { } paint)
        {
            canvas.FillRect(rect, paint);
        }
    }

    /// <summary>The picture relationship <paramref name="id"/> of part <paramref name="owner"/> names, or null.</summary>
    private Picture? Picture(string owner, string id) =>
        presentation.Package.Target(owner, id) is { } part ? presentation.RenderCache.Picture(part) : null;

    private ShapePaintContext Context(string owner)
    {
        if (!contexts.TryGetValue(owner, out ShapePaintContext? context))
        {
            context = new ShapePaintContext(Theme, (c, id, rect) => Picture(owner, id)?.Draw(c, rect), Fonts.Provider);
            contexts[owner] = context;
        }

        return context;
    }

    /// <summary>
    /// Draws shapes back to front, each placed by <paramref name="place"/> from its EMU offset and size. With
    /// <paramref name="inheritedOnly"/>, a master's or layout's placeholders are left out: on a slide they only lend their looks.
    /// </summary>
    private void DrawTree(SlideShapeCollection shapes, Func<double, double, double, double, RectF> place, XElement? groupFill, bool inheritedOnly)
    {
        foreach (SlideShape shape in shapes)
        {
            if (shape.Hidden || (inheritedOnly && shape.PlaceholderElement is not null) || HiddenByHeaderFooter(shape))
            {
                continue;
            }

            switch (shape)
            {
                case GroupShape group:
                    DrawGroup(group, place, groupFill);
                    break;
                case PictureShape picture:
                    DrawPicture(picture, place);
                    break;
                case GraphicFrame:
                    // Tables, charts, SmartArt diagrams, embedded objects: drawn from stage B3 of the plan. Until then they leave
                    // their frame empty rather than draw a stand-in.
                    break;
                default:
                    DrawShape(shape, place, groupFill);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a date, footer, slide number or header placeholder is switched off on the layout or master drawn, by its
    /// header and footer flags (<c>p:hf</c>). A slide shows the ones it has.
    /// </summary>
    private bool HiddenByHeaderFooter(SlideShape shape)
    {
        if (target is Slide || shape.Owner != target || shape.PlaceholderElement is not { } ph)
        {
            return false;
        }

        XElement? flags = target.Xml.Root.Element(P + "hf") ?? master?.Xml.Root.Element(P + "hf");
        if (flags is null)
        {
            return false;
        }

        string? flag = (string?)ph.Attribute("type") switch
        {
            "dt" => "dt",
            "ftr" => "ftr",
            "sldNum" => "sldNum",
            "hdr" => "hdr",
            _ => null,
        };
        return flag is not null && !PptxNames.Bool(flags, flag, fallback: true);
    }

    private void DrawGroup(GroupShape group, Func<double, double, double, double, RectF> place, XElement? outerFill)
    {
        XElement? properties = group.Element.Element(P + "grpSpPr");
        XElement? xfrm = properties?.Element(A + "xfrm");
        if (Offset(xfrm, "off", "ext") is not { } box)
        {
            return;
        }

        RectF rect = place(box.X, box.Y, box.Width, box.Height);
        XElement? fill = properties!.Elements().FirstOrDefault(DrawingFills.IsFill);
        if (fill?.Name == A + "grpFill")
        {
            fill = outerFill;
        }

        (double X, double Y, double Width, double Height) child = Offset(xfrm, "chOff", "chExt") is { Width: > 0, Height: > 0 } given ? given : box;
        double sx = child.Width > 0 ? rect.Width / child.Width : 0;
        double sy = child.Height > 0 ? rect.Height / child.Height : 0;
        int saved = canvas.Save();
        try
        {
            ShapePainter.Transform(canvas, rect, xfrm);

            // The group's children are in its child coordinates, mapped onto its box; their own sizes, lines and text are not scaled.
            RectF Inner(double x, double y, double width, double height) =>
                new((float)(rect.X + ((x - child.X) * sx)), (float)(rect.Y + ((y - child.Y) * sy)), (float)(width * sx), (float)(height * sy));
            DrawTree(group.Shapes, Inner, fill, inheritedOnly: false);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    private void DrawShape(SlideShape shape, Func<double, double, double, double, RectF> place, XElement? groupFill)
    {
        ShapeLook look = ShapeLook.Of(shape);
        if (Offset(look.Properties.Element(A + "xfrm"), "off", "ext") is not { } box)
        {
            return;
        }

        RectF rect = place(box.X, box.Y, box.Width, box.Height);
        string owner = shape.Owner.PartName;
        ShapePaintContext context = Context(owner);
        if (look.Properties.Element(A + "blipFill") is { } blipFill)
        {
            // A picture fill keeps its crop, stretch and tiling: the shape's own context draws it.
            context = context with { Picture = (c, _, r) => PictureFill.Draw(c, r, blipFill, id => Picture(owner, id)) };
        }

        ShapePainter.Draw(canvas, rect, look.Properties, look.Style, context, groupFill);
        if (look.TextBodies.Count > 0 && shape is AutoShape)
        {
            DrawText(look, rect);
        }
    }

    private void DrawText(ShapeLook look, RectF box)
    {
        XElement spPr = look.Properties;
        XElement? xfrm = spPr.Element(A + "xfrm");
        ShapeGeometry geometry = ShapeGeometry.Read(spPr) ?? ShapeGeometry.Preset("rect")!;
        RectF textRect = geometry.Layout(box.Width, box.Height, spPr.Element(A + "prstGeom")?.Element(A + "avLst")).TextRect;
        bool flipH = ShapePainter.Flag(xfrm, "flipH");
        bool flipV = ShapePainter.Flag(xfrm, "flipV");
        float x = flipH ? box.Width - textRect.X - textRect.Width : textRect.X;
        float y = flipV ? box.Height - textRect.Y - textRect.Height : textRect.Y;
        RectF rect = new(box.X + x, box.Y + y, textRect.Width, textRect.Height);

        double rotation = (PptxNames.Long(xfrm, "rot") ?? 0) / 60000.0;
        if (rotation != 0)
        {
            // The text rectangle turns with the shape about the shape's centre, the text itself about its own.
            Vector2 center = new(box.X + (box.Width / 2), box.Y + (box.Height / 2));
            Vector2 own = new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
            Vector2 moved = Vector2.Transform(own - center, Matrix3x2.CreateRotation((float)(rotation * Math.PI / 180))) + center;
            rect = new RectF(moved.X - (rect.Width / 2), moved.Y - (rect.Height / 2), rect.Width, rect.Height);
        }

        // A vertically flipped shape turns its text upside down; text marked upright keeps level whatever the shape's turn.
        TextFrame frame = new(look, MasterTextStyle(look), presentation.Main.Root.Element(P + "defaultTextStyle"), Theme, Fonts, Fields)
        {
            Pictures = BulletPicture,
        };
        if (frame.Upright)
        {
            rotation = 0;
        }
        else if (flipV)
        {
            rotation += 180;
        }

        TextEngine.Draw(canvas, rect, rotation, frame);
    }

    /// <summary>A picture bullet's picture: the relationship is the part's that holds the bullet's element.</summary>
    private Picture? BulletPicture(XElement element, string id)
    {
        foreach (SlideBase? page in (ReadOnlySpan<SlideBase?>)[target, layout, master])
        {
            if (page is not null && element.Document == page.Xml.Root.Document)
            {
                return Picture(page.PartName, id);
            }
        }

        return null;
    }

    /// <summary>
    /// The master text style a placeholder's text starts from: the title, body or other style by its type. A shape that is not
    /// a placeholder takes none, going from its own list style straight to the presentation's default text style, as
    /// PowerPoint draws it.
    /// </summary>
    private XElement? MasterTextStyle(ShapeLook look)
    {
        XElement? root = master?.Xml.Root ?? (target is SlideMaster or NotesMaster ? target.Xml.Root : null);
        if (root is null || look.PlaceholderType is null)
        {
            return null;
        }

        if (root.Name == P + "notesMaster")
        {
            return root.Element(P + "notesStyle");
        }

        XElement? styles = root.Element(P + "txStyles");
        return look.PlaceholderType switch
        {
            "title" or "ctrTitle" => styles?.Element(P + "titleStyle"),
            "body" or "subTitle" or "obj" or "chart" or "tbl" or "clipArt" or "dgm" or "media" or "pic" => styles?.Element(P + "bodyStyle"),
            _ => styles?.Element(P + "otherStyle"),
        };
    }

    private void DrawPicture(PictureShape picture, Func<double, double, double, double, RectF> place)
    {
        ShapeLook look = ShapeLook.Of(picture);
        if (Offset(look.Properties.Element(A + "xfrm"), "off", "ext") is not { } box)
        {
            return;
        }

        RectF rect = place(box.X, box.Y, box.Width, box.Height);
        string owner = picture.Owner.PartName;
        XElement? blipFill = picture.Element.Element(P + "blipFill");
        int saved = canvas.Save();
        try
        {
            ShapePainter.Transform(canvas, rect, look.Properties.Element(A + "xfrm"));
            if (blipFill is not null)
            {
                ShapeGeometry geometry = ShapeGeometry.Read(look.Properties) ?? ShapeGeometry.Preset("rect")!;
                GeometryLayout layout = geometry.Layout(rect.Width, rect.Height, look.Properties.Element(A + "prstGeom")?.Element(A + "avLst"));
                int clip = canvas.Save();
                canvas.Translate(rect.X, rect.Y);
                foreach (GeometryPath path in layout.Paths)
                {
                    if (path.Fill != PathFill.None)
                    {
                        canvas.ClipPath(path.Path);
                        break;
                    }
                }

                PictureFill.Draw(canvas, new RectF(0, 0, rect.Width, rect.Height), blipFill, id => Picture(owner, id));
                canvas.RestoreToCount(clip);
            }

            // The picture's outline and effects, without a fill of its own: drawn untransformed, as the canvas already turns.
            XElement outline = new(look.Properties);
            outline.Elements().Where(DrawingFills.IsFill).Remove();
            outline.Element(A + "xfrm")?.Remove();
            ShapePainter.Draw(canvas, rect, outline, look.Style, Context(owner));
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>An <c>a:xfrm</c>'s offset and extent (or child offset and extent), in EMUs; null when either is missing.</summary>
    private static (double X, double Y, double Width, double Height)? Offset(XElement? xfrm, string offset, string extent)
    {
        XElement? off = xfrm?.Element(A + offset);
        XElement? ext = xfrm?.Element(A + extent);
        if (off is null || ext is null)
        {
            return null;
        }

        return (PptxNames.Long(off, "x") ?? 0, PptxNames.Long(off, "y") ?? 0, Math.Max(0, PptxNames.Long(ext, "cx") ?? 0), Math.Max(0, PptxNames.Long(ext, "cy") ?? 0));
    }
}
