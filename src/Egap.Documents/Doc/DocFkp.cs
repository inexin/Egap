using System.Buffers.Binary;

namespace Egap.Documents.Doc;

/// <summary>
/// The character or paragraph properties of the stream's bytes ([MS-DOC] 2.8.5, 2.8.6, 2.9.33, 2.9.174): the bin table's
/// 512-byte formatted disk pages, each a list of byte runs and the CHPX or PAPX of each, read into one sorted list. A
/// paragraph run's block is its istd then its grpprl; a character run's is its grpprl.
/// </summary>
internal sealed class DocFkp
{
    /// <summary>The size of a formatted disk page.</summary>
    public const int PageSize = 512;

    private readonly List<(long From, long To, byte[] Block)> runs;

    private DocFkp(List<(long From, long To, byte[] Block)> runs)
    {
        this.runs = runs;
    }

    /// <summary>The number of runs.</summary>
    public int Count => runs.Count;

    /// <summary>The run at <paramref name="index"/>.</summary>
    public (long From, long To, byte[] Block) this[int index] => runs[index];

    /// <summary>Reads a PlcBteChpx or PlcBtePapx and the pages it names.</summary>
    public static DocFkp Read(byte[] document, ReadOnlySpan<byte> plc, bool paragraphs, bool word6, WarningCollector warnings)
    {
        List<(long From, long To, byte[] Block)> runs = [];
        if (plc.Length < 4)
        {
            return new DocFkp(runs);
        }

        // Word 6's page numbers are 2 bytes, Word 97's 4 (of which 22 bits count).
        int pnSize = word6 ? 2 : 4;
        int count = (plc.Length - 4) / (4 + pnSize);
        HashSet<long> seen = [];
        for (int i = 0; i < count; i++)
        {
            int pnAt = (4 * (count + 1)) + (pnSize * i);
            long pn = word6 ? BinaryPrimitives.ReadUInt16LittleEndian(plc[pnAt..]) : BinaryPrimitives.ReadUInt32LittleEndian(plc[pnAt..]) & 0x3FFFFF;
            if ((pn + 1) * PageSize > document.Length)
            {
                warnings.Add(ConversionWarningKind.FormattingDropped, "A formatting page lies past the end of the WordDocument stream; its formatting is left out.");
                continue;
            }

            if (!seen.Add(pn))
            {
                // A page named twice gives its runs once.
                continue;
            }

            ReadPage(document.AsSpan((int)(pn * PageSize), PageSize), paragraphs, word6, runs, warnings);
        }

        runs.Sort((a, b) => a.From.CompareTo(b.From));
        return new DocFkp(runs);
    }

    /// <summary>The index of the run holding the byte at <paramref name="fc"/>, or -1.</summary>
    public int Find(long fc)
    {
        int low = 0;
        int high = runs.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            (long from, long to, _) = runs[middle];
            if (to <= fc)
            {
                low = middle + 1;
            }
            else if (from > fc)
            {
                high = middle - 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    private static void ReadPage(ReadOnlySpan<byte> page, bool paragraphs, bool word6, List<(long, long, byte[])> runs, WarningCollector warnings)
    {
        int crun = page[PageSize - 1];
        // A PAPX page's BX is the offset byte and the PHE: 13 bytes, Word 6's 7.
        int entry = paragraphs ? (word6 ? 7 : 13) : 1;
        if ((4 * (crun + 1)) + (entry * crun) > PageSize - 1)
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "A formatting page says it holds more runs than it can; its formatting is left out.");
            return;
        }

        for (int r = 0; r < crun; r++)
        {
            long from = BinaryPrimitives.ReadUInt32LittleEndian(page[(4 * r)..]);
            long to = BinaryPrimitives.ReadUInt32LittleEndian(page[(4 * (r + 1))..]);
            if (to <= from)
            {
                continue;
            }

            int at = page[(4 * (crun + 1)) + (entry * r)] * 2;
            byte[] block = [];
            if (at > 0 && at < PageSize - 1)
            {
                if (paragraphs)
                {
                    // PapxInFkp: cb, or 0 and a second count; the GrpPrlAndIstd is 2 × cb − 1 bytes, or 2 × the second.
                    // Word 6's is a count of words, then that many words.
                    int cb = page[at];
                    (int start, int size) = word6 ? (at + 1, 2 * cb) : cb != 0 ? (at + 1, (2 * cb) - 1) : (at + 2, 2 * page[at + 1]);
                    block = page.Slice(start, Math.Max(0, Math.Min(size, PageSize - 1 - start))).ToArray();
                }
                else
                {
                    block = page.Slice(at + 1, Math.Min(page[at], PageSize - 1 - at - 1)).ToArray();
                }
            }

            runs.Add((from, to, block));
        }
    }
}
