using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Signatures;

/// <summary>
/// Verifies one signature (Phase 5 design Q8): integrity by its SubFilter (ETSI.CAdES.detached and adbe.pkcs7.detached over
/// the byte range, adbe.pkcs7.sha1 over a SHA-1 digest the CMS holds, adbe.x509.rsa_sha1 as a raw RSA signature, ETSI.RFC3161
/// as a document timestamp), the chain to the caller's trust anchors with .NET's X509Chain, and the signature timestamp
/// with .NET's Rfc3161TimestampToken. Revocation and the changes since signing are decided by their own steps.
/// </summary>
internal static class SignatureVerifier
{
    private const string TimeStampingUsage = "1.3.6.1.5.5.7.3.8";

    public static PdfSignatureReport Verify(PdfSignature signature, PdfVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PdfSignatureReport report = new(signature);
        DateTimeOffset time = options.VerificationTime ?? DateTimeOffset.UtcNow;

        // The file's own validation data: its DSS certificates help build chains, its responses answer for revocation.
        RevocationCheck.Store dss = RevocationCheck.ReadDss(signature.Document);
        options = options with { ExtraCertificates = [.. options.ExtraCertificates, .. dss.Certificates] };
        bool readable = signature.ByteRange.Count > 0 && (signature.ByteRangeIssues & (PdfByteRangeIssues.OutsideFile | PdfByteRangeIssues.Overlapping)) == 0;
        if (signature.ByteRangeIssues != PdfByteRangeIssues.None)
        {
            report.Problem($"The byte range is wrong: {signature.ByteRangeIssues}.");
        }

        Dictionary<HashAlgorithmName, byte[]> hashes = [];
        byte[]? RangeHash(HashAlgorithmName hash)
        {
            if (!readable)
            {
                return null;
            }

            if (!hashes.TryGetValue(hash, out byte[]? value))
            {
                using Stream signed = signature.OpenSignedBytes();
                value = CmsAlgorithms.Hash(hash, signed);
                hashes[hash] = value;
            }

            return value;
        }

        try
        {
            switch (signature.SubFilter)
            {
                case "ETSI.RFC3161":
                    VerifyDocumentTimestamp(signature, options, time, report, RangeHash, dss);
                    break;
                case "adbe.x509.rsa_sha1":
                    VerifyRsaSha1(signature, options, time, report, RangeHash, dss);
                    break;
                default:
                    VerifyCms(signature, options, time, report, RangeHash, dss);
                    break;
            }
        }
        catch (PdfException e)
        {
            report.Integrity = PdfVerdict.Invalid;
            report.Problem(e.Message);
        }

        // A byte range that does not cover exactly the file around /Contents can never be valid, whatever the CMS says.
        if (signature.ByteRangeIssues != PdfByteRangeIssues.None && report.Integrity == PdfVerdict.Valid)
        {
            report.Integrity = PdfVerdict.Invalid;
        }

        if (signature.Revision is null)
        {
            report.ChangesSince = PdfVerdict.Indeterminate;
            report.Problem("The signature covers no whole revision, so what came after it cannot be told apart.");
        }
        else if (signature.Revision.IsLatest)
        {
            report.ChangesSince = PdfVerdict.NotApplicable;
        }
        else
        {
            List<PdfSignatureChange> changes = ChangeAnalyzer.Analyze(signature.Document, signature, out _);
            report.Changes = changes;
            report.ChangesSince = ChangeAnalyzer.Verdict(changes);
            foreach (PdfSignatureChange change in changes.Where(c => c.Verdict != PdfChangeVerdict.Allowed))
            {
                report.Problem($"Revision {change.Revision}: {change.Description} ({change.Verdict})");
            }
        }

        return report;
    }

