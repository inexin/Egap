using Egap.Pdf.IO;

namespace Egap.Pdf.Parsing;

/// <summary>One object found in an object stream: its number and value.</summary>
internal readonly record struct StoredObject(int ObjectNumber, PdfObject Value);

/// <summary>Reads the objects of an object stream (ISO 32000-2, 7.5.7): <c>/N</c> pairs of numbers and offsets, then the objects.</summary>
internal static class ObjectStreamReader
{
    /// <summary>The objects by index; an index whose entry is malformed holds <see langword="null"/>.</summary>
    public static StoredObject?[] Read(PdfStream stream, int streamNumber, PdfReadOptions options, WarningLog log, IObjectResolver resolver)
    {
        PdfDictionary dictionary = stream.Dictionary;
        long count = dictionary.GetInt("N") ?? -1;
        long first = dictionary.GetInt("First") ?? -1;
        if (count < 0 || first < 0)
        {
            log.Add(PdfWarningCode.MalformedObjectStream, "The object stream has no valid /N or /First.", null, streamNumber);
            return [];
        }

        if (count > options.MaxObjects)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxObjects), $"An object stream claims more than {options.MaxObjects} objects.");
        }

        byte[] data = stream.GetDecodedBytes();
        if (first > data.Length)
        {
            log.Add(PdfWarningCode.MalformedObjectStream, "The object stream's /First is past the end of its data.", null, streamNumber);
            return [];
        }

        // The header holds the pairs; a token that is not an integer ends it early.
        Lexer lexer = new(new SourceReader(PdfSource.FromMemory(data)), log) { ObjectNumber = streamNumber };
        List<(long Number, long Offset)> pairs = [];
        for (long i = 0; i < count; i++)
        {
            Token number = lexer.Next();
            Token offset = lexer.Next();
            if (number.Kind != TokenKind.Integer || offset.Kind != TokenKind.Integer || number.Start >= first)
            {
                log.Add(PdfWarningCode.MalformedObjectStream, $"The object stream's header promised {count} objects but has {i}.", null, streamNumber);
                break;
            }

            pairs.Add((number.Integer, offset.Integer));
        }

        StoredObject?[] objects = new StoredObject?[pairs.Count];
        ObjectParser parser = new(lexer, options, resolver);
        for (int i = 0; i < pairs.Count; i++)
        {
            (long number, long offset) = pairs[i];
            if (number is < 1 or > int.MaxValue || offset < 0 || first + offset >= data.Length)
            {
                log.Add(PdfWarningCode.MalformedObjectStream, $"Entry {i} of the object stream (object {number} at {offset}) is out of range.", null, streamNumber);
                continue;
            }

            lexer.Position = first + offset;
            lexer.ObjectNumber = (int)number;
            objects[i] = new StoredObject((int)number, parser.ParseObject());
        }

        return objects;
    }
}
