using System.Reflection;

namespace Egap.Documents.Tests;

/// <summary>
/// Change notifications and version numbers (Phase 6 Build Plan task 2b, design Q3 editor-ready model): every public mutator
/// raises <see cref="Document.Changed"/> and stamps the paragraph and story it touched.
/// </summary>
public class ChangeNotificationTests
{
    /// <summary>
    /// Every public member that changes the tree, by "Type.Member" name, with an action that calls it once on a fresh document.
    /// <see cref="EveryPublicMutatorIsCovered"/> fails when a new mutator is added without an entry here.
    /// </summary>
    private static readonly Dictionary<string, Action<Fixture>> Mutators = new()
    {
        ["NodeCollection`1.Add"] = f => f.Paragraph.Inlines.Add(new Run(f.Doc, "x")),
        ["NodeCollection`1.Insert"] = f => f.Paragraph.Inlines.Insert(0, new Run(f.Doc, "x")),
        ["NodeCollection`1.RemoveAt"] = f => f.Paragraph.Inlines.RemoveAt(0),
        ["NodeCollection`1.Remove"] = f => f.Paragraph.Inlines.Remove(f.Run),
        ["NodeCollection`1.Clear"] = f => f.Paragraph.Inlines.Clear(),
        ["Node.Remove"] = f => f.Run.Remove(),
        ["Run.Text"] = f => f.Run.Text = "changed",
        ["Break.Kind"] = f => f.Break.Kind = BreakKind.Page,
        ["Document.AddSection"] = f => f.Doc.AddSection(),
        ["BlockContainer.AddParagraph"] = f => f.Body.AddParagraph("p"),
        ["BlockContainer.AddTable"] = f => f.Body.AddTable(1, 1),
        ["Paragraph.AddRun"] = f => f.Paragraph.AddRun("r"),
        ["Paragraph.AddTab"] = f => f.Paragraph.AddTab(),
        ["Paragraph.AddBreak"] = f => f.Paragraph.AddBreak(),
        ["Table.AddRow"] = f => f.Table.AddRow(1),
        ["Row.AddCell"] = f => f.Table.Rows[0].AddCell(),
        ["HeaderFooterCollection.GetOrAdd"] = f => f.Doc.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.FooterEven),
    };

    /// <summary>Public members that only read, or make detached copies, and so raise nothing.</summary>
    private static readonly HashSet<string> NotMutators =
    [
        "Node.Clone", "Node.Accept", "Node.GetText", "Node.Ancestors", "Node.Descendants", "Node.GetAncestor",
        "Document.Import", "Document.Clone", "NodeCollection`1.IndexOf", "NodeCollection`1.Contains", "NodeCollection`1.GetEnumerator",
        "Run.ToString", "Section.Clone", "Body.Clone", "HeaderFooter.Clone", "Paragraph.Clone", "Table.Clone", "Row.Clone",
        "Cell.Clone", "Run.Clone", "Tab.Clone", "Break.Clone",
    ];

    public static TheoryData<string> MutatorNames => [.. Mutators.Keys];

    [Theory]
    [MemberData(nameof(MutatorNames))]
    public void MutatorRaisesChangedAndStampsVersions(string name)
    {
        Fixture f = new();
        List<DocumentChangedEventArgs> changes = [];
        f.Doc.Changed += (_, e) => changes.Add(e);
        long before = f.Doc.Version;

        Mutators[name](f);

        Assert.NotEmpty(changes);
        Assert.Equal(before + changes.Count, f.Doc.Version);
        Assert.Equal(f.Doc.Version, changes[^1].Version);
        if (name is not ("Document.AddSection" or "HeaderFooterCollection.GetOrAdd"))
        {
            Assert.Equal(f.Doc.Version, f.Body.Version);   // the other two change no body
        }
    }

    [Fact]
    public void EveryPublicMutatorIsCovered()
    {
        Assembly assembly = typeof(Document).Assembly;
        List<string> missing = [];
        foreach (Type type in assembly.GetExportedTypes().Where(t => typeof(Node).IsAssignableFrom(t) || t.Name.StartsWith("NodeCollection", StringComparison.Ordinal) || t == typeof(HeaderFooterCollection)))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName && !method.Name.StartsWith("set_", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = $"{type.Name}.{(method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Name[4..] : method.Name)}";
                if (!Mutators.ContainsKey(name) && !NotMutators.Contains(name))
                {
                    missing.Add(name);
                }
            }
        }

        Assert.Empty(missing.Distinct());
    }

    [Fact]
    public void InsertAndRemoveNameTheParentAndIndex()
    {
        Fixture f = new();
        List<DocumentChangedEventArgs> changes = [];
        f.Doc.Changed += (_, e) => changes.Add(e);
        Run added = new(f.Doc, "new");

        f.Paragraph.Inlines.Insert(1, added);
        added.Remove();

        Assert.Equal(DocumentChangeKind.NodeInserted, changes[0].Kind);
        Assert.Same(added, changes[0].Node);
        Assert.Same(f.Paragraph, changes[0].Parent);
        Assert.Equal(1, changes[0].Index);
        Assert.Equal(DocumentChangeKind.NodeRemoved, changes[1].Kind);
        Assert.Same(f.Paragraph, changes[1].Parent);
        Assert.Equal(1, changes[1].Index);
        Assert.Null(added.Parent);
    }

    [Fact]
    public void PropertyAndTextChangesSayWhat()
    {
        Fixture f = new();
        List<DocumentChangedEventArgs> changes = [];
        f.Doc.Changed += (_, e) => changes.Add(e);

        f.Run.Text = "other";
        f.Run.Text = "other";
        f.Break.Kind = BreakKind.Column;

        Assert.Equal(2, changes.Count);
        Assert.Equal(DocumentChangeKind.TextChanged, changes[0].Kind);
        Assert.Same(f.Paragraph, changes[0].Parent);
        Assert.Equal(-1, changes[0].Index);
        Assert.Equal(DocumentChangeKind.PropertyChanged, changes[1].Kind);
        Assert.Equal("Kind", changes[1].Property);
    }

    [Fact]
    public void ClearRaisesOneRemovalPerNodeFromTheEnd()
    {
        Fixture f = new();
        List<int> indexes = [];
        f.Doc.Changed += (_, e) => indexes.Add(e.Index);

        f.Paragraph.Inlines.Clear();

        Assert.Equal([1, 0], indexes);
    }

    [Fact]
    public void VersionsMarkOnlyWhatChanged()
    {
        Fixture f = new();
        Paragraph other = f.Body.AddParagraph("untouched");
        HeaderFooter header = f.Doc.Sections[0].HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary);
        long otherVersion = other.Version;
        long headerVersion = header.Version;
        Cell cell = f.Table.Rows[0].Cells[0];

        cell.Paragraphs.Single().AddRun("in a cell");

        Assert.Equal(f.Doc.Version, cell.Paragraphs.Single().Version);
        Assert.Equal(f.Doc.Version, f.Body.Version);
        Assert.Equal(otherVersion, other.Version);
        Assert.Equal(headerVersion, header.Version);
        Assert.True(f.Paragraph.Version < f.Doc.Version);
    }

    /// <summary>A small document: one section whose body holds a paragraph (run, break) and a 1 by 1 table.</summary>
    private sealed class Fixture
    {
        public Fixture()
        {
            Doc = new Document();
            Body = Doc.AddSection().Body;
            Paragraph = Body.AddParagraph();
            Run = Paragraph.AddRun("text");
            Break = Paragraph.AddBreak();
            Table = Body.AddTable(1, 1);
        }

        public Document Doc { get; }

        public Body Body { get; }

        public Paragraph Paragraph { get; }

        public Run Run { get; }

        public Break Break { get; }

        public Table Table { get; }
    }
}
