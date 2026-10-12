using System.Text;
using System.Text.RegularExpressions;

using Egap.Pdf.Tests.Parsing;
using Egap.Pdf.Trees;

namespace Egap.Pdf.Tests.Trees;

/// <summary>Name and number trees (7.9.6, 7.9.7): lookups, in-place edits, splits, limits and broken trees (Phase 2 task 2).</summary>
public sealed partial class TreeTests
{
    private static PdfString Key(string text)
    {
        return PdfString.FromBytes(Encoding.ASCII.GetBytes(text));
    }

    private static NameTree NewNameTree(PdfDocument pdf, PdfDictionary? root = null)
    {
        return new NameTree(root ?? [], pdf.Objects, pdf.Log);
    }

    /// <summary>
    /// Checks the tree is well formed the way the spec asks: the root has no /Limits, kids are references, every other node
    /// has /Limits equal to the least and greatest key below it, no node holds more than 128 items, and keys strictly
    /// increase. Returns the keys in order.
    /// </summary>
    private static List<string> CheckWellFormed(PdfDictionary root, string entriesKey = "Names")
    {
        Assert.Null(root["Limits"]);
        List<string> keys = [];
        Walk(root, isRoot: true);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        return keys;

        (string? Low, string? High) Walk(PdfDictionary node, bool isRoot)
        {
            List<string> below = [];
            if (node.Get<PdfArray>("Kids") is PdfArray kids)
            {
                Assert.Null(node[entriesKey]);
                Assert.InRange(kids.Count, isRoot ? 0 : 1, PdfTree<long>.MaxNodeSize);
                for (int i = 0; i < kids.Count; i++)
                {
                    Assert.IsType<PdfReference>(kids[i]);
                    (string? low, string? high) = Walk(kids.Get<PdfDictionary>(i)!, isRoot: false);
                    below.Add(low!);
                    below.Add(high!);
                }
            }
            else
            {
                PdfArray leaf = node.Get<PdfArray>(entriesKey)!;
                Assert.Equal(0, leaf.Count % 2);
                Assert.InRange(leaf.Count / 2, isRoot ? 0 : 1, PdfTree<long>.MaxNodeSize);
                for (int i = 0; i < leaf.Count; i += 2)
                {
                    string key = KeyText(leaf.Get(i));
                    keys.Add(key);
                    below.Add(key);
                }
            }

            if (!isRoot)
            {
                PdfArray limits = node.Get<PdfArray>("Limits")!;
                Assert.Equal(below[0], KeyText(limits.Get(0)));
                Assert.Equal(below[^1], KeyText(limits.Get(1)));
            }

            return below.Count == 0 ? (null, null) : (below[0], below[^1]);
        }
    }

