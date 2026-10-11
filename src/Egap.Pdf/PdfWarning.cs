using System.Text;

namespace Egap.Pdf;

/// <summary>One repair made while reading a damaged file. In strict mode the same problem throws a <see cref="PdfException"/>.</summary>
/// <param name="Code">What kind of repair it was.</param>
/// <param name="Message">What was wrong and what was done about it.</param>
/// <param name="Offset">The byte offset in the file where the problem was found, when known.</param>
/// <param name="ObjectNumber">The number of the object being read, when known.</param>
public sealed record PdfWarning(PdfWarningCode Code, string Message, long? Offset = null, int? ObjectNumber = null)
{
    /// <summary>The warning as one line: the code, where it was found and the message.</summary>
    public override string ToString()
    {
        StringBuilder text = new();
        text.Append(Code);
        if (ObjectNumber is int number)
        {
            text.Append(" in object ").Append(number);
        }

        if (Offset is long offset)
        {
            text.Append(" at byte ").Append(offset);
        }

        return text.Append(": ").Append(Message).ToString();
    }
}
