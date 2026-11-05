namespace Egap.Documents.Word;

/// <summary>
/// A table row as Word's formats define it before its text: the row and table formatting and the cells, each up to its
/// right edge (RTF's <c>\trowd</c> to <c>\cellx</c>, DOC's TAP, DOCX's <c>w:trPr</c> and <c>w:tblGrid</c>).
/// </summary>
internal sealed class WordRowDefinition
{
    public RowFormat Row { get; } = new();

    public TableFormat Table { get; } = new();

    public List<WordCellDefinition> Cells { get; } = [];

    /// <summary>The cell whose properties come before its right edge does.</summary>
    public WordCellDefinition Pending { get; set; } = new();

    /// <summary>The left edge of the first cell, in twips.</summary>
    public int Left { get; set; }

    /// <summary>Half the space between cells' text, in twips.</summary>
    public int? Gap { get; set; }

    public bool AutoFit { get; set; }

    public int WidthUnit { get; set; }

    public int Width { get; set; }

    /// <summary>The table style's index, or -1.</summary>
    public int Style { get; set; } = -1;

    public TablePosition? Position { get; set; }
}

/// <summary>A cell definition: its formatting, its right edge in twips, and how it merges.</summary>
internal sealed class WordCellDefinition
{
    public CellFormat Format { get; } = new();

    public int Right { get; set; }

    public bool MergeFirst { get; set; }

    public bool Merged { get; set; }

    public int WidthUnit { get; set; }

    public int Width { get; set; }
}

/// <summary>A style sheet entry, read before the styles are made so that every link can be resolved.</summary>
internal sealed class WordStyleDefinition
{
    public StyleType Type { get; set; } = StyleType.Paragraph;

    public int Index { get; set; }

    public string Name { get; set; } = "";

    public RunFormat Char { get; set; } = new();

    public ParagraphFormat Para { get; set; } = new();

    public TableFormat Table { get; } = new();

    public int BasedOn { get; set; } = -1;

    public int Next { get; set; } = -1;

    public int Link { get; set; } = -1;

    public bool Hidden { get; set; }

    public bool Primary { get; set; }

    public int? Priority { get; set; }

    public int ListOverride { get; set; }

    public int ListLevel { get; set; }
}

/// <summary>A list level as Word's formats define it (RTF's <c>\listlevel</c>, DOC's LVL, DOCX's <c>w:lvl</c>).</summary>
internal sealed class WordListLevel
{
    public NumberStyle Style { get; set; }

    public ParagraphAlignment Alignment { get; set; }

    public ListLabelSuffix Suffix { get; set; }

    public int Start { get; set; } = 1;

    /// <summary>The label with level numbers as the characters 0 to 8 where they go.</summary>
    public string Text { get; set; } = "";

    public bool Legal { get; set; }

    public bool NoRestart { get; set; }

    public RunFormat Char { get; set; } = new();

    public ParagraphFormat Para { get; set; } = new();

    /// <summary>Puts this level on a list definition's level.</summary>
    public void FillLevel(ListLevel target)
    {
        target.NumberStyle = Style;
        target.Alignment = Alignment;
        target.Suffix = Suffix;
        target.StartAt = Start;
        target.LevelText = Text;
        target.IsLegal = Legal;
        if (NoRestart)
        {
            target.RestartAfterLevel = 0;
        }

        target.ParagraphFormat.CopyRaw(Para);
        target.RunFormat.CopyRaw(Char);
    }
}
