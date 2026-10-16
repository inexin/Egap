using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Signatures;

/// <summary>
/// Writes a signature (Phase 5 design, 5.1): an incremental update adding the signed field and its signature dictionary,
/// with <c>/Contents</c> a zero-filled hex string of the reserved size and <c>/ByteRange</c> a placeholder of fixed width.
/// The original bytes are copied to the output while they are hashed, the update's bytes around <c>/Contents</c> are hashed
/// after them, the byte range is patched in, and the CMS made from the digest is written into the reserved space; no other
/// byte moves. The output is a stream the caller gives, never the input file (design Q3).
/// </summary>
internal static class SignatureWriter
{
    /// <summary>Room for the signed attributes, the SignerInfo and the structure around them, beyond certificates and the signature value.</summary>
    private const int Overhead = 2048;

    /// <summary>Room for a signature timestamp token, with its authority's certificates (B-T and up).</summary>
    private const int TimestampRoom = 12 * 1024;

    /// <summary>What a deferred signature reserves when no signer tells it how big the CMS will be.</summary>
    private const int DeferredDefault = 32 * 1024;

    /// <summary>Where the signature landed: the update with its byte range set, and what the CMS must sign.</summary>
    internal sealed class Prepared(byte[] update, long baseLength, int contentsAt, int reservedSize, byte[] digest)
    {
        public byte[] Update { get; } = update;

        public long BaseLength { get; } = baseLength;

        /// <summary>Where the <c>&lt;</c> of <c>/Contents</c> is in <see cref="Update"/>.</summary>
        public int ContentsAt { get; } = contentsAt;

        public int ReservedSize { get; } = reservedSize;

        public byte[] Digest { get; } = digest;

        /// <summary>Writes the CMS's hex digits into the update, after the <c>&lt;</c>.</summary>
        public void Fill(ReadOnlySpan<byte> cms)
        {
            if (cms.Length > ReservedSize)
            {
                throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The signature needs {cms.Length} bytes, but only {ReservedSize} were reserved; sign again with PdfSignOptions.ReservedSize of at least {cms.Length}."));
            }

            Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(Update, ContentsAt + 1);
        }
    }

