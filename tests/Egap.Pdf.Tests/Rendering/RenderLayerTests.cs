using System.Text;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Optional content (Phase 4 task 16): configurations, usage by intent, membership dictionaries and overrides.</summary>
public sealed class RenderLayerTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);
    private static readonly Rgba8 Black = new(0, 0, 0, 255);

    [Fact]
    public void TheDefaultConfigurationHidesItsOffGroups()
    {
        using PdfDocument pdf = Document(out _);
        using Raster<Rgba8> raster = pdf.Pages[0].Render();

        Assert.Equal(Black, raster.GetPixel(5, 35));
        Assert.Equal(White, raster.GetPixel(15, 35));
    }

    [Fact]
    public void UsageRulesFollowTheIntent()
    {
        using PdfDocument pdf = Document(out _);
        using Raster<Rgba8> display = pdf.Pages[0].Render();
        using Raster<Rgba8> print = pdf.Pages[0].Render(new PdfRenderOptions { Intent = PdfRenderIntent.Print });

        // The third group shows on screen and prints off.
        Assert.Equal(Black, display.GetPixel(25, 35));
        Assert.Equal(White, print.GetPixel(25, 35));
    }

    [Fact]
    public void MembershipDictionariesAndExpressionsCombineGroups()
    {
        using PdfDocument pdf = Document(out _);
        using Raster<Rgba8> raster = pdf.Pages[0].Render();

        // AllOn of the first two (one off) hides; Not of the second (off) shows.
        Assert.Equal(White, raster.GetPixel(5, 25));
        Assert.Equal(Black, raster.GetPixel(15, 25));
    }

    [Fact]
    public void CallersOverrideGroupsAndHiddenXObjectsAreSkipped()
    {
        using PdfDocument pdf = Document(out PdfDictionary[] groups);
        PdfLayer first = pdf.Layers.First(l => l.Dictionary == groups[0]);
        PdfLayer second = pdf.Layers.First(l => l.Dictionary == groups[1]);
        using Raster<Rgba8> raster = pdf.Pages[0].Render(new PdfRenderOptions { Layers = new Dictionary<PdfLayer, bool> { [first] = false, [second] = true } });

        Assert.Equal(White, raster.GetPixel(5, 35));
        Assert.Equal(Black, raster.GetPixel(15, 35));
        Assert.Equal(White, raster.GetPixel(15, 25));

        // The form XObject marked with the first group follows it too.
        Assert.Equal(White, raster.GetPixel(35, 35));
        using Raster<Rgba8> plain = pdf.Pages[0].Render();
        Assert.Equal(Black, plain.GetPixel(35, 35));
    }

    [Fact]
    public void ANamedConfigurationCanBeChosen()
    {
        using PdfDocument pdf = Document(out _);
        using Raster<Rgba8> raster = pdf.Pages[0].Render(new PdfRenderOptions { LayerConfiguration = "Everything" });

        Assert.Equal(Black, raster.GetPixel(15, 35));
    }

    /// <summary>
    /// A 40 by 40 page: groups L1 (on), L2 (off) and L3 (prints off) each mark a square in the top row; a membership
    /// dictionary (AllOn of L1 and L2) and a visibility expression (Not L2) mark squares below them; a form marked L1 sits at
    /// the top right.
    /// </summary>
    private static PdfDocument Document(out PdfDictionary[] groups)
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 40, 40));
        PdfDictionary l1 = new() { ["Type"] = PdfName.Get("OCG"), ["Name"] = PdfString.FromText("L1") };
        PdfDictionary l2 = new() { ["Type"] = PdfName.Get("OCG"), ["Name"] = PdfString.FromText("L2") };
        PdfDictionary l3 = new()
        {
            ["Type"] = PdfName.Get("OCG"),
            ["Name"] = PdfString.FromText("L3"),
            ["Usage"] = new PdfDictionary { ["Print"] = new PdfDictionary { ["PrintState"] = PdfName.Get("OFF") } },
        };
        PdfReference r1 = pdf.Objects.Add(l1), r2 = pdf.Objects.Add(l2), r3 = pdf.Objects.Add(l3);
        groups = [l1, l2, l3];
        pdf.Catalog["OCProperties"] = new PdfDictionary
        {
            ["OCGs"] = new PdfArray { r1, r2, r3 },
            ["D"] = new PdfDictionary
            {
                ["OFF"] = new PdfArray { r2 },
                ["AS"] = new PdfArray { new PdfDictionary { ["Event"] = PdfName.Get("Print"), ["Category"] = new PdfArray { PdfName.Get("Print") }, ["OCGs"] = new PdfArray { r3 } } },
            },
            ["Configs"] = new PdfArray { new PdfDictionary { ["Name"] = PdfString.FromText("Everything"), ["BaseState"] = PdfName.Get("ON") } },
        };

        PdfStream form = new(new PdfDictionary
        {
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(30), new PdfInteger(0), new PdfInteger(40), new PdfInteger(10) },
            ["OC"] = r1,
        });
        form.SetDecodedBytes("0 g 30 0 10 10 re f"u8.ToArray(), compress: false);
        page.Resources = new PdfDictionary
        {
            ["Properties"] = new PdfDictionary
            {
                ["P1"] = r1,
                ["P2"] = r2,
                ["P3"] = r3,
                ["M1"] = new PdfDictionary { ["Type"] = PdfName.Get("OCMD"), ["OCGs"] = new PdfArray { r1, r2 }, ["P"] = PdfName.Get("AllOn") },
                ["M2"] = new PdfDictionary { ["Type"] = PdfName.Get("OCMD"), ["VE"] = new PdfArray { PdfName.Get("Not"), r2 } },
            },
            ["XObject"] = new PdfDictionary { ["F"] = pdf.Objects.Add(form) },
        };
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(
            "/OC /P1 BDC 0 0 10 10 re f EMC /OC /P2 BDC 10 0 10 10 re f EMC /OC /P3 BDC 20 0 10 10 re f EMC " +
            "/OC /M1 BDC 0 10 10 10 re f EMC /OC /M2 BDC 10 10 10 10 re f EMC /F Do")));
        return pdf;
    }
}
