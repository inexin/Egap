using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf.Writing;

/// <summary>
/// Writes a document whole and linearized (ISO 32000-1 Annex F, Phase 5 design Q15), laid out as qpdf lays it out so its
/// check agrees: the linearization dictionary and the first page's cross-reference section first, then the catalog with
/// the objects needed to open the document, the primary hint stream, the first page and everything it uses, each other
/// page with its private objects, the objects several other pages share, the rest, and the main cross-reference section.
/// Objects are numbered in that order, the first page's part after the rest, as qpdf numbers them. The hint stream holds
/// the page offset and shared object hint tables, and the outline hint table when the document opens on its outline.
/// Objects are written on their own with classic cross-reference tables; encryption is not combined with linearization.
/// </summary>
internal sealed class LinearizedWriter
{
    private static readonly HashSet<string> OwnTrailerKeys = ["Size", "Prev", "XRefStm", "ID", "Encrypt", "Type", "W", "Index", "Length", "Filter", "DecodeParms", "DL"];

    /// <summary>The catalog entries a viewer needs to open the document (Annex F.3, part 4), as qpdf takes them.</summary>
    private static readonly string[] OpenDocumentKeys = ["ViewerPreferences", "PageMode", "Threads", "OpenAction", "AcroForm"];

    private readonly PdfDocument document;
    private readonly PdfSaveOptions options;

    /// <summary>Every object to write by its key: the source object's number and generation, or a negative id for a made one.</summary>
    private readonly Dictionary<(int, int), Item> items = [];

    /// <summary>The pages by their dictionaries, for the attributes they inherit.</summary>
    private readonly Dictionary<PdfDictionary, PdfPage> pagesByDictionary = new(ReferenceEqualityComparer.Instance);

    private LinearizedWriter(PdfDocument document, PdfSaveOptions options)
    {
        this.document = document;
        this.options = options;
    }

    private sealed class Item((int, int) key, PdfObject value)
    {
        public (int, int) Key { get; } = key;

        public PdfObject Value { get; } = value;

        public int Number { get; set; }

        public byte[] Bytes { get; set; } = [];

        public long Offset { get; set; }
    }

    public static void Write(PdfDocument document, Stream output, PdfSaveOptions options)
    {
        if (document.IsEncrypted && options.Encryption.Kind != PdfEncryptionKind.None || options.Encryption.Kind == PdfEncryptionKind.Aes256)
        {
            throw new PdfException("Linearization is not combined with encryption: save with PdfEncryption.None to linearize.");
        }

        new LinearizedWriter(document, options).Emit(output);
    }

    private Item ItemFor(PdfReference reference)
    {
        (int, int) key = (reference.ObjectNumber, reference.Generation);
        if (!items.TryGetValue(key, out Item? item))
        {
            item = new Item(key, document.Objects[reference]);
            items[key] = item;
        }

        return item;
    }

    /// <summary>
    /// The objects reachable from a value, breadth first, never through a page other than <paramref name="self"/>, a page
    /// tree node or /Parent (qpdf's traversal: an annotation's /P leads to its page, which ends the walk anyway).
    /// </summary>
    private List<Item> Reach(PdfObject start, PdfDictionary? self, HashSet<PdfDictionary> pageDictionaries, HashSet<(int, int)>? visited = null, bool stopAtPageTree = true, bool skipThumbnails = false)
    {
        List<Item> found = [];
        visited ??= [];
        Queue<PdfObject> queue = new([start]);
        while (queue.TryDequeue(out PdfObject? value))
        {
            foreach (PdfReference reference in References(value, skipThumbnails))
            {
                (int, int) key = (reference.ObjectNumber, reference.Generation);
                if (visited.Contains(key))
                {
                    continue;
                }

                PdfObject target = document.Objects[reference];
                PdfDictionary? dictionary = target switch
                {
                    PdfStream s => s.Dictionary,
                    PdfDictionary d => d,
                    _ => null,
                };
                if (target is PdfNull || (dictionary is not null && ((pageDictionaries.Contains(dictionary) && !ReferenceEquals(dictionary, self)) || (stopAtPageTree && dictionary.GetName("Type")?.Value == "Pages"))))
                {
                    continue;
                }

                visited.Add(key);
                found.Add(ItemFor(reference));
                queue.Enqueue(target);
            }
        }

        return found;
    }

