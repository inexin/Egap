namespace Egap.Pdf.Tests.Print;

/// <summary>Preflight: the colour inventory and the print profile checks, on hand-made files with known contents.</summary>
public sealed class PreflightTests
{
    public static TheoryData<string> Files => [.. PrintSamples.All().Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(Files))]
    public void ItemsAreExactlyTheKnownContents(string name)
    {
        PrintSamples.PrintFile file = PrintSamples.All().Single(f => f.Name == name);
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(file.Bytes));

        PdfPreflightReport report = PdfPreflight.Run(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(file.Truth.OutputCondition, report.OutputIntents.FirstOrDefault()?.Name);
        for (int i = 0; i < file.Truth.Pages.Count; i++)
        {
            PrintSamples.PageTruth truth = file.Truth.Pages[i];
            PdfPreflightItem[] items = [.. report.ItemsOn(i + 1)];
            string all = string.Join("\n", items.Select(x => x.ToString()));
            Assert.Equal(truth.Spaces.Order(StringComparer.Ordinal),
                items.Where(x => x.Kind == PdfPreflightItemKind.ColorSpace).Select(x => x.ColorSpaceName!).Distinct().Order(StringComparer.Ordinal));
            Assert.Equal(truth.Spots, items.SelectMany(x => x.Spots).Distinct().Order(StringComparer.Ordinal));
            PdfPreflightItem[] images = [.. items.Where(x => x.Kind == PdfPreflightItemKind.Image)];
            Assert.Equal(truth.Images.Select(t => t.Name), images.Select(x => x.Name!));
            foreach ((PrintSamples.ImageTruth expected, PdfPreflightItem found) in truth.Images.Zip(images))
            {
                Assert.Equal(expected.Space, found.ColorSpaceName);
                Assert.Equal(expected.Dpi, found.Dpi!.Value, 1);
            }

            Assert.Equal(truth.FontsNotEmbedded, items.Where(x => x.Kind == PdfPreflightItemKind.Font && x.Embedded == false).Select(x => x.Name!));
            Assert.True(truth.Transparency == items.Any(x => x.Kind == PdfPreflightItemKind.Transparency), all);
            Assert.True(truth.Overprint == items.Any(x => x.Kind == PdfPreflightItemKind.Overprint), all);
            Assert.Equal(truth.Hairlines, items.Count(x => x.Kind == PdfPreflightItemKind.Hairline));
            PdfPreflightItem boxes = items.Single(x => x.Kind == PdfPreflightItemKind.PageBoxes);
            Assert.True(truth.Bleed == boxes.Bleed >= 3, all);
        }
    }

    [Fact]
    public void TheAffinityCaseShowsTheFogra39IntentAndTheRgbImagesByNameAndPage()
    {
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(PrintSamples.Affinity().Bytes));

        PdfPreflightReport report = PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated, TestContext.Current.CancellationToken);

        PdfPreflightItem intent = Assert.Single(report.OutputIntents);
        Assert.Equal("FOGRA39", intent.Name);
        Assert.Equal(PdfColorSpaceKinds.CalibratedCmyk, intent.ColorSpace);
        Assert.Contains("FOGRA39", intent.ProfileName, StringComparison.OrdinalIgnoreCase);
        PdfAIssue[] rgb = [.. report.Issues.Where(i => i.Clause == "ColorSpace")];
        Assert.Equal(2, rgb.Length);
        Assert.All(rgb, i => Assert.Equal(1, i.Page));
        Assert.Contains(rgb, i => i.Description.Contains("ICCBased RGB", StringComparison.Ordinal));
        Assert.Contains(rgb, i => i.Description.Contains("DeviceRGB", StringComparison.Ordinal));
        PdfAIssue low = Assert.Single(report.Issues, i => i.Clause == "ImageResolution");
        Assert.Contains("/Im2", low.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Issues, i => i.Clause is "Bleed" or "TrimBox");
    }

    [Fact]
    public void ProfilesFlagFontsBleedHairlinesAndTransparency()
    {
        using PdfDocument rules = PdfDocument.Open(new MemoryStream(PrintSamples.FontsAndRules().Bytes));
        PdfPreflightReport report = PdfPreflight.Run(rules, PdfPrintProfile.WebOffset, TestContext.Current.CancellationToken);
        Assert.Contains(report.Issues, i => i.Clause == "Font" && i.Description.Contains("Helvetica", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Clause == "TrimBox");
        Assert.Equal(2, report.Issues.Count(i => i.Clause == "LineWidth"));
        Assert.Contains(report.Issues, i => i.Clause == "ColorSpace" && i.Description.Contains("Lab", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Clause == "ImageResolution");

        using PdfDocument transparent = PdfDocument.Open(new MemoryStream(PrintSamples.Transparency().Bytes));
        PdfPrintProfile noTransparency = PdfPrintProfile.SheetfedCoated with { AllowTransparency = false, RequiredBleed = null };
        PdfPreflightReport flat = PdfPreflight.Run(transparent, noTransparency, TestContext.Current.CancellationToken);
        Assert.Equal(4, flat.Issues.Count(i => i.Clause == "Transparency"));
        Assert.All(flat.Issues, i => Assert.Equal("Transparency", i.Clause));
    }

    [Fact]
    public void TheInkCheckMeasuresEachPageOnItsPlates()
    {
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(PrintSamples.FlatInk().Bytes));

        PdfPreflightReport web = PdfPreflight.Run(pdf, PdfPrintProfile.WebOffset with { RequiredBleed = null }, TestContext.Current.CancellationToken);
        PdfPreflightReport sheetfed = PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated with { RequiredBleed = null }, TestContext.Current.CancellationToken);

        PdfPreflightItem ink = Assert.Single(web.Items, i => i.Kind == PdfPreflightItemKind.InkCoverage);
        Assert.Equal(1, ink.Page);
        Assert.InRange(ink.MaxInkCoverage!.Value, 325, 327);
        Assert.InRange(ink.ShareOverLimit!.Value, 0.45, 0.55);
        PdfAIssue issue = Assert.Single(web.Issues, i => i.Clause == "InkCoverage");
        Assert.Contains("326%", issue.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(sheetfed.Issues, i => i.Clause == "InkCoverage");
    }

    [Fact]
    public void NoProfileMeansNoIssues()
    {
        foreach (PrintSamples.PrintFile file in PrintSamples.All())
        {
            using PdfDocument pdf = PdfDocument.Open(new MemoryStream(file.Bytes));
            PdfPreflightReport report = PdfPreflight.Run(pdf, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(report.Passed, report.ToString());
        }
    }

    [Fact]
    public void ADeviceSpaceWithADefaultSpaceIsReportedAsTheDefault()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(3) });
        profile.SetDecodedBytes(Egami.Colors.ColorProfile.Srgb.ToIcc());
        page.Resources = new PdfDictionary
        {
            ["ColorSpace"] = new PdfDictionary { ["DefaultRGB"] = new PdfArray { PdfName.Get("ICCBased"), pdf.Objects.Add(profile) } },
        };
        page.SetContent(PdfContent.Parse("1 0 0 rg 0 0 10 10 re f"u8));

        PdfPreflightReport report = PdfPreflight.Run(pdf, PdfPrintProfile.PdfX4, TestContext.Current.CancellationToken);

        PdfPreflightItem space = Assert.Single(report.Items, i => i.Kind == PdfPreflightItemKind.ColorSpace);
        Assert.Equal(PdfColorSpaceKinds.CalibratedRgb, space.ColorSpace);
        Assert.StartsWith("DeviceRGB as DefaultRGB", space.ColorSpaceName, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Issues, i => i.Clause == "ColorSpace");
    }

    [Fact]
    public void PatternsAndPrintedAppearancesAreWalked()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfStream tile = new(new PdfDictionary
        {
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(1),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) },
            ["XStep"] = new PdfInteger(10),
            ["YStep"] = new PdfInteger(10),
        });
        tile.SetDecodedBytes("0 1 0 rg 0 0 5 5 re f"u8.ToArray());
        page.Resources = new PdfDictionary { ["Pattern"] = new PdfDictionary { ["P0"] = pdf.Objects.Add(tile) } };
        page.SetContent(PdfContent.Parse("/Pattern cs /P0 scn 0 0 100 100 re f"u8));
        PdfStream appearance = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(20), new PdfInteger(20) },
        });
        appearance.SetDecodedBytes("0 0 0 1 k 0 0 20 20 re f"u8.ToArray());
        PdfDictionary annotation = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Square"),
            ["Rect"] = new PdfArray { new PdfInteger(100), new PdfInteger(100), new PdfInteger(120), new PdfInteger(120) },
            ["F"] = new PdfInteger(4),
            ["AP"] = new PdfDictionary { ["N"] = pdf.Objects.Add(appearance) },
        };
        page.Dictionary["Annots"] = new PdfArray { pdf.Objects.Add(annotation) };

        PdfPreflightReport report = PdfPreflight.Run(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PdfColorSpaceKinds.DeviceRgb | PdfColorSpaceKinds.DeviceCmyk, report.ColorSpaces);
    }
}
