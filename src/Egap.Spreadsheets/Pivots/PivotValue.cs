using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Pivots;

/// <summary>What a pivot cache value is, as the cache's item elements tell them apart (ECMA-376 Part 1, 18.10.1).</summary>
internal enum PivotValueKind : byte
{
    /// <summary>No value: <c>m</c>.</summary>
    Blank,

    /// <summary>A number: <c>n</c>.</summary>
    Number,

    /// <summary>A date, from a cell with a date format: <c>d</c>.</summary>
    Date,

    /// <summary>Text: <c>s</c>.</summary>
    Text,

    /// <summary>TRUE or FALSE: <c>b</c>.</summary>
    Boolean,

    /// <summary>An error such as <c>#N/A</c>: <c>e</c>.</summary>
    Error,
}

/// <summary>
/// A value in a pivot cache: a shared item or a record's value. Two texts that differ only in case are the same item, as
/// in Excel; a date keeps its serial number in the workbook's date system.
/// </summary>
internal readonly struct PivotValue : IEquatable<PivotValue>
{
    private readonly double number;
    private readonly string? text;

    private PivotValue(PivotValueKind kind, double number, string? text)
    {
        Kind = kind;
        this.number = number;
        this.text = text;
    }

    public PivotValueKind Kind { get; }

    /// <summary>The number, the date's serial number or 1 for TRUE.</summary>
    public double Number => number;

    /// <summary>The text, or the error's text such as <c>#N/A</c>.</summary>
    public string Text => text ?? "";

    public bool IsNumeric => Kind is PivotValueKind.Number or PivotValueKind.Date;

    public static PivotValue Blank => default;

    public static PivotValue FromNumber(double value) => new(PivotValueKind.Number, value, null);

    public static PivotValue FromDate(double serial) => new(PivotValueKind.Date, serial, null);

    public static PivotValue FromText(string value) => new(PivotValueKind.Text, 0, value);

    public static PivotValue FromBoolean(bool value) => new(PivotValueKind.Boolean, value ? 1 : 0, null);

    public static PivotValue FromError(string value) => new(PivotValueKind.Error, 0, value);

    /// <summary>A cell's value; a number in a cell with a date format is a date, and empty text stays text, as Excel keeps it.</summary>
    public static PivotValue FromCell(CellValue value, bool date) => value.Kind switch
    {
        CellValueKind.Number => date ? FromDate(value.Number) : FromNumber(value.Number),
        CellValueKind.Text => FromText(value.Text),
        CellValueKind.Boolean => FromBoolean(value.Boolean),
        CellValueKind.Error => FromError(CellValue.ErrorText(value.Error)),
        _ => Blank,
    };

    /// <summary>The value as a cell holds it: a date as its serial number, an error as the error.</summary>
    public CellValue ToCell() => Kind switch
    {
        PivotValueKind.Number or PivotValueKind.Date => CellValue.FromNumber(number),
        PivotValueKind.Text => CellValue.FromText(text),
        PivotValueKind.Boolean => CellValue.FromBoolean(number != 0),
        PivotValueKind.Error => CellValue.TryParseError(Text, out CellError error) ? CellValue.FromError(error) : CellValue.FromText(text),
        _ => CellValue.Empty,
    };

    /// <summary>Reads an item element: <c>m</c>, <c>n</c>, <c>s</c>, <c>b</c>, <c>e</c> or <c>d</c>; null for another element.</summary>
    public static PivotValue? Read(XElement element, bool date1904)
    {
        string? v = (string?)element.Attribute("v");
        switch (element.Name.LocalName)
        {
            case "m":
                return Blank;
            case "n":
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? FromNumber(n) : FromNumber(0);
            case "s":
                return FromText(Xlsx.XlsxNames.Decode(v ?? ""));
            case "b":
                return FromBoolean(v is "1" or "true");
            case "e":
                return FromError(v ?? "#N/A");
            case "d":
                return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime date)
                    && ExcelValues.Serial(date, date1904) is double serial ? FromDate(serial) : FromDate(0);
            default:
                return null;
        }
    }

    /// <summary>The value as a cache item element.</summary>
    public XElement ToXml(XNamespace ns, bool date1904) => Kind switch
    {
        PivotValueKind.Number => new XElement(ns + "n", new XAttribute("v", Xlsx.XlsxNames.Number(number))),
        PivotValueKind.Date => new XElement(ns + "d", new XAttribute("v", DateText(number, date1904))),
        PivotValueKind.Text => new XElement(ns + "s", new XAttribute("v", Xlsx.XlsxNames.Encode(Text))),
        PivotValueKind.Boolean => new XElement(ns + "b", new XAttribute("v", number != 0 ? "1" : "0")),
        PivotValueKind.Error => new XElement(ns + "e", new XAttribute("v", Text)),
        _ => new XElement(ns + "m"),
    };

    /// <summary>A serial number as the cache writes a date: <c>2020-04-03T00:00:00</c>.</summary>
    public static string DateText(double serial, bool date1904) =>
        (ExcelValues.ToDateTime(serial, date1904) ?? new DateTime(1900, 1, 1)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The order a field's items sort in: numbers and dates, then text in the workbook's culture without case, then FALSE
    /// and TRUE, then errors, then the blank item.
    /// </summary>
    public int CompareTo(PivotValue other, CompareInfo compare)
    {
        int rank = Rank(Kind) - Rank(other.Kind);
        if (rank != 0)
        {
            return rank;
        }

        return Kind switch
        {
            PivotValueKind.Text or PivotValueKind.Error => compare.Compare(Text, other.Text, CompareOptions.IgnoreCase),
            PivotValueKind.Blank => 0,
            _ => number.CompareTo(other.number),
        };
    }

    private static int Rank(PivotValueKind kind) => kind switch
    {
        PivotValueKind.Number or PivotValueKind.Date => 0,
        PivotValueKind.Text => 1,
        PivotValueKind.Boolean => 2,
        PivotValueKind.Error => 3,
        _ => 4,
    };

    public bool Equals(PivotValue other) => Kind == other.Kind && Kind switch
    {
        PivotValueKind.Text or PivotValueKind.Error => string.Equals(text, other.text, StringComparison.OrdinalIgnoreCase),
        PivotValueKind.Blank => true,
        _ => number.Equals(other.number),
    };

    public override bool Equals(object? obj) => obj is PivotValue other && Equals(other);

    public override int GetHashCode() => Kind switch
    {
        PivotValueKind.Text or PivotValueKind.Error => HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Text)),
        PivotValueKind.Blank => 0,
        _ => HashCode.Combine(Kind, number),
    };

    public override string ToString() => Kind switch
    {
        PivotValueKind.Blank => "(blank)",
        PivotValueKind.Text or PivotValueKind.Error => Text,
        PivotValueKind.Boolean => number != 0 ? "TRUE" : "FALSE",
        _ => number.ToString(CultureInfo.InvariantCulture),
    };
}
