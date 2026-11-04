namespace Egap.Documents;

/// <summary>How <see cref="Document.Load(string, DocumentLoadOptions?)"/> reads a file whose format it finds itself.</summary>
public sealed class DocumentLoadOptions
{
    /// <summary>The password of an encrypted file; null when the file is not encrypted, or to ask <see cref="PasswordCallback"/>.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Asked for a password when the file is encrypted and <see cref="Password"/> is missing or wrong. It gets the attempt's
    /// number, from 1, and gives a password to try, or null to give up, which throws <see cref="DocumentPasswordException"/>.
    /// </summary>
    public Func<int, string?>? PasswordCallback { get; init; }

    /// <summary>The limits on the file, for the formats that take them (DOCX, RTF and encrypted files).</summary>
    public DocumentLimits Limits { get; init; } = new();
}
