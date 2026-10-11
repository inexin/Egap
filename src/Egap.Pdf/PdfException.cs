namespace Egap.Pdf;

/// <summary>The exception thrown when a PDF file cannot be read, or, in strict mode, when it needs any repair.</summary>
public class PdfException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public PdfException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the exception that caused it.</summary>
    public PdfException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception for a problem at a known place in the file.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="offset">The byte offset in the file, when known.</param>
    /// <param name="objectNumber">The number of the object being read, when known.</param>
    public PdfException(string message, long? offset, int? objectNumber)
        : base(message)
    {
        Offset = offset;
        ObjectNumber = objectNumber;
    }

    /// <summary>The byte offset in the file where the problem was found, when known.</summary>
    public long? Offset { get; init; }

    /// <summary>The number of the object being read, when known.</summary>
    public int? ObjectNumber { get; init; }

    /// <summary>
    /// In strict mode, the kind of repair that the problem would have needed; <see langword="null"/> for problems that no
    /// repair can fix.
    /// </summary>
    public PdfWarningCode? Code { get; init; }
}
