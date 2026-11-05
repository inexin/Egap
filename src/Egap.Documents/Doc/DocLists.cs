using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// The lists: Word 97's list definitions (PlfLst, each followed in the table stream by its LVLs) and list overrides
/// (PlfLfo), which a paragraph names by its ilfo; and Word 6's paragraph numbers (sprmPAnld), one single-level list per
/// distinct number format, as old-style numbers are read from RTF.
/// </summary>
internal sealed class DocLists
{
    private readonly Document document;
    private readonly DocContext context;
    private readonly Dictionary<int, ListDefinition> definitions = [];
    private readonly List<List?> overrides = [];
    private readonly Dictionary<string, List> numbers = new(StringComparer.Ordinal);
    private int nextId;

    private DocLists(Document document, DocContext context)
    {
        this.document = document;
        this.context = context;
    }

    /// <summary>The list a paragraph's ilfo (from 1) names, or null.</summary>
    public List? this[int ilfo] => ilfo >= 1 && ilfo <= overrides.Count ? overrides[ilfo - 1] : null;

    /// <summary>Reads the list definitions at <paramref name="plfLst"/> and the overrides at <paramref name="plfLfo"/>.</summary>
    public static DocLists Read(Document document, DocContext context, ReadOnlySpan<byte> table, (long Fc, long Lcb) plfLst, (long Fc, long Lcb) plfLfo, int maxEntries)
    {
        DocLists lists = new(document, context);
        if (plfLst.Lcb >= 2 && plfLst.Fc + plfLst.Lcb <= table.Length)
        {
            lists.ReadDefinitions(table, (int)plfLst.Fc, (int)plfLst.Lcb, maxEntries);
        }

        if (plfLfo.Lcb >= 4 && plfLfo.Fc + plfLfo.Lcb <= table.Length)
        {
            lists.ReadOverrides(table.Slice((int)plfLfo.Fc, (int)plfLfo.Lcb), maxEntries);
        }

        return lists;
    }

