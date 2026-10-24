using System.Text;

namespace Egap.Documents.Html.Tests;

/// <summary>The small MIME reader MHTML uses: headers, encoded words, boundaries, base64, quoted-printable and limits.</summary>
public class MimeTests
{
    [Theory]
    [InlineData("caf=C3=A9", "café")]
    [InlineData("soft=\r\nbreak", "softbreak")]
    [InlineData("soft=\nbreak", "softbreak")]
    [InlineData("a=3Db", "a=b")]
    [InlineData("trailing   \r\nline", "trailing\r\nline")]
    [InlineData("bad =ZZ kept", "bad =ZZ kept")]
    public void QuotedPrintableDecodes(string encoded, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(QuotedPrintable.Decode(Encoding.ASCII.GetBytes(encoded))));
    }

    [Fact]
    public void QuotedPrintableRoundTripsWithShortLines()
    {
        string text = "<p class=\"x\">" + string.Concat(Enumerable.Repeat("Ærlig talt = ", 30)) + "</p>\n<p>tab\tend \n</p>";

        string encoded = QuotedPrintable.Encode(Encoding.UTF8.GetBytes(text));

        Assert.All(encoded.Split("\r\n"), line => Assert.True(line.Length <= 76, line));
        Assert.DoesNotContain(" \r\n", encoded, StringComparison.Ordinal);
        Assert.Equal(text.Replace("\n", "\r\n", StringComparison.Ordinal), Encoding.UTF8.GetString(QuotedPrintable.Decode(Encoding.ASCII.GetBytes(encoded))));
    }

    [Theory]
    [InlineData("=?utf-8?Q?Caf=C3=A9_au_lait?=", "Café au lait")]
    [InlineData("=?UTF-8?B?w6ZyZQ==?= and =?iso-8859-1?q?=E6re?=", "ære and ære")]
    [InlineData("=?utf-8?q?a?= =?utf-8?q?b?=", "ab")]
    [InlineData("plain =?not a word", "plain =?not a word")]
    public void EncodedWordsAreDecoded(string value, string expected)
    {
        Assert.Equal(expected, MimeHeaders.DecodeWords(value));
    }

    [Fact]
    public void ParametersAreRead()
    {
        const string type = "multipart/related;\ttype=\"text/html\"; boundary=\"----=_Part \\\"1\\\"\"; start=<a@b>";

        Assert.Equal("multipart/related", MimeHeaders.MediaType(type));
        Assert.Equal("text/html", MimeHeaders.Parameter(type, "type"));
        Assert.Equal("----=_Part \"1\"", MimeHeaders.Parameter(type, "BOUNDARY"));
        Assert.Equal("<a@b>", MimeHeaders.Parameter(type, "start"));
        Assert.Null(MimeHeaders.Parameter(type, "charset"));
    }

    [Fact]
    public void MultipartIsSplitAtItsBoundaries()
    {
        string message = "MIME-Version: 1.0\r\nContent-Type: multipart/related;\r\n boundary=\"b1\"\r\n\r\npreamble\r\n" +
            "--b1\r\nContent-Type: text/plain\r\n\r\nfirst\r\n--b1 \r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\nAAEC\r\nAw==\r\n" +
            "--b1--\r\nepilogue";

        MimeEntity root = MimeReader.Read(Encoding.ASCII.GetBytes(message), new DocumentLimits());

        Assert.True(root.IsMultipart);
        Assert.Equal(2, root.Parts.Count);
        Assert.Equal("first", Encoding.ASCII.GetString(root.Parts[0].Body));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, root.Parts[1].Body);
    }

    [Fact]
    public void FoldedHeadersAreUnfolded()
    {
        MimeEntity root = MimeReader.Read("Subject: one\r\n two\r\n\r\nbody"u8, new DocumentLimits());

        Assert.Equal("one two", root.Header("subject"));
        Assert.Equal("body", Encoding.ASCII.GetString(root.Body));
    }

    [Fact]
    public void Base64IsReadLeniently()
    {
        // Word sets the spare bits of a last group and pads it oddly; line breaks and stray characters are skipped.
        MimeEntity part = MimeReader.Read("Content-Transfer-Encoding: base64\r\n\r\nAAEC\r\nA*w"u8, new DocumentLimits());
        MimeEntity word = MimeReader.Read("Content-Transfer-Encoding: base64\r\n\r\nQUJDRB=="u8, new DocumentLimits());

        Assert.Equal(new byte[] { 0, 1, 2, 3 }, part.Body);
        Assert.Equal("ABCD"u8.ToArray(), word.Body);
    }

    [Fact]
    public void CharsetsNetTurnsOffAreUnknown()
    {
        Assert.Null(MimeEntity.Charset("utf-7"));
        Assert.Equal(1252, MimeEntity.Charset("windows-1252")!.CodePage);
        Assert.Equal("ISO-8859-1", MimeEntity.Charset("\"iso-8859-1\"")!.WebName, ignoreCase: true);
        Assert.Null(MimeEntity.Charset("x-no-such-charset"));
    }

    [Fact]
    public void LimitsHold()
    {
        StringBuilder many = new("Content-Type: multipart/mixed; boundary=x\r\n\r\n");
        for (int i = 0; i < 20; i++)
        {
            many.Append("--x\r\n\r\npart\r\n");
        }

        many.Append("--x--\r\n");
        Assert.Throws<DocumentLimitException>(() => MimeReader.Read(Encoding.ASCII.GetBytes(many.ToString()), new DocumentLimits { MaxEntries = 10 }));

        StringBuilder deep = new();
        for (int i = 0; i < 20; i++)
        {
            deep.Append(System.Globalization.CultureInfo.InvariantCulture, $"Content-Type: multipart/mixed; boundary=b{i}\r\n\r\n--b{i}\r\n");
        }

        Assert.Throws<DocumentLimitException>(() => MimeReader.Read(Encoding.ASCII.GetBytes(deep.ToString()), new DocumentLimits { MaxDepth = 8 }));
        Assert.Throws<DocumentLimitException>(() => MimeReader.Read("Content-Type: text/plain\r\n\r\n0123456789"u8, new DocumentLimits { MaxPartSize = 4 }));
        Assert.Throws<DocumentException>(() => MimeReader.Read("\r\nno headers"u8, new DocumentLimits()));
    }
}
