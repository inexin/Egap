using System.Globalization;
using System.Text;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// The signed test files in tests/files/signatures, and hand-made signed files: an incremental update that adds a signed
/// signature field to <c>base.pdf</c>, with whatever byte range and dictionary entries a test needs. The contents are not a real
/// CMS unless a test gives one; reading a signature checks none of it.
/// </summary>
internal static class SignedFiles
{
    public static string PathOf(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "files", "signatures", name);
    }

    public static byte[] Base => File.ReadAllBytes(PathOf("base.pdf"));

    /// <summary>
    /// <c>base.pdf</c> with one update adding a signature field <c>Sig1</c> (object 7) whose value (object 6) has the given
    /// contents. <paramref name="byteRange"/> maps the right byte range (start, contents start, contents end, file length) to
    /// the one written, which must keep the same text width; <see langword="null"/> writes the right one.
    /// </summary>
    public static byte[] Signed(
        byte[]? contents = null,
        Func<long[], string>? byteRange = null,
        string signatureEntries = "",
        string fieldEntries = "",
        string catalogEntries = "",
        byte[]? source = null,
        string extraObjects = "")
    {
        byte[] original = source ?? Base;
        contents ??= [0x30, 0x03, 0x02, 0x01, 0x00, 0, 0, 0];
        string hex = Convert.ToHexString(contents);
        const string placeholder = "[0000000000 0000000000 0000000000 0000000000]";
        StringBuilder update = new();
        List<(int Number, int Offset)> offsets = [];
        int at = original.Length;

        void Add(int number, string body)
        {
            offsets.Add((number, at + Encoding.Latin1.GetByteCount(update.ToString())));
            update.Append(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n");
        }

        update.Append('\n');
        Add(6, $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached /M (D:20261016120000Z) /Name (Test Signer) /Reason (Testing) {signatureEntries} /ByteRange {placeholder} /Contents <{hex}> >>");
        Add(7, $"<< /FT /Sig /T (Sig1) /V 6 0 R /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /F 132 /P 3 0 R {fieldEntries} >>");
        Add(1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] /SigFlags 3 >> {catalogEntries} >>");
        Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /Annots [7 0 R] >>");
        if (extraObjects.Length > 0)
        {
            update.Append(extraObjects);
        }

        int xref = at + Encoding.Latin1.GetByteCount(update.ToString());
        update.Append("xref\n");
        foreach ((int number, int offset) in offsets.OrderBy(o => o.Number))
        {
            update.Append(CultureInfo.InvariantCulture, $"{number} 1\n{offset:D10} 00000 n\r\n");
        }

        int previous = LastStartXref(original);
        update.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size 8 /Root 1 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n");

        byte[] file = [.. original, .. Encoding.Latin1.GetBytes(update.ToString())];
        string text = Encoding.Latin1.GetString(file);
        int contentsStart = text.IndexOf("/Contents <" + hex, original.Length, StringComparison.Ordinal) + "/Contents ".Length;
        int contentsEnd = contentsStart + hex.Length + 2;
        long[] right = [0, contentsStart, contentsEnd, file.Length - contentsEnd];
        string written = byteRange?.Invoke(right) ?? string.Create(CultureInfo.InvariantCulture, $"[{right[0]} {right[1]} {right[2]} {right[3]}]");
        written = written.PadRight(placeholder.Length);
        if (written.Length != placeholder.Length)
        {
            throw new ArgumentException("The byte range must fit the placeholder.", nameof(byteRange));
        }

        int placeholderAt = text.IndexOf(placeholder, original.Length, StringComparison.Ordinal);
        Encoding.Latin1.GetBytes(written).CopyTo(file, placeholderAt);
        return file;
    }

    /// <summary>The offset the last <c>startxref</c> of a file names.</summary>
    public static int LastStartXref(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length;
        return int.Parse(text[at..].Trim().Split('\n', '\r')[0], CultureInfo.InvariantCulture);
    }
}
