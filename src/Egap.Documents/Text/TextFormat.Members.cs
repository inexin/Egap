namespace Egap.Documents;

/// <summary>Plain text as members of <see cref="Document"/>, forwarding to <see cref="TextFormat"/>'s methods.</summary>
public static partial class TextFormat
{
    extension(Document document)
    {
        /// <summary>Reads plain text from a string, as <see cref="Parse(string)"/> does.</summary>
        public static Document ParseText(string text) => Parse(text);

        /// <summary>Reads a plain-text file, as <see cref="Load(string)"/> does.</summary>
        public static Document LoadText(string path) => Load(path);

        /// <summary>Reads plain text from a stream, as <see cref="Load(Stream)"/> does.</summary>
        public static Document LoadText(Stream stream) => Load(stream);

        /// <summary>
        /// The document as plain text. Also callable as <c>TextFormat.ToText(document, options)</c>, as before extension
        /// members.
        /// </summary>
        public string ToText(TextSaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            return new PlainTextWriter(document, options ?? new TextSaveOptions()).Write();
        }

        /// <summary>Writes the document as a plain-text file, as <see cref="Save(Document, string, TextSaveOptions?)"/> does.</summary>
        /// <returns>What plain text could not hold: nothing, since everything it leaves out is left out by design.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsText(string path, TextSaveOptions? options = null) => Save(document, path, options);

        /// <summary>Writes the document as plain text to a stream, as <see cref="Save(Document, Stream, TextSaveOptions?)"/> does.</summary>
        /// <returns>What plain text could not hold: nothing, since everything it leaves out is left out by design.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsText(Stream stream, TextSaveOptions? options = null) => Save(document, stream, options);
    }
}
