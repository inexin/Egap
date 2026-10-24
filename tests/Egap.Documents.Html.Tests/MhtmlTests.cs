using System.Text;

namespace Egap.Documents.Html.Tests;

/// <summary>MHTML web archives as Chromium and Word save them, read and written.</summary>
public class MhtmlTests
{
    internal const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    // The shape Chromium's "Save as single file" writes: quoted-printable HTML, base64 resources, Content-Location URLs.
    private const string Chromium =
        "From: <Saved by Blink>\r\nSnapshot-Content-Location: https://example.com/page\r\nSubject: =?utf-8?Q?Caf=C3=A9?=\r\n" +
        "MIME-Version: 1.0\r\nContent-Type: multipart/related;\r\n\ttype=\"text/html\";\r\n\tboundary=\"----MultipartBoundary--abc----\"\r\n\r\n\r\n" +
        "------MultipartBoundary--abc----\r\nContent-Type: text/html\r\nContent-ID: <frame-1@mhtml.blink>\r\nContent-Transfer-Encoding: quoted-printable\r\n" +
        "Content-Location: https://example.com/page\r\n\r\n" +
        "<html><head><meta http-equiv=3D\"Content-Type\" content=3D\"text/html; charset=3DUTF-8\"><link rel=3D\"stylesheet\" href=3D\"style.css\"></head>" +
        "<body><h1>Caf=C3=A9 menu</h1><p class=3D\"red\">A long paragraph that the encoder wrapped with a soft line =\r\nbreak.</p>" +
        "<img src=3D\"https://cdn.example.com/dot.png\" alt=3D\"dot\"><img src=3D\"cid:css-img@mhtml.blink\" alt=3D\"cid\"></body></html>\r\n" +
        "------MultipartBoundary--abc----\r\nContent-Type: text/css\r\nContent-Transfer-Encoding: quoted-printable\r\nContent-Location: https://example.com/style.css\r\n\r\n" +
        ".red { color: rgb(255, 0, 0); }\r\n" +
        "------MultipartBoundary--abc----\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\nContent-Location: https://cdn.example.com/dot.png\r\n\r\n" +
        Png + "\r\n" +
        "------MultipartBoundary--abc----\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\nContent-ID: <css-img@mhtml.blink>\r\n\r\n" +
        Png + "\r\n" +
        "------MultipartBoundary--abc------\r\n";

    // The shape Word's "Single File Web Page" writes: file: locations and a page that links its files relatively.
    private const string Word =
        "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"----=_NextPart_01DA\"\r\n\r\nThis document is a Single File Web Page.\r\n\r\n" +
        "------=_NextPart_01DA\r\nContent-Location: file:///C:/0A1B2C3D/doc.htm\r\nContent-Transfer-Encoding: quoted-printable\r\n" +
        "Content-Type: text/html; charset=\"windows-1252\"\r\n\r\n" +
        "<html><body><p class=3DMsoNormal>Bl=E5b=E6r</p><p><img src=3D\"doc_files/image001.png\" alt=3D\"berry\"></p></body></html>\r\n\r\n" +
        "------=_NextPart_01DA\r\nContent-Location: file:///C:/0A1B2C3D/doc_files/image001.png\r\nContent-Transfer-Encoding: base64\r\nContent-Type: image/png\r\n\r\n" +
        Png + "\r\n\r\n------=_NextPart_01DA--\r\n";

