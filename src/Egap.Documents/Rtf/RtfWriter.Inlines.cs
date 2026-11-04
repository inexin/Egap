using System.Buffers.Binary;
using System.Globalization;

using Egami;
using Egami.Formats;
using Egami.Formats.Png;

namespace Egap.Documents.Rtf;

/// <summary>The inlines: runs, tabs and breaks, fields, bookmarks, notes, comments, pictures, shapes and kept islands.</summary>
internal sealed partial class RtfWriter
{
    /// <summary>The fields whose groups are open, innermost last.</summary>
    private readonly List<OpenField> openFields = [];

    /// <summary>The fields below this count belong to an enclosing story or cell and stay open.</summary>
    private int fieldFloor;

    /// <summary>Counts the paragraphs written, so a field knows whether it closes in the paragraph it opened in.</summary>
    private int paragraphSerial;

    /// <summary>A note's first paragraph starts with its automatic mark.</summary>
    private bool noteMarkPending;

    /// <summary>A field being written: the paragraph it opened in and whether its result has begun.</summary>
    private sealed class OpenField(int paragraph)
    {
        public int Paragraph { get; } = paragraph;

        public bool Separated { get; set; }
    }

    private void WriteInlines(Paragraph paragraph)
    {
        List<(int Index, string Markup)>? kept = options.KeepUnknownGroups ? paragraph.Extensions.Get<RtfExtension>(RtfExtension.Format)?.Inline : null;
        int next = 0;
        NodeCollection<Inline> inlines = paragraph.Inlines;
        for (int i = 0; i < inlines.Count; i++)
        {
            while (kept is not null && next < kept.Count && kept[next].Index <= i)
            {
                Raw(kept[next++].Markup);
            }

            WriteInline(inlines[i]);
        }

        while (kept is not null && next < kept.Count)
        {
            Raw(kept[next++].Markup);
        }
    }

    private void WriteInline(Inline inline)
    {
        switch (inline)
        {
            case Run run:
                WriteRun(run);
                break;
            case Tab:
                Marked(inline, () => Word("tab"));
                break;
            case Break @break:
                Marked(inline, () => Word(@break.Kind switch { BreakKind.Page => "page", BreakKind.Column => "column", _ => "line" }));
                break;
            case FieldStart start:
                StartField(start);
                break;
            case FieldSeparator:
                if (openFields.Count > fieldFloor && !openFields[^1].Separated)
                {
                    Close();
                    Open();
                    Word("fldrslt");
                    openFields[^1].Separated = true;
                }

                break;
            case FieldEnd:
                if (openFields.Count > fieldFloor)
                {
                    CloseField();
                }

                break;
            case BookmarkStart start:
                TextDestination("bkmkstart", start.Name);
                break;
            case BookmarkEnd end:
                TextDestination("bkmkend", end.Name);
                break;
            case CommentRangeStart start:
                TextDestination("atrfstart", start.CommentId.ToString(CultureInfo.InvariantCulture));
                break;
            case CommentRangeEnd end:
                TextDestination("atrfend", end.CommentId.ToString(CultureInfo.InvariantCulture));
                break;
            case Comment comment:
                WriteComment(comment);
                break;
            case Footnote note:
                WriteNote(note);
                break;
            case Hyperlink link:
                WriteHyperlink(link);
                break;
            case InlineContentControl control:
                warnings.Add(ConversionWarningKind.ContentControlsDropped, "RTF has no content controls; their content was kept.");
                foreach (Inline child in control.Inlines)
                {
                    WriteInline(child);
                }

                break;
            case Picture picture:
                Marked(inline, () => WritePicture(picture));
                break;
            case Shape shape:
                WriteShape(shape);
                break;
            case Island island:
                WriteIsland(island);
                break;
        }
    }

    /// <summary>Writes an inline, in a group with its tracked change when it has one.</summary>
    private void Marked(Inline inline, Action write)
    {
        if (inline.Revision is not { } revision)
        {
            write();
            return;
        }

        Open();
        RevisionWords(revision);
        write();
        Close();
    }