    /// <summary>The references a value holds directly (its own containers searched), not following them.</summary>
    private static IEnumerable<PdfReference> References(PdfObject value, bool skipThumbnails)
    {
        Stack<(PdfObject Value, int Depth)> stack = new([(value, 0)]);
        while (stack.TryPop(out (PdfObject Value, int Depth) next))
        {
            switch (next.Value)
            {
                case PdfReference reference:
                    yield return reference;
                    break;
                case PdfStream stream when next.Depth < 64:
                    // The stream's length is written from its data, so an indirect /Length object is not needed.
                    foreach ((PdfName key, PdfObject item) in stream.Dictionary.Entries.Reverse())
                    {
                        if (key.Value != "Length")
                        {
                            stack.Push((item, next.Depth + 1));
                        }
                    }

                    break;
                case PdfDictionary dictionary when next.Depth < 64:
                    foreach ((PdfName key, PdfObject item) in dictionary.Entries.Reverse())
                    {
                        if ((key.Value != "Parent" || item is not PdfReference) && !(skipThumbnails && key.Value == "Thumb"))
                        {
                            stack.Push((item, next.Depth + 1));
                        }
                    }

                    break;
                case PdfArray array when next.Depth < 64:
                    for (int i = array.Count - 1; i >= 0; i--)
                    {
                        stack.Push((array[i], next.Depth + 1));
                    }

                    break;
            }
        }
    }

