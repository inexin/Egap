using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Signatures.Cms;

/// <summary>One attribute of a signer: its type and each of its encoded values (RFC 5652, 5.3).</summary>
internal sealed record CmsAttribute(string Oid, IReadOnlyList<ReadOnlyMemory<byte>> Values);

/// <summary>
/// A CMS SignedData (RFC 5652, 5), read from a signature's <c>/Contents</c> or a timestamp token. Read with BER rules, since
/// some signers write indefinite lengths; anything after the first element (the zeros that pad <c>/Contents</c>) is ignored.
/// Every malformed structure throws <see cref="PdfException"/>; nothing is checked cryptographically here.
/// </summary>
internal sealed class CmsSignedData
{
    /// <summary>The most bytes decoded, as a guard against hostile input: far above any real signature.</summary>
    public const int MaxLength = 64 << 20;

    private CmsSignedData()
    {
    }

    public int Version { get; private init; }

    public IReadOnlyList<CmsAlgorithm> DigestAlgorithms { get; private init; } = [];

    /// <summary>The type of the signed content: <c>id-data</c> for a PDF signature, <c>id-ct-TSTInfo</c> for a timestamp token.</summary>
    public string ContentType { get; private init; } = "";

    /// <summary>The encapsulated content, or null for a detached signature, as PDF signatures are.</summary>
    public ReadOnlyMemory<byte>? Content { get; private init; }

    /// <summary>The certificates (DER) the signer included, plain X.509 certificates only.</summary>
    public IReadOnlyList<byte[]> Certificates { get; private init; } = [];

    /// <summary>The CRLs (DER) the signer included.</summary>
    public IReadOnlyList<byte[]> Crls { get; private init; } = [];

    public IReadOnlyList<CmsSignerInfo> Signers { get; private init; } = [];

    /// <summary>Decodes a ContentInfo holding a SignedData.</summary>
    /// <exception cref="PdfException">The bytes are not a SignedData Egap can read.</exception>
    public static CmsSignedData Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxLength)
        {
            throw new PdfLimitException(nameof(MaxLength), $"The signature holds more than {MaxLength} bytes.");
        }

        try
        {
            AsnReader contentInfo = new AsnReader(bytes, AsnEncodingRules.BER).ReadSequence();
            string type = contentInfo.ReadObjectIdentifier();
            if (type != CmsOids.SignedData)
            {
                throw new PdfException($"The signature is a CMS {type}, not a SignedData.");
            }

            AsnReader signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            int version = ReadSmallInteger(signedData);
            List<CmsAlgorithm> digests = [];
            AsnReader digestSet = signedData.ReadSetOf(skipSortOrderValidation: true);
            while (digestSet.HasData)
            {
                digests.Add(CmsAlgorithm.Read(digestSet));
            }

            AsnReader encapsulated = signedData.ReadSequence();
            string contentType = encapsulated.ReadObjectIdentifier();
            ReadOnlyMemory<byte>? content = null;
            if (encapsulated.HasData)
            {
                content = ReadOctets(encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)));
            }

            List<byte[]> certificates = [];
            List<byte[]> crls = [];
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                AsnReader set = signedData.ReadSetOf(true, new Asn1Tag(TagClass.ContextSpecific, 0));
                while (set.HasData)
                {
                    // Only plain certificates; attribute certificates and other formats are kept out.
                    Asn1Tag tag = set.PeekTag();
                    ReadOnlyMemory<byte> encoded = set.ReadEncodedValue();
                    if (tag.HasSameClassAndValue(Asn1Tag.Sequence))
                    {
                        certificates.Add(encoded.ToArray());
                    }
                }
            }

            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
            {
                AsnReader set = signedData.ReadSetOf(true, new Asn1Tag(TagClass.ContextSpecific, 1));
                while (set.HasData)
                {
                    Asn1Tag tag = set.PeekTag();
                    ReadOnlyMemory<byte> encoded = set.ReadEncodedValue();
                    if (tag.HasSameClassAndValue(Asn1Tag.Sequence))
                    {
                        crls.Add(encoded.ToArray());
                    }
                }
            }

            List<CmsSignerInfo> signers = [];
            AsnReader signerSet = signedData.ReadSetOf(skipSortOrderValidation: true);
            while (signerSet.HasData)
            {
                signers.Add(CmsSignerInfo.Read(signerSet));
            }

            return new CmsSignedData
            {
                Version = version,
                DigestAlgorithms = digests,
                ContentType = contentType,
                Content = content,
                Certificates = certificates,
                Crls = crls,
                Signers = signers,
            };
        }
        catch (Exception e) when (e is AsnContentException or ArgumentException or CryptographicException or OverflowException)
        {
            throw new PdfException($"The signature is not a CMS SignedData Egap can read: {e.Message}");
        }
    }

    /// <summary>Loads each certificate .NET can read; one it cannot is left out.</summary>
    public List<X509Certificate2> LoadCertificates()
    {
        List<X509Certificate2> loaded = [];
        foreach (byte[] certificate in Certificates)
        {
            try
            {
                loaded.Add(X509CertificateLoader.LoadCertificate(certificate));
            }
            catch (CryptographicException)
            {
                // A certificate .NET cannot parse cannot be the signer's or part of its chain.
            }
        }

        return loaded;
    }

    internal static int ReadSmallInteger(AsnReader reader)
    {
        BigInteger value = reader.ReadInteger();
        return value >= int.MinValue && value <= int.MaxValue ? (int)value : throw new AsnContentException("An integer is out of range.");
    }

    /// <summary>An OCTET STRING, primitive or constructed (BER).</summary>
    internal static byte[] ReadOctets(AsnReader reader)
    {
        return reader.ReadOctetString();
    }
}

