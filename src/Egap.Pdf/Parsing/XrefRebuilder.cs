using Egap.Pdf.IO;

namespace Egap.Pdf.Parsing;

/// <summary>
/// Rebuilds the cross-reference of a file whose own cannot be read, by scanning for object headers (hub Q8): every object
/// found, the objects inside object streams, and a trailer from the file's trailers and cross-reference streams, with the
/// catalog found by its <c>/Type</c> when no trailer names one.
/// </summary>
internal static class XrefRebuilder
{
    private static readonly HashSet<string> TrailerKeys = ["Root", "Info", "ID", "Encrypt"];

    public static XrefTable Rebuild(PdfSource source, PdfReadOptions options, WarningLog log, IObjectResolver references)
    {
        ObjectScanner scanner = ObjectScanner.Scan(source, options);
        XrefTable table = new();
        foreach ((int number, (long offset, int generation)) in scanner.Objects)
        {
            table.Set(number, new XrefEntry(XrefEntryKind.InFile, offset, generation));
        }

        // Look inside each object once: object streams add their objects, cross-reference streams their trailer keys, and
        // the newest catalog is remembered. Warnings from this look are dropped; loading the objects reports them again.
        WarningLog quiet = new(strict: false);
        List<PdfDictionary> xrefStreams = [];
        int? catalog = null;
        foreach ((int number, (long offset, _)) in scanner.Objects.OrderBy(o => o.Value.Offset))
        {
            PdfObject value;
            try
            {
                value = new ObjectParser(new Lexer(new SourceReader(source), quiet), options, references).ParseIndirectObject(offset).Value;
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                continue;
            }

            PdfDictionary? dictionary = value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
            switch (dictionary?.GetName("Type")?.Value)
            {
                case "Catalog":
                    catalog = number;
                    break;
                case "XRef":
                    xrefStreams.Add(dictionary);
                    break;
                case "ObjStm" when value is PdfStream stream:
                    AddStoredObjects(stream, number, table, options, quiet);
                    break;
            }
        }

        // The newest trailer wins key by key: classic trailers, then cross-reference streams, newest first.
        foreach (long at in Enumerable.Reverse(scanner.Trailers))
        {
            Lexer lexer = new(new SourceReader(source, at + "trailer"u8.Length), quiet);
            if (new ObjectParser(lexer, options, references).ParseObject() is PdfDictionary trailer)
            {
                Merge(trailer, table.Trailer);
            }
        }

        foreach (PdfDictionary dictionary in Enumerable.Reverse(xrefStreams))
        {
            Merge(dictionary, table.Trailer);
        }

        if (catalog is int found && !RootIsCatalog(table, source, options, references))
        {
            table.Trailer.SetParsed("Root", new PdfReference(found, scanner.Objects[found].Generation, references));
        }

        log.Add(PdfWarningCode.XrefRebuilt, $"The cross-reference could not be used; it was rebuilt from the {table.Count} objects found by scanning the file.");
        return table;
    }

    /// <summary>
    /// For a cross-reference that read with damage: adds the objects a scan finds that it does not list at all, keeping
    /// every entry it does have.
    /// </summary>
    public static void FillGaps(PdfSource source, XrefTable table, PdfReadOptions options, WarningLog log)
    {
        ObjectScanner scanner = ObjectScanner.Scan(source, options);
        int added = 0;
        foreach ((int number, (long offset, int generation)) in scanner.Objects.OrderBy(o => o.Key))
        {
            if (table.AddIfAbsent(number, new XrefEntry(XrefEntryKind.InFile, offset, generation)))
            {
                added++;
            }
        }

        if (added > 0)
        {
            log.Add(PdfWarningCode.XrefRebuilt, $"The damaged cross-reference left out {added} objects found by scanning the file; they were added.");
        }
    }

    private static void AddStoredObjects(PdfStream stream, int streamNumber, XrefTable table, PdfReadOptions options, WarningLog quiet)
    {
        StoredObject?[] stored;
        try
        {
            stored = ObjectStreamReader.Read(stream, streamNumber, options, quiet, new ForwardingResolver());
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return;
        }

        for (int index = 0; index < stored.Length; index++)
        {
            // An object written directly in the file wins over one in an object stream.
            if (stored[index] is StoredObject found && (!table.TryGet(found.ObjectNumber, out XrefEntry existing) || existing.Kind != XrefEntryKind.InFile))
            {
                table.Set(found.ObjectNumber, new XrefEntry(XrefEntryKind.InObjectStream, streamNumber, index));
            }
        }
    }

    private static void Merge(PdfDictionary from, PdfDictionary into)
    {
        foreach ((PdfName key, PdfObject value) in from.Entries)
        {
            if (TrailerKeys.Contains(key.Value) && !into.ContainsKey(key))
            {
                into.SetParsed(key, value is PdfArray array ? array.Clone() : value is PdfDictionary dictionary ? dictionary.Clone() : value);
            }
        }
    }

    /// <summary>Whether the trailer's /Root points at an object found by the scan that is a catalog.</summary>
    private static bool RootIsCatalog(XrefTable table, PdfSource source, PdfReadOptions options, IObjectResolver references)
    {
        if (table.Trailer["Root"] is not PdfReference root || !table.TryGet(root.ObjectNumber, out XrefEntry entry) || entry.Kind != XrefEntryKind.InFile)
        {
            return false;
        }

        try
        {
            PdfObject value = new ObjectParser(new Lexer(new SourceReader(source), new WarningLog(strict: false)), options, references).ParseIndirectObject(entry.Offset).Value;
            return value is PdfDictionary dictionary && (dictionary.GetName("Type")?.Value == "Catalog" || dictionary.ContainsKey("Pages"));
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return false;
        }
    }
}
