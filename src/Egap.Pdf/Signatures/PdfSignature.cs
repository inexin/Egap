using System.Globalization;

using Egap.Pdf.Metadata;

namespace Egap.Pdf;

/// <summary>
/// A signature or document timestamp in a signed signature field (ISO 32000-2, 12.8): its value dictionary, the bytes it
/// signs and the revision those bytes are. Reading checks nothing cryptographic; <see cref="ByteRangeIssues"/> reports what is
/// wrong with the byte range itself.
/// </summary>
/// <example>
/// <code>
/// foreach (PdfSignature signature in pdf.Signatures)
/// {
///     Console.WriteLine($"{signature.Name}: {signature.Kind} {signature.SubFilter}, revision {signature.Revision?.Index}");
///     if (signature.ByteRangeIssues != PdfByteRangeIssues.None) Console.WriteLine($"  byte range: {signature.ByteRangeIssues}");
/// }
/// </code>
/// </example>
public sealed class PdfSignature
{
    private readonly PdfDocument document;

    private PdfSignature(PdfDocument document, PdfSignatureField field, PdfDictionary dictionary)
    {
        this.document = document;
        Field = field;
        Dictionary = dictionary;
        Contents = dictionary.Get<PdfString>("Contents")?.Bytes.ToArray() ?? [];
        ByteRange = ReadByteRange(dictionary, out PdfByteRangeIssues issues);
        Revision = FindRevision(document, ByteRange);
        ByteRangeIssues = issues | CheckByteRange(document, ByteRange, Contents, Revision);
        (Certification, FieldLock) = ReadReferences(document, dictionary);
        Kind = dictionary.GetName("Type")?.Value == "DocTimeStamp" || SubFilter == "ETSI.RFC3161" ? PdfSignatureKind.DocumentTimestamp
            : Certification is not null ? PdfSignatureKind.Certification
            : PdfSignatureKind.Approval;
    }

    /// <summary>The signature field.</summary>
    public PdfSignatureField Field { get; }

    /// <summary>The field's fully qualified name.</summary>
    public string Name => Field.FullName;

    /// <summary>The signature dictionary, the field's <c>/V</c>.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Whether it is an approval signature, a certification signature or a document timestamp.</summary>
    public PdfSignatureKind Kind { get; }

    /// <summary>The signature handler (<c>/Filter</c>), usually <c>Adobe.PPKLite</c>.</summary>
    public string? Filter => Dictionary.GetName("Filter")?.Value;

    /// <summary>
    /// The encoding of <see cref="Contents"/> (<c>/SubFilter</c>): <c>ETSI.CAdES.detached</c> (PAdES),
    /// <c>adbe.pkcs7.detached</c>, <c>adbe.pkcs7.sha1</c>, <c>adbe.x509.rsa_sha1</c> or <c>ETSI.RFC3161</c>.
    /// </summary>
    public string? SubFilter => Dictionary.GetName("SubFilter")?.Value;

    /// <summary>The ranges of the file the signature covers; empty when <c>/ByteRange</c> is missing or malformed.</summary>
    public IReadOnlyList<PdfByteRange> ByteRange { get; }

    /// <summary>What is wrong with the byte range; <see cref="PdfByteRangeIssues.None"/> when nothing is.</summary>
    public PdfByteRangeIssues ByteRangeIssues { get; }

    /// <summary>
    /// The <c>/Contents</c> bytes: a DER-encoded CMS SignedData (or a timestamp token, or for <c>adbe.x509.rsa_sha1</c> a raw
    /// signature), usually followed by the zero bytes that filled the rest of the space reserved for it.
    /// </summary>
    public ReadOnlyMemory<byte> Contents { get; }

    /// <summary>The signer's name as written in the dictionary (<c>/Name</c>); the certificate is the authority.</summary>
    public string? SignerName => Dictionary.GetText("Name");

    /// <summary>The reason given for signing (<c>/Reason</c>).</summary>
    public string? Reason => Dictionary.GetText("Reason");

    /// <summary>Where the document was signed (<c>/Location</c>).</summary>
    public string? Location => Dictionary.GetText("Location");

    /// <summary>How to reach the signer (<c>/ContactInfo</c>).</summary>
    public string? ContactInfo => Dictionary.GetText("ContactInfo");

    /// <summary>The signing time the signer's computer claimed (<c>/M</c>); a timestamp is the trusted time.</summary>
    public DateTimeOffset? SigningTime => PdfDate.ParsePdf(Dictionary.GetText("M"));

    /// <summary>The changes a certification signature allows after it; <see langword="null"/> for any other signature.</summary>
    public PdfCertification? Certification { get; }

