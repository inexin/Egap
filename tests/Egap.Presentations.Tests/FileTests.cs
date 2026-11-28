using System.Text;

using Egap.Documents;

namespace Egap.Presentations.Tests;

/// <summary>Encrypted decks, the password to modify, saving as another kind of file, and the file's properties and show settings.</summary>
public sealed class FileTests
{
    private const string Password = "Egap pässword 1";

    [Fact]
    public void AnEncryptedDeckRoundTripsWithItsPassword()
    {
        Presentation presentation = Presentation.Create();
        Slide slide = presentation.Slides.Add(presentation.SlideMasters[0].GetLayout(SlideLayoutType.Title)!);
        ((AutoShape)slide.Shapes[0]).TextBody!.Text = "Secret";

        using MemoryStream saved = new();
        presentation.Save(saved, new PresentationSaveOptions { Password = Password });
        byte[] file = saved.ToArray();
        Assert.Equal((byte)0xD0, file[0]);
        Assert.DoesNotContain("Secret", Encoding.Latin1.GetString(file), StringComparison.Ordinal);

        Presentation back = Presentation.Load(new MemoryStream(file), new PresentationLoadOptions { Password = Password });
        Assert.Equal("Secret", ((AutoShape)back.Slides[0].Shapes[0]).TextBody!.Text);

        // Saved again without a password, it is a plain package.
        using MemoryStream plain = new();
        back.Save(plain);
        Assert.Equal((byte)'P', plain.ToArray()[0]);
    }

    [Fact]
    public void AWrongOrMissingPasswordIsADocumentPasswordException()
    {
        using MemoryStream saved = new();
        Presentation.Create().Save(saved, new PresentationSaveOptions { Password = Password });

        DocumentPasswordException missing = Assert.Throws<DocumentPasswordException>(() => Presentation.Load(new MemoryStream(saved.ToArray())));
        Assert.False(missing.WrongPassword);
        DocumentPasswordException wrong = Assert.Throws<DocumentPasswordException>(() => Presentation.Load(new MemoryStream(saved.ToArray()), new PresentationLoadOptions { Password = "nope" }));
        Assert.True(wrong.WrongPassword);
    }

    [Fact]
    public void AStandardEncryptedDeckLoads()
    {
        using MemoryStream package = new();
        Presentation.Create().Save(package);
        using MemoryStream encrypted = new();
        Egap.Documents.Office.OfficeEncryption.EncryptStandard(package.ToArray(), "pw", encrypted);

        Presentation back = Presentation.Load(new MemoryStream(encrypted.ToArray()), new PresentationLoadOptions { Password = "pw" });
        Assert.Equal(11, back.SlideMasters[0].Layouts.Count);
    }

