using System.Buffers.Binary;
using System.Globalization;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// Pictures and drawings. An inline picture is a special 0x01 character whose sprmCPicLocation points at a PICF and its Office
/// Art in the Data stream ([MS-DOC] PICFAndOfficeArtData); a floating shape is a special 0x08 character with an FSPA in
/// PlcSpaMom (PlcSpaHdr in the headers) naming a shape of the Office Art drawing; a text box's text is a story of the text box
/// part, which the shape's lTxid names. Office Art property names follow [MS-ODRAW], as RTF's shape properties do, so the
/// look and placement come from the same code as RTF's.
/// </summary>
internal sealed partial class DocReader
{
    /// <summary>An FSPA ([MS-DOC] Spa): the shape id, its rectangle in twips and the flags word (bx, by, wr, wrk, fBelowText).</summary>
    private readonly record struct Spa(int Spid, int Left, int Top, int Right, int Bottom, int Flags, int Order);

    private readonly Dictionary<int, Spa> mainSpas = [];
    private readonly Dictionary<int, Spa> headerSpas = [];
    private readonly List<(int Start, int End)> textBoxes = [];
    private readonly List<(int Start, int End)> headerTextBoxes = [];
    private readonly HashSet<int> textBoxesRead = [];
    private OfficeArtContent? art;

    // Word keeps the pictures of picture bullets in the main text, in a range bookmarked _PictureBullets; they are not content.
    private (int Start, int End) pictureBullets = (-1, -1);

    private long MaxPicture => Math.Min(limits.MaxPartSize, 1L << 28);

    private void ReadDrawings()
    {
        if (fib.IsWord6)
        {
            return;
        }

        ReadSpas(Table(DocTable.PlcSpaMom), mainSpas);
        ReadSpas(Table(DocTable.PlcSpaHdr), headerSpas);
        ReadTextBoxes(Table(DocTable.PlcftxbxTxt), TextBoxBase, fib.CcpTxbx, textBoxes);
        ReadTextBoxes(Table(DocTable.PlcfHdrtxbxTxt), HeaderTextBoxBase, fib.CcpHdrTxbx, headerTextBoxes);
        if (!Table(DocTable.DggInfo).IsEmpty)
        {
            art = OfficeArt.Read(Table(DocTable.DggInfo).ToArray(), word, MaxPicture);
        }
    }

