using Egap.Pdf.IO;

namespace Egap.Pdf.Parsing;

/// <summary>An indirect object as parsed: its number, generation and value.</summary>
internal readonly record struct IndirectObject(int ObjectNumber, int Generation, PdfObject Value);

/// <summary>
/// Builds objects from the lexer's tokens (ISO 32000-2, 7.3): direct objects, references, and indirect objects with their
/// <c>obj</c> header and stream data. Damage is repaired with warnings, as other readers do; nesting deeper than
/// <see cref="PdfReadOptions.MaxNestingDepth"/> throws <see cref="PdfLimitException"/>. Containers are parsed with an explicit
/// stack, so no input can overflow the call stack.
/// </summary>
internal sealed class ObjectParser
{
    private readonly Lexer lexer;
    private readonly PdfReadOptions options;
    private readonly IObjectResolver? resolver;
    private readonly Stack<Frame> frames = new();

    public ObjectParser(Lexer lexer, PdfReadOptions options, IObjectResolver? resolver)
    {
        this.lexer = lexer;
        this.options = options;
        this.resolver = resolver;
    }

    public Lexer Lexer => lexer;

    private WarningLog Log => lexer.Log;

    /// <summary>
    /// Parses one direct object at the current position. Where no object can start (at the end of the data, or at a
    /// keyword such as <c>endobj</c>) it warns and returns <see cref="PdfNull"/>, leaving the position at that token.
    /// </summary>
    public PdfObject ParseObject()
    {
        frames.Clear();
        while (true)
        {
            Token token = lexer.Next();
            PdfObject? value;
            switch (token.Kind)
            {
                case TokenKind.ArrayStart:
                    Push(new Frame(new PdfArray(), token.Start));
                    continue;
                case TokenKind.DictionaryStart:
                    Push(new Frame(new PdfDictionary(), token.Start));
                    continue;
                case TokenKind.ArrayEnd:
                    if (frames.TryPeek(out Frame? top) && top.Container is PdfArray)
                    {
                        value = frames.Pop().Container;
                        break;
                    }

                    if (top is not null)
                    {
                        Warn(PdfWarningCode.UnexpectedToken, "A ']' inside a dictionary was skipped.", token.Start);
                        continue;
                    }

                    Warn(PdfWarningCode.UnexpectedToken, "A ']' where an object should start was read as null.", token.Start);
                    return PdfNull.Instance;
                case TokenKind.DictionaryEnd:
                    if (frames.TryPeek(out top) && top.Container is PdfDictionary)
                    {
                        if (top.Key is not null)
                        {
                            Warn(PdfWarningCode.MalformedDictionary, $"The key /{top.Key.Value} has no value; it was dropped.", token.Start);
                        }

                        value = frames.Pop().Container;
                        break;
                    }

                    if (top is not null)
                    {
                        // An array left open inside a dictionary: close the array here and let the '>>' close the dictionary.
                        Warn(PdfWarningCode.UnterminatedObject, "An array was not closed before '>>'.", top.Start);
                        lexer.Position = token.Start;
                        value = frames.Pop().Container;
                        break;
                    }

                    Warn(PdfWarningCode.UnexpectedToken, "A '>>' where an object should start was read as null.", token.Start);
                    return PdfNull.Instance;
                case TokenKind.Integer:
                    value = IntegerOrReference(token);
                    break;
                case TokenKind.Real:
                    value = new PdfReal(token.Real, lexer.CleanNumber ? lexer.Bytes.ToArray() : null);
                    break;
                case TokenKind.LiteralString:
                case TokenKind.HexString:
                    value = PdfString.FromParsed(lexer.Bytes.ToArray(), token.Kind == TokenKind.HexString);
                    break;
                case TokenKind.Name:
                    value = PdfName.Get(lexer.Bytes);
                    break;
                case TokenKind.Keyword when lexer.Bytes.SequenceEqual("true"u8):
                    value = PdfBoolean.True;
                    break;
                case TokenKind.Keyword when lexer.Bytes.SequenceEqual("false"u8):
                    value = PdfBoolean.False;
                    break;
                case TokenKind.Keyword when lexer.Bytes.SequenceEqual("null"u8):
                    value = PdfNull.Instance;
                    break;
                case TokenKind.Keyword when !IsStructureKeyword(lexer.Bytes):
                    Warn(PdfWarningCode.UnexpectedToken, $"The keyword '{System.Text.Encoding.Latin1.GetString(lexer.Bytes)}' was skipped.", token.Start);
                    if (frames.TryPeek(out top) && top.Key is not null)
                    {
                        // An unknown keyword as a value: drop the key with it.
                        top.Key = null;
                    }

                    continue;
                case TokenKind.BraceOpen:
                case TokenKind.BraceClose:
                    Warn(PdfWarningCode.UnexpectedToken, "A brace outside a content stream was skipped.", token.Start);
                    continue;
                default:
                    // The end of the data, or a structure keyword such as endobj or stream: the object ends here.
                    lexer.Position = token.Start;
                    if (frames.Count == 0)
                    {
                        Warn(PdfWarningCode.UnexpectedToken, token.Kind == TokenKind.EndOfData ? "An object was expected before the end of the data; read as null." : "An object was expected; read as null.", token.Start);
                        return PdfNull.Instance;
                    }

                    Warn(PdfWarningCode.UnterminatedObject, "An array or dictionary was not closed.", frames.Peek().Start);
                    value = frames.Pop().Container;
                    while (frames.Count > 0)
                    {
                        Deliver(value, token.Start);
                        value = frames.Pop().Container;
                    }

                    return value;
            }

            if (frames.Count == 0)
            {
                return value;
            }

            Deliver(value, token.Start);
        }
    }