    /// <summary>The fields the signature locks (its FieldMDP reference); <see langword="null"/> when it locks none.</summary>
    public PdfFieldLock? FieldLock { get; }

    /// <summary>The revision the signature covers; <see langword="null"/> when its byte range ends where no revision does.</summary>
    public PdfRevision? Revision { get; }

    /// <summary>Whether the byte range is well formed and covers the file's last revision, so nothing was added after signing.</summary>
    public bool CoversWholeFile => ByteRangeIssues == PdfByteRangeIssues.None && Revision?.IsLatest == true;

    /// <summary>The bytes the signature covers, read from the file: the byte ranges joined.</summary>
    /// <exception cref="InvalidOperationException">The byte range is malformed or runs past the end of the file.</exception>
    public Stream OpenSignedBytes()
    {
        if (ByteRange.Count == 0 || ByteRangeIssues.HasFlag(PdfByteRangeIssues.OutsideFile) || ByteRangeIssues.HasFlag(PdfByteRangeIssues.Overlapping))
        {
            throw new InvalidOperationException($"The signature's byte range cannot be read ({ByteRangeIssues}).");
        }

        return new SignedBytesStream(document, ByteRange);
    }

    /// <summary>
    /// Verifies the signature (Phase 5 design Q8) and reports each verdict apart: integrity, the certificate chain to
    /// <see cref="PdfVerifyOptions.TrustAnchors"/>, the timestamp, revocation, and the changes made since signing. Nothing is
    /// fetched from the network.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfSignatureReport report = signature.Verify(new PdfVerifyOptions { TrustAnchors = [root] });
    /// Console.WriteLine($"{report.Integrity} {report.Chain} {report.Timestamp}");
    /// </code>
    /// </example>
    public PdfSignatureReport Verify(PdfVerifyOptions? options = null)
    {
        return Signatures.SignatureVerifier.Verify(this, options ?? PdfVerifyOptions.Default);
    }

