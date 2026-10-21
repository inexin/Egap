using System.Text;

using Egami;
using Egami.Formats.Png;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Vector pictures drawn through the picture drawers, and picture sizes read from image headers.</summary>
public class PictureDrawingTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Theory]
    [InlineData("image/svg+xml", true)]
    [InlineData("image/x-emf", true)]
    [InlineData("image/wmf", true)]
    [InlineData("image/png", false)]
    [InlineData("application/octet-stream", false)]
    public void VectorFormatsHaveADrawer(string contentType, bool vector)
    {
        Assert.Equal(vector, PictureDrawers.For(contentType) is not null);
    }

    [Fact]
    public void SvgPicturesDrawAndDamagedOnesLeaveTheirBoxEmpty()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"#ff0000\"/></svg>");
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        Picture red = paragraph.Inlines.Add(new Picture(document, new ImageData(svg, "image/svg+xml"), 60, 60));
        Picture broken = paragraph.Inlines.Add(new Picture(document, new ImageData("<svg"u8.ToArray(), "image/svg+xml"), 60, 60));

        DocumentLayout layout = document.Layout(Isolated);
        Raster<Rgba8> page = layout.Pages[0].Render(72);

        LayoutRect drawn = layout.FindBoxes(red).Single().Box.Bounds;
        Rgba8 inside = page.GetPixel((int)(drawn.X + 30), (int)(drawn.Y + 30));
        Assert.Equal((255, 0, 0), (inside.R, inside.G, inside.B));
        LayoutRect empty = layout.FindBoxes(broken).Single().Box.Bounds;
        Rgba8 blank = page.GetPixel((int)(empty.X + 30), (int)(empty.Y + 30));
        Assert.Equal((255, 255, 255), (blank.R, blank.G, blank.B));
    }

    [Fact]
    public void PictureSizesComeFromTheHeaderAtNinetySixPixelsAnInch()
    {
        using Raster<Rgba8> raster = Raster.Create(40, 20, new Rgba8(0, 0, 0, 255));
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);

        Assert.Equal((30.0, 15.0), ImageSize.Intrinsic(png.ToArray()));
        Assert.Equal((75.0, 75.0), ImageSize.Intrinsic("not an image"u8.ToArray()));
    }
}