    /// <summary>
    /// Parses an indirect object at <paramref name="offset"/>: <c>n g obj</c>, the object, stream data if any, and
    /// <c>endobj</c>. Throws <see cref="PdfException"/> when there is no object header there.
    /// </summary>
    public IndirectObject ParseIndirectObject(long offset)
    {
        lexer.Position = offset;
        lexer.ObjectNumber = null;
        Token number = lexer.Next();
        Token generation = lexer.Next();
        Token keyword = lexer.Next();
        if (number.Kind != TokenKind.Integer || generation.Kind != TokenKind.Integer || !lexer.IsKeyword(keyword, "obj"u8)
            || number.Integer is < 1 or > int.MaxValue || generation.Integer is < 0 or > 65535)
        {
            throw new PdfException("There is no object header ('n g obj') here.", offset, null);
        }

        int objectNumber = (int)number.Integer;
        lexer.ObjectNumber = objectNumber;
        try
        {
            PdfObject value = ParseObject();
            Token next = lexer.Next();
            if (value is PdfDictionary dictionary && next.Kind == TokenKind.Keyword && lexer.Bytes.StartsWith("stream"u8))
            {
                // Data that starts with a regular character runs into the keyword: the data starts after "stream".
                lexer.Position = next.Start + "stream"u8.Length;
                value = ReadStream(dictionary, offset);
                next = lexer.Next();
            }

            if (!lexer.IsKeyword(next, "endobj"u8))
            {
                Warn(PdfWarningCode.MissingEndobj, "The object has no 'endobj'.", next.Start);
                lexer.Position = next.Start;
            }

            return new IndirectObject(objectNumber, (int)generation.Integer, value);
        }
        finally
        {
            lexer.ObjectNumber = null;
        }
    }

    /// <summary>Reads stream data after the <c>stream</c> keyword, leaving the position after <c>endstream</c>.</summary>
    private PdfStream ReadStream(PdfDictionary dictionary, long objectStart)
    {
        SourceReader reader = lexer.Reader;

        // The keyword is followed by CRLF or LF (7.3.8.1). Spaces before it, or a lone CR, are repaired.
        long keywordEnd = reader.Position;
        if (reader.Peek() == ' ')
        {
            Warn(PdfWarningCode.MalformedStreamKeyword, "'stream' is followed by spaces before its end-of-line marker.", keywordEnd);
            while (reader.Peek() == ' ')
            {
                reader.Skip();
            }
        }

        if (reader.Peek() == '\r')
        {
            reader.Skip();
            if (reader.Peek() == '\n')
            {
                reader.Skip();
            }
            else
            {
                Warn(PdfWarningCode.MalformedStreamKeyword, "'stream' is followed by a lone carriage return.", keywordEnd);
            }
        }
        else if (reader.Peek() == '\n')
        {
            reader.Skip();
        }
        else
        {
            Warn(PdfWarningCode.MalformedStreamKeyword, "'stream' is not followed by an end-of-line marker.", keywordEnd);
        }

        long start = reader.Position;
        PdfSource source = reader.Source;
        long? length = StreamLength(dictionary);
        if (length is long declared && declared >= 0 && start + declared <= source.Length && EndstreamFollows(start + declared))
        {
            return new PdfStream(dictionary, new SourceData(source, start, declared)) { Origin = (options, Log, lexer.ObjectNumber) };
        }

        // The length is missing, wrong or unreadable: the data runs to the next endstream (or endobj, or the end of the file).
        long end = SourceSearch.IndexOf(source, "endstream"u8, start);
        if (end >= 0)
        {
            Warn(PdfWarningCode.WrongStreamLength, length is null ? "The stream has no usable /Length; the data was found by searching for 'endstream'." : $"The stream's /Length {length} is wrong; the data was found by searching for 'endstream'.", objectStart);
            reader.Position = end + "endstream"u8.Length;
        }
        else
        {
            end = SourceSearch.IndexOf(source, "endobj"u8, start);
            Warn(PdfWarningCode.MissingEndstream, "The stream has no 'endstream'.", objectStart);
            if (end < 0)
            {
                end = source.Length;
            }

            reader.Position = end;
        }

        long dataEnd = TrimEndOfLine(source, start, end);
        return new PdfStream(dictionary, new SourceData(source, start, dataEnd - start)) { Origin = (options, Log, lexer.ObjectNumber) };
    }

