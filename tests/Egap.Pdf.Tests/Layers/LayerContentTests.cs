using System.Text;

namespace Egap.Pdf.Tests.Layers;

/// <summary>Removing a layer's content from pages and form XObjects: Phase 3 task 6.</summary>
public sealed class LayerContentTests
{
    [Fact]
    public void RemovesASelfContainedSequenceWhole()
    {
        (PdfDocument pdf, PdfPage page) = Document("q /OC /L1 BDC q 1 0 0 rg 0 0 10 10 re f Q EMC 0 0 1 1 re f Q");
        using (pdf)
        {
            Assert.Equal(1, pdf.Layers[0].RemoveContent());

            Assert.Equal("q 0 0 1 1 re f Q", Text(page));
        }
    }

    [Fact]
    public void KeepsTheStateChangesOfASequenceThatLeaksThem()
    {
        (PdfDocument pdf, PdfPage page) = Document("BT /F1 12 Tf /OC /L1 BDC 2 0 0 2 0 0 cm 0 g 0 0 10 10 re W f (hidden) Tj /Im0 Do EMC (shown) Tj ET");
        using (pdf)
        {
            Assert.Equal(1, pdf.Layers[0].RemoveContent());

            Assert.Equal("BT /F1 12 Tf 2 0 0 2 0 0 cm 0 g 0 0 10 10 re W n 3 Tr (hidden) Tj 0 Tr (shown) Tj ET", Text(page));
        }
    }

    [Fact]
    public void FollowsPropertiesByNameAndMembershipDictionaries()
    {
        (PdfDocument pdf, PdfPage page) = Document("/OC /MC0 BDC 0 0 1 1 re f EMC /OC /MC1 BDC 0 0 2 2 re f EMC /OC /MC2 BDC 0 0 3 3 re f EMC");
        using (pdf)
        {
            PdfReference l1 = pdf.Layers[0].Reference, l2 = pdf.Layers[1].Reference;
            page.Resources!["Properties"] = new PdfDictionary
            {
                ["MC0"] = l1,

                // Shown when either is on: L2 is on, so hiding L1 hides nothing.
                ["MC1"] = new PdfDictionary { ["Type"] = PdfName.Get("OCMD"), ["OCGs"] = new PdfArray { l1, l2 } },

                // Shown only when both are on: hiding L1 hides it.
                ["MC2"] = new PdfDictionary { ["Type"] = PdfName.Get("OCMD"), ["OCGs"] = new PdfArray { l1, l2 }, ["P"] = PdfName.Get("AllOn") },
            };

            Assert.Equal(2, pdf.Layers[0].RemoveContent());

            Assert.Equal("/OC /MC1 BDC 0 0 2 2 re f EMC", Text(page));
        }
    }

    [Fact]
    public void RemovesFromFormsAndDropsXObjectsMarkedWithTheLayer()
    {
        (PdfDocument pdf, PdfPage page) = Document("/Fm0 Do /Fm1 Do");
        using (pdf)
        {
            PdfReference l1 = pdf.Layers[0].Reference;
            PdfStream form = Form("/OC /MC0 BDC 0 0 1 1 re f EMC 1 1 1 1 re f");
            ((PdfDictionary)form.Dictionary["Resources"]!)["Properties"] = new PdfDictionary
            {
                ["MC0"] = new PdfDictionary { ["Type"] = PdfName.Get("OCMD"), ["OCGs"] = l1 },
            };
            PdfStream marked = Form("0 0 5 5 re f");
            marked.Dictionary["OC"] = l1;
            page.Resources!["XObject"] = new PdfDictionary { ["Fm0"] = pdf.Objects.Add(form), ["Fm1"] = pdf.Objects.Add(marked) };

            Assert.Equal(2, pdf.Layers[0].RemoveContent());

            Assert.Equal("/Fm0 Do", Text(page));
            Assert.Equal("1 1 1 1 re\nf\n", Encoding.ASCII.GetString(form.GetDecodedBytes()));
        }
    }

    [Fact]
    public void ASequenceLeftOpenRunsToTheEnd()
    {
        (PdfDocument pdf, PdfPage page) = Document("0 0 1 1 re f /OC /L1 BDC 0 0 2 2 re f");
        using (pdf)
        {
            Assert.Equal(1, pdf.Layers[0].RemoveContent());

            Assert.Equal("0 0 1 1 re f", Text(page));
        }
    }

    [Fact]
    public void LeavesNothingOfTheLayerInsideAKeptSequence()
    {
        (PdfDocument pdf, PdfPage page) = Document("/OC /L1 BDC 2 0 0 2 0 0 cm /OC /L1 BDC 0 0 1 1 re f EMC /OC /L2 BDC 0 0 1 1 re f EMC");
        using (pdf)
        {
            Assert.Equal(1, pdf.Layers[0].RemoveContent());

            Assert.Equal("2 0 0 2 0 0 cm 0 0 1 1 re n /OC /L2 BDC 0 0 1 1 re n EMC", Text(page));
            Assert.Equal(0, pdf.Layers[0].RemoveContent());
        }
    }

    [Fact]
    public void RemovingTheLayerCanRemoveItsContent()
    {
        (PdfDocument pdf, PdfPage page) = Document("/OC /L1 BDC 0 0 1 1 re f EMC /OC /L2 BDC 0 0 2 2 re f EMC");
        using (pdf)
        {
            Assert.True(pdf.Layers.Remove(pdf.Layers[0], removeContent: true));

            Assert.Single(pdf.Layers);
            Assert.Equal("/OC /L2 BDC 0 0 2 2 re f EMC", Text(page));
            Assert.Equal(1, pdf.Layers[0].RemoveContent());
            Assert.Equal("", Text(page));
        }
    }

    private static PdfStream Form(string content)
    {
        PdfStream form = new(new PdfDictionary
        {
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) },
            ["Resources"] = new PdfDictionary(),
        });
        form.SetDecodedBytes(Encoding.ASCII.GetBytes(content));
        return form;
    }

    /// <summary>A page with the given content and two layers, L1 and L2, both on, named L1 and L2 in the page's properties.</summary>
    private static (PdfDocument Pdf, PdfPage Page) Document(string content)
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfReference l1 = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("OCG"), ["Name"] = PdfString.FromText("One") });
        PdfReference l2 = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("OCG"), ["Name"] = PdfString.FromText("Two") });
        pdf.Catalog["OCProperties"] = new PdfDictionary
        {
            ["OCGs"] = new PdfArray { l1, l2 },
            ["D"] = new PdfDictionary { ["Order"] = new PdfArray { l1, l2 } },
        };
        PdfPage page = pdf.Pages.AddBlank();
        PdfStream stream = new();
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(content));
        page.Dictionary["Contents"] = pdf.Objects.Add(stream);
        page.Resources = new PdfDictionary
        {
            ["Properties"] = new PdfDictionary { ["L1"] = l1, ["L2"] = l2 },
            ["XObject"] = new PdfDictionary { ["Im0"] = pdf.Objects.Add(new PdfStream(new PdfDictionary { ["Subtype"] = PdfName.Get("Image") })) },
        };
        return (pdf, page);
    }

    private static string Text(PdfPage page)
    {
        return string.Join(' ', page.GetContent().Select(o => o.ToString()));
    }
}
