namespace Egap.Pdf;

/// <summary>
/// The exception thrown when an encrypted file needs a password that was not given, or the password given is neither the
/// user nor the owner password. Set <see cref="PdfReadOptions.Password"/> and open the file again.
/// </summary>
public sealed class PdfPasswordException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public PdfPasswordException(string message)
        : base(message)
    {
    }
}
