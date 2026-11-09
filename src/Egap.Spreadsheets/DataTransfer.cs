using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets;

/// <summary>How <see cref="Cell.ImportData{T}(IEnumerable{T}, DataImportOptions?)"/> and its overloads write data into cells.</summary>
public sealed class DataImportOptions
{
    /// <summary>Whether the first row holds the column names: property names, or the table's or reader's column names. Default true.</summary>
    public bool Header { get; init; } = true;

    /// <summary>
    /// The columns to write, by name and in this order; null writes them all, in the order of the type's properties or the
    /// table's columns. Names match without regard to case.
    /// </summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>The number format for dates without a time of day. Default <c>yyyy-mm-dd</c>.</summary>
    public string DateFormat { get; init; } = "yyyy-mm-dd";

    /// <summary>The number format for dates with a time of day. Default <c>yyyy-mm-dd hh:mm:ss</c>.</summary>
    public string DateTimeFormat { get; init; } = "yyyy-mm-dd hh:mm:ss";

    /// <summary>The number format for times of day and durations. Default <c>[h]:mm:ss</c>, which also shows more than a day.</summary>
    public string TimeFormat { get; init; } = "[h]:mm:ss";

    /// <summary>
    /// The name of an Excel table to make over the data, with its header row and filter buttons, or null for none. Needs
    /// <see cref="Header"/>.
    /// </summary>
    public string? TableName { get; init; }
}

/// <summary>How <see cref="CellRange.ToDataTable(DataExportOptions?)"/> and <see cref="CellRange.ToList{T}(DataExportOptions?)"/> read cells.</summary>
public sealed class DataExportOptions
{
    /// <summary>
    /// Whether the range's first row holds the column names, which name the table's columns and pick the properties they
    /// fill. Without it, columns are named <c>Column1</c>, <c>Column2</c> and so on and fill properties in their order.
    /// Default true.
    /// </summary>
    public bool Header { get; init; } = true;

    /// <summary>Whether rows whose cells are all empty are left out. Default true.</summary>
    public bool SkipEmptyRows { get; init; } = true;
}

/// <summary>Writes .NET values into cells and reads cells back as .NET values.</summary>
internal static class DataTransfer
{
    private const int MaxText = 32767;

    /// <summary>A column to write: its header and how to get a row's value.</summary>
    private sealed record Source(string Name, Type Type, Func<object, object?> Get);

    public static CellRange Import<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(Cell at, IEnumerable<T> items, DataImportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(items);
        options ??= new DataImportOptions();
        List<Source> columns;
        if (IsScalar(typeof(T)))
        {
            columns = [new Source("Value", typeof(T), item => item)];
        }
        else
        {
            columns = [.. Properties(typeof(T)).Select(p => new Source(p.Name, p.PropertyType, p.GetValue))];
        }

        columns = Pick(columns, options);
        Writer writer = new(at, columns, options);
        foreach (T item in items)
        {
            writer.Row(item is null ? null : (object)item);
        }

        return writer.Finish();
    }

