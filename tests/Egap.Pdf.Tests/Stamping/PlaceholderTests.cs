using System.Globalization;

namespace Egap.Pdf.Tests.Stamping;

/// <summary>The placeholder names shared by stamps and page bands.</summary>
public sealed class PlaceholderTests
{
    private static readonly PdfPlaceholderValues Values = new()
    {
        Page = 3,
        TotalPages = 12,
        Label = "iii",
        Now = new DateTimeOffset(2026, 10, 19, 14, 5, 0, TimeSpan.Zero),
        Title = "Annual report",
        File = "report.pdf",
    };

    [Theory]
    [InlineData("Page {page} of {total-pages}", "Page 3 of 12")]
    [InlineData("{page}/{pages}", "3/12")]
    [InlineData("{page:000}", "003")]
    [InlineData("{label}", "iii")]
    [InlineData("{date:yyyy-MM-dd} {time:HH:mm}", "2026-10-19 14:05")]
    [InlineData("{date}", "10/19/2026")]
    [InlineData("{title} ({file})", "Annual report (report.pdf)")]
    [InlineData("{unknown} {bates}", "{unknown} {bates}")]
    [InlineData("{{page}", "{page}")]
    [InlineData("no fields", "no fields")]
    public void FillsEachPlaceholder(string template, string expected)
    {
        Assert.Equal(expected, PdfPlaceholders.Fill(template, Values));
    }

    [Fact]
    public void UsesTheCulture()
    {
        PdfPlaceholderValues norwegian = Values with { Culture = CultureInfo.GetCultureInfo("nb-NO") };

        Assert.Equal("19.10.2026", PdfPlaceholders.Fill("{date}", norwegian));
        Assert.Equal("3", PdfPlaceholders.Fill("{label}", Values with { Label = null }));
    }

    [Fact]
    public void StampsTakeTheSharedNames()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();
        pdf.Pages.AddBlank();

        pdf.Stamp(PdfStamp.Text("Page {page} of {total-pages}"));

        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Equal("Page 2 of 2", reopened.Pages[1].ExtractText().Trim());
    }
}
