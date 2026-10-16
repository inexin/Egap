using System.Formats.Asn1;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// A small PKI inside the test process, behind an <see cref="HttpMessageHandler"/> at http://ca.test: a root CA that issues a
/// signer, a revoked signer and a timestamp authority, answers OCSP requests (signed by the root itself), serves its CRL and
/// grants RFC 3161 timestamps. Long-term validation is tested with it without a network.
/// </summary>
internal sealed class TestCertificateAuthority : HttpMessageHandler
{
    private readonly RSA rootKey = RSA.Create(2048);
    private readonly RSA tsaKey = RSA.Create(2048);
    private long serial;

    public TestCertificateAuthority()
    {
        CertificateRequest rootRequest = new("CN=Egap Test LTV Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(5));

        SignerKey = RSA.Create(2048);
        Signer = Issue("CN=Egap Test LTV Signer", SignerKey, null);
        RevokedKey = RSA.Create(2048);
        Revoked = Issue("CN=Egap Test LTV Revoked", RevokedKey, null);
        Tsa = Issue("CN=Egap Test LTV TSA", tsaKey, "1.3.6.1.5.5.7.3.8");
        RevokedAt = DateTimeOffset.UtcNow.AddHours(-1);
    }

    public static Uri Base { get; } = new("http://ca.test/");

    public X509Certificate2 Root { get; }

    public X509Certificate2 Signer { get; }

    public RSA SignerKey { get; }

    public X509Certificate2 Revoked { get; }

    public RSA RevokedKey { get; }

    public X509Certificate2 Tsa { get; }

    public DateTimeOffset RevokedAt { get; }

    /// <summary>Whether OCSP is answered; off, clients fall back to the CRL.</summary>
    public bool OcspUp { get; set; } = true;

    public int OcspRequests { get; private set; }

    public int CrlRequests { get; private set; }

    public static PdfTimestampClient Timestamps(HttpClient http) => new(new Uri(Base, "tsa"), http);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/ocsp" when OcspUp:
                OcspRequests++;
                return Ok(OcspResponse(body));
            case "/crl":
                CrlRequests++;
                return Ok(Crl());
            case "/tsa":
                return Ok(TimestampResponse(body));
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (IDisposable item in (IDisposable[])[Root, Signer, Revoked, Tsa, rootKey, tsaKey, SignerKey, RevokedKey])
            {
                item.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static HttpResponseMessage Ok(byte[] content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
    }

    private X509Certificate2 Issue(string name, RSA key, string? usage)
    {
        CertificateRequest request = new(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        if (usage is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], true));
        }

        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([new Uri(Base, "ocsp").ToString()], null));
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([new Uri(Base, "crl").ToString()]));
        byte[] serialNumber = new BigInteger(Interlocked.Increment(ref serial) + 1000).ToByteArray(isUnsigned: false, isBigEndian: true);
        using X509Certificate2 issued = request.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), serialNumber);
        return issued.CopyWithPrivateKey(key);
    }

    private byte[] Crl()
    {
        CertificateRevocationListBuilder builder = new();
        builder.AddEntry(Revoked, RevokedAt, X509RevocationReason.KeyCompromise);
        return builder.Build(Root, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>A BasicOCSPResponse for the request's one certificate, signed by the root as the issuer itself.</summary>
    private byte[] OcspResponse(byte[] request)
    {
        AsnReader single = new AsnReader(request, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence().ReadSequence();
        ReadOnlyMemory<byte> id = single.PeekEncodedValue();
        AsnReader fields = single.ReadSequence();
        fields.ReadEncodedValue();
        fields.ReadOctetString();
        fields.ReadOctetString();
        BigInteger serialNumber = fields.ReadInteger();
        bool revoked = serialNumber == new BigInteger(Revoked.SerialNumberBytes.Span, isUnsigned: false, isBigEndian: true);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        AsnWriter data = new(AsnEncodingRules.DER);
        using (data.PushSequence())
        {
            using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
            {
                data.WriteOctetString(SHA1Of(Root));
            }

            data.WriteGeneralizedTime(now, omitFractionalSeconds: true);
            using (data.PushSequence())
            using (data.PushSequence())
            {
                data.WriteEncodedValue(id.Span);
                if (revoked)
                {
                    using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                    {
                        data.WriteGeneralizedTime(RevokedAt, omitFractionalSeconds: true);
                    }
                }
                else
                {
                    data.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                }

                data.WriteGeneralizedTime(now.AddMinutes(-1), omitFractionalSeconds: true);
                using (data.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    data.WriteGeneralizedTime(now.AddDays(1), omitFractionalSeconds: true);
                }
            }
        }

        byte[] tbs = data.Encode();
        AsnWriter basic = new(AsnEncodingRules.DER);
        using (basic.PushSequence())
        {
            basic.WriteEncodedValue(tbs);
            using (basic.PushSequence())
            {
                basic.WriteObjectIdentifier("1.2.840.113549.1.1.11");
                basic.WriteNull();
            }

            basic.WriteBitString(rootKey.SignData(tbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        AsnWriter response = new(AsnEncodingRules.DER);
        using (response.PushSequence())
        {
            response.WriteEnumeratedValue(OcspStatus.Successful);
            using (response.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (response.PushSequence())
            {
                response.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                response.WriteOctetString(basic.Encode());
            }
        }

        return response.Encode();
    }

    private byte[] TimestampResponse(byte[] query)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(query, out Rfc3161TimestampRequest? request, out _));
        byte[] serialNumber = new BigInteger(Interlocked.Increment(ref serial) + 5000).ToByteArray(isUnsigned: false, isBigEndian: true);
        Rfc3161TimestampTokenInfo info = new(new Oid("1.3.6.1.4.1.99999.2"), request!.HashAlgorithmId, request.GetMessageHash(), serialNumber, DateTimeOffset.UtcNow, nonce: request.GetNonce());
        SignedCms cms = new(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        CmsSigner signer = new(Tsa) { IncludeOption = X509IncludeOption.EndCertOnly };
        AsnWriter essCertId = new(AsnEncodingRules.DER);
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        using (essCertId.PushSequence())
        {
            essCertId.WriteOctetString(SHA256.HashData(Tsa.RawData));
        }

        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", essCertId.Encode()));
        cms.ComputeSignature(signer);
        AsnWriter response = new(AsnEncodingRules.DER);
        using (response.PushSequence())
        {
            using (response.PushSequence())
            {
                response.WriteInteger(0);
            }

            response.WriteEncodedValue(cms.Encode());
        }

        return response.Encode();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "An OCSP responder key hash is SHA-1 by RFC 6960.")]
    private static byte[] SHA1Of(X509Certificate2 certificate)
    {
        AsnReader spki = new AsnReader(certificate.PublicKey.ExportSubjectPublicKeyInfo(), AsnEncodingRules.DER).ReadSequence();
        spki.ReadEncodedValue();
        return SHA1.HashData(spki.ReadBitString(out _));
    }

    private enum OcspStatus
    {
        Successful = 0,
    }
}
