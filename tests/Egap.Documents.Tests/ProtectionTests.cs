using System.IO.Compression;
using System.Text;

using Egap.Documents.Rtf;

namespace Egap.Documents.Tests;

public class ProtectionTests
{
    // What Word 365 wrote for a read-only document protected with the password "pw".
    private const string WordHash = "dh8AwFzCrxWoAj0ElATAU3Rub4z86eLVk77032Q+PtyLQHK9Zy/ANanmE1nP7H8whQSQvdHQYCtchypiPrjiJQ==";
    private const string WordSalt = "Kd9tq4lW1tpphvsDG1FGrQ==";

    // The same in RTF, with its own salt: Word's header, the hash and the salt.
    private const string WordRtfHash = "0200000078000000180000000e800000a08601004000000010000000280000006800000000000000"
        + "fd4fd57ec2ceffcda309e91210e6f5b21cdc06cf7ab6a00dc165cc33a9bea357f592eecaf06d7acf292186cc5f0e2eeb02aaa3c58f8c6ac2f5dc7cdd9618e1fc"
        + "b0860a42b8e57d55bfd8e7061909ed83";

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    [Fact]
    public void WordsHashOfAPasswordVerifies()
    {
        Document document = new();
        document.Protection.Set(ProtectionKind.ReadOnly, true, "SHA-512", Convert.FromBase64String(WordHash), Convert.FromBase64String(WordSalt), 100_000);
        Assert.True(document.Protection.Verify("pw"));
        Assert.False(document.Protection.Verify("pW"));
        Assert.False(document.Protection.Verify(""));
        Assert.False(document.Protection.Verify(null));
    }

    [Fact]
    public void LongUnicodePasswordsUseTheirFirst15CharactersAsWordDoes()
    {
        // Word 365 for "Pässwörd-ÆØÅ-1234567890-long".
        Document document = new();
        document.Protection.Set(
            ProtectionKind.ReadOnly,
            true,
            "SHA-512",
            Convert.FromBase64String("9L3mBBlXjckVAE0oS7mrhwRw52/7m5hWHkFROLRLu5LKc/RtjDL/dBd3GPsOViF04JdOOCQdbuI6YK/Ps7QYtA=="),
            Convert.FromBase64String("etWi1HR57bF5GH3WA5wHXA=="),
            100_000);
        Assert.True(document.Protection.Verify("Pässwörd-ÆØÅ-1234567890-long"));
        Assert.True(document.Protection.Verify("Pässwörd-ÆØÅ-12 and more"));
        Assert.False(document.Protection.Verify("Pässwörd-ÆØÅ-2"));
    }

    [Fact]
    public void ProtectMakesASha512HashThatVerifies()
    {
        Document document = new();
        document.Protection.Protect(ProtectionKind.Comments, "secret");
        DocumentProtection protection = document.Protection;
        Assert.Equal(ProtectionKind.Comments, protection.Kind);
        Assert.True(protection.IsEnforced);
        Assert.True(protection.HasPassword);
        Assert.Equal("SHA-512", protection.HashAlgorithm);
        Assert.Equal(64, protection.Hash.Length);
        Assert.Equal(16, protection.Salt.Length);
        Assert.Equal(DocumentProtection.DefaultSpinCount, protection.SpinCount);
        Assert.True(protection.Verify("secret"));
        Assert.False(protection.Verify("Secret"));

        protection.Unprotect();
        Assert.Equal(ProtectionKind.None, protection.Kind);
        Assert.False(protection.IsEnforced);
        Assert.False(protection.HasPassword);
        Assert.True(protection.Verify("anything"));
    }

