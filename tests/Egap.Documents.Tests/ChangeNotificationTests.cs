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
        ["Paragraph.Style"] = f => f.Paragraph.Style = (ParagraphStyle)f.Doc.Styles.AddBuiltIn(BuiltInStyle.Quote),
        ["Run.Style"] = f => f.Run.Style = (CharacterStyle)f.Doc.Styles.AddBuiltIn(BuiltInStyle.Strong),
        ["Table.Style"] = f => f.Table.Style = (TableStyle)f.Doc.Styles.AddBuiltIn(BuiltInStyle.TableGrid),
        ["Table.Grid"] = f => f.Table.Grid = [100],
        ["Paragraph.SetList"] = f => f.Paragraph.SetList(f.Doc.Lists.AddNumbered()),
        ["Paragraph.RemoveList"] = f =>
        {
            f.Paragraph.SetList(f.Doc.Lists.AddNumbered());
            f.Paragraph.RemoveList();
        },
        ["Paragraph.AddField"] = f => f.Paragraph.AddField(FieldKind.Page),
        ["Paragraph.AddHyperlink"] = f => f.Paragraph.AddHyperlink("a", "https://example.com"),
        ["Paragraph.AddFootnote"] = f => f.Paragraph.AddFootnote("n"),
        ["Paragraph.AddPicture"] = f => f.Paragraph.AddPicture(new ImageData(new byte[] { 1 }, "image/png"), 1, 1),
        ["CompositeInline.AddRun"] = f => f.Paragraph.AddHyperlink("a", "b").AddRun("more"),
        ["ContentControl.AddParagraph"] = f => f.Body.Blocks.Add(new ContentControl(f.Doc)).AddParagraph("x"),
        ["Shape.AddTextBox"] = f => f.Paragraph.Inlines.Add(new Shape(f.Doc, ShapeKind.Rectangle, 1, 1)).AddTextBox(),
        ["Footnote.AddParagraph"] = f => f.Paragraph.AddFootnote("n").AddParagraph("more"),
        ["Comment.AddParagraph"] = f => f.Paragraph.Inlines.Add(new Comment(f.Doc, "a")).AddParagraph("x"),
        ["Comment.AddReply"] = f => f.Paragraph.Inlines.Add(new Comment(f.Doc, "a")).AddReply("b", "c"),
        ["Node.Replace"] = f => f.Paragraph.Replace("text", "other"),
        ["Document.UpdateFields"] = f => f.Paragraph.AddField("TITLE", "x").Start.Document.UpdateFields(),
        ["Document.FileName"] = f => f.Doc.FileName = "a.docx",
    };

    /// <summary>One node of each kind with settable properties, in the fixture's tree, for <see cref="EveryNodePropertyRaisesChanged"/>.</summary>
    private static readonly Dictionary<Type, Func<Fixture, Node>> NodesWithProperties = new()
    {
        [typeof(Run)] = f => f.Run,
        [typeof(Break)] = f => f.Break,
        [typeof(Paragraph)] = f => f.Paragraph,
        [typeof(Table)] = f => f.Table,
        [typeof(Row)] = f => f.Table.Rows[0],
        [typeof(Tab)] = f => f.Paragraph.AddTab(),
        [typeof(Hyperlink)] = f => f.Paragraph.AddHyperlink("a", "b"),
        [typeof(InlineContentControl)] = f => f.Paragraph.Inlines.Add(new InlineContentControl(f.Doc)),
        [typeof(Picture)] = f => f.Paragraph.AddPicture(new ImageData(new byte[] { 1 }, "image/png"), 1, 1),
        [typeof(Shape)] = f => f.Paragraph.Inlines.Add(new Shape(f.Doc, ShapeKind.Rectangle, 1, 1)),
        [typeof(FieldStart)] = f => f.Paragraph.AddField(FieldKind.Page).Start,
        [typeof(FieldSeparator)] = f => f.Paragraph.AddField(FieldKind.Page).Separator!,
        [typeof(FieldEnd)] = f => f.Paragraph.AddField(FieldKind.Page).End,
        [typeof(BookmarkStart)] = f => f.Paragraph.Inlines.Add(new BookmarkStart(f.Doc, "b")),
        [typeof(BookmarkEnd)] = f => f.Paragraph.Inlines.Add(new BookmarkEnd(f.Doc, "b")),
        [typeof(CommentRangeStart)] = f => f.Paragraph.Inlines.Add(new CommentRangeStart(f.Doc, 0)),
        [typeof(CommentRangeEnd)] = f => f.Paragraph.Inlines.Add(new CommentRangeEnd(f.Doc, 0)),
        [typeof(Footnote)] = f => f.Paragraph.AddFootnote("n"),
        [typeof(Comment)] = f => f.Paragraph.Inlines.Add(new Comment(f.Doc, "a")),
        [typeof(Island)] = f => f.Paragraph.Inlines.Add(new Island(f.Doc, IslandKind.Math, "docx", "<x/>")),
    };

    /// <summary>Public members that only read, or make detached copies, and so raise nothing.</summary>
    private static readonly HashSet<string> NotMutators =
    [
        "Node.Clone", "Node.Accept", "Node.GetText", "Node.Ancestors", "Node.Descendants", "Node.GetAncestor",
        "Document.Import", "Document.Clone", "NodeCollection`1.IndexOf", "NodeCollection`1.Contains", "NodeCollection`1.GetEnumerator",
        "Run.ToString", "Section.Clone", "Body.Clone", "HeaderFooter.Clone", "Paragraph.Clone", "Table.Clone", "Row.Clone",
        "Cell.Clone", "Run.Clone", "Tab.Clone", "Break.Clone", "Run.GetEffectiveFormat", "Paragraph.GetEffectiveFormat",
        "Table.GetEffectiveFormat", "Row.GetEffectiveFormat", "Cell.GetEffectiveFormat", "Inline.Revision",
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
        if (name is not ("Document.AddSection" or "HeaderFooterCollection.GetOrAdd" or "Footnote.AddParagraph" or "Comment.AddParagraph" or "Document.FileName"))
        {
            Assert.Equal(f.Doc.Version, f.Body.Version);   // the others change another story or none
        }
    }

    [Fact]
    public void EveryPublicMutatorIsCovered()
    {
        Assembly assembly = typeof(Document).Assembly;
        List<string> missing = [];
        foreach (Type type in assembly.GetExportedTypes().Where(t => typeof(Node).IsAssignableFrom(t) || t.Name.StartsWith("NodeCollection", StringComparison.Ordinal) || t == typeof(HeaderFooterCollection)))
        {
            // Property setters are covered per node kind by EveryNodePropertyRaisesChanged.
            if (type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(p => IsPlainSetter(p))
                && !type.IsAbstract && !NodesWithProperties.ContainsKey(type) && typeof(Node).IsAssignableFrom(type) && !Mutators.Keys.Any(k => k.StartsWith(type.Name + ".", StringComparison.Ordinal)))
            {
                missing.Add(type.Name + " (no entry in NodesWithProperties)");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName && !method.Name.StartsWith("set_", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = $"{type.Name}.{(method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Name[4..] : method.Name)}";
                bool reads = method.Name is "Clone" or "Replies" || (method.Name.StartsWith("Get", StringComparison.Ordinal) && method.Name != "GetOrAdd");
                bool coveredSetter = method.Name.StartsWith("set_", StringComparison.Ordinal) && NodesWithProperties.ContainsKey(type);
                bool initOnly = method.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");
                if (!Mutators.ContainsKey(name) && !NotMutators.Contains(name) && !reads && !coveredSetter && !initOnly)
                {
                    missing.Add(name);
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(", ", missing.Distinct()));
    }

    /// <summary>Every format a node owns reports every property change, with the property's name.</summary>
    [Fact]
    public void EveryFormatPropertyOfANodeRaisesChanged()
    {
        Fixture f = new();
        Section section = f.Doc.Sections[0];
        (Node Node, FormatBase Format, string Prefix)[] owned =
        [
            (f.Run, f.Run.Format, "Format."), (f.Paragraph, f.Paragraph.Format, "Format."), (f.Paragraph, f.Paragraph.MarkFormat, "MarkFormat."),
            (f.Table, f.Table.Format, "Format."), (f.Table.Rows[0], f.Table.Rows[0].Format, "Format."),
            (f.Table.Rows[0].Cells[0], f.Table.Rows[0].Cells[0].Format, "Format."), (section, section.PageSetup, "PageSetup."),
        ];
        List<string> silent = [];
        foreach ((Node node, FormatBase format, string prefix) in owned)
        {
            // FontName is a shorthand that sets the four font slots, each reported under its own name.
            foreach (PropertyInfo property in format.GetType().GetProperties().Where(p => p.CanWrite && p.Name != nameof(RunFormat.FontName)))
            {
                List<DocumentChangedEventArgs> changes = [];
                EventHandler<DocumentChangedEventArgs> handler = (_, e) => changes.Add(e);
                f.Doc.Changed += handler;
                property.SetValue(format, property.PropertyType == typeof(string) ? property.Name : property.PropertyType == typeof(List) ? f.Doc.Lists.AddNumbered() : SampleValue(property.PropertyType));
                f.Doc.Changed -= handler;
                if (!changes.Any(c => c.Node == node && c.Property == prefix + property.Name))
                {
                    silent.Add($"{format.GetType().Name}.{property.Name}");
                }
            }
        }

        Assert.True(silent.Count == 0, string.Join(", ", silent));
    }

    [Fact]
    public void StyleDefaultAndThemeChangesRaiseFormattingChanged()
    {
        Fixture f = new();
        ParagraphStyle style = f.Doc.Styles.AddParagraphStyle("S");
        List<DocumentChangedEventArgs> changes = [];
        f.Doc.Changed += (_, e) => changes.Add(e);
        long formatting = f.Doc.FormattingVersion;

        style.RunFormat.Size = 12;
        style.Name = "Renamed";
        f.Doc.Styles.DefaultRunFormat.Bold = true;
        f.Doc.Theme.MinorLatinFont = "Aptos";
        f.Doc.Theme.Colors[ThemeColor.Accent2] = new Egami.Pixels.Rgba8(1, 2, 3, 255);

        Assert.Equal(5, changes.Count);
        Assert.All(changes, c => Assert.Equal(DocumentChangeKind.FormattingChanged, c.Kind));
        Assert.Equal(["Styles.S.RunFormat.Size", "Styles.S.Name", "Styles.DefaultRunFormat.Bold", "Theme.MinorLatinFont", "Theme.Colors.Accent2"], changes.Select(c => c.Property));
        Assert.Equal(formatting + 5, f.Doc.FormattingVersion);
    }

    /// <summary>Every settable property of every node kind reports its change, under its own name.</summary>
    [Fact]
    public void EveryNodePropertyRaisesChanged()
    {
        List<string> silent = [];
        foreach ((Type type, Func<Fixture, Node> make) in NodesWithProperties)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(IsPlainSetter))
            {
                Fixture f = new();
                Node node = make(f);
                object value = property.Name switch
                {
                    "Style" => property.PropertyType == typeof(ParagraphStyle) ? f.Doc.Styles.AddBuiltIn(BuiltInStyle.Quote)
                        : property.PropertyType == typeof(CharacterStyle) ? f.Doc.Styles.AddBuiltIn(BuiltInStyle.Strong)
                        : f.Doc.Styles.AddBuiltIn(BuiltInStyle.TableGrid),
                    "Revision" or "MarkRevision" => new Revision(RevisionKind.Deletion, "a"),
                    "FormatRevision" => new FormatRevision("a", null, node is Run ? new RunFormat() : new ParagraphFormat()),
                    "Grid" => new double[] { 10 },
                    "Image" => new ImageData(new byte[] { 2 }, "image/gif"),
                    "Floating" => new FloatingPlacement { Wrap = WrapMode.Through },
                    "Crop" => (0.1, 0.0, 0.0, 0.0),
                    "Outline" => new Border(BorderStyle.Dashed),
                    "Fill" => DocumentColor.FromHex("#010203"),
                    "Kind" when node is Shape => ShapeKind.Ellipse,
                    "Kind" when node is Footnote => NoteKind.Endnote,
                    "Kind" when node is Break => BreakKind.Column,
                    "ParentId" => 7,
                    "CommentId" => 5,
                    "Date" => DateTimeOffset.UnixEpoch,
                    _ when property.PropertyType == typeof(bool) => !(bool)property.GetValue(node)!,
                    _ => property.PropertyType == typeof(string) ? property.Name + "!" : SampleValue(property.PropertyType),
                };
                List<DocumentChangedEventArgs> changes = [];
                f.Doc.Changed += (_, e) => changes.Add(e);
                property.SetValue(node, value);
                if (!changes.Any(c => c.Node == node && c.Property == property.Name || (c.Kind == DocumentChangeKind.TextChanged && c.Node == node)))
                {
                    silent.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(silent.Count == 0, string.Join(", ", silent));
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

    private static object SampleValue(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t switch
        {
            _ when t == typeof(bool) => true,
            _ when t == typeof(long) => 4L,
            _ when t == typeof(double) => 3.0,
            _ when t == typeof(int) => 2,
            _ when t == typeof(string) => "sample",
            _ when t.IsEnum => Enum.GetValues(t).GetValue(1)!,
            _ when t == typeof(DocumentColor) => DocumentColor.FromHex("#123456"),
            _ when t == typeof(TableWidth) => TableWidth.Points(100),
            _ when t == typeof(Border) => new Border(BorderStyle.Single),
            _ when t == typeof(Shading) => Shading.Solid(DocumentColor.FromHex("#EEEEEE")),
            _ when t == typeof(IReadOnlyList<TabStop>) => new[] { new TabStop(36) },
            _ when t == typeof(IReadOnlyList<TextColumn>) => new[] { new TextColumn(200, 10) },
            _ => throw new NotSupportedException(t.Name),
        };
    }

    private static bool IsPlainSetter(PropertyInfo property)
    {
        return property.SetMethod is { IsPublic: true } setter
            && !setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");
    }
}
