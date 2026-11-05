using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>One style of the style sheet as the file gives it ([MS-DOC] 2.9.258, STD).</summary>
internal sealed class DocStyleEntry
{
    public int Istd { get; init; }

    public int Sti { get; init; }

    /// <summary>stk: 1 paragraph, 2 character, 3 table, 4 list.</summary>
    public int Kind { get; init; }

    public int Base { get; init; } = 0xFFF;

    public int Next { get; init; } = 0xFFF;

    public int Link { get; init; } = 0xFFF;

    public string Name { get; set; } = "";

    public bool Hidden { get; init; }

    public bool Primary { get; init; }

    public int? Priority { get; init; }

    /// <summary>The UpxPapx's grpprl (after its istd), the UpxChpx's and the UpxTapx's.</summary>
    public byte[] Papx { get; init; } = [];

    public byte[] Chpx { get; init; } = [];

    public byte[] Tapx { get; init; } = [];

    /// <summary>The style's own paragraph properties, once applied.</summary>
    public DocPap Pap { get; } = new();

    /// <summary>The style's own character properties, once applied.</summary>
    public DocChp Chp { get; } = new();

    /// <summary>The model style made for it.</summary>
    public Style? Style { get; set; }
}

/// <summary>
/// The style sheet ([MS-DOC] 2.9.271, STSH): the STSHI's default fonts, then an STD per istd, each with its base, its
/// name and its property exceptions. The styles' sprms go on model styles through <see cref="DocProperties"/>; a style's
/// toggles ($80, $81) are taken against its base's resolved value.
/// </summary>
internal sealed class DocStyles
{
    private readonly List<DocStyleEntry?> entries;
    private readonly Dictionary<int, RunFormat> resolved = [];

    private DocStyles(List<DocStyleEntry?> entries, int[] defaultFonts)
    {
        this.entries = entries;
        DefaultFonts = defaultFonts;
    }

    /// <summary>The STSHI's rgftcStandardChpStsh: the default ASCII, East Asian and other fonts' indexes.</summary>
    public int[] DefaultFonts { get; }

    public int Count => entries.Count;

    public DocStyleEntry? this[int istd] => istd >= 0 && istd < entries.Count ? entries[istd] : null;

