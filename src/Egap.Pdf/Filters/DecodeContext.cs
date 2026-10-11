using Egap.Pdf.IO;

namespace Egap.Pdf.Filters;

/// <summary>What a decoder needs besides its input: the limits, where warnings go, and the stream it is decoding.</summary>
/// <param name="Options">The read options with the limits.</param>
/// <param name="Log">Where warnings go.</param>
/// <param name="EncodedLength">The stream's encoded length, for the decompression ratio limit.</param>
/// <param name="ObjectNumber">The stream's object number, recorded with warnings.</param>
internal sealed record DecodeContext(PdfReadOptions Options, WarningLog Log, long EncodedLength, int? ObjectNumber)
{
    public void Warn(PdfWarningCode code, string message)
    {
        Log.Add(code, message, null, ObjectNumber);
    }
}
