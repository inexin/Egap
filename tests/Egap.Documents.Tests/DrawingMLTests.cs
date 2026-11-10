using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents.DrawingML;

namespace Egap.Documents.Tests;

/// <summary>The shared DrawingML colour, fill and line reading that charts and shapes use.</summary>
public sealed class DrawingMLTests
{
    private static readonly XNamespace A = DrawingNs.A;

    [Fact]
    public void FindsTheFirstColourItKnows()
    {
        XElement fill = new(A + "solidFill", new XElement(A + "hslClr"), new XElement(A + "schemeClr", new XAttribute("val", "accent2"), new XElement(A + "lumMod", new XAttribute("val", "75000"))));

        DrawingColorElement found = DrawingColors.Find(fill)!.Value;

        Assert.Equal(DrawingColorKind.Theme, found.Kind);
        Assert.Equal("accent2", found.Name);
        Assert.True(DrawingColors.TryChange(found.Element.Elements().Single(), out string name, out double value));
        Assert.Equal(("lumMod", 0.75), (name, value));
    }

    [Fact]
    public void ReadsSystemColoursWithTheirLastValue()
    {
        XElement fill = new(A + "solidFill", new XElement(A + "sysClr", new XAttribute("val", "windowText"), new XAttribute("lastClr", "1F2E3D")));

        DrawingColorElement found = DrawingColors.Find(fill)!.Value;

        Assert.Equal(DrawingColorKind.Named, found.Kind);
        Assert.Equal(Rgba8.FromHex("1F2E3D"), found.Rgb);
        Assert.Null(DrawingColors.Find(new XElement(A + "noFill")));
    }

    [Fact]
    public void WritesColoursBack()
    {
        XElement color = DrawingColors.Element(DrawingColorKind.Rgb, new Rgba8(0xC0, 0, 0, 255), "");
        color.Add(DrawingColors.ChangeElement("alpha", 0.5));

        Assert.Equal(A + "srgbClr", color.Name);
        Assert.Equal("C00000", (string?)color.Attribute("val"));
        Assert.Equal("50000", (string?)color.Element(A + "alpha")?.Attribute("val"));
    }

    [Fact]
    public void AppliesColourChanges()
    {
        Rgba8 blue = Rgba8.FromHex("4472C4");

        Assert.Equal(new Rgba8(0x44, 0x72, 0xC4, 128), DrawingColors.Apply(blue, "alpha", 0.5));
        Assert.Equal(new Rgba8(0xBB, 0x8D, 0x3B, 255), DrawingColors.Apply(blue, "inv", 1));
        Assert.Equal(blue, DrawingColors.Apply(blue, "unknown", 0.3));
        Rgba8 darker = DrawingColors.Apply(blue, "lumMod", 0.75);
        Assert.True(darker.R < blue.R && darker.G < blue.G && darker.B < blue.B);
    }

    [Fact]
    public void ReadsGradientsAndPatterns()
    {
        XElement gradient = new(
            A + "gradFill",
            new XElement(A + "gsLst", new XElement(A + "gs", new XAttribute("pos", "0")), new XElement(A + "gs", new XAttribute("pos", "100000"))),
            new XElement(A + "lin", new XAttribute("ang", "5400000")));

        Assert.Equal([0.0, 1.0], DrawingFills.Stops(gradient).Select(s => s.Position));
        Assert.Equal(90, DrawingFills.Angle(gradient));
        Assert.Equal("gradFill", DrawingFills.Kind(new XElement(A + "spPr", new XElement(A + "xfrm"), gradient)));
        Assert.Equal(0.25, DrawingFills.PatternInk("pct25"));
        Assert.Equal(0.35, DrawingFills.PatternInk("dkHorz"));
    }

    [Fact]
    public void GivesPresetDashLengths()
    {
        Assert.Equal([2f, 6f], DrawingFills.Dash("dot", 2)!);
        Assert.Equal([2f, 2f], DrawingFills.Dash("sysDot", 2)!);
        Assert.Null(DrawingFills.Dash("solid", 2));
    }
}
