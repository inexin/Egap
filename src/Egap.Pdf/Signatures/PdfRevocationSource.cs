using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf;

/// <summary>
/// Where revocation data (OCSP responses and CRLs) comes from (Phase 5 design Q5): <see cref="Online"/> asks each
/// certificate's OCSP responder, then its CRL distribution point, through the caller's <see cref="HttpClient"/>;
/// <see cref="Offline"/> uses responses the caller already has. Egap never opens a connection on its own.
/// </summary>
/// <example>
/// <code>
/// using HttpClient http = new();
/// PdfSignOptions options = new()
/// {
///     Signer = signer,
///     Level = PadesLevel.BaselineLT,
///     Timestamp = new PdfTimestampClient(tsaUri, http),
///     Revocation = PdfRevocationSource.Online(http),
/// };
/// </code>
/// </example>
public abstract class PdfRevocationSource
{
    private protected PdfRevocationSource()
    {
    }

    /// <summary>Asks the OCSP responder each certificate names (AIA), then its CRL distribution point, through <paramref name="http"/>.</summary>
    public static PdfRevocationSource Online(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new OnlineSource(http);
    }

    /// <summary>Uses OCSP responses and CRLs (DER) the caller already has; nothing is fetched.</summary>
    public static PdfRevocationSource Offline(IEnumerable<ReadOnlyMemory<byte>>? ocspResponses = null, IEnumerable<ReadOnlyMemory<byte>>? crls = null)
    {
        return new OfflineSource([.. ocspResponses ?? []], [.. crls ?? []]);
    }

    /// <summary>Revocation data for one certificate: the raw OCSP response or CRL, and what it says; null when there is none.</summary>
    internal abstract Task<RevocationData?> GetAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken);

    /// <summary>The first of the candidates that answers for the certificate, OCSP responses before CRLs.</summary>
    internal static RevocationData? FirstAnswer(IEnumerable<ReadOnlyMemory<byte>> ocsps, IEnumerable<ReadOnlyMemory<byte>> crls, X509Certificate2 certificate, X509Certificate2 issuer)
    {
        foreach (ReadOnlyMemory<byte> response in ocsps)
        {
            if (Ocsp.Read(response, certificate, issuer, out X509Certificate2? responder) is { } answer)
            {
                return new RevocationData(response.ToArray(), null, answer, responder);
            }
        }

        foreach (ReadOnlyMemory<byte> crl in crls)
        {
            if (Crl.Read(crl, certificate, issuer) is { } answer)
            {
                return new RevocationData(null, crl.ToArray(), answer, issuer);
            }
        }

        return null;
    }

    private sealed class OfflineSource(List<ReadOnlyMemory<byte>> ocsps, List<ReadOnlyMemory<byte>> crls) : PdfRevocationSource
    {
        internal override Task<RevocationData?> GetAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken)
        {
            return Task.FromResult(FirstAnswer(ocsps, crls, certificate, issuer));
        }
    }

    private sealed class OnlineSource(HttpClient http) : PdfRevocationSource
    {
        internal override async Task<RevocationData?> GetAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken)
        {
            foreach (string url in OcspUrls(certificate))
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                using ByteArrayContent body = new(Ocsp.Request(certificate, issuer, nonce));
                body.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");
                try
                {
                    using HttpResponseMessage response = await http.PostAsync(new Uri(url), body, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                        if (Ocsp.Read(bytes, certificate, issuer, out X509Certificate2? responder) is { } answer)
                        {
                            return new RevocationData(bytes, null, answer, responder);
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    // An unreachable responder: try the CRL.
                }
            }

            foreach (string url in CrlUrls(certificate))
            {
                try
                {
                    byte[] bytes = await http.GetByteArrayAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                    if (Crl.Read(bytes, certificate, issuer) is { } answer)
                    {
                        return new RevocationData(null, bytes, answer, issuer);
                    }
                }
                catch (HttpRequestException)
                {
                }
            }

            return null;
        }
    }

    /// <summary>The OCSP responder URLs of the certificate's authority information access extension.</summary>
    internal static IEnumerable<string> OcspUrls(X509Certificate2 certificate)
    {
        return certificate.Extensions.OfType<X509AuthorityInformationAccessExtension>().FirstOrDefault()?.EnumerateOcspUris().Where(IsHttp) ?? [];
    }

    /// <summary>The http URLs of the certificate's CRL distribution points (RFC 5280, 4.2.1.13).</summary>
    internal static List<string> CrlUrls(X509Certificate2 certificate)
    {
        List<string> urls = [];
        if (certificate.Extensions["2.5.29.31"] is not { } extension)
        {
            return urls;
        }

        try
        {
            AsnReader points = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (points.HasData)
            {
                AsnReader point = points.ReadSequence();
                if (!point.HasData || !point.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    continue;
                }

                AsnReader name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                if (!name.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    continue;
                }

                AsnReader generalNames = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (generalNames.HasData)
                {
                    Asn1Tag tag = generalNames.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                    {
                        string url = generalNames.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
                        if (IsHttp(url))
                        {
                            urls.Add(url);
                        }
                    }
                    else
                    {
                        generalNames.ReadEncodedValue();
                    }
                }
            }
        }
        catch (AsnContentException)
        {
        }

        return urls;
    }

    private static bool IsHttp(string url)
    {
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One certificate's revocation data: the raw response or CRL, what it says, and who signed it.</summary>
internal sealed record RevocationData(byte[]? Ocsp, byte[]? Crl, RevocationAnswer Answer, X509Certificate2? Responder);
