using System.Security.Cryptography;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>Portable fonts give the same PDF bytes on every machine.</summary>
public sealed class PortableOutputTests
{
    [Fact]
    public void APortableDeterministicExportHasKnownBytes()
    {
        Document document = new();
        document.Properties.Title = "Portable";
        Section section = document.AddSection();
        section.Body.AddParagraph("Invoice 2026-118").Runs.First().Format.Bold = true;
        section.Body.AddParagraph("Delivered in Oslo: 12 boxes of paper, 3 400 NOK, payable within 14 days.");
        Table table = section.Body.AddTable(2, 2);
        table.Rows[0].Cells[0].AddParagraph("Item");
        table.Rows[0].Cells[1].AddParagraph("Price");
        table.Rows[1].Cells[0].AddParagraph("Paper");
        table.Rows[1].Cells[1].AddParagraph("3 400");

        using MemoryStream first = new();
        document.SaveAsPdf(first, Options());
        using MemoryStream second = new();
        document.SaveAsPdf(second, Options());

        Assert.Equal(first.ToArray(), second.ToArray());

        // The same hash on Windows, Linux and macOS (CI runs on Linux): no system font, no time and no machine detail
        // reaches the file. A change to layout or writing that moves a byte changes it; update it then, after checking why.
        string hash = Convert.ToHexString(SHA256.HashData(first.ToArray()));
        Assert.True(hash == "93A729480C85DBB57449966A0608896ACDEC8C89875AD84A130A09C91ABCF297", hash);
    }

    private static PdfExportOptions Options() => new()
    {
        Layout = new LayoutOptions { Fonts = FontSettings.Portable, Strict = true },
        Save = new PdfSaveOptions { Deterministic = true },
    };
}