    private void Emit(Stream output)
    {
        // The catalog and Info as objects of their own, even when the trailer holds them directly.
        Item catalog = document.Trailer["Root"] is PdfReference root ? ItemFor(root) : Made(-1, document.Catalog);
        Item? info = document.Trailer["Info"] switch
        {
            PdfReference reference when document.Objects[reference] is PdfDictionary => ItemFor(reference),
            PdfDictionary direct => Made(-2, direct),
            _ => null,
        };

        int pageCount = document.Pages.Count;
        if (pageCount == 0)
        {
            throw new PdfException("A document without pages cannot be linearized: the hint tables describe pages.");
        }

        List<PdfPage> pages = [.. Enumerable.Range(0, pageCount).Select(i => document.Pages[i])];
        if (pages.Any(p => p.Reference is null))
        {
            throw new PdfException("Every page must be an indirect object to linearize.");
        }

        HashSet<PdfDictionary> pageDictionaries = new(pages.Select(p => p.Dictionary), ReferenceEqualityComparer.Instance);
        foreach (PdfPage page in pages)
        {
            pagesByDictionary.TryAdd(page.Dictionary, page);
        }

        // Who uses each object, as qpdf counts it: pages (by index), the keys opening the document needs, the outline,
        // and any other catalog or trailer key.
        Dictionary<(int, int), Use> uses = [];
        Use UseOf(Item item)
        {
            if (!uses.TryGetValue(item.Key, out Use? use))
            {
                use = new Use();
                uses[item.Key] = use;
            }

            return use;
        }

        List<List<Item>> pageObjects = [];
        for (int i = 0; i < pageCount; i++)
        {
            Item pageItem = ItemFor(pages[i].Reference!);
            HashSet<(int, int)> visited = [pageItem.Key];
            List<Item> reached = [pageItem, .. Reach(Pushed(pages[i].Dictionary), pages[i].Dictionary, pageDictionaries, visited, skipThumbnails: true)];
            pageObjects.Add(reached);
            foreach (Item item in reached)
            {
                UseOf(item).Pages.Add(i);
            }

            // A page's thumbnail is used by the thumbnail, not the page (qpdf keeps them apart).
            if (pages[i].Dictionary["Thumb"] is PdfObject thumbnail)
            {
                foreach (Item item in Reach(thumbnail, null, pageDictionaries, []))
                {
                    UseOf(item).Thumbnails++;
                }
            }
        }

        PdfDictionary catalogDictionary = (PdfDictionary)catalog.Value;
        bool openOnOutline = catalogDictionary.GetName("PageMode")?.Value == "UseOutlines";
        List<Item> rootOrder = [];
        foreach ((PdfName key, PdfObject value) in catalogDictionary.Entries)
        {
            if (key.Value is "Pages" or "Type")
            {
                continue;
            }

            foreach (Item item in Reach(value, null, pageDictionaries, [catalog.Key]))
            {
                Use use = UseOf(item);
                if (OpenDocumentKeys.Contains(key.Value))
                {
                    use.OpenDocument = true;
                }
                else if (key.Value == "Outlines")
                {
                    use.Outline = true;
                }
                else
                {
                    use.Others++;
                }

                rootOrder.Add(item);
            }
        }

        foreach ((PdfName key, PdfObject value) in document.Trailer.Entries)
        {
            if (OwnTrailerKeys.Contains(key.Value) || key.Value == "Root")
            {
                continue;
            }

            List<Item> reached = info is not null && key.Value == "Info" ? [info, .. Reach(info.Value, null, pageDictionaries, [info.Key])] : Reach(value, null, pageDictionaries, []);
            foreach (Item item in reached)
            {
                UseOf(item).Others++;
                rootOrder.Add(item);
            }
        }

        Kind KindOf(Item item)
        {
            if (item == catalog)
            {
                return Kind.Root;
            }

            Use use = UseOf(item);
            int otherPages = use.Pages.Count(p => p != 0);
            bool firstPage = use.Pages.Contains(0);
            return use.Outline ? Kind.Outline
                : use.OpenDocument ? Kind.OpenDocument
                : firstPage ? Kind.FirstPage
                : otherPages == 1 && use.Others == 0 && use.Thumbnails == 0 ? Kind.OtherPagePrivate
                : otherPages > 1 ? Kind.OtherPageShared
                : Kind.Other;
        }

        HashSet<(int, int)> placed = [catalog.Key];
        List<Item> part4 = [catalog, .. rootOrder.Where(i => KindOf(i) == Kind.OpenDocument && placed.Add(i.Key))];
        List<Item> part6 = pageCount > 0 ? [.. pageObjects[0].Where(i => KindOf(i) == Kind.FirstPage && placed.Add(i.Key))] : [];
        // The outline goes in the first page's part when the document opens on it, else first in part 9; either way it
        // has its hint table, as qpdf writes and checks it.
        List<Item> outlineObjects = [.. rootOrder.Where(i => KindOf(i) == Kind.Outline && placed.Add(i.Key))];
        List<Item> outline = openOnOutline ? outlineObjects : [];
        List<List<Item>> part7 = [];
        for (int i = 1; i < pageCount; i++)
        {
            Item pageItem = pageObjects[i][0];
            placed.Add(pageItem.Key);
            part7.Add([pageItem, .. pageObjects[i].Skip(1).Where(o => KindOf(o) == Kind.OtherPagePrivate && placed.Add(o.Key))]);
        }

        List<Item> part8 = [.. pageObjects.Skip(1).SelectMany(o => o).Where(o => KindOf(o) == Kind.OtherPageShared && placed.Add(o.Key))];

        // Part 9: the rest: the page tree, the outline when the document does not open on it, and whatever else the
        // catalog and trailer reach, page objects of other kinds included.
        List<Item> part9 = [.. openOnOutline ? [] : outlineObjects, .. rootOrder.Where(i => placed.Add(i.Key))];
        part9.AddRange(pageObjects.SelectMany(o => o).Where(o => placed.Add(o.Key)));
        HashSet<(int, int)> otherVisited = [.. placed];
        foreach (Item item in Reach(catalogDictionary, null, [], otherVisited, stopAtPageTree: false))
        {
            if (placed.Add(item.Key))
            {
                part9.Add(item);
            }
        }

        // Numbers: the second half (parts 7 to 9) from 1, then the linearization dictionary, part 4, the hint stream and part 6.
        List<Item> secondHalf = [.. part7.SelectMany(s => s), .. part8, .. part9];
        int number = 1;
        foreach (Item item in secondHalf)
        {
            item.Number = number++;
        }

        int linearizationNumber = number++;
        foreach (Item item in part4)
        {
            item.Number = number++;
        }

        int hintNumber = number++;
        foreach (Item item in part6.Concat(outline))
        {
            item.Number = number++;
        }

        int size = number;
        foreach (Item item in items.Values.Where(i => i.Number > 0))
        {
            item.Bytes = Serialize(item);
        }

        // Layout. Values filled in later are written at fixed widths, so the positions hold.
        string version = document.WriterVersion;
        byte[] header = [.. Encoding.ASCII.GetBytes($"%PDF-{version}\n"), (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];
        int firstCount = size - linearizationNumber;
        int linearizationLength = Linearization(linearizationNumber, 0, 0, 0, 0, 0, 0, 0).Length;
        long firstXref = header.Length + linearizationLength;
        byte[] firstXrefHead = Encoding.ASCII.GetBytes($"xref\n{linearizationNumber} {firstCount}\n");
        string id = FileId();
        int firstTrailerLength = FirstTrailer(size, catalog, info, id, 0).Length;
        long position = firstXref + firstXrefHead.Length + (20L * firstCount) + firstTrailerLength;
        foreach (Item item in part4)
        {
            item.Offset = position;
            position += item.Bytes.Length;
        }

        long hintOffset = position;

        // Part 6 and on are placed after a hint stream whose length depends only on lengths and counts, so it is made from
        // provisional offsets first and its length taken from that.
        byte[] hint = HintStream(hintNumber, part6, outline, outlineObjects, part7, part8, pageObjects, 0);
        LayOut(hintOffset + hint.Length);
        hint = HintStream(hintNumber, part6, outline, outlineObjects, part7, part8, pageObjects, hint.Length);

        void LayOut(long start)
        {
            long at = start;
            foreach (Item item in part6.Concat(outline).Concat(secondHalf))
            {
                item.Offset = at;
                at += item.Bytes.Length;
            }

            position = at;
        }

        long endOfFirstPage = part6.Concat(outline).LastOrDefault() is Item last ? last.Offset + last.Bytes.Length : hintOffset + hint.Length;
        long mainXref = position;
        byte[] mainXrefHead = Encoding.ASCII.GetBytes($"xref\n0 {linearizationNumber}\n");
        long firstMainEntry = mainXref + mainXrefHead.Length - 1;
        byte[] mainTrailer = Encoding.ASCII.GetBytes($"trailer\n<< /Size {linearizationNumber} /ID {id} >>\nstartxref\n{firstXref}\n%%EOF\n");
        long fileLength = mainXref + mainXrefHead.Length + (20L * linearizationNumber) + mainTrailer.Length;
        Item? firstPage = pageCount > 0 ? part6[0] : null;

        Put(header);
        Put(Linearization(linearizationNumber, fileLength, hintOffset, hint.Length, firstPage?.Number ?? 0, endOfFirstPage, pageCount, firstMainEntry));
        Put(firstXrefHead);
        Put(Entry(header.Length, 0, 'n'));
        foreach (Item item in part4)
        {
            Put(Entry(item.Offset, 0, 'n'));
        }

        Put(Entry(hintOffset, 0, 'n'));
        foreach (Item item in part6.Concat(outline))
        {
            Put(Entry(item.Offset, 0, 'n'));
        }

        Put(FirstTrailer(size, catalog, info, id, mainXref));
        foreach (Item item in part4)
        {
            Put(item.Bytes);
        }

        Put(hint);
        foreach (Item item in part6.Concat(outline).Concat(secondHalf))
        {
            Put(item.Bytes);
        }

        Put(mainXrefHead);
        Put(Entry(0, 65535, 'f'));
        foreach (Item item in secondHalf)
        {
            Put(Entry(item.Offset, 0, 'n'));
        }

        Put(mainTrailer);

        void Put(byte[] bytes)
        {
            output.Write(bytes);
        }
    }

    /// <summary>What uses an object, as qpdf classifies objects for linearization.</summary>
    private sealed class Use
    {
        public HashSet<int> Pages { get; } = [];

        public bool OpenDocument { get; set; }

        public bool Outline { get; set; }

        public int Others { get; set; }

        public int Thumbnails { get; set; }
    }

    private enum Kind
    {
        Root,
        Outline,
        OpenDocument,
        FirstPage,
        OtherPagePrivate,
        OtherPageShared,
        Other,
    }

    private Item Made(int id, PdfDictionary value)
    {
        Item item = new((id, 0), value);
        items[item.Key] = item;
        return item;
    }

    private byte[] Serialize(Item item)
    {
        ByteWriter buffer = new();
        buffer.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{item.Number} 0 obj\n"));
        ObjectWriter.Write(Pushed(item.Value), buffer, Remap);
        buffer.WriteAscii("\nendobj\n");
        return buffer.ToArray();
    }

    /// <summary>
    /// A page with the attributes it inherits written on it, and a page tree node without them: a linearized file keeps
    /// nothing inheritable above the pages (Annex F, as qpdf checks), so a viewer showing one page needs no node.
    /// </summary>
    private PdfObject Pushed(PdfObject value)
    {
        if (value is not PdfDictionary dictionary || value is PdfStream)
        {
            return value;
        }

        string? type = dictionary.GetName("Type")?.Value;
        if (type == "Pages" || (type is null && dictionary.ContainsKey("Kids")))
        {
            PdfDictionary node = dictionary.Clone();
            foreach (string key in Inheritable)
            {
                node.Remove(key);
            }

            return node;
        }

        if (pagesByDictionary.TryGetValue(dictionary, out PdfPage? page) && Inheritable.Any(k => dictionary.Get(k) is null))
        {
            PdfDictionary copy = dictionary.Clone();
            foreach (string key in Inheritable)
            {
                if (copy.Get(key) is null && page.InheritedValue(key) is PdfObject inherited)
                {
                    copy[key] = inherited switch
                    {
                        PdfArray array => array.Clone(),
                        PdfStream => inherited,
                        PdfDictionary d => d.Clone(),
                        _ => inherited,
                    };
                }
            }

            return copy;
        }

        return value;
    }

    private static readonly string[] Inheritable = ["Resources", "MediaBox", "CropBox", "Rotate"];

    private PdfReference? Remap(PdfReference reference)
    {
        return items.TryGetValue((reference.ObjectNumber, reference.Generation), out Item? item) && item.Number > 0 ? new PdfReference(item.Number, 0) : null;
    }

    /// <summary>The linearization parameter dictionary (F.2), its numbers padded so the object keeps one length.</summary>
    private static byte[] Linearization(int number, long length, long hintOffset, long hintLength, int firstPage, long endOfFirstPage, int pages, long mainXref)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{number} 0 obj\n<< /Linearized 1 /L {length,10} /H [ {hintOffset,10} {hintLength,10} ] /O {firstPage,10} /E {endOfFirstPage,10} /N {pages,10} /T {mainXref,10} >>\nendobj\n");
        return Encoding.ASCII.GetBytes(text);
    }

