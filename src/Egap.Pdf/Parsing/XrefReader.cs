using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf.Parsing;

/// <summary>
/// Reads a file's cross-reference (ISO 32000-2, 7.5.4 to 7.5.8): finds <c>startxref</c>, then reads each section (a table, a
/// stream, or a table with an <c>/XRefStm</c>) and follows <c>/Prev</c> back to the first, newest entries winning. Loops in
/// the chain are detected, and damage past the newest section is repaired with warnings. A file whose newest section cannot
/// be read throws <see cref="PdfException"/>, for the caller to repair by scanning.
/// </summary>
internal sealed class XrefReader(PdfSource source, PdfReadOptions options, WarningLog log, IObjectResolver? references = null)
{
    /// <summary>Trailer keys that describe one section and are not merged into the file's trailer.</summary>
    private static readonly HashSet<string> SectionKeys = ["Prev", "XRefStm", "Type", "W", "Index", "Length", "Filter", "DecodeParms", "DL", "F", "FFilter", "FDecodeParms"];

    public XrefTable Read()
    {
        XrefTable table = new();
        HashSet<long> visited = [];
        long? offset = FindStartXref();
        bool newest = true;
        while (offset is long at)
        {
            if (!visited.Add(at))
            {
                log.Add(PdfWarningCode.BrokenXrefChain, $"/Prev points back to the section at {at}; the loop was cut.", at);
                break;
            }

            CheckSectionCount(table);
            Dictionary<int, XrefEntry> entries = [];
            XrefSection section;
            try
            {
                section = ReadSection(at, entries);
            }
            catch (PdfException e) when (table.Sections.Count > 0 && e is not PdfLimitException && e.Code is null)
            {
                log.Add(PdfWarningCode.BrokenXrefChain, $"The older cross-reference section at {at} cannot be read ({e.Message}); it was skipped.", at);
                break;
            }

            table.Sections.Add(section);
            if (!section.IsStream && section.Trailer.Get("XRefStm") is PdfInteger { Value: >= 0 } stream)
            {
                ReadHybridStream(stream.Value, entries, table, visited);
            }

            foreach ((int number, XrefEntry entry) in entries)
            {
                table.AddIfAbsent(number, entry);
            }

            if (table.Count > options.MaxObjects)
            {
                throw new PdfLimitException(nameof(PdfReadOptions.MaxObjects), $"The cross-reference lists more than {options.MaxObjects} objects.");
            }

            // An update's trailer holds every entry of the one before (7.5.6), so the newest trailer is the file's trailer and
            // a key it leaves out was removed. Only a missing /Root is taken from an older trailer, as other readers do.
            foreach ((PdfName key, PdfObject value) in section.Trailer.Entries)
            {
                if (!SectionKeys.Contains(key.Value) && !table.Trailer.ContainsKey(key) && (newest || key.Value == "Root"))
                {
                    table.Trailer.SetParsed(key, value is PdfArray or PdfDictionary ? Copy(value) : value);
                }
            }

            newest = false;
            offset = section.Trailer.Get("Prev") switch
            {
                PdfInteger { Value: >= 0 } prev => prev.Value,
                null => null,
                _ => Broken(at),
            };
        }

        return table;
    }

    private long? Broken(long at)
    {
        log.Add(PdfWarningCode.BrokenXrefChain, $"The section at {at} has a /Prev that is not an offset; older sections were not read.", at);
        return null;
    }

    private static PdfObject Copy(PdfObject value)
    {
        return value is PdfArray array ? array.Clone() : ((PdfDictionary)value).Clone();
    }

