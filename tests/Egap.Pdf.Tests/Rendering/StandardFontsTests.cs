using Egami.Fonts;

using Egap.Fonts;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Egap.Fonts: the bundled metric-compatible substitutes, found by their names and aliases.</summary>
public sealed class StandardFontsTests
{
    [Theory]
    [InlineData("Helvetica", 400, "LiberationSans")]
    [InlineData("Arial", 700, "LiberationSans-Bold")]
    [InlineData("Times New Roman", 400, "LiberationSerif")]
    [InlineData("Courier-New", 700, "LiberationMono-Bold")]
    [InlineData("Calibri", 400, "Carlito-Regular")]
    [InlineData("Cambria", 700, "Caladea-Bold")]
    public void AliasesFindTheirMetricCompatibleFaces(string name, int weight, string postScript)
    {
        Typeface face = StandardFonts.Find(name, new FontStyle(weight, 5, FontSlant.Upright))!;

        Assert.Equal(postScript, face.PostScriptName);
    }

    [Fact]
    public void ItalicsAndUnknownNames()
    {
        Assert.Equal("LiberationSerif-BoldItalic", StandardFonts.Find("Times", new FontStyle(700, 5, FontSlant.Italic))!.PostScriptName);
        Assert.Null(StandardFonts.Find("Verdana", FontStyle.Normal));
    }

    [Fact]
    public void TheCollectionHoldsEveryFaceAndTheLicencesShip()
    {
        Assert.Equal(5, StandardFonts.Collection.Count);
        Assert.All(StandardFonts.Collection.Families, f => Assert.Equal(4, f.Styles.Count));
        string[] resources = typeof(StandardFonts).Assembly.GetManifestResourceNames();
        Assert.Contains("Egap.Fonts.Liberation-OFL.txt", resources);
        Assert.Contains("Egap.Fonts.Carlito-OFL.txt", resources);
        Assert.Contains("Egap.Fonts.Caladea-OFL.txt", resources);
    }
}
