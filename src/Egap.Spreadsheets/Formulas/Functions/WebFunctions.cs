using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// ENCODEURL and FILTERXML, Excel's web functions that need no network, and the other functions that came to Excel after
/// stage D: BAHTTEXT, PERCENTOF and ECMA.CEILING. Microsoft's function reference is the source for each.
/// </summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> WebFunctions() =>
    [
        Text1("ENCODEURL", (s, c) => EvalValue.FromText(EncodeUrl(s))),
        new("FILTERXML", 2, 2, Kinds.Value, FilterXml),
        new("BAHTTEXT", 1, 1, Kinds.Value, BahtText),
        new("PERCENTOF", 2, 2, Kinds.Any, PercentOf),
        new("ECMA.CEILING", 2, 2, Kinds.Value, (a, c) => Binary(a, c, Ceiling)),
    ];

    /// <summary>ENCODEURL: the UTF-8 bytes of the text, each as %XX except letters, digits, '-', '_' and '.'.</summary>
    private static string EncodeUrl(string text)
    {
        StringBuilder result = new(text.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'_' or (byte)'.')
            {
                result.Append((char)b);
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// FILTERXML: the nodes an XPath finds in XML, one per row. An element gives its own text (not its children's), an
    /// attribute its value, read as a number, TRUE or FALSE where it reads as one; empty text, another kind of node, an
    /// XPath that finds no nodes, a DTD, or XML that is not well formed give #VALUE!. Names without a prefix match elements
    /// whatever their default namespace, as in Excel; a prefix is the one the XML declares.
    /// </summary>
    private static EvalValue FilterXml(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].IsError)
        {
            return arguments[0];
        }

        if (arguments[1].IsError)
        {
            return arguments[1];
        }

        if (arguments[0].Kind != EvalKind.Text || arguments[1].Kind != EvalKind.Text)
        {
            return Error(CellError.Value);
        }

        XDocument document;
        try
        {
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 20 };
            using StringReader text = new(arguments[0].Text);
            using XmlReader reader = XmlReader.Create(text, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return Error(CellError.Value);
        }

        XmlNamespaceManager namespaces = new(new NameTable());
        foreach (XElement element in document.Descendants().ToList())
        {
            foreach (XAttribute declaration in element.Attributes().Where(a => a.IsNamespaceDeclaration).ToList())
            {
                if (declaration.Name.Namespace == XNamespace.Xmlns)
                {
                    namespaces.AddNamespace(declaration.Name.LocalName, declaration.Value);
                }
                else
                {
                    declaration.Remove();
                }
            }
        }

        foreach (XElement element in document.Descendants().ToList())
        {
            if (element.Name.Namespace != XNamespace.None && namespaces.LookupPrefix(element.Name.NamespaceName) is null or "")
            {
                element.Name = element.Name.LocalName;
            }
        }

        object found;
        try
        {
            found = document.XPathEvaluate(arguments[1].Text, namespaces);
        }
        catch (XPathException)
        {
            return Error(CellError.Value);
        }
        catch (ArgumentException)
        {
            return Error(CellError.Value);
        }

        if (found is not IEnumerable<object> nodes)
        {
            return Error(CellError.Value);
        }

        List<EvalValue> values = [];
        foreach (object node in nodes)
        {
            string? text = node switch
            {
                XElement element => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)),
                XAttribute attribute => attribute.Value,
                _ => null,
            };
            values.Add(text is null || text.Length == 0 ? Error(CellError.Value) : TypedValue(text, context));
            if (values.Count > EvalContext.MaxArrayCells)
            {
                return Error(CellError.Number);
            }
        }

        return values.Count switch
        {
            0 => Error(CellError.Value),
            1 => values[0],
            _ => Result(values.Count, 1, [.. values]),
        };
    }

    /// <summary>Text as Excel reads it typed into a cell: a number, a date, TRUE or FALSE where it reads as one, otherwise the text.</summary>
    private static EvalValue TypedValue(string text, EvalContext context)
    {
        string trimmed = text.Trim();
        if (trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return EvalValue.True;
        }

        if (trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return EvalValue.False;
        }

        return ExcelValues.TryParse(trimmed, context, out double number) ? Number(number) : EvalValue.FromText(text);
    }

    /// <summary>PERCENTOF: the sum of a subset over the sum of all, as SUM adds each; an error in the values gives #NUM!.</summary>
    private static EvalValue PercentOf(EvalValue[] arguments, EvalContext context)
    {
        if (!PercentSum(arguments[0], context, out double part, out EvalValue error) || !PercentSum(arguments[1], context, out double whole, out error))
        {
            return error;
        }

        return whole == 0 ? Error(CellError.DivisionByZero) : Number(part / whole);
    }

    private static bool PercentSum(in EvalValue argument, EvalContext context, out double total, out EvalValue error)
    {
        total = 0;
        error = default;
        foreach ((EvalValue value, bool direct) in Scalars(argument))
        {
            switch (value.Kind)
            {
                case EvalKind.Error:
                    error = Error(CellError.Number);
                    return false;
                case EvalKind.Number:
                case EvalKind.Boolean when direct:
                    total += value.Number;
                    break;
                case EvalKind.Text when direct:
                    if (!Num(value, context, out double number, out error))
                    {
                        return false;
                    }

                    total += number;
                    break;
            }
        }

        return true;
    }

    private static readonly string[] ThaiDigits = ["ศูนย์", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า"];
    private static readonly string[] ThaiPlaces = ["", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน"];

    /// <summary>
    /// BAHTTEXT: a number as Thai text in baht and satang, rounded to the satang from its 15 significant digits: "ลบ" before
    /// a negative amount, "ถ้วน" when there are no satang.
    /// </summary>
    private static EvalValue BahtText(EvalValue[] arguments, EvalContext context)
    {
        if (!Num(arguments[0], context, out double x, out EvalValue error))
        {
            return error;
        }

        // The amount in satang, from the number's 15 significant digits, half a satang up.
        string scientific = Math.Abs(x).ToString("E14", CultureInfo.InvariantCulture);
        BigInteger digits = BigInteger.Parse(scientific[..1] + scientific[2..16], CultureInfo.InvariantCulture);
        int exponent = int.Parse(scientific[17..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) - 12;
        BigInteger satang;
        if (exponent >= 0)
        {
            satang = digits * BigInteger.Pow(10, exponent);
        }
        else if (exponent < -16)
        {
            satang = 0;
        }
        else
        {
            BigInteger scale = BigInteger.Pow(10, -exponent);
            satang = BigInteger.DivRem(digits, scale, out BigInteger rest);
            if (rest * 2 >= scale)
            {
                satang++;
            }
        }

        BigInteger baht = BigInteger.DivRem(satang, 100, out BigInteger cents);
        StringBuilder text = new();
        if (x < 0 && satang > 0)
        {
            text.Append("ลบ");
        }

        if (baht > 0)
        {
            ThaiNumber(baht, text);
            text.Append("บาท");
        }

        if (cents == 0)
        {
            text.Append(baht == 0 ? "ศูนย์บาทถ้วน" : "ถ้วน");
        }
        else
        {
            ThaiGroup((int)cents, higher: false, text);
            text.Append("สตางค์");
        }

        return EvalValue.FromText(text.ToString());
    }

    /// <summary>A whole number in Thai: groups of six digits joined by "ล้าน" (a million).</summary>
    private static void ThaiNumber(BigInteger number, StringBuilder text)
    {
        BigInteger million = 1_000_000;
        BigInteger high = BigInteger.DivRem(number, million, out BigInteger low);
        if (high > 0)
        {
            ThaiNumber(high, text);
            text.Append("ล้าน");
        }

        ThaiGroup((int)low, higher: high > 0, text);
    }

    /// <summary>
    /// Up to six digits in Thai: ten is "สิบ", twenty "ยี่สิบ", and a one in the units after anything higher is "เอ็ด".
    /// </summary>
    private static void ThaiGroup(int group, bool higher, StringBuilder text)
    {
        for (int place = 5; place >= 2; place--)
        {
            int digit = group / (int)Math.Pow(10, place) % 10;
            if (digit > 0)
            {
                text.Append(ThaiDigits[digit]).Append(ThaiPlaces[place]);
            }
        }

        int tens = group / 10 % 10, units = group % 10;
        text.Append(tens switch
        {
            0 => "",
            1 => "สิบ",
            2 => "ยี่สิบ",
            _ => ThaiDigits[tens] + "สิบ",
        });
        if (units == 1)
        {
            text.Append(group >= 10 || higher ? "เอ็ด" : "หนึ่ง");
        }
        else if (units > 0)
        {
            text.Append(ThaiDigits[units]);
        }
    }
}
