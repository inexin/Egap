using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Tests.Objects;

public sealed class ObjectTests
{
    private static string Write(PdfObject value)
    {
        return Encoding.Latin1.GetString(ObjectWriter.ToBytes(value));
    }

    private sealed class Owner : IObjectOwner
    {
        public int Changes { get; private set; }

        public void OnChanged()
        {
            Changes++;
        }
    }

    [Fact]
    public void WritesEveryObjectType()
    {
        PdfDictionary dictionary = new()
        {
            ["Type"] = PdfName.Get("Page"),
            ["Count"] = 3,
            ["Scale"] = 1.5,
            ["Ok"] = true,
            ["Nothing"] = PdfNull.Instance,
            ["Kids"] = new PdfArray([new PdfReference(4, 0), PdfString.FromBytes("a(b)\\"u8), PdfString.FromBytes([0, 255], hex: true)]),
            ["Inner"] = new PdfDictionary(),
            ["Empty"] = new PdfArray(),
        };

        Assert.Equal(
            "<< /Type /Page /Count 3 /Scale 1.5 /Ok true /Nothing null /Kids [4 0 R (a\\(b\\)\\\\) <00FF>] /Inner << >> /Empty [] >>",
            Write(dictionary));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e-7, "0.0000001")]
    [InlineData(-1.25e-10, "-0.000000000125")]
    [InlineData(1e21, "1000000000000000000000.0")]
    [InlineData(123456.789, "123456.789")]
    public void FormatsRealsWithoutExponents(double value, string expected)
    {
        Assert.Equal(expected, ObjectWriter.FormatReal(value));
        Assert.Equal(value, double.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RealKeepsItsParsedText()
    {
        Assert.Equal("1.50", Write(new PdfReal(1.5, "1.50"u8.ToArray())));
        Assert.Equal("1.5", Write(new PdfReal(1.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReal(double.NaN));
    }

    [Fact]
    public void IntegerAndRealStayDistinct()
    {
        Assert.Equal("1", Write(new PdfInteger(1)));
        Assert.Equal("1.0", Write(new PdfReal(1)));
        Assert.NotEqual<PdfObject>(new PdfInteger(1), new PdfReal(1));
    }

    [Theory]
    [InlineData("Type", "/Type")]
    [InlineData("lime Green", "/lime#20Green")]
    [InlineData("a#b", "/a#23b")]
    [InlineData("p(q)", "/p#28q#29")]
    [InlineData("é", "/#C3#A9")]
    [InlineData("", "/")]
    public void EscapesNames(string value, string expected)
    {
        string written = Write(PdfName.Get(value));

        Assert.Equal(expected, written);
        Lexer lexer = new(new SourceReader(PdfSource.FromMemory(Encoding.Latin1.GetBytes(written))), WarningLog.StrictLog);
        Assert.Equal(TokenKind.Name, lexer.Next().Kind);
        Assert.Equal(value, PdfName.Get(lexer.Bytes).Value);
    }

    [Fact]
    public void NamesAreInternedAndCompareByBytes()
    {
        Assert.Same(PdfName.Get("Type"), PdfName.Get("Type"));
        Assert.Same(PdfName.Get("Type"), PdfName.Get("Type"u8));
        Assert.True(PdfName.Get("A") == "A");
        Assert.True(PdfName.Get("A") != PdfName.Get("B"));
        Assert.Equal(PdfName.Get([0xFF]), PdfName.Get([0xFF]));
        Assert.Equal("ÿ", PdfName.Get([0xFF]).Value);
    }

    [Fact]
    public void StringsRoundTripThroughTheLexer()
    {
        byte[] all = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            all[i] = (byte)i;
        }

        foreach (bool hex in new[] { false, true })
        {
            byte[] written = ObjectWriter.ToBytes(PdfString.FromBytes(all, hex));
            Lexer lexer = new(new SourceReader(PdfSource.FromMemory(written)), WarningLog.StrictLog);
            Assert.Equal(hex ? TokenKind.HexString : TokenKind.LiteralString, lexer.Next().Kind);
            Assert.Equal(all, lexer.Bytes.ToArray());
        }
    }

    [Theory]
    [InlineData("Plain text", false)]
    [InlineData("été € • ﬁ", false)]
    [InlineData("Ω Greek", true)]
    [InlineData("\U0001F600 emoji", true)]
    public void TextStringsRoundTrip(string text, bool utf16)
    {
        PdfString value = PdfString.FromText(text);

        Assert.Equal(text, value.ToText());
        Assert.Equal(utf16, value.Bytes is [0xFE, 0xFF, ..]);
    }

    [Fact]
    public void DecodesPdfDocEncoding()
    {
        PdfString value = PdfString.FromBytes([0x18, 0x80, 0x8A, 0x93, 0x9F, 0xA0, 0xAD, 0xE9, 0x41]);

        Assert.Equal("˘•−ﬁ�€�éA", value.ToText());
    }

    [Fact]
    public void DecodesUtf8AndLittleEndianAndLanguageEscapes()
    {
        Assert.Equal("hé", PdfString.FromBytes([0xEF, 0xBB, 0xBF, (byte)'h', 0xC3, 0xA9]).ToText());
        Assert.Equal("hi", PdfString.FromBytes([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]).ToText());
        Assert.Equal("Hello", PdfString.FromBytes([0xFE, 0xFF, 0, 0x1B, 0, (byte)'e', 0, (byte)'n', 0, 0x1B, 0, (byte)'H', 0, (byte)'e', 0, (byte)'l', 0, (byte)'l', 0, (byte)'o']).ToText());
    }

    [Fact]
    public void TextThatLooksLikeAByteOrderMarkIsWrittenAsUtf16()
    {
        PdfString value = PdfString.FromText("þÿx");

        Assert.Equal("þÿx", value.ToText());
        Assert.True(value.Bytes is [0xFE, 0xFF, 0x00, 0xFE, ..]);
    }

    [Fact]
    public void DictionaryKeepsOrderAndReplacesInPlace()
    {
        PdfDictionary dictionary = new() { ["B"] = 1, ["A"] = 2, ["C"] = 3 };
        dictionary["A"] = 20;
        dictionary["D"] = 4;
        dictionary["C"] = null;

        Assert.Equal(["B", "A", "D"], dictionary.Keys.Select(k => k.Value));
        Assert.Equal(20, dictionary.GetInt("A"));
        Assert.Throws<ArgumentException>(() => dictionary.Add("B", 5));
    }

    [Fact]
    public void TypedGettersFollowTheValueKinds()
    {
        PdfDictionary dictionary = new() { ["I"] = 7, ["R"] = -2.75, ["B"] = false, ["N"] = PdfName.Get("X"), ["S"] = PdfString.FromText("t") };

        Assert.Equal(7, dictionary.GetNumber("I"));
        Assert.Equal(-2.75, dictionary.GetNumber("R"));
        Assert.Equal(-2, dictionary.GetInt("R"));
        Assert.Null(dictionary.GetInt("B"));
        Assert.False(dictionary.GetBool("B"));
        Assert.Equal("X", dictionary.GetName("N")!.Value);
        Assert.Equal("t", dictionary.GetText("S"));
        Assert.Null(dictionary.Get("Missing"));
        Assert.Null(dictionary.Get<PdfArray>("I"));
    }

    [Fact]
    public void UnresolvedReferenceIsNull()
    {
        PdfDictionary dictionary = new() { ["Ref"] = new PdfReference(5, 0) };

        Assert.IsType<PdfReference>(dictionary["Ref"]);
        Assert.Same(PdfNull.Instance, dictionary.Get("Ref"));
    }

    [Fact]
    public void ContainerCannotBeInTwoPlaces()
    {
        PdfDictionary shared = new() { ["A"] = 1 };
        PdfDictionary first = new() { ["X"] = shared };
        PdfArray second = [];

        Assert.Throws<InvalidOperationException>(() => second.Add(shared));
        Assert.Throws<InvalidOperationException>(() => first["Y"] = shared);

        // A copy can go anywhere, and removing the original frees it.
        second.Add(shared.Clone());
        first.Remove("X");
        second.Add(shared);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void ContainerCannotContainItself()
    {
        PdfArray outer = [];
        PdfArray inner = [];
        outer.Add(inner);

        Assert.Throws<InvalidOperationException>(() => inner.Add(outer));
        Assert.Throws<InvalidOperationException>(() => outer.Add(outer));
    }

    [Fact]
    public void StreamsMustBeIndirect()
    {
        Assert.Throws<ArgumentException>(() => new PdfArray().Add(new PdfStream()));
        Assert.Throws<ArgumentException>(() => new PdfDictionary()["S"] = new PdfStream());
    }

    [Fact]
    public void SettingTheSameValueAgainIsAllowed()
    {
        PdfDictionary inner = [];
        PdfDictionary outer = new() { ["I"] = inner };

        outer["I"] = inner;
        Assert.Same(inner, outer["I"]);
    }

    [Fact]
    public void ScalarsCanBeShared()
    {
        PdfName name = PdfName.Get("N");
        PdfArray first = [name, name];
        PdfArray second = [name];

        Assert.Equal(3, first.Count + second.Count);
    }

    [Fact]
    public void ChangesAnywhereInTheTreeReachTheOwner()
    {
        PdfArray kids = [1];
        PdfDictionary inner = new() { ["Kids"] = kids };
        PdfDictionary root = new() { ["Inner"] = inner };
        Owner owner = new();
        Containment.Bind(root, owner);

        kids.Add(2);
        inner["X"] = 3;
        root.Remove("Missing");
        root["Y"] = 4;
        kids[0] = 9;
        kids.RemoveAt(1);

        Assert.Equal(5, owner.Changes);

        // A detached container no longer reports to the owner.
        root.Remove("Inner");
        int before = owner.Changes;
        kids.Add(5);
        Assert.Equal(before, owner.Changes);
    }

    [Fact]
    public void StreamChangesReachTheOwner()
    {
        PdfStream stream = new();
        Owner owner = new();
        Containment.Bind(stream, owner);

        stream.Dictionary["Filter"] = PdfName.Get("FlateDecode");
        stream.SetEncodedBytes([1, 2, 3]);

        Assert.Equal(2, owner.Changes);
        Assert.Equal(3, stream.EncodedLength);
        Assert.Throws<InvalidOperationException>(() => new PdfDictionary()["D"] = stream.Dictionary);
    }

    [Fact]
    public void BindRefusesAnObjectAlreadyPlaced()
    {
        PdfDictionary inner = [];
        _ = new PdfArray([inner]);

        Assert.Throws<InvalidOperationException>(() => Containment.Bind(inner, new Owner()));
    }

    [Fact]
    public void CloneIsDeepForContainersOnly()
    {
        PdfReference reference = new(3, 0);
        PdfDictionary original = new() { ["A"] = new PdfArray([new PdfDictionary { ["B"] = 1 }, reference]) };

        PdfDictionary copy = original.Clone();
        copy.Get<PdfArray>("A")!.Get<PdfDictionary>(0)!["B"] = 2;

        Assert.Equal(1, original.Get<PdfArray>("A")!.Get<PdfDictionary>(0)!.GetInt("B"));
        Assert.Same(reference, copy.Get<PdfArray>("A")![1]);
        Assert.Equal(Write(original).Replace("/B 1", "/B 2", StringComparison.Ordinal), Write(copy));
    }

    [Fact]
    public void StreamWritesItsDataAndActualLength()
    {
        PdfStream stream = new(new PdfDictionary { ["Length"] = 99, ["Type"] = PdfName.Get("XObject") });
        stream.SetEncodedBytes("abc"u8);

        Assert.Equal("<< /Length 3 /Type /XObject >>\nstream\nabc\nendstream", Write(stream));
        Assert.Equal("<< /Length 3 /Type /XObject >> stream (3 bytes)", stream.ToString());
        Assert.Equal("<< /Length 0 >>\nstream\n\nendstream", Write(new PdfStream()));
    }

    [Fact]
    public void ReferencesCompareByNumberAndGeneration()
    {
        Assert.Equal(new PdfReference(1, 0), new PdfReference(1, 0));
        Assert.NotEqual(new PdfReference(1, 0), new PdfReference(1, 1));
        Assert.Equal("12 0 R", new PdfReference(12, 0).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReference(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReference(1, 65536));
    }

    [Fact]
    public void ArrayMutationsKeepItems()
    {
        PdfArray array = [1, 2, 3];
        array.Insert(0, 0);
        array.Remove(new PdfInteger(2));

        Assert.Equal("[0 1 3]", Write(array));
        Assert.Contains(new PdfInteger(3), array);
        Assert.Equal(2, array.IndexOf(new PdfInteger(3)));
        array.Clear();
        Assert.Empty(array);
    }

    [Fact]
    public void DeepTreesFailToWriteInsteadOfOverflowing()
    {
        PdfArray root = [];
        PdfArray at = root;
        for (int i = 0; i < ObjectWriter.MaxDepth + 10; i++)
        {
            PdfArray next = [];
            at.Add(next);
            at = next;
        }

        Assert.Throws<InvalidOperationException>(() => Write(root));
    }
}
