using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>Opens a PDF named on the command line, or standard input, with the session's read options and a password.</summary>
internal static class PdfInput
{
    /// <summary>
    /// Opens the input; when it needs a password that none of the options gave, egap asks on the terminal (CLI design Q9).
    /// <paramref name="inMemory"/> reads the file whole first, so the output may replace it (<c>--in-place</c>).
    /// </summary>
    public static PdfDocument Open(EgapSession session, InputFile file, string? password, bool inMemory = false)
    {
        PdfReadOptions options = session.ReadOptions with { Password = password };
        byte[]? bytes = file.Path is null ? Stdin(session) : inMemory ? File.ReadAllBytes(file.Path) : null;
        PdfDocument document;
        try
        {
            document = OpenWith(file, bytes, options);
        }
        catch (PdfPasswordException) when (password is null && SecretOptions.Prompt(session, $"Password for {file.Display}") is { } typed)
        {
            document = OpenWith(file, bytes, options with { Password = typed });
        }

        session.Report(file.Display, document.Warnings);
        return document;
    }

    /// <summary>Opens a PDF named by an option, such as a stamp or a background, inside the allowed folders.</summary>
    public static PdfDocument Open(EgapSession session, string path, string? password = null)
    {
        string full = session.ResolvePath(path);
        return Open(session, new InputFile(full, path, string.Empty, 0), password);
    }

    private static PdfDocument OpenWith(InputFile file, byte[]? bytes, PdfReadOptions options)
    {
        return bytes is not null ? PdfDocument.Open(bytes, options) : PdfDocument.Open(file.Path!, options);
    }

    private static byte[] Stdin(EgapSession session)
    {
        using Stream input = session.Console.In();
        using MemoryStream buffer = new();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }
}