    private static string KeyText(PdfObject key)
    {
        return key is PdfInteger number ? number.Value.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) : Encoding.ASCII.GetString(((PdfString)key).Bytes);
    }

    [Fact]
    public void LooksUpInAFlatTree()
    {
        using PdfDocument pdf = PdfDocument.Create();
        NameTree tree = NewNameTree(pdf, new PdfDictionary { ["Names"] = new PdfArray([Key("a"), 1, Key("c"), 3]) });

        Assert.True(tree.TryGet(Key("c"), out PdfObject value));
        Assert.Equal(new PdfInteger(3), value);
        Assert.False(tree.TryGet(Key("b"), out _));
        Assert.Equal(["a", "c"], tree.Entries().Select(e => KeyText(e.Key)));
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void LooksUpThroughKidsByTheirLimits()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 3 0 R >> >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Object(3, "<< /Kids [4 0 R 5 0 R] >>")
            .Object(4, "<< /Limits [(apple) (fig)] /Kids [6 0 R 7 0 R] >>")
            .Object(5, "<< /Limits [(grape) (pear)] /Names [(grape) 10 (pear) 11] >>")
            .Object(6, "<< /Limits [(apple) (cherry)] /Names [(apple) 1 (cherry) 2] >>")
            .Object(7, "<< /Limits [(date) (fig)] /Names [(date) 3 (fig) 4] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 8 /Root 1 0 R")).ToArray());
        NameTree tree = NewNameTree(pdf, pdf.Objects.Get(3) as PdfDictionary);

        foreach ((string key, long expected) in new[] { ("apple", 1L), ("cherry", 2L), ("date", 3L), ("fig", 4L), ("grape", 10L), ("pear", 11L) })
        {
            Assert.True(tree.TryGet(Key(key), out PdfObject value), key);
            Assert.Equal(new PdfInteger(expected), value);
        }

        Assert.False(tree.TryGet(Key("banana"), out _));
        Assert.False(tree.TryGet(Key("zebra"), out _));
        Assert.Equal(["apple", "cherry", "date", "fig", "grape", "pear"], CheckWellFormed(tree.Root));
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void KeysCompareAsBytes()
    {
        // Keys sort by their bytes, so a UTF-16 key (FE FF ...) sorts after every PDFDocEncoding one.
        using PdfDocument pdf = PdfDocument.Create();
        NameTree tree = NewNameTree(pdf);
        tree.Set(PdfString.FromText("Ω"), 1);
        tree.Set(PdfString.FromText("B"), 2);
        tree.Set(PdfString.FromText("a"), 3);

        Assert.Equal([(byte)'B', (byte)'a', 0xFE], tree.Entries().Select(e => e.Key.Bytes[0]));
    }

    [Fact]
    public void SetReplacesTheValueOfAnExistingKey()
    {
        using PdfDocument pdf = PdfDocument.Create();
        NameTree tree = NewNameTree(pdf);
        tree.Set(Key("a"), 1);
        tree.Set(Key("a"), 2);

        Assert.Single(tree.Entries());
        Assert.True(tree.TryGet(Key("a"), out PdfObject value));
        Assert.Equal(new PdfInteger(2), value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(5000)]
    public void InsertsSplitNodesAndKeepLimitsRight(int count)
    {
        using PdfDocument pdf = PdfDocument.Create();
        NameTree tree = NewNameTree(pdf);
        Random random = new(count);
        List<int> numbers = [.. Enumerable.Range(0, count).OrderBy(_ => random.Next())];
        foreach (int number in numbers)
        {
            tree.Set(Key($"k{number:D5}"), number);
        }

        List<string> keys = CheckWellFormed(tree.Root);
        Assert.Equal(count, keys.Count);
        Assert.Equal(count > PdfTree<long>.MaxNodeSize, tree.Root["Kids"] is not null);
        foreach (int number in numbers.Take(300))
        {
            Assert.True(tree.TryGet(Key($"k{number:D5}"), out PdfObject value));
            Assert.Equal(new PdfInteger(number), value);
        }

        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void InsertsInOrderAtEitherEnd()
    {
        using PdfDocument pdf = PdfDocument.Create();
        NameTree tree = NewNameTree(pdf);
        for (int i = 0; i < 1000; i++)
        {
            tree.Set(Key($"m{i:D4}"), i);
            tree.Set(Key($"a{999 - i:D4}"), i);
        }

        Assert.Equal(2000, CheckWellFormed(tree.Root).Count);
    }

    [Fact]
    public void RemovesEntriesAndEmptyNodes()
    {
        using PdfDocument pdf = PdfDocument.Create();
        NameTree tree = NewNameTree(pdf);
        Random random = new(7);
        List<int> numbers = [.. Enumerable.Range(0, 3000)];
        foreach (int number in numbers)
        {
            tree.Set(Key($"k{number:D5}"), number);
        }

        Assert.False(tree.Remove(Key("missing")));
        List<int> order = [.. numbers.OrderBy(_ => random.Next())];
        for (int i = 0; i < order.Count; i++)
        {
            Assert.True(tree.Remove(Key($"k{order[i]:D5}")));
            if (i % 500 == 0)
            {
                Assert.Equal(order.Count - i - 1, CheckWellFormed(tree.Root).Count);
                Assert.False(tree.TryGet(Key($"k{order[i]:D5}"), out _));
            }
        }

        Assert.Empty(CheckWellFormed(tree.Root));
        Assert.Null(tree.Root["Kids"]);
        Assert.Equal(new PdfArray(), tree.Root["Names"]);
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void NumberTreeFindsTheFloorEntry()
    {
        using PdfDocument pdf = PdfDocument.Create();
        NumberTree tree = new([], pdf.Objects, pdf.Log);
        for (long start = 0; start < 2000; start += 7)
        {
            tree.Set(start, start);
        }

        Assert.Equal(CheckWellFormed(tree.Root, "Nums").Count, tree.Entries().Count);
        Assert.True(tree.TryGetFloor(15, out KeyValuePair<long, PdfObject> entry));
        Assert.Equal(14, entry.Key);
        Assert.True(tree.TryGetFloor(14, out entry));
        Assert.Equal(14, entry.Key);
        Assert.True(tree.TryGetFloor(100_000, out entry));
        Assert.Equal(1995, entry.Key);
        Assert.False(tree.TryGetFloor(-1, out _));

        NumberTree late = new(new PdfDictionary { ["Nums"] = new PdfArray([new PdfInteger(5), PdfNull.Instance]) }, pdf.Objects, pdf.Log);
        Assert.False(late.TryGetFloor(4, out _));
    }

    [Fact]
    public void AnEditTouchesOnlyThePath()
    {
        // A three-level tree of 40 leaves: an incremental save after one insert writes the leaf and its two ancestors only.
        using PdfDocument built = PdfDocument.Create();
        PdfReference root = built.Objects.Add(new PdfDictionary());
        NameTree tree = new((PdfDictionary)built.Objects[root], built.Objects, built.Log);
        for (int i = 0; i < 40 * 64; i++)
        {
            tree.Set(Key($"k{i:D5}"), i);
        }

        built.Catalog["Names"] = new PdfDictionary { ["Dests"] = root };
        using MemoryStream original = new();
        built.Save(original);

        using PdfDocument pdf = PdfDocument.Open(original.ToArray());
        PdfReference reopened = (PdfReference)pdf.Catalog.Get<PdfDictionary>("Names")!["Dests"]!;
        NameTree edited = new((PdfDictionary)pdf.Objects[reopened], pdf.Objects, pdf.Log);
        edited.Set(Key("k01000x"), 1);
        using MemoryStream update = new();
        pdf.Save(update);

        string appended = Encoding.Latin1.GetString(update.ToArray().AsSpan((int)original.Length));
        int written = ObjectHeader().Count(appended);
        Assert.InRange(written, 1, 3);
        Assert.Equal(40 * 64 + 1, CheckWellFormed(edited.Root).Count);
        Assert.Empty(pdf.Warnings);
    }

    [Fact]
    public void ReadsABrokenTreeWithAWarning()
    {
        // Node 4 lists its own parent as a kid, node 5 has no /Limits and an odd array, node 6 is out of order with a name key.
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Object(3, "<< /Kids [4 0 R 5 0 R 6 0 R 8 0 R] >>")
            .Object(4, "<< /Limits [(a) (b)] /Kids [3 0 R 7 0 R] >>")
            .Object(5, "<< /Names [(m) 13 (n)] >>")
            .Object(6, "<< /Limits [(x) (z)] /Names [(z) 26 /x 24] >>")
            .Object(7, "<< /Limits [(a) (b)] /Names [(a) 1 (b) 2] >>")
            .Object(8, "<< /Limits [(q) (q)] /Names [(q) 17] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 9 /Root 1 0 R")).ToArray());
        NameTree tree = NewNameTree(pdf, pdf.Objects.Get(3) as PdfDictionary);

        Assert.Equal(["a", "b", "m", "q", "x", "z"], tree.Entries().Select(e => KeyText(e.Key)));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedTree);
        Assert.True(tree.TryGet(Key("m"), out PdfObject m));
        Assert.Equal(new PdfInteger(13), m);
        Assert.True(tree.TryGet(Key("x"), out PdfObject x));
        Assert.Equal(new PdfInteger(24), x);

        // Editing rebuilds it into a well-formed tree with every entry kept.
        tree.Set(Key("c"), 3);
        Assert.Equal(["a", "b", "c", "m", "q", "x", "z"], CheckWellFormed(tree.Root));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedTree && w.Message.Contains("rebuilt", StringComparison.Ordinal));
    }

    [Fact]
    public void ALoopIsReadOnce()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Object(3, "<< /Kids [4 0 R] >>")
            .Object(4, "<< /Limits [(a) (z)] /Kids [4 0 R 5 0 R] >>")
            .Object(5, "<< /Limits [(a) (a)] /Names [(a) 1] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 6 /Root 1 0 R")).ToArray());
        NameTree tree = NewNameTree(pdf, pdf.Objects.Get(3) as PdfDictionary);

        Assert.Single(tree.Entries());
        Assert.False(tree.TryGet(Key("b"), out _));
        Assert.True(tree.Remove(Key("a")));
        Assert.Empty(CheckWellFormed(tree.Root));
    }

    [Fact]
    public void RealKeysInANumberTreeAreTruncated()
    {
        using PdfDocument pdf = PdfDocument.Create();
        NumberTree tree = new(new PdfDictionary { ["Nums"] = new PdfArray([new PdfReal(2.0), Key("two"), new PdfInteger(1), Key("one")]) }, pdf.Objects, pdf.Log);

        Assert.Equal([1L, 2L], tree.Entries().Select(e => e.Key));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.MalformedTree);
        Assert.True(tree.TryGet(2, out PdfObject two));
        Assert.Equal(Key("two"), two);
    }

    [Fact]
    public void StrictModeThrowsOnABrokenTree()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(b) 1 (a) 2] >> >> >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 3 /Root 1 0 R")).ToArray(), new PdfReadOptions { Strict = true });
        NameTree tree = NewNameTree(pdf, pdf.Catalog.Get<PdfDictionary>("Names")!.Get<PdfDictionary>("Dests"));

        PdfException error = Assert.Throws<PdfException>(() => tree.Entries());
        Assert.Equal(PdfWarningCode.MalformedTree, error.Code);
    }

    [Fact]
    public void DirectValuesSurviveARebuild()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfDictionary root = new() { ["Names"] = new PdfArray([Key("b"), new PdfArray([1]), Key("a"), new PdfDictionary { ["D"] = 2 }]) };
        NameTree tree = NewNameTree(pdf, root);

        tree.Set(Key("c"), 3);

        Assert.Equal(["a", "b", "c"], CheckWellFormed(root));
        Assert.True(tree.TryGet(Key("a"), out PdfObject a));
        Assert.Equal(2, ((PdfDictionary)a).GetInt("D"));
    }

    [GeneratedRegex(@"\d+ 0 obj")]
    private static partial Regex ObjectHeader();
}
