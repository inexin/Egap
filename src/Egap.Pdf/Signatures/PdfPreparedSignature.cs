using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Egap.Pdf;

/// <summary>
/// A signature waiting for its CMS (Phase 5 design Q3): the file is written with the space reserved, and
/// <see cref="Digest"/> is what a signing service signs. <see cref="CompleteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
/// writes the CMS it returns into that space, in the same stream; a file prepared earlier, in another process, is completed with
/// <see cref="CompleteAsync(Stream, ReadOnlyMemory{byte}, CancellationToken)"/>.
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

    internal PdfPreparedSignature(Stream output, long outputStart, long contentsOffset, int reservedSize, byte[] digest, HashAlgorithmName digestAlgorithm)
    {
        this.output = output;
        this.outputStart = outputStart;
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

    /// <summary>Writes the CMS into the reserved space of the prepared file in the stream it was written to.</summary>
    /// <exception cref="PdfException">The CMS does not fit the reserved space.</exception>
    public async Task CompleteAsync(ReadOnlyMemory<byte> cms, CancellationToken cancellationToken = default)
    {
        await Patch(output, outputStart + ContentsOffset, ReservedSize, cms, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the CMS into a file prepared earlier: the last signature in <paramref name="file"/> whose contents are still all
    /// zeros. The stream must be readable, writable and seekable.
    /// </summary>
    /// <exception cref="PdfException">The file has no prepared signature, or the CMS does not fit.</exception>
    public static async Task CompleteAsync(Stream file, ReadOnlyMemory<byte> cms, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
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
