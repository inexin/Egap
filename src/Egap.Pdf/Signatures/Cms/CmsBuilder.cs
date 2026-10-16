using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Signatures.Cms;

/// <summary>
/// Writes a detached CMS SignedData for a PDF signature (RFC 5652; ETSI EN 319 122-1 for the PAdES attributes). The signed
/// attributes are written first, so they can be signed by any key, in process or not, and the SignedData is put together
/// around the signature afterwards. Written with System.Formats.Asn1 rather than SignedCms, which needs the whole signed
/// content in memory and cannot sign a digest computed while the file streams past.
/// </summary>
internal static class CmsBuilder
{
    /// <summary>
    /// The signed attributes, DER-encoded as a SET OF (the bytes the signature covers): content-type id-data, message-digest,
    /// ESS signing-certificate-v2 for the signer's certificate, the signing time when given (adbe.pkcs7.detached only: PAdES
    /// puts it in /M), then any extra attributes.
    /// </summary>
    public static byte[] SignedAttributes(HashAlgorithmName digest, ReadOnlySpan<byte> contentDigest, X509Certificate2 signer, DateTimeOffset? signingTime = null, IEnumerable<CmsAttribute>? extra = null)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSetOf())
        {
            WriteAttribute(writer, CmsOids.ContentType, w => w.WriteObjectIdentifier(CmsOids.Data));
            byte[] messageDigest = contentDigest.ToArray();
            WriteAttribute(writer, CmsOids.MessageDigest, w => w.WriteOctetString(messageDigest));
            WriteAttribute(writer, CmsOids.SigningCertificateV2, w => WriteSigningCertificateV2(w, digest, signer));
            if (signingTime is { } time)
            {
                WriteAttribute(writer, CmsOids.SigningTime, w => WriteTime(w, time));
            }

            foreach (CmsAttribute attribute in extra ?? [])
            {
                WriteAttribute(writer, attribute.Oid, w =>
                {
                    foreach (ReadOnlyMemory<byte> value in attribute.Values)
                    {
                        w.WriteEncodedValue(value.Span);
                    }
                });
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// A ContentInfo holding the detached SignedData: the signer's certificate and chain, the signed attributes and the
    /// signature, with any unsigned attributes (the signature timestamp).
    /// </summary>
    public static byte[] SignedData(
        HashAlgorithmName digest,
        ReadOnlySpan<byte> signedAttributes,
        CmsAlgorithm signatureAlgorithm,
        ReadOnlySpan<byte> signature,
        X509Certificate2 signer,
        IEnumerable<X509Certificate2> chain,
        IEnumerable<CmsAttribute>? unsignedAttributes = null)
    {
        CmsAlgorithm digestAlgorithm = new(CmsAlgorithms.DigestOid(digest));
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(CmsOids.SignedData);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf())
                {
                    digestAlgorithm.Write(writer);
                }

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(CmsOids.Data);
                }

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    HashSet<string> written = [];
                    foreach (X509Certificate2 certificate in chain.Prepend(signer))
                    {
                        if (written.Add(certificate.Thumbprint))
                        {
                            writer.WriteEncodedValue(certificate.RawData);
                        }
                    }
                }

                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    using (writer.PushSequence())
                    {
                        writer.WriteEncodedValue(signer.IssuerName.RawData);
                        writer.WriteInteger(signer.SerialNumberBytes.Span);
                    }

                    digestAlgorithm.Write(writer);

                    // The signed attributes as [0] IMPLICIT: the same content under the context tag.
                    writer.WriteEncodedValue(Retag(signedAttributes, 0xA0));
                    signatureAlgorithm.Write(writer);
                    writer.WriteOctetString(signature);
                    if (unsignedAttributes?.ToList() is { Count: > 0 } unsigned)
                    {
                        using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)))
                        {
                            foreach (CmsAttribute attribute in unsigned)
                            {
                                WriteAttribute(writer, attribute.Oid, w =>
                                {
                                    foreach (ReadOnlyMemory<byte> value in attribute.Values)
                                    {
                                        w.WriteEncodedValue(value.Span);
                                    }
                                });
                            }
                        }
                    }
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>The same encoding with another first (tag) byte.</summary>
    private static byte[] Retag(ReadOnlySpan<byte> encoded, byte tag)
    {
        byte[] copy = encoded.ToArray();
        copy[0] = tag;
        return copy;
    }

    private static void WriteAttribute(AsnWriter writer, string oid, Action<AsnWriter> values)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            using (writer.PushSetOf())
            {
                values(writer);
            }
        }
    }

    /// <summary>
    /// ESS signing-certificate-v2 (RFC 5035): the hash of the signer's certificate with the signature's digest algorithm,
    /// which is left out when it is SHA-256 (its DEFAULT), and the issuer and serial number.
    /// </summary>
    private static void WriteSigningCertificateV2(AsnWriter writer, HashAlgorithmName digest, X509Certificate2 signer)
    {
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            if (digest != HashAlgorithmName.SHA256)
            {
                new CmsAlgorithm(CmsAlgorithms.DigestOid(digest)).Write(writer);
            }

            writer.WriteOctetString(CmsAlgorithms.Hash(digest, signer.RawData));
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4)))
                {
                    writer.WriteEncodedValue(signer.IssuerName.RawData);
                }

                writer.WriteInteger(signer.SerialNumberBytes.Span);
            }
        }
    }

    /// <summary>A time as UTCTime from 1950 to 2049 and GeneralizedTime otherwise (RFC 5652, 11.3).</summary>
    private static void WriteTime(AsnWriter writer, DateTimeOffset time)
    {
        DateTimeOffset utc = time.ToUniversalTime();
        if (utc.Year is >= 1950 and < 2050)
        {
            writer.WriteUtcTime(utc);
        }
        else
        {
            writer.WriteGeneralizedTime(utc, omitFractionalSeconds: true);
        }
    }
}