    public static async Task SignAsync(PdfDocument document, string fieldName, PdfSignOptions options, Stream output, CancellationToken cancellationToken)
    {
        PdfSigner signer = options.Signer ?? throw new ArgumentException("PdfSignOptions.Signer is required to sign; to sign elsewhere, use PrepareSignatureAsync.", nameof(options));
        CheckTimestamp(options);
        Prepared prepared = await PrepareAsync(document, fieldName, options, output, cancellationToken).ConfigureAwait(false);
        byte[] cms = await BuildCmsAsync(prepared.Digest, signer, options, cancellationToken).ConfigureAwait(false);
        prepared.Fill(cms);
        await output.WriteAsync(prepared.Update, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PdfPreparedSignature> PrepareDeferredAsync(PdfDocument document, string fieldName, PdfSignOptions options, Stream output, CancellationToken cancellationToken)
    {
        if (!output.CanSeek)
        {
            throw new ArgumentException("A prepared signature is completed in place, so the output stream must be seekable.", nameof(output));
        }

        CheckTimestamp(options);
        long start = output.Position;
        Prepared prepared = await PrepareAsync(document, fieldName, options, output, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(prepared.Update, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        PdfTimestampClient? timestamp = options.Level >= PadesLevel.BaselineT ? options.Timestamp : null;
        return new PdfPreparedSignature(output, start, prepared.BaseLength + prepared.ContentsAt, prepared.ReservedSize, prepared.Digest, options.DigestAlgorithm, timestamp);
    }

    /// <summary>
    /// Adds a document timestamp (ISO 32000-2, 12.8.5): an update whose <c>/DocTimeStamp</c> holds an RFC 3161 token over the
    /// whole file before it, as PAdES B-LTA ends with.
    /// </summary>
    public static async Task TimestampAsync(PdfDocument document, PdfTimestampClient client, string? fieldName, int? reservedSize, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        fieldName ??= FreeName(document, "DocTimeStamp");
        int reserved = reservedSize ?? TimestampRoom;
        PdfDictionary dictionary = new()
        {
            ["Type"] = PdfName.Get("DocTimeStamp"),
            ["Filter"] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get("ETSI.RFC3161"),
        };
        Prepared prepared = await PrepareCoreAsync(document, fieldName, dictionary, reserved, client.DigestAlgorithm, output, cancellationToken).ConfigureAwait(false);
        prepared.Fill(await client.RequestAsync(prepared.Digest, cancellationToken).ConfigureAwait(false));
        await output.WriteAsync(prepared.Update, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>B-T and up need a timestamp authority.</summary>
    private static void CheckTimestamp(PdfSignOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Level >= PadesLevel.BaselineT && options.Timestamp is null)
        {
            throw new ArgumentException($"PAdES {options.Level} needs a timestamp authority: set PdfSignOptions.Timestamp.", nameof(options));
        }
    }

    /// <summary>A field name not yet in the form: the stem with the first free number after it.</summary>
    internal static string FreeName(PdfDocument document, string stem)
    {
        HashSet<string> taken = [.. document.Form.GetAllFields().Select(f => f.FullName)];
        for (int i = 1; ; i++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"{stem}{i}");
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>The CMS for a digest: PAdES signed attributes, signed by the signer's key, with the chain, and from B-T a timestamp on the signature.</summary>
    internal static async Task<byte[]> BuildCmsAsync(byte[] digest, PdfSigner signer, PdfSignOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset? signingTime = options.Format == PdfSignatureFormat.Pkcs7 ? options.SigningTime ?? DateTimeOffset.UtcNow : null;
        byte[] attributes = CmsBuilder.SignedAttributes(options.DigestAlgorithm, digest, signer.Certificate, signingTime);
        (byte[] signature, CmsAlgorithm algorithm) = signer.Sign(options.DigestAlgorithm, options.UseRsaPss, attributes);
        List<CmsAttribute> unsigned = [];
        if (options.Level >= PadesLevel.BaselineT && options.Timestamp is { } client)
        {
            // The signature timestamp covers the signature value (RFC 3161, appendix A).
            byte[] token = await client.RequestAsync(client.Hash(signature), cancellationToken).ConfigureAwait(false);
            unsigned.Add(new CmsAttribute(CmsOids.SignatureTimestampToken, [token]));
        }

        return CmsBuilder.SignedData(options.DigestAlgorithm, attributes, algorithm, signature, signer.Certificate, signer.Chain, unsigned);
    }

    /// <summary>
    /// Adds the field and signature dictionary, builds the update, copies the original file to <paramref name="output"/> while
    /// hashing it, patches the byte range and hashes the rest. The update is returned for the caller to fill and write.
    /// </summary>
    private static Task<Prepared> PrepareAsync(PdfDocument document, string fieldName, PdfSignOptions options, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DigestAlgorithm != HashAlgorithmName.SHA256 && options.DigestAlgorithm != HashAlgorithmName.SHA384 && options.DigestAlgorithm != HashAlgorithmName.SHA512)
        {
            throw new ArgumentException("The digest algorithm must be SHA-256, SHA-384 or SHA-512.", nameof(options));
        }

        PdfDictionary signature = new()
        {
            ["Type"] = PdfName.Get("Sig"),
            ["Filter"] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get(options.Format == PdfSignatureFormat.Pades ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached"),
            ["M"] = PdfString.FromText(Metadata.PdfDate.FormatPdf(options.SigningTime ?? document.Clock.GetLocalNow())),
        };
        string? name = options.SignerName ?? options.Signer?.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        Set(signature, "Name", name);
        Set(signature, "Reason", options.Reason);
        Set(signature, "Location", options.Location);
        Set(signature, "ContactInfo", options.ContactInfo);
        signature["Prop_Build"] = new PdfDictionary { ["App"] = new PdfDictionary { ["Name"] = PdfName.Get("Egap") } };
        return PrepareCoreAsync(document, fieldName, signature, options.ReservedSize ?? EstimateSize(options), options.DigestAlgorithm, output, cancellationToken);
    }

    /// <summary>
    /// Puts the dictionary, with the byte range and contents placeholders added, into the field; builds the update; copies
    /// the original to <paramref name="output"/> while hashing it; patches the byte range and hashes the rest.
    /// </summary>
    private static async Task<Prepared> PrepareCoreAsync(PdfDocument document, string fieldName, PdfDictionary signature, int reserved, HashAlgorithmName digestAlgorithm, Stream output, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentNullException.ThrowIfNull(output);
        document.ThrowIfReadOnly();
        PdfSaveOptions save = new() { Mode = PdfSaveMode.Incremental };
        if (document.WritesWhole(save))
        {
            throw new PdfException("The file needed repair, so a signature cannot be added as an update; save it (it is written whole), open the result and sign that.");
        }

        string marker = Marker();
        signature["ByteRange"] = new PdfArray([.. marker.Split(' ').Select(n => (PdfObject)new PdfInteger(long.Parse(n, CultureInfo.InvariantCulture)))]);
        signature["Contents"] = PdfString.FromBytes(new byte[reserved], hex: true);
        SignatureFields.Attach(document, fieldName, document.Objects.Add(signature));
        (byte[] update, long baseLength) = document.BuildUpdate(save) ?? throw new InvalidOperationException("Adding a signature changed nothing.");

        // The byte range placeholder and the contents follow each other in the signature dictionary as written.
        int rangeAt = IndexOf(update, Encoding.ASCII.GetBytes("[" + marker + "]"), 0);
        int contentsAt = IndexOf(update, Encoding.ASCII.GetBytes("<" + new string('0', 2 * reserved) + ">"), 0);
        if (rangeAt < 0 || contentsAt < 0)
        {
            throw new InvalidOperationException("The signature's placeholders were not found in the update.");
        }

        long contentsStart = baseLength + contentsAt;
        long contentsEnd = contentsStart + (2L * reserved) + 2;
        long total = baseLength + update.Length;
        string range = string.Create(CultureInfo.InvariantCulture, $"[0 {contentsStart} {contentsEnd} {total - contentsEnd}]");
        int width = marker.Length + 2;
        if (range.Length > width)
        {
            throw new PdfException("The file is too large for the byte range placeholder.");
        }

        Encoding.ASCII.GetBytes(range.PadRight(width)).CopyTo(update, rangeAt);

        using IncrementalHash hash = IncrementalHash.CreateHash(digestAlgorithm);
        await document.CopyBaseAsync(output, hash, cancellationToken).ConfigureAwait(false);
        hash.AppendData(update, 0, contentsAt);
        hash.AppendData(update, (int)(contentsEnd - baseLength), (int)(total - contentsEnd));
        byte[] digest = hash.GetHashAndReset();
        document.MarkSignedElsewhere();
        return new Prepared(update, baseLength, contentsAt, reserved, digest);
    }

    /// <summary>The bytes to reserve: the certificates, the signature value, the attributes and, from B-T, a timestamp.</summary>
    private static int EstimateSize(PdfSignOptions options)
    {
        if (options.Signer is not { } signer)
        {
            return DeferredDefault;
        }

        int size = Overhead + signer.Certificate.RawData.Length + signer.Chain.Sum(c => c.RawData.Length) + signer.SignatureSize();
        if (options.Level >= PadesLevel.BaselineT)
        {
            size += TimestampRoom;
        }

        return size;
    }

    /// <summary>
    /// A byte range placeholder: four ten-digit numbers no file will hold by chance, so the written update can be searched
    /// for it, and wide enough for any real file's offsets.
    /// </summary>
    private static string Marker()
    {
        Span<byte> random = stackalloc byte[12];
        RandomNumberGenerator.Fill(random);
        long a = 1_000_000_000L + BitConverter.ToUInt32(random), b = 1_000_000_000L + BitConverter.ToUInt32(random[4..]), c = 1_000_000_000L + BitConverter.ToUInt32(random[8..]);
        return string.Create(CultureInfo.InvariantCulture, $"0 {a} {b} {c}");
    }

    private static void Set(PdfDictionary dictionary, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            dictionary[key] = PdfString.FromText(value);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        int at = haystack.AsSpan(from).IndexOf(needle);
        return at < 0 ? -1 : from + at;
    }
}
