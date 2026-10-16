using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;

namespace Egap.Pdf;

/// <summary>
/// A signature waiting for its CMS (Phase 5 design Q3): the file is written with the space reserved, and
/// <see cref="Digest"/> is what a signing service signs. <see cref="CompleteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
/// writes the CMS it returns into that space, in the same stream; a file prepared earlier, in another process, is completed with
/// <see cref="CompleteAsync(Stream, ReadOnlyMemory{byte}, PdfTimestampClient?, CancellationToken)"/>.
/// </summary>
/// <example>
/// <code>
/// PdfPreparedSignature prepared = await pdf.PrepareSignatureAsync("Signature1", new PdfSignOptions(), output);
/// byte[] cms = await remoteService.SignAsync(prepared.Digest, prepared.DigestAlgorithm);
/// await prepared.CompleteAsync(cms);
/// </code>
/// </example>
public sealed class PdfPreparedSignature
{
    private readonly Stream output;
    private readonly long outputStart;
    private readonly PdfTimestampClient? timestamp;

    internal PdfPreparedSignature(Stream output, long outputStart, long contentsOffset, int reservedSize, byte[] digest, HashAlgorithmName digestAlgorithm, PdfTimestampClient? timestamp)
    {
        this.output = output;
        this.outputStart = outputStart;
        this.timestamp = timestamp;
        ContentsOffset = contentsOffset;
        ReservedSize = reservedSize;
        Digest = digest;
        DigestAlgorithm = digestAlgorithm;
    }

    /// <summary>The digest of the signed byte ranges: the CMS's message-digest attribute must hold it.</summary>
    public ReadOnlyMemory<byte> Digest { get; }

    /// <summary>The algorithm <see cref="Digest"/> was computed with.</summary>
    public HashAlgorithmName DigestAlgorithm { get; }

    /// <summary>The bytes reserved for the CMS.</summary>
    public int ReservedSize { get; }

    /// <summary>Where the hex string that receives the CMS starts in the file (its <c>&lt;</c>).</summary>
    public long ContentsOffset { get; }

    /// <summary>
    /// Writes the CMS into the reserved space of the prepared file in the stream it was written to. When the signature was
    /// prepared at B-T or above and the CMS has no timestamp, one is added from the options' timestamp authority.
    /// </summary>
    /// <exception cref="PdfException">The CMS does not fit the reserved space, or is not a SignedData.</exception>
    public async Task CompleteAsync(ReadOnlyMemory<byte> cms, CancellationToken cancellationToken = default)
    {
        if (timestamp is not null)
        {
            cms = await AddTimestampAsync(cms, timestamp, cancellationToken).ConfigureAwait(false);
        }

        await Patch(output, outputStart + ContentsOffset, ReservedSize, cms, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the CMS into a file prepared earlier: the last signature in <paramref name="file"/> whose contents are still all
    /// zeros. The stream must be readable, writable and seekable.
    /// </summary>
    /// <exception cref="PdfException">The file has no prepared signature, or the CMS does not fit.</exception>
    /// <param name="file">The prepared file: readable, writable and seekable, positioned at its start.</param>
    /// <param name="cms">The CMS the signing service made for the prepared digest.</param>
    /// <param name="timestamp">A timestamp authority to put a timestamp on the signature (B-T), unless the CMS has one already.</param>
    /// <param name="cancellationToken">Cancels the completion.</param>
    public static async Task CompleteAsync(Stream file, ReadOnlyMemory<byte> cms, PdfTimestampClient? timestamp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (timestamp is not null)
        {
            cms = await AddTimestampAsync(cms, timestamp, cancellationToken).ConfigureAwait(false);
        }

        long start = file.Position;
        long offset;
        int reserved;
        using (PdfDocument pdf = PdfDocument.Open(file, leaveOpen: true))
        {
            PdfSignature? waiting = pdf.Signatures.LastOrDefault(s => s.ByteRange.Count == 2 && !s.Contents.Span.ContainsAnyExcept((byte)0));
            if (waiting is null)
            {
                throw new PdfException("The file has no prepared signature waiting for its contents.");
            }

            offset = waiting.ByteRange[0].End;
            reserved = waiting.Contents.Length;
        }

        await Patch(file, start + offset, reserved, cms, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The CMS with a timestamp on its signer's signature value, unless it has one; .NET's SignedCms re-encodes it.</summary>
    private static async Task<ReadOnlyMemory<byte>> AddTimestampAsync(ReadOnlyMemory<byte> cms, PdfTimestampClient client, CancellationToken cancellationToken)
    {
        SignedCms signed = new();
        try
        {
            signed.Decode(cms.Span);
        }
        catch (CryptographicException e)
        {
            throw new PdfException($"The CMS to complete the signature with is not a SignedData: {e.Message}");
        }

        if (signed.SignerInfos.Count != 1)
        {
            throw new PdfException("The CMS to complete the signature with must have exactly one signer.");
        }

        SignerInfo signer = signed.SignerInfos[0];
        if (signer.UnsignedAttributes.Cast<CryptographicAttributeObject>().Any(a => a.Oid.Value == Signatures.Cms.CmsOids.SignatureTimestampToken))
        {
            return cms;
        }

        byte[] token = await client.RequestAsync(client.Hash(signer.GetSignature()), cancellationToken).ConfigureAwait(false);
        signer.AddUnsignedAttribute(new AsnEncodedData(Signatures.Cms.CmsOids.SignatureTimestampToken, token));
        return signed.Encode();
    }

    private static async Task Patch(Stream stream, long contentsOffset, int reservedSize, ReadOnlyMemory<byte> cms, CancellationToken cancellationToken)
    {
        if (cms.Length > reservedSize)
        {
            throw new PdfException(string.Create(CultureInfo.InvariantCulture, $"The signature needs {cms.Length} bytes, but only {reservedSize} were reserved; sign again with a larger PdfSignOptions.ReservedSize."));
        }

        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new InvalidOperationException("The prepared file's stream must be writable and seekable to complete it.");
        }

        stream.Position = contentsOffset + 1;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Convert.ToHexString(cms.Span)), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
