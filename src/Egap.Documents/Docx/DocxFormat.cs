using Egap.Documents.Docx;

namespace Egap.Documents;

/// <summary>How <see cref="DocxFormat"/> reads a file.</summary>
public sealed class DocxLoadOptions
{
    /// <summary>The limits on the package: entries, part sizes, compression ratio and nesting depth.</summary>
    public DocumentLimits Limits { get; init; } = new();

    /// <summary>The password of an encrypted file; null when the file is not encrypted, or to ask <see cref="PasswordCallback"/>.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Asked for a password when the file is encrypted and <see cref="Password"/> is missing or wrong. It gets the attempt's
    /// number, from 1, and gives a password to try, or null to give up, which throws <see cref="DocumentPasswordException"/>.
    /// </summary>
    public Func<int, string?>? PasswordCallback { get; init; }
}

/// <summary>Which of Word's file kinds a DOCX package is (Phase 6 design Q10).</summary>
public enum DocxKind
{
    /// <summary>A document (.docx).</summary>
    Document,

    /// <summary>A template (.dotx).</summary>
    Template,

    /// <summary>A document that may hold macros (.docm); Egap keeps them and never runs them.</summary>
    MacroEnabledDocument,

    /// <summary>A template that may hold macros (.dotm).</summary>
    MacroEnabledTemplate,
}

/// <summary>How <see cref="DocxFormat"/> writes a file.</summary>
public sealed class DocxSaveOptions
{
    /// <summary>
    /// The kind to write; null keeps the kind the document was read as (a new document is a .docx), or, saving to a path,
    /// takes it from the file's extension. A kind that holds no macros drops the macros a .docm or .dotm had.
    /// </summary>
    public DocxKind? Kind { get; init; }

    /// <summary>
    /// The password to encrypt the file with, as Word does when a document is saved with a password to open it (agile
    /// encryption: AES-256 with SHA-512 and 100,000 key spins); null or empty writes the file unencrypted. At most 255
    /// characters, as in Word.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// Word's DOCX format, with its DOTX and DOCM variants (Phase 6 design Q9 and Q10): strict and transitional files read
/// into the model, with what the model does not name kept as extension data so a DOCX round trip keeps it. Files encrypted
/// with a password to open them are read with <see cref="DocxLoadOptions.Password"/> and written with
/// <see cref="DocxSaveOptions.Password"/>.
/// </summary>
public static partial class DocxFormat
{
    /// <summary>Reads the DOCX, DOTX or DOCM file at <paramref name="path"/>.</summary>
    /// <exception cref="DocumentException">The file is not a Word document Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="DocxLoadOptions.Limits"/>.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    public static Document Load(string path, DocxLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads the DOCX, DOTX or DOCM file at <paramref name="path"/>, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The file is not a Word document Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="DocxLoadOptions.Limits"/>.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, DocxLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        Document document = Load(stream, out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads a DOCX, DOTX or DOCM file from <paramref name="stream"/>, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not a Word document Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="DocxLoadOptions.Limits"/>.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    public static Document Load(Stream stream, DocxLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads a DOCX, DOTX or DOCM file from <paramref name="stream"/>, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The stream is not a Word document Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="DocxLoadOptions.Limits"/>.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, DocxLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new DocxLoadOptions();
        Stream source = stream;
        if (!stream.CanSeek)
        {
            MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.Position = 0;
            source = copy;
        }

        // A compound file is an encrypted package (or a binary .doc, which the package reader sends to Document.Load).
        if (source.CanSeek && Office.OfficeEncryption.IsEncryptedPackage(source))
        {
            Office.CompoundFile file = Office.CompoundFile.Read(source, options.Limits);
            byte[] decrypted = Office.OfficeEncryption.Decrypt(file, options.Password, options.PasswordCallback, options.Limits);
            source = new MemoryStream(decrypted, writable: false);
        }

        OpcPackage package = OpcPackage.Read(source, options.Limits);

        // What the model does not name is kept as extension data, not lost; what is reported is an embedded part (an RTF
        // altChunk) that could not be read in.
        return DocxReader.Read(package, options.Limits, out warnings);
    }

    /// <summary>
    /// Writes <paramref name="document"/> as DOCX to <paramref name="path"/>. A document read from DOCX keeps what the model
    /// does not name; parts and paragraphs that were not edited are written as they were read.
    /// </summary>
    /// <returns>What DOCX could not hold: margin boxes in the corners and at the sides of the page.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, DocxSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        DocxKind? kind = options?.Kind ?? Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".DOTX" => DocxKind.Template,
            ".DOCM" => DocxKind.MacroEnabledDocument,
            ".DOTM" => DocxKind.MacroEnabledTemplate,
            ".DOCX" => DocxKind.Document,
            _ => null,
        };
        using FileStream stream = File.Create(path);
        return Save(document, stream, new DocxSaveOptions { Kind = kind, Password = options?.Password });
    }

    /// <summary>The kind <paramref name="document"/> was read as, or null for a document not read from DOCX.</summary>
    public static DocxKind? KindOf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DocxPackageData.Of(document)?.MainContentType is { } type ? DocxWriter.KindOf(type) : null;
    }

    /// <summary>Writes <paramref name="document"/> as DOCX to <paramref name="stream"/>, which is left open.</summary>
    /// <returns>What DOCX could not hold: margin boxes in the corners and at the sides of the page.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, DocxSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrEmpty(options?.Password))
        {
            return DocxWriter.Write(document, stream, kind: options?.Kind);
        }

        if (options.Password.Length > Office.OfficeEncryption.MaxPasswordLength)
        {
            throw new ArgumentException($"A password has at most {Office.OfficeEncryption.MaxPasswordLength} characters.", nameof(options));
        }

        // The package is written whole, then encrypted into a compound file.
        using MemoryStream package = new();
        IReadOnlyList<ConversionWarning> warnings = DocxWriter.Write(document, package, kind: options.Kind);
        Office.OfficeEncryption.Encrypt(package.GetBuffer().AsSpan(0, (int)package.Length), options.Password, stream);
        return warnings;
    }
}
