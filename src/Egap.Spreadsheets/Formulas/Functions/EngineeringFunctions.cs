using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Formulas;

/// <summary>Number bases, bits, complex numbers, unit conversion, error functions and Bessel functions.</summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> EngineeringFunctions() =>
    [
        new("BESSELI", 2, 2, Kinds.Value, (a, c) => EngBessel(a, c, EngBesselKind.I)),
        new("BESSELJ", 2, 2, Kinds.Value, (a, c) => EngBessel(a, c, EngBesselKind.J)),
        new("BESSELK", 2, 2, Kinds.Value, (a, c) => EngBessel(a, c, EngBesselKind.K)),
        new("BESSELY", 2, 2, Kinds.Value, (a, c) => EngBessel(a, c, EngBesselKind.Y)),
        new("BIN2DEC", 1, 1, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Binary, null)),
        new("BIN2HEX", 1, 2, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Binary, EngBase.Hex)),
        new("BIN2OCT", 1, 2, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Binary, EngBase.Octal)),
        new("DEC2BIN", 1, 2, Kinds.Value, (a, c) => EngFromDecimal(a, c, EngBase.Binary)),
        new("DEC2HEX", 1, 2, Kinds.Value, (a, c) => EngFromDecimal(a, c, EngBase.Hex)),
        new("DEC2OCT", 1, 2, Kinds.Value, (a, c) => EngFromDecimal(a, c, EngBase.Octal)),
        new("HEX2BIN", 1, 2, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Hex, EngBase.Binary)),
        new("HEX2DEC", 1, 1, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Hex, null)),
        new("HEX2OCT", 1, 2, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Hex, EngBase.Octal)),
        new("OCT2BIN", 1, 2, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Octal, EngBase.Binary)),
        new("OCT2DEC", 1, 1, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Octal, null)),
        new("OCT2HEX", 1, 2, Kinds.Value, (a, c) => EngFromDigits(a, c, EngBase.Octal, EngBase.Hex)),
        new("BITAND", 2, 2, Kinds.Value, (a, c) => EngBits(a, c, static (x, y) => x & y)),
        new("BITOR", 2, 2, Kinds.Value, (a, c) => EngBits(a, c, static (x, y) => x | y)),
        new("BITXOR", 2, 2, Kinds.Value, (a, c) => EngBits(a, c, static (x, y) => x ^ y)),
        new("BITLSHIFT", 2, 2, Kinds.Value, (a, c) => EngShift(a, c, left: true)),
        new("BITRSHIFT", 2, 2, Kinds.Value, (a, c) => EngShift(a, c, left: false)),
        new("DELTA", 1, 2, Kinds.Value, (a, c) => EngStep(a, c, static (x, y) => x == y)),
        new("GESTEP", 1, 2, Kinds.Value, (a, c) => EngStep(a, c, static (x, y) => x >= y)),
        new("ERF", 1, 2, Kinds.Value, EngErfFunction),
        new("ERF.PRECISE", 1, 1, Kinds.Value, (a, c) => EngNumber(a[0], c, out double x, out EvalValue e) ? Number(EngErf(x)) : e),
        new("ERFC", 1, 1, Kinds.Value, (a, c) => EngNumber(a[0], c, out double x, out EvalValue e) ? Number(EngFlush(EngErfc(x))) : e),
        new("ERFC.PRECISE", 1, 1, Kinds.Value, (a, c) => EngNumber(a[0], c, out double x, out EvalValue e) ? Number(EngFlush(EngErfc(x))) : e),
        new("CONVERT", 3, 3, Kinds.Value, EngConvert),
        new("COMPLEX", 2, 3, Kinds.Value, EngComplexFunction),
        new("IMABS", 1, 1, Kinds.Value, (a, c) => EngComplexNumber(a, c, static z => EngSize(z))),
        new("IMAGINARY", 1, 1, Kinds.Value, (a, c) => EngComplexNumber(a, c, static z => z.Im)),
        new("IMREAL", 1, 1, Kinds.Value, (a, c) => EngComplexNumber(a, c, static z => z.Re)),
        new("IMARGUMENT", 1, 1, Kinds.Value, EngArgument),
        new("IMCONJUGATE", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, static z => new EngComplex(z.Re, -z.Im))),
        new("IMCOS", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngCos)),
        new("IMCOSH", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngCosh)),
        new("IMCOT", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngCot)),
        new("IMCSC", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngCsc)),
        new("IMCSCH", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngCsch)),
        new("IMEXP", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngExp)),
        new("IMLN", 1, 1, Kinds.Value, (a, c) => EngComplexLog(a, c, 1)),
        new("IMLOG10", 1, 1, Kinds.Value, (a, c) => EngComplexLog(a, c, Math.Log(10))),
        new("IMLOG2", 1, 1, Kinds.Value, (a, c) => EngComplexLog(a, c, Math.Log(2))),
        new("IMSEC", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngSec)),
        new("IMSECH", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngSech)),
        new("IMSIN", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngSin)),
        new("IMSINH", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngSinh)),
        new("IMSQRT", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngSqrt)),
        new("IMTAN", 1, 1, Kinds.Value, (a, c) => EngComplexUnary(a, c, EngTan)),
        new("IMDIV", 2, 2, Kinds.Value, EngDivideFunction),
        new("IMPOWER", 2, 2, Kinds.Value, EngPower),
        new("IMSUB", 2, 2, Kinds.Value, EngSubtract),
        new("IMSUM", 1, 255, Kinds.Any, (a, c) => EngFold(a, c, product: false)),
        new("IMPRODUCT", 1, 255, Kinds.Any, (a, c) => EngFold(a, c, product: true)),
    ];

    /// <summary>
    /// A number argument as the functions that began in Excel's Analysis ToolPak read it: TRUE and FALSE are #VALUE!, an
    /// argument left empty is #N/A, an empty cell is 0.
    /// </summary>
    private static bool EngNumber(in EvalValue value, EvalContext context, out double number, out EvalValue error)
    {
        if (value.Kind == EvalKind.Boolean)
        {
            number = 0;
            error = Error(CellError.Value);
            return false;
        }

        if (value.Kind == EvalKind.Missing)
        {
            number = 0;
            error = Error(CellError.NotAvailable);
            return false;
        }

        return Num(value, context, out number, out error);
    }

    /// <summary>A result too small for a double's full precision is 0 in Excel, which calculates with such numbers flushed to zero.</summary>
    private static double EngFlush(double number) => Math.Abs(number) < 2.2250738585072014E-308 ? 0 : number;

    // ---- Number bases ----

    private enum EngBase
    {
        Binary,
        Octal,
        Hex,
    }

    private static int EngRadix(EngBase radix) => radix switch
    {
        EngBase.Binary => 2,
        EngBase.Octal => 8,
        _ => 16,
    };

    /// <summary>How many bits the ten digits of a base hold in two's complement.</summary>
    private static int EngBitCount(EngBase radix) => radix switch
    {
        EngBase.Binary => 10,
        EngBase.Octal => 30,
        _ => 40,
    };

    /// <summary>BIN2DEC and the other functions from digits: ten digits at most, the top bit of ten meaning a negative number.</summary>
    private static EvalValue EngFromDigits(EvalValue[] arguments, EvalContext context, EngBase from, EngBase? to)
    {
        if (!EngDigits(arguments[0], from, out long value, out EvalValue error))
        {
            return error;
        }

        return to is { } target ? EngToDigits(value, target, arguments, context) : Number(value);
    }

    private static bool EngDigits(in EvalValue argument, EngBase radix, out long value, out EvalValue error)
    {
        value = 0;
        error = default;
        string text;
        switch (argument.Kind)
        {
            case EvalKind.Text:
                text = argument.Text;
                break;
            case EvalKind.Number:
                // A number is read by its digits, so 1E5 is the digits 100000 and 1,5 has a comma that no base has.
                if (argument.Number != Math.Floor(argument.Number) || argument.Number < 0 || argument.Number >= 1e15)
                {
                    error = Error(CellError.Number);
                    return false;
                }

                text = argument.Number.ToString("F0", CultureInfo.InvariantCulture);
                break;
            case EvalKind.Blank:
                text = "";
                break;
            case EvalKind.Error:
                error = argument;
                return false;
            case EvalKind.Missing:
                error = Error(CellError.NotAvailable);
                return false;
            default:
                error = Error(CellError.Value);
                return false;
        }

        if (text.Length > 10)
        {
            error = Error(CellError.Number);
            return false;
        }

        int size = EngRadix(radix);
        foreach (char c in text)
        {
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 99,
            };
            if (digit >= size)
            {
                error = Error(CellError.Number);
                return false;
            }

            value = (value * size) + digit;
        }

        int bits = EngBitCount(radix);
        if (value >= 1L << (bits - 1))
        {
            value -= 1L << bits;
        }

        return true;
    }

    private static EvalValue EngFromDecimal(EvalValue[] arguments, EvalContext context, EngBase to)
    {
        if (!EngNumber(arguments[0], context, out double number, out EvalValue error))
        {
            return error;
        }

        number = Math.Truncate(number);
        return Math.Abs(number) > 1e15 ? Error(CellError.Number) : EngToDigits((long)number, to, arguments, context);
    }

    /// <summary>A number in a base: zeros in front up to the places asked for; a negative number has ten digits whatever the places.</summary>
    private static EvalValue EngToDigits(long value, EngBase radix, EvalValue[] arguments, EvalContext context)
    {
        double places = -1;
        if (arguments.Length > 1 && arguments[1].Kind != EvalKind.Missing)
        {
            if (!EngNumber(arguments[1], context, out places, out EvalValue error))
            {
                return error;
            }

            places = Math.Truncate(places);
            if (places is < 1 or > 10)
            {
                return Error(CellError.Number);
            }
        }

        long limit = 1L << (EngBitCount(radix) - 1);
        if (value >= limit || value < -limit)
        {
            return Error(CellError.Number);
        }

        long unsigned = value >= 0 ? value : value + (2 * limit);
        string text = EngDigitText(unsigned, EngRadix(radix));
        if (value < 0 || places < 0)
        {
            return EvalValue.FromText(text);
        }

        return text.Length > places ? Error(CellError.Number) : EvalValue.FromText(text.PadLeft((int)places, '0'));
    }

    private static string EngDigitText(long value, int radix)
    {
        if (value == 0)
        {
            return "0";
        }

        Span<char> digits = stackalloc char[64];
        int at = digits.Length;
        while (value > 0)
        {
            digits[--at] = "0123456789ABCDEF"[(int)(value % radix)];
            value /= radix;
        }

        return new string(digits[at..]);
    }

    // ---- Bits ----

    private const double EngBitLimit = 281474976710656; // 2^48

    private static bool EngBitNumber(in EvalValue argument, EvalContext context, out long bits, out EvalValue error)
    {
        bits = 0;
        if (!Num(argument, context, out double number, out error))
        {
            return false;
        }

        if (number < 0 || number >= EngBitLimit || number != Math.Floor(number))
        {
            error = Error(CellError.Number);
            return false;
        }

        bits = (long)number;
        return true;
    }

    private static EvalValue EngBits(EvalValue[] arguments, EvalContext context, Func<long, long, long> function) =>
        EngBitNumber(arguments[0], context, out long a, out EvalValue error) && EngBitNumber(arguments[1], context, out long b, out error)
            ? Number(function(a, b))
            : error;

    private static EvalValue EngShift(EvalValue[] arguments, EvalContext context, bool left)
    {
        if (!EngBitNumber(arguments[0], context, out long bits, out EvalValue error) || !Num(arguments[1], context, out double shift, out error))
        {
            return error;
        }

        shift = Math.Truncate(shift);
        if (bits == 0)
        {
            return Number(0);
        }

        if (Math.Abs(shift) > 53)
        {
            return Error(CellError.Number);
        }

        int by = (int)(left ? shift : -shift);
        if (by <= 0)
        {
            return Number(by <= -48 ? 0 : bits >> -by);
        }

        // A shift past the 48 bits leaves nothing, found with Excel; a shorter one that loses a bit is #NUM!.
        if (by >= 49)
        {
            return Number(0);
        }

        double shifted = Math.ScaleB((double)bits, by);
        return shifted >= EngBitLimit ? Error(CellError.Number) : Number(shifted);
    }

    private static EvalValue EngStep(EvalValue[] arguments, EvalContext context, Func<double, double, bool> test)
    {
        if (!EngNumber(arguments[0], context, out double a, out EvalValue error))
        {
            return error;
        }

        double b = 0;
        if (arguments.Length > 1 && arguments[1].Kind != EvalKind.Missing && !EngNumber(arguments[1], context, out b, out error))
        {
            return error;
        }

        return Number(test(a, b) ? 1 : 0);
    }

    // ---- The error function ----

    private static EvalValue EngErfFunction(EvalValue[] arguments, EvalContext context)
    {
        if (!EngNumber(arguments[0], context, out double lower, out EvalValue error))
        {
            return error;
        }

        if (arguments.Length == 1)
        {
            return Number(EngErf(lower));
        }

        if (!EngNumber(arguments[1], context, out double upper, out error))
        {
            return error;
        }

        return Number(EngErf(upper) - EngErf(lower));
    }

    private static double EngErf(double x)
    {
        double size = Math.Abs(x);
        double result = size < 1.5 ? EngErfSeries(size) : 1 - EngErfcFraction(size);
        return x < 0 ? -result : result;
    }

    private static double EngErfc(double x)
    {
        if (x < 0)
        {
            return 2 - EngErfc(-x);
        }

        return x < 1.5 ? 1 - EngErfSeries(x) : EngErfcFraction(x);
    }

    /// <summary>erf by its series with all terms positive: 2/√π e^(-x²) Σ 2^k x^(2k+1) / (2k+1)!!.</summary>
    private static double EngErfSeries(double x)
    {
        double term = x, sum = x, square = x * x;
        for (int k = 1; k < 500; k++)
        {
            term *= 2 * square / ((2 * k) + 1);
            sum += term;
            if (term < sum * 1e-18)
            {
                break;
            }
        }

        return 2 / Math.Sqrt(Math.PI) * Math.Exp(-square) * sum;
    }

    /// <summary>erfc for x of 1.5 and more, by its continued fraction: e^(-x²)/√π · 1/(x + (1/2)/(x + (2/2)/(x + (3/2)/(x + ...)))).</summary>
    private static double EngErfcFraction(double x)
    {
        double fraction = x;
        for (int k = 1000; k >= 1; k--)
        {
            fraction = x + (k / 2.0 / fraction);
        }

        return Math.Exp(-x * x) / Math.Sqrt(Math.PI) / fraction;
    }

    // ---- Bessel functions ----

    private enum EngBesselKind
    {
        I,
        J,
        K,
        Y,
    }

    /// <summary>The largest x Excel takes for J and Y, past which it gives #NUM!.</summary>
    private const double EngBesselLimit = 134217728;

    private const double EngEuler = 0.57721566490153286;

    private static EvalValue EngBessel(EvalValue[] arguments, EvalContext context, EngBesselKind kind)
    {
        if (!EngNumber(arguments[0], context, out double x, out EvalValue error) || !EngNumber(arguments[1], context, out double order, out error))
        {
            return error;
        }

        if (order < 0 || order >= 2147483648)
        {
            return Error(CellError.Number);
        }

        int n = (int)Math.Truncate(order);
        switch (kind)
        {
            case EngBesselKind.J:
                return Math.Abs(x) >= EngBesselEdge(n) ? Error(CellError.Number) : Number(EngBesselJ(x, n));
            case EngBesselKind.Y:
                return x <= 0 || x >= EngBesselEdge(n) ? Error(CellError.Number) : Number(EngBesselY(x, n));
            case EngBesselKind.I:
                return Number(EngBesselI(x, n));
            default:
                return x <= 0 ? Error(CellError.Number) : Number(EngFlush(EngBesselK(x, n)));
        }
    }

    /// <summary>The largest x of J and Y: order 1 turns its x back by π/2 before the limit applies, and the other orders need order 0.</summary>
    private static double EngBesselEdge(int order) => order == 1 ? EngBesselLimit + (Math.PI / 2) : EngBesselLimit;

    /// <summary>ln of n!, exact by product for small n, by Stirling's series beyond.</summary>
    private static double EngLogFactorial(int n)
    {
        if (n < 20)
        {
            double product = 1;
            for (int i = 2; i <= n; i++)
            {
                product *= i;
            }

            return Math.Log(product);
        }

        double z = n + 1.0, inverse = 1 / z, square = inverse * inverse;
        return ((z - 0.5) * Math.Log(z)) - z + (0.5 * Math.Log(2 * Math.PI))
            + (inverse * ((1.0 / 12) - (square * ((1.0 / 360) - (square * ((1.0 / 1260) - (square / 1680)))))));
    }

    private static double EngBesselJ(double x, int n)
    {
        if (x == 0)
        {
            return n == 0 ? 1 : 0;
        }

        double size = Math.Abs(x);
        double sign = x < 0 && n % 2 == 1 ? -1 : 1;

        // The first term of the series bounds the value when the order is past x; it underflows long before the sum could matter.
        if (n > size && (n * Math.Log(size / 2)) - EngLogFactorial(n) < -760)
        {
            return 0;
        }

        if (size > 1000 && n < size)
        {
            double j0 = EngHankel(size, 0, cosine: true), j1 = EngHankel(size, 1, cosine: true);
            if (n == 0)
            {
                return j0;
            }

            for (int k = 1; k < n; k++)
            {
                (j0, j1) = (j1, (2 * k / size * j1) - j0);
            }

            return sign * j1;
        }

        return sign * EngMiller(size, n);
    }

    /// <summary>
    /// J of order n by Miller's method: the recurrence run downward from far above the order, where it is stable, and scaled
    /// so that J0 + 2 J2 + 2 J4 + ... is 1.
    /// </summary>
    private static double EngMiller(double x, int n)
    {
        double top = Math.Max(n, x);
        int start = 2 * (int)((top + 30 + Math.Sqrt(60 * top)) / 2);
        double above = 0, current = 1e-300, found = 0, norm = 0;
        for (int k = start; k >= 1; k--)
        {
            double below = (2 * k / x * current) - above;
            above = current;
            current = below;
            if (k - 1 == n)
            {
                found = current;
            }

            if (k > 1 && (k - 1) % 2 == 0)
            {
                norm += 2 * current;
            }

            if (Math.Abs(current) > 1e250)
            {
                current *= 1e-250;
                above *= 1e-250;
                found *= 1e-250;
                norm *= 1e-250;
            }
        }

        norm += current;
        return found / norm;
    }

    /// <summary>
    /// J or Y of order 0 or 1 for large x, by Hankel's asymptotic expansion: √(2/(πx)) (P cos χ - Q sin χ) for J and
    /// √(2/(πx)) (P sin χ + Q cos χ) for Y, with χ = x - (ν/2 + 1/4)π.
    /// </summary>
    private static double EngHankel(double x, int order, bool cosine)
    {
        double mu = 4.0 * order * order, eight = 8 * x;
        double p = 1, q = 0, term = 1, smallest = 1;
        for (int k = 1; k < 200; k++)
        {
            double odd = (2 * k) - 1;
            term *= (mu - (odd * odd)) / (k * eight);
            double size = Math.Abs(term);
            if (size > smallest)
            {
                break;
            }

            smallest = size;
            switch (k % 4)
            {
                case 1:
                    q += term;
                    break;
                case 2:
                    p -= term;
                    break;
                case 3:
                    q -= term;
                    break;
                default:
                    p += term;
                    break;
            }

            if (size < 1e-18)
            {
                break;
            }
        }

        double chi = x - (((order / 2.0) + 0.25) * Math.PI);
        (double sine, double cos) = Math.SinCos(chi);
        return Math.Sqrt(2 / (Math.PI * x)) * (cosine ? (p * cos) - (q * sine) : (p * sine) + (q * cos));
    }

    private static double EngBesselY(double x, int n)
    {
        double y0, y1;
        if (x > 25)
        {
            y0 = EngHankel(x, 0, cosine: false);
            y1 = EngHankel(x, 1, cosine: false);
        }
        else
        {
            (y0, y1) = EngNeumann(x);
        }

        if (n == 0)
        {
            return y0;
        }

        for (int k = 1; k < n && double.IsFinite(y1); k++)
        {
            (y0, y1) = (y1, (2 * k / x * y1) - y0);
        }

        return y1;
    }

    /// <summary>
    /// Y0 and Y1 for x of 25 and less from Neumann's expansion in the bounded J of even order, whose terms do not cancel as
    /// the power series' do: Y0 = (2/π)((ln(x/2) + γ) J0 + 2 Σ (-1)^(k+1) J2k / k), and Y1 = -Y0'.
    /// </summary>
    private static (double Y0, double Y1) EngNeumann(double x)
    {
        int start = 2 * (int)((x + 30 + Math.Sqrt(60 * x)) / 2);
        double[] j = new double[start + 2];
        j[start] = 1e-300;
        for (int k = start; k >= 1; k--)
        {
            j[k - 1] = (2 * k / x * j[k]) - j[k + 1];
            if (Math.Abs(j[k - 1]) > 1e250)
            {
                for (int i = k - 1; i <= start; i++)
                {
                    j[i] *= 1e-250;
                }
            }
        }

        double norm = j[0];
        for (int k = 2; k <= start; k += 2)
        {
            norm += 2 * j[k];
        }

        double log = Math.Log(x / 2) + EngEuler;
        double evens = 0, odds = 0;
        for (int k = 1; 2 * k < start; k++)
        {
            double sign = k % 2 == 1 ? 1 : -1;
            evens += sign * j[2 * k] / k;
            odds += sign * (j[(2 * k) - 1] - j[(2 * k) + 1]) / k;
        }

        double j0 = j[0] / norm, j1 = j[1] / norm;
        double y0 = 2 / Math.PI * ((log * j0) + (2 * evens / norm));
        double y1 = -2 / Math.PI * ((j0 / x) - (log * j1) + (odds / norm));
        return (y0, y1);
    }

    /// <summary>I by its power series, whose terms are all positive, kept in range by carrying a logarithm alongside.</summary>
    private static double EngBesselI(double x, int n)
    {
        if (x == 0)
        {
            return n == 0 ? 1 : 0;
        }

        double size = Math.Abs(x), sign = x < 0 && n % 2 == 1 ? -1 : 1;

        // Excel works every order from I0, so where I0 overflows every order is #NUM!.
        if (size > 713.5)
        {
            return double.NaN;
        }

        double quarter = size * size / 4, term = 1, sum = 1, scale = 0;
        for (int k = 1; k < 100000; k++)
        {
            term *= quarter / (k * (double)(k + (double)n));
            sum += term;
            if (term < sum * 1e-18)
            {
                break;
            }

            if (sum > 1e250)
            {
                sum *= 1e-250;
                term *= 1e-250;
                scale += 250 * Math.Log(10);
            }
        }

        if (n == 0 && scale == 0)
        {
            return sum;
        }

        double first = n == 0 ? 0 : (n * Math.Log(size / 2)) - EngLogFactorial(n);
        return sign * Math.Exp(first + scale + Math.Log(sum));
    }

    /// <summary>K by the recurrence upward, stable for K, from K0 and K1.</summary>
    private static double EngBesselK(double x, int n)
    {
        double k0 = EngBesselKIntegral(x, 0);
        if (n == 0 || k0 == 0)
        {
            return k0;
        }

        double k1 = EngBesselKIntegral(x, 1);
        for (int k = 1; k < n && double.IsFinite(k1); k++)
        {
            (k0, k1) = (k1, k0 + EngFlush(2 * k / x * k1));
        }

        return k1;
    }

    /// <summary>
    /// K of order 0 or 1 from its integral, the integral of e^(-x cosh t) cosh(νt) from 0 up, by the trapezoid rule, which
    /// converges exponentially on an even analytic integrand.
    /// </summary>
    private static double EngBesselKIntegral(double x, int order)
    {
        if (x > 760)
        {
            return 0;
        }

        double step = Math.Min(0.1, 0.5 / Math.Sqrt(x));
        double sum = 0.5;
        for (int i = 1; i < 1000000; i++)
        {
            double t = i * step;

            // x (cosh t - 1) written as 2x sinh²(t/2), so it keeps its digits for small t.
            double half = Math.Sinh(t / 2);
            double value = Math.Exp(-2 * x * half * half);
            value = order == 0 ? value : value * Math.Cosh(t);
            sum += value;
            if (value < sum * 1e-18 && x * Math.Cosh(t) > 1)
            {
                break;
            }
        }

        return Math.Exp(-x) * sum * step;
    }

    // ---- Complex numbers ----

    /// <summary>A complex number and the letter it was written with, or none for one written without.</summary>
    private readonly record struct EngComplex(double Re, double Im, char Suffix = '\0');

    private static double EngSize(EngComplex z)
    {
        double square = (z.Re * z.Re) + (z.Im * z.Im);
        if (square is > 1e-280 and < 1e280)
        {
            return Math.Sqrt(square);
        }

        double larger = Math.Max(Math.Abs(z.Re), Math.Abs(z.Im));
        if (larger == 0)
        {
            return 0;
        }

        double ratio = Math.Min(Math.Abs(z.Re), Math.Abs(z.Im)) / larger;
        return larger * Math.Sqrt(1 + (ratio * ratio));
    }

    /// <summary>
    /// π/2 as Excel's complex functions have it, which is the 64 bits of a floating-point unit's π. It shows where a sine or
    /// cosine should be 0, as in IMSQRT("-1"): the high and low parts of a number held in two doubles.
    /// </summary>
    private static readonly EngExact EngHalfPi = new(Math.PI / 2, 6.1257422745431E-17);

    /// <summary>A number held to twice a double's digits, as the sum of two.</summary>
    private readonly record struct EngExact(double Hi, double Lo)
    {
        public static EngExact operator +(EngExact a, EngExact b)
        {
            double sum = a.Hi + b.Hi, part = sum - a.Hi;
            return Tidy(sum, (a.Hi - (sum - part)) + (b.Hi - part) + a.Lo + b.Lo);
        }

        public static EngExact operator -(EngExact a) => new(-a.Hi, -a.Lo);

        public static EngExact operator *(EngExact a, EngExact b)
        {
            double product = a.Hi * b.Hi;
            return Tidy(product, Math.FusedMultiplyAdd(a.Hi, b.Hi, -product) + (a.Hi * b.Lo) + (a.Lo * b.Hi));
        }

        public static EngExact operator /(EngExact a, double divisor)
        {
            double quotient = a.Hi / divisor;
            return Tidy(quotient, (a.Lo - Math.FusedMultiplyAdd(quotient, divisor, -a.Hi)) / divisor);
        }

        private static EngExact Tidy(double high, double low)
        {
            double sum = high + low;
            return new EngExact(sum, low - (sum - high));
        }
    }

    /// <summary>Sine and cosine to a double's last digit, by their series, after taking off multiples of that π/2.</summary>
    private static (double Sine, double Cosine) EngSinCos(double angle)
    {
        if (!(Math.Abs(angle) < 1e15))
        {
            return (double.NaN, double.NaN);
        }

        double quarters = Math.Round(angle / (Math.PI / 2));
        EngExact rest = new EngExact(angle, 0) + -(new EngExact(quarters, 0) * EngHalfPi);
        EngExact term = new(1, 0), sine = default, cosine = new(1, 0);
        for (int n = 1; n <= 28; n++)
        {
            term = term * rest / n;
            switch (n % 4)
            {
                case 1:
                    sine += term;
                    break;
                case 2:
                    cosine += -term;
                    break;
                case 3:
                    sine += -term;
                    break;
                default:
                    cosine += term;
                    break;
            }
        }

        return (((long)quarters % 4 + 4) % 4) switch
        {
            0 => (sine.Hi, cosine.Hi),
            1 => (cosine.Hi, -sine.Hi),
            2 => (-sine.Hi, -cosine.Hi),
            _ => (-cosine.Hi, sine.Hi),
        };
    }

    private static double EngSine(double angle) => EngSinCos(angle).Sine;

    private static double EngCosine(double angle) => EngSinCos(angle).Cosine;

    /// <summary>An angle as a complex function takes it: past 2^27 there is no answer.</summary>
    private static double EngAngle(double angle) => Math.Abs(angle) >= EngBesselLimit ? double.NaN : angle;

    /// <summary>The size as the functions that go on to a logarithm or a root work it out, so it overflows and underflows on the way.</summary>
    private static double EngSquare(EngComplex z) => (z.Re * z.Re) + (z.Im * z.Im);

    private static bool EngParseComplex(in EvalValue argument, EvalContext context, out EngComplex z, out EvalValue error)
    {
        z = default;
        error = default;
        string text;
        switch (argument.Kind)
        {
            case EvalKind.Number:
                z = new EngComplex(argument.Number, 0);
                return true;
            case EvalKind.Blank:
                return true;
            case EvalKind.Text:
                text = argument.Text;
                break;
            case EvalKind.Error:
                error = argument;
                return false;
            case EvalKind.Missing:
                error = Error(CellError.NotAvailable);
                return false;
            default:
                error = Error(CellError.Value);
                return false;
        }

        if (EngReadComplex(text.Trim(), context, out z))
        {
            return true;
        }

        error = Error(CellError.Number);
        return false;
    }

    /// <summary>
    /// Reads complex text: a real part, then an imaginary part ending in i or j, either alone. Each part is read as Excel reads
    /// any text as a number, so spaces and the locale's separators are allowed and a date is its serial number.
    /// </summary>
    private static bool EngReadComplex(string text, EvalContext context, out EngComplex z)
    {
        z = default;
        if (text.Length == 0)
        {
            return false;
        }

        // The parts divide at the first sign that is not at the start and not an exponent's.
        int split = -1;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] is '+' or '-' && text[i - 1] is not ('e' or 'E' or '+' or '-'))
            {
                split = i;
                break;
            }
        }

        if (split < 0)
        {
            char last = text[^1];
            if (last is 'i' or 'j')
            {
                if (!EngImaginaryPart(text, 1, context, out double only))
                {
                    return false;
                }

                z = new EngComplex(0, only, last);
                return true;
            }

            if (!EngPart(text, context, out double real))
            {
                return false;
            }

            z = new EngComplex(real, 0);
            return true;
        }

        string second = text[split..].TrimEnd();
        char suffix = second[^1];
        if (suffix is not ('i' or 'j') || !EngPart(text[..split], context, out double re) || !EngImaginaryPart(second, 2, context, out double im))
        {
            return false;
        }

        z = new EngComplex(re, im, suffix);
        return true;
    }

    /// <summary>A part read as a number: not one Excel could not enter, from 1E308 up, and not two separators read as a date.</summary>
    private static bool EngPart(string text, EvalContext context, out double number)
    {
        number = 0;
        string separator = context.Culture.NumberFormat.NumberDecimalSeparator;
        int first = text.IndexOf(separator, StringComparison.Ordinal);
        if (first >= 0 && text.IndexOf(separator, first + 1, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        return ExcelValues.TryParse(text, context, out number) && Math.Abs(number) < 1e308;
    }

    /// <summary>An imaginary part with its letter: the signs allowed in front, and a bare letter means 1.</summary>
    private static bool EngImaginaryPart(string text, int signs, EvalContext context, out double number)
    {
        number = 0;
        string digits = text[..^1].Trim();
        double sign = 1;
        int skip = 0;
        while (skip < digits.Length && skip < signs && digits[skip] is '+' or '-')
        {
            sign *= digits[skip] == '-' ? -1 : 1;
            skip++;
        }

        digits = digits[skip..].Trim();
        if (digits.Length == 0)
        {
            number = sign;
            return true;
        }

        if (digits[0] is '+' or '-' || !EngPart(digits, context, out double size))
        {
            return false;
        }

        number = sign * size;
        return true;
    }

    /// <summary>A part of a complex number as text: 15 significant digits, written out in full when that takes no more than 21 characters.</summary>
    private static string EngPartText(double number, CultureInfo culture)
    {
        if (number == 0)
        {
            return "0";
        }

        string text = number.ToString("G15", CultureInfo.InvariantCulture);
        int exponent = text.IndexOf('E', StringComparison.Ordinal);
        if (exponent >= 0)
        {
            int power = int.Parse(text.AsSpan(exponent + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            string sign = number < 0 ? "-" : "";
            string digits = text[sign.Length..exponent].Replace(".", "", StringComparison.Ordinal);
            if (power >= 0 && power < 21)
            {
                text = sign + digits.PadRight(power + 1, '0');
            }
            else if (power < 0 && 1 - power + digits.Length <= 21)
            {
                text = sign + "0." + new string('0', -power - 1) + digits;
            }
        }

        string separator = culture.NumberFormat.NumberDecimalSeparator;
        return separator == "." ? text : text.Replace(".", separator, StringComparison.Ordinal);
    }

    private static EvalValue EngComplexText(in EngComplex z, EvalContext context, char suffix)
    {
        // A number that 15 digits round past the largest double has no text.
        if (!(Math.Abs(z.Re) < 1.797693134862315E+308) || !(Math.Abs(z.Im) < 1.797693134862315E+308))
        {
            return Error(CellError.Number);
        }

        CultureInfo culture = context.Culture;
        if (z.Im == 0)
        {
            return EvalValue.FromText(EngPartText(z.Re, culture));
        }

        StringBuilder text = new();
        if (z.Re != 0)
        {
            text.Append(EngPartText(z.Re, culture));
            if (z.Im > 0)
            {
                text.Append('+');
            }
        }

        string part = EngPartText(z.Im, culture);
        if (part == "1")
        {
            part = "";
        }
        else if (part == "-1")
        {
            part = "-";
        }

        text.Append(part).Append(suffix);
        return EvalValue.FromText(text.ToString());
    }

    private static char EngSuffixOf(in EngComplex z) => z.Suffix == '\0' ? 'i' : z.Suffix;

    private static EvalValue EngComplexFunction(EvalValue[] arguments, EvalContext context)
    {
        if (!EngNumber(arguments[0], context, out double re, out EvalValue error) || !EngNumber(arguments[1], context, out double im, out error))
        {
            return error;
        }

        char suffix = 'i';
        if (arguments.Length > 2 && arguments[2].Kind != EvalKind.Missing)
        {
            EvalValue letter = arguments[2];
            if (letter.IsError)
            {
                return letter;
            }

            if (letter.Kind == EvalKind.Text && letter.Text == "j")
            {
                suffix = 'j';
            }
            else if (!(letter.Kind == EvalKind.Blank || (letter.Kind == EvalKind.Text && letter.Text is "" or "i")))
            {
                return Error(CellError.Value);
            }
        }

        return EngComplexText(new EngComplex(re, im), context, suffix);
    }

    private static EvalValue EngComplexNumber(EvalValue[] arguments, EvalContext context, Func<EngComplex, double> function) =>
        EngParseComplex(arguments[0], context, out EngComplex z, out EvalValue error) ? Number(function(z)) : error;

    private static EvalValue EngArgument(EvalValue[] arguments, EvalContext context)
    {
        if (!EngParseComplex(arguments[0], context, out EngComplex z, out EvalValue error))
        {
            return error;
        }

        return z.Re == 0 && z.Im == 0 ? Error(CellError.DivisionByZero) : Number(Math.Atan2(z.Im, z.Re));
    }

    private static EvalValue EngComplexUnary(EvalValue[] arguments, EvalContext context, Func<EngComplex, EngComplex> function) =>
        EngParseComplex(arguments[0], context, out EngComplex z, out EvalValue error)
            ? EngComplexText(function(z), context, EngSuffixOf(z))
            : error;

    private static EvalValue EngComplexLog(EvalValue[] arguments, EvalContext context, double divisor)
    {
        if (!EngParseComplex(arguments[0], context, out EngComplex z, out EvalValue error))
        {
            return error;
        }

        if (z.Re == 0 && z.Im == 0)
        {
            return Error(CellError.Number);
        }

        double re = 0.5 * Math.Log(EngSquare(z)), im = Math.Atan2(z.Im, z.Re);
        return EngComplexText(new EngComplex(re / divisor, im / divisor), context, EngSuffixOf(z));
    }

    // Excel's hyperbolic parts differ by function, as its last digits and noise for small numbers show.
    private static double EngExpSinh(double x) => (Math.Exp(x) - (1 / Math.Exp(x))) / 2;

    private static double EngExpCosh(double x) => (Math.Exp(x) + (1 / Math.Exp(x))) / 2;

    private static EngComplex EngCos(EngComplex z)
    {
        (double sine, double cosine) = EngSinCos(EngAngle(z.Re));
        return new EngComplex(cosine * EngExpCosh(z.Im), -sine * EngExpSinh(z.Im));
    }

    private static EngComplex EngSin(EngComplex z)
    {
        (double sine, double cosine) = EngSinCos(EngAngle(z.Re));
        return new EngComplex(sine * EngExpCosh(z.Im), cosine * EngExpSinh(z.Im));
    }

    private static EngComplex EngCosh(EngComplex z)
    {
        (double sine, double cosine) = EngSinCos(EngAngle(z.Im));
        return new EngComplex(Math.Cosh(z.Re) * cosine, Math.Sinh(z.Re) * sine);
    }

    private static EngComplex EngSinh(EngComplex z)
    {
        (double sine, double cosine) = EngSinCos(EngAngle(z.Im));
        return new EngComplex(Math.Sinh(z.Re) * cosine, Math.Cosh(z.Re) * sine);
    }

    private static EngComplex EngTan(EngComplex z)
    {
        if (z.Im == 0)
        {
            (double s, double c) = EngSinCos(EngAngle(z.Re));
            return new EngComplex(s / c, 0);
        }

        double hyperbolic = Math.Cosh(2 * z.Im);
        if (double.IsInfinity(hyperbolic))
        {
            return new EngComplex(0, Math.Sign(z.Im));
        }

        (double sine, double cosine) = EngSinCos(2 * EngAngle(z.Re));
        double divisor = cosine + hyperbolic;
        return new EngComplex(sine / divisor, Math.Sinh(2 * z.Im) / divisor);
    }

    private static EngComplex EngCot(EngComplex z)
    {
        if (z.Im == 0)
        {
            if (double.IsNaN(EngAngle(z.Re)))
            {
                return default;
            }

            (double s, double c) = EngSinCos(z.Re);
            return new EngComplex(c / s, 0);
        }

        double hyperbolic = Math.Cosh(2 * z.Im);
        if (double.IsInfinity(hyperbolic))
        {
            return new EngComplex(0, -Math.Sign(z.Im));
        }

        (double sine, double cosine) = EngSinCos(2 * EngAngle(z.Re));
        double divisor = hyperbolic - cosine;
        return new EngComplex(sine / divisor, -Math.Sinh(2 * z.Im) / divisor);
    }

    private static EngComplex EngSec(EngComplex z)
    {
        double hyperbolic = Math.Cosh(2 * z.Im);
        if (double.IsInfinity(hyperbolic))
        {
            return default;
        }

        double angle = EngAngle(z.Re);
        (double sine, double cosine) = EngSinCos(angle);
        if (z.Im == 0)
        {
            return new EngComplex(1 / cosine, 0);
        }

        double divisor = EngCosine(2 * angle) + hyperbolic;
        return new EngComplex(2 * cosine * Math.Cosh(z.Im) / divisor, 2 * sine * Math.Sinh(z.Im) / divisor);
    }

    private static EngComplex EngCsc(EngComplex z)
    {
        double angle = EngAngle(z.Re);
        (double sine, double cosine) = EngSinCos(angle);
        if (z.Im == 0)
        {
            return new EngComplex(1 / sine, 0);
        }

        double hyperbolic = Math.Cosh(2 * z.Im);
        if (double.IsInfinity(hyperbolic))
        {
            return default;
        }

        double divisor = (hyperbolic - EngCosine(2 * angle)) / 2;
        return new EngComplex(sine * (Math.Cosh(z.Im) / divisor), -cosine * (Math.Sinh(z.Im) / divisor));
    }

    private static EngComplex EngSech(EngComplex z)
    {
        if (double.IsInfinity(Math.Cosh(z.Re)))
        {
            return default;
        }

        EngComplex cosh = EngCosh(z);
        double square = EngSquare(cosh);
        return new EngComplex(cosh.Re / square, -cosh.Im / square);
    }

    private static EngComplex EngCsch(EngComplex z)
    {
        double hyperbolic = Math.Cosh(2 * z.Re);
        if (double.IsInfinity(hyperbolic))
        {
            return default;
        }

        double angle = EngAngle(z.Im);
        (double sine, double cosine) = EngSinCos(angle);
        double divisor = (hyperbolic - EngCosine(2 * angle)) / 2;
        return new EngComplex(Math.Sinh(z.Re) * cosine / divisor, -Math.Cosh(z.Re) * sine / divisor);
    }

    private static EngComplex EngExp(EngComplex z)
    {
        // Excel holds e^x at the largest number rather than overflow.
        double size = Math.Min(Math.Exp(z.Re), double.MaxValue);
        (double sine, double cosine) = EngSinCos(EngAngle(z.Im));
        return new EngComplex(size * cosine, size * sine);
    }

    private static EngComplex EngSqrt(EngComplex z)
    {
        double size = Math.Sqrt(Math.Sqrt(EngSquare(z)));
        (double sine, double cosine) = EngSinCos(Math.Atan2(z.Im, z.Re) / 2);
        return new EngComplex(size * cosine, size * sine);
    }

    /// <summary>a / b by Smith's method, which does not overflow in the middle; 0 when b is past the largest number.</summary>
    private static EngComplex EngDivide(EngComplex a, EngComplex b)
    {
        if (double.IsInfinity(b.Re) || double.IsInfinity(b.Im))
        {
            return default;
        }

        if (Math.Abs(b.Re) >= Math.Abs(b.Im))
        {
            double ratio = b.Im / b.Re, divisor = b.Re + (b.Im * ratio);
            return new EngComplex((a.Re + (a.Im * ratio)) / divisor, (a.Im - (a.Re * ratio)) / divisor);
        }
        else
        {
            double ratio = b.Re / b.Im, divisor = (b.Re * ratio) + b.Im;
            return new EngComplex(((a.Re * ratio) + a.Im) / divisor, ((a.Im * ratio) - a.Re) / divisor);
        }
    }

    private static EvalValue EngDivideFunction(EvalValue[] arguments, EvalContext context)
    {
        if (!EngParseComplex(arguments[0], context, out EngComplex a, out EvalValue error) || !EngParseComplex(arguments[1], context, out EngComplex b, out error))
        {
            return error;
        }

        if (!EngSameSuffix(a, b, out char suffix))
        {
            return Error(CellError.Number);
        }

        return b.Re == 0 && b.Im == 0 ? Error(CellError.Number) : EngComplexText(EngDivide(a, b), context, suffix);
    }

    private static EvalValue EngSubtract(EvalValue[] arguments, EvalContext context)
    {
        if (!EngParseComplex(arguments[0], context, out EngComplex a, out EvalValue error) || !EngParseComplex(arguments[1], context, out EngComplex b, out error))
        {
            return error;
        }

        return EngSameSuffix(a, b, out char suffix)
            ? EngComplexText(new EngComplex(a.Re - b.Re, a.Im - b.Im), context, suffix)
            : Error(CellError.Number);
    }

    private static bool EngSameSuffix(in EngComplex a, in EngComplex b, out char suffix)
    {
        suffix = a.Suffix == '\0' ? EngSuffixOf(b) : a.Suffix;
        return a.Suffix == '\0' || b.Suffix == '\0' || a.Suffix == b.Suffix;
    }

    private static EvalValue EngPower(EvalValue[] arguments, EvalContext context)
    {
        if (!EngParseComplex(arguments[0], context, out EngComplex z, out EvalValue error) || !EngNumber(arguments[1], context, out double power, out error))
        {
            return error;
        }

        if (z.Re == 0 && z.Im == 0)
        {
            return power > 0 ? EngComplexText(default, context, 'i') : Error(CellError.Number);
        }

        // A negative power is one over the positive one, as Excel's last digits show.
        double radius = Math.Sqrt(EngSquare(z));
        double size = power < 0 ? 1 / Math.Pow(radius, -power) : Math.Pow(radius, power);
        (double sine, double cosine) = EngSinCos(Math.Atan2(z.Im, z.Re) * power);
        return EngComplexText(new EngComplex(size * cosine, size * sine), context, EngSuffixOf(z));
    }

    /// <summary>IMSUM and IMPRODUCT: every value of every argument in turn. Past the first, an argument left empty is left out; an empty cell is 0.</summary>
    private static EvalValue EngFold(EvalValue[] arguments, EvalContext context, bool product)
    {
        bool any = false;
        EngComplex total = default;
        char suffix = '\0';
        for (int i = 0; i < arguments.Length; i++)
        {
            EvalValue argument = arguments[i];
            if (argument.Kind == EvalKind.Missing)
            {
                if (i == 0)
                {
                    return Error(CellError.NotAvailable);
                }

                continue;
            }

            List<EvalValue> values = [];
            foreach ((EvalValue value, bool _) in Scalars(argument))
            {
                values.Add(value);
            }

            if (argument.Kind == EvalKind.Reference && values.Count < argument.Areas.Sum(static area => area.Range.CellCount))
            {
                values.Add(EvalValue.Blank);
            }

            foreach (EvalValue value in values)
            {
                if (!EngParseComplex(value, context, out EngComplex z, out EvalValue error))
                {
                    return error;
                }

                if (z.Suffix != '\0')
                {
                    if (suffix != '\0' && z.Suffix != suffix)
                    {
                        return Error(CellError.Value);
                    }

                    suffix = z.Suffix;
                }

                if (!any)
                {
                    total = z;
                    any = true;
                }
                else if (product)
                {
                    total = new EngComplex((total.Re * z.Re) - (total.Im * z.Im), (total.Re * z.Im) + (total.Im * z.Re));
                }
                else
                {
                    total = new EngComplex(total.Re + z.Re, total.Im + z.Im);
                }
            }
        }

        if (product && (!double.IsFinite(total.Re) || !double.IsFinite(total.Im)))
        {
            return Error(CellError.Value);
        }

        return EngComplexText(total, context, suffix == '\0' ? 'i' : suffix);
    }

    // ---- CONVERT ----

    private enum EngUnitKind
    {
        Mass,
        Distance,
        Time,
        Pressure,
        Force,
        Energy,
        Power,
        Magnetism,
        Temperature,
        Volume,
        Area,
        Information,
        Speed,
    }

    /// <summary>A unit: its category, its size in the category's base unit, and the power a metric prefix is raised to, 0 for a unit that takes none.</summary>
    private readonly record struct EngUnit(EngUnitKind Kind, double Size, int PrefixPower = 0);

    private const double EngInch = 0.0254, EngFoot = 0.3048, EngYard = 0.9144, EngMile = 1609.344, EngNauticalMile = 1852;
    private const double EngAngstrom = 1e-10, EngLightYear = 9.4607304725808E+15, EngPicaPoint = EngInch / 72;

    private static readonly Dictionary<string, EngUnit> EngUnits = EngBuildUnits();

    private static Dictionary<string, EngUnit> EngBuildUnits()
    {
        Dictionary<string, EngUnit> units = new(StringComparer.Ordinal);
        void Add(EngUnitKind kind, double size, int prefix, params string[] names)
        {
            foreach (string name in names)
            {
                units.Add(name, new EngUnit(kind, size, prefix));
            }
        }

        // Sizes are the ones Excel calculates with, some of which are older values of the constants.
        Add(EngUnitKind.Mass, 1, 1, "g");
        Add(EngUnitKind.Mass, 14593.902937206363, 0, "sg");
        Add(EngUnitKind.Mass, 453.59237, 0, "lbm");
        Add(EngUnitKind.Mass, 1.660538782E-24, 1, "u");
        Add(EngUnitKind.Mass, 28.349523125, 0, "ozm");
        Add(EngUnitKind.Mass, 0.06479891, 0, "grain");
        Add(EngUnitKind.Mass, 45359.237, 0, "cwt", "shweight");
        Add(EngUnitKind.Mass, 50802.34544, 0, "uk_cwt", "lcwt", "hweight");
        Add(EngUnitKind.Mass, 6350.29318, 0, "stone");
        Add(EngUnitKind.Mass, 907184.74, 0, "ton");
        Add(EngUnitKind.Mass, 1016046.9088, 0, "uk_ton", "LTON", "brton");

        Add(EngUnitKind.Distance, 1, 1, "m");
        Add(EngUnitKind.Distance, EngMile, 0, "mi");
        Add(EngUnitKind.Distance, EngNauticalMile, 0, "Nmi");
        Add(EngUnitKind.Distance, EngInch, 0, "in");
        Add(EngUnitKind.Distance, EngFoot, 0, "ft");
        Add(EngUnitKind.Distance, EngYard, 0, "yd");
        Add(EngUnitKind.Distance, EngAngstrom, 1, "ang");
        Add(EngUnitKind.Distance, 1.143, 0, "ell");
        Add(EngUnitKind.Distance, EngLightYear, 1, "ly");
        Add(EngUnitKind.Distance, 30856775812815532, 1, "parsec", "pc");
        Add(EngUnitKind.Distance, EngPicaPoint, 0, "Picapt", "Pica");
        Add(EngUnitKind.Distance, EngInch / 6, 0, "pica");
        Add(EngUnitKind.Distance, 1609.3472186944373, 0, "survey_mi");

        Add(EngUnitKind.Time, 31557600, 0, "yr");
        Add(EngUnitKind.Time, 86400, 0, "day", "d");
        Add(EngUnitKind.Time, 3600, 0, "hr");
        Add(EngUnitKind.Time, 60, 0, "mn", "min");
        Add(EngUnitKind.Time, 1, 1, "sec", "s");

        Add(EngUnitKind.Pressure, 1, 1, "Pa", "p");
        Add(EngUnitKind.Pressure, 101325, 1, "atm", "at");
        Add(EngUnitKind.Pressure, 133.322, 1, "mmHg");
        Add(EngUnitKind.Pressure, 6894.7572931683617, 0, "psi");
        Add(EngUnitKind.Pressure, 101325.0 / 760, 0, "Torr");

        Add(EngUnitKind.Force, 1, 1, "N");
        Add(EngUnitKind.Force, 1e-5, 1, "dyn", "dy");
        Add(EngUnitKind.Force, 4.4482216152605005, 0, "lbf");
        Add(EngUnitKind.Force, 0.00980665, 1, "pond");

        Add(EngUnitKind.Energy, 1, 1, "J");
        Add(EngUnitKind.Energy, 1e-7, 1, "e");
        Add(EngUnitKind.Energy, 4.184, 1, "c");
        Add(EngUnitKind.Energy, 4.1868, 1, "cal");
        Add(EngUnitKind.Energy, 1.602176487E-19, 1, "eV", "ev");
        Add(EngUnitKind.Energy, 2684519.5376961729, 0, "HPh", "hh");
        Add(EngUnitKind.Energy, 3600, 1, "Wh", "wh");
        Add(EngUnitKind.Energy, 1.3558179483314003, 0, "flb");
        Add(EngUnitKind.Energy, 1055.05585262, 0, "BTU", "btu");

        Add(EngUnitKind.Power, 745.69987158227025, 0, "HP", "h");
        Add(EngUnitKind.Power, 735.49875, 0, "PS");
        Add(EngUnitKind.Power, 1, 1, "W", "w");

        Add(EngUnitKind.Magnetism, 1, 1, "T");
        Add(EngUnitKind.Magnetism, 1e-4, 1, "ga");

        // Only names here; EngTemperature has the scales.
        Add(EngUnitKind.Temperature, 1, 0, "C", "cel", "F", "fah", "Rank", "Reau");
        Add(EngUnitKind.Temperature, 1, 1, "K", "kel");

        const double Gallon = 3.785411784;
        Add(EngUnitKind.Volume, Gallon / 768, 0, "tsp");
        Add(EngUnitKind.Volume, 0.005, 0, "tspm");
        Add(EngUnitKind.Volume, Gallon / 256, 0, "tbs");
        Add(EngUnitKind.Volume, Gallon / 128, 0, "oz");
        Add(EngUnitKind.Volume, Gallon / 16, 0, "cup");
        Add(EngUnitKind.Volume, Gallon / 8, 0, "pt", "us_pt");
        Add(EngUnitKind.Volume, 0.56826125, 1, "uk_pt");
        Add(EngUnitKind.Volume, Gallon / 4, 0, "qt");
        Add(EngUnitKind.Volume, 1.1365225, 0, "uk_qt");
        Add(EngUnitKind.Volume, Gallon, 0, "gal");
        Add(EngUnitKind.Volume, 4.54609, 0, "uk_gal");
        Add(EngUnitKind.Volume, 1, 1, "l", "L", "lt");
        Add(EngUnitKind.Volume, Cubed(EngAngstrom), 3, "ang3", "ang^3");
        Add(EngUnitKind.Volume, 158.987294928, 0, "barrel");
        Add(EngUnitKind.Volume, 35.23907016688, 0, "bushel");
        Add(EngUnitKind.Volume, Cubed(EngFoot), 0, "ft3", "ft^3");
        Add(EngUnitKind.Volume, Cubed(EngInch), 0, "in3", "in^3");
        Add(EngUnitKind.Volume, Cubed(EngLightYear), 0, "ly3", "ly^3");
        Add(EngUnitKind.Volume, 1000, 3, "m3", "m^3");
        Add(EngUnitKind.Volume, Cubed(EngMile), 0, "mi3", "mi^3");
        Add(EngUnitKind.Volume, Cubed(EngYard), 0, "yd3", "yd^3");
        Add(EngUnitKind.Volume, Cubed(EngNauticalMile), 0, "Nmi3", "Nmi^3");
        Add(EngUnitKind.Volume, Cubed(EngPicaPoint), 0, "Picapt3", "Picapt^3", "Pica3", "Pica^3");
        Add(EngUnitKind.Volume, 2831.6846592, 0, "GRT", "regton");
        Add(EngUnitKind.Volume, 1132.67386368, 0, "MTON");

        Add(EngUnitKind.Area, 4046.8564224, 0, "uk_acre");
        Add(EngUnitKind.Area, 4046.8726098742522, 0, "us_acre");
        Add(EngUnitKind.Area, EngAngstrom * EngAngstrom, 2, "ang2", "ang^2");
        Add(EngUnitKind.Area, 100, 1, "ar");
        Add(EngUnitKind.Area, EngFoot * EngFoot, 0, "ft2", "ft^2");
        Add(EngUnitKind.Area, 10000, 0, "ha");
        Add(EngUnitKind.Area, EngInch * EngInch, 0, "in2", "in^2");
        Add(EngUnitKind.Area, EngLightYear * EngLightYear, 0, "ly2", "ly^2");
        Add(EngUnitKind.Area, 1, 2, "m2", "m^2");
        Add(EngUnitKind.Area, 2500, 0, "Morgen");
        Add(EngUnitKind.Area, EngMile * EngMile, 0, "mi2", "mi^2");
        Add(EngUnitKind.Area, EngNauticalMile * EngNauticalMile, 0, "Nmi2", "Nmi^2");
        Add(EngUnitKind.Area, EngPicaPoint * EngPicaPoint, 0, "Picapt2", "Picapt^2", "Pica2", "Pica^2");
        Add(EngUnitKind.Area, EngYard * EngYard, 0, "yd2", "yd^2");

        Add(EngUnitKind.Information, 1, 1, "bit");
        Add(EngUnitKind.Information, 8, 1, "byte");

        Add(EngUnitKind.Speed, 1853.184 / 3600, 0, "admkn");
        Add(EngUnitKind.Speed, EngNauticalMile / 3600.0, 0, "kn");
        Add(EngUnitKind.Speed, 1 / 3600.0, 1, "m/h", "m/hr");
        Add(EngUnitKind.Speed, 1, 1, "m/s", "m/sec");
        Add(EngUnitKind.Speed, EngMile / 3600, 1, "mph");
        return units;

        static double Cubed(double metres) => metres * metres * metres * 1000;
    }

    private static readonly (string Name, double Size)[] EngPrefixes =
    [
        ("Y", 1e24), ("Z", 1e21), ("E", 1e18), ("P", 1e15), ("T", 1e12), ("G", 1e9), ("M", 1e6), ("k", 1e3), ("h", 1e2),
        ("da", 1e1), ("e", 1e1), ("d", 1e-1), ("c", 1e-2), ("m", 1e-3), ("u", 1e-6), ("n", 1e-9), ("p", 1e-12), ("f", 1e-15),
        ("a", 1e-18), ("z", 1e-21), ("y", 1e-24),
    ];

    private static readonly (string Name, double Size)[] EngBinaryPrefixes =
    [
        ("Yi", 1208925819614629174706176.0), ("Zi", 1180591620717411303424.0), ("Ei", 1152921504606846976.0), ("Pi", 1125899906842624.0),
        ("Ti", 1099511627776.0), ("Gi", 1073741824.0), ("Mi", 1048576.0), ("ki", 1024.0),
    ];

    private static bool EngFindUnit(string name, out EngUnit unit, out double scale)
    {
        scale = 1;
        if (EngUnits.TryGetValue(name, out unit))
        {
            return true;
        }

        foreach ((string prefix, double size) in EngBinaryPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && EngUnits.TryGetValue(name[prefix.Length..], out unit) && unit.Kind == EngUnitKind.Information)
            {
                scale = size;
                return true;
            }
        }

        foreach ((string prefix, double size) in EngPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && EngUnits.TryGetValue(name[prefix.Length..], out unit) && unit.PrefixPower > 0)
            {
                scale = Math.Pow(size, unit.PrefixPower);
                return true;
            }
        }

        unit = default;
        return false;
    }

    private static EvalValue EngConvert(EvalValue[] arguments, EvalContext context)
    {
        if (arguments[0].Kind == EvalKind.Missing || arguments[1].Kind == EvalKind.Missing || arguments[2].Kind == EvalKind.Missing)
        {
            return Error(CellError.NotAvailable);
        }

        if (!EngNumber(arguments[0], context, out double number, out EvalValue error))
        {
            return error;
        }

        for (int i = 1; i <= 2; i++)
        {
            if (arguments[i].IsError)
            {
                return arguments[i];
            }

            if (arguments[i].Kind is EvalKind.Boolean)
            {
                return Error(CellError.Value);
            }
        }

        if (arguments[1].Kind != EvalKind.Text || arguments[2].Kind != EvalKind.Text
            || !EngFindUnit(arguments[1].Text, out EngUnit from, out double fromScale)
            || !EngFindUnit(arguments[2].Text, out EngUnit to, out double toScale)
            || from.Kind != to.Kind)
        {
            return Error(CellError.NotAvailable);
        }

        if (from.Kind == EngUnitKind.Temperature)
        {
            // Excel leaves out the kelvin's prefix to and from the Réaumur scale, and from it to the Rankine scale, as it finds them.
            char fromKind = EngScale(arguments[1].Text), toKind = EngScale(arguments[2].Text);
            fromScale = toKind is 'R' or 'E' ? 1 : fromScale;
            toScale = fromKind == 'E' ? 1 : toScale;
            return Number(EngTemperature(number * fromScale, arguments[1].Text, arguments[2].Text) / toScale);
        }

        return Number(EngFlush(number * (from.Size * fromScale) / (to.Size * toScale)));
    }

    /// <summary>A temperature through the kelvin: each scale is a factor and an offset.</summary>
    private static double EngTemperature(double value, string from, string to)
    {
        if (EngScale(from) == EngScale(to))
        {
            return value;
        }

        double kelvin = EngScale(from) switch
        {
            'C' => value + 273.15,
            'F' => ((value - 32) / 1.8) + 273.15,
            'R' => value / 1.8,
            'E' => (value * 1.25) + 273.15,
            _ => value,
        };
        return EngScale(to) switch
        {
            'C' => kelvin - 273.15,
            'F' => ((kelvin - 273.15) * 1.8) + 32,
            'R' => kelvin * 1.8,
            'E' => (kelvin - 273.15) / 1.25,
            _ => kelvin,
        };
    }

    /// <summary>A letter for a temperature's name, whatever the name it goes by; K for the kelvin, with or without a prefix.</summary>
    private static char EngScale(string name) => name switch
    {
        "C" or "cel" => 'C',
        "F" or "fah" => 'F',
        "Rank" => 'R',
        "Reau" => 'E',
        _ => 'K',
    };
}
