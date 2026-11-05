using System.Buffers.Binary;
using System.Text;

namespace Egap.Documents.Doc;

/// <summary>The font table: the names that the ftc of a sprm indexes.</summary>
internal static class DocFonts
{
    /// <summary>
    /// Reads SttbfFfn ([MS-DOC] 2.9.286): a count, a zero extra-data size, then per font a size byte and an FFN
    /// ([MS-DOC] 2.9.82) whose name, null-terminated UTF-16, starts 39 bytes in. Word 6's table is its size in bytes, then
    /// per font a size byte (less one), ffid, a weight word, a charset, the alternate name's index and the name, 8-bit and
    /// null-terminated.
    /// </summary>
    public static List<string> Read(ReadOnlySpan<byte> table, bool word6, int maxEntries)
    {
        return word6 ? ReadWord6(table, maxEntries) : Read97(table, maxEntries);
    }

    private static List<string> Read97(ReadOnlySpan<byte> sttbf, int maxEntries)
    {
        List<string> fonts = [];
        if (sttbf.Length < 4)
        {
            return fonts;
        }

        int count = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(sttbf), maxEntries);
        int at = 4;
        for (int i = 0; i < count && at < sttbf.Length; i++)
        {
            int size = sttbf[at] + 1;
            ReadOnlySpan<byte> ffn = sttbf.Slice(at + 1, Math.Min(size - 1, sttbf.Length - at - 1));
            StringBuilder name = new();
            for (int c = 39; c + 1 < ffn.Length; c += 2)
            {
                char ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(ffn[c..]);
                if (ch == '\0')
                {
                    break;
                }

                name.Append(ch);
            }

            fonts.Add(name.ToString());
            at += size;
        }

        return fonts;
    }

    private static List<string> ReadWord6(ReadOnlySpan<byte> table, int maxEntries)
    {
        List<string> fonts = [];
        if (table.Length < 2)
        {
            return fonts;
        }

        int end = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(table), table.Length);
        for (int at = 2; at + 6 <= end && fonts.Count < maxEntries;)
        {
            int size = table[at] + 1;
            ReadOnlySpan<byte> name = table.Slice(at + 6, Math.Max(0, Math.Min(size - 6, end - at - 6)));
            int zero = name.IndexOf((byte)0);
            fonts.Add(Encoding.Latin1.GetString(zero >= 0 ? name[..zero] : name));
            at += size;
        }

        return fonts;
    }
}