    public static CellRange Import(Cell at, DataTable table, DataImportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(table);
        options ??= new DataImportOptions();
        List<Source> columns = [.. table.Columns.Cast<DataColumn>().Select(c => new Source(c.ColumnName, c.DataType, row => ((DataRow)row)[c]))];
        columns = Pick(columns, options);
        Writer writer = new(at, columns, options);
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState != DataRowState.Deleted)
            {
                writer.Row(row);
            }
        }

        return writer.Finish();
    }

    public static CellRange Import(Cell at, IDataReader reader, DataImportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new DataImportOptions();
        List<Source> columns = [];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            int field = i;
            columns.Add(new Source(reader.GetName(i), reader.GetFieldType(i), r => ((IDataRecord)r).GetValue(field)));
        }

        columns = Pick(columns, options);
        Writer writer = new(at, columns, options);
        while (reader.Read())
        {
            writer.Row(reader);
        }

        return writer.Finish();
    }

    public static DataTable ToDataTable(CellRange range, DataExportOptions? options)
    {
        options ??= new DataExportOptions();
        Reader reader = new(range, options);
        DataTable table = new(range.Worksheet.Name) { Locale = CultureInfo.InvariantCulture };
        Type[] types = new Type[reader.Columns];
        for (int c = 0; c < reader.Columns; c++)
        {
            string name = reader.Name(c);
            string unique = name;
            for (int n = 2; table.Columns.Contains(unique); n++)
            {
                unique = name + n.ToString(CultureInfo.InvariantCulture);
            }

            types[c] = table.Columns.Add(unique, reader.ColumnType(c)).DataType;
        }

        table.BeginLoadData();
        object?[] values = new object?[reader.Columns];
        foreach (int row in reader.Rows())
        {
            for (int c = 0; c < reader.Columns; c++)
            {
                values[c] = reader.Value(row, c, types[c]) ?? DBNull.Value;
            }

            table.Rows.Add(values);
        }

        table.EndLoadData();
        table.AcceptChanges();
        return table;
    }

    public static List<T> ToList<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] T>(CellRange range, DataExportOptions? options)
    {
        options ??= new DataExportOptions();
        Reader reader = new(range, options);
        List<T> list = [];
        if (IsScalar(typeof(T)))
        {
            foreach (int row in reader.Rows())
            {
                list.Add(reader.Convert(row, 0, typeof(T)) is T value ? value : default!);
            }

            return list;
        }

        PropertyInfo[] properties = [.. Properties(typeof(T))];
        ConstructorInfo? empty = typeof(T).GetConstructor(Type.EmptyTypes);
        ConstructorInfo? full = empty is null && !typeof(T).IsValueType ? Constructor(typeof(T)) : null;
        if (empty is null && full is null && !typeof(T).IsValueType)
        {
            throw new InvalidOperationException($"{typeof(T).Name} needs a public constructor without parameters, or one whose parameters are named as its properties.");
        }

        // Each member to fill, with the column it takes: by header name, else by place.
        PropertyInfo[] settable = full is null ? [.. properties.Where(p => p.CanWrite && p.SetMethod!.IsPublic)] : [];
        (string Name, Type Type)[] members = full is null
            ? [.. settable.Select(p => (p.Name, p.PropertyType))]
            : [.. full.GetParameters().Select(p => (p.Name!, p.ParameterType))];
        int[] columnOf = new int[members.Length];
        for (int m = 0; m < members.Length; m++)
        {
            columnOf[m] = options.Header ? reader.Find(members[m].Name) : (m < reader.Columns ? m : -1);
        }

        foreach (int row in reader.Rows())
        {
            if (full is not null)
            {
                object?[] arguments = new object?[members.Length];
                for (int m = 0; m < members.Length; m++)
                {
                    arguments[m] = columnOf[m] < 0 ? null : reader.Convert(row, columnOf[m], members[m].Type);
                }

                list.Add((T)full.Invoke(arguments));
                continue;
            }

            object item = empty is null ? Activator.CreateInstance<T>()! : empty.Invoke(null);
            for (int m = 0; m < members.Length; m++)
            {
                if (columnOf[m] >= 0)
                {
                    settable[m].SetValue(item, reader.Convert(row, columnOf[m], members[m].Type));
                }
            }

            list.Add((T)item);
        }

        return list;
    }

    /// <summary>The public instance properties that can be read, in declaration order.</summary>
    private static IEnumerable<PropertyInfo> Properties([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetMethod!.IsPublic && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken);

    /// <summary>The public constructor with the most parameters, when every parameter is named as a property, as a record's is.</summary>
    private static ConstructorInfo? Constructor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
    {
        HashSet<string> names = new(Properties(type).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        return type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0 && c.GetParameters().All(p => p.Name is not null && names.Contains(p.Name)))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
    }

    private static List<Source> Pick(List<Source> columns, DataImportOptions options)
    {
        if (options.TableName is not null && !options.Header)
        {
            throw new ArgumentException("A table needs the header row.", nameof(options));
        }

        if (options.Columns is null)
        {
            return columns;
        }

        List<Source> picked = [];
        foreach (string name in options.Columns)
        {
            picked.Add(columns.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There is no column '{name}'.", nameof(options)));
        }

        return picked;
    }

    /// <summary>Whether a type goes in one cell rather than one cell per property.</summary>
    internal static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(object);
    }

    private static string Normal(string name) => new([.. name.Where(ch => ch is not (' ' or '_' or '-')).Select(char.ToUpperInvariant)]);

    /// <summary>Writes rows of values into the cells below a top-left cell.</summary>
    private sealed class Writer
    {
        private readonly Worksheet sheet;
        private readonly int top;
        private readonly int left;
        private readonly List<Source> columns;
        private readonly DataImportOptions options;
        private readonly bool date1904;
        private readonly Dictionary<(int Style, string Format), int> formatted = [];
        private int row;

        public Writer(Cell at, List<Source> columns, DataImportOptions options)
        {
            ArgumentNullException.ThrowIfNull(at);
            sheet = at.Worksheet;
            top = at.Row;
            left = at.Column;
            this.columns = columns;
            this.options = options;
            date1904 = sheet.Workbook.Date1904;
            if (columns.Count == 0 || left + columns.Count > CellAddress.MaxColumns)
            {
                throw new ArgumentException(columns.Count == 0 ? "There are no columns to write." : "The columns run past the sheet's last column.");
            }

            row = top;
            if (options.Header)
            {
                for (int c = 0; c < columns.Count; c++)
                {
                    sheet.SetValue(row, left + c, CellValue.FromText(columns[c].Name.Length > MaxText ? columns[c].Name[..MaxText] : columns[c].Name));
                }

                row++;
            }
        }

        public void Row(object? item)
        {
            if (row >= CellAddress.MaxRows)
            {
                throw new InvalidOperationException("The rows run past the sheet's last row.");
            }

            for (int c = 0; c < columns.Count; c++)
            {
                object? value = item is null ? null : columns[c].Get(item);
                Write(row, left + c, value);
            }

            row++;
        }

        public CellRange Finish()
        {
            int last = Math.Max(top, row - 1);
            RangeAddress range = new(new CellAddress(top, left), new CellAddress(last, left + columns.Count - 1));
            if (options.TableName is not null)
            {
                sheet.Tables.Add(row - 1 > top ? range : new RangeAddress(range.First, new CellAddress(top + 1, range.Last.Column)), options.TableName);
            }

            return sheet.Range(range);
        }

        private void Write(int r, int c, object? value)
        {
            switch (value)
            {
                case null or DBNull:
                    if (sheet.GetKind(r, c) != CellValueKind.Empty || sheet.HasFormula(r, c))
                    {
                        sheet.SetValue(r, c, CellValue.Empty);
                    }

                    return;
                case string text:
                    sheet.SetValue(r, c, CellValue.FromText(text.Length > MaxText ? throw new ArgumentException($"The text for {new CellAddress(r, c)} is longer than a cell's 32,767 characters.") : text));
                    return;
                case char ch:
                    sheet.SetValue(r, c, CellValue.FromText(ch.ToString()));
                    return;
                case bool flag:
                    sheet.SetValue(r, c, CellValue.FromBoolean(flag));
                    return;
                case double or float or decimal or int or long or short or byte or sbyte or ushort or uint or ulong:
                    double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (double.IsFinite(number))
                    {
                        sheet.SetNumber(r, c, number);
                    }
                    else
                    {
                        sheet.SetValue(r, c, CellValue.FromError(CellError.Number));
                    }

                    return;
                case DateTime date:
                    Date(r, c, date);
                    return;
                case DateTimeOffset offset:
                    Date(r, c, offset.DateTime);
                    return;
                case DateOnly day:
                    Date(r, c, day.ToDateTime(TimeOnly.MinValue));
                    return;
                case TimeOnly time:
                    Number(r, c, time.ToTimeSpan().TotalDays, options.TimeFormat);
                    return;
                case TimeSpan span:
                    Number(r, c, span.TotalDays, options.TimeFormat);
                    return;
                case Enum:
                    sheet.SetValue(r, c, CellValue.FromText(value.ToString()));
                    return;
                case IFormattable formattable:
                    sheet.SetValue(r, c, CellValue.FromText(Cap(formattable.ToString(null, CultureInfo.InvariantCulture))));
                    return;
                default:
                    sheet.SetValue(r, c, CellValue.FromText(Cap(value.ToString() ?? "")));
                    return;
            }
        }

        private static string Cap(string text) => text.Length > MaxText ? text[..MaxText] : text;

        /// <summary>A date as its serial number with a date format; one before the date system's first day as ISO text, as Excel keeps such dates.</summary>
        private void Date(int r, int c, DateTime date)
        {
            if (ExcelValues.Serial(date.Date, date1904) is not double serial)
            {
                sheet.SetValue(r, c, CellValue.FromText(date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                return;
            }

            Number(r, c, serial + date.TimeOfDay.TotalDays, date.TimeOfDay == TimeSpan.Zero ? options.DateFormat : options.DateTimeFormat);
        }

        private void Number(int r, int c, double number, string format)
        {
            sheet.SetNumber(r, c, number);
            int style = sheet.GetStyleIndex(r, c);
            if (!formatted.TryGetValue((style, format), out int index))
            {
                CellStyle old = sheet.Workbook.Styles[style];
                index = old.NumberFormat == format ? style : sheet.Workbook.Styles.Add(old with { NumberFormat = format });
                formatted[(style, format)] = index;
            }

            if (index != style)
            {
                sheet.SetStyleIndex(r, c, index);
            }
        }
    }

    /// <summary>Reads a range's rows as values of .NET types.</summary>
    private sealed class Reader
    {
        private readonly Worksheet sheet;
        private readonly RangeAddress range;
        private readonly DataExportOptions options;
        private readonly CultureInfo culture;
        private readonly string[] names;

        public Reader(CellRange cells, DataExportOptions options)
        {
            sheet = cells.Worksheet;
            this.options = options;
            culture = sheet.Workbook.Culture;

            // A whole column or row reads only as far as the sheet holds anything.
            RangeAddress address = cells.Address;
            if (sheet.UsedRange?.Address is { } used)
            {
                address = new RangeAddress(address.First, new CellAddress(Math.Min(address.Last.Row, Math.Max(address.First.Row, used.Last.Row)), Math.Min(address.Last.Column, Math.Max(address.First.Column, used.Last.Column))));
            }
            else
            {
                address = new RangeAddress(address.First, address.First);
            }

            range = address;
            Columns = range.ColumnCount;
            names = new string[Columns];
            for (int c = 0; c < Columns; c++)
            {
                string name = options.Header ? sheet.GetValue(range.First.Row, range.First.Column + c) is { IsEmpty: false } header ? Text(range.First.Row, range.First.Column + c) : "" : "";
                names[c] = name.Length > 0 ? name : "Column" + (c + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        public int Columns { get; }

        public string Name(int column) => names[column];

        /// <summary>The column whose header is a name, ignoring case, spaces, hyphens and underscores; -1 when none is.</summary>
        public int Find(string name)
        {
            string wanted = Normal(name);
            return Array.FindIndex(names, n => Normal(n) == wanted);
        }

        /// <summary>The rows of data, without the header and, when asked, the empty rows.</summary>
        public IEnumerable<int> Rows()
        {
            for (int r = range.First.Row + (options.Header ? 1 : 0); r <= range.Last.Row; r++)
            {
                if (options.SkipEmptyRows && Empty(r))
                {
                    continue;
                }

                yield return r;
            }
        }

        /// <summary>
        /// The type that holds a column's values: double for numbers, DateTime for numbers with a date format, TimeSpan for
        /// numbers with a time format, bool, string, or object when the column mixes them.
        /// </summary>
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
        public Type ColumnType(int column)
        {
            Type? found = null;
            foreach (int row in Rows())
            {
                Type? type = Natural(row, range.First.Column + column);
                if (type is null)
                {
                    continue;
                }

                if (found is null)
                {
                    found = type;
                }
                else if (found != type)
                {
                    return typeof(object);
                }
            }

            return found ?? typeof(string);
        }

        /// <summary>A cell's value as the column's type: null for an empty cell.</summary>
        public object? Value(int row, int column, Type type)
        {
            int c = range.First.Column + column;
            CellValue value = sheet.GetValue(row, c);
            if (value.IsEmpty)
            {
                return null;
            }

            if (type == typeof(object))
            {
                type = Natural(row, c) ?? typeof(string);
            }

            return Convert(row, column, type);
        }

        /// <summary>A cell's value converted to a type; null for an empty cell.</summary>
        /// <exception cref="FormatException">The value cannot be the type.</exception>
        public object? Convert(int row, int column, Type type)
        {
            int c = range.First.Column + column;
            CellValue value = sheet.GetValue(row, c);
            Type? inner = Nullable.GetUnderlyingType(type);
            if (value.IsEmpty)
            {
                // Reflection sets a value-type member given null to its default.
                return null;
            }

            Type target = inner ?? type;
            try
            {
                return To(value, row, c, target);
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException or InvalidCastException)
            {
                throw new FormatException($"{sheet.Name}!{new CellAddress(row, c)} holds {Describe(value)}, which is not a {target.Name}.", e);
            }
        }

        private object To(CellValue value, int row, int column, Type target)
        {
            if (target == typeof(object))
            {
                return To(value, row, column, Natural(row, column) ?? typeof(string));
            }

            if (target == typeof(string))
            {
                return Text(row, column);
            }

            if (value.Kind == CellValueKind.Error)
            {
                throw new FormatException();
            }

            if (target == typeof(bool))
            {
                return value.Kind switch
                {
                    CellValueKind.Boolean => value.Boolean,
                    CellValueKind.Text => bool.Parse(value.Text.Trim()),
                    _ => throw new FormatException(),
                };
            }

            if (target == typeof(DateTime) || target == typeof(DateTimeOffset) || target == typeof(DateOnly))
            {
                DateTime date = value.Kind switch
                {
                    CellValueKind.Number => ExcelValues.ToDateTime(value.Number, sheet.Workbook.Date1904) ?? throw new FormatException(),
                    CellValueKind.Text => DateTime.Parse(value.Text, culture, DateTimeStyles.AllowWhiteSpaces),
                    _ => throw new FormatException(),
                };
                return target == typeof(DateTime) ? date : target == typeof(DateOnly) ? DateOnly.FromDateTime(date) : new DateTimeOffset(date, TimeSpan.Zero);
            }

            if (target == typeof(TimeSpan) || target == typeof(TimeOnly))
            {
                TimeSpan span = value.Kind switch
                {
                    CellValueKind.Number => TimeSpan.FromSeconds(Math.Round(value.Number * 86400, 3)),
                    CellValueKind.Text => TimeSpan.Parse(value.Text.Trim(), culture),
                    _ => throw new FormatException(),
                };
                return target == typeof(TimeSpan) ? span : TimeOnly.FromTimeSpan(span);
            }

            if (target.IsEnum)
            {
                return value.Kind switch
                {
                    CellValueKind.Text => Enum.Parse(target, value.Text.Trim(), ignoreCase: true),
                    CellValueKind.Number => Enum.ToObject(target, System.Convert.ToInt64(value.Number)),
                    _ => throw new FormatException(),
                };
            }

            if (target == typeof(Guid))
            {
                return value.Kind == CellValueKind.Text ? Guid.Parse(value.Text.Trim()) : throw new FormatException();
            }

            if (target == typeof(char))
            {
                string text = Text(row, column);
                return text.Length == 1 ? text[0] : throw new FormatException();
            }

            double number = value.Kind switch
            {
                CellValueKind.Number => value.Number,
                CellValueKind.Text => double.Parse(value.Text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, culture),
                _ => throw new FormatException(),
            };
            if (target == typeof(double))
            {
                return number;
            }

            if (target == typeof(float))
            {
                return (float)number;
            }

            if (target == typeof(decimal))
            {
                return (decimal)number;
            }

            // Whole numbers take whole values only, as a cast that drops a fraction would lose data without a word.
            if (number != Math.Floor(number))
            {
                throw new FormatException();
            }

            return System.Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
        }

        /// <summary>The type a cell's value has by itself; null for an empty cell.</summary>
        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
        private Type? Natural(int row, int column)
        {
            CellValue value = sheet.GetValue(row, column);
            switch (value.Kind)
            {
                case CellValueKind.Empty:
                    return null;
                case CellValueKind.Boolean:
                    return typeof(bool);
                case CellValueKind.Number:
                    NumberFormatCode format = NumberFormatCode.Get(sheet.Workbook.Styles[sheet.GetStyleIndex(row, column)].NumberFormat);
                    // A number with a date format that is no date, such as a negative one, stays a number.
                    return !format.IsDateTime ? typeof(double)
                        : format.IsTimeOnly ? (value.Number >= 0 && value.Number * 86400 <= TimeSpan.MaxValue.TotalSeconds ? typeof(TimeSpan) : typeof(double))
                        : ExcelValues.ToDateTime(value.Number, sheet.Workbook.Date1904) is null ? typeof(double) : typeof(DateTime);
                default:
                    return typeof(string);
            }
        }

        private string Text(int row, int column) =>
            NumberFormatCode.Get(sheet.Workbook.Styles[sheet.GetStyleIndex(row, column)].NumberFormat).Format(sheet.GetValue(row, column), culture, sheet.Workbook.Date1904);

        private bool Empty(int row)
        {
            for (int c = range.First.Column; c <= range.Last.Column; c++)
            {
                if (!sheet.GetValue(row, c).IsEmpty)
                {
                    return false;
                }
            }

            return true;
        }

        private static string Describe(CellValue value) => value.Kind switch
        {
            CellValueKind.Text => "the text \"" + (value.Text.Length > 40 ? value.Text[..40] + "..." : value.Text) + "\"",
            CellValueKind.Number => "the number " + value.Number.ToString(CultureInfo.InvariantCulture),
            CellValueKind.Boolean => value.Boolean ? "TRUE" : "FALSE",
            _ => value.ToString(),
        };
    }
}
