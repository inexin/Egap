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
        if (options.Level >= PadesLevel.BaselineLT)
        {
            // B-LT adds the validation data in an update of its own, B-LTA a document timestamp after it.
            using PdfDocument signed = document.OpenWithUpdate(prepared.Update);
            PdfTimestampClient? archive = options.Level == PadesLevel.BaselineLTA ? options.Timestamp : null;
            await LongTermWriter.AddAsync(signed, options.Revocation!, archive, output, copyBase: false, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<PdfPreparedSignature> PrepareDeferredAsync(PdfDocument document, string fieldName, PdfSignOptions options, Stream output, CancellationToken cancellationToken)
    {
        if (!output.CanSeek)
        {
            throw new ArgumentException("A prepared signature is completed in place, so the output stream must be seekable.", nameof(output));
        }

        CheckTimestamp(options);
        if (options.Level >= PadesLevel.BaselineLT)
        {
            throw new ArgumentException("A prepared signature goes up to B-T; once it is completed, open the file and call AddValidationDataAsync for B-LT or B-LTA.", nameof(options));
        }

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
    public static async Task TimestampAsync(PdfDocument document, PdfTimestampClient client, string? fieldName, int? reservedSize, Stream output, CancellationToken cancellationToken, bool copyBase = true)
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
        Prepared prepared = await PrepareCoreAsync(document, fieldName, dictionary, reserved, client.DigestAlgorithm, output, cancellationToken, guard: false, copyBase: copyBase).ConfigureAwait(false);
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

        if (options.Level >= PadesLevel.BaselineLT && options.Revocation is null)
        {
            throw new ArgumentException($"PAdES {options.Level} needs revocation data: set PdfSignOptions.Revocation.", nameof(options));
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

        // The field's seed values must be met before anything is written (design Q7).
        PdfSignatureField? existing = document.Form.TryGetField(fieldName, out PdfField field) ? field as PdfSignatureField : null;
        SeedValueCheck.Check(existing?.SeedValue, options);

        // A certification (DocMDP) and the field lock (FieldMDP) are signature references (12.8.1); the catalog's /Perms
        // names the certification.
        PdfArray references = [];
        if (options.Certification is { } level)
        {
            if (document.Signatures.Count > 0)
            {
                throw new ArgumentException("A certification signature must be the document's first signature; this document is signed already.", nameof(options));
            }

            references.Add(Reference("DocMDP", new PdfDictionary { ["P"] = new PdfInteger((int)level) }));
        }

        if ((options.FieldLock ?? existing?.Lock) is { } fieldLock)
        {
            PdfDictionary parameters = new() { ["Action"] = PdfName.Get(fieldLock.Action.ToString()) };
            if (fieldLock.Action != PdfFieldLockAction.All)
            {
                parameters["Fields"] = new PdfArray([.. fieldLock.Fields.Select(f => (PdfObject)PdfString.FromText(f))]);
            }

            references.Add(Reference("FieldMDP", parameters));
        }

        if (references.Count > 0)
        {
            signature["Reference"] = references;
        }

        DateTimeOffset time = options.SigningTime ?? document.Clock.GetLocalNow();
        Func<PdfReference>? draw = options.Appearance is { } appearance ? () => appearance.Build(document, name, time, options.Reason, options.Location) : null;
        Action<PdfReference>? certify = options.Certification is null ? null : reference =>
        {
            PdfDictionary perms = document.Catalog.Get<PdfDictionary>("Perms") is { } existingPerms ? existingPerms : [];
            perms["DocMDP"] = reference;
            document.Catalog["Perms"] = perms;
        };
        return PrepareCoreAsync(document, fieldName, signature, options.ReservedSize ?? EstimateSize(options), options.DigestAlgorithm, output, cancellationToken, options.Appearance, draw, certify, !options.AllowInvalidatingSignatures);
    }

    /// <summary>A signature reference dictionary (12.8.1, Table 256) for a transform method and its parameters.</summary>
    private static PdfDictionary Reference(string method, PdfDictionary parameters)
    {
        parameters["Type"] = PdfName.Get("TransformParams");
        parameters["V"] = PdfName.Get("1.2");
        return new PdfDictionary
        {
            ["Type"] = PdfName.Get("SigRef"),
            ["TransformMethod"] = PdfName.Get(method),
            ["TransformParams"] = parameters,
        };
    }

    /// <summary>
    /// Puts the dictionary, with the byte range and contents placeholders added, into the field; builds the update; copies
    /// the original to <paramref name="output"/> while hashing it; patches the byte range and hashes the rest.
    /// </summary>
    private static async Task<Prepared> PrepareCoreAsync(PdfDocument document, string fieldName, PdfDictionary signature, int reserved, HashAlgorithmName digestAlgorithm, Stream output, CancellationToken cancellationToken, PdfSignatureAppearance? appearance = null, Func<PdfReference>? drawAppearance = null, Action<PdfReference>? afterAdd = null, bool guard = true, bool copyBase = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentNullException.ThrowIfNull(output);
        document.ThrowIfReadOnly();
        // A signature changes only what signing needs: no modification date, which a certification signature might forbid.
        PdfSaveOptions save = new() { Mode = PdfSaveMode.Incremental, UpdateModificationDate = false };
        if (document.WritesWhole(save))
        {
            throw new PdfException("The file needed repair, so a signature cannot be added as an update; save it (it is written whole), open the result and sign that.");
        }

        string marker = Marker();
        signature["ByteRange"] = new PdfArray([.. marker.Split(' ').Select(n => (PdfObject)new PdfInteger(long.Parse(n, CultureInfo.InvariantCulture)))]);
        signature["Contents"] = PdfString.FromBytes(new byte[reserved], hex: true);
        PdfReference added = document.Objects.Add(signature);
        SignatureFields.Attach(document, fieldName, added, appearance, drawAppearance);
        afterAdd?.Invoke(added);
        (byte[] update, long baseLength) = document.BuildUpdate(save) ?? throw new InvalidOperationException("Adding a signature changed nothing.");
        if (guard)
        {
            // Signing a document whose certification forbids it breaks that certification (design Q7).
            SignatureGuard.Check(document, rewrite: false, update);
        }

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
        await document.CopyBaseAsync(copyBase ? output : null, hash, cancellationToken).ConfigureAwait(false);
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
