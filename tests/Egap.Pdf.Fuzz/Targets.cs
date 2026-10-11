using Egap.Pdf.IO;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Fuzz;

/// <summary>The fuzz targets. Each reads one input; see Program.cs for what counts as a crash.</summary>
internal static class Targets
{
    public static IEnumerable<string> Names => ["lexer"];

    public static Action<Stream> Get(string name)
    {
        return name switch
        {
            "lexer" => Lexer,
            _ => throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name)),
        };
    }

    private static byte[] ReadAll(Stream input)
    {
        using MemoryStream copy = new();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Tokenizes the input leniently (must not throw) and strictly (may throw only <see cref="PdfException"/>).</summary>
    private static void Lexer(Stream input)
    {
        byte[] bytes = ReadAll(input);
        using PdfSource source = PdfSource.FromMemory(bytes);
        Lex(new Lexer(new SourceReader(source), new WarningLog(strict: false)));
        try
        {
            Lex(new Lexer(new SourceReader(source), new WarningLog(strict: true)));
        }
        catch (PdfException)
        {
        }
    }

    private static void Lex(Lexer lexer)
    {
        long last = -1;
        while (lexer.Next() is { Kind: not TokenKind.EndOfData } token)
        {
            if (token.Start <= last)
            {
                throw new InvalidOperationException($"The lexer went backwards at {token.Start}.");
            }

            last = token.Start;
        }
    }
}