    /// <summary>The stream's declared length, following a reference; null when there is none or it is not an integer.</summary>
    private long? StreamLength(PdfDictionary dictionary)
    {
        PdfObject? length = dictionary["Length"];
        if (length is PdfReference reference && resolver is not null)
        {
            try
            {
                length = resolver.Resolve(reference);
            }
            catch (PdfException) when (!Log.Strict)
            {
                // The length object itself is damaged; searching for endstream still finds the data.
                return null;
            }
        }

        return length is PdfInteger integer ? integer.Value : null;
    }

    /// <summary>Whether <c>endstream</c> follows <paramref name="position"/>, after an optional end-of-line marker. Leaves the
    /// position after it when it does.</summary>
    private bool EndstreamFollows(long position)
    {
        SourceReader reader = lexer.Reader;
        reader.Position = position;
        while (CharClass.IsWhiteSpace(reader.Peek()))
        {
            reader.Skip();
        }

        if (!reader.StartsWith("endstream"u8))
        {
            return false;
        }

        reader.Position += "endstream"u8.Length;
        return true;
    }

    /// <summary>The end of stream data before <c>endstream</c> at <paramref name="end"/>: one end-of-line marker is not data.</summary>
    private static long TrimEndOfLine(PdfSource source, long start, long end)
    {
        Span<byte> last = stackalloc byte[2];
        if (end - start >= 2 && source.Read(end - 2, last) == 2 && last[0] == '\r' && last[1] == '\n')
        {
            return end - 2;
        }

        if (end - start >= 1 && source.Read(end - 1, last[..1]) == 1 && last[0] is (byte)'\n' or (byte)'\r')
        {
            return end - 1;
        }

        return end;
    }

    private PdfObject IntegerOrReference(Token token)
    {
        // n g R: look two tokens ahead, and go back if they are not a generation and R.
        if (token.Integer < 0 || token.Integer > int.MaxValue)
        {
            return new PdfInteger(token.Integer);
        }

        long after = lexer.Position;
        Token generation = lexer.Next();
        if (generation.Kind == TokenKind.Integer && generation.Integer >= 0)
        {
            Token r = lexer.Next();
            if (lexer.IsKeyword(r, "R"u8))
            {
                if (token.Integer == 0 || generation.Integer > 65535)
                {
                    Warn(PdfWarningCode.InvalidReference, $"The reference {token.Integer} {generation.Integer} R is not valid; read as null.", token.Start);
                    return PdfNull.Instance;
                }

                resolver?.Saw((int)token.Integer);
                return new PdfReference((int)token.Integer, (int)generation.Integer, resolver);
            }
        }

        lexer.Position = after;
        return new PdfInteger(token.Integer);
    }

    private void Push(Frame frame)
    {
        if (frames.Count >= options.MaxNestingDepth)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxNestingDepth), $"Arrays and dictionaries nest more than {options.MaxNestingDepth} deep.")
            {
                Offset = frame.Start,
                ObjectNumber = lexer.ObjectNumber,
            };
        }

        frames.Push(frame);
    }

    /// <summary>Puts a finished value into the innermost open container.</summary>
    private void Deliver(PdfObject value, long offset)
    {
        Frame top = frames.Peek();
        if (top.Container is PdfArray array)
        {
            array.AddParsed(value);
            return;
        }

        PdfDictionary dictionary = (PdfDictionary)top.Container;
        if (top.Key is null)
        {
            if (value is PdfName name)
            {
                top.Key = name;
            }
            else
            {
                Warn(PdfWarningCode.MalformedDictionary, "A dictionary key is not a name; it was dropped.", offset);
            }

            return;
        }

        if (dictionary.ContainsKey(top.Key))
        {
            Warn(PdfWarningCode.DuplicateKey, $"The key /{top.Key.Value} appears twice; the last value was kept.", offset);
        }

        dictionary.SetParsed(top.Key, value);
        top.Key = null;
    }

    private static bool IsStructureKeyword(ReadOnlySpan<byte> keyword)
    {
        return keyword.SequenceEqual("endobj"u8) || keyword.SequenceEqual("stream"u8) || keyword.SequenceEqual("endstream"u8)
            || keyword.SequenceEqual("obj"u8) || keyword.SequenceEqual("xref"u8) || keyword.SequenceEqual("trailer"u8)
            || keyword.SequenceEqual("startxref"u8);
    }

    private void Warn(PdfWarningCode code, string message, long offset)
    {
        Log.Add(code, message, offset, lexer.ObjectNumber);
    }

    /// <summary>An open array or dictionary, with the dictionary key waiting for its value.</summary>
    private sealed class Frame(PdfObject container, long start)
    {
        public PdfObject Container { get; } = container;

        public long Start { get; } = start;

        public PdfName? Key { get; set; }
    }
}

/// <summary>Stream data that stays in the source file until read.</summary>
internal sealed class SourceData(PdfSource source, long offset, long length) : StreamData
{
    public PdfSource Source { get; } = source;

    public long Offset { get; } = offset;

    public override long Length { get; } = length;

    public override byte[] Read()
    {
        if (Length > Array.MaxLength)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxDecodedStreamBytes), $"A stream of {Length} bytes is too large to read into memory.");
        }

        return Source.ReadBytes(Offset, (int)Length);
    }
}
