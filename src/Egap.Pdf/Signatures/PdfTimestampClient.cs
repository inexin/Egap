using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace Egap.Pdf;

/// <summary>
/// An RFC 3161 timestamp authority, reached through the caller's <see cref="HttpClient"/> (Phase 5 design Q5): Egap never
/// opens a connection on its own and has no default server. The client carries the caller's proxy, timeouts, retries and any
/// credentials the authority wants. Requests are built and responses checked with .NET's <see cref="Rfc3161TimestampRequest"/>.
/// </summary>
/// <example>
/// <code>
/// using HttpClient http = new();
/// PdfSignOptions options = new()
/// {
///     Signer = signer,
///     Level = PadesLevel.BaselineT,
///     Timestamp = new PdfTimestampClient(new Uri("http://timestamp.example/tsa"), http),
/// };
/// </code>
/// </example>
public sealed class PdfTimestampClient
{
    private readonly HttpClient http;

    /// <summary>A client for the authority at <paramref name="url"/>.</summary>
    /// <param name="url">The authority's address.</param>
    /// <param name="http">The HTTP client requests go through; it stays the caller's to dispose.</param>
    public PdfTimestampClient(Uri url, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(http);
        Url = url;
        this.http = http;
    }

    /// <summary>The authority's address.</summary>
    public Uri Url { get; }

    /// <summary>The digest algorithm of the message imprint: SHA-256 (the default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName DigestAlgorithm { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>The timestamp policy to ask for (an OID), or <see langword="null"/> for the authority's default.</summary>
    public string? Policy { get; init; }

    /// <summary>
    /// Gets a timestamp token for data whose hash with <see cref="DigestAlgorithm"/> is <paramref name="hash"/>, and checks it
    /// answers the request: granted, the same imprint and nonce, and signed by the authority's certificate.
    /// </summary>
    /// <returns>The token, a CMS ContentInfo (DER).</returns>
    /// <exception cref="PdfException">The authority refused, or answered with something that is not the token asked for.</exception>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    internal async Task<byte[]> RequestAsync(ReadOnlyMemory<byte> hash, CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        nonce[0] &= 0x7F;
        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromHash(hash, DigestAlgorithm, Policy is null ? null : new Oid(Policy), nonce, requestSignerCertificates: true);
        using ByteArrayContent body = new(request.Encode());
        body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using HttpResponseMessage response = await http.PostAsync(Url, body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The timestamp authority {Url} answered {(int)response.StatusCode} {response.ReasonPhrase}."));
        }

        byte[] reply = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Rfc3161TimestampToken token = request.ProcessResponse(reply, out int read);
            return token.AsSignedCms().Encode();
        }
        catch (CryptographicException e)
        {
            throw new PdfException($"The timestamp authority {Url} did not grant a valid timestamp: {e.Message}");
        }
    }

    /// <summary>The hash of data with <see cref="DigestAlgorithm"/>.</summary>
    internal byte[] Hash(ReadOnlySpan<byte> data)
    {
        return Signatures.Cms.CmsAlgorithms.Hash(DigestAlgorithm, data);
    }
}