    [Fact]
    public void AnOverlongPasswordIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Presentation.Create().Save(new MemoryStream(), new PresentationSaveOptions { Password = new string('x', 256) }));
    }

    [Fact]
    public void TheModifyPasswordIsSetVerifiedAndCleared()
    {
        Presentation presentation = Presentation.Create();
        Assert.False(presentation.HasModifyPassword);
        Assert.True(presentation.VerifyModifyPassword("anything"));

        presentation.SetModifyPassword("edit me");
        Presentation back = BuildingTests.Reload(presentation);
        Assert.True(back.HasModifyPassword);
        Assert.True(back.VerifyModifyPassword("edit me"));
        Assert.False(back.VerifyModifyPassword("Edit me"));
        Assert.False(back.VerifyModifyPassword(null));

        // CT_Presentation's order: the verifier after the default text style.
        using MemoryStream stream = new();
        back.Save(stream);
        string main = PptxBuilder.Entry(stream.ToArray(), "ppt/presentation.xml");
        Assert.True(main.IndexOf("defaultTextStyle", StringComparison.Ordinal) < main.IndexOf("modifyVerifier", StringComparison.Ordinal));

        back.SetModifyPassword(null);
        Assert.False(BuildingTests.Reload(back).HasModifyPassword);
    }

    [Fact]
    public void ALoadedModifyVerifierIsKeptAndChecked()
    {
        // PowerPoint 2010's form, by algorithm number: SHA-512 (14), 1 spin, over the UTF-16 password.
        byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] hash = Egap.Documents.DocumentProtection.ComputeHash("SHA-512", Encoding.Unicode.GetBytes("pw"), salt, 1);
        PptxBuilder builder = new()
        {
            PresentationTail = $"<p:modifyVerifier cryptProviderType=\"rsaAES\" cryptAlgorithmClass=\"hash\" cryptAlgorithmType=\"typeAny\" cryptAlgorithmSid=\"14\" spinCount=\"1\" saltData=\"{Convert.ToBase64String(salt)}\" hashData=\"{Convert.ToBase64String(hash)}\"/>",
        };
        builder.Slides.Add("");
        byte[] file = builder.Build();
        Presentation presentation = Presentation.Load(new MemoryStream(file));

        Assert.True(presentation.HasModifyPassword);
        Assert.True(presentation.VerifyModifyPassword("pw"));
        Assert.False(presentation.VerifyModifyPassword("px"));
        using MemoryStream saved = new();
        presentation.Save(saved);
        Assert.Contains("hashData=", PptxBuilder.Entry(saved.ToArray(), "ppt/presentation.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PresentationFormat.Potx, "application/vnd.openxmlformats-officedocument.presentationml.template.main+xml")]
    [InlineData(PresentationFormat.Ppsx, "application/vnd.openxmlformats-officedocument.presentationml.slideshow.main+xml")]
    [InlineData(PresentationFormat.Pptm, "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml")]
    [InlineData(PresentationFormat.Ppsm, "application/vnd.ms-powerpoint.slideshow.macroEnabled.main+xml")]
    public void SavingAsAnotherKindChangesTheMainContentType(PresentationFormat format, string contentType)
    {
        Presentation presentation = Presentation.Create();
        presentation.Format = format;
        using MemoryStream saved = new();
        presentation.Save(saved);

        Assert.Contains(contentType, PptxBuilder.Entry(saved.ToArray(), "[Content_Types].xml"), StringComparison.Ordinal);
        Assert.Equal(format, Presentation.Load(new MemoryStream(saved.ToArray())).Format);
    }

    [Fact]
    public void SavingAMacroDeckWithoutMacrosLeavesTheProjectOut()
    {
        byte[] file = MacroDeck();
        Presentation presentation = Presentation.Load(new MemoryStream(file));
        Assert.Equal(PresentationFormat.Pptm, presentation.Format);

        using MemoryStream pptx = new();
        presentation.Save(pptx, new PresentationSaveOptions { Format = PresentationFormat.Pptx });
        string[] names = [.. PptxBuilder.Entries(pptx.ToArray()).Select(e => e.Name)];
        Assert.DoesNotContain("ppt/vbaProject.bin", names);
        Assert.DoesNotContain("ppt/vbaData.xml", names);
        Assert.DoesNotContain("vbaProject", PptxBuilder.Entry(pptx.ToArray(), "ppt/_rels/presentation.xml.rels"), StringComparison.Ordinal);
        string types = PptxBuilder.Entry(pptx.ToArray(), "[Content_Types].xml");
        Assert.DoesNotContain("vbaProject", types, StringComparison.Ordinal);
        Assert.DoesNotContain("macroEnabled", types, StringComparison.Ordinal);
        Assert.Equal(PresentationFormat.Pptx, Presentation.Load(new MemoryStream(pptx.ToArray())).Format);

        // The presentation keeps them for a save with macros.
        using MemoryStream pptm = new();
        presentation.Save(pptm);
        Assert.Contains("ppt/vbaProject.bin", PptxBuilder.Entries(pptm.ToArray()).Select(e => e.Name));
    }

    [Fact]
    public void APathSaveTakesItsKindFromTheExtension()
    {
        string folder = Path.Combine(Path.GetTempPath(), "egap-pres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Presentation.Create().Save(Path.Combine(folder, "deck.potx"));
            Assert.Equal(PresentationFormat.Potx, Presentation.Load(Path.Combine(folder, "deck.potx")).Format);
            Assert.Throws<ArgumentException>(() => Presentation.Create().Save(Path.Combine(folder, "deck.txt")));
            Presentation.Create().Save(Path.Combine(folder, "deck.txt"), new PresentationSaveOptions { Format = PresentationFormat.Ppsx });
            Assert.Equal(PresentationFormat.Ppsx, Presentation.Load(Path.Combine(folder, "deck.txt")).Format);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PropertiesAreReadAndWrittenInPlace()
    {
        Presentation presentation = Presentation.Create();
        PresentationProperties properties = presentation.Properties;
        Assert.Equal("Presentation", properties.Title);
        properties.Title = "Quarterly";
        properties.Author = "Ada Example";
        properties.Keywords = "sales, q3";
        properties.Company = "Example Ltd";
        properties.Saved = new DateTimeOffset(2026, 11, 28, 9, 30, 0, TimeSpan.Zero);
        properties.SetCustom("Project", "Egap");
        properties.SetCustom("Gone", "soon");
        properties.SetCustom("gone", null);

        Presentation reloaded = BuildingTests.Reload(presentation);
        PresentationProperties back = reloaded.Properties;
        Assert.Equal("Quarterly", back.Title);
        Assert.Equal("Ada Example", back.Author);
        Assert.Equal("sales, q3", back.Keywords);
        Assert.Equal("Example Ltd", back.Company);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 9, 30, 0, TimeSpan.Zero), back.Saved);
        Assert.NotNull(back.Created);
        Assert.Equal(["Project"], back.CustomNames);
        Assert.Equal("Egap", back.GetCustom("project"));

        back.Author = null;
        Assert.Null(BuildingTests.Reload(reloaded).Properties.Author);
    }

    [Fact]
    public void ShowSettingsAreKept()
    {
        Presentation presentation = Presentation.Create();
        Assert.Equal(SlideShowType.Presented, presentation.SlideShow.Type);
        Assert.False(presentation.SlideShow.Loop);
        Assert.True(presentation.SlideShow.UseTimings);

        presentation.SlideShow.Type = SlideShowType.Kiosk;
        presentation.SlideShow.UseTimings = false;
        Presentation back = BuildingTests.Reload(presentation);
        Assert.Equal(SlideShowType.Kiosk, back.SlideShow.Type);
        Assert.True(back.SlideShow.Loop);
        Assert.False(back.SlideShow.UseTimings);

        back.SlideShow.Type = SlideShowType.Browsed;
        back.SlideShow.Loop = false;
        Presentation again = BuildingTests.Reload(back);
        Assert.Equal(SlideShowType.Browsed, again.SlideShow.Type);
        Assert.False(again.SlideShow.Loop);
    }

    /// <summary>A PPTM with a VBA project, its data part and their content types.</summary>
    private static byte[] MacroDeck()
    {
        PptxBuilder builder = new() { MainContentType = "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml" };
        builder.Slides.Add("");
        byte[] file = builder.Build();
        string rels = PptxBuilder.Entry(file, "ppt/_rels/presentation.xml.rels")
            .Replace("</Relationships>", "<Relationship Id=\"rIdVba\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\"/></Relationships>", StringComparison.Ordinal);
        string types = PptxBuilder.Entry(file, "[Content_Types].xml")
            .Replace("</Types>", "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/><Override PartName=\"/ppt/vbaData.xml\" ContentType=\"application/vnd.ms-word.vbaData+xml\"/></Types>", StringComparison.Ordinal);
        file = PptxBuilder.WithEntry(file, "ppt/_rels/presentation.xml.rels", Encoding.UTF8.GetBytes(rels));
        file = PptxBuilder.WithEntry(file, "[Content_Types].xml", Encoding.UTF8.GetBytes(types));
        file = PptxBuilder.WithEntry(file, "ppt/vbaProject.bin", [0xCC, 0x61, 0x00, 0x00]);
        file = PptxBuilder.WithEntry(file, "ppt/_rels/vbaProject.bin.rels", Encoding.UTF8.GetBytes(
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.microsoft.com/office/2006/relationships/wordVbaData\" Target=\"vbaData.xml\"/></Relationships>"));
        return PptxBuilder.WithEntry(file, "ppt/vbaData.xml", Encoding.UTF8.GetBytes("<wne:vbaSuppData xmlns:wne=\"http://schemas.microsoft.com/office/word/2006/wordml\"/>"));
    }
}
