namespace Egap.Documents;

/// <summary>DOCX as members of <see cref="Document"/>, forwarding to <see cref="DocxFormat"/>'s methods.</summary>
public static partial class DocxFormat
{
    extension(Document document)
    {
        /// <summary>Reads a DOCX, DOTX, DOCM or DOTM file, as <see cref="Load(string, DocxLoadOptions?)"/> does.</summary>
        public static Document LoadDocx(string path, DocxLoadOptions? options = null) => Load(path, options);

        /// <summary>
        /// Reads a DOCX, DOTX, DOCM or DOTM file, reporting what could not be read as it was, as
        /// <see cref="Load(string, out IReadOnlyList{ConversionWarning}, DocxLoadOptions?)"/> does.
        /// </summary>
        public static Document LoadDocx(string path, out IReadOnlyList<ConversionWarning> warnings, DocxLoadOptions? options = null) =>
            Load(path, out warnings, options);

        /// <summary>Reads a DOCX, DOTX, DOCM or DOTM file from a stream, as <see cref="Load(Stream, DocxLoadOptions?)"/> does.</summary>
        public static Document LoadDocx(Stream stream, DocxLoadOptions? options = null) => Load(stream, options);

        /// <summary>
        /// Reads a DOCX, DOTX, DOCM or DOTM file from a stream, reporting what could not be read as it was, as
        /// <see cref="Load(Stream, out IReadOnlyList{ConversionWarning}, DocxLoadOptions?)"/> does.
        /// </summary>
        public static Document LoadDocx(Stream stream, out IReadOnlyList<ConversionWarning> warnings, DocxLoadOptions? options = null) =>
            Load(stream, out warnings, options);

        /// <summary>Writes the document as a Word file, as <see cref="Save(Document, string, DocxSaveOptions?)"/> does.</summary>
        /// <returns>What Word's format could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsDocx(string path, DocxSaveOptions? options = null) => Save(document, path, options);

        /// <summary>Writes the document as a Word file to a stream, as <see cref="Save(Document, Stream, DocxSaveOptions?)"/> does.</summary>
        /// <returns>What Word's format could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsDocx(Stream stream, DocxSaveOptions? options = null) => Save(document, stream, options);
    }
}
