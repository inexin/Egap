using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Signatures.Cms;

/// <summary>What revocation data says about one certificate.</summary>
internal enum RevocationStatus
{
    Good,
    Revoked,
    Unknown,
}

/// <summary>One certificate's status from an OCSP response or a CRL, and the time range it speaks for.</summary>
internal sealed record RevocationAnswer(RevocationStatus Status, DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate, DateTimeOffset? RevokedAt, string Source);

/// <summary>
/// OCSP (RFC 6960) requests and responses, written and read with System.Formats.Asn1 (Phase 5 design Q2). A response is
/// trusted when its signature verifies with the issuer's key or with a responder certificate the issuer gave the
/// OCSPSigning usage.
/// </summary>
internal static class Ocsp
{
    private const string BasicResponse = "1.3.6.1.5.5.7.48.1.1";
    private const string NonceExtension = "1.3.6.1.5.5.7.48.1.2";
    private const string OcspSigning = "1.3.6.1.5.5.7.3.9";

    /// <summary>An OCSP request for one certificate, with SHA-1 CertID hashes (what every responder understands) and a nonce.</summary>
    public static byte[] Request(X509Certificate2 certificate, X509Certificate2 issuer, byte[]? nonce = null)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            using (writer.PushSequence())
            {
                WriteCertId(writer, certificate, issuer);
            }

