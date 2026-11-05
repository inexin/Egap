using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Doc;

using static Egap.Documents.Tests.DocBuilder;

namespace Egap.Documents.Tests;

/// <summary>The binary Word reader's pictures, shapes and text boxes, on documents with Office Art built for each case.</summary>
public sealed class DocDrawingTests
{
    private const ushort Special = 0x0855;
    private const ushort PicLocation = 0x6A03;
    private static readonly string PictureMark = ((char)1).ToString();
    private static readonly string DrawingMark = ((char)8).ToString();
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 1, 2, 3, 4];

    private static Document Read(DocBuilder builder, out IReadOnlyList<ConversionWarning> warnings)
    {
        return DocReader.Read(builder.Build(), new DocumentLimits(), out warnings);
    }

    // ---- Office Art records ([MS-ODRAW]): the version and instance word, the type, the length, the body ----

    private static byte[] Record(int version, int instance, int type, params byte[][] body)
    {
        byte[] content = [.. body.SelectMany(b => b)];
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(version | (instance << 4)));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), content.Length);
        return [.. header, .. content];
    }

    private static byte[] Container(int type, params byte[][] children) => Record(0xF, 0, type, children);

    private static byte[] Int(int value) => BitConverter.GetBytes(value);

    /// <summary>A shape container: the FSP (type, spid, flags) and an FOPT of simple properties and UTF-16 strings.</summary>
    private static byte[] ShapeContainer(int type, int spid, int flags, (int Id, int Value)[] properties, (int Id, string Text)[]? strings = null, byte[]? extra = null)
    {
        strings ??= [];
        List<byte> table = [];
        List<byte> complex = [];
        foreach ((int id, int value) in properties)
        {
            table.AddRange(BitConverter.GetBytes((ushort)id));
            table.AddRange(Int(value));
        }

        foreach ((int id, string text) in strings)
        {
            byte[] data = [.. Encoding.Unicode.GetBytes(text), 0, 0];
            table.AddRange(BitConverter.GetBytes((ushort)(id | 0x8000)));
            table.AddRange(Int(data.Length));
            complex.AddRange(data);
        }

        return Container(0xF004,
            Record(2, type, 0xF00A, Int(spid), Int(flags)),
            Record(3, properties.Length + strings.Length, 0xF00B, [.. table, .. complex]),
            extra ?? []);
    }

    /// <summary>A PNG blip (one UID, then the tag byte) in an FBSE.</summary>
    private static byte[] StoredPng()
    {
        byte[] blip = Record(0, 0x6E0, 0xF01E, new byte[16], [0xFF], Png);
        byte[] fbse = new byte[36];
        fbse[0] = 6;
        fbse[1] = 6;
        BinaryPrimitives.WriteInt32LittleEndian(fbse.AsSpan(20), blip.Length);
        BinaryPrimitives.WriteInt32LittleEndian(fbse.AsSpan(24), 1);
        return Record(2, 6, 0xF007, fbse, blip);
    }

    [Fact]
    public void InlinePicturesComeFromTheDataStream()
    {
        // PICF: lcb, cbHeader, mm 0x64; dxaGoal and dyaGoal 1440 by 720 twips, mx 1000 and my 500 (thousandths).
        byte[] art = [.. ShapeContainer(75, 1025, 0xA00, [(0x104, 1), (0x100, 0x4000)], [(0x381, "A logo")]), .. StoredPng()];
        byte[] picf = new byte[0x44];
        BinaryPrimitives.WriteInt32LittleEndian(picf, picf.Length + art.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(4), 0x44);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(6), 0x64);
        BinaryPrimitives.WriteInt16LittleEndian(picf.AsSpan(28), 1440);
        BinaryPrimitives.WriteInt16LittleEndian(picf.AsSpan(30), 720);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(32), 1000);
        BinaryPrimitives.WriteUInt16LittleEndian(picf.AsSpan(34), 500);
        DocBuilder builder = new() { Data = [.. new byte[16], .. picf, .. art] };
        builder.Run("See ").Run(PictureMark, Sprm(Special, 1), Sprm32(PicLocation, 16)).Run(" here").Paragraph();

        Document document = Read(builder, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Empty(warnings);
        Picture picture = document.Descendants<Picture>().Single();
        Assert.Equal("image/png", picture.Image.ContentType);
        Assert.Equal(Png, picture.Image.Bytes.ToArray());
        Assert.Equal(72, picture.Width);
        Assert.Equal(18, picture.Height);
        Assert.Null(picture.Floating);
        Assert.Equal(0.25, picture.Crop.Top);
        Assert.Equal("A logo", picture.Description);
        Assert.Equal("See  here", string.Concat(document.Sections[0].Body.Paragraphs.Single().Runs.Select(r => r.Text)));
    }

    [Fact]
    public void FloatingShapesAndTextBoxesComeFromOfficeArt()
    {
        DocBuilder builder = new();
        builder.Run("A").Run(DrawingMark, Sprm(Special, 1)).Run("B").Run(DrawingMark, Sprm(Special, 1)).Run("C").Run(DrawingMark, Sprm(Special, 1)).Paragraph();
        builder.Story = DocStory.TextBoxes;
        builder.Run("In the box").Paragraph().Paragraph();

        // FSPA: lid, the rectangle in twips, the flags (bx at bit 1, by at 3, wr at 5, wrk at 9, fBelowText 0x4000), cTxbx.
        static byte[] Spa(int spid, int left, int top, int right, int bottom, int flags) =>
            [.. Int(spid), .. Int(left), .. Int(top), .. Int(right), .. Int(bottom), .. BitConverter.GetBytes((ushort)flags), .. Int(0)];
        builder.Table(40, Plc([1, 3, 5, 99],
            Spa(1025, 1440, 720, 4320, 2160, (1 << 1) | (2 << 3) | (2 << 5) | (1 << 9)),
            Spa(1026, 0, 0, 2880, 1440, (2 << 1) | (2 << 3) | (3 << 5) | 0x4000),
            Spa(1027, 0, 0, 2880, 1440, 3 << 5)));

        // The drawing group with its blip store, then the main drawing (dgglbl 0): the patriarch, a text box, a picture, a group.
        byte[] dgg = Container(0xF000, Record(0, 0, 0xF006, new byte[16]), Container(0xF001, StoredPng()));
        byte[] drawing = Container(0xF002, Record(0, 1, 0xF008, Int(4), Int(1027)), Container(0xF003,
            ShapeContainer(0, 1024, 0x5, []),
            ShapeContainer(202, 1025, 0xA00, [(0x80, 0x10000), (0x181, 0x0000FF), (0x1BF, 0x100010), (0x1FF, 0x80000), (0x81, 25400)]),
            ShapeContainer(75, 1026, 0xA00, [(0x104, 1), (0x38F, 2), (0x390, 2), (0x3BF, 0x2000000)], [(0x381, "A floating picture")]),
            Container(0xF003,
                ShapeContainer(0, 1027, 0xA01, [], extra: Record(1, 0, 0xF009, Int(0), Int(0), Int(100), Int(100))),
                ShapeContainer(3, 1028, 0xA02, [], extra: Record(0, 0, 0xF00F, Int(50), Int(0), Int(100), Int(50))))));
        builder.Table(50, [.. dgg, 0, .. drawing]);

        // The text box stories: the box's text, then the spare; each FTXBXS ends with lid and txidUndo.
        static byte[] Ftxbxs(bool reusable, int lid) =>
            [.. Int(1), .. Int(0), .. BitConverter.GetBytes((ushort)(reusable ? 1 : 0)), .. Int(0), .. Int(lid), .. Int(0)];
        builder.Table(56, Plc([0, 11, 12], Ftxbxs(false, 1025), Ftxbxs(true, 0)));

        Document document = Read(builder, out IReadOnlyList<ConversionWarning> warnings);

        Shape box = document.Descendants<Shape>().Single(s => s.TextBox is not null);
        Assert.Equal(ShapeKind.Rectangle, box.Kind);
        Assert.Equal(144, box.Width);
        Assert.Equal(72, box.Height);
        Assert.Equal(DocumentColor.FromRgb(new Egami.Pixels.Rgba8(255, 0, 0, 255)), box.Fill);
        Assert.Null(box.Outline);
        Assert.Equal(2, box.TextInsets!.Value.Left);
        Assert.Equal("In the box", box.TextBox!.GetText().Trim());
        FloatingPlacement placement = box.Floating!;
        Assert.Equal(HorizontalAnchor.Page, placement.HorizontalAnchor);
        Assert.Equal(VerticalAnchor.Paragraph, placement.VerticalAnchor);
        Assert.Equal(72, placement.HorizontalOffset);
        Assert.Equal(36, placement.VerticalOffset);
        Assert.Equal(WrapMode.Square, placement.Wrap);
        Assert.Equal(WrapSide.Left, placement.WrapSide);

        Picture picture = document.Descendants<Picture>().Single();
        Assert.Equal("A floating picture", picture.Description);
        Assert.Equal(144, picture.Width);
        Assert.Equal(HorizontalAnchor.Page, picture.Floating!.HorizontalAnchor);
        Assert.Equal(FloatingAlignment.Center, picture.Floating.HorizontalAlignment);
        Assert.Equal(WrapMode.BehindText, picture.Floating.Wrap);
        Assert.False(picture.Floating.AllowOverlap);

        // A group's member, placed by its child anchor in the group's space mapped onto the group's rectangle.
        Shape member = document.Descendants<Shape>().Single(s => s.Kind == ShapeKind.Ellipse);
        Assert.Equal(72, member.Width);
        Assert.Equal(36, member.Height);
        Assert.Equal(72, member.Floating!.HorizontalOffset);
        Assert.Equal(0, member.Floating.VerticalOffset);
        Assert.Empty(warnings);

        // The text box's text is not read again as main text.
        Assert.Equal("ABC", string.Concat(document.Sections[0].Body.Paragraphs.Single().Runs.Select(r => r.Text)));
    }

    [Fact]
    public void PictureBulletsAreNotContent()
    {
        DocBuilder builder = new() { Data = new byte[0x44] };
        builder.Run("Item").Paragraph().Run(PictureMark, Sprm(Special, 1), Sprm32(PicLocation, 0)).Paragraph();
        builder.Table(21, builder.Sttb(["_PictureBullets"])).Table(22, Plc([5, 99], [0, 0, 0, 0])).Table(23, Plc([6, 99]));

        Document document = Read(builder, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Empty(document.Descendants<Picture>());
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.PictureDropped);
    }

    [Fact]
    public void PicturesWithoutReadableDataAreLeftOutWithAWarning()
    {
        DocBuilder builder = new() { Data = new byte[0x44] };
        builder.Run(PictureMark, Sprm(Special, 1), Sprm32(PicLocation, 0)).Run(DrawingMark, Sprm(Special, 1)).Paragraph();

        Document document = Read(builder, out IReadOnlyList<ConversionWarning> warnings);

        Assert.Empty(document.Descendants<Picture>());
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.PictureDropped);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.DrawingDropped);
    }
}
