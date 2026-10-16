using System.Formats.Asn1;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Tests.Signatures;

/// <summary>
/// An RFC 3161 timestamp authority inside the test process, behind an <see cref="HttpMessageHandler"/>: it answers each
/// query with a token signed by its own certificate (critical timeStamping EKU, ESS signing-certificate-v2), so timestamps
/// are tested without a network. It can also refuse, fail or answer nonsense.
/// </summary>
internal sealed class TestTimestampAuthority : HttpMessageHandler
{
    private readonly RSA key = RSA.Create(2048);
    private long serial;

    public TestTimestampAuthority()
    {
        CertificateRequest request = new("CN=Egap Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>What the authority does: answer, refuse (status 2), fail with HTTP 500, or send bytes that are no response.</summary>
    public string Mode { get; set; } = "answer";

    public int Requests { get; private set; }

    public static Uri Url { get; } = new("http://tsa.test/");

    public static PdfTimestampClient Client(HttpClient http, HashAlgorithmName? digest = null)
    {
        return new PdfTimestampClient(Url, http) { DigestAlgorithm = digest ?? HashAlgorithmName.SHA256 };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Assert.Equal("application/timestamp-query", request.Content!.Headers.ContentType!.MediaType);
        byte[] query = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        return Mode switch
        {
            "fail" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "nonsense" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x30, 0x03, 0x02, 0x01, 0x05]) },
            "refuse" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Response(2, null)) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Response(0, Token(query))) },
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Certificate.Dispose();
            key.Dispose();
        }

        base.Dispose(disposing);
    }

    private byte[] Token(byte[] query)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(query, out Rfc3161TimestampRequest? request, out _));
        byte[] serialNumber = new BigInteger(++serial).ToByteArray(isUnsigned: false, isBigEndian: true);
        Rfc3161TimestampTokenInfo info = new(new Oid("1.3.6.1.4.1.99999.1"), request!.HashAlgorithmId, request.GetMessageHash(), serialNumber, DateTimeOffset.UtcNow, nonce: request.GetNonce());
        SignedCms cms = new(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        CmsSigner signer = new(Certificate) { IncludeOption = request.RequestSignerCertificate ? X509IncludeOption.EndCertOnly : X509IncludeOption.None };
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", SigningCertificateV2()));
        cms.ComputeSignature(signer);
        return cms.Encode();
    }

    private byte[] SigningCertificateV2()
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence())
        using (writer.PushSequence())
        {
            writer.WriteOctetString(SHA256.HashData(Certificate.RawData));
        }

        return writer.Encode();
    }

    private static byte[] Response(int status, byte[]? token)
    {
        AsnWriter writer = new(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(status);
            }

            if (token is not null)
            {
                writer.WriteEncodedValue(token);
            }
        }

        return writer.Encode();
    }
}
