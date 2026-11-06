namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Loans, annuities, cash flows, depreciation and securities, from Microsoft's function reference and the textbook
/// formulas, with Excel's own answers (<c>Oracle/functions-financial.txt</c>) the judge of every quirk.
/// </summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> FinancialFunctions() =>
    [
        Financial("FV", 3, 5, [0, 0], static (v, _) => Number(Annuity.Future(v[0], v[1], v[2], v[3], v[4]))),
        Financial("PV", 3, 5, [0, 0], static (v, _) => Annuity.Present(v[0], v[1], v[2], v[3], v[4])),
        Financial("PMT", 3, 5, [0, 0], static (v, _) => Annuity.Payment(v[0], v[1], v[2], v[3], v[4])),
        Financial("NPER", 3, 5, [0, 0], static (v, _) => Annuity.Periods(v[0], v[1], v[2], v[3], v[4])),
        Financial("RATE", 3, 6, [0, 0, 0.1], static (v, _) => Annuity.Rate(v[0], v[1], v[2], v[3], v[4], v[5])),
        Financial("IPMT", 4, 6, [0, 0], static (v, _) => Annuity.Interest(v[0], v[1], v[2], v[3], v[4], v[5])),
        Financial("PPMT", 4, 6, [0, 0], static (v, _) => Annuity.Principal(v[0], v[1], v[2], v[3], v[4], v[5])),
        Financial("ISPMT", 4, 4, [], static (v, _) => Annuity.StraightInterest(v[0], v[1], v[2], v[3])),
        Financial("CUMIPMT", 6, 6, [], static (v, _) => Annuity.Cumulative(v, interest: true), strict: true),
        Financial("CUMPRINC", 6, 6, [], static (v, _) => Annuity.Cumulative(v, interest: false), strict: true),
        Financial("EFFECT", 2, 2, [], static (v, _) => Conversions.Effect(v[0], v[1]), strict: true),
        Financial("NOMINAL", 2, 2, [], static (v, _) => Conversions.Nominal(v[0], v[1]), strict: true),
        Financial("DOLLARDE", 2, 2, [], static (v, _) => Conversions.Dollar(v[0], v[1], toDecimal: true), strict: true),
        Financial("DOLLARFR", 2, 2, [], static (v, _) => Conversions.Dollar(v[0], v[1], toDecimal: false), strict: true),
        Financial("PDURATION", 3, 3, [], static (v, _) => Conversions.Duration(v[0], v[1], v[2])),
        Financial("RRI", 3, 3, [], static (v, _) => Conversions.EquivalentRate(v[0], v[1], v[2])),
        Financial("SLN", 3, 3, [], static (v, _) => Depreciation.Straight(v[0], v[1], v[2])),
        Financial("SYD", 4, 4, [], static (v, _) => Depreciation.SumOfYears(v[0], v[1], v[2], v[3])),
        Financial("DB", 4, 5, [12], static (v, _) => Depreciation.FixedDeclining(v[0], v[1], v[2], v[3], v[4])),
        Financial("DDB", 4, 5, [2], static (v, _) => Depreciation.DoubleDeclining(v[0], v[1], v[2], v[3], v[4])),
        new("VDB", 5, 7, Kinds.Value, Depreciation.Variable),
        new("AMORLINC", 6, 7, Kinds.Value, Depreciation.FrenchLinear),
        new("AMORDEGRC", 6, 7, Kinds.Value, Depreciation.FrenchDeclining),
        new("NPV", 2, 255, Kinds.ValueThenAny, CashFlows.NetPresentValue),
        new("IRR", 1, 2, Kinds.AnyThenValues, CashFlows.InternalRate),
        new("MIRR", 3, 3, Kinds.AnyThenValues, CashFlows.ModifiedRate),
        new("XNPV", 3, 3, Kinds.ValueThenAny, CashFlows.DatedPresentValue),
        new("XIRR", 2, 3, [ArgumentKind.Any, ArgumentKind.Any, ArgumentKind.Value], CashFlows.DatedRate),
        new("FVSCHEDULE", 2, 2, Kinds.ValueThenAny, CashFlows.Schedule),
        .. Securities.Functions(),
    ];

    /// <summary>
    /// A function of numbers only: each argument read in turn, so the first that isn't a number gives the error; the optional
    /// arguments after <paramref name="min"/> take the given fallbacks when left off. A <paramref name="strict"/> function,
    /// one that began in Excel's Analysis ToolPak, takes no booleans: they give #VALUE!.
    /// </summary>
    private static FormulaFunction Financial(string name, int min, int max, double[] fallbacks, Func<double[], EvalContext, EvalValue> body, bool strict = false) =>
        new(name, min, max, Kinds.Value, (a, c) =>
        {
            double[] values = new double[max];
            for (int i = 0; i < max; i++)
            {
                if (i < a.Length)
                {
                    if (!Strict(a[i], c, strict, out values[i], out EvalValue error))
                    {
                        return error;
                    }
                }
                else
                {
                    values[i] = fallbacks[i - min];
                }
            }

            return body(values, c);
        });

    /// <summary>A number argument; with <paramref name="strict"/> a boolean is #VALUE!.</summary>
    private static bool Strict(in EvalValue value, EvalContext context, bool strict, out double number, out EvalValue error)
    {
        if (strict && value.Kind == EvalKind.Boolean)
        {
            number = 0;
            error = Error(CellError.Value);
            return false;
        }

        return Num(value, context, out number, out error);
    }

    /// <summary>ln(1 + x), exact for small x, by Kahan's correction: .NET's double.LogP1 is the plain formula.</summary>
    private static double LogOnePlus(double x)
    {
        double y = 1 + x;
        return y == 1 ? x : Math.Log(y) - ((y - 1 - x) / y);
    }

    /// <summary>e^x - 1, exact for small x, by Kahan's correction: .NET's double.ExpM1 is the plain formula.</summary>
    private static double ExpMinusOne(double x)
    {
        double u = Math.Exp(x);
        if (u == 1)
        {
            return x;
        }

        double less = u - 1;
        return less == -1 || double.IsInfinity(u) ? less : less * x / Math.Log(u);
    }

    /// <summary>The time value of money: level payments at a rate per period.</summary>
    private static class Annuity
    {
        public static double Future(double rate, double periods, double payment, double present, double type)
        {
            if (rate == 0)
            {
                return -(present + (payment * periods));
            }

            double growth = Math.Pow(1 + rate, periods);
            return -((present * growth) + (payment * (1 + (rate * Timing(type))) * (growth - 1) / rate));
        }

        public static EvalValue Present(double rate, double periods, double payment, double future, double type)
        {
            if (rate == 0)
            {
                return Number(-(future + (payment * periods)));
            }

            double growth = Math.Pow(1 + rate, periods);
            return growth == 0
                ? Error(CellError.DivisionByZero)
                : Number(-(future + (payment * (1 + (rate * Timing(type))) * (growth - 1) / rate)) / growth);
        }

        public static EvalValue Payment(double rate, double periods, double present, double future, double type)
        {
            if (periods == 0)
            {
                return Error(CellError.Number);
            }

            return Number(Level(rate, periods, present, future, type));
        }

        private static double Level(double rate, double periods, double present, double future, double type)
        {
            if (rate == 0)
            {
                return -(present + future) / periods;
            }

            // Grown less one exactly, so very small rates stay right, and a growth too big to hold leaves the interest alone.
            double grown = ExpMinusOne(periods * LogOnePlus(rate));
            double owed = double.IsInfinity(grown) ? present : present + ((present + future) / grown);
            return -rate / (1 + (rate * Timing(type))) * owed;
        }

        /// <summary>The level payment by the textbook's formula, which the period-by-period functions build on in Excel.</summary>
        private static double Plain(double rate, double periods, double present, double future, double type)
        {
            if (rate == 0)
            {
                return -(present + future) / periods;
            }

            double discount = Math.Pow(1 + rate, -periods);
            return (-(present + (future * discount)) * rate) / ((1 + (rate * Timing(type))) * (1 - discount));
        }

        public static EvalValue Periods(double rate, double payment, double present, double future, double type)
        {
            if (rate == 0)
            {
                return payment == 0 ? Error(CellError.DivisionByZero) : Number(-(present + future) / payment);
            }

            double step = payment * (1 + (rate * Timing(type))) / rate;
            double ratio = (step - future) / (step + present);
            return ratio > 0 && 1 + rate > 0 ? Number(Math.Log(ratio) / Math.Log(1 + rate)) : Error(CellError.Number);
        }

        /// <summary>RATE: Newton's method from the guess; a step past -1 can't come back, and gives #NUM!.</summary>
        public static EvalValue Rate(double periods, double payment, double present, double future, double type, double guess)
        {
            if (guess <= -1)
            {
                return Error(CellError.Value);
            }

            if (periods <= 0)
            {
                return Error(CellError.Number);
            }

            double due = Timing(type);
            double rate = guess;
            for (int i = 0; i < 1000; i++)
            {
                (double value, double slope) = RateFunction(rate, periods, payment, present, future, due);
                if (!double.IsFinite(value) || !double.IsFinite(slope) || slope == 0)
                {
                    return Error(CellError.Number);
                }

                double next = rate - (value / slope);
                if (next <= -1)
                {
                    return Error(CellError.Number);
                }

                if (Math.Abs(next - rate) < 1e-7)
                {
                    // One more step to reach what the iteration is heading for.
                    (value, slope) = RateFunction(next, periods, payment, present, future, due);
                    return Number(slope != 0 && double.IsFinite(value / slope) ? next - (value / slope) : next);
                }

                rate = next;
            }

            return Error(CellError.Number);
        }

        private static (double Value, double Slope) RateFunction(double rate, double periods, double payment, double present, double future, double due)
        {
            if (Math.Abs(rate) < 1e-12)
            {
                return (present + (payment * periods) + future, (present * periods) + (payment * periods * (due + ((periods - 1) / 2))));
            }

            double growth = Math.Pow(1 + rate, periods);
            double slopeGrowth = periods * growth / (1 + rate);
            double annuity = (growth - 1) / rate;
            double slopeAnnuity = (slopeGrowth - annuity) / rate;
            double factor = 1 + (rate * due);
            return ((present * growth) + (payment * factor * annuity) + future,
                (present * slopeGrowth) + (payment * ((due * annuity) + (factor * slopeAnnuity))));
        }

        public static EvalValue Interest(double rate, double period, double periods, double present, double future, double type)
        {
            if (period < 1 || period > periods || periods <= 0)
            {
                return Error(CellError.Number);
            }

            return Number(InterestPart(rate, period, periods, present, future, Timing(type)));
        }

        private static double InterestPart(double rate, double period, double periods, double present, double future, double due)
        {
            double payment = Plain(rate, periods, present, future, due);
            if (due != 0 && period == 1)
            {
                return 0;
            }

            double balance = Future(rate, period - 1, payment, present, due);
            return due != 0 ? balance * rate / (1 + rate) : balance * rate;
        }

        public static EvalValue Principal(double rate, double period, double periods, double present, double future, double type)
        {
            if (period < 1 || period > periods || periods <= 0)
            {
                return Error(CellError.Number);
            }

            double due = Timing(type);
            return Number(Plain(rate, periods, present, future, due) - InterestPart(rate, period, periods, present, future, due));
        }

        public static EvalValue StraightInterest(double rate, double period, double periods, double present) =>
            periods == 0 ? Error(CellError.DivisionByZero) : Number(present * rate * ((period / periods) - 1));

        /// <summary>
        /// CUMIPMT and CUMPRINC: the interest or principal paid over the whole periods within a span, a part-period at either end
        /// left out. The number of periods is taken as given.
        /// </summary>
        public static EvalValue Cumulative(double[] v, bool interest)
        {
            double rate = v[0], periods = v[1], present = v[2], first = v[3], last = v[4], type = v[5];
            if (rate <= 0 || periods <= 0 || present <= 0 || first < 1 || last < first || last > periods || type is not (0 or 1))
            {
                return Error(CellError.Number);
            }

            double payment = Plain(rate, periods, present, 0, type);
            double total = 0;
            for (double period = Math.Ceiling(first); period <= Math.Floor(last); period++)
            {
                double part = InterestPart(rate, period, periods, present, 0, type);
                total += interest ? part : payment - part;
            }

            return Number(total);
        }

        /// <summary>Payments at the period's start for any type that isn't 0, as Excel reads it.</summary>
        private static double Timing(double type) => type != 0 ? 1 : 0;
    }

    /// <summary>Rates and prices turned into one another.</summary>
    private static class Conversions
    {
        public static EvalValue Effect(double nominal, double periods)
        {
            periods = Math.Truncate(periods);
            return nominal <= 0 || periods < 1 ? Error(CellError.Number) : Number(Math.Pow(1 + (nominal / periods), periods) - 1);
        }

        public static EvalValue Nominal(double effect, double periods)
        {
            periods = Math.Truncate(periods);
            return effect <= 0 || periods < 1 ? Error(CellError.Number) : Number(periods * (Math.Pow(1 + effect, 1 / periods) - 1));
        }

        /// <summary>DOLLARDE and DOLLARFR: the digits after the point as a count of 1/fraction, or the other way round.</summary>
        public static EvalValue Dollar(double value, double fraction, bool toDecimal)
        {
            fraction = Math.Truncate(fraction);
            if (fraction < 0)
            {
                return Error(CellError.Number);
            }

            if (fraction == 0)
            {
                return Error(CellError.DivisionByZero);
            }

            double whole = Math.Truncate(value);
            double part = value - whole;
            double scale = Math.Pow(10, Math.Ceiling(Math.Log10(fraction)));
            return Number(whole + (toDecimal ? part * scale / fraction : part * fraction / scale));
        }

        public static EvalValue Duration(double rate, double present, double future) =>
            rate <= 0 || present <= 0 || future <= 0
                ? Error(CellError.Number)
                : Number((Math.Log(future) - Math.Log(present)) / Math.Log(1 + rate));

        public static EvalValue EquivalentRate(double periods, double present, double future) =>
            periods <= 0 || (present == 0 && future != 0)
                ? Error(CellError.Number)
                : Number(present == future ? 0 : Math.Pow(future / present, 1 / periods) - 1);
    }
}
