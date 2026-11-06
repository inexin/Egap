namespace Egap.Spreadsheets.Formulas;

internal static partial class FormulaFunctions
{
    /// <summary>What an asset loses in value per period, by each method Excel knows.</summary>
    private static class Depreciation
    {
        public static EvalValue Straight(double cost, double salvage, double life) =>
            life == 0 ? Error(CellError.DivisionByZero) : Number((cost - salvage) / life);

        public static EvalValue SumOfYears(double cost, double salvage, double life, double period)
        {
            if (salvage < 0 || life <= 0 || period <= 0 || period > life)
            {
                return Error(CellError.Number);
            }

            return Number((cost - salvage) * (life - period + 1) * 2 / (life * (life + 1)));
        }

        /// <summary>DB: a fixed rate, rounded to three places, on what is left; a first year of some months and a last of the rest.</summary>
        public static EvalValue FixedDeclining(double cost, double salvage, double life, double period, double month)
        {
            month = Math.Truncate(month);
            period = Math.Truncate(period);
            if (cost < 0 || salvage < 0 || life <= 0 || period <= 0 || month < 1 || month > 12 || period > life + (month < 12 ? 1 : 0))
            {
                return Error(CellError.Number);
            }

            if (cost == 0)
            {
                return Number(0);
            }

            double rate = Math.Round((1 - Math.Pow(salvage / cost, 1 / life)) * 1000, MidpointRounding.AwayFromZero) / 1000;
            double first = cost * rate * month / 12;
            if (period == 1)
            {
                return Number(first);
            }

            double total = first;
            double charge = first;
            for (double p = 2; p <= period; p++)
            {
                charge = (cost - total) * rate;
                if (p > life)
                {
                    charge *= (12 - month) / 12;
                }

                total += charge;
            }

            return Number(charge);
        }

        /// <summary>DDB: declining at factor/life; a fraction of a period is allowed, and nothing goes below the salvage.</summary>
        public static EvalValue DoubleDeclining(double cost, double salvage, double life, double period, double factor)
        {
            if (cost < 0 || salvage < 0 || life <= 0 || period <= 0 || period > life || factor <= 0)
            {
                return Error(CellError.Number);
            }

            return Number(DecliningCharge(cost, salvage, life, period, factor));
        }

        private static double DecliningCharge(double cost, double salvage, double life, double period, double factor)
        {
            double rate = factor / life;
            if (rate >= 1)
            {
                return period <= 1 ? Math.Max(cost - salvage, 0) : 0;
            }

            double before = period <= 1 ? cost : cost * Math.Pow(1 - rate, period - 1);
            double after = before * rate;
            return Math.Max(Math.Min(after, before - salvage), 0);
        }

        /// <summary>VDB: declining balance over a span of periods, parts of periods pro rata, moving to straight line when that gives more.</summary>
        public static EvalValue Variable(EvalValue[] a, EvalContext c)
        {
            Span<double> v = stackalloc double[5];
            for (int i = 0; i < 5; i++)
            {
                if (!Num(a[i], c, out v[i], out EvalValue error))
                {
                    return error;
                }
            }

            if (!Optional(a, 5, 2, c, out double factor, out EvalValue e) || !OptionalFlag(a, 6, false, out bool noSwitch, out e))
            {
                return e;
            }

            double cost = v[0], salvage = v[1], life = v[2], start = v[3], end = v[4];
            if (life == 0)
            {
                return Error(CellError.DivisionByZero);
            }

            if (cost < 0 || salvage < 0 || life < 0 || start < 0 || end < start || end > life || factor < 0)
            {
                return Error(CellError.Number);
            }

            double rate = Math.Min(factor / life, 1);
            if (noSwitch)
            {
                double total = 0, value = cost;
                for (int period = 0; period < Math.Ceiling(end); period++)
                {
                    double charge = Math.Min(value * rate, Math.Max(value - salvage, 0));
                    double overlap = Math.Min(end, period + 1) - Math.Max(start, period);
                    if (overlap > 0)
                    {
                        total += charge * overlap;
                    }

                    value -= charge;
                }

                return Number(total);
            }

            // What is left at the start is an asset of its own: its periods count from there.
            double before = Switching(cost, salvage, life, rate, start, true);
            return Number(Switching(cost - before, salvage, life - start, rate, end - start, false));
        }

        /// <summary>
        /// Declining balance from now on, moving to straight line over the remaining life when that gives more. Up to the start of
        /// the span asked for, the part of a period comes first, pro rata; within the span it comes last, a part of what the whole
        /// period would have written off. The point of the switch depends on that order.
        /// </summary>
        private static double Switching(double cost, double salvage, double life, double rate, double span, bool partFirst)
        {
            double value = cost, line = 0, elapsed = 0;
            bool straight = false;
            double part = span - Math.Floor(span);
            int whole = (int)Math.Floor(span);
            int steps = whole + (part > 0 ? 1 : 0);
            for (int step = 0; step < steps; step++)
            {
                bool isPart = part > 0 && step == (partFirst ? 0 : steps - 1);
                double left = life - elapsed;
                double spare = Math.Max(value - salvage, 0);
                double even = left > 0 ? spare / left : 0;
                if (!straight && even > value * rate)
                {
                    straight = true;
                    line = even;
                }

                double charge = straight ? line : value * rate;
                value -= !isPart ? Math.Min(charge, spare) : partFirst || left <= part ? Math.Min(charge * part, spare) : Math.Min(charge, spare) * part;
                elapsed += isPart ? part : 1;
            }

            return cost - value;
        }

