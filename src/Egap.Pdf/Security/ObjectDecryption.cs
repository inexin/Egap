using Egap.Pdf.Objects;

namespace Egap.Pdf.Security;

/// <summary>
/// Decrypts an object loaded from an encrypted file (7.6.2): every string in it, and its stream data when it is read. The
/// encryption dictionary, cross-reference streams, the contents of signatures and, when <c>/EncryptMetadata</c> is false,
/// metadata streams are not encrypted and are left alone. Objects inside object streams are not encrypted on their own: the
/// object stream is.
/// </summary>
internal static class ObjectDecryption
{
    /// <summary>Decrypts an object in place and returns it; a string, which cannot change in place, comes back decrypted.</summary>
    public static PdfObject Decrypt(PdfObject root, int number, int generation, StandardSecurityHandler handler)
    {
        // A cross-reference stream is not encrypted at all, its dictionary's strings (such as /ID) included (7.6.2).
        if (root is PdfStream { Dictionary: var header } && header.GetName("Type")?.Value == "XRef")
        {
            return root;
        }

        if (root is PdfString rootText)
        {
            return Decrypted(rootText, number, generation, handler);
        }

        Stack<PdfObject> pending = new([root]);
        while (pending.TryPop(out PdfObject? value))
        {
            switch (value)
            {
                case PdfArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (array[i] is PdfString text)
                        {
                            array.ReplaceParsed(i, Decrypted(text, number, generation, handler));
                        }
                        else
                        {
                            pending.Push(array[i]);
                        }
                    }

                    break;
                case PdfDictionary dictionary:
                    DecryptDictionary(dictionary, number, generation, handler, pending);
                    break;
                case PdfStream stream:
                    DecryptDictionary(stream.Dictionary, number, generation, handler, pending);
                    string? type = stream.Dictionary.GetName("Type")?.Value;
                    if (type == "XRef" || (type == "Metadata" && !handler.EncryptMetadata))
                    {
                        break;
                    }

                    stream.ReplaceData(new DecryptedData(stream.Data, handler, number, generation, CryptFilter(stream)));
                    break;
            }
        }

        return root;
    }

    private static void DecryptDictionary(PdfDictionary dictionary, int number, int generation, StandardSecurityHandler handler, Stack<PdfObject> pending)
    {
        // A signature's /Contents is not encrypted, so the signed bytes stay what was signed (7.6.2).
        bool signature = dictionary.GetName("Type")?.Value is "Sig" or "DocTimeStamp" || dictionary.ContainsKey("ByteRange");
        foreach ((PdfName key, PdfObject item) in dictionary.Entries.ToArray())
        {
            if (item is PdfString text)
            {
                if (!(signature && key.Value == "Contents"))
                {
                    dictionary.ReplaceParsed(key, Decrypted(text, number, generation, handler));
                }
            }
            else
            {
                pending.Push(item);
            }
        }
    }

    private static PdfString Decrypted(PdfString text, int number, int generation, StandardSecurityHandler handler)
    {
        return PdfString.FromParsed(handler.DecryptString(text.Bytes, number, generation), text.IsHex);
    }

    /// <summary>The crypt filter a stream names for itself with a leading <c>/Crypt</c> filter (7.6.6), or null for the default.</summary>
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

/// <summary>Stream data decrypted when read.</summary>
internal sealed class DecryptedData(StreamData encrypted, StandardSecurityHandler handler, int number, int generation, string? cryptFilter) : StreamData
{
    private long? length;

    /// <summary>The decrypted length; AES padding makes it known only after decrypting, so this decrypts once.</summary>
    public override long Length => length ??= Read().Length;

    /// <summary>The encrypted length, which equal plaintexts share, known without decrypting.</summary>
    public override long StoredLength => encrypted.StoredLength;

    public override byte[] Read()
    {
        byte[] plain = handler.DecryptStream(encrypted.Read(), number, generation, cryptFilter);
        length = plain.Length;
        return plain;
    }
}
