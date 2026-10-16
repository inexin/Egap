using System.Globalization;
using System.Security.Cryptography;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// Reading signatures (Phase 5 task 2): value dictionaries, byte ranges and the revisions they cover, SubFilters, DocMDP,
/// FieldMDP and seed values, and every malformed byte range reported.
/// </summary>
public sealed class SignatureReadingTests
{
    [Fact]
    public void APadesSignatureReads()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal("Signature1", signature.Name);
        Assert.Equal(PdfSignatureKind.Approval, signature.Kind);
        Assert.Equal("Adobe.PPKLite", signature.Filter);
        Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
        Assert.Equal(PdfByteRangeIssues.None, signature.ByteRangeIssues);
        Assert.True(signature.CoversWholeFile);
        Assert.Equal(1, signature.Revision?.Index);
        Assert.Equal(2, signature.ByteRange.Count);
        Assert.Equal(0, signature.ByteRange[0].Start);
        Assert.Equal(0x30, signature.Contents.Span[0]);
        Assert.Null(signature.Certification);
        Assert.Null(signature.FieldLock);
        Assert.NotNull(signature.SigningTime);
        Assert.Same(signature.Dictionary, signature.Field.Signature);
    }

    [Fact]
    public void TheSignedBytesAreTheRangesJoined()
    {
        byte[] file = File.ReadAllBytes(SignedFiles.PathOf("pyhanko-bb-rsa.pdf"));
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfSignature signature = Assert.Single(pdf.Signatures);
        byte[] expected = [.. signature.ByteRange.SelectMany(r => file.Skip((int)r.Start).Take((int)r.Length))];
        using Stream signed = signature.OpenSignedBytes();
        Assert.Equal(expected.Length, signed.Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(signed));

        // Read in small pieces across the gap too.
        signed.Position = signature.ByteRange[0].Length - 3;
        byte[] across = new byte[6];
        Assert.Equal(6, signed.ReadAtLeast(across, 6));
        Assert.Equal(expected.Skip((int)signature.ByteRange[0].Length - 3).Take(6), across);
    }

    [Theory]
    [InlineData("pyhanko-bb-pkcs7.pdf", "adbe.pkcs7.detached", PdfSignatureKind.Approval)]
    [InlineData("pyhanko-bb-ec384-sha384.pdf", "ETSI.CAdES.detached", PdfSignatureKind.Approval)]
    [InlineData("pyhanko-bt-ec.pdf", "ETSI.CAdES.detached", PdfSignatureKind.Approval)]
    [InlineData("pyhanko-doc-timestamp.pdf", "ETSI.RFC3161", PdfSignatureKind.DocumentTimestamp)]
    [InlineData("pyhanko-certify-2.pdf", "ETSI.CAdES.detached", PdfSignatureKind.Certification)]
    public void EveryReferenceReads(string file, string subFilter, PdfSignatureKind kind)
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf(file));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(subFilter, signature.SubFilter);
        Assert.Equal(kind, signature.Kind);
        Assert.Equal(PdfByteRangeIssues.None, signature.ByteRangeIssues);
        Assert.True(signature.CoversWholeFile);
    }

    [Theory]
    [InlineData("pyhanko-certify-1.pdf", PdfCertification.NoChanges)]
    [InlineData("pyhanko-certify-2.pdf", PdfCertification.FormFillingAndSigning)]
    [InlineData("pyhanko-certify-3.pdf", PdfCertification.AnnotationsFormFillingAndSigning)]
    public void CertificationLevelsRead(string file, PdfCertification level)
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf(file));
        Assert.Equal(level, Assert.Single(pdf.Signatures).Certification);
    }

    [Fact]
    public void SignaturesComeInSigningOrder()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-two-signatures.pdf"));
        Assert.Equal(["Signature1", "Signature2"], pdf.Signatures.Select(s => s.Name));
        PdfSignature first = pdf.Signatures[0];
        PdfSignature second = pdf.Signatures[1];
        Assert.Equal(PdfSignatureKind.Certification, first.Kind);
        Assert.False(first.CoversWholeFile);
        Assert.Equal(PdfByteRangeIssues.None, first.ByteRangeIssues);
        Assert.True(second.CoversWholeFile);
        Assert.True(first.Revision!.Index < second.Revision!.Index);
    }

    [Fact]
    public void ALongTermFileHasItsTimestampLast()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.PathOf("pyhanko-blta-rsa.pdf"));
        Assert.Equal([PdfSignatureKind.Approval, PdfSignatureKind.DocumentTimestamp], pdf.Signatures.Select(s => s.Kind));
        Assert.False(pdf.Signatures[0].CoversWholeFile);

        // pyHanko adds the timestamp authority's validation data in one more update after the document timestamp.
        Assert.Equal(pdf.Revisions.Count - 2, pdf.Signatures[1].Revision!.Index);
        Assert.Equal(PdfByteRangeIssues.None, pdf.Signatures[1].ByteRangeIssues);
        Assert.Equal(4, pdf.Revisions.Count);
        Assert.NotNull(pdf.Catalog.Get<PdfDictionary>("DSS"));
    }

    [Fact]
    public void AHandMadeSignatureReadsWithItsEntries()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(signatureEntries: "/Location (Oslo) /ContactInfo (test@example.com)"));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(PdfByteRangeIssues.None, signature.ByteRangeIssues);
        Assert.Equal("Test Signer", signature.SignerName);
        Assert.Equal("Testing", signature.Reason);
        Assert.Equal("Oslo", signature.Location);
        Assert.Equal("test@example.com", signature.ContactInfo);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), signature.SigningTime);
        Assert.Equal(1, signature.Revision?.Index);
        Assert.Equal("Sig1 (Approval, ETSI.CAdES.detached)", signature.ToString());
    }

    [Theory]
    [InlineData("out", PdfByteRangeIssues.OutsideFile | PdfByteRangeIssues.NotAtRevisionEnd)]
    [InlineData("short", PdfByteRangeIssues.NotAtRevisionEnd)]
    [InlineData("overlap", PdfByteRangeIssues.Overlapping | PdfByteRangeIssues.ContentsNotExcluded)]
    [InlineData("start", PdfByteRangeIssues.NotFromStart)]
    [InlineData("wide", PdfByteRangeIssues.ContentsNotExcluded)]
    [InlineData("three", PdfByteRangeIssues.ContentsNotExcluded)]
    [InlineData("odd", PdfByteRangeIssues.Malformed)]
    [InlineData("negative", PdfByteRangeIssues.Malformed)]
    [InlineData("text", PdfByteRangeIssues.Malformed)]
    public void BadByteRangesAreReported(string kind, PdfByteRangeIssues issues)
    {
        string Range(long[] r) => kind switch
        {
            "out" => Format(r[0], r[1], r[2], r[3] + 100),
            "short" => Format(r[0], r[1], r[2], r[3] - 10),
            "overlap" => Format(r[0], r[1] + 30, r[2], r[3]),
            "start" => Format(5, r[1] - 5, r[2], r[3]),
            "wide" => Format(r[0], r[1] - 1, r[2], r[3]),
            "three" => Format(r[0], r[1], r[2], 10, r[2] + 10, r[3] - 10),
            "odd" => Format(r[0], r[1], r[2]),
            "negative" => Format(r[0], -r[1], r[2], r[3]),
            _ => "[0 (a) 1 2]",
        };

        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(byteRange: Range));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(issues, signature.ByteRangeIssues);
        Assert.False(signature.CoversWholeFile);
        if (issues.HasFlag(PdfByteRangeIssues.Malformed))
        {
            Assert.Empty(signature.ByteRange);
            Assert.Null(signature.Revision);
            Assert.Throws<InvalidOperationException>(() => signature.OpenSignedBytes());
        }
    }

    [Fact]
    public void AGapOfTheDigitsAloneIsTheContentsToo()
    {
        // Some signers sign the angle brackets and leave out only the hex digits, as pdf.js's signed_verified.pdf does.
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(byteRange: r => Format(r[0], r[1] + 1, r[2] - 1, r[3] + 1)));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(PdfByteRangeIssues.None, signature.ByteRangeIssues);
        Assert.True(signature.CoversWholeFile);
    }

    [Fact]
    public void AGapHoldingOtherBytesIsNotTheContents()
    {
        // The gap is a hex string of the right size, but a decoy before the real /Contents: the signature's own contents are
        // then inside the signed bytes, and what is left out is something else. The decoy starts 78 bytes before /Contents'
        // string: "<40010000> /ByteRange " then the 45-character range and " /Contents ".
        static string Range(long[] r) => Format(0, r[1] - 78, r[1] - 68, r[2] + r[3] - (r[1] - 68));
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(contents: [0x30, 0x01, 0x00, 0x00], signatureEntries: "/Decoy <40010000>", byteRange: Range));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(PdfByteRangeIssues.ContentsNotExcluded, signature.ByteRangeIssues);
    }

    [Fact]
    public void ReferencesGiveDocMdpAndFieldMdp()
    {
        const string references = "/Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P 1 /V /1.2 >> >> "
            + "<< /Type /SigRef /TransformMethod /FieldMDP /TransformParams << /Type /TransformParams /Action /Include /Fields [(Name) (Address)] /V /1.2 >> >>]";
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(signatureEntries: references, catalogEntries: "/Perms << /DocMDP 6 0 R >>"));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(PdfSignatureKind.Certification, signature.Kind);
        Assert.Equal(PdfCertification.NoChanges, signature.Certification);
        Assert.Equal(new PdfFieldLock(PdfFieldLockAction.Include, ["Name", "Address"]).Action, signature.FieldLock!.Action);
        Assert.Equal(["Name", "Address"], signature.FieldLock.Fields);
    }

    [Fact]
    public void ThePermsEntryAloneMakesACertification()
    {
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(catalogEntries: "/Perms << /DocMDP 6 0 R >>"));
        PdfSignature signature = Assert.Single(pdf.Signatures);
        Assert.Equal(PdfSignatureKind.Certification, signature.Kind);
        Assert.Equal(PdfCertification.FormFillingAndSigning, signature.Certification);
    }

    [Fact]
    public void FieldLocksAndSeedValuesRead()
    {
        const string field = "/Lock << /Type /SigFieldLock /Action /Exclude /Fields [(Comments)] /P 2 >> "
            + "/SV << /Type /SV /Ff 75 /Filter /Adobe.PPKLite /SubFilter [/ETSI.CAdES.detached /adbe.pkcs7.detached] /DigestMethod [/SHA256 /SHA384] "
            + "/Reasons [(Approved) (Reviewed)] /MDP << /P 2 >> /TimeStamp << /URL (http://tsa.example) /Ff 1 >> /AddRevInfo true "
            + "/Cert << /Ff 5 /Subject [<3003020100>] /OID [(2.5.29.32.0)] /KeyUsage [(1X0XXXXXX)] /URL (http://ca.example) >> >>";
        using PdfDocument pdf = PdfDocument.Open(SignedFiles.Signed(fieldEntries: field));
        PdfSignatureField signatureField = Assert.Single(pdf.Signatures).Field;
        PdfFieldLock fieldLock = signatureField.Lock!;
        Assert.Equal(PdfFieldLockAction.Exclude, fieldLock.Action);
        Assert.Equal(["Comments"], fieldLock.Fields);
        Assert.Equal(PdfCertification.FormFillingAndSigning, fieldLock.Certification);
        Assert.False(fieldLock.Covers("Comments"));
        Assert.False(fieldLock.Covers("Comments.Line1"));
        Assert.True(fieldLock.Covers("Name"));

        PdfSeedValue seed = signatureField.SeedValue!;
        Assert.Equal(PdfSeedValueFlags.Filter | PdfSeedValueFlags.SubFilter | PdfSeedValueFlags.Reasons | PdfSeedValueFlags.DigestMethod, seed.Flags);
        Assert.Equal("Adobe.PPKLite", seed.Filter);
        Assert.Equal(["ETSI.CAdES.detached", "adbe.pkcs7.detached"], seed.SubFilters);
        Assert.Equal(["SHA256", "SHA384"], seed.DigestMethods);
        Assert.Equal(["Approved", "Reviewed"], seed.Reasons);
        Assert.Equal(PdfCertification.FormFillingAndSigning, seed.Certification);
        Assert.Equal("http://tsa.example", seed.TimestampUrl);
        Assert.True(seed.TimestampRequired);
        Assert.True(seed.AddRevocationInfo);
        PdfCertificateSeed cert = seed.Certificate!;
        Assert.Equal(PdfCertificateSeedFlags.Subject | PdfCertificateSeedFlags.Policy, cert.Flags);
        Assert.Equal([0x30, 0x03, 0x02, 0x01, 0x00], Assert.Single(cert.Subjects));
        Assert.Equal(["2.5.29.32.0"], cert.Policies);
        Assert.Equal(["1X0XXXXXX"], cert.KeyUsage);
        Assert.Equal("http://ca.example", cert.Url);
        Assert.Empty(cert.Issuers);
    }

    [Fact]
    public void FieldLockActionsCoverTheRightFields()
    {
        Assert.True(new PdfFieldLock(PdfFieldLockAction.All, []).Covers("Anything"));
        PdfFieldLock include = new(PdfFieldLockAction.Include, ["A.B"]);
        Assert.True(include.Covers("A.B"));
        Assert.True(include.Covers("A.B.C"));
        Assert.False(include.Covers("A.BC"));
        Assert.False(include.Covers("A"));
    }

    [Fact]
    public void UnsignedFieldsAndFilesWithoutFormsHaveNoSignatures()
    {
        using (PdfDocument plain = PdfDocument.Open(SignedFiles.Base))
        {
            Assert.Empty(plain.Signatures);
        }

        byte[] file = SignedFiles.Signed();
        string text = System.Text.Encoding.Latin1.GetString(file).Replace("/V 6 0 R", "/V  null", StringComparison.Ordinal);
        using PdfDocument unsigned = PdfDocument.Open(System.Text.Encoding.Latin1.GetBytes(text));
        Assert.False(Assert.IsType<PdfSignatureField>(Assert.Single(unsigned.Form.GetAllFields())).IsSigned);
        Assert.Empty(unsigned.Signatures);
    }

    private static string Format(params long[] values)
    {
        return "[" + string.Join(' ', values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
    }
}
