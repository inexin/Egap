namespace Egap.Documents.Docx;

/// <summary>How <see cref="DocxFormat"/> reads a file.</summary>
public sealed class DocxLoadOptions
{
    /// <summary>The limits on the package: entries, part sizes, compression ratio and nesting depth.</summary>
    public DocumentLimits Limits { get; init; } = new();
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
}

/// <summary>
/// Word's DOCX format, with its DOTX and DOCM variants (Phase 6 design Q9 and Q10): strict and transitional files read
/// into the model, with what the model does not name kept as extension data so a DOCX round trip keeps it.
/// </summary>
public static class DocxFormat
{
    /// <summary>Reads the DOCX, DOTX or DOCM file at <paramref name="path"/>.</summary>
    /// <exception cref="DocumentException">The file is not a Word document Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="DocxLoadOptions.Limits"/>.</exception>
    public static Document Load(string path, DocxLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        Document document = Load(stream, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads a DOCX, DOTX or DOCM file from <paramref name="stream"/>, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not a Word document Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="DocxLoadOptions.Limits"/>.</exception>
    public static Document Load(Stream stream, DocxLoadOptions? options = null)
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

        OpcPackage package = OpcPackage.Read(source, options.Limits);
        return DocxReader.Read(package);
    }

    /// <summary>
    /// Writes <paramref name="document"/> as DOCX to <paramref name="path"/>. A document read from DOCX keeps what the model
    /// does not name; parts and paragraphs that were not edited are written as they were read.
    /// </summary>
    public static void Save(Document document, string path, DocxSaveOptions? options = null)
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
        Save(document, stream, new DocxSaveOptions { Kind = kind });
    }

    /// <summary>The kind <paramref name="document"/> was read as, or null for a document not read from DOCX.</summary>
    public static DocxKind? KindOf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DocxPackageData.Of(document)?.MainContentType is { } type ? DocxWriter.KindOf(type) : null;
    }

    /// <summary>Writes <paramref name="document"/> as DOCX to <paramref name="stream"/>, which is left open.</summary>
    public static void Save(Document document, Stream stream, DocxSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        DocxWriter.Write(document, stream, kind: options?.Kind);
    }
}