        private static bool ReadFrench(EvalValue[] a, EvalContext c, out double cost, out double bought, out double firstEnd, out double salvage, out double period, out double rate, out int basis, out EvalValue error)
        {
            bought = firstEnd = salvage = period = rate = 0;
            basis = 0;
            double basisValue = 0;
            if (!Strict(a[0], c, true, out cost, out error) || !Strict(a[1], c, true, out double buy, out error) || !Strict(a[2], c, true, out double end, out error)
                || !Strict(a[3], c, true, out salvage, out error) || !Strict(a[4], c, true, out period, out error) || !Strict(a[5], c, true, out rate, out error)
                || (a.Length > 6 && !Strict(a[6], c, true, out basisValue, out error)))
            {
                return false;
            }

            basis = (int)Math.Truncate(basisValue);
            period = Math.Truncate(period);
            bought = Math.Truncate(buy);
            firstEnd = Math.Truncate(end);
            if (cost <= 0 || salvage < 0 || salvage > cost || period < 0 || rate <= 0 || bought > firstEnd || basis is < 0 or > 4 or 2)
            {
                error = Error(CellError.Number);
                return false;
            }

            return true;
        }

        /// <summary>AMORLINC: the rate on the cost each period, pro rata in the first, until only the salvage is left.</summary>
        public static EvalValue FrenchLinear(EvalValue[] a, EvalContext c)
        {
            if (!ReadFrench(a, c, out double cost, out double bought, out double firstEnd, out double salvage, out double period, out double rate, out int basis, out EvalValue error))
            {
                return error;
            }

            double full = cost * rate;
            double first = full * FirstPart(bought, firstEnd, basis, c);
            double left = cost - salvage;
            if (period == 0)
            {
                return Number(Math.Min(first, left));
            }

            left -= first;
            for (double p = 1; p < period && left > 0; p++)
            {
                left -= full;
            }

            return Number(Math.Max(Math.Min(full, left), 0));
        }

        /// <summary>
        /// The part of a year the first period takes in the French methods: a whole year when bought on the period's last day;
        /// the calendar bases read 29 February as the 28th and count against the first year's days.
        /// </summary>
        private static double FirstPart(double bought, double firstEnd, int basis, EvalContext c)
        {
            if (bought == firstEnd)
            {
                return 1;
            }

            if (basis is 0 or 4)
            {
                return Securities.Days(bought, firstEnd, basis, c) / 360;
            }

            (int y1, int m1, int d1) = Parts(bought, c.Date1904);
            (int y2, int m2, int d2) = Parts(firstEnd, c.Date1904);
            double days = firstEnd - bought + (m1 == 2 && d1 == 29 ? 1 : 0) - (m2 == 2 && d2 == 29 ? 1 : 0);
            return days / (basis == 3 || !DateTime.IsLeapYear(y1) ? 365 : 366);
        }

        /// <summary>
        /// AMORDEGRC: the French declining method. The rate is multiplied by a coefficient for the asset's life, charges are
        /// whole numbers, and the last two periods share what is left.
        /// </summary>
        public static EvalValue FrenchDeclining(EvalValue[] a, EvalContext c)
        {
            if (!ReadFrench(a, c, out double cost, out double bought, out double firstEnd, out double salvage, out double period, out double rate, out int basis, out EvalValue error))
            {
                return error;
            }

            double life = 1 / rate;
            if (life <= 2)
            {
                return Error(CellError.Number);
            }

            if (cost == salvage)
            {
                return Number(0);
            }

            double declining = rate * (life <= 4 ? 1.5 : life <= 6 ? 2 : 2.5);
            int periods = (int)Math.Ceiling(life);
            double left = cost - salvage;

            // The first charge is rounded for the value that follows; later ones are rounded only as they are given.
            double first = Math.Min(Whole(cost * declining * FirstPart(bought, firstEnd, basis, c)), left);
            if (period == 0)
            {
                return Number(first);
            }

            double value = cost - first;
            left -= first;
            double half = 0;
            for (int p = 1; p < periods; p++)
            {
                if (left < 0)
                {
                    return Number(0);
                }

                // The last two periods share what is there at the start of the first of them.
                double charge = p == periods - 2 ? half = value / 2 : p == periods - 1 ? (half > 0 ? half : value / 2) : value * declining;
                if (p == period)
                {
                    return Number(Whole(charge));
                }

                value -= charge;
                left -= charge;
            }

            return Number(0);
        }

        /// <summary>Rounded to a whole number, a hair over so that a half lost to the binary fraction still goes up.</summary>
        private static double Whole(double value) => Math.Round(value * (1 + 4e-15), MidpointRounding.AwayFromZero);
    }
}