    public static DocStyles Read(ReadOnlySpan<byte> stsh, bool word6, WarningCollector warnings)
    {
        List<DocStyleEntry?> entries = [];
        int[] defaultFonts = [0, 0, 0];
        if (stsh.Length < 2)
        {
            return new DocStyles(entries, defaultFonts);
        }

        int cbStshi = BinaryPrimitives.ReadUInt16LittleEndian(stsh);
        if (cbStshi < (word6 ? 12 : 18) || 2 + cbStshi > stsh.Length)
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "The style sheet's header is damaged; the styles are left out.");
            return new DocStyles(entries, defaultFonts);
        }

        ReadOnlySpan<byte> stshi = stsh.Slice(2, cbStshi);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(stshi);
        int stdfSize = BinaryPrimitives.ReadUInt16LittleEndian(stshi[2..]);
        if (stshi.Length >= 18)
        {
            defaultFonts = [BinaryPrimitives.ReadUInt16LittleEndian(stshi[12..]), BinaryPrimitives.ReadUInt16LittleEndian(stshi[14..]), BinaryPrimitives.ReadUInt16LittleEndian(stshi[16..])];
        }
        else if (stshi.Length >= 14)
        {
            int ftc = BinaryPrimitives.ReadUInt16LittleEndian(stshi[12..]);
            defaultFonts = [ftc, ftc, ftc];
        }

        int at = 2 + cbStshi;
        for (int i = 0; i < count && at + 2 <= stsh.Length && i < 0xFFE; i++)
        {
            int cbStd = BinaryPrimitives.ReadUInt16LittleEndian(stsh[at..]);
            at += 2;
            if (cbStd == 0 || at + cbStd > stsh.Length)
            {
                entries.Add(null);
                at += Math.Min(cbStd, stsh.Length - at);
                continue;
            }

            entries.Add(ReadStd(stsh.Slice(at, cbStd), i, stdfSize, word6));
            at += cbStd;
        }

        return new DocStyles(entries, defaultFonts);
    }

    // An STD: the StdfBase (sti, stk and istdBase, cupx and istdNext, bchUpe, grfstd), Word 97's StdfPost2000 (istdLink and
    // iPriority) when the base is 18 bytes, the name, then the UPXs.
    private static DocStyleEntry? ReadStd(ReadOnlySpan<byte> std, int istd, int stdfSize, bool word6)
    {
        if (std.Length < 8 || stdfSize < 8)
        {
            return null;
        }

        int sti = BinaryPrimitives.ReadUInt16LittleEndian(std) & 0x0FFF;
        int stkBase = BinaryPrimitives.ReadUInt16LittleEndian(std[2..]);
        int cupxNext = BinaryPrimitives.ReadUInt16LittleEndian(std[4..]);
        int grfstd = !word6 && std.Length >= 10 ? BinaryPrimitives.ReadUInt16LittleEndian(std[8..]) : 0;
        int link = 0xFFF;
        int? priority = null;
        if (!word6 && stdfSize >= 18 && std.Length >= 18)
        {
            link = BinaryPrimitives.ReadUInt16LittleEndian(std[10..]) & 0x0FFF;
            int iPriority = BinaryPrimitives.ReadUInt16LittleEndian(std[16..]) >> 4;
            priority = iPriority is > 0 and < 0xFFF ? iPriority : null;
        }

        int kind = stkBase & 0x0F;
        int at = stdfSize;
        if (at + 2 > std.Length)
        {
            return new DocStyleEntry { Istd = istd, Sti = sti, Kind = kind, Base = stkBase >> 4, Next = cupxNext >> 4, Name = NameOf(sti, istd) };
        }

        string name;
        if (word6)
        {
            // Word 6's name: a length byte, the 8-bit characters, a null.
            int length = Math.Min(std[at], std.Length - at - 1);
            name = Encoding.Latin1.GetString(std.Slice(at + 1, Math.Max(0, length)));
            at += 1 + std[at] + 1;
        }
        else
        {
            int cch = BinaryPrimitives.ReadUInt16LittleEndian(std[at..]);
            int length = Math.Min(cch, Math.Max(0, (std.Length - at - 2) / 2));
            StringBuilder text = new(length);
            for (int c = 0; c < length; c++)
            {
                text.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(std[(at + 2 + (2 * c))..]));
            }

            name = text.ToString();
            at += 2 + (2 * cch) + 2;
        }

        // The UPXs start at an even offset.
        at += at & 1;
        byte[] papx = [];
        byte[] chpx = [];
        byte[] tapx = [];
        switch (kind)
        {
            case 1:
                papx = Upx(std, ref at, istdFirst: true);
                chpx = Upx(std, ref at, istdFirst: false);
                break;
            case 2:
                chpx = Upx(std, ref at, istdFirst: false);
                break;
            case 3:
                tapx = Upx(std, ref at, istdFirst: false);
                papx = Upx(std, ref at, istdFirst: true);
                chpx = Upx(std, ref at, istdFirst: false);
                break;
            case 4:
                papx = Upx(std, ref at, istdFirst: true);
                break;
        }

        return new DocStyleEntry
        {
            Istd = istd,
            Sti = sti,
            Kind = kind,
            Base = stkBase >> 4,
            Next = cupxNext >> 4,
            Link = link,
            Name = name.Length > 0 ? name.Split(',')[0] : NameOf(sti, istd),
            Hidden = (grfstd & 0x0102) != 0,
            Primary = (grfstd & 0x1000) != 0,
            Priority = priority,
            Papx = papx,
            Chpx = chpx,
            Tapx = tapx,
        };
    }

    // An LPUpx: its size, the UPX (a UpxPapx's istd first), and a pad byte to an even size.
    private static byte[] Upx(ReadOnlySpan<byte> std, ref int at, bool istdFirst)
    {
        if (at + 2 > std.Length)
        {
            return [];
        }

        int size = BinaryPrimitives.ReadUInt16LittleEndian(std[at..]);
        int start = at + 2 + (istdFirst ? 2 : 0);
        int end = Math.Min(at + 2 + size, std.Length);
        at += 2 + size + (size & 1);
        return start < end ? std[start..end].ToArray() : [];
    }

    // The names of the built-in styles a file may leave unnamed, by sti ([MS-DOC] 2.9.260).
    private static string NameOf(int sti, int istd) => sti switch
    {
        0 => "Normal",
        >= 1 and <= 9 => "heading " + sti.ToString(System.Globalization.CultureInfo.InvariantCulture),
        29 => "footnote text",
        30 => "annotation text",
        31 => "header",
        32 => "footer",
        38 => "footnote reference",
        39 => "annotation reference",
        41 => "page number",
        42 => "endnote reference",
        43 => "endnote text",
        65 => "Default Paragraph Font",
        105 => "Normal Table",
        107 => "No List",
        _ => "Style " + istd.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Applies each style's sprms and makes the model styles: paragraph, character and table styles, with their bases, next
    /// styles and links, and Word's defaults marked.
    /// </summary>
    public void Make(Document document, DocContext context, Func<int, List?> list)
    {
        HashSet<int> prepared = [];
        foreach (DocStyleEntry? entry in entries)
        {
            if (entry is not null)
            {
                Prepare(entry, context, prepared, 0);
            }
        }

        HashSet<string> ids = new(document.Styles.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        WordStyleIds styleIds = new();
        foreach (DocStyleEntry? entry in entries)
        {
            if (entry is null || entry.Kind is not (1 or 2 or 3))
            {
                continue;
            }

            int istd = entry.Istd;
            string id = styleIds.Make(entry.Name, ids);
            Style style = entry.Kind switch
            {
                2 => document.Styles.AddCharacterStyle(id, entry.Name),
                3 => document.Styles.AddTableStyle(id, entry.Name),
                _ => document.Styles.AddParagraphStyle(id, entry.Name),
            };
            entry.Style = style;
            style.Hidden = entry.Hidden;
            style.IsPrimary = entry.Primary;
            style.Priority = entry.Priority;
            switch (style)
            {
                case ParagraphStyle paragraph:
                    paragraph.ParagraphFormat.CopyRaw(entry.Pap.Format);
                    paragraph.RunFormat.CopyRaw(entry.Chp.Format);
                    // The list and its level each pass down from the base style unless the style gives its own.
                    if (entry.Pap.Ilfo is int ilfo)
                    {
                        paragraph.ParagraphFormat.List = ilfo > 0 ? list(ilfo) ?? List.None : List.None;
                    }

                    if (entry.Pap.Ilvl is int ilvl)
                    {
                        paragraph.ParagraphFormat.ListLevel = Math.Clamp(ilvl, 0, 8);
                    }

                    if (entry.Pap.Frame is { } frame)
                    {
                        paragraph.ParagraphFormat.Frame = frame;
                    }

                    if (istd == 0)
                    {
                        document.Styles.SetDefault(paragraph);
                    }

                    break;
                case CharacterStyle character:
                    character.RunFormat.CopyRaw(entry.Chp.Format);
                    if (entry.Sti == 65)
                    {
                        document.Styles.SetDefault(character);
                    }

                    break;
                case TableStyle table:
                    table.RunFormat.CopyRaw(entry.Chp.Format);
                    table.ParagraphFormat.CopyRaw(entry.Pap.Format);
                    DocRow row = new(context.Word6);
                    DocProperties.ApplyPap(new DocPap(), entry.Tapx, context, row);
                    table.TableFormat.CopyRaw(row.Build().Table);
                    if (entry.Sti == 105)
                    {
                        document.Styles.SetDefault(table);
                    }

                    break;
            }
        }

        foreach (DocStyleEntry? entry in entries)
        {
            if (entry?.Style is not { } style)
            {
                continue;
            }

            if (this[entry.Base]?.Style is { } parent && entry.Base != entry.Istd && parent.Type == style.Type)
            {
                try
                {
                    style.BasedOn = parent;
                }
                catch (ArgumentException)
                {
                    // A loop of based-on links; the style keeps no base.
                }
            }

            if (style is ParagraphStyle paragraph)
            {
                if (this[entry.Next]?.Style is ParagraphStyle next)
                {
                    paragraph.Next = next;
                }

                if (this[entry.Link]?.Style is CharacterStyle linked)
                {
                    paragraph.Linked = linked;
                }
            }
            else if (style is CharacterStyle character && this[entry.Link]?.Style is ParagraphStyle linkedParagraph)
            {
                character.Linked = linkedParagraph;
            }
        }
    }

    // Applies a style's sprms, its base's first, so its toggles see what the base resolves to.
    private void Prepare(DocStyleEntry entry, DocContext context, HashSet<int> prepared, int depth)
    {
        if (!prepared.Add(entry.Istd))
        {
            return;
        }

        if (depth < 16 && entry.Base != entry.Istd && this[entry.Base] is { } parent)
        {
            Prepare(parent, context, prepared, depth + 1);
        }

        DocProperties.ApplyChp(entry.Chp, entry.Chpx, context, p => DocProperties.ToggleOf(Base(entry.Istd), p));
        if (entry.Kind is 1 or 3 or 4)
        {
            DocProperties.ApplyPap(entry.Pap, entry.Papx, context);
        }
    }

    /// <summary>A style's character formatting with everything it inherits, from its base styles down.</summary>
    public RunFormat Resolved(int istd)
    {
        return Resolved(istd, 0);
    }

    private RunFormat Resolved(int istd, int depth)
    {
        if (resolved.TryGetValue(istd, out RunFormat? format))
        {
            return format;
        }

        format = new RunFormat();
        if (this[istd] is { } entry && depth < 16)
        {
            if (entry.Base != istd && entry.Base < 0xFFE)
            {
                format.Apply(Resolved(entry.Base, depth + 1));
            }

            format.Apply(entry.Chp.Format);
        }

        resolved[istd] = format;
        return format;
    }

    // The character formatting a style's base gives, for its own toggles.
    private RunFormat Base(int istd)
    {
        return this[istd] is { } entry && entry.Base != istd && entry.Base < 0xFFE ? Resolved(entry.Base) : new RunFormat();
    }
}