    /// <summary>
    /// The CMS SignedData in <see cref="Contents"/> (Phase 5 task 3); for <c>adbe.x509.rsa_sha1</c>, which holds no CMS, use
    /// <see cref="Signatures.Cms.RsaSha1Signature"/>.
    /// </summary>
    /// <exception cref="PdfException">The contents are not a SignedData Egap can read.</exception>
    internal Signatures.Cms.CmsSignedData DecodeCms()
    {
        return Signatures.Cms.CmsSignedData.Decode(Contents);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Name} ({Kind}, {SubFilter})");
    }

    /// <summary>The signed signature fields of the document's form, in signing order: by where their byte ranges end.</summary>
    internal static IReadOnlyList<PdfSignature> Find(PdfDocument document)
    {
        List<PdfSignature> found = [];
        if (!document.Form.Exists)
        {
            return found;
        }

        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfField field in document.Form.GetAllFields())
        {
            if (field is PdfSignatureField { Signature: { } dictionary } signatureField && field.IsTerminal && seen.Add(dictionary))
            {
                found.Add(new PdfSignature(document, signatureField, dictionary));
            }
        }

        return [.. found.OrderBy(s => s.ByteRange.Count > 0 ? s.ByteRange[^1].End : long.MaxValue)];
    }

    private static PdfByteRange[] ReadByteRange(PdfDictionary dictionary, out PdfByteRangeIssues issues)
    {
        issues = PdfByteRangeIssues.None;
        if (dictionary.Get<PdfArray>("ByteRange") is not { Count: >= 2 } array || array.Count % 2 != 0)
        {
            issues = PdfByteRangeIssues.Malformed;
            return [];
        }

        PdfByteRange[] ranges = new PdfByteRange[array.Count / 2];
        for (int i = 0; i < ranges.Length; i++)
        {
            if (array.Get<PdfInteger>(2 * i) is not { Value: >= 0 } start || array.Get<PdfInteger>((2 * i) + 1) is not { Value: >= 0 } length
                || start.Value > long.MaxValue - length.Value)
            {
                issues = PdfByteRangeIssues.Malformed;
                return [];
            }

            ranges[i] = new PdfByteRange(start.Value, length.Value);
        }

        return ranges;
    }

    private static PdfRevision? FindRevision(PdfDocument document, IReadOnlyList<PdfByteRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return null;
        }

        // A signer may or may not count the end-of-line after %%EOF.
        long end = ranges[^1].End;
        return document.Revisions.FirstOrDefault(r => end >= r.EofEnd && end <= r.Length);
    }

    private static PdfByteRangeIssues CheckByteRange(PdfDocument document, IReadOnlyList<PdfByteRange> ranges, ReadOnlyMemory<byte> contents, PdfRevision? revision)
    {
        if (ranges.Count == 0)
        {
            return PdfByteRangeIssues.None;
        }

        PdfByteRangeIssues issues = PdfByteRangeIssues.None;
        if (ranges[0].Start != 0)
        {
            issues |= PdfByteRangeIssues.NotFromStart;
        }

        for (int i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start < ranges[i - 1].End)
            {
                issues |= PdfByteRangeIssues.Overlapping;
            }
        }

        if (ranges.Any(r => r.End > document.FileLength))
        {
            issues |= PdfByteRangeIssues.OutsideFile;
        }

        if (revision is null)
        {
            issues |= PdfByteRangeIssues.NotAtRevisionEnd;
        }

        if (ranges.Count != 2 || !GapIsContents(document, ranges[0].End, ranges[1].Start, contents.Span))
        {
            issues |= PdfByteRangeIssues.ContentsNotExcluded;
        }

        return issues;
    }

    /// <summary>
    /// Whether the bytes from <paramref name="start"/> to <paramref name="end"/> are the hex string holding exactly
    /// <paramref name="contents"/>: with its angle brackets, as most signers leave them out, or only its digits, with the
    /// brackets just outside, as some do (pdf.js's signed_verified.pdf, which pyHanko accepts too).
    /// </summary>
    private static bool GapIsContents(PdfDocument document, long start, long end, ReadOnlySpan<byte> contents)
    {
        // Two hex digits per byte, and the brackets inside or just outside; anything else in the gap (spaces, another
        // object) is not left out by accident only.
        long digits = 2L * contents.Length;
        bool inside = end - start == digits + 2, outside = end - start == digits;
        if ((!inside && !outside) || start < 1 || end + 1 > document.FileLength)
        {
            return false;
        }

        byte[] gap = document.ReadFileBytes(start - 1, (int)(end - start) + 2);
        int first = inside ? 1 : 0;
        if (gap[first] != '<' || gap[^(first + 1)] != '>')
        {
            return false;
        }

        for (int i = 0; i < contents.Length; i++)
        {
            int high = HexValue(gap[first + 1 + (2 * i)]);
            int low = HexValue(gap[first + 2 + (2 * i)]);
            if (high < 0 || low < 0 || ((high << 4) | low) != contents[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int HexValue(byte c)
    {
        return c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => -1,
        };
    }

    /// <summary>
    /// The DocMDP permissions and FieldMDP lock from the signature's references (<c>/Reference</c>, 12.8.1); a signature the
    /// catalog's <c>/Perms /DocMDP</c> names is a certification even without a reference, with the default permissions.
    /// </summary>
    private static (PdfCertification? Certification, PdfFieldLock? Lock) ReadReferences(PdfDocument document, PdfDictionary dictionary)
    {
        PdfCertification? certification = null;
        PdfFieldLock? fieldLock = null;
        if (dictionary.Get<PdfArray>("Reference") is { } references)
        {
            for (int i = 0; i < references.Count; i++)
            {
                if (references.Get<PdfDictionary>(i) is not { } reference)
                {
                    continue;
                }

                PdfDictionary? parameters = reference.Get<PdfDictionary>("TransformParams");
                switch (reference.GetName("TransformMethod")?.Value)
                {
                    case "DocMDP":
                        certification = Certifications.FromNumber(parameters?.GetInt("P") ?? 2);
                        break;
                    case "FieldMDP":
                        fieldLock = PdfFieldLock.Read(parameters);
                        break;
                }
            }
        }

        if (certification is null && ReferenceEquals(document.Catalog.Get<PdfDictionary>("Perms")?.Get<PdfDictionary>("DocMDP"), dictionary))
        {
            certification = PdfCertification.FormFillingAndSigning;
        }

        return (certification, fieldLock);
    }

    /// <summary>The signed bytes as one stream: each range read from the document's file in turn.</summary>
    private sealed class SignedBytesStream(PdfDocument document, IReadOnlyList<PdfByteRange> ranges) : Stream
    {
        private readonly long length = ranges.Sum(r => r.Length);
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => position;
            set => position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            long skipped = 0;
            foreach (PdfByteRange range in ranges)
            {
                if (total == buffer.Length)
                {
                    break;
                }

                if (position >= skipped + range.Length)
                {
                    skipped += range.Length;
                    continue;
                }

                long inRange = position - skipped;
                int count = (int)Math.Min(buffer.Length - total, range.Length - inRange);
                byte[] bytes = document.ReadFileBytes(range.Start + inRange, count);
                bytes.CopyTo(buffer[total..]);
                total += bytes.Length;
                position += bytes.Length;
                skipped += range.Length;
                if (bytes.Length < count)
                {
                    break;
                }
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                _ => length + offset,
            };
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