    private void WriteRun(Run run)
    {
        string text = run.Text;
        if (text.Length == 0)
        {
            return;
        }

        Open();
        RunWords(FormatOf(run), run.Style is { IsInDocument: true } ? run.Style : null);
        if (run.Revision is { } revision)
        {
            RevisionWords(revision);
        }

        if (run.FormatRevision is { OldFormat: RunFormat old } change)
        {
            Word("crauth", Author(change.Author));
            if (Dttm(change.Date) is int date)
            {
                Word("crdate", date);
            }

            Destination("oldcprops");
            RunWords(old, null);
            Close();
        }

        Text(text);
        Close();
    }

    /// <summary>A run's text with the formatting the paragraph's style gives and nothing of its own.</summary>
    private void PlainRun(string text)
    {
        Open();
        RunWords(given.Run(current?.Style, null), null);
        Text(text);
        Close();
    }

    /// <summary>A tracked insertion or deletion; a move is written as the insertion or deletion it is in RTF.</summary>
    private void RevisionWords(Revision revision)
    {
        int author = Author(revision.Author);
        int? date = Dttm(revision.Date);
        if (revision.Removes)
        {
            Word("deleted");
            Word("revauthdel", author);
            if (date is int deleted)
            {
                Word("revdttmdel", deleted);
            }
        }
        else
        {
            Word("revised");
            Word("revauth", author);
            if (date is int inserted)
            {
                Word("revdttm", inserted);
            }
        }
    }

    /// <summary>An ignorable destination that holds text: <c>{\*\word text}</c>.</summary>
    private void TextDestination(string word, string text)
    {
        Destination(word);
        Space();
        Text(text);
        Close();
    }

    // ---- Fields ----

    private void StartField(FieldStart start)
    {
        Open();
        Word("field");
        if (start.IsLocked)
        {
            Word("fldlock");
        }

        if (start.IsDirty)
        {
            Word("flddirty");
        }

        Destination("fldinst");
        openFields.Add(new OpenField(paragraphSerial));
    }

    /// <summary>
    /// Closes the innermost field's groups. A field that opened in an earlier paragraph took that paragraph's state with it,
    /// so the paragraph being written says its formatting again.
    /// </summary>
    private void CloseField()
    {
        OpenField field = openFields[^1];
        openFields.RemoveAt(openFields.Count - 1);
        Close();
        Close();
        if (field.Paragraph != paragraphSerial && current is not null)
        {
            ParagraphProperties(current, currentLevel);
        }
    }

    /// <summary>Closes the fields the story or cell being written opened and left open.</summary>
    private void CloseFields()
    {
        while (openFields.Count > fieldFloor)
        {
            CloseField();
        }
    }

    /// <summary>A hyperlink, as the <c>HYPERLINK</c> field RTF has for it.</summary>
    private void WriteHyperlink(Hyperlink link)
    {
        System.Text.StringBuilder code = new(" HYPERLINK ");
        if (!string.IsNullOrEmpty(link.Address))
        {
            Quoted(link.Address);
        }

        Switch("l", link.Anchor);
        Switch("o", link.Tooltip);
        Switch("t", link.Target);

        Open();
        Word("field");
        Destination("fldinst");
        PlainRun(code.ToString());
        Close();
        Open();
        Word("fldrslt");
        foreach (Inline child in link.Inlines)
        {
            WriteInline(child);
        }

        Close();
        Close();

        void Switch(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                code.Append('\\').Append(name).Append(' ');
                Quoted(value);
            }
        }