    // PlfLst ([MS-DOC] 2.9.183): cLst, then an LSTF (28 bytes: lsid, tplc, rgistdPara, flags, grfhic) per list; after the
    // PlfLst, each list's LVLs, one for a simple list and nine otherwise.
    private void ReadDefinitions(ReadOnlySpan<byte> table, int fc, int lcb, int maxEntries)
    {
        ReadOnlySpan<byte> plf = table.Slice(fc, lcb);
        int count = Math.Min(Math.Max(0, (int)BinaryPrimitives.ReadInt16LittleEndian(plf)), Math.Min((lcb - 2) / 28, maxEntries));
        int at = fc + lcb;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> lstf = plf.Slice(2 + (28 * i), 28);
            int lsid = BinaryPrimitives.ReadInt32LittleEndian(lstf);
            byte flags = lstf[26];
            bool simple = (flags & 0x01) != 0;
            bool hybrid = (flags & 0x10) != 0;
            int levels = simple ? 1 : 9;
            ListDefinition definition = document.Lists.AddDefinition(definitions.Count + 1);
            definition.Kind = simple ? ListKind.SingleLevel : hybrid ? ListKind.HybridMultilevel : definition.Kind;
            for (int level = 0; level < levels; level++)
            {
                if (ReadLevel(table, ref at) is not { } read)
                {
                    break;
                }

                read.FillLevel(definition.Levels[level]);
            }

            definitions.TryAdd(lsid, definition);
        }
    }

    // An LVL ([MS-DOC] 2.9.150): the LVLF (iStartAt, nfc, jc and flags, rgbxchNums, ixchFollow, ..., cbGrpprlChpx,
    // cbGrpprlPapx, ilvlRestartLim), the paragraph grpprl, the character grpprl, then the number text (an Xst) with the
    // level numbers as characters 0 to 8 at the places rgbxchNums names.
    private WordListLevel? ReadLevel(ReadOnlySpan<byte> table, ref int at)
    {
        if (at + 28 > table.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> lvlf = table.Slice(at, 28);
        WordListLevel level = new();
        WordProperties.Set(level, WordListLevelProperty.StartAt, BinaryPrimitives.ReadInt32LittleEndian(lvlf));
        WordProperties.Set(level, WordListLevelProperty.NumberFormat, lvlf[4]);
        WordProperties.Set(level, WordListLevelProperty.Alignment, lvlf[5] & 3);
        WordProperties.Set(level, WordListLevelProperty.Legal, (lvlf[5] >> 2) & 1);
        WordProperties.Set(level, WordListLevelProperty.NoRestart, (lvlf[5] >> 3) & 1);
        WordProperties.Set(level, WordListLevelProperty.Follow, lvlf[15]);
        ReadOnlySpan<byte> places = lvlf.Slice(6, 9);
        int cbChpx = lvlf[24];
        int cbPapx = lvlf[25];
        at += 28;
        if (at + cbPapx + cbChpx + 2 > table.Length)
        {
            at = table.Length;
            return level;
        }

        DocPap pap = new();
        DocProperties.ApplyPap(pap, table.Slice(at, cbPapx), context);
        level.Para = pap.Format;
        at += cbPapx;
        DocChp chp = new();
        DocProperties.ApplyChp(chp, table.Slice(at, cbChpx), context, _ => false);
        level.Char = chp.Format;
        at += cbChpx;
        int cch = BinaryPrimitives.ReadUInt16LittleEndian(table[at..]);
        at += 2;
        int length = Math.Min(cch, (table.Length - at) / 2);
        HashSet<int> numbers = [];
        foreach (byte place in places)
        {
            if (place > 0)
            {
                numbers.Add(place);
            }
        }

        StringBuilder text = new(length);
        for (int i = 0; i < length; i++)
        {
            char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(table[(at + (2 * i))..]);
            if (numbers.Contains(i + 1) && c <= '\u0008')
            {
                text.Append('%').Append((char)('1' + c));
            }
            else
            {
                text.Append(c);
            }
        }

        level.Text = text.ToString();
        at += 2 * cch;
        return level;
    }

    // PlfLfo ([MS-DOC] 2.9.182): lfoMac, an LFO (16 bytes: lsid, two unused, clfolvl, ibstFltAutoNum, grfhic, unused) per
    // override, then an LFOData per override: a CP, then clfolvl LFOLVLs (iStartAt, then ilvl in bits 0-3, fStartAt bit 4,
    // fFormatting bit 5), each with an LVL when fFormatting is set.
    private void ReadOverrides(ReadOnlySpan<byte> plf, int maxEntries)
    {
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(plf), (uint)Math.Min((plf.Length - 4) / 16, maxEntries));
        int at = 4 + (16 * count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> lfo = plf.Slice(4 + (16 * i), 16);
            int lsid = BinaryPrimitives.ReadInt32LittleEndian(lfo);
            int levels = lfo[12];
            List? list = null;
            if (definitions.TryGetValue(lsid, out ListDefinition? definition))
            {
                list = document.Lists.Add(++nextId, definition);
            }

            overrides.Add(list);
            // The LFOData: a CP, then the levels' overrides.
            at += 4;
            for (int l = 0; l < levels && at + 8 <= plf.Length; l++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(plf[at..]);
                int flags = BinaryPrimitives.ReadInt32LittleEndian(plf[(at + 4)..]);
                at += 8;
                int ilvl = flags & 0x0F;
                if (ilvl > 8)
                {
                    continue;
                }

                if ((flags & 0x20) != 0)
                {
                    int lvlAt = at;
                    WordListLevel? level = ReadLevel(plf, ref lvlAt);
                    at = lvlAt;
                    if (level is not null && list is not null)
                    {
                        level.FillLevel(list.Overrides[ilvl].CreateLevel());
                    }
                }

                if ((flags & 0x10) != 0 && list is not null)
                {
                    list.Overrides[ilvl].StartAt = Math.Max(0, start);
                }
            }
        }
    }

    /// <summary>
    /// The list a Word 6 paragraph number (an ANLD, [MS-DOC] 2.9.6 in its Word 6 form with 8-bit text) makes: nfc,
    /// cxchTextBefore, cxchTextAfter, jc and flags, more flags, kul and ico, ftc, hps, iStartAt, dxaIndent, dxaSpace,
    /// fNumber1, fNumberAcross, fRestartHdn, a spare, then the text before and after the number.
    /// </summary>
    public List? Number(byte[] anld, int level)
    {
        if (anld.Length < 20)
        {
            return null;
        }

        string key = Convert.ToHexString(anld) + ":" + level.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (numbers.TryGetValue(key, out List? found))
        {
            return found;
        }

        if (numbers.Count >= 10_000)
        {
            return null;
        }

        int nfc = anld[0];
        int before = anld[1];
        int after = anld[2];
        bool wide = !context.Word6;
        int textAt = 20;
        string text = Text(anld, textAt, before + after, wide);
        string prefix = text[..Math.Min(before, text.Length)];
        string suffix = text[Math.Min(before, text.Length)..];
        ListDefinition definition = document.Lists.AddDefinition(definitions.Count + numbers.Count + 1);
        definition.Kind = ListKind.SingleLevel;
        ListLevel listLevel = definition.Levels[0];
        bool bullet = nfc == 23;
        listLevel.NumberStyle = bullet ? NumberStyle.Bullet : WordProperties.NumberStyleOf(nfc);
        listLevel.LevelText = bullet ? prefix + suffix : prefix + "%1" + suffix;
        listLevel.StartAt = Math.Max(0, (int)BinaryPrimitives.ReadInt16LittleEndian(anld.AsSpan(10)));
        int ftc = BinaryPrimitives.ReadUInt16LittleEndian(anld.AsSpan(6));
        if (ftc < context.Fonts.Count && context.Fonts[ftc] is { Length: > 0 } font)
        {
            listLevel.RunFormat.FontAscii = font;
            listLevel.RunFormat.FontHighAnsi = font;
        }

        List list = document.Lists.Add(1_000_000 + numbers.Count, definition);
        numbers[key] = list;
        return list;
    }

    private static string Text(byte[] anld, int at, int count, bool wide)
    {
        StringBuilder text = new(count);
        for (int i = 0; i < count; i++)
        {
            int offset = at + (wide ? 2 * i : i);
            if (offset + (wide ? 2 : 1) > anld.Length)
            {
                break;
            }

            text.Append(wide ? (char)BinaryPrimitives.ReadUInt16LittleEndian(anld.AsSpan(offset)) : (char)anld[offset]);
        }

        return text.ToString();
    }
}