/// <summary>Who signed: the issuer and serial number of the signer's certificate, or its subject key identifier (RFC 5652, 5.3).</summary>
internal sealed record CmsSignerId(ReadOnlyMemory<byte>? Issuer, BigInteger? SerialNumber, ReadOnlyMemory<byte>? SubjectKeyIdentifier)
{
    /// <summary>Whether the certificate is the one this names.</summary>
    public bool Matches(X509Certificate2 certificate)
    {
        if (SubjectKeyIdentifier is { } ski)
        {
            return certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault() is { } extension
                && extension.SubjectKeyIdentifierBytes.Span.SequenceEqual(ski.Span);
        }

        return Issuer is { } issuer && SerialNumber is { } serial
            && certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer.Span)
            && new BigInteger(certificate.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true) == serial;
    }
}

/// <summary>One SignerInfo (RFC 5652, 5.3) with the attributes PDF signatures use read out.</summary>
internal sealed class CmsSignerInfo
{
    private CmsSignerInfo()
    {
    }

    public int Version { get; private init; }

    public CmsSignerId Id { get; private init; } = new(null, null, null);

    public CmsAlgorithm DigestAlgorithm { get; private init; }

    /// <summary>The signed attributes as the signature covers them: their encoding with the SET tag (RFC 5652, 5.4); null when there are none.</summary>
    public byte[]? SignedAttributesEncoding { get; private init; }

    public IReadOnlyList<CmsAttribute> SignedAttributes { get; private init; } = [];

    public CmsAlgorithm SignatureAlgorithm { get; private init; }

    public byte[] Signature { get; private init; } = [];

    public IReadOnlyList<CmsAttribute> UnsignedAttributes { get; private init; } = [];

    /// <summary>The digest algorithm as a hash .NET computes, or null when Egap does not know it.</summary>
    public HashAlgorithmName? DigestHash => CmsAlgorithms.HashFromDigestOid(DigestAlgorithm.Oid);

    /// <summary>The message-digest attribute: the hash of the signed content; null when missing or not one OCTET STRING.</summary>
    public byte[]? MessageDigest => Single(SignedAttributes, CmsOids.MessageDigest) is { } value ? TryRead(value, r => r.ReadOctetString()) : null;

    /// <summary>The content-type attribute.</summary>
    public string? ContentType => Single(SignedAttributes, CmsOids.ContentType) is { } value ? TryRead(value, r => r.ReadObjectIdentifier()) : null;

    /// <summary>The signing-time attribute, which PAdES leaves out (the time goes in /M).</summary>
    public DateTimeOffset? SigningTime => Single(SignedAttributes, CmsOids.SigningTime) is { } value ? TryReadTime(value) : null;

    /// <summary>The certificate hashes of the ESS signing-certificate-v2 attribute (RFC 5035), or of v1 (SHA-1), in order.</summary>
    public IReadOnlyList<(HashAlgorithmName Hash, byte[] CertificateHash)> SigningCertificateHashes => ReadSigningCertificates();

    /// <summary>The RFC 3161 timestamp token of the signature (id-aa-signatureTimeStampToken): a ContentInfo, or null.</summary>
    public ReadOnlyMemory<byte>? TimestampToken => Single(UnsignedAttributes, CmsOids.SignatureTimestampToken);

    /// <summary>Whether the signer uses a weak digest (SHA-1), read with a warning (design Q4).</summary>
    public bool UsesWeakDigest => DigestHash is { } hash && CmsAlgorithms.IsWeak(hash)
        || CmsAlgorithms.SignatureHash(SignatureAlgorithm, DigestHash ?? HashAlgorithmName.SHA256) is { } signatureHash && CmsAlgorithms.IsWeak(signatureHash);