        void Quoted(string value)
        {
            code.Append('"').Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\" ");
        }
    }

    // ---- Notes and comments ----

    private void WriteNote(Footnote note)
    {
        if (note.CustomMark is { Length: > 0 } mark)
        {
            // A mark of its own is text before the note, as Word writes it.
            Open();
            RunWords(given.Run(current?.Style, null), null);
            Word("super");
            Text(mark);
            Close();
        }
        else
        {
            Open();
            Word("super");
            Word("chftn");
            Close();
        }

        Open();
        Word("footnote");
        if (note.Kind == NoteKind.Endnote)
        {
            Word("ftnalt");
        }

        noteMarkPending = note.CustomMark is not { Length: > 0 };
        WriteInnerStory(note.Story.Blocks);
        noteMarkPending = false;
        Close();
    }

    private void WriteComment(Comment comment)
    {
        Open();
        TextDestination("atnid", comment.Initials ?? "");
        TextDestination("atnauthor", comment.Author);
        Word("chatn");
        Destination("annotation");
        TextDestination("atnref", comment.Id.ToString(CultureInfo.InvariantCulture));
        if (Dttm(comment.Date) is int date)
        {
            TextDestination("atndate", date.ToString(CultureInfo.InvariantCulture));
        }

        if (comment.ParentId is int parent)
        {
            TextDestination("atnparent", parent.ToString(CultureInfo.InvariantCulture));
        }

        WriteInnerStory(comment.Story.Blocks);
        Close();
        Close();
    }

    /// <summary>
    /// Writes a story inside a paragraph (a note, a comment, a text box): the paragraph and fields around it wait until it is
    /// done, and its own fields close inside it.
    /// </summary>
    private void WriteInnerStory(NodeCollection<Block> blocks)
    {
        Paragraph? outer = current;
        int outerLevel = currentLevel;
        int outerFloor = fieldFloor;
        fieldFloor = openFields.Count;
        try
        {
            WriteStory(blocks, null);
        }
        finally
        {
            fieldFloor = outerFloor;
            current = outer;
            currentLevel = outerLevel;
        }
    }

    // ---- Pictures ----

    private void WritePicture(Picture picture)
    {
        if (picture.Floating is { } floating)
        {
            ShapeGroup(floating, picture.Width, picture.Height, 75, () =>
            {
                Property("pib", () => PictGroup(picture.Image, picture.Width, picture.Height, picture.Crop, null, null));
                TextProperty("wzTitle", picture.Title);
                TextProperty("wzDescription", picture.Description);
            });
            return;
        }

        PictGroup(picture.Image, picture.Width, picture.Height, picture.Crop, picture.Title, picture.Description);
    }

    /// <summary>
    /// A <c>\pict</c> group: the picture shown at <paramref name="width"/> by <paramref name="height"/> points after its crop.
    /// PNG, JPEG and EMF are written as they are, WMF without its placeable header; other images are converted to PNG.
    /// </summary>
    private void PictGroup(ImageData image, double width, double height, (double Left, double Top, double Right, double Bottom) crop, string? title, string? description)
    {
        if (Blip(image, width, height) is not { } blip)
        {
            warnings.Add(ConversionWarningKind.PictureDropped, "A picture Egap could not convert for RTF was left out.");
            return;
        }

        Open();
        Word("pict");
        if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(description))
        {
            Destination("picprop");
            TextProperty("wzTitle", title);
            TextProperty("wzDescription", description);
            Close();
        }

        double left = Math.Clamp(crop.Left, 0, 0.99);
        double right = Math.Clamp(crop.Right, 0, 0.99 - left);
        double top = Math.Clamp(crop.Top, 0, 0.99);
        double bottom = Math.Clamp(crop.Bottom, 0, 0.99 - top);

        // The goal size is the whole picture's; the crops come off it, so what shows is the width asked for.
        long goalWidth = Math.Max(1, Twips(width / Math.Max(0.01, 1 - left - right)));
        long goalHeight = Math.Max(1, Twips(height / Math.Max(0.01, 1 - top - bottom)));
        Word("picscalex", 100);
        Word("picscaley", 100);
        Word("piccropl", Clamp(left * goalWidth));
        Word("piccropr", Clamp(right * goalWidth));
        Word("piccropt", Clamp(top * goalHeight));
        Word("piccropb", Clamp(bottom * goalHeight));
        (long picw, long pich) = blip.Pixels is { } pixels ? pixels : (Clamp(goalWidth * 2540.0 / 1440), Clamp(goalHeight * 2540.0 / 1440));
        Word("picw", picw);
        Word("pich", pich);
        Word("picwgoal", goalWidth);
        Word("pichgoal", goalHeight);
        if (blip.Word == "wmetafile")
        {
            Word("wmetafile", 8);
        }
        else
        {
            Word(blip.Word);
        }

        output.AppendLine();
        ReadOnlySpan<byte> bytes = blip.Bytes.Span;
        while (bytes.Length > 0)
        {
            int count = Math.Min(64, bytes.Length);
            output.Append(Convert.ToHexStringLower(bytes[..count])).AppendLine();
            bytes = bytes[count..];
        }

        Close();
    }

    /// <summary>The bytes RTF stores for a picture, the blip word that says their format, and the size in pixels of a raster image.</summary>
    private (ReadOnlyMemory<byte> Bytes, string Word, (long, long)? Pixels)? Blip(ImageData image, double width, double height)
    {
        ReadOnlyMemory<byte> bytes = image.Bytes;
        if (bytes.Length == 0)
        {
            return null;
        }

        switch (image.ContentType)
        {
            case "image/png":
                return (bytes, "pngblip", Pixels(bytes));
            case "image/jpeg":
                return (bytes, "jpegblip", Pixels(bytes));
            case "image/x-emf" or "image/emf":
                return (bytes, "emfblip", null);
            case "image/x-wmf" or "image/wmf":
                // RTF holds the metafile without its placeable header.
                return bytes.Length > 22 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span) == 0x9AC6CDD7
                    ? (bytes[22..], "wmetafile", null)
                    : (bytes, "wmetafile", null);
            case "image/svg+xml":
                warnings.Add(ConversionWarningKind.PictureDropped, "RTF has no SVG pictures; each was written as a PNG.");
                return Rendered(bytes, width, height);
        }

        try
        {
            using Raster raster = Raster.Load(bytes);
            using MemoryStream png = new();
            raster.Save(png, PngFormat.Info);
            return (png.ToArray(), "pngblip", (raster.Width, raster.Height));
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>An SVG picture drawn as a PNG at twice its size in pixels at 96 an inch.</summary>
    private static (ReadOnlyMemory<byte> Bytes, string Word, (long, long)? Pixels)? Rendered(ReadOnlyMemory<byte> svg, double width, double height)
    {
        if (Layout.PictureDrawers.For("image/svg+xml") is not { } drawer)
        {
            return null;
        }

        int pixelsWide = (int)Math.Clamp(Math.Ceiling(width / ImageSize.PointsPerPixel * 2), 1, 4096);
        int pixelsHigh = (int)Math.Clamp(Math.Ceiling(height / ImageSize.PointsPerPixel * 2), 1, 4096);
        try
        {
            using Raster<Egami.Pixels.Rgba8> raster = Raster.Create(pixelsWide, pixelsHigh, new Egami.Pixels.Rgba8(0, 0, 0, 0));
            using (Egami.Drawing.RasterCanvas<Egami.Pixels.Rgba8> canvas = new(raster))
            {
                drawer.Draw(svg, canvas, new Egami.Drawing.RectF(0, 0, pixelsWide, pixelsHigh));
            }

            using MemoryStream png = new();
            raster.Save(png, PngFormat.Info);
            return (png.ToArray(), "pngblip", (pixelsWide, pixelsHigh));
        }
        catch (Exception e) when (Layout.PictureDrawers.IsDrawFailure(e))
        {
            return null;
        }
    }

    private static (long, long)? Pixels(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            RasterInfo info = Raster.Probe(bytes);
            return (info.Width, info.Height);
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- Shapes ----

    private void WriteShape(Shape shape)
    {
        if (shape.Floating is null)
        {
            warnings.Add(ConversionWarningKind.LayoutApproximated, "RTF shapes float; each shape in the text was anchored to its line.");
        }

        FloatingPlacement placement = shape.Floating ?? new FloatingPlacement
        {
            HorizontalAnchor = HorizontalAnchor.Character,
            VerticalAnchor = VerticalAnchor.Line,
            Wrap = WrapMode.InFrontOfText,
        };
        if (shape.Kind == ShapeKind.Other)
        {
            warnings.Add(ConversionWarningKind.DrawingDropped, "A shape RTF has no simple type for was written as a rectangle.");
        }

        int type = shape.Kind switch
        {
            ShapeKind.RoundedRectangle => 2,
            ShapeKind.Ellipse => 3,
            ShapeKind.Triangle => 5,
            ShapeKind.RightArrow => 13,
            ShapeKind.Line => 20,
            _ => shape.TextBox is not null ? 202 : 1,
        };
        Marked(shape, () => ShapeGroup(placement, shape.Width, shape.Height, type, () =>
        {
            Property("fFilled", shape.Fill is null ? 0 : 1);
            if (shape.Fill is { } fill)
            {
                Property("fillColor", Bgr(fill));
            }

            Property("fLine", shape.Outline is null ? 0 : 1);
            if (shape.Outline is { } outline)
            {
                Property("lineWidth", Emu(outline.Width));
                Property("lineColor", Bgr(outline.Color));
            }

            if (shape.Rotation != 0 && double.IsFinite(shape.Rotation))
            {
                Property("rotation", Clamp(shape.Rotation % 360 * 65536));
            }

            TextProperty("wzTitle", shape.Title);
            TextProperty("wzDescription", shape.Description);
            if (shape.TextBox is { } box)
            {
                (double left, double top, double right, double bottom) = shape.TextInsets ?? (7.2, 3.6, 7.2, 3.6);
                Property("dxTextLeft", Emu(left));
                Property("dyTextTop", Emu(top));
                Property("dxTextRight", Emu(right));
                Property("dyTextBottom", Emu(bottom));
                Property("fFitShapeToText", shape.FitToText ? 1 : 0);
                Open();
                Word("shptxt");
                WriteInnerStory(box.Blocks);
                Close();
            }
        }));
    }

    /// <summary>A <c>\shp</c> group: the bounds, anchoring and wrapping of <paramref name="placement"/>, then the properties <paramref name="properties"/> writes.</summary>
    private void ShapeGroup(FloatingPlacement placement, double width, double height, int type, Action properties)
    {
        long left = Twips(placement.HorizontalOffset);
        long top = Twips(placement.VerticalOffset);
        Open();
        Word("shp");
        Destination("shpinst");
        Word("shpleft", left);
        Word("shptop", top);
        Word("shpright", left + Twips(width));
        Word("shpbottom", top + Twips(height));
        Word(placement.HorizontalAnchor switch
        {
            HorizontalAnchor.Page => "shpbxpage",
            HorizontalAnchor.Margin or HorizontalAnchor.LeftMargin or HorizontalAnchor.RightMargin or HorizontalAnchor.InsideMargin or HorizontalAnchor.OutsideMargin => "shpbxmargin",
            _ => "shpbxcolumn",
        });
        Word("shpbxignore");
        Word(placement.VerticalAnchor switch
        {
            VerticalAnchor.Page => "shpbypage",
            VerticalAnchor.Margin or VerticalAnchor.TopMargin or VerticalAnchor.BottomMargin or VerticalAnchor.InsideMargin or VerticalAnchor.OutsideMargin => "shpbymargin",
            _ => "shpbypara",
        });
        Word("shpbyignore");
        Word("shpwr", placement.Wrap switch
        {
            WrapMode.TopAndBottom => 1,
            WrapMode.Square => 2,
            WrapMode.Tight => 4,
            WrapMode.Through => 5,
            _ => 3,
        });
        Word("shpwrk", placement.WrapSide switch { WrapSide.Left => 1, WrapSide.Right => 2, WrapSide.Largest => 3, _ => 0 });
        Word("shpfblwtxt", placement.Wrap == WrapMode.BehindText ? 1 : 0);
        Word("shpz", Math.Clamp(placement.ZOrder, 0, int.MaxValue));
        Property("shapeType", type);
        Property("posrelh", placement.HorizontalAnchor switch
        {
            HorizontalAnchor.Margin => 0,
            HorizontalAnchor.Page => 1,
            HorizontalAnchor.Character => 3,
            HorizontalAnchor.LeftMargin => 4,
            HorizontalAnchor.RightMargin => 5,
            HorizontalAnchor.InsideMargin => 6,
            HorizontalAnchor.OutsideMargin => 7,
            _ => 2,
        });
        Property("posrelv", placement.VerticalAnchor switch
        {
            VerticalAnchor.Margin => 0,
            VerticalAnchor.Page => 1,
            VerticalAnchor.Line => 3,
            VerticalAnchor.TopMargin => 4,
            VerticalAnchor.BottomMargin => 5,
            VerticalAnchor.InsideMargin => 6,
            VerticalAnchor.OutsideMargin => 7,
            _ => 2,
        });
        if (Alignment(placement.HorizontalAlignment) is int posh)
        {
            Property("posh", posh);
        }

        if (Alignment(placement.VerticalAlignment) is int posv)
        {
            Property("posv", posv);
        }

        Property("fBehindDocument", placement.Wrap == WrapMode.BehindText ? 1 : 0);
        Property("dxWrapDistLeft", Emu(placement.DistanceLeft));
        Property("dxWrapDistRight", Emu(placement.DistanceRight));
        Property("dyWrapDistTop", Emu(placement.DistanceTop));
        Property("dyWrapDistBottom", Emu(placement.DistanceBottom));
        Property("fAllowOverlap", placement.AllowOverlap ? 1 : 0);
        Property("fLayoutInCell", placement.LayoutInCell ? 1 : 0);
        properties();
        Close();
        Close();
        output.AppendLine();

        static int? Alignment(FloatingAlignment alignment) => alignment switch
        {
            FloatingAlignment.Start => 1,
            FloatingAlignment.Center => 2,
            FloatingAlignment.End => 3,
            FloatingAlignment.Inside => 4,
            FloatingAlignment.Outside => 5,
            _ => null,
        };
    }

    /// <summary>A shape property: <c>{\sp{\sn name}{\sv value}}</c>.</summary>
    private void Property(string name, long value)
    {
        Property(name, () => Plain(value.ToString(CultureInfo.InvariantCulture)));
    }

    private void TextProperty(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Property(name, () => Text(value));
        }
    }

    private void Property(string name, Action value)
    {
        Open();
        Word("sp");
        Open();
        Word("sn");
        Space();
        Plain(name);
        Close();
        Open();
        Word("sv");
        Space();
        value();
        Close();
        Close();
    }

    /// <summary>A colour as a shape property wants it: blue, green and red from the high byte down.</summary>
    private long Bgr(DocumentColor color)
    {
        Egami.Pixels.Rgba8 rgb = color.Resolve(document.Theme, new Egami.Pixels.Rgba8(0, 0, 0, 255));
        return rgb.R | (rgb.G << 8) | (rgb.B << 16);
    }

    /// <summary>Points as EMUs, 12,700 to the point.</summary>
    private static long Emu(double points) => Clamp(points * 12700);

    // ---- Islands ----

    private void WriteIsland(Island island)
    {
        if (island.Format == RtfExtension.Format)
        {
            Raw(island.Markup);
            return;
        }

        if (island.Fallback is { } fallback)
        {
            warnings.Add(ConversionWarningKind.DrawingDropped, "Content kept from another format was written as its picture.");
            Picture picture = new(document, fallback, island.Width, island.Height) { Floating = island.Floating };
            WritePicture(picture);
            return;
        }

        warnings.Add(ConversionWarningKind.DrawingDropped, "Content kept from another format was written as its text.");
        if (island.Text.Length > 0)
        {
            PlainRun(island.Text);
        }
    }
}