    [Fact]
    public void ProtectWithoutAPasswordHasNoHash()
    {
        Document document = new();
        document.Protection.Protect(ProtectionKind.Forms);
        Assert.True(document.Protection.IsEnforced);
        Assert.False(document.Protection.HasPassword);
        Assert.True(document.Protection.Verify(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Protection.Protect(ProtectionKind.None, "x"));
    }

    [Fact]
    public void VerifyRefusesHugeSpinCountsAndUnknownAlgorithms()
    {
        Document document = new();
        document.Protection.Set(ProtectionKind.ReadOnly, true, "SHA-512", new byte[64], new byte[16], DocumentProtection.MaxSpinCount + 1);
        Assert.Throws<DocumentException>(() => document.Protection.Verify("pw"));
        document.Protection.Set(ProtectionKind.ReadOnly, true, "WHIRLPOOL", new byte[64], new byte[16], 10);
        Assert.Throws<DocumentException>(() => document.Protection.Verify("pw"));
    }

    [Fact]
    public void ThePlainPasswordIsTriedAfterWordsKey()
    {
        // ECMA-376's strict reading hashes the password's UTF-16 itself.
        byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] hash = DocumentProtection.ComputeHash("SHA-256", Encoding.Unicode.GetBytes("plain"), salt, 10);
        Document document = new();
        document.Protection.Set(ProtectionKind.ReadOnly, true, "SHA-256", hash, salt, 10, modern: true);
        Assert.True(document.Protection.Verify("plain"));
        Assert.False(document.Protection.Verify("plan"));
    }

