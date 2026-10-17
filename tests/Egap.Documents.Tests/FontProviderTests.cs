using Egami.Fonts;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>The font chain for layout (Phase 6 Build Plan task 15, design Q12).</summary>
public class FontProviderTests
{
    private static readonly FontStyle Regular = new(Weight: 400);
    private static readonly FontStyle Bold = new(Weight: 700);

    private static FontSettings Isolated()
    {
        return new FontSettings { UseSystemFonts = false };
    }

    [Fact]
    public void MissingWordFontsGetTheirMetricCompatibleSubstitutes()
    {
        FontProvider provider = new(new Document(), Isolated());

        Assert.Equal("Carlito", provider.Resolve("Calibri", Regular).Typeface.FamilyName);
        Assert.Equal("Caladea", provider.Resolve("Cambria", Bold).Typeface.FamilyName);
        Assert.Equal("Liberation Serif", provider.Resolve("Times New Roman", Regular).Typeface.FamilyName);
        FontSubstitution calibri = provider.Substitutions.First(s => s.Requested == "Calibri");
        Assert.Equal(FontSubstitutionReason.Missing, calibri.Reason);
        Assert.Equal("Carlito", calibri.Used);
    }

    [Fact]
    public void EmbeddedFontsComeFirst()
    {
        Document document = new();
        document.EmbeddedFonts.Add(new EmbeddedFont("Noto Sans", false, false, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"))));
        FontProvider provider = new(document, new FontSettings { UseSystemFonts = false, UseStandardSubstitutes = false });

        FontMatch regular = provider.Resolve("Noto Sans", Regular);
        FontMatch bold = provider.Resolve("Noto Sans", Bold);

        Assert.Equal("Noto Sans", regular.Typeface.FamilyName);
        Assert.Equal(FontSynthesis.None, regular.Synthesis);
        Assert.Equal(FontSynthesis.Bold, bold.Synthesis);
        Assert.Contains(provider.Substitutions, s => s.Reason == FontSubstitutionReason.Synthesized);
    }

    [Fact]
    public void CallerCollectionsAndTheReplacementTable()
    {
        FontCollection mine = new();
        mine.AddFile(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "SourceSans3-Latin.otf"));
        FontSettings settings = new()
        {
            UseSystemFonts = false,
            Collections = [mine],
            Substitutions = new Dictionary<string, string> { ["corporate sans"] = "Source Sans 3" },
        };
        FontProvider provider = new(new Document(), settings);

        Assert.StartsWith("Source Sans", provider.Resolve("Corporate Sans", Regular).Typeface.FamilyName, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFontsFallToTheLastResort()
    {
        FontProvider provider = new(new Document(), new FontSettings { UseSystemFonts = false, UseStandardSubstitutes = false });

        Assert.Equal("Liberation Sans", provider.Resolve("No Such Font", Regular).Typeface.FamilyName);
        Assert.Equal("Liberation Sans", provider.Resolve(null, Regular).Typeface.FamilyName);
        Assert.Single(provider.Substitutions);
        Assert.NotNull(provider.Fallback);
    }
}
