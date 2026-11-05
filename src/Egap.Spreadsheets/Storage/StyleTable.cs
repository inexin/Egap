namespace Egap.Spreadsheets.Storage;

/// <summary>Keeps each distinct value once and numbers them in the order they came.</summary>
internal sealed class Interner<T>
    where T : notnull
{
    private readonly List<T> items = [];
    private readonly Dictionary<T, int> indexes = [];

    public int Count => items.Count;

    public T this[int index] => items[index];

    public int Add(T item)
    {
        if (!indexes.TryGetValue(item, out int index))
        {
            index = items.Count;
            items.Add(item);
            indexes.Add(item, index);
        }

        return index;
    }

    /// <summary>
    /// Adds an item read from a file at the next index, so the file's indexes stay; when <paramref name="share"/> is set and
    /// no equal item came before, later adds of an equal item find this one.
    /// </summary>
    public int AddRead(T item, bool share)
    {
        int index = items.Count;
        items.Add(item);
        if (share)
        {
            indexes.TryAdd(item, index);
        }

        return index;
    }
}

/// <summary>A cell format as SpreadsheetML's <c>cellXfs</c> table keeps it: indexes into the font, fill, border and number format tables, with the alignment and protection inline.</summary>
internal readonly record struct CellFormatRecord(int Font, int Fill, int Border, int NumberFormat, CellAlignment Alignment, CellProtection Protection);

/// <summary>
/// The workbook's cell styles, each kept once with an index cells refer to, as Excel's <c>cellXfs</c>. Fonts, fills, borders
/// and number formats are kept once too, in their own tables, so the XLSX writer can write the style part as it is. Index 0
/// is <see cref="CellStyle.Default"/>.
/// </summary>
internal sealed class StyleTable
{
    private readonly List<CellStyle> styles = [];
    private readonly List<CellFormatRecord> records = [];
    private readonly Dictionary<CellStyle, int> indexes = [];

    public StyleTable()
    {
        AddDefaults();
    }

    private StyleTable(bool empty)
    {
        _ = empty;
    }

    /// <summary>The style part a loaded workbook came with: its document, and the elements of the tables the file had, written back for the indexes they cover.</summary>
    public Xlsx.StyleSource? Source { get; set; }

    /// <summary>An empty table that a reader fills in the file's order.</summary>
    public static StyleTable ForLoading() => new(empty: true);

    /// <summary>Adds the fills Excel wants first and the default style, when the table has no styles.</summary>
    public void AddDefaults()
    {
        if (styles.Count > 0)
        {
            return;
        }

        // Excel wants fills 0 and 1 to be none and gray125, whatever the cells use.
        if (Fills.Count == 0)
        {
            Fills.Add(CellFill.None);
            Fills.Add(new CellFill { Pattern = CellFillPattern.Gray125 });
        }

        Add(CellStyle.Default);
    }

    /// <summary>
    /// Adds a style read from a file at the next index with the table indexes the file gave; <paramref name="share"/> says
    /// whether the style is all the model holds of the file's, so a style set later can use it.
    /// </summary>
    public int AddRead(CellStyle style, CellFormatRecord record, bool share)
    {
        int index = styles.Count;
        styles.Add(style);
        records.Add(record);
        if (share)
        {
            indexes.TryAdd(style, index);
        }

        return index;
    }

    /// <summary>How many styles the table holds.</summary>
    public int Count => styles.Count;

    public Interner<CellFont> Fonts { get; } = new();

    public Interner<CellFill> Fills { get; } = new();

    public Interner<CellBorders> Borders { get; } = new();

    public NumberFormatTable NumberFormats { get; } = new();

    /// <summary>The style part's differential formats (dxfs) as conditional formatting reads them; null for one the model cannot describe.</summary>
    public List<CellStyle?> DifferentialStyles { get; } = [];

    /// <summary>The style at an index.</summary>
    public CellStyle this[int index] => styles[index];

    /// <summary>The style at an index as the cell formats table keeps it.</summary>
    public CellFormatRecord Record(int index) => records[index];

    /// <summary>The index of <paramref name="style"/>, added when it is new.</summary>
    public int Add(CellStyle style)
    {
        if (indexes.TryGetValue(style, out int index))
        {
            return index;
        }

        index = styles.Count;
        styles.Add(style);
        records.Add(new CellFormatRecord(Fonts.Add(style.Font), Fills.Add(style.Fill), Borders.Add(style.Borders),
            NumberFormats.Add(style.NumberFormat), style.Alignment, style.Protection));
        indexes.Add(style, index);
        return index;
    }
}

/// <summary>
/// Number format codes by their id, as SpreadsheetML's <c>numFmts</c> numbers them: Excel's built-in formats keep their
/// fixed ids (ECMA-376 Part 1, 18.8.30), and other codes get ids from 164 on.
/// </summary>
internal sealed class NumberFormatTable
{
    /// <summary>The first id for a code that is not built in.</summary>
    public const int FirstCustomId = 164;

    private static readonly Dictionary<int, string> BuiltInCodes = new()
    {
        [0] = "General",
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "mm-dd-yy",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "m/d/yy h:mm",
        [37] = "#,##0 ;(#,##0)",
        [38] = "#,##0 ;[Red](#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;[Red](#,##0.00)",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mmss.0",
        [48] = "##0.0E+0",
        [49] = "@",
    };

    private static readonly Dictionary<string, int> BuiltInIds = BuiltInCodes.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private readonly Dictionary<string, int> customIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> customCodes = [];
    private int nextId = FirstCustomId;

    /// <summary>The codes that are not built in, by id.</summary>
    public IReadOnlyDictionary<int, string> Custom => customCodes;

    /// <summary>The built-in code with an id, or null when the id is not one of Excel's fixed ones.</summary>
    public static string? BuiltIn(int id) => BuiltInCodes.GetValueOrDefault(id);

    /// <summary>The id of a code: its built-in id, or one from 164 on, added when it is new.</summary>
    public int Add(string code)
    {
        if (BuiltInIds.TryGetValue(code, out int id) || customIds.TryGetValue(code, out id))
        {
            return id;
        }

        id = nextId++;
        customIds.Add(code, id);
        customCodes.Add(id, code);
        return id;
    }

    /// <summary>The code with an id, or null when no code has it; a code the file gave for an id wins over the built-in one.</summary>
    public string? Code(int id) => customCodes.GetValueOrDefault(id) ?? BuiltIn(id);

    /// <summary>Adds a code a file defines for an id.</summary>
    public void AddRead(int id, string code)
    {
        if (customCodes.TryAdd(id, code))
        {
            customIds.TryAdd(code, id);
            nextId = Math.Max(nextId, id + 1);
        }
    }
}
