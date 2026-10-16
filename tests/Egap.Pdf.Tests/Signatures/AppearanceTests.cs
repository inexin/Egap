using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Visible signatures (Phase 5 task 6): the default layout and caller-drawn appearances through PdfCanvas, placed on the
/// page as displayed, drawn into the signed revision before hashing.
/// </summary>
public sealed class AppearanceTests
{
    [Fact]
    public async Task TheDefaultLayoutShowsInItsRectangle()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256", "CN=Ada Lovelace");
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new()
            {
                Signer = PdfSigner.FromKey(key, certificate),
                Reason = "Approved",
                Location = "London",
                Appearance = PdfSignatureAppearance.Default(0, new RectF(400, 700, 180, 60)),
            };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
            PdfSignature signature = Assert.Single(pdf.Signatures);
            SigningTests.AssertVerifies(signature, certificate);

            PdfDictionary widget = signature.Field.Dictionary;
            Assert.Equal([400, 82, 580, 142], widget.Get<PdfArray>("Rect")!.Select(n => Math.Round(((PdfReal)n).Value)));
            PdfStream appearance = Assert.IsType<PdfStream>(widget.Get<PdfDictionary>("AP")!.Get("N")?.Resolve());
            Assert.Equal([0, 0, 180, 60], appearance.Dictionary.Get<PdfArray>("BBox")!.Select(n => n is PdfReal r ? r.Value : ((PdfInteger)n).Value));
            Assert.NotNull(appearance.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font"));

            // The name and details are drawn with the bundled Liberation Sans, as text the content can be searched for.
            string content = Encoding.Latin1.GetString(appearance.GetDecodedBytes());
            Assert.True(content.Contains("Tj", StringComparison.Ordinal) || content.Contains("TJ", StringComparison.Ordinal), content);

            using Raster<Rgba8> raster = pdf.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(DarkPixels(raster, 400, 700, 180, 60) > 100);
            Assert.Equal(0, DarkPixels(raster, 100, 700, 180, 60));
        }
    }

    [Fact]
    public async Task ACallerDrawnAppearanceIsWhatShows()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            SolidPaint red = new(new RgbaF32(1, 0, 0, 1));
            PdfSignOptions options = new()
            {
                Signer = PdfSigner.FromKey(key, certificate),
                Appearance = PdfSignatureAppearance.Draw(0, new RectF(50, 50, 100, 40), c => c.FillRect(new RectF(0, 0, 100, 40), red)),
            };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
            SigningTests.AssertVerifies(Assert.Single(pdf.Signatures), certificate);
            using Raster<Rgba8> raster = pdf.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(100, 70));
            Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(100, 100));
        }
    }

    [Fact]
    public async Task AnImageShowsAtTheLeft()
    {
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(20, 20, new Rgba8(0, 0, 255, 255)))
        {
            PdfSignOptions options = new()
            {
                Signer = PdfSigner.FromKey(key, certificate),
                Appearance = PdfSignatureAppearance.Default(0, new RectF(100, 100, 200, 50), image),
            };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(SignedFiles.Base, "Sig", options));
            using Raster<Rgba8> raster = pdf.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
            Rgba8 left = raster.GetPixel(125, 125);
            Assert.True(left.B > 200 && left.R < 60, left.ToString());
        }
    }

    [Fact]
    public async Task OnATurnedPageTheAppearanceReadsUpright()
    {
        byte[] turned;
        using (PdfDocument original = PdfDocument.Open(SignedFiles.Base))
        using (MemoryStream output = new())
        {
            original.Pages[0].Rotate = 90;
            original.Save(output);
            turned = output.ToArray();
        }

        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            SolidPaint red = new(new RgbaF32(1, 0, 0, 1));

            // Red on the left half of the appearance only: displayed, the left half must still be red.
            PdfSignOptions options = new()
            {
                Signer = PdfSigner.FromKey(key, certificate),
                Appearance = PdfSignatureAppearance.Draw(0, new RectF(100, 100, 200, 50), c => c.FillRect(new RectF(0, 0, 100, 50), red)),
            };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(turned, "Sig", options));
            PdfStream appearance = Assert.IsType<PdfStream>(Assert.Single(pdf.Signatures).Field.Dictionary.Get<PdfDictionary>("AP")!.Get("N")?.Resolve());
            Assert.Equal([0, 1, -1, 0, 0, 0], appearance.Dictionary.Get<PdfArray>("Matrix")!.Select(n => ((PdfInteger)n).Value));
            using Raster<Rgba8> raster = pdf.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(842, raster.Width);
            Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(150, 125));
            Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(250, 125));
        }
    }

    [Fact]
    public async Task AnExistingFieldKeepsItsPlaceAndGetsTheAppearance()
    {
        byte[] withField = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SignedFiles.Signed()).Replace("/V 6 0 R", "/V  null", StringComparison.Ordinal));
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), Appearance = PdfSignatureAppearance.Default(0, new RectF(10, 10, 100, 30)) };
            using PdfDocument pdf = PdfDocument.Open(await SigningTests.Sign(withField, "Sig1", options));
            PdfDictionary widget = Assert.Single(pdf.Signatures).Field.Dictionary;
            Assert.All(widget.Get<PdfArray>("Rect")!, n => Assert.Equal(0, ((PdfInteger)n).Value));
            Assert.NotNull(widget.Get<PdfDictionary>("AP"));
        }
    }

    [Fact]
    public async Task BadPlacesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => PdfSignatureAppearance.Default(0, new RectF(0, 0, 0, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfSignatureAppearance.Default(-1, new RectF(0, 0, 10, 10)));
        Assert.Throws<ArgumentNullException>(() => PdfSignatureAppearance.Draw(0, new RectF(0, 0, 10, 10), null!));
        (X509Certificate2 certificate, AsymmetricAlgorithm key) = CmsTests.SelfSigned("p256");
        using (certificate)
        using (key)
        {
            using PdfDocument pdf = PdfDocument.Open(SignedFiles.Base);
            PdfSignOptions options = new() { Signer = PdfSigner.FromKey(key, certificate), Appearance = PdfSignatureAppearance.Default(3, new RectF(0, 0, 10, 10)) };
            await Assert.ThrowsAsync<ArgumentException>(() => pdf.SignAsync("Sig", options, new MemoryStream(), TestContext.Current.CancellationToken));
        }
    }

    private static int DarkPixels(Raster<Rgba8> raster, int x, int y, int width, int height)
    {
        int dark = 0;
        for (int j = y; j < y + height; j++)
        {
            for (int i = x; i < x + width; i++)
            {
                Rgba8 pixel = raster.GetPixel(i, j);
                if (pixel.R < 128 && pixel.G < 128 && pixel.B < 128)
                {
                    dark++;
                }
            }
        }

        return dark;
    }
}
