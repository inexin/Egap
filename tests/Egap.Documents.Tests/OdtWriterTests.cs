using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Odf;

namespace Egap.Documents.Tests;

/// <summary>The ODT writer: packages, kinds and versions, encryption, protection, text, fields, changes, notes, tables and kept markup.</summary>
public sealed class OdtWriterTests
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Config = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    public static TheoryData<string> DocxSeeds =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-xml"), "*.xml").Select(f => Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-package"), "*.docx").Select(f => Path.GetFileName(f)),
    ];

    public static TheoryData<string> OdtSeeds => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "odt")).Select(f => Path.GetFileName(f))];

    // ---- Helpers ----

    private static byte[] Save(Document document, out IReadOnlyList<ConversionWarning> warnings, OdtSaveOptions? options = null)
    {
        using MemoryStream stream = new();
        warnings = OdtFormat.Save(document, stream, options);
        byte[] bytes = stream.ToArray();
        if (Environment.GetEnvironmentVariable("EGAP_ODT_OUTPUT") is { Length: > 0 } folder)
        {
            // For opening every file the tests write in other applications.
            bool flat = options?.Kind is OdtKind.FlatDocument or OdtKind.FlatTemplate;
            File.WriteAllBytes(Path.Combine(folder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + (flat ? ".fodt" : ".odt")), bytes);
        }

        return bytes;
    }

    private static byte[] Save(Document document, OdtSaveOptions? options = null) => Save(document, out _, options);

    private static Document Reread(Document document, out IReadOnlyList<ConversionWarning> warnings, OdtSaveOptions? options = null, string? password = null)
    {
        using MemoryStream stream = new(Save(document, options));
        return OdtFormat.Load(stream, out warnings, new OdtLoadOptions { Password = password });
    }

    private static Document Reread(Document document, OdtSaveOptions? options = null, string? password = null) => Reread(document, out _, options, password);

    /// <summary>The XML of a part of a written package.</summary>
    private static XElement Part(byte[] package, string name)
    {
        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        using Stream stream = zip.GetEntry(name)!.Open();
        return XElement.Load(stream);
    }

    private static XElement Content(Document document, OdtSaveOptions? options = null) => Part(Save(document, options), "content.xml");

    private static XElement Content(Document document, out IReadOnlyList<ConversionWarning> warnings) => Part(Save(document, out warnings), "content.xml");

    private static XElement Body(XElement content) => content.Element(Office + "body")!.Element(Office + "text")!;

    private static Document LoadDocx(string name)
    {
        string xml = Path.Combine(AppContext.BaseDirectory, "files", "docx-xml", name);
        return File.Exists(xml)
            ? DocxFormat.Load(DocxSeedPackage.Wrap(File.ReadAllBytes(xml)))
            : DocxFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "docx-package", name));
    }

    private static Document NewDocument(out Body body)
    {
        Document document = new();
        body = document.AddSection().Body;
        return document;
    }

    private static void Same(string expected, string actual, string name)
    {
        if (expected != actual && Environment.GetEnvironmentVariable("EGAP_ODT_DUMPS") is { Length: > 0 } folder)
        {
            File.WriteAllText(Path.Combine(folder, name + ".a.txt"), expected);
            File.WriteAllText(Path.Combine(folder, name + ".b.txt"), actual);
        }

        Assert.Equal(expected, actual);
    }

    // ---- Round trips ----

    [Theory]
    [MemberData(nameof(OdtSeeds))]
    public void SeedsRoundTrip(string name)
    {
        Document first = OdtFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "odt", name));
        Document second = Reread(first);
        Document third = Reread(second);
        Same(RtfWriterTests.Dump(second), RtfWriterTests.Dump(third), "s-" + name);
        Assert.Equal(first.GetText(), second.GetText());
    }

    [Theory]
    [MemberData(nameof(DocxSeeds))]
    public void DocxSavedAsOdtRoundTrips(string name)
    {
        Document first = Reread(LoadDocx(name));
        Document second = Reread(first);
        Same(RtfWriterTests.Dump(first), RtfWriterTests.Dump(second), "r-" + name);
    }

    // ---- Packages, kinds and versions ----

    [Theory]
    [InlineData(OdtKind.Document, "application/vnd.oasis.opendocument.text")]
    [InlineData(OdtKind.Template, "application/vnd.oasis.opendocument.text-template")]
    public void PackageStartsWithItsStoredMediaType(OdtKind kind, string mediaType)
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Hello");
        byte[] bytes = Save(document, new OdtSaveOptions { Kind = kind });

        // The mimetype entry comes first, stored, so the type is at a fixed place in the file (ODF 1.3 Part 2, 3.3).
        Assert.Equal("mimetype", Encoding.ASCII.GetString(bytes, 30, 8));
        Assert.Equal(mediaType, Encoding.ASCII.GetString(bytes, 38, mediaType.Length));
        using ZipArchive zip = new(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(["mimetype", "content.xml", "styles.xml", "meta.xml", "settings.xml", "META-INF/manifest.xml"], zip.Entries.Select(e => e.FullName));
        Assert.Equal("Hello", OdtFormat.Load(new MemoryStream(bytes)).GetText().Trim());
    }

    [Theory]
    [InlineData(OdfVersion.Odf12, "1.2")]
    [InlineData(OdfVersion.Odf13, "1.3")]
    [InlineData(OdfVersion.Odf14, "1.4")]
    public void VersionIsWrittenOnEveryPart(OdfVersion version, string number)
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Hello");
        byte[] bytes = Save(document, new OdtSaveOptions { Version = version });
        foreach (string part in new[] { "content.xml", "styles.xml", "meta.xml", "settings.xml" })
        {
            Assert.Equal(number, Part(bytes, part).Attribute(Office + "version")?.Value);
        }

        Assert.Equal(number, Part(bytes, "META-INF/manifest.xml").Attribute(XName.Get("version", "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"))?.Value);
    }

    [Theory]
    [InlineData(OdtKind.FlatDocument, "application/vnd.oasis.opendocument.text")]
    [InlineData(OdtKind.FlatTemplate, "application/vnd.oasis.opendocument.text-template")]
    public void FlatFileHoldsPicturesInTheXml(OdtKind kind, string mediaType)
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Picture: ");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        paragraph.AddPicture(new ImageData(png, "image/png"), 20, 10);
        byte[] bytes = Save(document, new OdtSaveOptions { Kind = kind });
        XElement root = XElement.Load(new MemoryStream(bytes));
        Assert.Equal(mediaType, root.Attribute(Office + "mimetype")?.Value);
        XElement image = root.Descendants(Draw + "image").Single();
        Assert.Null(image.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink")));
        Assert.Equal(png, Convert.FromBase64String(image.Element(Office + "binary-data")!.Value));

        Picture read = OdtFormat.Load(new MemoryStream(bytes)).Descendants<Picture>().Single();
        Assert.Equal(png, read.Image.Bytes.ToArray());
        Assert.Equal(20, read.Width, 2);
    }

    [Fact]
    public void KindFollowsTheFileName()
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Hello");
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string template = Path.Combine(folder, "a.ott");
            string flat = Path.Combine(folder, "a.fodt");
            OdtFormat.Save(document, template);
            document.Save(flat);
            Assert.Equal("application/vnd.oasis.opendocument.text-template", Encoding.ASCII.GetString(File.ReadAllBytes(template), 38, 48));
            Assert.StartsWith("<?xml", File.ReadAllText(flat), StringComparison.Ordinal);
            Assert.Equal("Hello", Document.Load(flat).GetText().Trim());
            Assert.Equal("Hello", Document.Load(template).GetText().Trim());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PasswordOnAFlatFileIsAnError()
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Hello");
        Assert.Throws<ArgumentException>(() => Save(document, new OdtSaveOptions { Kind = OdtKind.FlatDocument, Password = "secret" }));
    }

    [Theory]
    [InlineData(OdtEncryption.AesCbc)]
    [InlineData(OdtEncryption.AesGcm)]
    public void PasswordEncryptsThePackage(OdtEncryption encryption)
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Secret text");
        byte[] bytes = Save(document, new OdtSaveOptions { Password = "pässword", Encryption = encryption });
        Assert.DoesNotContain("Secret text", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Throws<DocumentPasswordException>(() => OdtFormat.Load(new MemoryStream(bytes)));
        Assert.Throws<DocumentPasswordException>(() => OdtFormat.Load(new MemoryStream(bytes), new OdtLoadOptions { Password = "wrong" }));
        Assert.Equal("Secret text", OdtFormat.Load(new MemoryStream(bytes), new OdtLoadOptions { Password = "pässword" }).GetText().Trim());
    }

    // ---- Protection ----

    [Theory]
    [InlineData(ProtectionKind.ReadOnly)]
    [InlineData(ProtectionKind.Comments)]
    [InlineData(ProtectionKind.TrackedChanges)]
    [InlineData(ProtectionKind.Forms)]
    public void ProtectionRoundTrips(ProtectionKind kind)
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Protected");
        document.Protection.Protect(kind, "pass");
        Document read = Reread(document);
        Assert.Equal(kind, read.Protection.Kind);
        Assert.True(read.Protection.IsEnforced);
        Assert.True(read.Protection.Verify("pass"));
        Assert.False(read.Protection.Verify("other"));
        Assert.Equal("Protected", read.GetText().Trim());
    }

    [Fact]
    public void ProtectionIsWrittenAsLibreOfficeWritesIt()
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Form");
        document.Protection.Protect(ProtectionKind.Forms);
        byte[] bytes = Save(document);
        XElement settings = Part(bytes, "settings.xml");
        string? Item(string name) => settings.Descendants(Config + "config-item").FirstOrDefault(i => i.Attribute(Config + "name")?.Value == name)?.Value;
        Assert.Equal("true", Item("ProtectForm"));
        Assert.Equal("false", Item("LoadReadonly"));

        // The text is in a protected section, so only form fields can be changed.
        XElement section = Body(Part(bytes, "content.xml")).Element(Text + "section")!;
        Assert.Equal("true", section.Attribute(Text + "protected")?.Value);
        Assert.Equal("Form", section.Value);

        document.Protection.Protect(ProtectionKind.ReadOnly);
        settings = Part(Save(document), "settings.xml");
        Assert.Equal("true", Item("LoadReadonly"));
    }

    [Fact]
    public void LibreOfficeProtectionSettingsAreRead()
    {
        // What LibreOffice keeps of Word's protection: no password and no kind of its own.
        Document document = NewDocument(out Body body);
        body.AddParagraph("Read only");
        document.Protection.Protect(ProtectionKind.ReadOnly, "pass");
        byte[] bytes = Save(document);
        using MemoryStream edited = new();
        using (ZipArchive source = new(new MemoryStream(bytes), ZipArchiveMode.Read))
        using (ZipArchive target = new(edited, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream input = entry.Open();
                using Stream output = target.CreateEntry(entry.FullName, entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                if (entry.FullName == "settings.xml")
                {
                    XElement settings = XElement.Load(input);
                    settings.Descendants(Config + "config-item-set").Where(s => s.Attribute(Config + "name")?.Value == "egap:document-protection").Remove();
                    settings.Save(output);
                }
                else
                {
                    input.CopyTo(output);
                }
            }
        }

        Document read = OdtFormat.Load(new MemoryStream(edited.ToArray()));
        Assert.Equal(ProtectionKind.ReadOnly, read.Protection.Kind);
        Assert.True(read.Protection.IsEnforced);
        Assert.False(read.Protection.HasPassword);
    }

    [Fact]
    public void EditableRangesAreLibreOfficePermissionBookmarks()
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Locked ");
        paragraph.Inlines.Add(new EditableRangeStart(document, 1, EditorGroup.Editors));
        paragraph.AddRun("open");
        paragraph.Inlines.Add(new EditableRangeEnd(document, 1));
        paragraph.Inlines.Add(new EditableRangeStart(document, 2, "alice@example.com"));
        paragraph.AddRun(" mine");
        paragraph.Inlines.Add(new EditableRangeEnd(document, 2));
        document.Protection.Protect(ProtectionKind.ReadOnly);

        XElement p = Body(Content(document)).Element(Text + "p")!;
        Assert.Equal(["permission-for-group:1:editors", "permission-for-user:2:alice@example.com"],
            p.Elements(Text + "bookmark-start").Select(b => b.Attribute(Text + "name")!.Value));

        Paragraph read = Reread(document).Descendants<Paragraph>().First();
        EditableRangeStart[] starts = [.. read.Inlines.OfType<EditableRangeStart>()];
        Assert.Equal(2, starts.Length);
        Assert.Equal(EditorGroup.Editors, starts[0].Group);
        Assert.Equal("alice@example.com", starts[1].Editor);
        Assert.Equal([1, 2], read.Inlines.OfType<EditableRangeEnd>().Select(e => e.Id));
        Assert.Empty(read.Inlines.OfType<BookmarkStart>());
    }

    // ---- Text ----

    [Fact]
    public void WhiteSpaceIsKept()
    {
        Document document = NewDocument(out Body body);
        string[] texts = [" leading", "two  spaces", "trailing ", "tab\there", "   ", "a \t b"];
        foreach (string text in texts)
        {
            body.AddParagraph(text);
        }

        XElement first = Body(Content(document)).Element(Text + "p")!;
        Assert.Equal(Text + "s", ((XElement)first.FirstNode!).Name);
        Assert.Equal(texts, Reread(document).Descendants<Paragraph>().Select(p => p.GetText()));
    }

    [Fact]
    public void CharactersXmlCannotHoldAreLeftOut()
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("a\u0001b￾c\uD800d");
        Assert.Equal("abc�d", Reread(document).Descendants<Paragraph>().Single().GetText());
    }

    [Fact]
    public void RunFormattingAndStylesRoundTrip()
    {
        Document document = NewDocument(out Body body);
        ParagraphStyle heading = document.Styles.AddParagraphStyle("MyHeading", "My Heading");
        heading.ParagraphFormat.OutlineLevel = 0;
        heading.RunFormat.Bold = true;
        Paragraph title = body.AddParagraph("Title");
        title.Style = heading;
        Paragraph paragraph = body.AddParagraph();
        paragraph.AddRun("plain ");
        paragraph.AddRun("red", new RunFormat { Color = DocumentColor.FromHex("FF0000"), Italic = true, Size = 14 });
        paragraph.Format.Alignment = ParagraphAlignment.Center;
        paragraph.Format.SpaceAfter = 12;

        XElement h = Body(Content(document)).Element(Text + "h")!;
        Assert.Equal("1", h.Attribute(Text + "outline-level")?.Value);
        Document read = Reread(document);
        Paragraph[] paragraphs = [.. read.Descendants<Paragraph>()];
        Assert.Equal("My Heading", paragraphs[0].Style?.Name);
        Assert.Equal(0, paragraphs[0].GetEffectiveFormat().OutlineLevel);
        Run red = paragraphs[1].Inlines.OfType<Run>().Single(r => r.Text == "red");
        Assert.Equal(true, red.Format.Italic);
        Assert.Equal(14, red.Format.Size);
        Assert.Equal(DocumentColor.FromHex("FF0000"), red.Format.Color);
        Assert.Equal(ParagraphAlignment.Center, paragraphs[1].Format.Alignment);
        Assert.Equal(12, paragraphs[1].Format.SpaceAfter);
    }

    [Fact]
    public void ListsContinueAcrossOtherParagraphs()
    {
        Document document = NewDocument(out Body body);
        List list = document.Lists.AddNumbered();
        for (int i = 0; i < 2; i++)
        {
            Paragraph item = body.AddParagraph("item " + i);
            item.Format.List = list;
            item.Format.ListLevel = 0;
        }

        body.AddParagraph("between");
        Paragraph third = body.AddParagraph("item 2");
        third.Format.List = list;
        third.Format.ListLevel = 1;

        XElement[] lists = [.. Body(Content(document)).Elements(Text + "list")];
        Assert.Equal(2, lists.Length);
        Assert.Equal(lists[0].Attribute(XNamespace.Xml + "id")?.Value, lists[1].Attribute(Text + "continue-list")?.Value);
        Paragraph[] read = [.. Reread(document).Descendants<Paragraph>()];
        Assert.Same(read[0].Format.List, read[3].Format.List);
        Assert.Equal(1, read[3].Format.ListLevel);
        Assert.Null(read[2].Format.List);
    }

    [Fact]
    public void ListsCountFromZeroAndShareTheirDefinitionsCounters()
    {
        Document document = NewDocument(out Body body);
        List first = document.Lists.AddNumbered();
        first.Definition.Levels[0].StartAt = 0;
        List second = document.Lists.Add(first.Definition);
        second.Overrides[0].StartAt = 0;
        foreach ((string text, List list) in (ReadOnlySpan<(string, List)>)[("a", first), ("b", first), ("c", second), ("d", first)])
        {
            Paragraph item = body.AddParagraph(text);
            item.Format.List = list;
            item.Format.ListLevel = 0;
        }

        Assert.Equal(["0.", "1.", "0.", "1."], document.Descendants<Paragraph>().Select(p => p.ListLabel!.Text));
        Document read = Reread(document);
        Assert.Equal(["0.", "1.", "0.", "1."], read.Descendants<Paragraph>().Select(p => p.ListLabel!.Text));
    }

    // ---- Fields, notes, comments and changes ----

    [Fact]
    public void FieldsOdfHasAreFieldsAgain()
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Page ");
        paragraph.AddField("PAGE", "1");
        paragraph.AddRun(" of ");
        paragraph.AddField("NUMPAGES \\* roman", "i");
        paragraph.AddRun(" ");
        paragraph.Inlines.Add(new BookmarkStart(document, "target"));
        paragraph.AddRun("here");
        paragraph.Inlines.Add(new BookmarkEnd(document, "target"));
        paragraph.AddField("PAGEREF target \\h", "1");
        paragraph.AddField("QUOTE \"odd\"", "odd");

        XElement p = Body(Content(document, out IReadOnlyList<ConversionWarning> warnings)).Element(Text + "p")!;
        Assert.NotNull(p.Element(Text + "page-number"));
        Assert.Equal("i", p.Element(Text + "page-count")?.Attribute(Style + "num-format")?.Value);
        Assert.Equal("page", p.Element(Text + "bookmark-ref")?.Attribute(Text + "reference-format")?.Value);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.FieldsFlattened);

        Document read = Reread(document);
        Assert.Equal(["PAGE", "NUMPAGES \\* roman", "PAGEREF target \\h"], read.Descendants<FieldStart>().Select(f => f.GetField()!.Code));
        Assert.EndsWith("odd", read.GetText().Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void DateAndTimePicturesAreDataStyles()
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Saved ");
        paragraph.AddField("SAVEDATE \\@ \"dddd, d MMMM yyyy 'at' h:mm AM/PM\"", "Monday, 4 August 2022 at 7:10 AM");
        paragraph.AddRun(" today ");
        paragraph.AddField("DATE \\@ \"dd.MM.yy\"", "05.11.26");
        paragraph.AddRun(" now ");
        paragraph.AddField("TIME \\@ \"HH:mm\"", "07:34");

        XElement content = Content(document, out _);
        XElement p = Body(content).Element(Text + "p")!;
        XNamespace number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
        string? saved = p.Element(Text + "modification-date")?.Attribute(Style + "data-style-name")?.Value;
        XElement? style = content.Descendants(number + "date-style").FirstOrDefault(s => s.Attribute(Style + "name")?.Value == saved);
        Assert.NotNull(style);
        Assert.Equal(["day-of-week", "text", "day", "text", "month", "text", "year", "text", "hours", "text", "minutes", "text", "am-pm"],
            style.Elements().Select(e => e.Name.LocalName));
        Assert.NotNull(content.Descendants(number + "time-style").SingleOrDefault());

        Document read = Reread(document);
        Assert.Equal(["SAVEDATE \\@ \"dddd, d MMMM yyyy 'at' h:mm AM/PM\"", "DATE \\@ \"dd.MM.yy\"", "TIME \\@ \"HH:mm\""],
            read.Descendants<FieldStart>().Select(f => f.GetField()!.Code));
    }

    [Theory]
    [InlineData("TIME \\@ \"h:mm AM/PM\"", null)]
    [InlineData("TIME \\@ \"h:mm:ss a/p\"", "TIME \\@ \"h:mm:ss AM/PM\"")]
    [InlineData("TIME \\@ \"'Day' HH:mm\"", null)]
    public void TwelveHourAndQuotedTimePicturesAreTimeStyles(string code, string? reread)
    {
        // ODF has one AM/PM marker, so Word's A/P comes back as AM/PM.
        Document document = NewDocument(out Body body);
        body.AddParagraph("Now ").AddField(code, "7:34 AM");

        XElement content = Content(document, out _);
        XNamespace number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
        Assert.NotNull(Body(content).Descendants(Text + "time").SingleOrDefault());
        Assert.NotNull(content.Descendants(number + "time-style").SingleOrDefault());
        Assert.Empty(content.Descendants(number + "date-style"));
        Assert.Equal(reread ?? code, Reread(document).Descendants<FieldStart>().Single().GetField()!.Code);
    }

    [Fact]
    public void NotesAndNoteReferencesRoundTrip()
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Text");
        paragraph.Inlines.Add(new BookmarkStart(document, "note1"));
        paragraph.AddFootnote("The note.");
        paragraph.Inlines.Add(new BookmarkEnd(document, "note1"));
        paragraph.AddFootnote("An endnote.", NoteKind.Endnote);
        body.AddParagraph("See note ").AddField("NOTEREF note1 \\h", "1");

        XElement text = Body(Content(document));
        Assert.Equal("note1", text.Descendants(Text + "note").First().Attribute(Text + "id")?.Value);
        Assert.Equal("endnote", text.Descendants(Text + "note").Last().Attribute(Text + "note-class")?.Value);
        Assert.Equal("note1", text.Descendants(Text + "note-ref").Single().Attribute(Text + "ref-name")?.Value);

        Document read = Reread(document);
        Footnote[] notes = [.. read.Descendants<Footnote>()];
        Assert.Equal([NoteKind.Footnote, NoteKind.Endnote], notes.Select(n => n.Kind));
        Assert.Equal("The note.", notes[0].Story.GetText().Trim());
        Assert.Contains(read.Descendants<FieldStart>(), f => f.GetField()!.Code.StartsWith("NOTEREF note1", StringComparison.Ordinal));
    }

    [Fact]
    public void CommentsWithRangesRoundTrip()
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Before ");
        Comment comment = new(document, "Ann", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)) { Initials = "A", Done = true };
        comment.AddParagraph("A remark");
        paragraph.Inlines.Add(new CommentRangeStart(document, comment));
        paragraph.AddRun("commented");
        paragraph.Inlines.Add(new CommentRangeEnd(document, comment));
        paragraph.Inlines.Add(comment);

        XElement p = Body(Content(document)).Element(Text + "p")!;
        Assert.Single(p.Elements(Office + "annotation"));
        Assert.Single(p.Elements(Office + "annotation-end"));

        Document read = Reread(document);
        Comment readComment = read.Descendants<Comment>().Single();
        Assert.Equal("Ann", readComment.Author);
        Assert.Equal(comment.Date, readComment.Date);
        Assert.Equal("A remark", readComment.Story.GetText().Trim());
        Assert.Contains(read.Descendants<CommentRangeStart>(), r => r.CommentId == readComment.Id);
    }

    [Fact]
    public void TrackedChangesRoundTrip()
    {
        Document document = NewDocument(out Body body);
        document.Settings.TrackRevisions = true;
        DateTimeOffset when = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        Paragraph first = body.AddParagraph("Kept ");
        first.AddRun("inserted").Revision = new Revision(RevisionKind.Insertion, "Ann", when);
        first.AddRun(" gone").Revision = new Revision(RevisionKind.Deletion, "Bob", when);
        first.MarkRevision = new Revision(RevisionKind.Deletion, "Bob", when);
        body.AddParagraph("Joined");

        XElement text = Body(Content(document));
        XElement tracked = text.Element(Text + "tracked-changes")!;
        Assert.Equal("true", tracked.Attribute(Text + "track-changes")?.Value);
        Assert.Equal(2, tracked.Elements(Text + "changed-region").Count());

        // The deleted paragraph mark joins the two paragraphs; the deletion holds the end of the first.
        Assert.Single(text.Elements(Text + "p"));
        Document read = Reread(document);
        Assert.True(read.Settings.TrackRevisions);
        Paragraph[] paragraphs = [.. read.Descendants<Paragraph>()];
        Assert.Equal(2, paragraphs.Length);
        Run[] runs = [.. paragraphs[0].Inlines.OfType<Run>()];
        Assert.Equal([null, RevisionKind.Insertion, RevisionKind.Deletion], runs.Select(r => r.Revision?.Kind));
        Assert.Equal("Ann", runs[1].Revision!.Author);
        Assert.Equal(when, runs[1].Revision!.Date);
        Assert.Equal(RevisionKind.Deletion, paragraphs[0].MarkRevision?.Kind);
        Assert.Equal("Joined", paragraphs[1].GetText());
    }

    // ---- Pages, tables and drawings ----

    [Fact]
    public void SectionsAreMasterPagesWithTheirHeaders()
    {
        Document document = new();
        Section first = document.AddSection();
        first.PageSetup.PageWidth = 595;
        first.PageSetup.MarginTop = 90;
        first.PageSetup.HeaderDistance = 30;
        first.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Header one");
        first.Body.AddParagraph("One");
        Section second = document.AddSection();
        second.PageSetup.Orientation = PageOrientation.Landscape;
        second.PageSetup.PageWidth = 842;
        second.PageSetup.PageHeight = 595;
        second.PageSetup.PageNumberStart = 7;
        second.Body.AddParagraph("Two");

        byte[] bytes = Save(document);
        Assert.Equal(2, Part(bytes, "styles.xml").Descendants(Style + "master-page").Count());
        Document read = OdtFormat.Load(new MemoryStream(bytes));
        Assert.Equal(2, read.Sections.Count);
        Assert.Equal(90, read.Sections[0].PageSetup.MarginTop!.Value, 1);
        Assert.Equal(30, read.Sections[0].PageSetup.HeaderDistance!.Value, 1);
        Assert.Equal("Header one", read.Sections[0].HeadersFooters.Single(h => h.Kind == HeaderFooterKind.HeaderPrimary).GetText().Trim());
        Assert.Equal(PageOrientation.Landscape, read.Sections[1].PageSetup.Orientation);
        Assert.Equal(7, read.Sections[1].PageSetup.PageNumberStart);
        Assert.Equal("Two", read.Sections[1].Body.GetText().Trim());
    }

    [Fact]
    public void NegativeWordMarginsAreWrittenAsTheirSize()
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.MarginTop = -72;
        section.PageSetup.HeaderDistance = 36;
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Header");
        section.Body.AddParagraph("Body");

        Document read = OdtFormat.Load(new MemoryStream(Save(document)));
        Assert.Equal(72, read.Sections[0].PageSetup.MarginTop!.Value, 1);
        Assert.Equal(36, read.Sections[0].PageSetup.HeaderDistance!.Value, 1);
    }

    [Theory]
    [InlineData(OdfVersion.Odf12)]
    [InlineData(OdfVersion.Odf13)]
    public void FirstPageHeadersRoundTrip(OdfVersion version)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.DifferentFirstPage = true;
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("Every page");
        section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderFirst).AddParagraph("First page");
        section.Body.AddParagraph("Body");

        XElement styles = Part(Save(document, new OdtSaveOptions { Version = version }), "styles.xml");

        // ODF 1.3 has first-page headers; for 1.2 the first page has a master page of its own.
        Assert.Equal(version == OdfVersion.Odf12 ? 2 : 1, styles.Descendants(Style + "master-page").Count());
        Document read = Reread(document, new OdtSaveOptions { Version = version });
        Assert.Contains(read.Sections.SelectMany(s => s.HeadersFooters), h => h.Kind == HeaderFooterKind.HeaderPrimary && h.GetText().Trim() == "Every page");
        Assert.Contains(read.Sections.SelectMany(s => s.HeadersFooters), h => h.GetText().Trim() == "First page");
    }

    [Fact]
    public void MergedCellsAreSpansAndCoveredCells()
    {
        Document document = NewDocument(out Body body);
        Table table = body.AddTable(3, 3);
        table.Rows[0].Cells[0].Format.GridSpan = 2;
        table.Rows[0].Cells.RemoveAt(1);
        table.Rows[0].Cells[1].Format.VerticalMerge = VerticalMerge.Restart;
        table.Rows[1].Cells[2].Format.VerticalMerge = VerticalMerge.Continue;
        table.Rows[2].Cells[2].Format.VerticalMerge = VerticalMerge.Continue;
        table.Rows[0].Format.IsHeader = true;
        table.Rows[0].Cells[0].AddParagraph("wide");

        XElement element = Body(Content(document)).Element(Table + "table")!;
        Assert.Single(element.Elements(Table + "table-header-rows"));
        XElement[] cells = [.. element.Descendants(Table + "table-row").First().Elements()];
        Assert.Equal("2", cells[0].Attribute(Table + "number-columns-spanned")?.Value);
        Assert.Equal(Table + "covered-table-cell", cells[1].Name);
        Assert.Equal("3", cells[2].Attribute(Table + "number-rows-spanned")?.Value);

        Table read = Reread(document).Descendants<Table>().Single();
        Assert.Equal(2, read.Rows[0].Cells[0].Format.GridSpan);
        Assert.Equal(VerticalMerge.Restart, read.Rows[0].Cells[1].GetEffectiveFormat().VerticalMerge);
        Assert.Equal(VerticalMerge.Continue, read.Rows[2].Cells[2].GetEffectiveFormat().VerticalMerge);
        Assert.Equal(true, read.Rows[0].Format.IsHeader);
    }

    [Fact]
    public void ShapesAndTextBoxesRoundTrip()
    {
        Document document = NewDocument(out Body body);
        Paragraph paragraph = body.AddParagraph("Shapes");
        Shape box = new(document, ShapeKind.Rectangle, 100, 50) { Fill = DocumentColor.FromHex("FFFF00") };
        box.AddTextBox().AddParagraph("In the box");
        paragraph.Inlines.Add(box);
        paragraph.Inlines.Add(new Shape(document, ShapeKind.Ellipse, 30, 20)
        {
            Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 72, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 144 },
        });
        paragraph.Inlines.Add(new Shape(document, ShapeKind.Triangle, 40, 40));
        paragraph.Inlines.Add(new Shape(document, ShapeKind.Line, 2, 170)
        {
            Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 270, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 527 },
        });

        Shape[] read = [.. Reread(document).Descendants<Shape>()];
        Assert.Equal([ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Triangle, ShapeKind.Line], read.Select(s => s.Kind));
        Assert.Equal("In the box", read[0].TextBox!.GetText().Trim());
        Assert.Equal(100, read[0].Width, 1);
        Assert.Equal(HorizontalAnchor.Page, read[1].Floating!.HorizontalAnchor);
        Assert.Equal(144, read[1].Floating!.VerticalOffset, 1);
        Assert.Equal(270, read[3].Floating!.HorizontalOffset, 1);
        Assert.Equal(527, read[3].Floating!.VerticalOffset, 1);
        Assert.Equal(170, read[3].Height, 1);
    }

    [Fact]
    public void ARotatedShapeKeepsItsPlace()
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Turned").Inlines.Add(new Shape(document, ShapeKind.Rectangle, 150, 100)
        {
            Rotation = 30,
            Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 80, VerticalAnchor = VerticalAnchor.Page, VerticalOffset = 160 },
        });

        Shape read = Reread(Reread(document)).Descendants<Shape>().Single();
        Assert.Equal(30, read.Rotation, 1);
        Assert.Equal(80, read.Floating!.HorizontalOffset, 1);
        Assert.Equal(160, read.Floating!.VerticalOffset, 1);
    }

    [Fact]
    public void TextEndingInAColumnSectionGetsNoSectionAfterIt()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("One column");
        Section columns = document.AddSection();
        columns.PageSetup.SectionStart = SectionStart.Continuous;
        columns.PageSetup.ColumnCount = 2;
        columns.Body.AddParagraph("Two columns");

        Document read = Reread(Reread(document));
        Assert.Equal(2, read.Sections.Count);
        Assert.Equal(2, read.Sections[1].PageSetup.ColumnCount);
    }

    // ---- Kept markup ----

    [Fact]
    public void MarkupKeptFromAnOdtIsWrittenBack()
    {
        Document first = OdtFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "odt", "own-package.odt"));
        byte[] once = Save(first);
        byte[] twice = Save(OdtFormat.Load(new MemoryStream(once)));
        Assert.Equal(Part(once, "content.xml").ToString(), Part(twice, "content.xml").ToString());
    }

    [Fact]
    public void AFlatFileHoldsTheObjectsAndPicturesItLinksTo()
    {
        const string Ns = """xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" xmlns:chart="urn:oasis:names:tc:opendocument:xmlns:chart:1.0" """;
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        Dictionary<string, string> parts = new()
        {
            ["content.xml"] = $"""<office:document-content {Ns}office:version="1.3"><office:body><office:text><text:p>Chart <draw:frame text:anchor-type="as-char" svg:width="1in" svg:height="1in"><draw:object xlink:href="./Object 1" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/><draw:image xlink:href="./ObjectReplacements/Object 1" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/></draw:frame></text:p></office:text></office:body></office:document-content>""",
            ["styles.xml"] = $"""<office:document-styles {Ns}office:version="1.3"/>""",
            ["Object 1/content.xml"] = $"""<office:document-content {Ns}office:version="1.3"><office:body><office:chart><chart:chart chart:class="chart:bar" svg:width="1in" svg:height="1in"><chart:plot-area/></chart:chart></office:chart></office:body></office:document-content>""",
            ["META-INF/manifest.xml"] = """<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="styles.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="Object 1/" manifest:media-type="application/vnd.oasis.opendocument.chart"/><manifest:file-entry manifest:full-path="Object 1/content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="ObjectReplacements/Object 1" manifest:media-type="image/png"/></manifest:manifest>""",
        };
        using MemoryStream package = new();
        using (ZipArchive zip = new(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (StreamWriter mimetype = new(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()))
            {
                mimetype.Write("application/vnd.oasis.opendocument.text");
            }

            foreach ((string name, string xml) in parts)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open());
                writer.Write(xml);
            }

            using Stream picture = zip.CreateEntry("ObjectReplacements/Object 1").Open();
            picture.Write(png);
        }

        Document document = OdtFormat.Load(new MemoryStream(package.ToArray()));
        XElement flat = XElement.Load(new MemoryStream(Save(document, new OdtSaveOptions { Kind = OdtKind.FlatDocument })));

        // Nothing links to a package file the flat file does not have: the chart and its picture are in the XML.
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        Assert.DoesNotContain(flat.Descendants(), e => e.Attribute(xlink + "href")?.Value is { } href && !href.StartsWith('#') && !href.Contains(':'));
        XElement frame = flat.Descendants(Draw + "frame").Single();
        Assert.Equal("chart", frame.Element(Draw + "object")!.Element(Office + "document")!.Element(Office + "body")!.Elements().Single().Name.LocalName);
        Assert.Equal("application/vnd.oasis.opendocument.chart", frame.Element(Draw + "object")!.Element(Office + "document")!.Attribute(Office + "mimetype")?.Value);
        Assert.Equal(png, Convert.FromBase64String(frame.Element(Draw + "image")!.Element(Office + "binary-data")!.Value));

        // A package keeps the chart's folder too, and both read back with the object.
        using (ZipArchive zip = new(new MemoryStream(Save(document))))
        {
            Assert.NotNull(zip.GetEntry("Object 1/content.xml"));
        }

        Assert.Single(OdtFormat.Load(new MemoryStream(Save(document, new OdtSaveOptions { Kind = OdtKind.FlatDocument }))).Descendants<Island>());
    }

    [Fact]
    public void DocumentSaveWritesOdt()
    {
        Document document = NewDocument(out Body body);
        body.AddParagraph("Registry");
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".odt");
        try
        {
            document.Save(path);
            Assert.Equal("Registry", Document.Load(path).GetText().Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