    internal static CmsSignerInfo Read(AsnReader set)
    {
        AsnReader info = set.ReadSequence();
        int version = CmsSignedData.ReadSmallInteger(info);
        CmsSignerId id;
        if (info.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            id = new CmsSignerId(null, null, info.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0)));
        }
        else
        {
            AsnReader issuerAndSerial = info.ReadSequence();
            ReadOnlyMemory<byte> issuer = issuerAndSerial.ReadEncodedValue();
            id = new CmsSignerId(issuer, issuerAndSerial.ReadInteger(), null);
        }

        CmsAlgorithm digest = CmsAlgorithm.Read(info);
        byte[]? signedEncoding = null;
        List<CmsAttribute> signed = [];
        Asn1Tag signedTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
        if (info.PeekTag().HasSameClassAndValue(signedTag))
        {
            ReadOnlyMemory<byte> raw = info.PeekEncodedValue();
            signed = ReadAttributes(info.ReadSetOf(true, signedTag));

            // The signature covers the attributes as a SET OF: the same bytes with the universal SET tag.
            signedEncoding = raw.ToArray();
            signedEncoding[0] = 0x31;
        }

        CmsAlgorithm signatureAlgorithm = CmsAlgorithm.Read(info);
        byte[] signature = info.ReadOctetString();
        List<CmsAttribute> unsigned = [];
        Asn1Tag unsignedTag = new(TagClass.ContextSpecific, 1, isConstructed: true);
        if (info.HasData && info.PeekTag().HasSameClassAndValue(unsignedTag))
        {
            unsigned = ReadAttributes(info.ReadSetOf(true, unsignedTag));
        }

        return new CmsSignerInfo
        {
            Version = version,
            Id = id,
            DigestAlgorithm = digest,
            SignedAttributesEncoding = signedEncoding,
            SignedAttributes = signed,
            SignatureAlgorithm = signatureAlgorithm,
            Signature = signature,
            UnsignedAttributes = unsigned,
        };
    }

    /// <summary>The certificate among <paramref name="certificates"/> this signer names, or null.</summary>
    public X509Certificate2? FindCertificate(IEnumerable<X509Certificate2> certificates)
    {
        return certificates.FirstOrDefault(Id.Matches);
    }

    private static List<CmsAttribute> ReadAttributes(AsnReader set)
    {
        List<CmsAttribute> attributes = [];
        while (set.HasData)
        {
            AsnReader attribute = set.ReadSequence();
            string oid = attribute.ReadObjectIdentifier();
            AsnReader values = attribute.ReadSetOf(skipSortOrderValidation: true);
            List<ReadOnlyMemory<byte>> encoded = [];
            while (values.HasData)
            {
                encoded.Add(values.ReadEncodedValue());
            }

            attributes.Add(new CmsAttribute(oid, encoded));
        }

        return attributes;
    }

    /// <summary>The one value of the attribute; null when it is missing, repeated or has other than one value (RFC 5652, 11).</summary>
    private static ReadOnlyMemory<byte>? Single(IReadOnlyList<CmsAttribute> attributes, string oid)
    {
        CmsAttribute[] found = [.. attributes.Where(a => a.Oid == oid)];
        return found is [{ Values: [var value] }] ? value : (ReadOnlyMemory<byte>?)null;
    }

    private static T? TryRead<T>(ReadOnlyMemory<byte> value, Func<AsnReader, T> read)
        where T : class
    {
        try
        {
            AsnReader reader = new(value, AsnEncodingRules.BER);
            T result = read(reader);
            return reader.HasData ? null : result;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadTime(ReadOnlyMemory<byte> value)
    {
        try
        {
            AsnReader reader = new(value, AsnEncodingRules.BER);
            return reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ? reader.ReadUtcTime() : reader.ReadGeneralizedTime();
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private List<(HashAlgorithmName, byte[])> ReadSigningCertificates()
    {
        List<(HashAlgorithmName, byte[])> hashes = [];
        try
        {
            if (Single(SignedAttributes, CmsOids.SigningCertificateV2) is { } v2)
            {
                AsnReader certs = new AsnReader(v2, AsnEncodingRules.BER).ReadSequence().ReadSequence();
                while (certs.HasData)
                {
                    AsnReader id = certs.ReadSequence();
                    HashAlgorithmName hash = HashAlgorithmName.SHA256;
                    if (id.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                    {
                        hash = CmsAlgorithms.HashFromDigestOid(CmsAlgorithm.Read(id).Oid) ?? default;
                    }

                    if (hash != default)
                    {
                        hashes.Add((hash, id.ReadOctetString()));
                    }
                }
            }
            else if (Single(SignedAttributes, CmsOids.SigningCertificate) is { } v1)
            {
                AsnReader certs = new AsnReader(v1, AsnEncodingRules.BER).ReadSequence().ReadSequence();
                while (certs.HasData)
                {
                    hashes.Add((HashAlgorithmName.SHA1, certs.ReadSequence().ReadOctetString()));
                }
            }
        }
        catch (AsnContentException)
        {
            // A broken attribute names no certificate.
        }

        return hashes;
    }
}
