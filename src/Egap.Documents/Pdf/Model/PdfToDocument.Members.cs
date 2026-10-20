using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>PDF to document as members of <see cref="Document"/>, forwarding to <see cref="Convert"/>.</summary>
public static partial class PdfToDocument
{
    extension(Document)
    {
        /// <summary>Reads a PDF file into a document, as <see cref="Convert"/> converts it.</summary>
        /// <param name="path">The PDF file.</param>
        /// <param name="options">How to analyse and build; <see cref="PdfToDocumentOptions.Default"/> when null.</param>
        public static Document LoadPdf(string path, PdfToDocumentOptions? options = null)
        {
            using PdfDocument pdf = PdfDocument.Open(path);
            Document document = Convert(pdf, options);
            document.FileName = path;
            return document;
        }

        /// <summary>Reads a PDF from a stream, which is left open, into a document, as <see cref="Convert"/> converts it.</summary>
        /// <param name="stream">The PDF.</param>
        /// <param name="options">How to analyse and build; <see cref="PdfToDocumentOptions.Default"/> when null.</param>
        public static Document LoadPdf(Stream stream, PdfToDocumentOptions? options = null)
        {
            using PdfDocument pdf = PdfDocument.Open(stream, leaveOpen: true);
            return Convert(pdf, options);
        }
    }
}
