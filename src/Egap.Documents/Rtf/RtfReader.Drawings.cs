using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Rtf;

/// <summary>
/// Drawings: pictures (<c>\pict</c>), shapes (<c>\shp</c>, with text boxes and floating pictures), OLE objects and math. A
/// shape the model has no kind for, and an OLE object, become islands that keep their RTF and draw their result picture.
/// </summary>
internal sealed partial class RtfReader
{
    private bool DrawingDestination(string word, bool ignorable)
    {
        switch (word)
        {
            case "pict":
                if (ReadPicture() is { } picture)
                {
                    AddInline(picture);
                }

                return true;
            case "shppict":
                ReadGroup(depth);
                return true;
            case "shp":
                ReadShape(isGroup: false);
                return true;
            case "shpgrp":
                ReadShape(isGroup: true);
                return true;
            case "object":
                ReadObject();
                return true;
            case "mmath" or "mmathPara" or "moMathPara" or "moMath":
                ReadMath();
                return true;
            default:
                _ = ignorable;
                return false;
        }
    }

    // ---- Pictures ----

    /// <summary>Reads a <c>\pict</c> group (its word read) into a picture, or null with a warning when it can't be shown.</summary>
    private Picture? ReadPicture()
    {
        int groupDepth = depth;
        string? type = null;
        int picw = 0;
        int pich = 0;
        int goalw = 0;
        int goalh = 0;
        int scalex = 100;
        int scaley = 100;
        int cropl = 0;
        int cropr = 0;
        int cropt = 0;
        int cropb = 0;
        string? title = null;
        string? description = null;
        ArrayBufferWriter<byte> data = new();
        int high = -1;
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            if (kind == RtfTokenKind.End)
            {
                break;
            }

            if (kind == RtfTokenKind.GroupStart)
            {
                Push(tokens.Position - 1);
                RtfTokenKind first = tokens.Next();
                if (first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*')
                {
                    first = tokens.Next();
                }

                if (first == RtfTokenKind.ControlWord && tokens.Word == "picprop")
                {
                    Dictionary<string, string> properties = ReadShapeProperties(null);
                    description = properties.GetValueOrDefault("wzDescription") is { Length: > 0 } text ? text : description;
                    title = properties.GetValueOrDefault("wzTitle") is { Length: > 0 } caption ? caption : title;
                }
                else
                {
                    SkipRest(first);
                }

                continue;
            }

            if (kind == RtfTokenKind.GroupEnd)
            {
                Pop();
                if (depth < groupDepth)
                {
                    break;
                }

                continue;
            }

            if (kind == RtfTokenKind.Text)
            {
                if (data.WrittenCount + (tokens.Text.Length / 2) > options.Limits.MaxPartSize)
                {
                    throw new DocumentLimitException($"An RTF picture is larger than the limit of {options.Limits.MaxPartSize} bytes.");
                }

                Span<byte> target = data.GetSpan((tokens.Text.Length / 2) + 1);
                int written = 0;
                foreach (byte b in tokens.Text)
                {
                    int value = HexValue(b);
                    if (value < 0)
                    {
                        continue;
                    }

                    if (high < 0)
                    {
                        high = value;
                    }
                    else
                    {
                        target[written++] = (byte)((high << 4) | value);
                        high = -1;
                    }
                }

                data.Advance(written);
                continue;
            }

            if (kind != RtfTokenKind.ControlWord)
            {
                continue;
            }

            int p = tokens.Parameter;
            bool has = tokens.HasParameter;
            switch (tokens.Word)
            {
                case "pngblip":
                    type = "png";
                    break;
                case "jpegblip":
                    type = "jpeg";
                    break;
                case "emfblip":
                    type = "emf";
                    break;
                case "wmetafile":
                    type = "wmf";
                    break;
                case "dibitmap":
                    type = "dib";
                    break;
                case "wbitmap" or "macpict" or "pmmetafile":
                    type = tokens.Word;
                    break;
                case "picw" when has:
                    picw = p;
                    break;
                case "pich" when has:
                    pich = p;
                    break;
                case "picwgoal" when has:
                    goalw = p;
                    break;
                case "pichgoal" when has:
                    goalh = p;
                    break;
                case "picscalex" when has:
                    scalex = p;
                    break;
                case "picscaley" when has:
                    scaley = p;
                    break;
                case "piccropl" when has:
                    cropl = p;
                    break;
                case "piccropr" when has:
                    cropr = p;
                    break;
                case "piccropt" when has:
                    cropt = p;
                    break;
                case "piccropb" when has:
                    cropb = p;
                    break;
                case "bin" when has:
                    if (p < 0 || data.WrittenCount + (long)p > options.Limits.MaxPartSize)
                    {
                        throw new DocumentLimitException($"An RTF picture is larger than the limit of {options.Limits.MaxPartSize} bytes.");
                    }

                    data.Write(tokens.ReadBinary(p));
                    break;
            }
        }

        byte[] bytes = data.WrittenSpan.ToArray();
        if (bytes.Length == 0)
        {
            Warn(ConversionWarningKind.PictureDropped, "An RTF picture had no data.");
            return null;
        }

        bool metafile = type is "wmf" or "emf";
        switch (type)
        {
            case "png" or "jpeg" or "emf":
                break;
            case "wmf":
                bytes = WordPictures.WithPlaceableHeader(bytes, picw, pich, goalw, goalh);
                break;
            case "dib":
                bytes = WordPictures.WithBitmapFileHeader(bytes);
                break;
            default:
                Warn(ConversionWarningKind.PictureDropped, $"An RTF picture in a format Egap does not read ({type ?? "none"}) was dropped.");
                return null;
        }

        // The size: the goal size in twips, else the picture's own (pixels, or HIMETRIC for metafiles).
        double width = goalw > 0 ? goalw : metafile ? picw * 1440.0 / 2540 : picw * 15.0;
        double height = goalh > 0 ? goalh : metafile ? pich * 1440.0 / 2540 : pich * 15.0;
        if (width <= 0 || height <= 0)
        {
            width = width <= 0 ? 1440 : width;
            height = height <= 0 ? 1440 : height;
        }

        (double Left, double Top, double Right, double Bottom) crop = (Fraction(cropl, width), Fraction(cropt, height), Fraction(cropr, width), Fraction(cropb, height));
        // In whole twips, RTF's unit, so the size written back is the size read.
        double shown = Math.Round(Math.Max(0, width - cropl - cropr) * Math.Max(0, scalex) / 100.0) / 20.0;
        double shownHeight = Math.Round(Math.Max(0, height - cropt - cropb) * Math.Max(0, scaley) / 100.0) / 20.0;
        Picture picture = new(document, ImageData.FromBytes(bytes), Bounded(shown), Bounded(shownHeight))
        {
            Title = title,
            Description = description,
        };
        if (crop != default)
        {
            picture.Crop = crop;
        }

        return picture;

        static double Fraction(int cropped, double size) => Math.Clamp(cropped / size, 0, 0.99);
    }