    /// <summary>ETSI.CAdES.detached, adbe.pkcs7.detached and adbe.pkcs7.sha1: a CMS SignedData with one signer.</summary>
    private static void VerifyCms(PdfSignature signature, PdfVerifyOptions options, DateTimeOffset time, PdfSignatureReport report, Func<HashAlgorithmName, byte[]?> rangeHash, RevocationCheck.Store dss)
    {
        CmsSignedData cms = signature.DecodeCms();
        if (cms.Signers is not [CmsSignerInfo signer])
        {
            report.Integrity = PdfVerdict.Invalid;
            report.Problem($"The CMS has {cms.Signers.Count} signers; a PDF signature has one.");
            return;
        }

        List<X509Certificate2> certificates = cms.LoadCertificates();
        X509Certificate2? certificate = signer.FindCertificate(certificates) ?? signer.FindCertificate(options.ExtraCertificates);
        report.SignerCertificate = certificate;
        report.SignatureAlgorithm = CmsAlgorithms.SignatureName(signer.SignatureAlgorithm);
        if (signer.DigestHash is not HashAlgorithmName digest)
        {
            report.Integrity = PdfVerdict.Indeterminate;
            report.Problem($"The digest algorithm {signer.DigestAlgorithm.Oid} is not supported.");
            return;
        }

        report.DigestAlgorithm = digest.Name;
        if (signer.UsesWeakDigest)
        {
            report.Warning("The signature uses SHA-1, which is no longer safe against forgery.");
        }

        // adbe.pkcs7.sha1 signs a SHA-1 digest of the byte range, which the CMS carries as its content.
        byte[]? contentHash;
        if (signature.SubFilter == "adbe.pkcs7.sha1")
        {
            report.Warning("adbe.pkcs7.sha1 is deprecated (ISO 32000-2) and rests on SHA-1.");
            if (cms.Content is not { } content || rangeHash(HashAlgorithmName.SHA1) is not { } sha1 || !content.Span.SequenceEqual(sha1))
            {
                report.Integrity = PdfVerdict.Invalid;
                report.Problem("The SHA-1 digest in the CMS is not the digest of the byte range.");
                return;
            }

            contentHash = CmsAlgorithms.Hash(digest, content.Span);
        }
        else
        {
            if (cms.Content is not null)
            {
                report.Warning("The CMS carries its own content, which a detached PDF signature does not use.");
            }

            contentHash = rangeHash(digest);
        }

        if (certificate is null)
        {
            report.Integrity = PdfVerdict.Indeterminate;
            report.Problem("The signer's certificate is not in the signature.");
            return;
        }

        report.Integrity = CheckSigner(signature, signer, digest, contentHash, certificate, report);
        if (signer.TimestampToken is { } token)
        {
            report.Timestamp = CheckToken(token, signer.Signature, options, time, report, [.. certificates]);
        }

        report.Chain = CheckChain(certificate, certificates, options, time, null, report, "signer's");
        if (signature.Kind == PdfSignatureKind.Certification || signature.SubFilter == "ETSI.CAdES.detached")
        {
            CheckKeyUsage(certificate, report);
        }

        report.Revocation = Revocation(report, certificate, certificates, cms, signer, dss, options, time);
    }

    /// <summary>The revocation verdict for the chain verified, or the chain the certificates at hand make, at the time a valid timestamp proves.</summary>
    private static PdfVerdict Revocation(PdfSignatureReport report, X509Certificate2 certificate, IEnumerable<X509Certificate2> certificates, CmsSignedData? cms, CmsSignerInfo? signer, RevocationCheck.Store dss, PdfVerifyOptions options, DateTimeOffset time)
    {
        IReadOnlyList<X509Certificate2> chain = report.ChainCertificates.Count > 0 ? report.ChainCertificates : LongTermWriter.OrderedChain(certificate, [.. certificates, .. options.ExtraCertificates]);
        DateTimeOffset proven = report.Timestamp == PdfVerdict.Valid && report.TimestampTime is { } stamped ? stamped : time;
        return RevocationCheck.Check(chain, cms, signer, dss, options, proven, report);
    }

