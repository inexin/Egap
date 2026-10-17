namespace Egap.Documents.Tests;

/// <summary>Lists and numbering (Phase 6 Build Plan task 4, design Q6): definitions, lists, overrides, restarts and labels.</summary>
public class ListTests
{
    [Fact]
    public void NumberedListCountsAndNestedLevelsRestart()
    {
        Document doc = new();
        Body body = doc.AddSection().Body;
        List list = doc.Lists.AddNumbered();
        string[] labels = [.. new[] { (0, "a"), (1, "b"), (1, "c"), (0, "d"), (1, "e"), (2, "f") }.Select(item =>
        {
            Paragraph p = body.AddParagraph(item.Item2);
            p.SetList(list, item.Item1);
            return item.Item2;
        })];

        Assert.Equal(["1.", "a.", "b.", "2.", "a.", "i."], body.Paragraphs.Select(p => p.ListLabel!.Text));
        Assert.Equal(2, body.Paragraphs.Last().ListLabel!.Level);
        Assert.Equal(ParagraphAlignment.Right, body.Paragraphs.Last().ListLabel!.Format.Alignment);
    }

    [Fact]
    public void ListLevelIndentsGoBetweenStyleAndDirectFormatting()
    {
        Document doc = new();
        List list = doc.Lists.AddBulleted();
        ParagraphStyle style = doc.Styles.AddParagraphStyle("Indented");
        style.ParagraphFormat.LeftIndent = 100;
        Paragraph p = doc.AddSection().Body.AddParagraph("x");
        p.Style = style;
        p.SetList(list, 1);

        Assert.Equal(72, p.GetEffectiveFormat().LeftIndent);
        Assert.Equal(-18, p.GetEffectiveFormat().FirstLineIndent);
        Assert.Equal("o", p.ListLabel!.Text);

        p.Format.LeftIndent = 10;
        Assert.Equal(10, p.GetEffectiveFormat().LeftIndent);
    }

    [Fact]
    public void AStyleGivingTheListKeepsItsOwnIndents()
    {
        // Word: a style that puts paragraphs in a list keeps its own indents over the list level's (task 29, corpus finding).
        Document doc = new();
        List list = doc.Lists.AddNumbered();
        ParagraphStyle style = doc.Styles.AddParagraphStyle("Numbered");
        style.ParagraphFormat.List = list;
        style.ParagraphFormat.LeftIndent = 56.7;
        style.ParagraphFormat.FirstLineIndent = -28.35;
        ParagraphStyle derived = doc.Styles.AddParagraphStyle("Derived");
        derived.BasedOn = style;
        Paragraph p = doc.AddSection().Body.AddParagraph("x");
        p.Style = derived;

        Assert.Equal(56.7, p.GetEffectiveFormat().LeftIndent);
        Assert.Equal(-28.35, p.GetEffectiveFormat().FirstLineIndent);
    }

    [Fact]
    public void StyleCanPutParagraphsInAListAndNoneTakesThemOut()
    {
        Document doc = new();
        List list = doc.Lists.AddNumbered();
        ParagraphStyle style = doc.Styles.AddParagraphStyle("Numbered");
        style.ParagraphFormat.List = list;
        Body body = doc.AddSection().Body;
        Paragraph first = body.AddParagraph("a");
        first.Style = style;
        Paragraph second = body.AddParagraph("b");
        second.Style = style;
        Paragraph third = body.AddParagraph("c");
        third.Style = style;

        second.RemoveList();

        Assert.Equal("1.", first.ListLabel!.Text);
        Assert.Null(second.ListLabel);
        Assert.Same(List.None, second.Format.List);
        Assert.Equal("2.", third.ListLabel!.Text);
    }

    [Fact]
    public void ListsSharingADefinitionContinueUnlessOneRestarts()
    {
        Document doc = new();
        List first = doc.Lists.AddNumbered();
        List second = doc.Lists.Add(first.Definition);
        List restarted = doc.Lists.Add(first.Definition);
        restarted.Overrides[0].StartAt = 1;
        Body body = doc.AddSection().Body;
        body.AddParagraph("a").SetList(first);
        body.AddParagraph("b").SetList(first);
        body.AddParagraph("c").SetList(second);
        body.AddParagraph("d").SetList(restarted);
        body.AddParagraph("e").SetList(restarted);

        Assert.Equal(["1.", "2.", "3.", "1.", "2."], body.Paragraphs.Select(p => p.ListLabel!.Text));
    }

    [Fact]
    public void LabelsCombineLevelsAndFollowLegalNumbering()
    {
        Document doc = new();
        ListDefinition definition = doc.Lists.AddDefinition();
        definition.Kind = ListKind.Multilevel;
        definition.Levels[0].NumberStyle = NumberStyle.UpperRoman;
        definition.Levels[1].LevelText = "%1.%2";
        definition.Levels[2].LevelText = "%1.%2.%3)";
        definition.Levels[2].NumberStyle = NumberStyle.LowerLetter;
        definition.Levels[2].IsLegal = true;
        List list = doc.Lists.Add(definition);
        Body body = doc.AddSection().Body;
        body.AddParagraph().SetList(list, 0);
        body.AddParagraph().SetList(list, 1);
        body.AddParagraph().SetList(list, 2);
        body.AddParagraph().SetList(list, 2);

        Assert.Equal(["I.", "I.1", "1.1.a)", "1.1.b)"], body.Paragraphs.Select(p => p.ListLabel!.Text));
    }