    private static byte[] Entry(long offset, int generation, char kind)
    {
        return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} {generation:D5} {kind}\r\n"));
    }

    private static byte[] FirstTrailer(int size, Item catalog, Item? info, string id, long mainXref)
    {
        string infoEntry = info is null ? "" : $" /Info {info.Number} 0 R";
        return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {size} /Root {catalog.Number} 0 R{infoEntry} /ID {id} /Prev {mainXref,10} >>\nstartxref\n0\n%%EOF\n"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "The file ID is an identifier, not security; the spec suggests MD5 (14.4).")]
    private string FileId()
    {
        byte[] second = options.Deterministic ? MD5.HashData(Encoding.ASCII.GetBytes(document.Version + document.Pages.Count)) : MD5.HashData(Guid.NewGuid().ToByteArray());
        PdfArray pair = RewriteWriter.FileId(document, null, second);
        return $"[<{Convert.ToHexString(pair.Get<PdfString>(0)!.Bytes).ToLowerInvariant()}><{Convert.ToHexString(pair.Get<PdfString>(1)!.Bytes).ToLowerInvariant()}>]";
    }

    /// <summary>
    /// The primary hint stream (F.4): the page offset hint table, the shared object hint table at <c>/S</c>, and the outline
    /// hint table at <c>/O</c> when the outline is in the first page's part.
    /// </summary>
    /// <remarks>Locations in the tables are given as if the hint stream were not there (F.4): <paramref name="hintLength"/> is taken off.</remarks>
    private static byte[] HintStream(int number, List<Item> part6, List<Item> outline, List<Item> outlineGroup, List<List<Item>> part7, List<Item> part8, List<List<Item>> pageObjects, long hintLength)
    {
        int pageCount = pageObjects.Count;

        // Shared object table entries: each object of the first page's part (outline included), then each of part 8.
        List<Item> firstPart = [.. part6, .. outline];
        List<Item> shared = [.. firstPart, .. part8];
        Dictionary<(int, int), int> sharedIndex = [];
        for (int i = 0; i < shared.Count; i++)
        {
            sharedIndex[shared[i].Key] = i;
        }

        // Per page: its objects, where it starts, its length and the shared objects it uses.
        List<(int Objects, long Offset, long Length, List<int> Shared)> perPage = [];
        for (int i = 0; i < pageCount; i++)
        {
            List<Item> section = i == 0 ? firstPart : part7[i - 1];
            long offset = section.Count > 0 ? section[0].Offset - hintLength : 0;
            long length = section.Sum(o => (long)o.Bytes.Length);
            List<int> uses = i == 0 ? [] : [.. pageObjects[i].Where(o => sharedIndex.ContainsKey(o.Key)).Select(o => sharedIndex[o.Key])];
            perPage.Add((section.Count, offset, length, uses));
        }

        BitWriter bits = new();
        if (pageCount > 0)
        {
            int minObjects = perPage.Min(p => p.Objects);
            long minLength = perPage.Min(p => p.Length);
            int objectBits = Bits(perPage.Max(p => p.Objects) - minObjects);
            int lengthBits = Bits(perPage.Max(p => p.Length) - minLength);
            int sharedCountBits = Bits(perPage.Max(p => p.Shared.Count));
            int sharedIdBits = Bits(perPage.SelectMany(p => p.Shared).DefaultIfEmpty(0).Max());
            bits.Write(minObjects, 32);
            bits.Write(perPage[0].Offset, 32);
            bits.Write(objectBits, 16);
            bits.Write(minLength, 32);
            bits.Write(lengthBits, 16);
            bits.Write(0, 32);
            bits.Write(0, 16);
            bits.Write(minLength, 32);
            bits.Write(lengthBits, 16);
            bits.Write(sharedCountBits, 16);
            bits.Write(sharedIdBits, 16);
            bits.Write(0, 16);
            bits.Write(4, 16);
            Column(p => p.Objects - minObjects, objectBits);
            Column(p => p.Length - minLength, lengthBits);
            Column(p => p.Shared.Count, sharedCountBits);
            foreach ((_, _, _, List<int> uses) in perPage)
            {
                foreach (int use in uses)
                {
                    bits.Write(use, sharedIdBits);
                }
            }

            bits.Flush();

            // Numerators take no bits; content offsets are 0 bits wide; content lengths are the page lengths.
            Column(_ => 0, 0);
            Column(p => p.Length - minLength, lengthBits);

            void Column(Func<(int Objects, long Offset, long Length, List<int> Shared), long> value, int width)
            {
                foreach ((int, long, long, List<int>) page in perPage)
                {
                    bits.Write(value(page), width);
                }

                bits.Flush();
            }
        }

        int sharedOffset = bits.Length;
        long minGroup = shared.Count == 0 ? 0 : shared.Min(s => (long)s.Bytes.Length);
        int groupBits = shared.Count == 0 ? 0 : Bits(shared.Max(s => (long)s.Bytes.Length) - minGroup);
        bits.Write(part8.Count > 0 ? part8[0].Number : 0, 32);
        bits.Write(part8.Count > 0 ? part8[0].Offset - hintLength : 0, 32);
        bits.Write(firstPart.Count, 32);
        bits.Write(shared.Count, 32);
        bits.Write(0, 16);
        bits.Write(minGroup, 32);
        bits.Write(groupBits, 16);
        foreach (Item item in shared)
        {
            bits.Write(item.Bytes.Length - minGroup, groupBits);
        }

        bits.Flush();
        foreach (Item _ in shared)
        {
            bits.Write(0, 1);
        }

        bits.Flush();

        int? outlineOffset = null;
        if (outlineGroup.Count > 0)
        {
            outlineOffset = bits.Length;
            bits.Write(outlineGroup[0].Number, 32);
            bits.Write(outlineGroup[0].Offset - hintLength, 32);
            bits.Write(outlineGroup.Count, 32);
            bits.Write(outlineGroup[^1].Offset + outlineGroup[^1].Bytes.Length - outlineGroup[0].Offset, 32);
            bits.Flush();
        }

        byte[] data = bits.ToArray();
        string dictionary = outlineOffset is int o
            ? $"<< /S {sharedOffset} /O {o} /Length {data.Length} >>"
            : $"<< /S {sharedOffset} /Length {data.Length} >>";
        ByteWriter buffer = new();
        buffer.WriteAscii(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n{dictionary}\nstream\n"));
        buffer.Write(data);
        buffer.WriteAscii("\nendstream\nendobj\n");
        return buffer.ToArray();
    }

    /// <summary>The bits a value needs: 0 for 0.</summary>
    private static int Bits(long value)
    {
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    /// <summary>Writes values most significant bit first, a table at a time.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int current;
        private int used;

        /// <summary>Bytes written so far, a partial byte not counted.</summary>
        public int Length => bytes.Count;

        public void Write(long value, int width)
        {
            for (int bit = width - 1; bit >= 0; bit--)
            {
                current = (current << 1) | (int)((value >> bit) & 1);
                if (++used == 8)
                {
                    bytes.Add((byte)current);
                    current = 0;
                    used = 0;
                }
            }
        }

        /// <summary>Pads to a byte boundary.</summary>
        public void Flush()
        {
            if (used > 0)
            {
                bytes.Add((byte)(current << (8 - used)));
                current = 0;
                used = 0;
            }
        }

        public byte[] ToArray()
        {
            Flush();
            return [.. bytes];
        }
    }
}