    private static double Bounded(double points) => double.IsFinite(points) ? Math.Clamp(points, 0, 31680) : 0;

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary>Finds the first picture in the rest of the current group, reading it, and closes the group.</summary>
    private Picture? FindPicture()
    {
        int groupDepth = depth;
        Picture? found = null;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return found;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return found;
                    }

                    break;
                case RtfTokenKind.ControlWord when tokens.Word == "pict":
                    if (found is null)
                    {
                        found = ReadPicture();
                    }
                    else
                    {
                        SkipDestination();
                    }

                    if (depth < groupDepth)
                    {
                        return found;
                    }

                    break;
                case RtfTokenKind.ControlWord when tokens.Word == "nonshppict" && found is not null:
                    SkipDestination();
                    break;
                case RtfTokenKind.ControlWord when tokens.Word == "bin" && tokens.HasParameter:
                    _ = tokens.ReadBinary(Math.Max(0, tokens.Parameter));
                    break;
            }
        }
    }

    // ---- Shapes ----

    /// <summary>What a shape's instance group says: its bounds, anchoring, wrapping and properties.</summary>
    private sealed class ShapeInfo
    {
        public int Left { get; set; }

        public int Top { get; set; }

        public int Right { get; set; }

        public int Bottom { get; set; }

        public HorizontalAnchor HorizontalAnchor { get; set; } = HorizontalAnchor.Column;

        public VerticalAnchor VerticalAnchor { get; set; } = VerticalAnchor.Paragraph;

        public int Wrap { get; set; } = 3;

        public int WrapSide { get; set; }

        public bool Behind { get; set; }

        public long ZOrder { get; set; }

        public Dictionary<string, string> Properties { get; set; } = [];

        public Picture? Picture { get; set; }

        public Shape? TextBoxShape { get; set; }

        public Picture? Result { get; set; }
    }

    private void ReadShape(bool isGroup)
    {
        FlushText();
        int start = state.GroupStart;
        int groupDepth = depth;
        ShapeInfo info = new();
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            if (kind == RtfTokenKind.End)
            {
                break;
            }

            if (kind == RtfTokenKind.GroupStart)
            {
                Push(tokens.Position - 1);
                RtfTokenKind first = tokens.Next();
                if (first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*')
                {
                    first = tokens.Next();
                }

                if (first == RtfTokenKind.ControlWord && tokens.Word == "shpinst")
                {
                    ReadShapeInstance(info, isGroup);
                }
                else if (first == RtfTokenKind.ControlWord && tokens.Word == "shprslt")
                {
                    info.Result = FindPicture();
                }
                else
                {
                    SkipRest(first);
                }

                continue;
            }

            if (kind == RtfTokenKind.GroupEnd)
            {
                Pop();
                if (depth < groupDepth)
                {
                    break;
                }
            }
        }

        string markup = start >= 0 ? Markup(start, tokens.Position) : "";
        double width = Bounded(Math.Abs((long)info.Right - info.Left) / 20.0);
        double height = Bounded(Math.Abs((long)info.Bottom - info.Top) / 20.0);
        FloatingPlacement placement = WordShapes.Placement(new WordShapeAnchor(info.Left, info.Top, info.Right, info.Bottom, info.HorizontalAnchor, info.VerticalAnchor, info.Wrap, info.WrapSide, info.Behind, info.ZOrder), info.Properties);
        int shapeType = int.TryParse(info.Properties.GetValueOrDefault("shapeType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) ? t : 0;
        string? name = info.Properties.GetValueOrDefault("wzName");
        string? description = info.Properties.GetValueOrDefault("wzDescription");
        if (!isGroup && info.Picture is { } picture && info.TextBoxShape is null && shapeType is 75 or 0 or 1)
        {
            picture.Width = width > 0 ? width : picture.Width;
            picture.Height = height > 0 ? height : picture.Height;
            picture.Floating = placement;
            picture.Description ??= NullIfEmpty(description);
            picture.Title ??= NullIfEmpty(info.Properties.GetValueOrDefault("wzTitle"));
            AddInline(picture);
            return;
        }

        ShapeKind? simple = WordShapes.Kind(shapeType);
        if (!isGroup && (info.TextBoxShape is not null || simple is not null))
        {
            Shape shape = info.TextBoxShape ?? new Shape(document, simple!.Value, width, height);
            shape.Kind = simple ?? ShapeKind.Rectangle;
            shape.Width = width;
            shape.Height = height;
            shape.Floating = placement;
            shape.Description = NullIfEmpty(description);
            shape.Title = NullIfEmpty(info.Properties.GetValueOrDefault("wzTitle"));
            WordShapes.ApplyLook(shape, info.Properties);
            AddInline(shape);
            return;
        }

        Warn(ConversionWarningKind.DrawingDropped, isGroup
            ? "An RTF shape group was kept as an island that draws its picture fallback."
            : "An RTF drawing shape was kept as an island that draws its picture fallback.");
        Island island = new(document, isGroup ? IslandKind.ShapeGroup : IslandKind.Other, RtfExtension.Format, markup)
        {
            Fallback = info.Result?.Image,
            Width = width > 0 ? width : info.Result?.Width ?? 0,
            Height = height > 0 ? height : info.Result?.Height ?? 0,
            Floating = placement,
            Text = name ?? "",
        };
        AddInline(island);

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }

    private void ReadShapeInstance(ShapeInfo info, bool isGroup)
    {
        int groupDepth = depth;
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            switch (kind)
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    {
                        Push(tokens.Position - 1);
                        RtfTokenKind first = tokens.Next();
                        if (first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*')
                        {
                            first = tokens.Next();
                        }

                        if (first == RtfTokenKind.ControlWord && tokens.Word == "sp")
                        {
                            ReadShapeProperty(info.Properties, info);
                        }
                        else if (first == RtfTokenKind.ControlWord && tokens.Word == "shptxt" && !isGroup && info.TextBoxShape is null && noteDepth == 0 && commentDepth == 0)
                        {
                            Shape shape = new(document, ShapeKind.Rectangle, 0, 0);
                            TextBoxStory box = shape.AddTextBox();
                            info.TextBoxShape = shape;
                            noteDepth++;
                            try
                            {
                                ReadStory(box.Blocks);
                            }
                            finally
                            {
                                noteDepth--;
                            }
                        }
                        else
                        {
                            SkipRest(first);
                        }

                        break;
                    }

                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    int p = tokens.Parameter;
                    switch (tokens.Word)
                    {
                        case "shpleft":
                            info.Left = p;
                            break;
                        case "shptop":
                            info.Top = p;
                            break;
                        case "shpright":
                            info.Right = p;
                            break;
                        case "shpbottom":
                            info.Bottom = p;
                            break;
                        case "shpbxpage":
                            info.HorizontalAnchor = HorizontalAnchor.Page;
                            break;
                        case "shpbxmargin":
                            info.HorizontalAnchor = HorizontalAnchor.Margin;
                            break;
                        case "shpbxcolumn":
                            info.HorizontalAnchor = HorizontalAnchor.Column;
                            break;
                        case "shpbypage":
                            info.VerticalAnchor = VerticalAnchor.Page;
                            break;
                        case "shpbymargin":
                            info.VerticalAnchor = VerticalAnchor.Margin;
                            break;
                        case "shpbypara":
                            info.VerticalAnchor = VerticalAnchor.Paragraph;
                            break;
                        case "shpwr":
                            info.Wrap = p;
                            break;
                        case "shpwrk":
                            info.WrapSide = p;
                            break;
                        case "shpfblwtxt":
                            info.Behind = p != 0;
                            break;
                        case "shpz":
                            info.ZOrder = p;
                            break;
                        case "bin" when tokens.HasParameter:
                            _ = tokens.ReadBinary(Math.Max(0, p));
                            break;
                    }

                    break;
            }
        }
    }

    /// <summary>Reads the <c>{\sp{\sn name}{\sv value}}</c> pairs of the rest of the current group.</summary>
    private Dictionary<string, string> ReadShapeProperties(ShapeInfo? info)
    {
        Dictionary<string, string> properties = [];
        int groupDepth = depth;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return properties;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind first = tokens.Next();
                    if (first == RtfTokenKind.ControlWord && tokens.Word == "sp")
                    {
                        ReadShapeProperty(properties, info);
                    }
                    else
                    {
                        SkipRest(first);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return properties;
                    }

                    break;
            }
        }
    }

    /// <summary>Reads one <c>\sp</c> group; a picture value (<c>pib</c>) becomes the shape's picture.</summary>
    private void ReadShapeProperty(Dictionary<string, string> properties, ShapeInfo? info)
    {
        int groupDepth = depth;
        string? name = null;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind first = tokens.Next();
                    if (first == RtfTokenKind.ControlWord && tokens.Word == "sn")
                    {
                        name = ReadText();
                    }
                    else if (first == RtfTokenKind.ControlWord && tokens.Word == "sv" && name is not null)
                    {
                        if (name == "pib" && info is { Picture: null })
                        {
                            info.Picture = FindPicture();
                        }
                        else if (name is "pib" or "fillBlip" or "pibFlags" || properties.Count >= 1000)
                        {
                            SkipDestination();
                        }
                        else
                        {
                            properties[name] = ReadText();
                        }
                    }
                    else
                    {
                        SkipRest(first);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    // ---- OLE objects and math ----

    private void ReadObject()
    {
        FlushText();
        int start = state.GroupStart;
        int groupDepth = depth;
        Picture? result = null;
        string? objectClass = null;
        int width = 0;
        int height = 0;
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            if (kind == RtfTokenKind.End)
            {
                break;
            }

            if (kind == RtfTokenKind.GroupStart)
            {
                Push(tokens.Position - 1);
                RtfTokenKind first = tokens.Next();
                if (first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*')
                {
                    first = tokens.Next();
                }

                if (first == RtfTokenKind.ControlWord && tokens.Word == "result")
                {
                    result ??= FindPicture();
                }
                else if (first == RtfTokenKind.ControlWord && tokens.Word == "objclass")
                {
                    objectClass = ReadText();
                }
                else
                {
                    SkipRest(first);
                }

                continue;
            }

            if (kind == RtfTokenKind.GroupEnd)
            {
                Pop();
                if (depth < groupDepth)
                {
                    break;
                }

                continue;
            }

            if (kind == RtfTokenKind.ControlWord && tokens.HasParameter)
            {
                if (tokens.Word == "objw")
                {
                    width = tokens.Parameter;
                }
                else if (tokens.Word == "objh")
                {
                    height = tokens.Parameter;
                }
                else if (tokens.Word == "bin")
                {
                    _ = tokens.ReadBinary(Math.Max(0, tokens.Parameter));
                }
            }
        }

        string markup = start >= 0 ? Markup(start, tokens.Position) : "";
        Warn(ConversionWarningKind.DrawingDropped, "An embedded RTF object was kept as an island that draws its result picture.");
        AddInline(new Island(document, IslandKind.OleObject, RtfExtension.Format, markup)
        {
            Fallback = result?.Image,
            Width = result?.Width ?? Bounded(WordProperties.Twips(Math.Max(0, width))),
            Height = result?.Height ?? Bounded(WordProperties.Twips(Math.Max(0, height))),
            Text = objectClass ?? "",
        });
    }

    /// <summary>Reads the header's <c>\mmathPr</c> group into the document's math settings.</summary>
    private void ReadMathProperties()
    {
        int start = state.GroupStart;
        _ = ReadText(trim: false, skipIgnorable: false);
        string markup = start >= 0 ? Markup(start, tokens.Position) : "";
        document.MathSettings = OfficeMath.MathSettings.Read(OfficeMath.RtfMath.MathProperties(markup, FontName));
    }

    private void ReadMath()
    {
        FlushText();
        int start = state.GroupStart;
        string text = ReadText(trim: false, skipIgnorable: false);
        string markup = start >= 0 ? Markup(start, tokens.Position) : "";
        // The equation's text is its UnicodeMath (UTN #28), as Word copies it as plain text.
        string unicodeMath = OfficeMath.MathIslands.Read(RtfExtension.Format, markup) is { } tree ? OfficeMath.UnicodeMath.Write(tree) : text;
        AddInline(new Island(document, IslandKind.Math, RtfExtension.Format, markup) { Text = unicodeMath.Length > 0 ? unicodeMath : text });
    }
}
