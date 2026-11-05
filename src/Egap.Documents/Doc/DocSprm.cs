using System.Buffers.Binary;

namespace Egap.Documents.Doc;

/// <summary>
/// Walks a grpprl ([MS-DOC] 2.2.5): each Prl is a sprm and its operand. In a Word 97 file the sprm is two bytes and the
/// operand's size comes from its spra bits; in a Word 6 file the sprm is one byte, sized by <see cref="Word6"/>, and given
/// out as the Word 97 sprm that sets the same property (sprms Word 97 has no counterpart for are skipped). A variable-size
/// operand is given with its size byte (or word) first. A Prl that runs past the end stops the walk.
/// </summary>
internal ref struct DocSprms
{
    private readonly ReadOnlySpan<byte> grpprl;
    private readonly bool word6;
    private int at;

    public DocSprms(ReadOnlySpan<byte> grpprl, bool word6)
    {
        this.grpprl = grpprl;
        this.word6 = word6;
        at = 0;
        Sprm = 0;
        Operand = default;
    }

    /// <summary>The current Prl's sprm, as Word 97 numbers it.</summary>
    public ushort Sprm { get; private set; }

    /// <summary>The current Prl's operand.</summary>
    public ReadOnlySpan<byte> Operand { get; private set; }

    /// <summary>Whether the walk stopped at a Prl it could not size, so later ones were left out.</summary>
    public bool Damaged { get; private set; }

    public readonly DocSprms GetEnumerator() => this;

    public readonly DocSprms Current => this;

    public bool MoveNext()
    {
        return word6 ? MoveNextWord6() : MoveNext97();
    }

    private bool MoveNext97()
    {
        if (at + 2 > grpprl.Length)
        {
            return false;
        }

        ushort sprm = BinaryPrimitives.ReadUInt16LittleEndian(grpprl[at..]);
        int size = OperandSize(sprm, grpprl[(at + 2)..]);
        if (size < 0 || at + 2 + size > grpprl.Length)
        {
            Damaged = at + 2 < grpprl.Length || size < 0;
            at = grpprl.Length;
            return false;
        }

        Sprm = sprm;
        Operand = grpprl.Slice(at + 2, size);
        at += 2 + size;
        return true;
    }

    private bool MoveNextWord6()
    {
        while (at < grpprl.Length)
        {
            byte code = grpprl[at];
            if (code == 0)
            {
                // Word 6 pads a grpprl with zeros.
                at = grpprl.Length;
                return false;
            }

            (int size, ushort same) = Word6(code);
            if (size == -1 && at + 1 < grpprl.Length)
            {
                size = 1 + grpprl[at + 1];
            }
            else if (size == -2 && at + 2 < grpprl.Length)
            {
                size = 2 + BinaryPrimitives.ReadUInt16LittleEndian(grpprl[(at + 1)..]) - 1;
            }

            if (size < 0 || at + 1 + size > grpprl.Length)
            {
                Damaged = true;
                at = grpprl.Length;
                return false;
            }

            ReadOnlySpan<byte> operand = grpprl.Slice(at + 1, size);
            at += 1 + size;
            if (same != 0)
            {
                Sprm = same;
                Operand = operand;
                return true;
            }
        }

        return false;
    }

    /// <summary>A Word 97 sprm's operand size in bytes ([MS-DOC] 2.2.5.1), or -1 when it cannot be read.</summary>
    public static int OperandSize(ushort sprm, ReadOnlySpan<byte> rest)
    {
        switch (sprm >> 13)
        {
            case 0 or 1:
                return 1;
            case 2 or 4 or 5:
                return 2;
            case 3:
                return 4;
            case 7:
                return 3;
        }

        if (sprm is 0xD608 or 0xD606)
        {
            // sprmTDefTable and sprmTDefTable10: a 2-byte size, one more than the bytes after it.
            return rest.Length >= 2 ? Math.Max(2, 2 + BinaryPrimitives.ReadUInt16LittleEndian(rest) - 1) : -1;
        }

        if (sprm == 0xC615 && rest.Length >= 1 && rest[0] == 255)
        {
            // sprmPChgTabs with its long form ([MS-DOC] 2.6.2): the deletions with their close ranges, then the additions.
            if (rest.Length < 2)
            {
                return -1;
            }

            int deleted = rest[1];
            int added = 2 + (4 * deleted);
            return added < rest.Length ? added + 1 + (3 * rest[added]) : -1;
        }

        return rest.Length >= 1 ? 1 + rest[0] : -1;
    }

    /// <summary>
    /// A Word 6 sprm's operand size (0 none, -1 a size byte and that many, -2 a size word, -3 unknown) and the Word 97
    /// sprm that sets the same property (0 for none this reader applies). Ported from ClassicMac; where [MS-DOC] 2.9.215 Prm0
    /// gives an isprm for the same sprm (5, 7 to 9, 24, 25, 65 to 67, 83, 85 to 92, 94, 98, 104 and 117), the numbers
    /// agree. The rest are as ClassicMac reads Word 6 and Word 95 files; Word no longer opens them, so they are not
    /// checked against it.
    /// </summary>
    public static (int Size, ushort Same) Word6(byte sprm) => sprm switch
    {
        2 => (2, 0),
        4 or 6 or 37 or 44 or 50 or 51 => (1, 0),
        5 => (1, 0x2403),                            // sprmPJc
        7 => (1, 0x2405),                            // sprmPFKeep
        8 => (1, 0x2406),                            // sprmPFKeepFollow
        9 => (1, 0x2407),                            // sprmPFPageBreakBefore
        10 or 11 or 14 => (1, 0),
        13 => (1, 0x240D),                           // sprmPNLvlAnm
        16 => (2, 0x840E),                           // sprmPDxaRight
        17 => (2, 0x840F),                           // sprmPDxaLeft
        19 => (2, 0x8411),                           // sprmPDxaLeft1
        20 => (4, 0x6412),                           // sprmPDyaLine
        18 or 26 or 27 or 28 or >= 30 and <= 36 or >= 38 and <= 43 or 45 or 46 or 47 or 48 or 49 => (2, 0),
        21 => (2, 0xA413),                           // sprmPDyaBefore
        22 => (2, 0xA414),                           // sprmPDyaAfter
        24 => (1, 0x2416),                           // sprmPFInTable
        25 => (1, 0x2417),                           // sprmPFTtp
        29 => (1, 0),
        68 => (-1, 0x6A03),                          // sprmCPicLocation, with a size byte
        12 => (-1, 0xC63E),                          // sprmPAnld
        15 => (-1, 0xC60D),                          // sprmPChgTabsPapx
        23 => (-1, 0xC615),                          // sprmPChgTabs
        64 or 74 or 77 or 79 or 81 or 82 or 103 or 105 or 106 or 108 or >= 111 and <= 116 or 179 or 181 or 191 or 207 => (-1, 0),
        117 => (1, 0x0855),                          // sprmCFSpec
        65 => (1, 0x0800),                           // sprmCFRMarkDel
        66 => (1, 0x0801),                           // sprmCFRMarkIns
        67 => (1, 0x0802),                           // sprmCFFldVanish
        71 or 75 or 100 or 102 or 118 or 119 => (1, 0),
        104 => (1, 0x2A48),                          // sprmCIss
        90 => (1, 0x083A),                           // sprmCFSmallCaps
        69 or 72 or 80 or 96 or 97 or 101 or 107 or 109 or 110 or >= 121 and <= 124 => (2, 0),
        70 => (4, 0),
        73 or 95 => (3, 0),
        83 => (0, 0x2A33),                           // sprmCPlain
        85 => (1, 0x0835),                           // sprmCFBold
        86 => (1, 0x0836),                           // sprmCFItalic
        87 => (1, 0x0837),                           // sprmCFStrike
        88 => (1, 0x0838),                           // sprmCFOutline
        89 => (1, 0x0839),                           // sprmCFShadow
        91 => (1, 0x083B),                           // sprmCFCaps
        92 => (1, 0x083C),                           // sprmCFVanish
        93 => (2, 0x4A4F),                           // sprmCFtc, as sprmCRgFtc0
        94 => (1, 0x2A3E),                           // sprmCKul
        98 => (1, 0x2A42),                           // sprmCIco
        99 => (2, 0x4A43),                           // sprmCHps
        131 or 132 or 138 or 139 or 142 or 143 or 146 or 147 or 150 or 151 or 152 or 153 or 158 or 159 or 162 or 185 or 186 => (1, 0),
        136 or 137 => (3, 0),
        140 or 141 or 144 or 145 or 148 or 149 or 154 or 155 or 156 or 157 or 160 or 161 or >= 164 and <= 171 or 182 or 183 or 184 or 189 or 195 or 197 or 198 => (2, 0),
        133 => (-1, 0),
        163 => (0, 0),
        187 => (12, 0),
        188 => (-2, 0),
        190 => (-2, 0xD608),                         // sprmTDefTable
        192 or 194 or 196 or 200 => (4, 0),
        193 or 199 => (5, 0),
        _ => (-3, 0),
    };
}
