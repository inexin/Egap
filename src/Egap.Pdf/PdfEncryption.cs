namespace Egap.Pdf;

/// <summary>
/// How a save encrypts the document: keep the encryption it was opened with, drop it, or apply new AES-256 encryption.
/// </summary>
/// <example>
/// <code>
/// pdf.Save("locked.pdf", new PdfSaveOptions
/// {
///     Mode = PdfSaveMode.Rewrite,
///     Encryption = PdfEncryption.Aes256("user", "owner", PdfPermissions.Print | PdfPermissions.ExtractForAccessibility),
/// });
/// </code>
/// </example>
public sealed class PdfEncryption
{
    private PdfEncryption(PdfEncryptionKind kind, string userPassword = "", string ownerPassword = "", PdfPermissions permissions = PdfPermissions.All)
    {
        Kind = kind;
        UserPassword = userPassword;
        OwnerPassword = ownerPassword;
        Permissions = permissions;
    }

    /// <summary>Keep the document's encryption as it was opened, or no encryption for a document that had none.</summary>
    public static PdfEncryption Keep { get; } = new(PdfEncryptionKind.Keep);

    /// <summary>Save without encryption. For an encrypted document this writes the whole document decrypted.</summary>
    public static PdfEncryption None { get; } = new(PdfEncryptionKind.None);

    /// <summary>What the save does.</summary>
    public PdfEncryptionKind Kind { get; }

    /// <summary>For new encryption, the password that opens the file with the permissions given.</summary>
    public string UserPassword { get; }

    /// <summary>For new encryption, the password that opens the file with every permission.</summary>
    public string OwnerPassword { get; }

    /// <summary>For new encryption, what a reader opening with the user password is allowed (advisory, design Q5).</summary>
    public PdfPermissions Permissions { get; }

    /// <summary>
    /// New AES-256 encryption (ISO 32000-2, revision 6), the default for new encryption. The whole document is written again.
    /// </summary>
    /// <param name="userPassword">The password that opens the file; empty to let anyone open it.</param>
    /// <param name="ownerPassword">The password that allows everything; must not be empty.</param>
    /// <param name="permissions">What a reader opening with the user password is allowed.</param>
    public static PdfEncryption Aes256(string userPassword, string ownerPassword, PdfPermissions permissions = PdfPermissions.All)
    {
        ArgumentNullException.ThrowIfNull(userPassword);
        ArgumentException.ThrowIfNullOrEmpty(ownerPassword);
        return new PdfEncryption(PdfEncryptionKind.Aes256, userPassword, ownerPassword, permissions & PdfPermissions.All);
    }
}

/// <summary>The kinds of <see cref="PdfEncryption"/>.</summary>
public enum PdfEncryptionKind
{
    /// <summary>Keep the document's encryption, if any.</summary>
    Keep,

    /// <summary>No encryption.</summary>
    None,

    /// <summary>New AES-256 encryption.</summary>
    Aes256,
}