            if (nonce is not null)
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                using (writer.PushSequence())
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(NonceExtension);
                    AsnWriter value = new(AsnEncodingRules.DER);
                    value.WriteOctetString(nonce);
                    writer.WriteOctetString(value.Encode());
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// The status the response gives for <paramref name="certificate"/>, after checking its signature against the issuer;
    /// null when the response is not successful, names no such certificate, or is not signed by the issuer or its responder.
    /// </summary>
    public static RevocationAnswer? Read(ReadOnlyMemory<byte> response, X509Certificate2 certificate, X509Certificate2 issuer, out X509Certificate2? responder)
    {
        responder = null;
        try
        {
            AsnReader outer = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
            if (outer.ReadEnumeratedValue<ResponseStatus>() != ResponseStatus.Successful || !outer.HasData)
            {
                return null;
            }

            AsnReader bytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            if (bytes.ReadObjectIdentifier() != BasicResponse)
            {
                return null;
            }

            AsnReader basic = new AsnReader(bytes.ReadOctetString(), AsnEncodingRules.BER).ReadSequence();
            ReadOnlyMemory<byte> tbs = basic.PeekEncodedValue();
            AsnReader data = basic.ReadSequence();
            CmsAlgorithm algorithm = CmsAlgorithm.Read(basic);
            byte[] signature = basic.ReadBitString(out _);
            List<X509Certificate2> certificates = [];
            if (basic.HasData)
            {
                AsnReader list = basic.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                while (list.HasData)
                {
                    try
                    {
                        certificates.Add(X509CertificateLoader.LoadCertificate(list.ReadEncodedValue().Span));
                    }
                    catch (CryptographicException)
                    {
                    }
                }
            }

            responder = FindResponder(tbs, algorithm, signature, issuer, certificates);
            if (responder is null)
            {
                return null;
            }

            if (data.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                data.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            }

            data.ReadEncodedValue();
            data.ReadGeneralizedTime();
            AsnReader responses = data.ReadSequence();
            while (responses.HasData)
            {
                AsnReader single = responses.ReadSequence();
                if (!MatchesCertId(single.ReadSequence(), certificate, issuer))
                {
                    continue;
                }

                Asn1Tag tag = single.PeekTag();
                RevocationStatus status = tag.TagValue switch
                {
                    0 => RevocationStatus.Good,
                    1 => RevocationStatus.Revoked,
                    _ => RevocationStatus.Unknown,
                };
                DateTimeOffset? revokedAt = null;
                if (status == RevocationStatus.Revoked)
                {
                    revokedAt = single.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)).ReadGeneralizedTime();
                }
                else
                {
                    single.ReadEncodedValue();
                }

                DateTimeOffset thisUpdate = single.ReadGeneralizedTime();
                DateTimeOffset? nextUpdate = single.HasData && single.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))
                    ? single.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadGeneralizedTime() : null;
                return new RevocationAnswer(status, thisUpdate, nextUpdate, revokedAt, "OCSP");
            }

            return null;
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The certificates an OCSP response carries, for the DSS.</summary>
    public static List<byte[]> Certificates(ReadOnlyMemory<byte> response)
    {
        List<byte[]> found = [];
        try
        {
            AsnReader outer = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
            outer.ReadEnumeratedBytes();
            AsnReader bytes = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            bytes.ReadObjectIdentifier();
            AsnReader basic = new AsnReader(bytes.ReadOctetString(), AsnEncodingRules.BER).ReadSequence();
            basic.ReadEncodedValue();
            basic.ReadEncodedValue();
            basic.ReadBitString(out _);
            if (basic.HasData)
            {
                AsnReader list = basic.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                while (list.HasData)
                {
                    found.Add(list.ReadEncodedValue().ToArray());
                }
            }
        }
        catch (AsnContentException)
        {
        }

        return found;
    }

    private static X509Certificate2? FindResponder(ReadOnlyMemory<byte> tbs, CmsAlgorithm algorithm, byte[] signature, X509Certificate2 issuer, List<X509Certificate2> certificates)
    {
        HashAlgorithmName digest = CmsAlgorithms.SignatureHash(algorithm, HashAlgorithmName.SHA256) ?? HashAlgorithmName.SHA256;
        if (CmsAlgorithms.Verify(algorithm, digest, tbs.Span, signature, issuer))
        {
            return issuer;
        }

        // A delegated responder: issued by the issuer, with the OCSPSigning usage (RFC 6960, 4.2.2.2).
        foreach (X509Certificate2 candidate in certificates)
        {
            bool delegated = candidate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)
                && candidate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == OcspSigning))
                && IssuedBy(candidate, issuer);
            if (delegated && CmsAlgorithms.Verify(algorithm, digest, tbs.Span, signature, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="issuer"/>'s key signed <paramref name="certificate"/>.</summary>
    internal static bool IssuedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        try
        {
            AsnReader cert = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
            ReadOnlyMemory<byte> tbs = cert.ReadEncodedValue();
            CmsAlgorithm algorithm = CmsAlgorithm.Read(cert);
            byte[] signature = cert.ReadBitString(out _);
            HashAlgorithmName digest = CmsAlgorithms.SignatureHash(algorithm, HashAlgorithmName.SHA256) ?? HashAlgorithmName.SHA256;
            return CmsAlgorithms.Verify(algorithm, digest, tbs.Span, signature, issuer);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static void WriteCertId(AsnWriter writer, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        using (writer.PushSequence())
        {
            new CmsAlgorithm(CmsOids.Sha1, new byte[] { 0x05, 0x00 }).Write(writer);
            writer.WriteOctetString(Sha1(issuer.SubjectName.RawData));
            writer.WriteOctetString(Sha1(PublicKeyBits(issuer)));
            writer.WriteInteger(certificate.SerialNumberBytes.Span);
        }
    }

    private static bool MatchesCertId(AsnReader id, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        CmsAlgorithm algorithm = CmsAlgorithm.Read(id);
        if (CmsAlgorithms.HashFromDigestOid(algorithm.Oid) is not HashAlgorithmName hash)
        {
            return false;
        }

        byte[] nameHash = id.ReadOctetString();
        byte[] keyHash = id.ReadOctetString();
        BigInteger serial = id.ReadInteger();
        return nameHash.AsSpan().SequenceEqual(CmsAlgorithms.Hash(hash, issuer.SubjectName.RawData))
            && keyHash.AsSpan().SequenceEqual(CmsAlgorithms.Hash(hash, PublicKeyBits(issuer)))
            && serial == new BigInteger(certificate.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true);
    }

    /// <summary>The subjectPublicKey BIT STRING's bits, which the issuer key hash is over (RFC 6960, 4.1.1).</summary>
    private static byte[] PublicKeyBits(X509Certificate2 certificate)
    {
        AsnReader spki = new AsnReader(certificate.PublicKey.ExportSubjectPublicKeyInfo(), AsnEncodingRules.DER).ReadSequence();
        spki.ReadEncodedValue();
        return spki.ReadBitString(out _);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "RFC 6960's CertID uses SHA-1 as a lookup key, not for security; every responder understands it.")]
    private static byte[] Sha1(ReadOnlySpan<byte> data)
    {
        return SHA1.HashData(data);
    }

    private enum ResponseStatus
    {
        Successful = 0,
        MalformedRequest = 1,
        InternalError = 2,
        TryLater = 3,
        SigRequired = 5,
        Unauthorized = 6,
    }
}

/// <summary>Certificate revocation lists (RFC 5280, 5), read with System.Formats.Asn1 and trusted when the issuer's key signed them.</summary>
internal static class Crl
{
    /// <summary>
    /// The status the CRL gives <paramref name="certificate"/>, after checking it is the issuer's and signed by it; null when
    /// it is another issuer's or the signature does not verify.
    /// </summary>
    public static RevocationAnswer? Read(ReadOnlyMemory<byte> crl, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        try
        {
            AsnReader list = new AsnReader(crl, AsnEncodingRules.BER).ReadSequence();
            ReadOnlyMemory<byte> tbsBytes = list.PeekEncodedValue();
            AsnReader tbs = list.ReadSequence();
            CmsAlgorithm algorithm = CmsAlgorithm.Read(list);
            byte[] signature = list.ReadBitString(out _);
            HashAlgorithmName digest = CmsAlgorithms.SignatureHash(algorithm, HashAlgorithmName.SHA256) ?? HashAlgorithmName.SHA256;
            if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
            {
                tbs.ReadInteger();
            }

            tbs.ReadEncodedValue();
            ReadOnlyMemory<byte> issuerName = tbs.ReadEncodedValue();
            if (!issuerName.Span.SequenceEqual(issuer.SubjectName.RawData) || !CmsAlgorithms.Verify(algorithm, digest, tbsBytes.Span, signature, issuer))
            {
                return null;
            }

            DateTimeOffset thisUpdate = ReadTime(tbs);
            DateTimeOffset? nextUpdate = tbs.HasData && (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) || tbs.PeekTag().HasSameClassAndValue(Asn1Tag.GeneralizedTime)) ? ReadTime(tbs) : null;
            BigInteger serial = new(certificate.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true);
            if (tbs.HasData && tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                AsnReader revoked = tbs.ReadSequence();
                while (revoked.HasData)
                {
                    AsnReader entry = revoked.ReadSequence();
                    if (entry.ReadInteger() == serial)
                    {
                        return new RevocationAnswer(RevocationStatus.Revoked, thisUpdate, nextUpdate, ReadTime(entry), "CRL");
                    }
                }
            }

            return new RevocationAnswer(RevocationStatus.Good, thisUpdate, nextUpdate, null, "CRL");
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTimeOffset ReadTime(AsnReader reader)
    {
        return reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
    }
}
