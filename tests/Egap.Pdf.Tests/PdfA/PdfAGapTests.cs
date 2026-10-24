using System.Text;

using Egami.Colors;

namespace Egap.Pdf.Tests.PdfA;

/// <summary>PDF/A rules the corpus showed Egap did not check: each with a hand-made file that breaks it.</summary>
public sealed class PdfAGapTests
{
    [Fact]
    public void UndefinedRenderingIntentsBecomeRelativeColorimetric()
    {
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking("/Odd ri BT /F1 12 Tf 72 700 Td (x) Tj ET q 10 0 0 10 300 300 cm /Im Do Q /G gs"));
        PdfDictionary resources = pdf.Pages[0].Resources!;
        resources.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("G")!["RI"] = PdfName.Get("Vivid");
        PdfDictionary image = ((PdfStream)resources.Get<PdfDictionary>("XObject")!["Im"]!.Resolve()).Dictionary;
        image["Intent"] = PdfName.Get("Photographic");

        PdfAReport report = Converted(pdf, "6.2.6", 3);

        Assert.Equal("RelativeColorimetric", resources.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("G")!.GetName("RI")?.Value);
        Assert.Equal("RelativeColorimetric", image.GetName("Intent")?.Value);
        Assert.Contains("/RelativeColorimetric ri", Encoding.Latin1.GetString(pdf.Pages[0].GetContent().ToBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public void HalftoneNamesAndPrimaryTransferFunctionsAreRemoved()
    {
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking());
        PdfDictionary function = new() { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["N"] = new PdfInteger(1) };
        PdfDictionary cyan = new() { ["HalftoneType"] = new PdfInteger(1), ["Frequency"] = new PdfInteger(100), ["Angle"] = new PdfInteger(15), ["SpotFunction"] = PdfName.Get("Round"), ["TransferFunction"] = function };
        PdfDictionary gold = new() { ["HalftoneType"] = new PdfInteger(1), ["Frequency"] = new PdfInteger(100), ["Angle"] = new PdfInteger(45), ["SpotFunction"] = PdfName.Get("Round"), ["TransferFunction"] = function.Clone() };
        PdfDictionary halftone = new() { ["HalftoneType"] = new PdfInteger(5), ["HalftoneName"] = PdfString.FromText("Press"), ["Cyan"] = cyan, ["Gold"] = gold, ["Default"] = cyan.Clone() };
        pdf.Pages[0].Resources!.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("G")!["HT"] = halftone;

        Converted(pdf, "6.2.5", 3);

        Assert.False(halftone.ContainsKey("HalftoneName"));
        Assert.False(cyan.ContainsKey("TransferFunction"));
        Assert.True(gold.ContainsKey("TransferFunction"));
    }

    [Fact]
    public void WidgetActionsAndPresentationStepsAreRemoved()
    {
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking());
        PdfDictionary widget = new()
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Widget"),
            ["FT"] = PdfName.Get("Btn"),
            ["Ff"] = new PdfInteger(65536),
            ["T"] = PdfString.FromText("go"),
            ["Rect"] = new PdfArray { new PdfInteger(10), new PdfInteger(10), new PdfInteger(60), new PdfInteger(30) },
            ["A"] = new PdfDictionary { ["S"] = PdfName.Get("URI"), ["URI"] = PdfString.FromText("https://example.com") },
        };
        PdfReference reference = pdf.Objects.Add(widget);
        pdf.Pages[0].Dictionary.Get<PdfArray>("Annots")!.Add(reference);
        pdf.Catalog.Get<PdfDictionary>("AcroForm")!.Get<PdfArray>("Fields")!.Add(reference);
        pdf.Pages[0].Dictionary["PresSteps"] = new PdfDictionary { ["Type"] = PdfName.Get("NavNode") };

        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);

        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Fixes, i => i.Clause == "6.4.1" && i.Description.Contains("widget", StringComparison.Ordinal));
        Assert.Contains(report.Fixes, i => i.Clause == "6.10");
        Assert.False(widget.ContainsKey("A"));
        Assert.False(pdf.Pages[0].Dictionary.ContainsKey("PresSteps"));
    }

    [Fact]
    public void AnUnreadableIccProfileIsReplacedByAStandardOne()
    {
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking("/P cs 1 0 0 0 sc 0 0 10 10 re f /G gs"));
        PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(4), ["Alternate"] = PdfName.Get("DeviceCMYK") });
        profile.SetDecodedBytes(Encoding.ASCII.GetBytes(new string('x', 200)));
        pdf.Pages[0].Resources!["ColorSpace"] = new PdfDictionary { ["P"] = new PdfArray { PdfName.Get("ICCBased"), pdf.Objects.Add(profile) } };

        Converted(pdf, "6.2.4.2", 1);

        Assert.Equal(ColorModel.Cmyk, ColorProfile.FromIcc(profile.GetDecodedBytes()).Model);
    }

    [Fact]
    public void MetadataStreamsSayWhatTheyAreAndStructureMetadataIsChecked()
    {
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking());
        PdfStream undeclared = new(new PdfDictionary());
        undeclared.SetDecodedBytes(Encoding.UTF8.GetBytes("<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:made=\"http://example.com/made/\"><made:Up>1</made:Up></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>"), compress: false);
        pdf.Catalog["StructTreeRoot"] = new PdfDictionary { ["Type"] = PdfName.Get("StructTreeRoot"), ["K"] = pdf.Objects.Add(new PdfDictionary { ["S"] = PdfName.Get("Part"), ["Metadata"] = pdf.Objects.Add(undeclared) }) };

        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);

        Assert.True(report.Succeeded, report.ToString());
        Assert.Contains(report.Fixes, i => i.Clause == "6.6.2.3" && i.Description.StartsWith("object metadata", StringComparison.Ordinal));
        Assert.DoesNotContain("made:Up", Encoding.UTF8.GetString(undeclared.GetDecodedBytes()), StringComparison.Ordinal);
        PdfStream catalogMetadata = pdf.Catalog.Get<PdfStream>("Metadata")!;
        Assert.Equal(("Metadata", "XML"), (catalogMetadata.Dictionary.GetName("Type")?.Value, catalogMetadata.Dictionary.GetName("Subtype")?.Value));

        // A catalog stream written without /Type and /Subtype gets them.
        catalogMetadata.Dictionary.Remove("Type");
        catalogMetadata.Dictionary.Remove("Subtype");
        Converted(pdf, "6.6.2.1", 1);
        Assert.Equal("XML", catalogMetadata.Dictionary.GetName("Subtype")?.Value);
    }

    [Fact]
    public void UnfixableGapsAreReportedAndChangeNothing()
    {
        // A name past 127 bytes, two Separation spaces for one ink that disagree, a DeviceN colourant whose alternate is
        // DeviceCMYK under an sRGB intent, and a non-symbolic TrueType font without a standard encoding.
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking("/S1 cs 1 sc 0 0 5 5 re f /S2 cs 1 sc 5 5 5 5 re f /N cs 1 sc 0 0 1 1 re f /G gs"));
        PdfDictionary tint1 = new() { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["C0"] = new PdfArray { new PdfInteger(1) }, ["C1"] = new PdfArray { new PdfInteger(0) }, ["N"] = new PdfInteger(1) };
        PdfDictionary tint2 = new() { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["C0"] = new PdfArray { new PdfInteger(1) }, ["C1"] = new PdfArray { new PdfReal(0.5) }, ["N"] = new PdfInteger(1) };
        PdfArray gold = [PdfName.Get("Separation"), PdfName.Get("Gold"), PdfName.Get("DeviceCMYK"), tint1];
        PdfArray deviceN = [PdfName.Get("DeviceN"), new PdfArray { PdfName.Get("Gold") }, PdfName.Get("DeviceGray"), tint1.Clone(), new PdfDictionary { ["Colorants"] = new PdfDictionary { ["Gold"] = gold } }];
        pdf.Pages[0].Resources!["ColorSpace"] = new PdfDictionary
        {
            ["S1"] = new PdfArray { PdfName.Get("Separation"), PdfName.Get("Silver"), PdfName.Get("DeviceGray"), tint1.Clone() },
            ["S2"] = new PdfArray { PdfName.Get("Separation"), PdfName.Get("Silver"), PdfName.Get("DeviceGray"), tint2 },
            ["N"] = deviceN,
        };
        pdf.Catalog["Long"] = PdfName.Get(new string('n', 130));
        byte[] before = Save(pdf);
        using PdfDocument opened = PdfDocument.Open(before);

        PdfAReport report = PdfAConverter.Convert(opened, PdfAConformance.PdfA2b);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Unmet, i => i.Clause == "6.1.13" && i.Description.Contains("127", StringComparison.Ordinal));
        Assert.Contains(report.Unmet, i => i.Clause == "6.2.4.4" && i.Description.Contains("Silver", StringComparison.Ordinal));
        Assert.Contains(report.Unmet, i => i.Clause == "6.2.4.3" && i.Description.Contains("DeviceCMYK", StringComparison.Ordinal));
        Assert.False(opened.IsChanged);
    }

    [Fact]
    public void NameLengthCountsTheNamesBytes()
    {
        // 70 bytes that are not UTF-8 read as 70 Latin-1 characters, 140 bytes once encoded as UTF-8: still within the limit.
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking());
        pdf.Catalog["Short"] = PdfName.Get(Enumerable.Repeat((byte)0xE9, 70).ToArray());
        Assert.DoesNotContain(PdfAConverter.Check(pdf, PdfAConformance.PdfA2b).Issues, i => i.Description.Contains("127", StringComparison.Ordinal));

        pdf.Catalog["Long"] = PdfName.Get(Enumerable.Repeat((byte)'n', 128).ToArray());
        Assert.Contains(PdfAConverter.Check(pdf, PdfAConformance.PdfA2b).Issues, i => i.Clause == "6.1.13" && i.Description.Contains("127", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonSymbolicTrueTypeFontWithoutAStandardEncodingIsReported()
    {
        // Conversion embeds Helvetica as a TrueType subset with WinAnsiEncoding, which passes; Differences alone do not.
        using PdfDocument pdf = PdfDocument.Open(PdfAConvertTests.Breaking());
        Assert.True(PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b).Succeeded);
        PdfDictionary font = pdf.Pages[0].Resources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("F1")!;
        Assert.Equal("TrueType", font.GetName("Subtype")?.Value);
        Assert.True(PdfAConverter.Check(pdf, PdfAConformance.PdfA2b).Succeeded);

        font["Encoding"] = new PdfDictionary { ["Differences"] = new PdfArray { new PdfInteger(65), PdfName.Get("B") } };
        PdfAReport report = PdfAConverter.Check(pdf, PdfAConformance.PdfA2b);

        Assert.Contains(report.Issues, i => i.Clause == "6.2.11.6" && i.Description.Contains("non-symbolic TrueType", StringComparison.Ordinal) && !i.Fixable);
    }

    private static PdfAReport Converted(PdfDocument pdf, string clause, int fixes)
    {
        PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
        Assert.True(report.Succeeded, report.ToString());
        Assert.True(report.Fixes.Count(i => i.Clause == clause) >= fixes, report.ToString());
        Assert.True(PdfAConverter.Check(PdfDocument.Open(Save(pdf)), PdfAConformance.PdfA2b).Succeeded);
        return report;
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream stream = new();
        pdf.Save(stream);
        return stream.ToArray();
    }
}
