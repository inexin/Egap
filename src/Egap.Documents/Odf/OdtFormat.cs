using Egap.Documents.Odf;

namespace Egap.Documents;

/// <summary>Options for reading OpenDocument Text.</summary>
public sealed class OdtLoadOptions
{
    /// <summary>Limits that stop a hostile file from using too much memory or time.</summary>
    public DocumentLimits Limits { get; init; } = new();

    /// <summary>The password of an encrypted file; null when the file is not encrypted, or to ask <see cref="PasswordCallback"/>.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Asked for a password when the file is encrypted and <see cref="Password"/> is missing or wrong. It gets the attempt's
    /// number, from 1, and gives a password to try, or null to give up, which throws <see cref="DocumentPasswordException"/>.
    /// </summary>
    public Func<int, string?>? PasswordCallback { get; init; }
}

/// <summary>Which kind of OpenDocument Text file a package is.</summary>
public enum OdtKind
{
    /// <summary>A document (.odt).</summary>
    Document,

    /// <summary>A template (.ott).</summary>
    Template,

    /// <summary>A flat XML document (.fodt): one XML file, no package.</summary>
    FlatDocument,

    /// <summary>A flat XML template (.fott).</summary>
    FlatTemplate,
}

/// <summary>The version of OpenDocument a file is written in.</summary>
public enum OdfVersion
{
    /// <summary>ODF 1.2, for older readers: first-page headers are a master page of their own.</summary>
    Odf12,

    /// <summary>ODF 1.3, as LibreOffice writes by default.</summary>
    Odf13,

    /// <summary>ODF 1.4.</summary>
    Odf14,
}

/// <summary>How a password-protected OpenDocument file is encrypted.</summary>
public enum OdtEncryption
{
    /// <summary>
    /// Each file in AES-256 in CBC mode, its key from the password by PBKDF2 (100,000 rounds), as ODF 1.2 to 1.4 describe:
    /// LibreOffice from 3.x on and other ODF readers open it.
    /// </summary>
    AesCbc,

    /// <summary>
    /// The whole package in AES-256 in GCM mode, its key from the password by Argon2id, as LibreOffice 24.2 and later write by
    /// default: it hides the file names too, but older readers cannot open it.
    /// </summary>
    AesGcm,
}

/// <summary>How <see cref="OdtFormat"/> writes a file.</summary>
public sealed class OdtSaveOptions
{
    /// <summary>
    /// The kind to write; null takes it from the file's extension (.ott, .fodt and .fott, else a document), or writes a
    /// document to a stream.
    /// </summary>
    public OdtKind? Kind { get; init; }

    /// <summary>The ODF version to write; ODF 1.3 by default.</summary>
    public OdfVersion Version { get; init; } = OdfVersion.Odf13;

    /// <summary>
    /// The password to encrypt the file with; null or empty writes it unencrypted. A flat file cannot be encrypted.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>How the file is encrypted when <see cref="Password"/> is given.</summary>
    public OdtEncryption Encryption { get; init; } = OdtEncryption.AesCbc;

    /// <summary>
    /// Whether markup and files the ODT reader kept (forms, variables, charts and other embedded objects, LibreOffice's
    /// settings) are written back; true by default. Turn it off for a file that only says what the model holds.
    /// </summary>
    public bool KeepUnknownMarkup { get; init; } = true;
}

/// <summary>
/// OpenDocument Text (ODF 1.2 to 1.4) read into the model: ODT documents, OTT templates and flat FODT files, with their
/// styles, lists, tables, frames, sections, page styles, headers and footers, notes, comments, fields, bookmarks, links and
/// tracked changes. Charts, formulas and other embedded objects are kept as islands drawn as their stored picture. The model
/// is written back as ODF 1.3 (1.2 and 1.4 as options), and ODT to ODT keeps what the model does not name.
/// </summary>
public static class OdtFormat
{
    /// <summary>Reads an ODT, OTT or FODT file.</summary>
    /// <exception cref="DocumentException">The file is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(string path, OdtLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads an ODT, OTT or FODT file, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The file is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, OdtLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Document document = Read(File.ReadAllBytes(path), out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads ODT, OTT or FODT from a stream, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(Stream stream, OdtLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads ODT, OTT or FODT from a stream, which is left open, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The stream is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, OdtLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Read(copy.ToArray(), out warnings, options);
    }

    /// <summary>
    /// Writes <paramref name="document"/> to <paramref name="path"/> as OpenDocument Text: a document, or a template or flat
    /// file as <see cref="OdtSaveOptions.Kind"/> or the extension (.ott, .fodt, .fott) says.
    /// </summary>
    /// <returns>What OpenDocument could not hold, such as margin boxes and content controls.</returns>
    /// <exception cref="ArgumentException">A password was given for a flat file.</exception>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, OdtSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new OdtSaveOptions();
        OdtKind kind = options.Kind ?? KindOf(path);
        Check(kind, options);
        using MemoryStream buffer = new();
        IReadOnlyList<ConversionWarning> warnings = OdtWriter.Write(document, buffer, options, kind);
        using FileStream stream = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(stream);
        return warnings;
    }

    /// <summary>Writes <paramref name="document"/> as OpenDocument Text to <paramref name="stream"/>, which is left open.</summary>
    /// <returns>What OpenDocument could not hold, such as margin boxes and content controls.</returns>
    /// <exception cref="ArgumentException">A password was given for a flat file.</exception>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, OdtSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new OdtSaveOptions();
        OdtKind kind = options.Kind ?? OdtKind.Document;
        Check(kind, options);
        return OdtWriter.Write(document, stream, options, kind);
    }

    /// <summary>The kind a file's extension names: .ott a template, .fodt and .fott flat files, anything else a document.</summary>
    internal static OdtKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ott" => OdtKind.Template,
        ".fodt" => OdtKind.FlatDocument,
        ".fott" => OdtKind.FlatTemplate,
        _ => OdtKind.Document,
    };

    private static void Check(OdtKind kind, OdtSaveOptions options)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(options.Version) || !Enum.IsDefined(options.Encryption))
        {
            throw new ArgumentException("The save options name a kind, version or encryption that does not exist.", nameof(options));
        }

        if (kind is OdtKind.FlatDocument or OdtKind.FlatTemplate && !string.IsNullOrEmpty(options.Password))
        {
            throw new ArgumentException("A flat OpenDocument file cannot be encrypted; save it as .odt to give it a password.", nameof(options));
        }
    }

    internal static Document Read(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings, OdtLoadOptions? options = null)
    {
        return OdtReader.Read(bytes, options ?? new OdtLoadOptions(), out warnings);
    }
}