    [Fact]
    public void LevelThatNeverRestartsKeepsCounting()
    {
        Document doc = new();
        List list = doc.Lists.AddNumbered();
        list.Definition.Levels[1].RestartAfterLevel = -1;
        Body body = doc.AddSection().Body;
        body.AddParagraph().SetList(list, 0);
        body.AddParagraph().SetList(list, 1);
        body.AddParagraph().SetList(list, 0);
        body.AddParagraph().SetList(list, 1);

        Assert.Equal("b.", body.Paragraphs.Last().ListLabel!.Text);
    }

    [Fact]
    public void OverrideLevelChangesOneListOnly()
    {
        Document doc = new();
        List a = doc.Lists.AddNumbered();
        List b = doc.Lists.Add(a.Definition);
        ListLevel own = b.Overrides[0].CreateLevel();
        own.LevelText = "(%1)";
        Body body = doc.AddSection().Body;
        body.AddParagraph().SetList(a);
        body.AddParagraph().SetList(b);

        Assert.Equal(["1.", "(2)"], body.Paragraphs.Select(p => p.ListLabel!.Text));
        Assert.Equal("%1.", a.GetLevel(0).LevelText);

        b.Overrides[0].RemoveLevel();
        Assert.Equal("2.", body.Paragraphs.Last().ListLabel!.Text);
    }

    [Fact]
    public void HeadersNumberOnTheirOwnAndTablesInDocumentOrder()
    {
        Document doc = new();
        List list = doc.Lists.AddNumbered();
        Section section = doc.AddSection();
        section.Body.AddParagraph().SetList(list);
        section.Body.AddTable(1, 1).Rows[0].Cells[0].Paragraphs.Single().SetList(list);
        section.Body.AddParagraph().SetList(list);
        Paragraph header = section.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph();
        header.SetList(list);

        Assert.Equal("3.", section.Body.Paragraphs.Last().ListLabel!.Text);
        Assert.Equal("1.", header.ListLabel!.Text);
    }

    [Theory]
    [InlineData(4, NumberStyle.UpperRoman, "IV")]
    [InlineData(1994, NumberStyle.LowerRoman, "mcmxciv")]
    [InlineData(27, NumberStyle.UpperLetter, "AA")]
    [InlineData(53, NumberStyle.LowerLetter, "aaa")]
    [InlineData(3, NumberStyle.DecimalZero, "03")]
    [InlineData(12, NumberStyle.DecimalZero, "12")]
    [InlineData(22, NumberStyle.Ordinal, "22nd")]
    [InlineData(13, NumberStyle.Ordinal, "13th")]
    [InlineData(21, NumberStyle.CardinalText, "Twenty-one")]
    [InlineData(20, NumberStyle.OrdinalText, "Twentieth")]
    [InlineData(112, NumberStyle.OrdinalText, "One hundred twelfth")]
    [InlineData(5, NumberStyle.None, "")]
    public void NumbersAreWrittenInEachStyle(int value, NumberStyle style, string expected)
    {
        Assert.Equal(expected, Numbering.Format(value, style));
    }

    [Fact]
    public void LabelsFollowEdits()
    {
        Document doc = new();
        List list = doc.Lists.AddNumbered();
        Body body = doc.AddSection().Body;
        Paragraph a = body.AddParagraph();
        a.SetList(list);
        Paragraph b = body.AddParagraph();
        b.SetList(list);
        Assert.Equal("2.", b.ListLabel!.Text);

        a.Remove();
        Assert.Equal("1.", b.ListLabel!.Text);

        list.Definition.Levels[0].NumberStyle = NumberStyle.UpperLetter;
        Assert.Equal("A.", b.ListLabel!.Text);
    }

    [Fact]
    public void ImportAndCloneBringListsAlong()
    {
        Document source = new();
        List list = source.Lists.AddNumbered();
        list.Overrides[0].StartAt = 5;
        Paragraph p = source.AddSection().Body.AddParagraph("x");
        p.SetList(list);
        Document target = new();

        Paragraph imported = target.Import(p);
        target.AddSection().Body.Blocks.Add(imported);
        Document clone = source.Clone();

        Assert.Same(target, imported.Format.List!.Document);
        Assert.Equal("5.", imported.ListLabel!.Text);
        Assert.Single(target.Lists);
        Paragraph cloned = clone.Sections[0].Body.Paragraphs.Single();
        Assert.Same(clone.Lists[list.Id], cloned.Format.List);
        Assert.Equal("5.", cloned.ListLabel!.Text);
    }

    [Fact]
    public void ListsMustBelongToTheDocument()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();

        Assert.Throws<ArgumentException>(() => p.SetList(new Document().Lists.AddNumbered()));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Format.ListLevel = 9);
        Assert.Throws<InvalidOperationException>(() => List.None.GetLevel(0));
    }

    [Fact]
    public void ListChangesRaiseFormattingChanged()
    {
        Document doc = new();
        List list = doc.Lists.AddNumbered();
        List<string?> changes = [];
        doc.Changed += (_, e) => changes.Add(e.Property);

        list.Definition.Levels[0].StartAt = 3;
        list.Definition.Levels[0].ParagraphFormat.LeftIndent = 10;
        list.Overrides[1].StartAt = 2;
        list.Overrides[2].CreateLevel().LevelText = "x";

        Assert.Equal(
            ["ListDefinitions.0.Levels[0].StartAt", "ListDefinitions.0.Levels[0].ParagraphFormat.LeftIndent", "Lists.1.Overrides[1].StartAt", "Lists.1.Overrides[2].Level", "Lists.1.Overrides[2].Level.LevelText"],
            changes);
    }
}