    [Fact]
    public void DocxReadsWordsProtectionAndEditableRanges()
    {
        string settings = "<w:settings " + W + "><w:documentProtection w:edit=\"readOnly\" w:enforcement=\"1\" w:cryptProviderType=\"rsaAES\" "
            + "w:cryptAlgorithmClass=\"hash\" w:cryptAlgorithmType=\"typeAny\" w:cryptAlgorithmSid=\"14\" w:cryptSpinCount=\"100000\" "
            + "w:hash=\"" + WordHash + "\" w:salt=\"" + WordSalt + "\"/></w:settings>";
        string body = "<w:document " + W + "><w:body>"
            + "<w:p><w:r><w:t>Locked.</w:t></w:r></w:p>"
            + "<w:p><w:permStart w:id=\"782849113\" w:edGrp=\"everyone\"/><w:r><w:t>Open to all.</w:t></w:r></w:p>"
            + "<w:p><w:permStart w:id=\"1359559185\" w:ed=\"EXAMPLE\\someone\"/><w:permEnd w:id=\"782849113\"/><w:r><w:t>Open to one.</w:t></w:r></w:p>"
            + "<w:p><w:permEnd w:id=\"1359559185\"/><w:r><w:t>Locked.</w:t></w:r></w:p>"
            + "</w:body></w:document>";
        Document document = DocxFormat.Load(Package(body, settings));
        Assert.Equal(ProtectionKind.ReadOnly, document.Protection.Kind);
        Assert.True(document.Protection.IsEnforced);
        Assert.True(document.Protection.Verify("pw"));

        List<EditableRangeStart> starts = [.. document.Descendants<EditableRangeStart>()];
        Assert.Equal(2, starts.Count);
        Assert.Equal(782849113, starts[0].Id);
        Assert.Equal(EditorGroup.Everyone, starts[0].Group);
        Assert.Null(starts[0].Editor);
        Assert.Equal("EXAMPLE\\someone", starts[1].Editor);
        Assert.Equal(EditorGroup.None, starts[1].Group);
        Assert.Equal([782849113, 1359559185], document.Descendants<EditableRangeEnd>().Select(e => e.Id));

        // Written back unchanged: same hash, same ranges.
        MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        string savedSettings = Part(saved, "word/settings.xml");
        Assert.Contains("w:hash=\"" + WordHash + "\"", savedSettings, StringComparison.Ordinal);
        Assert.Contains("w:cryptAlgorithmSid=\"14\"", savedSettings, StringComparison.Ordinal);
        string savedBody = Part(saved, "word/document.xml");
        Assert.Contains("<w:permStart w:id=\"782849113\" w:edGrp=\"everyone\" />", savedBody, StringComparison.Ordinal);
        Assert.Contains("w:ed=\"EXAMPLE\\someone\"", savedBody, StringComparison.Ordinal);
        Assert.Contains("<w:permEnd w:id=\"1359559185\" />", savedBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("readOnly", ProtectionKind.ReadOnly)]
    [InlineData("comments", ProtectionKind.Comments)]
    [InlineData("trackedChanges", ProtectionKind.TrackedChanges)]
    [InlineData("forms", ProtectionKind.Forms)]
    public void DocxReadsEveryKindAndTheNewerAttributes(string edit, ProtectionKind kind)
    {
        byte[] salt = new byte[16];
        byte[] hash = DocumentProtection.ComputeHash("SHA-256", DocumentProtection.LegacyKey("pw"), salt, 5);
        string settings = "<w:settings " + W + "><w:documentProtection w:edit=\"" + edit + "\" w:enforcement=\"true\" w:algorithmName=\"SHA-256\" "
            + "w:hashValue=\"" + Convert.ToBase64String(hash) + "\" w:saltValue=\"" + Convert.ToBase64String(salt) + "\" w:spinCount=\"5\"/></w:settings>";
        Document document = DocxFormat.Load(Package("<w:document " + W + "><w:body><w:p/></w:body></w:document>", settings));
        Assert.Equal(kind, document.Protection.Kind);
        Assert.Equal("SHA-256", document.Protection.HashAlgorithm);
        Assert.True(document.Protection.Verify("pw"));

        // The newer attributes are written back as they came.
        document.Protection.Set(kind, true, "SHA-256", hash, salt, 5, modern: true);
        MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        Assert.Contains("w:algorithmName=\"SHA-256\"", Part(saved, "word/settings.xml"), StringComparison.Ordinal);
        Assert.True(DocxFormat.Load(new MemoryStream(saved.ToArray())).Protection.Verify("pw"));
    }

    [Fact]
    public void DocxWritesANewProtectionAndUnprotectRemovesIt()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.Write("Locked. ");
        EditableRangeStart start = builder.StartEditableRange();
        builder.Write("Open.");
        builder.EndEditableRange(start);
        EditableRangeStart user = builder.StartEditableRange("someone@example.com");
        builder.Write("Theirs.");
        builder.EndEditableRange(user);
        Assert.NotEqual(start.Id, user.Id);
        document.Protection.Protect(ProtectionKind.ReadOnly, "pw");

        MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        string settings = Part(saved, "word/settings.xml");
        Assert.Contains("w:edit=\"readOnly\" w:enforcement=\"1\" w:cryptProviderType=\"rsaAES\"", settings, StringComparison.Ordinal);
        Document read = DocxFormat.Load(new MemoryStream(saved.ToArray()));
        Assert.True(read.Protection.Verify("pw"));
        Assert.Equal(2, read.Descendants<EditableRangeStart>().Count());
        Assert.Equal("someone@example.com", read.Descendants<EditableRangeStart>().Last().Editor);

        read.Protection.Unprotect();
        MemoryStream again = new();
        DocxFormat.Save(read, again);
        Assert.DoesNotContain("documentProtection", Part(again, "word/settings.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void RtfReadsWordsProtection()
    {
        string rtf = @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}{\*\protusertbl{EXAMPLE\'5csomeone}}{\info{\*\passwordhash " + WordRtfHash + @"}}"
            + @"\formshade\annotprot\readprot\enforceprot1\protlevel3 "
            + @"Locked.\par {\*\protstart ffff010059540000}Open to all.\par {\*\protstart 01000100113a0000}{\*\protend ffff010059540000}Open to one.\par "
            + @"{\*\protend 01000100113a0000}Locked.\par}";
        Document document = RtfFormat.Parse(rtf, out _);
        Assert.Equal(ProtectionKind.ReadOnly, document.Protection.Kind);
        Assert.True(document.Protection.IsEnforced);
        Assert.Equal("SHA-512", document.Protection.HashAlgorithm);
        Assert.Equal(100_000, document.Protection.SpinCount);
        Assert.True(document.Protection.Verify("pw"));

        List<EditableRangeStart> starts = [.. document.Descendants<EditableRangeStart>()];
        Assert.Equal(2, starts.Count);
        Assert.Equal(0x5459, starts[0].Id);
        Assert.Equal(EditorGroup.Everyone, starts[0].Group);
        Assert.Equal("EXAMPLE\\someone", starts[1].Editor);
        Assert.Equal([0x5459, 0x3A11], document.Descendants<EditableRangeEnd>().Select(e => e.Id));
    }

    [Theory]
    [InlineData(@"\revprot\enforceprot1\protlevel3", ProtectionKind.TrackedChanges)]
    [InlineData(@"\annotprot\enforceprot1\protlevel3", ProtectionKind.Comments)]
    [InlineData(@"\formprot\allprot", ProtectionKind.Forms)]
    [InlineData(@"\enforceprot1\protlevel2", ProtectionKind.Forms)]
    public void RtfReadsEveryKind(string words, ProtectionKind kind)
    {
        Document document = RtfFormat.Parse(@"{\rtf1\ansi " + words + @" x\par}", out _);
        Assert.Equal(kind, document.Protection.Kind);
        Assert.True(document.Protection.IsEnforced);
        Assert.False(document.Protection.HasPassword);
    }

    [Theory]
    [InlineData(ProtectionKind.ReadOnly, @"\annotprot\readprot\enforceprot1\protlevel3")]
    [InlineData(ProtectionKind.Comments, @"\annotprot\enforceprot1\protlevel3")]
    [InlineData(ProtectionKind.TrackedChanges, @"\revprot\enforceprot1\protlevel3")]
    [InlineData(ProtectionKind.Forms, @"\formprot\allprot")]
    public void RtfWritesProtectionAsWordDoes(ProtectionKind kind, string words)
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.Write("Locked. ");
        EditableRangeStart start = builder.StartEditableRange(EditorGroup.Editors);
        builder.Write("Open.");
        builder.EndEditableRange(start);
        EditableRangeStart user = builder.StartEditableRange("EXAMPLE\\someone");
        builder.Write("Theirs.");
        builder.EndEditableRange(user);
        document.Protection.Protect(kind, "pw");

        MemoryStream stream = new();
        RtfFormat.Save(document, stream);
        string rtf = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Contains(words, rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\*\protusertbl{EXAMPLE\'5csomeone}}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\*\protstart fbff010001000000}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\*\protend 0100010002000000}", rtf, StringComparison.Ordinal);
        Assert.Contains(@"{\*\passwordhash 0200000078000000180000000e800000a0860100400000001000000028000000680000000000000", rtf, StringComparison.Ordinal);

        Document read = RtfFormat.Parse(rtf, out _);
        Assert.Equal(kind, read.Protection.Kind);
        Assert.True(read.Protection.Verify("pw"));
        Assert.Equal(document.Protection.Hash.ToArray(), read.Protection.Hash.ToArray());
        Assert.Equal(EditorGroup.Editors, read.Descendants<EditableRangeStart>().First().Group);
        Assert.Equal("EXAMPLE\\someone", read.Descendants<EditableRangeStart>().Last().Editor);
    }

    [Fact]
    public void ProtectionAndRangesSurviveCloneAndConversion()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        EditableRangeStart start = builder.StartEditableRange();
        start.FirstColumn = 1;
        start.LastColumn = 2;
        builder.Write("Open.");
        builder.EndEditableRange(start);
        document.Protection.Protect(ProtectionKind.TrackedChanges, "pw");

        Document copy = document.Clone();
        Assert.True(copy.Protection.Verify("pw"));
        EditableRangeStart copied = copy.Descendants<EditableRangeStart>().Single();
        Assert.Equal((1, 2), (copied.FirstColumn, copied.LastColumn));

        MemoryStream rtf = new();
        RtfFormat.Save(document, rtf);
        MemoryStream docx = new();
        DocxFormat.Save(RtfFormat.Load(new MemoryStream(rtf.ToArray())), docx);
        Document back = DocxFormat.Load(new MemoryStream(docx.ToArray()));
        Assert.Equal(ProtectionKind.TrackedChanges, back.Protection.Kind);
        Assert.True(back.Protection.Verify("pw"));
        Assert.Single(back.Descendants<EditableRangeStart>());
    }

    [Fact]
    public void EditableRangeStartTakesAGroupOrAnEditor()
    {
        Document document = new();
        EditableRangeStart start = new(document, 1, EditorGroup.Owners);
        start.Editor = "someone";
        Assert.Equal(EditorGroup.None, start.Group);
        start.Group = EditorGroup.Everyone;
        Assert.Null(start.Editor);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditableRangeStart(document, 1, EditorGroup.None));
        Assert.Throws<ArgumentException>(() => new EditableRangeStart(document, 1, ""));
    }

    private static MemoryStream Package(string documentXml, string settingsXml)
    {
        Docx.OpcPackageWriter writer = new();
        writer.AddPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", Encoding.UTF8.GetBytes(documentXml));
        writer.AddPart("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml", Encoding.UTF8.GetBytes(settingsXml));
        writer.AddRelationship("/", "rId1", Docx.OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId1", Docx.OpcRelationshipTypes.Settings, "settings.xml");
        MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return stream;
    }

    private static string Part(MemoryStream package, string name)
    {
        using ZipArchive zip = new(new MemoryStream(package.ToArray()), ZipArchiveMode.Read);
        using StreamReader reader = new(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