    [Fact]
    public void ChromiumArchivesLoadWithTheirPicturesAndStyles()
    {
        Document document = HtmlFormat.LoadMhtml(Encoding.ASCII.GetBytes(Chromium), out _);

        string text = document.GetText();
        Assert.Contains("Café menu", text, StringComparison.Ordinal);
        Assert.Contains("soft line break", text, StringComparison.Ordinal);
        List<Picture> pictures = [.. document.Descendants<Picture>()];
        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, p => Assert.Equal("image/png", p.Image.ContentType));
        Run red = document.Descendants<Run>().First(r => r.Text.Contains("soft", StringComparison.Ordinal));
        Assert.Equal(new Egami.Pixels.Rgba8(255, 0, 0, 255), red.Format.Color?.Resolve(null, default));
    }

    [Fact]
    public void WordArchivesLoadWithTheirCharsetAndFiles()
    {
        Document document = HtmlFormat.LoadMhtml(Encoding.ASCII.GetBytes(Word), out _);

        Assert.Contains("Blåbær", document.GetText(), StringComparison.Ordinal);
        Assert.Single(document.Descendants<Picture>());
    }

    [Fact]
    public void WhatTheArchiveDoesNotHoldGoesToTheResolverOnly()
    {
        string message = Chromium.Replace("https://cdn.example.com/dot.png\r\n", "https://cdn.example.com/other.png\r\n", StringComparison.Ordinal);
        List<Uri> asked = [];

        Document document = HtmlFormat.LoadMhtml(Encoding.ASCII.GetBytes(message), out IReadOnlyList<ConversionWarning> warnings, new HtmlLoadOptions
        {
            ResourceResolver = uri =>
            {
                asked.Add(uri);
                return null;
            },
        });

        Assert.Contains(new Uri("https://cdn.example.com/dot.png"), asked);
        Assert.Single(document.Descendants<Picture>());
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.ResourceNotLoaded);
    }

    [Fact]
    public void WrittenArchivesReadBackWithTextAndPictures()
    {
        Document document = HtmlFormat.LoadMhtml(Encoding.ASCII.GetBytes(Chromium), out _);
        document.Properties.Title = "Kafé";

        string mhtml = document.ToMhtml();

        Assert.StartsWith("From: <Saved by Egap>\r\n", mhtml, StringComparison.Ordinal);
        Assert.Contains("Subject: =?utf-8?B?", mhtml, StringComparison.Ordinal);
        Assert.Contains("Content-Type: multipart/related;", mhtml, StringComparison.Ordinal);
        Assert.Contains("Content-Transfer-Encoding: quoted-printable", mhtml, StringComparison.Ordinal);
        Assert.All(mhtml.Split("\r\n"), line => Assert.True(line.Length <= 78, line));

        // The two parts hold the same image, which is written once.
        Assert.Single(mhtml.Split("Content-Type: image/png").Skip(1));
        Assert.Equal(DocumentFormatKind.Mhtml, Document.Identify(new MemoryStream(Encoding.ASCII.GetBytes(mhtml))));

        Document again = HtmlFormat.LoadMhtml(Encoding.ASCII.GetBytes(mhtml), out _);
        Assert.Contains("Café menu", again.GetText(), StringComparison.Ordinal);
        Assert.Equal(2, again.Descendants<Picture>().Count());
    }

    [Fact]
    public void FormatsAreIdentifiedAndRegistered()
    {
        HtmlFormat.Register();
        string folder = Directory.CreateTempSubdirectory("egap-mhtml").FullName;
        try
        {
            string path = Path.Combine(folder, "page.mht");
            File.WriteAllText(path, Word, Encoding.ASCII);
            Assert.Equal(DocumentFormatKind.Mhtml, Document.Identify(path));
            Assert.Equal(DocumentFormatKind.Mhtml, Document.Identify(new MemoryStream(Encoding.ASCII.GetBytes(Word))));
            Assert.Equal(DocumentFormatKind.Markdown, Document.Identify(new MemoryStream("Title: not mail\n\nsome *text*"u8.ToArray())));

            Document document = Document.Load(path);
            Assert.Contains("Blåbær", document.GetText(), StringComparison.Ordinal);
            string saved = Path.Combine(folder, "again.mhtml");
            document.Save(saved);
            Assert.Contains("Blåbær", HtmlFormat.LoadMhtml(saved).GetText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ArchivesWithoutAPageAreRefused()
    {
        string message = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=b\r\n\r\n--b\r\nContent-Type: image/png\r\n\r\nx\r\n--b--\r\n";

        Assert.Throws<DocumentException>(() => HtmlFormat.LoadMhtml(Encoding.ASCII.GetBytes(message), out _));
    }
}
