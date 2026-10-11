using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Fuzz;

/// <summary>The fuzz targets. Each reads one input; see Program.cs for what counts as a crash.</summary>
internal static class Targets
{
    public static IEnumerable<string> Names => ["lexer", "parser"];

    public static Action<Stream> Get(string name)
    {
        return name switch
        {
            "lexer" => Lexer,
            "parser" => Parser,
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

    /// <summary>
    /// Parses every direct object in turn and every indirect object whose header is found, leniently (must not throw except
    /// at a limit) and strictly (may throw only <see cref="PdfException"/>). Each indirect object is written back, and the
    /// written bytes must parse strictly, without a single warning, to an object that writes the same bytes again.
    /// </summary>
    private static void Parser(Stream input)
    {
        byte[] bytes = ReadAll(input);
        using PdfSource source = PdfSource.FromMemory(bytes);
        PdfReadOptions options = new() { MaxNestingDepth = 64 };
        try
        {
            ParseAll(source, new WarningLog(strict: false), options, roundTrip: true);
        }
        catch (PdfLimitException)
        {
        }

        try
        {
            ParseAll(source, new WarningLog(strict: true), options, roundTrip: false);
        }
        catch (PdfException)
        {
        }
    }

    private static void ParseAll(PdfSource source, WarningLog log, PdfReadOptions options, bool roundTrip)
    {
        ObjectParser parser = new(new Lexer(new SourceReader(source), log), options, null);
        while (!parser.Lexer.Reader.AtEnd)
        {
            long before = parser.Lexer.Position;
            parser.ParseObject();
            if (parser.Lexer.Position == before && parser.Lexer.Next().Kind == TokenKind.EndOfData)
            {
                break;
            }
        }

        // Indirect objects: every "n g obj" header the lexer sees.
        Lexer scan = new(new SourceReader(source), new WarningLog(strict: false));
        Token first = default, second = default;
        while (scan.Next() is { Kind: not TokenKind.EndOfData } token)
        {
            if (token.Kind == TokenKind.Keyword && scan.Bytes.SequenceEqual("obj"u8) && first.Kind == TokenKind.Integer && second.Kind == TokenKind.Integer)
            {
                IndirectObject parsed;
                try
                {
                    parsed = parser.ParseIndirectObject(first.Start);
                }
                catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
                {
                    // Not a header after all (such as "0 0 obj"): fine.
                    first = second = default;
                    continue;
                }

                if (roundTrip)
                {
                    RoundTrip(parsed);
                }
            }

            first = second;
            second = token;
        }
    }

    private static void RoundTrip(IndirectObject parsed)
    {
        ByteWriter output = new();
        output.WriteAscii(FormattableString.Invariant($"{parsed.ObjectNumber} {parsed.Generation} obj\n"));
        ObjectWriter.Write(parsed.Value, output);
        output.WriteAscii("\nendobj\n");
        byte[] written = output.ToArray();

        using PdfSource source = PdfSource.FromMemory(written);
        ObjectParser parser = new(new Lexer(new SourceReader(source), new WarningLog(strict: true)), new PdfReadOptions { MaxNestingDepth = 64 }, null);
        IndirectObject again = parser.ParseIndirectObject(0);
        if (again.ObjectNumber != parsed.ObjectNumber || again.Generation != parsed.Generation)
        {
            throw new InvalidOperationException("The object header did not survive a round trip.");
        }

        byte[] rewritten = ObjectWriter.ToBytes(again.Value);
        if (!rewritten.AsSpan().SequenceEqual(ObjectWriter.ToBytes(parsed.Value)))
        {
            throw new InvalidOperationException("The object did not survive a round trip.");
        }
    }
}
