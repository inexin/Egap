using System.Text;

namespace Egap.Pdf.Tests.Tagging;

/// <summary>The structure tree model (Phase 5 task 17): read, edit, the parent tree rebuilt, kept on page import.</summary>
public sealed class StructureTreeTests
{
    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    /// <summary>A two-page document, each page a heading and a paragraph tagged with MCIDs 0 and 1, and a link on page 1.</summary>
    private static byte[] Tagged()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStructureElement document = pdf.Structure.Add(PdfStructureType.Document);
        document.Language = "en";
        for (int i = 0; i < 2; i++)
        {
            PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 300, 300));
            page.Dictionary["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") } } };
            page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(
                $"/H1 <</MCID 0>> BDC BT /F1 18 Tf 20 250 Td (Heading {i + 1}) Tj ET EMC /P <</MCID 1>> BDC BT /F1 10 Tf 20 200 Td (Body {i + 1}) Tj ET EMC")));
            PdfStructureElement heading = document.Add(PdfStructureType.H1);
            heading.Title = $"Heading {i + 1}";
            heading.AddMarkedContent(page, 0);
            PdfStructureElement paragraph = document.Add(PdfStructureType.P);
            paragraph.AddMarkedContent(page, 1);
            if (i == 0)
            {
                PdfLinkAnnotation link = new(new PdfRectangle(20, 190, 100, 210), new PdfUriAction("https://example.org"));
                page.Annotations.Add(link);
                document.Add(PdfStructureType.Link).AddAnnotation(page, link);
            }
        }

        return Save(pdf);
    }

    [Fact]
    public void ATreeIsWrittenAndReadBack()
    {
        using PdfDocument pdf = PdfDocument.Open(Tagged());
        Assert.True(pdf.Structure.Exists);
        Assert.True(pdf.Structure.IsMarked);
        PdfStructureElement root = Assert.IsType<PdfStructureElement>(pdf.Structure.Root);
        Assert.Equal(PdfStructureType.Document, root.Type);
        Assert.Equal("en", root.Language);
        Assert.Equal(["H1", "P", "Link", "H1", "P"], root.Elements.Select(e => e.Type.Name));
        PdfStructureElement heading = root.Elements.First();
        Assert.Equal("Heading 1", heading.Title);
        Assert.Same(root, heading.Parent);
        PdfMarkedContentReference marked = Assert.IsType<PdfMarkedContentReference>(Assert.Single(heading.Children));
        Assert.Equal(0, marked.Mcid);
        Assert.Same(pdf.Pages[0].Dictionary, marked.PageDictionary);

        // The parent tree leads from page content back to elements.
        Assert.Same(heading, pdf.Structure.FindMarkedContent(pdf.Pages[0], 0));
        long key = pdf.Pages[1].Dictionary.GetInt("StructParents")!.Value;
        PdfDictionary parentTree = pdf.Structure.Root!.Dictionary.Get<PdfDictionary>("P")!.Get<PdfDictionary>("ParentTree")!;
        Assert.NotNull(parentTree);
        Assert.NotEqual(key, pdf.Pages[0].Dictionary.GetInt("StructParents"));
        PdfAnnotation link = Assert.Single(pdf.Pages[0].Annotations);
        Assert.NotNull(link.Dictionary.GetInt("StructParent"));
        Assert.IsType<PdfObjectReference>(Assert.Single(root.Elements.ElementAt(2).Children));
    }

    [Fact]
    public void RoleMapsResolveToStandardTypes()
    {
        using PdfDocument pdf = PdfDocument.Open(Tagged());
        pdf.Structure.SetRole("Chapter", PdfStructureType.Sect);
        pdf.Structure.SetRole("Kapittel", "Chapter");
        PdfStructureElement own = pdf.Structure.Root!.Add("Kapittel");
        Assert.Equal("Kapittel", own.Type.Name);
        Assert.Equal(PdfStructureType.Sect, own.StandardType);
        Assert.False(own.Type.IsStandard);
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        Assert.Equal("Chapter", saved.Structure.RoleMap["Kapittel"]);
    }

    [Fact]
    public void EditsRebuildTheParentTree()
    {
        using PdfDocument pdf = PdfDocument.Open(Tagged());
        PdfStructureElement root = pdf.Structure.Root!;
        PdfStructureElement second = root.Elements.ElementAt(1);
        Assert.True(root.Remove(second));
        PdfStructureElement figure = root.Add(PdfStructureType.Figure, 0);
        figure.AlternateText = "A diagram";
        figure.AddAttributes("Layout", new PdfDictionary { ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) } });
        figure.AddMarkedContent(pdf.Pages[0], 1);
        using PdfDocument saved = PdfDocument.Open(Save(pdf));
        PdfStructureElement found = saved.Structure.FindMarkedContent(saved.Pages[0], 1)!;
        Assert.Equal(PdfStructureType.Figure, found.Type);
        Assert.Equal("A diagram", found.AlternateText);
        Assert.Equal("Layout", Assert.Single(found.Attributes).GetName("O")?.Value);
        Assert.Equal(PdfStructureType.Figure, saved.Structure.Root!.Elements.First().Type);
    }

    [Fact]
    public void ImportedPagesBringTheirStructure()
    {
        using PdfDocument source = PdfDocument.Open(Tagged());
        using PdfDocument target = PdfDocument.Create();
        target.Pages.Import(source, [1]);
        Assert.DoesNotContain(target.Log.ToArray(), w => w.Code == PdfWarningCode.NotImported);
        using PdfDocument saved = PdfDocument.Open(Save(target));
        Assert.True(saved.Structure.Exists);
        PdfStructureElement top = Assert.Single(saved.Structure.Children);
        Assert.Equal(PdfStructureType.Document, top.Type);

        // Only the second page's heading and paragraph come; the first page's elements and its link stay behind.
        Assert.Equal(["H1", "P"], top.Elements.Select(e => e.Type.Name));
        Assert.Equal("Heading 2", top.Elements.First().Title);
        Assert.Same(top.Elements.First(), saved.Structure.FindMarkedContent(saved.Pages[0], 0));
        Assert.NotNull(saved.Pages[0].Dictionary.GetInt("StructParents"));
    }

    [Fact]
    public void MergingTwoTaggedFilesKeepsBothTrees()
    {
        using PdfDocument target = PdfDocument.Open(Tagged());
        using PdfDocument other = PdfDocument.Open(Tagged());
        target.Pages.Import(other, [0, 1]);
        using PdfDocument saved = PdfDocument.Open(Save(target));
        Assert.Equal(4, saved.Pages.Count);
        PdfStructureElement root = saved.Structure.Root!;
        Assert.Equal(8, root.Elements.Count(e => e.Type.Name is "H1" or "P"));
        Assert.Same(root.Elements.Last(e => e.Type.Name == "P").Children.OfType<PdfMarkedContentReference>().Single().PageDictionary, saved.Pages[3].Dictionary);
    }

    [Fact]
    public void AnUntaggedDocumentHasNoTree()
    {
        using PdfDocument pdf = PdfDocument.Create();
        Assert.False(pdf.Structure.Exists);
        Assert.Empty(pdf.Structure.Children);
        Assert.Null(pdf.Structure.Root);
        Assert.Empty(pdf.Structure.Descendants());
    }
}
