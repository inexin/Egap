using Egap.Pdf.Objects;

namespace Egap.Pdf.Security;

/// <summary>
/// Makes the encrypted form of an object for writing (7.6.2): a copy whose strings and stream data are encrypted for its
/// object number and generation. The same things decryption leaves alone are left alone here: signature contents, metadata
/// streams when metadata is not encrypted, and the encryption dictionary (which the writer never passes in).
/// </summary>
internal static class ObjectEncryption
{
    public static PdfObject EncryptedCopy(PdfObject value, int number, int generation, StandardSecurityHandler handler, bool deterministic)
    {
        switch (value)
        {
            case PdfString text:
                return Encrypted(text, number, generation, handler, deterministic);
            case PdfArray array:
                PdfArray arrayCopy = array.Clone();
                EncryptStrings(arrayCopy, number, generation, handler, deterministic);
                return arrayCopy;
            case PdfDictionary dictionary:
                PdfDictionary dictionaryCopy = dictionary.Clone();
                EncryptStrings(dictionaryCopy, number, generation, handler, deterministic);
                return dictionaryCopy;
            case PdfStream stream:
                PdfStream streamCopy = stream.Clone();
                EncryptStrings(streamCopy.Dictionary, number, generation, handler, deterministic);
                if (!(streamCopy.Dictionary.GetName("Type")?.Value == "Metadata" && !handler.EncryptMetadata))
                {
                    streamCopy.ReplaceData(new BytesData(handler.EncryptStream(stream.GetEncodedBytes(), number, generation, CryptFilter(stream), deterministic)));
                }

                return streamCopy;
            default:
                return value;
        }
    }

    private static void EncryptStrings(PdfObject root, int number, int generation, StandardSecurityHandler handler, bool deterministic)
    {
        Stack<PdfObject> pending = new([root]);
        while (pending.TryPop(out PdfObject? value))
        {
            if (value is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is PdfString text)
                    {
                        array.ReplaceParsed(i, Encrypted(text, number, generation, handler, deterministic));
                    }
                    else
                    {
                        pending.Push(array[i]);
                    }
                }
            }
            else if (value is PdfDictionary dictionary)
            {
                bool signature = dictionary.GetName("Type")?.Value is "Sig" or "DocTimeStamp" || dictionary.ContainsKey("ByteRange");
                foreach ((PdfName key, PdfObject item) in dictionary.Entries.ToArray())
                {
                    if (item is PdfString text)
                    {
                        if (!(signature && key.Value == "Contents"))
                        {
                            dictionary.ReplaceParsed(key, Encrypted(text, number, generation, handler, deterministic));
                        }
                    }
                    else
                    {
                        pending.Push(item);
                    }
                }
            }
        }
    }

    private static PdfString Encrypted(PdfString text, int number, int generation, StandardSecurityHandler handler, bool deterministic)
    {
        return PdfString.FromParsed(handler.EncryptString(text.Bytes, number, generation, deterministic), text.IsHex);
    }

    /// <summary>The crypt filter a stream names for itself with a leading <c>/Crypt</c> filter, or null for the default.</summary>
    private static string? CryptFilter(PdfStream stream)
    {
        if (stream.Filters is not [{ Value: "Crypt" }, ..])
        {
            return null;
        }

        PdfDictionary? parameters = stream.Dictionary.Get("DecodeParms") switch
        {
            PdfDictionary single => single,
            PdfArray array when array.Count > 0 => array.Get<PdfDictionary>(0),
            _ => null,
        };
        return parameters?.GetName("Name")?.Value ?? "Identity";
    }
}