    /// <summary>The signer's own checks: the message digest and content type when there are signed attributes, the signature, and the PAdES certificate binding.</summary>
    private static PdfVerdict CheckSigner(PdfSignature signature, CmsSignerInfo signer, HashAlgorithmName digest, byte[]? contentHash, X509Certificate2 certificate, PdfSignatureReport report)
    {
        if (contentHash is null)
        {
            report.Problem("The signed bytes cannot be read.");
            return PdfVerdict.Invalid;
        }

        bool verified;
        if (signer.SignedAttributesEncoding is { } attributes)
        {
            if (signer.MessageDigest is not { } messageDigest || !messageDigest.AsSpan().SequenceEqual(contentHash))
            {
                report.Problem("The message digest is not the digest of the signed bytes: the file changed after signing.");
                return PdfVerdict.Invalid;
            }

            if (signer.ContentType != CmsOids.Data)
            {
                report.Problem("The signed content type is not id-data.");
                return PdfVerdict.Invalid;
            }

            verified = CmsAlgorithms.Verify(signer.SignatureAlgorithm, digest, attributes, signer.Signature, certificate);
        }
        else
        {
            verified = CmsAlgorithms.VerifyHash(signer.SignatureAlgorithm, digest, contentHash, signer.Signature, certificate);
        }

        if (!verified)
        {
            report.Problem("The signature value does not verify with the signer's public key.");
            return PdfVerdict.Invalid;
        }

        IReadOnlyList<(HashAlgorithmName Hash, byte[] CertificateHash)> bindings = signer.SigningCertificateHashes;
        if (bindings.Count == 0)
        {
            if (signature.SubFilter == "ETSI.CAdES.detached")
            {
                report.Warning("The PAdES signature has no signing-certificate-v2 attribute binding it to the signer's certificate.");
            }
        }
        else if (!bindings[0].CertificateHash.AsSpan().SequenceEqual(CmsAlgorithms.Hash(bindings[0].Hash, certificate.RawData)))
        {
            report.Problem("The signing-certificate attribute names another certificate than the one that signed.");
            return PdfVerdict.Invalid;
        }

        return PdfVerdict.Valid;
    }

    /// <summary>The old raw RSA signature over the byte range's SHA-1 digest, with the certificates in /Cert.</summary>
    private static void VerifyRsaSha1(PdfSignature signature, PdfVerifyOptions options, DateTimeOffset time, PdfSignatureReport report, Func<HashAlgorithmName, byte[]?> rangeHash, RevocationCheck.Store dss)
    {
        report.Warning("adbe.x509.rsa_sha1 is deprecated (ISO 32000-2) and rests on SHA-1.");
        report.DigestAlgorithm = "SHA1";
        report.SignatureAlgorithm = "RSA";
        byte[] value = RsaSha1Signature.Read(signature.Contents);
        List<X509Certificate2> certificates = RsaSha1Signature.Certificates(signature.Dictionary);
        if (certificates.Count == 0)
        {
            report.Integrity = PdfVerdict.Indeterminate;
            report.Problem("The signature dictionary has no /Cert.");
            return;
        }

        X509Certificate2 certificate = certificates[0];
        report.SignerCertificate = certificate;
        if (rangeHash(HashAlgorithmName.SHA1) is not { } hash)
        {
            report.Integrity = PdfVerdict.Invalid;
            report.Problem("The signed bytes cannot be read.");
            return;
        }

        bool verified = CmsAlgorithms.VerifyHash(new CmsAlgorithm(CmsOids.Sha1WithRsa), HashAlgorithmName.SHA1, hash, value, certificate);
        report.Integrity = verified ? PdfVerdict.Valid : PdfVerdict.Invalid;
        if (!verified)
        {
            report.Problem("The signature value does not verify with the signer's public key.");
        }

        report.Chain = CheckChain(certificate, certificates, options, time, null, report, "signer's");
        report.Revocation = Revocation(report, certificate, certificates, null, null, dss, options, time);
    }

