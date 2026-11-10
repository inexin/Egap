using Egap.Documents;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Encrypted workbooks: the files in <c>Oracle/encrypted</c> (see <c>encrypted.txt</c>) were encrypted by Excel (agile XLSX,
/// RC4 CryptoAPI XLS) and LibreOffice (Office 97/2000 RC4 XLS), all with one password, over the same small workbook.
/// </summary>
public sealed class EncryptionTests
{
    private const string Password = "Egap pässword 1";

    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Oracle", "encrypted", name);

    [Theory]
    [InlineData("agile.xlsx")]
    [InlineData("cryptoapi.xls")]
    [InlineData("rc4.xls")]
    public void OpensWithThePassword(string name)
    {
        Workbook book = Workbook.Load(Path(name), new WorkbookLoadOptions { Password = Password });
        AssertContent(book);
    }

    [Theory]
    [InlineData("agile.xlsx")]
    [InlineData("cryptoapi.xls")]
    [InlineData("rc4.xls")]
    public void AsksForThePasswordAndRefusesAWrongOne(string name)
    {
        DocumentPasswordException missing = Assert.Throws<DocumentPasswordException>(() => Workbook.Load(Path(name)));
        Assert.False(missing.WrongPassword);
        DocumentPasswordException wrong = Assert.Throws<DocumentPasswordException>(() => Workbook.Load(Path(name), new WorkbookLoadOptions { Password = "egap pässword 1" }));
        Assert.True(wrong.WrongPassword);
    }

    [Theory]
    [InlineData(WorkbookEncryption.Agile)]
    [InlineData(WorkbookEncryption.Standard)]
    public void SavesEncryptedXlsxThatOpensAgain(WorkbookEncryption encryption)
    {
        Workbook book = Workbook.Load(Path("rc4.xls"), new WorkbookLoadOptions { Password = Password });
        using MemoryStream saved = new();
        book.Save(saved, new WorkbookSaveOptions { Password = Password, Encryption = encryption });
        Assert.True(saved.Length > 0);
        Assert.Equal(0xD0, saved.GetBuffer()[0]);
        saved.Position = 0;
        AssertContent(Workbook.Load(saved, new WorkbookLoadOptions { Password = Password }));
        saved.Position = 0;
        Assert.Throws<DocumentPasswordException>(() => Workbook.Load(saved));
    }

    [Fact]
    public void ExcelsDefaultPasswordNeedsNoneGiven()
    {
        // Excel encrypts a workbook with VelvetSweatshop when it has no password of its own, and opens it without asking.
        Workbook book = Workbook.Load(Path("rc4.xls"), new WorkbookLoadOptions { Password = Password });
        using MemoryStream saved = new();
        book.Save(saved, new WorkbookSaveOptions { Password = "VelvetSweatshop" });
        saved.Position = 0;
        AssertContent(Workbook.Load(saved));
    }

    [Fact]
    public void AnEmptyPasswordSavesThePlainFile()
    {
        Workbook book = Workbook.Create();
        book.Worksheets[0]["A1"].Value = "x";
        using MemoryStream saved = new();
        book.Save(saved, new WorkbookSaveOptions { Password = "" });
        Assert.Equal((byte)'P', saved.GetBuffer()[0]);
    }

    private static void AssertContent(Workbook book)
    {
        Assert.Equal(["Data", "Second"], book.Worksheets.Select(s => s.Name));
        Worksheet data = book.Worksheets["Data"];
        Assert.Equal("Name", data["A1"].Text);
        Assert.Equal(12.5, data["B2"].Value.Number);
        Assert.Equal(45000, data["C2"].Value.Number);
        Assert.Equal("Beta æøå", data["A3"].Text);
        Assert.Equal("SUM(B2:B3)", data["B4"].Formula);
        Assert.Equal(9.5, data["B4"].Value.Number);
        Worksheet second = book.Worksheets["Second"];
        Assert.Equal("On the second sheet", second["A1"].Text);
        Assert.Equal("Row 400 of filler text", second["A400"].Text);
        Assert.Equal(600, second["B400"].Value.Number);
    }
}