    private void CheckSectionCount(XrefTable table)
    {
        if (table.Sections.Count >= options.MaxXrefSections)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxXrefSections), $"The file has more than {options.MaxXrefSections} cross-reference sections.");
        }
    }

    /// <summary>Reads a hybrid file's stream: its entries fill in what the table leaves out or lists as free (7.5.8.4).</summary>
    private void ReadHybridStream(long offset, Dictionary<int, XrefEntry> entries, XrefTable table, HashSet<long> visited)
    {
        if (!visited.Add(offset))
        {
            log.Add(PdfWarningCode.BrokenXrefChain, $"/XRefStm points to the section at {offset}, which was already read.", offset);
            return;
        }

        CheckSectionCount(table);
        Dictionary<int, XrefEntry> streamEntries = [];
        try
        {
            table.Sections.Add(ReadStream(offset, streamEntries));
        }
        catch (PdfException e) when (e is not PdfLimitException && e.Code is null)
        {
            log.Add(PdfWarningCode.BrokenXrefChain, $"The /XRefStm stream at {offset} cannot be read ({e.Message}); it was skipped.", offset);
            return;
        }

        foreach ((int number, XrefEntry entry) in streamEntries)
        {
            if (!entries.TryGetValue(number, out XrefEntry existing) || existing.Kind == XrefEntryKind.Free)
            {
                entries[number] = entry;
            }
        }
    }

    /// <summary>The offset after the last <c>startxref</c>, looking in the last 1 KiB first and then the whole file.</summary>
    private long FindStartXref()
    {
        long at = SourceSearch.LastIndexOf(source, "startxref"u8, source.Length, Math.Max(0, source.Length - 1024));
        if (at < 0)
        {
            at = SourceSearch.LastIndexOf(source, "startxref"u8, source.Length);
            if (at < 0)
            {
                throw new PdfException("The file has no 'startxref'.");
            }

            log.Add(PdfWarningCode.MalformedXref, $"The last 'startxref' is {source.Length - at} bytes before the end of the file.", at);
        }

        // Objects after the last startxref mean an update whose end was cut off; its objects would be lost, so the caller
        // rebuilds instead.
        if (SourceSearch.IndexOf(source, " obj"u8, at) >= 0)
        {
            throw new PdfException("The file's last 'startxref' is followed by more objects; it looks truncated.", at, null);
        }

        Lexer lexer = new(new SourceReader(source, at + "startxref"u8.Length), log);
        Token token = lexer.Next();
        if (token.Kind != TokenKind.Integer || token.Integer < 0 || token.Integer >= source.Length)
        {
            throw new PdfException("'startxref' is not followed by an offset inside the file.", at, null);
        }

        return token.Integer;
    }

    private XrefSection ReadSection(long offset, Dictionary<int, XrefEntry> entries)
    {
        Lexer lexer = new(new SourceReader(source, offset), log);
        Token first = lexer.Next();
        if (lexer.IsKeyword(first, "xref"u8))
        {
            return ReadTable(lexer, offset, entries);
        }

        return ReadStream(offset, entries);
    }

    private XrefSection ReadTable(Lexer lexer, long offset, Dictionary<int, XrefEntry> entries)
    {
        bool firstSubsection = true;
        while (true)
        {
            Token token = lexer.Next();
            if (lexer.IsKeyword(token, "trailer"u8))
            {
                break;
            }

            Token countToken = lexer.Next();
            if (token.Kind != TokenKind.Integer || countToken.Kind != TokenKind.Integer || token.Integer is < 0 or > int.MaxValue || countToken.Integer < 0)
            {
                // Not a subsection header: look for the trailer.
                long trailer = SourceSearch.IndexOf(source, "trailer"u8, token.Start);
                if (trailer < 0)
                {
                    throw new PdfException("The cross-reference table has no 'trailer'.", offset, null);
                }

                log.Add(PdfWarningCode.MalformedXref, "The cross-reference table holds something other than subsections; it was read up to that point.", token.Start);
                lexer.Position = trailer + "trailer"u8.Length;
                break;
            }

            long start = token.Integer;
            for (long i = 0; i < countToken.Integer; i++)
            {
                long entryStart = lexer.Position;
                Token position = lexer.Next();
                Token generation = lexer.Next();
                Token type = lexer.Next();
                bool inUse = lexer.IsKeyword(type, "n"u8);
                if (position.Kind != TokenKind.Integer || generation.Kind != TokenKind.Integer || (!inUse && !lexer.IsKeyword(type, "f"u8)))
                {
                    log.Add(PdfWarningCode.MalformedXref, $"A subsection promised {countToken.Integer} entries but has {i}.", position.Start);
                    lexer.Position = entryStart;
                    break;
                }

                if (firstSubsection && i == 0 && start == 1 && !inUse && position.Integer == 0 && generation.Integer == 65535)
                {
                    // A common off-by-one: the free head of the list numbered 1 instead of 0.
                    log.Add(PdfWarningCode.MalformedXref, "The first subsection starts at 1 but holds object 0; it was renumbered from 0.", token.Start);
                    start = 0;
                }

                long number = start + i;
                if (number > int.MaxValue)
                {
                    break;
                }

                if (entries.Count >= options.MaxObjects)
                {
                    throw new PdfLimitException(nameof(PdfReadOptions.MaxObjects), $"The cross-reference lists more than {options.MaxObjects} objects.");
                }

                if (generation.Integer is < 0 or > 65535)
                {
                    log.Add(PdfWarningCode.MalformedXref, $"The entry for object {number} has the generation {generation.Integer}; it was skipped.", position.Start);
                }
                else if (inUse && position.Integer <= 0)
                {
                    log.Add(PdfWarningCode.MalformedXref, $"The entry for object {number} is in use at offset {position.Integer}; it was read as free.", position.Start);
                    entries[(int)number] = XrefEntry.FreeEntry((int)generation.Integer);
                }
                else
                {
                    // Within a section the last entry for a number wins, as in the file.
                    entries[(int)number] = inUse ? new XrefEntry(XrefEntryKind.InFile, position.Integer, (int)generation.Integer) : XrefEntry.FreeEntry((int)generation.Integer);
                }
            }

            firstSubsection = false;
        }

        ObjectParser parser = new(lexer, options, references);
        if (parser.ParseObject() is not PdfDictionary trailerDictionary)
        {
            throw new PdfException("The cross-reference table's trailer is not a dictionary.", offset, null);
        }

        return new XrefSection(offset, IsStream: false, trailerDictionary);
    }

    private XrefSection ReadStream(long offset, Dictionary<int, XrefEntry> entries)
    {
        ObjectParser parser = new(new Lexer(new SourceReader(source), log), options, references);
        IndirectObject found = parser.ParseIndirectObject(offset);
        if (found.Value is not PdfStream stream)
        {
            throw new PdfException("The cross-reference section is neither a table nor a stream.", offset, null);
        }

        PdfDictionary dictionary = stream.Dictionary;
        if (dictionary.GetName("Type")?.Value != "XRef")
        {
            if (dictionary.Get("W") is not PdfArray)
            {
                throw new PdfException("The cross-reference section is a stream that is not /Type /XRef.", offset, null);
            }

            log.Add(PdfWarningCode.MalformedXref, "A cross-reference stream has no /Type /XRef.", offset);
        }

        int[] widths = Widths(dictionary, offset);
        long size = dictionary.GetInt("Size") ?? -1;
        List<(long Start, long Count)> ranges = Ranges(dictionary, size, offset);
        int warningsBefore = log.Count;
        byte[] data = stream.GetDecodedBytes();
        if (log.ToArray().Skip(warningsBefore).Any(w => w.Code == PdfWarningCode.DamagedFilterData))
        {
            // Entries from damaged data would point anywhere: treat the section as unreadable, so the file is rebuilt.
            throw new PdfException("The cross-reference stream's data is damaged.", offset, null);
        }

        int row = widths[0] + widths[1] + widths[2];
        int at = 0;
        foreach ((long start, long count) in ranges)
        {
            for (long i = 0; i < count; i++)
            {
                if (at + row > data.Length)
                {
                    log.Add(PdfWarningCode.MalformedXref, "A cross-reference stream's data ends before its last entry.", offset);
                    return Self(found, offset, entries, dictionary);
                }

                long type = widths[0] == 0 ? 1 : Field(data, ref at, widths[0]);
                long second = Field(data, ref at, widths[1]);
                long third = Field(data, ref at, widths[2]);
                long number = start + i;
                if (number > int.MaxValue)
                {
                    break;
                }

                if (entries.Count >= options.MaxObjects)
                {
                    throw new PdfLimitException(nameof(PdfReadOptions.MaxObjects), $"The cross-reference lists more than {options.MaxObjects} objects.");
                }

                switch (type)
                {
                    case 0:
                        entries[(int)number] = XrefEntry.FreeEntry((int)Math.Min(third, 65535));
                        break;
                    case 1 when third <= 65535 && second > 0:
                        entries[(int)number] = new XrefEntry(XrefEntryKind.InFile, second, (int)third);
                        break;
                    case 2 when second is > 0 and <= int.MaxValue && third <= int.MaxValue:
                        entries[(int)number] = new XrefEntry(XrefEntryKind.InObjectStream, second, (int)third);
                        break;
                    case 1 or 2:
                        log.Add(PdfWarningCode.MalformedXref, $"The cross-reference stream entry for object {number} is out of range; it was skipped.", offset);
                        break;
                    default:
                        // Other types are reserved and stand for the null object (7.5.8.3).
                        break;
                }
            }
        }

        return Self(found, offset, entries, dictionary);
    }

    /// <summary>
    /// The stream is itself an object at the offset it was read from, whatever its own entries say (or leave out), as qpdf
    /// reads it.
    /// </summary>
    private static XrefSection Self(IndirectObject found, long offset, Dictionary<int, XrefEntry> entries, PdfDictionary dictionary)
    {
        entries[found.ObjectNumber] = new XrefEntry(XrefEntryKind.InFile, offset, found.Generation);
        return new XrefSection(offset, IsStream: true, dictionary);
    }


    private static long Field(byte[] data, ref int at, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[at++];
        }

        return value;
    }

    private static int[] Widths(PdfDictionary dictionary, long offset)
    {
        if (dictionary.Get("W") is not PdfArray { Count: 3 } w || Enumerable.Range(0, 3).Any(i => w.GetInt(i) is not (>= 0 and <= 8)))
        {
            throw new PdfException("A cross-reference stream has no valid /W.", offset, null);
        }

        int[] widths = [(int)w.GetInt(0)!, (int)w.GetInt(1)!, (int)w.GetInt(2)!];
        if (widths[1] == 0)
        {
            throw new PdfException("A cross-reference stream's /W gives no width to offsets.", offset, null);
        }

        return widths;
    }

    private List<(long Start, long Count)> Ranges(PdfDictionary dictionary, long size, long offset)
    {
        if (dictionary.Get("Index") is not PdfArray index)
        {
            if (size < 0)
            {
                throw new PdfException("A cross-reference stream has neither /Size nor /Index.", offset, null);
            }

            return [(0, size)];
        }

        List<(long, long)> ranges = [];
        for (int i = 0; i + 1 < index.Count; i += 2)
        {
            if (index.GetInt(i) is long start && index.GetInt(i + 1) is long count && start >= 0 && count >= 0)
            {
                ranges.Add((start, count));
            }
        }

        if (index.Count % 2 != 0 || ranges.Count * 2 != index.Count)
        {
            log.Add(PdfWarningCode.MalformedXref, "A cross-reference stream's /Index is malformed; its valid pairs were used.", offset);
        }

        return ranges;
    }
}

/// <summary>A resolver that passes references on to one set later, so objects read before the object table exists still resolve through it.</summary>
internal sealed class ForwardingResolver : IObjectResolver
{
    public IObjectResolver? Target { get; set; }

    /// <summary>The highest number seen before the target was set, as in the trailer.</summary>
    public int HighestSeen { get; private set; }

    public void Saw(int objectNumber)
    {
        if (Target is null)
        {
            HighestSeen = Math.Max(HighestSeen, objectNumber);
        }
        else
        {
            Target.Saw(objectNumber);
        }
    }

    public PdfObject Resolve(PdfReference reference)
    {
        return Target?.Resolve(reference) ?? PdfNull.Instance;
    }
}