    /// <summary>A document timestamp: an RFC 3161 token whose imprint is the digest of the byte range.</summary>
    private static void VerifyDocumentTimestamp(PdfSignature signature, PdfVerifyOptions options, DateTimeOffset time, PdfSignatureReport report, Func<HashAlgorithmName, byte[]?> rangeHash, RevocationCheck.Store dss)
    {
        if (!Rfc3161TimestampToken.TryDecode(signature.Contents, out Rfc3161TimestampToken? token, out _))
        {
            report.Integrity = PdfVerdict.Invalid;
            report.Problem("The document timestamp is not an RFC 3161 token.");
            return;
        }

        if (HashFor(token.TokenInfo.HashAlgorithmId) is not HashAlgorithmName hash)
        {
            report.Integrity = PdfVerdict.Indeterminate;
            report.Problem($"The timestamp's digest algorithm {token.TokenInfo.HashAlgorithmId.Value} is not supported.");
            return;
        }

        report.DigestAlgorithm = hash.Name;
        report.TimestampTime = token.TokenInfo.Timestamp;
        if (CmsAlgorithms.IsWeak(hash))
        {
            report.Warning("The document timestamp uses SHA-1.");
        }

        if (rangeHash(hash) is not { } digest)
        {
            report.Integrity = PdfVerdict.Invalid;
            report.Problem("The signed bytes cannot be read.");
            return;
        }

        X509Certificate2Collection candidates = [.. options.ExtraCertificates];
        X509Certificate2? authority = null;
        bool verified;
        try
        {
            verified = token.VerifySignatureForHash(digest, hash, out authority, candidates);
        }
        catch (CryptographicException)
        {
            verified = false;
        }

        if (!verified)
        {
            report.Integrity = authority is null && token.TokenInfo.GetMessageHash().Span.SequenceEqual(digest) ? PdfVerdict.Indeterminate : PdfVerdict.Invalid;
            report.Problem(report.Integrity == PdfVerdict.Invalid ? "The document timestamp does not cover the signed bytes, or its signature does not verify." : "The timestamp authority's certificate is not in the token.");
            return;
        }

        report.Integrity = PdfVerdict.Valid;
        report.SignerCertificate = authority;
        report.TimestampCertificate = authority;
        report.SignatureAlgorithm = "RFC 3161";
        List<X509Certificate2> embedded = [.. token.AsSignedCms().Certificates];
        report.Chain = CheckChain(authority!, embedded, options, time, TimeStampingUsage, report, "timestamp authority's");
        report.Revocation = Revocation(report, authority!, embedded, null, null, dss, options, time);
    }

    /// <summary>A signature timestamp: valid, over the signature value, from an authority that chains to a trust anchor.</summary>
    private static PdfVerdict CheckToken(ReadOnlyMemory<byte> encoded, ReadOnlySpan<byte> signatureValue, PdfVerifyOptions options, DateTimeOffset time, PdfSignatureReport report, X509Certificate2[] extra)
    {
        if (!Rfc3161TimestampToken.TryDecode(encoded, out Rfc3161TimestampToken? token, out _))
        {
            report.Problem("The signature timestamp is not an RFC 3161 token.");
            return PdfVerdict.Invalid;
        }

        X509Certificate2Collection candidates = [.. extra, .. options.ExtraCertificates];
        X509Certificate2? authority = null;
        bool verified;
        try
        {
            verified = token.VerifySignatureForData(signatureValue, out authority, candidates);
        }
        catch (CryptographicException)
        {
            verified = false;
        }

        if (!verified)
        {
            report.Problem("The signature timestamp does not cover the signature value, or its own signature does not verify.");
            return PdfVerdict.Invalid;
        }

        report.TimestampTime = token.TokenInfo.Timestamp;
        report.TimestampCertificate = authority;
        if (HashFor(token.TokenInfo.HashAlgorithmId) is { } hash && CmsAlgorithms.IsWeak(hash))
        {
            report.Warning("The signature timestamp uses SHA-1.");
        }

        List<X509Certificate2> embedded = [.. token.AsSignedCms().Certificates];
        PdfSignatureReport scratch = new(report.Signature);
        PdfVerdict chain = CheckChain(authority!, embedded, options, time, TimeStampingUsage, scratch, "timestamp authority's");
        foreach (string problem in scratch.Problems)
        {
            report.Problem(problem);
        }

        return chain;
    }

