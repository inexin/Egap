using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Signatures;

/// <summary>
/// Long-term validation data (Phase 5 design Q4; ISO 32000-2, 12.8.4.3; ETSI EN 319 142-1 B-LT and B-LTA): for every
/// signature and timestamp, the certificates of its chain and an OCSP response or CRL for each, written into the catalog's
/// Document Security Store with a VRI entry per signature, in an update of its own; then, for B-LTA, a document timestamp
/// over it all.
/// </summary>
internal static class LongTermWriter
{
    /// <summary>
    /// Adds a DSS update for every signature of <paramref name="document"/>, and an archive timestamp after it when asked.
    /// With <paramref name="copyBase"/> the original file is written to <paramref name="output"/> first; without, the output
    /// already holds it (signing writes the signature update, then this).
    /// </summary>
    public static async Task AddAsync(PdfDocument document, PdfRevocationSource revocation, PdfTimestampClient? archive, Stream output, bool copyBase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentNullException.ThrowIfNull(output);
        PdfSaveOptions save = new() { Mode = PdfSaveMode.Incremental, UpdateModificationDate = false };
        if (document.WritesWhole(save))
        {
            throw new PdfException("The file needed repair, so validation data cannot be added as an update.");
        }

        Collected collected = new();
        foreach (PdfSignature signature in document.Signatures)
        {
            await CollectAsync(signature, revocation, collected, cancellationToken).ConfigureAwait(false);
        }

        WriteDss(document, collected);
        if (document.BuildUpdate(save) is not (byte[] update, _))
        {
            return;
        }

        if (copyBase)
        {
            using IncrementalHash ignored = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await document.CopyBaseAsync(output, ignored, cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync(update, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (archive is not null)
        {
            using PdfDocument withDss = document.OpenWithUpdate(update, writable: true);
            await SignatureWriter.TimestampAsync(withDss, archive, null, null, output, cancellationToken, copyBase: false).ConfigureAwait(false);
        }
    }

    /// <summary>What goes into the DSS: certificates, OCSP responses and CRLs, each once, and the VRI entry of each signature.</summary>
    private sealed class Collected
    {
        public Dictionary<string, byte[]> Certificates { get; } = [];

        public Dictionary<string, byte[]> Ocsps { get; } = [];

        public Dictionary<string, byte[]> Crls { get; } = [];

        public Dictionary<string, (HashSet<string> Certificates, HashSet<string> Ocsps, HashSet<string> Crls)> Vri { get; } = [];

        public static string Key(byte[] data)
        {
            return Convert.ToHexString(SHA256.HashData(data));
        }
    }

    private static async Task CollectAsync(PdfSignature signature, PdfRevocationSource revocation, Collected collected, CancellationToken cancellationToken)
    {
        (HashSet<string> certs, HashSet<string> ocsps, HashSet<string> crls) vri = ([], [], []);
        collected.Vri[VriKey(signature.Contents.Span)] = vri;
        List<(X509Certificate2 Leaf, List<X509Certificate2> Pool)> chains = [];
        if (signature.SubFilter == "ETSI.RFC3161")
        {
            if (Rfc3161TimestampToken.TryDecode(signature.Contents, out Rfc3161TimestampToken? token, out _) && TimestampSigner(token) is { } authority)
            {
                chains.Add((authority, [.. token.AsSignedCms().Certificates]));
            }
        }
        else if (signature.SubFilter != "adbe.x509.rsa_sha1")
        {
            CmsSignedData cms;
            try
            {
                cms = signature.DecodeCms();
            }
            catch (PdfException)
            {
                return;
            }

            List<X509Certificate2> pool = cms.LoadCertificates();
            if (cms.Signers is [CmsSignerInfo signer] && signer.FindCertificate(pool) is { } leaf)
            {
                chains.Add((leaf, pool));
                if (signer.TimestampToken is { } encoded && Rfc3161TimestampToken.TryDecode(encoded, out Rfc3161TimestampToken? token, out _) && TimestampSigner(token) is { } authority)
                {
                    chains.Add((authority, [.. token.AsSignedCms().Certificates]));
                }
            }
        }

        foreach ((X509Certificate2 leaf, List<X509Certificate2> pool) in chains)
        {
            List<X509Certificate2> chain = OrderedChain(leaf, pool);
            for (int i = 0; i < chain.Count; i++)
            {
                vri.certs.Add(Add(collected.Certificates, chain[i].RawData));
                if (i + 1 >= chain.Count || IsSelfIssued(chain[i]))
                {
                    continue;
                }

                if (await revocation.GetAsync(chain[i], chain[i + 1], cancellationToken).ConfigureAwait(false) is not { } data)
                {
                    continue;
                }

                if (data.Ocsp is { } ocsp)
                {
                    vri.ocsps.Add(Add(collected.Ocsps, ocsp));
                    foreach (byte[] certificate in Ocsp.Certificates(ocsp))
                    {
                        vri.certs.Add(Add(collected.Certificates, certificate));
                    }
                }

                if (data.Crl is { } crl)
                {
                    vri.crls.Add(Add(collected.Crls, crl));
                }
            }
        }
    }

    /// <summary>
    /// The chain from <paramref name="leaf"/> up, leaf first, built from the certificates at hand with no trust decided and
    /// nothing downloaded: the order the revocation data is fetched in.
    /// </summary>
    internal static List<X509Certificate2> OrderedChain(X509Certificate2 leaf, IEnumerable<X509Certificate2> pool)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(pool.ToArray());
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        try
        {
            chain.Build(leaf);
        }
        catch (CryptographicException)
        {
            // A certificate OpenSSL cannot read ends the chain where it stands.
            return [leaf];
        }

        return [.. chain.ChainElements.Select(e => e.Certificate)];
    }

    internal static bool IsSelfIssued(X509Certificate2 certificate)
    {
        return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
    }

    private static X509Certificate2? TimestampSigner(Rfc3161TimestampToken token)
    {
        SignedCms cms = token.AsSignedCms();
        return cms.SignerInfos.Count == 1 ? cms.SignerInfos[0].Certificate : null;
    }

    private static string Add(Dictionary<string, byte[]> set, byte[] data)
    {
        string key = Collected.Key(data);
        set.TryAdd(key, data);
        return key;
    }

    /// <summary>The VRI key of a signature: the upper-case hex SHA-1 of its /Contents bytes (12.8.4.4).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "ISO 32000-2 12.8.4.4 names VRI entries by the SHA-1 of the signature; it is a key, not a security measure.")]
    internal static string VriKey(ReadOnlySpan<byte> contents)
    {
        return Convert.ToHexString(SHA1.HashData(contents));
    }

    /// <summary>Writes the collected data into the catalog's /DSS, keeping what it held; each certificate, response and CRL a stream of its own.</summary>
    private static void WriteDss(PdfDocument document, Collected collected)
    {
        PdfDictionary dss = document.Catalog.Get<PdfDictionary>("DSS") ?? [];
        Dictionary<string, PdfReference> written = [];
        PdfArray Merge(string key, Dictionary<string, byte[]> items)
        {
            PdfArray array = dss.Get<PdfArray>(key) is { } existing ? existing : [];
            Dictionary<string, PdfReference> held = [];
            foreach (PdfObject item in array)
            {
                if (item is PdfReference reference && reference.Resolve() is PdfStream stream && Bytes(stream) is { } bytes)
                {
                    held.TryAdd(Collected.Key(bytes), reference);
                }
            }

            foreach ((string hash, byte[] data) in items)
            {
                if (!held.TryGetValue(hash, out PdfReference? reference))
                {
                    reference = Stream(document, data);
                    array.Add(reference);
                    held[hash] = reference;
                }

                written[hash] = reference;
            }

            return array;
        }

        if (collected.Certificates.Count > 0)
        {
            dss["Certs"] = Merge("Certs", collected.Certificates);
        }

        if (collected.Ocsps.Count > 0)
        {
            dss["OCSPs"] = Merge("OCSPs", collected.Ocsps);
        }

        if (collected.Crls.Count > 0)
        {
            dss["CRLs"] = Merge("CRLs", collected.Crls);
        }

        PdfDictionary vri = dss.Get<PdfDictionary>("VRI") ?? [];
        foreach ((string key, (HashSet<string> certs, HashSet<string> ocsps, HashSet<string> crls)) in collected.Vri)
        {
            // A signature's VRI entry, once written, stays as it is: validators treat changing it as tampering.
            if (vri.ContainsKey(key))
            {
                continue;
            }

            PdfDictionary entry = new();
            Put(entry, "Cert", certs, written);
            Put(entry, "OCSP", ocsps, written);
            Put(entry, "CRL", crls, written);
            vri[key] = entry;
        }

        dss["VRI"] = vri;
        if (document.Catalog.Get("DSS") is null)
        {
            document.Catalog["DSS"] = document.Objects.Add(dss);
        }
    }

    private static void Put(PdfDictionary entry, string key, HashSet<string> hashes, Dictionary<string, PdfReference> written)
    {
        PdfArray array = [.. hashes.Where(written.ContainsKey).Select(h => (PdfObject)written[h])];
        if (array.Count > 0)
        {
            entry[key] = array;
        }
    }

    private static PdfReference Stream(PdfDocument document, byte[] data)
    {
        PdfStream stream = new(new PdfDictionary());
        stream.SetDecodedBytes(data);
        return document.Objects.Add(stream);
    }

    private static byte[]? Bytes(PdfStream stream)
    {
        try
        {
            return stream.GetDecodedBytes();
        }
        catch (PdfException)
        {
            return null;
        }
    }
}