    // PlcSpa: the anchors' CPs, then a 26-byte FSPA each (lid, the rca's four edges, the flags word, cTxbx).
    private void ReadSpas(ReadOnlySpan<byte> plc, Dictionary<int, Spa> spas)
    {
        int count = PlcCount(plc, 26);
        for (int i = 0; i < count && spas.Count < limits.MaxEntries; i++)
        {
            ReadOnlySpan<byte> spa = PlcData(plc, count, 26, i);
            spas.TryAdd(PlcCp(plc, i), new Spa(
                BinaryPrimitives.ReadInt32LittleEndian(spa),
                BinaryPrimitives.ReadInt32LittleEndian(spa[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(spa[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(spa[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(spa[16..]),
                BinaryPrimitives.ReadUInt16LittleEndian(spa[20..]),
                i));
        }
    }

    // PlcftxbxTxt: the stories' CPs from the start of the part, with a 22-byte FTXBXS each; the last is a spare and not a story.
    private void ReadTextBoxes(ReadOnlySpan<byte> plc, int at, int length, List<(int Start, int End)> stories)
    {
        int count = PlcCount(plc, 22);
        int floor = 0;
        for (int i = 0; i < count - 1 && i < limits.MaxEntries; i++)
        {
            int start = Math.Clamp(PlcCp(plc, i), floor, length);
            int end = Math.Clamp(PlcCp(plc, i + 1), start, length);
            floor = end;
            stories.Add((at + start, at + end));
        }
    }

    /// <summary>
    /// The inline picture whose PICF is at <paramref name="location"/>, or an inline shape with no picture (a horizontal line
    /// is an inline rectangle), or null with a warning.
    /// </summary>
    private Inline? InlinePicture(int location)
    {
        // PICF: lcb, cbHeader, the mfpf (mm at +6), ..., dxaGoal and dyaGoal at +28, mx and my (thousandths) at +32.
        byte[] source = data ?? word;
        if (location < 0 || location > source.Length - 0x24)
        {
            return DroppedPicture("A picture of the Word document has no data.");
        }

        ReadOnlySpan<byte> picf = source.AsSpan(location);
        uint lcb = BinaryPrimitives.ReadUInt32LittleEndian(picf);
        int cbHeader = BinaryPrimitives.ReadUInt16LittleEndian(picf[4..]);
        if (cbHeader < 0x24 || lcb < cbHeader || lcb > source.Length - location)
        {
            return DroppedPicture("A picture of the Word document has a damaged header.");
        }

        int mm = BinaryPrimitives.ReadUInt16LittleEndian(picf[6..]);
        double width = Scaled(BinaryPrimitives.ReadInt16LittleEndian(picf[28..]), BinaryPrimitives.ReadUInt16LittleEndian(picf[32..]));
        double height = Scaled(BinaryPrimitives.ReadInt16LittleEndian(picf[30..]), BinaryPrimitives.ReadUInt16LittleEndian(picf[34..]));
        int start = location + cbHeader;
        int end = location + (int)lcb;
        if (mm == 0x66 && start < end)
        {
            // A name (cchPicName, then the name) before the Office Art.
            start = Math.Min(end, start + 1 + source[start]);
        }

        if (mm is not (0x64 or 0x66))
        {
            return DroppedPicture("A picture of the Word document is an old-style metafile picture, which Egap does not read yet.");
        }

        (OfficeArtShape shape, ImageData? image) = OfficeArt.Inline(source, start, end, MaxPicture);

        // A picture with no FBSE of its own names one in the document's blip store.
        if (image is null && shape[0x104] is uint pib && art is not null && pib >= 1 && pib <= art.Blips.Count)
        {
            image = art.Blips[(int)pib - 1];
        }

        if (image is null && shape[0x104] is null && WordShapes.Kind(shape.Type) is { } kind)
        {
            Shape inline = new(document, kind, width, height);
            WordShapes.ApplyLook(inline, Names(shape));
            return inline;
        }

        if (image is null)
        {
            return DroppedPicture("A picture of the Word document is in a format Egap does not read.");
        }

        Picture picture = new(document, image, width, height);
        ApplyPicture(picture, shape);
        return picture;

        static double Scaled(int goal, int scale) => Math.Clamp(goal * (scale > 0 ? scale : 1000) / 1000.0 / 20.0, 0, 31680);
    }

    private Picture? DroppedPicture(string message)
    {
        warnings.Add(ConversionWarningKind.PictureDropped, message);
        return null;
    }

    /// <summary>
    /// The floating pictures and shapes anchored at the special 0x08 character at <paramref name="cp"/>: one, or a group's
    /// members, each placed by its rectangle in the group's space; none, with a warning, when they cannot be read.
    /// </summary>
    private List<Inline> FloatingDrawings(int cp, bool header)
    {
        List<Inline> drawings = [];
        Dictionary<int, Spa> spas = header ? headerSpas : mainSpas;
        if (!spas.TryGetValue(header ? cp - HeaderBase : cp, out Spa spa)
            || art is null
            || !(header ? art.Header : art.Main).TryGetValue(spa.Spid, out OfficeArtShape? shape))
        {
            warnings.Add(ConversionWarningKind.DrawingDropped, "A drawing of the Word document has no shape.");
            return drawings;
        }

        Place(shape, (spa.Left, spa.Top, spa.Right, spa.Bottom), spa, header, Names(shape), drawings, 0);
        return drawings;
    }

    /// <summary>
    /// Places <paramref name="shape"/> at <paramref name="rect"/> (twips, from the FSPA's reference areas); a group places each
    /// member at its child anchor mapped from the group's space onto the rectangle, with the group's placement properties.
    /// </summary>
    private void Place(OfficeArtShape shape, (double Left, double Top, double Right, double Bottom) rect, Spa spa, bool header,
        Dictionary<string, string> placementNames, List<Inline> drawings, int depth)
    {
        if (shape.IsGroup)
        {
            if (depth >= 8 || shape.GroupSpace is not { } space || space.Right == space.Left || space.Bottom == space.Top)
            {
                warnings.Add(ConversionWarningKind.DrawingDropped, "A group of shapes in the Word document is left out.");
                return;
            }

            // The members are placed by offset: an alignment of the group would put each member at the same place.
            Dictionary<string, string> names = new(placementNames);
            names.Remove("posh");
            names.Remove("posv");
            double scaleX = (rect.Right - rect.Left) / (space.Right - space.Left);
            double scaleY = (rect.Bottom - rect.Top) / (space.Bottom - space.Top);
            foreach (OfficeArtShape member in shape.Children)
            {
                if (member.ChildAnchor is { } a && drawings.Count < limits.MaxEntries)
                {
                    Place(member, (rect.Left + ((a.Left - space.Left) * scaleX), rect.Top + ((a.Top - space.Top) * scaleY),
                        rect.Left + ((a.Right - space.Left) * scaleX), rect.Top + ((a.Bottom - space.Top) * scaleY)), spa, header, names, drawings, depth + 1);
                }
            }

            return;
        }

        if (Drawing(shape, rect, spa, header, depth == 0 ? Names(shape) : Merged(placementNames, Names(shape)), drawings.Count) is { } drawing)
        {
            drawings.Add(drawing);
        }

        static Dictionary<string, string> Merged(Dictionary<string, string> group, Dictionary<string, string> own)
        {
            Dictionary<string, string> merged = new(own);
            foreach (string key in (string[])["posh", "posv", "posrelh", "posrelv", "dxWrapDistLeft", "dyWrapDistTop", "dxWrapDistRight", "dyWrapDistBottom", "fBehindDocument", "fAllowOverlap", "fLayoutInCell"])
            {
                if (group.TryGetValue(key, out string? value))
                {
                    merged[key] = value;
                }
                else
                {
                    merged.Remove(key);
                }
            }

            return merged;
        }
    }

    /// <summary>One picture or shape at <paramref name="rect"/>, or null with a warning.</summary>
    private Inline? Drawing(OfficeArtShape shape, (double Left, double Top, double Right, double Bottom) rect, Spa spa, bool header,
        Dictionary<string, string> properties, int member)
    {
        // bx: 0 margin, 1 page, 2 column; by: 0 margin, 1 page, 2 paragraph; wr, wrk, then fBelowText.
        int flags = spa.Flags;
        WordShapeAnchor anchor = new(Twips(rect.Left), Twips(rect.Top), Twips(rect.Right), Twips(rect.Bottom),
            ((flags >> 1) & 3) switch { 0 => HorizontalAnchor.Margin, 1 => HorizontalAnchor.Page, _ => HorizontalAnchor.Column },
            ((flags >> 3) & 3) switch { 0 => VerticalAnchor.Margin, 1 => VerticalAnchor.Page, _ => VerticalAnchor.Paragraph },
            (flags >> 5) & 0xF, (flags >> 9) & 0xF, (flags & 0x4000) != 0, ((long)spa.Order << 16) + member);
        FloatingPlacement placement = WordShapes.Placement(anchor, properties);
        double width = Math.Clamp(Math.Abs(rect.Right - rect.Left) / 20.0, 0, 31680);
        double height = Math.Clamp(Math.Abs(rect.Bottom - rect.Top) / 20.0, 0, 31680);
        uint textId = shape[0x80] ?? 0;
        if (textId == 0 && shape[0x104] is uint pib && pib >= 1 && pib <= art!.Blips.Count)
        {
            if (art.Blips[(int)pib - 1] is not { } image)
            {
                warnings.Add(ConversionWarningKind.PictureDropped, "A floating picture of the Word document is in a format Egap does not read.");
                return null;
            }

            Picture picture = new(document, image, width, height) { Floating = placement };
            ApplyPicture(picture, shape);
            return picture;
        }

        ShapeKind? kind = WordShapes.Kind(shape.Type);
        if (textId == 0 && kind is null)
        {
            warnings.Add(ConversionWarningKind.DrawingDropped, $"A drawing shape of the Word document (type {shape.Type.ToString(CultureInfo.InvariantCulture)}) is left out.");
            return null;
        }

        Shape result = new(document, kind ?? ShapeKind.Rectangle, width, height)
        {
            Floating = placement,
            Description = shape.Text(0x381),
        };
        if (shape[0x4] is uint rotation)
        {
            result.Rotation = (int)rotation / 65536.0;
        }

        List<(int Start, int End)> stories = header ? headerTextBoxes : textBoxes;
        int index = (int)(textId >> 16) - 1;
        if (textId != 0 && index >= 0 && index < stories.Count && textBoxesRead.Add((header ? 1 << 30 : 0) | index))
        {
            TextBoxStory box = result.AddTextBox();
            Enter();
            StoryBuilder story = new(this, box.Blocks, StoryKind.TextBox);
            story.Read(stories[index].Start, stories[index].End);
            story.Finish();
            Leave();
        }

        WordShapes.ApplyLook(result, properties);
        return result;

        static int Twips(double value) => (int)Math.Clamp(Math.Round(value), int.MinValue / 2, int.MaxValue / 2);
    }

    /// <summary>A picture's crop and alternative text from its shape's properties (cropFrom* as 16.16 fractions, wzDescription).</summary>
    private static void ApplyPicture(Picture picture, OfficeArtShape shape)
    {
        (double, double, double, double) crop = (Fraction(shape[0x102]), Fraction(shape[0x100]), Fraction(shape[0x103]), Fraction(shape[0x101]));
        if (crop != default)
        {
            picture.Crop = crop;
        }

        picture.Description = shape.Text(0x381);

        static double Fraction(uint? value) => value is uint v ? Math.Clamp((int)v / 65536.0, -1, 0.99) : 0;
    }

    /// <summary>
    /// A shape's properties by their [MS-ODRAW] names, as RTF writes them: the booleans that the shape sets (their use bit on),
    /// and the relative positions counted from 0, as RTF counts them (the binary values count from 1).
    /// </summary>
    private static Dictionary<string, string> Names(OfficeArtShape shape)
    {
        Dictionary<string, string> names = [];
        Number(0x81, "dxTextLeft");
        Number(0x82, "dyTextTop");
        Number(0x83, "dxTextRight");
        Number(0x84, "dyTextBottom");
        Flag(0xBF, 1, "fFitShapeToText");
        Number(0x181, "fillColor");
        Flag(0x1BF, 4, "fFilled");
        Number(0x1C0, "lineColor");
        Number(0x1CB, "lineWidth");
        Flag(0x1FF, 3, "fLine");
        Number(0x384, "dxWrapDistLeft");
        Number(0x385, "dyWrapDistTop");
        Number(0x386, "dxWrapDistRight");
        Number(0x387, "dyWrapDistBottom");
        Number(0x38F, "posh");
        Number(0x391, "posv");
        if (shape[0x390] is uint relh)
        {
            names["posrelh"] = ((int)relh - 1).ToString(CultureInfo.InvariantCulture);
        }

        if (shape[0x392] is uint relv)
        {
            names["posrelv"] = ((int)relv - 1).ToString(CultureInfo.InvariantCulture);
        }

        Flag(0x3BF, 5, "fBehindDocument");
        Flag(0x3BF, 9, "fAllowOverlap");
        Flag(0x3BF, 15, "fLayoutInCell");
        return names;

        void Number(int id, string name)
        {
            if (shape[id] is uint value)
            {
                names[name] = ((int)value).ToString(CultureInfo.InvariantCulture);
            }
        }

        // A boolean property set: the value bit, and its use bit 16 places higher saying it is set.
        void Flag(int id, int bit, string name)
        {
            if (shape[id] is uint value && (value & (1u << (bit + 16))) != 0)
            {
                names[name] = ((value >> bit) & 1).ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