    /// <summary>
    /// Builds the chain from <paramref name="certificate"/> to a trust anchor at <paramref name="time"/>, revocation left to
    /// its own step. No anchors means indeterminate; a broken chain (a bad signature, a certificate used beyond its rights)
    /// is invalid; an untrusted or incomplete one indeterminate; one that has expired, but was valid when a trusted timestamp
    /// proves the signature existed, is indeterminate too, for long-term validation to decide.
    /// </summary>
    private static PdfVerdict CheckChain(X509Certificate2 certificate, IEnumerable<X509Certificate2> embedded, PdfVerifyOptions options, DateTimeOffset time, string? usage, PdfSignatureReport report, string whose)
    {
        if (options.TrustAnchors.Count == 0 && !options.UseSystemTrust)
        {
            report.Problem($"No trust anchors were given, so the {whose} certificate cannot be trusted.");
            return PdfVerdict.Indeterminate;
        }

        X509Certificate2[] extra = [.. embedded, .. options.ExtraCertificates];
        (bool built, X509ChainStatusFlags flags, List<X509Certificate2> path) = Build(certificate, extra, options, time, usage);
        if (built)
        {
            if (whose == "signer's")
            {
                report.ChainCertificates = path;
            }

            return PdfVerdict.Valid;
        }

        const X509ChainStatusFlags Broken = X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.NotValidForUsage | X509ChainStatusFlags.InvalidBasicConstraints
            | X509ChainStatusFlags.ExplicitDistrust | X509ChainStatusFlags.Cyclic | X509ChainStatusFlags.InvalidExtension | X509ChainStatusFlags.HasNotSupportedCriticalExtension;
        if ((flags & Broken) != 0)
        {
            report.Problem($"The {whose} certificate chain is broken: {flags}.");
            return PdfVerdict.Invalid;
        }

        if ((flags & X509ChainStatusFlags.NotTimeValid) != 0 && report.TimestampTime is { } proven && report.Timestamp == PdfVerdict.Valid && proven < time
            && Build(certificate, extra, options, proven, usage).Built)
        {
            report.Problem($"The {whose} certificate has expired since; it was valid when the timestamp proves the signature existed, which long-term validation decides.");
            return PdfVerdict.Indeterminate;
        }

        report.Problem($"The {whose} certificate does not chain to a trust anchor at the verification time: {flags}.");
        return PdfVerdict.Indeterminate;
    }

    private static (bool Built, X509ChainStatusFlags Flags, List<X509Certificate2> Path) Build(X509Certificate2 certificate, X509Certificate2[] extra, PdfVerifyOptions options, DateTimeOffset time, string? usage)
    {
        (bool built, X509ChainStatusFlags flags, List<X509Certificate2> path) = BuildWith(certificate, extra, options.TrustAnchors, time, usage, system: false);
        if (!built && options.UseSystemTrust)
        {
            (built, flags, path) = BuildWith(certificate, extra, [], time, usage, system: true);
        }

        return (built, flags, path);
    }

    private static (bool Built, X509ChainStatusFlags Flags, List<X509Certificate2> Path) BuildWith(X509Certificate2 certificate, X509Certificate2[] extra, IReadOnlyList<X509Certificate2> anchors, DateTimeOffset time, string? usage, bool system)
    {
        if (!system && anchors.Count == 0)
        {
            return (false, X509ChainStatusFlags.UntrustedRoot, []);
        }

        using X509Chain chain = new();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = time.UtcDateTime;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        if (!system)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors.ToArray());
        }

        if (usage is not null)
        {
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage));
        }

        bool built;
        try
        {
            built = chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            // OpenSSL refuses a certificate whose key it cannot read; the chain is broken, not undecided.
            return (false, X509ChainStatusFlags.InvalidExtension, []);
        }

        X509ChainStatusFlags flags = X509ChainStatusFlags.NoError;
        foreach (X509ChainStatus status in chain.ChainStatus)
        {
            flags |= status.Status;
        }

        List<X509Certificate2> path = [.. chain.ChainElements.Select(e => e.Certificate)];
        return (built, flags, path);
    }

    /// <summary>A signer's key usage, when given, must allow digital signatures or non-repudiation.</summary>
    private static void CheckKeyUsage(X509Certificate2 certificate, PdfSignatureReport report)
    {
        X509KeyUsageExtension? usage;
        try
        {
            usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            _ = usage?.KeyUsages;
        }
        catch (CryptographicException)
        {
            report.Problem("The signer's certificate has a key usage extension that cannot be read.");
            report.Chain = PdfVerdict.Invalid;
            return;
        }

        if (usage is not null && (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
        {
            report.Problem("The signer's certificate does not allow digital signatures.");
            report.Chain = PdfVerdict.Invalid;
        }
    }

    private static HashAlgorithmName? HashFor(Oid oid)
    {
        return oid.Value is { } value ? CmsAlgorithms.HashFromDigestOid(value) : null;
    }
}
