namespace Egap.Spreadsheets.Formulas;

internal static partial class FormulaFunctions
{
    /// <summary>
    /// Bonds, bills and other securities: their coupon dates on the five day-count bases (0 US 30/360, 1 actual/actual,
    /// 2 actual/360, 3 actual/365, 4 European 30/360), and prices and yields by the formulas Microsoft's reference gives.
    /// </summary>
    private static class Securities
    {
        public static IEnumerable<FormulaFunction> Functions() =>
        [
            Security("COUPDAYBS", 2, 3, 4, [0], (v, c) => Coupon(v[0], v[1], v[2], v[3], c, (s, b) => Number(s.Accrued))),
            Security("COUPDAYS", 2, 3, 4, [0], (v, c) => Coupon(v[0], v[1], v[2], v[3], c, (s, b) => Number(s.Length))),
            Security("COUPDAYSNC", 2, 3, 4, [0], (v, c) => Coupon(v[0], v[1], v[2], v[3], c, (s, b) => Number(s.ToNext))),
            Security("COUPNCD", 2, 3, 4, [0], (v, c) => Coupon(v[0], v[1], v[2], v[3], c, (s, b) => Number(s.Next))),
            Security("COUPPCD", 2, 3, 4, [0], (v, c) => Coupon(v[0], v[1], v[2], v[3], c, (s, b) => Number(s.Previous))),
            Security("COUPNUM", 2, 3, 4, [0], (v, c) => Coupon(v[0], v[1], v[2], v[3], c, (s, b) => s.InRange ? Number(s.Count) : Error(CellError.Number))),
            Security("PRICE", 2, 6, 7, [0], Price),
            Security("YIELD", 2, 6, 7, [0], Yield),
            Security("DURATION", 2, 5, 6, [0], (v, c) => Duration(v, c, modified: false)),
            Security("MDURATION", 2, 5, 6, [0], (v, c) => Duration(v, c, modified: true)),
            Security("DISC", 2, 4, 5, [0], Discount),
            Security("INTRATE", 2, 4, 5, [0], InterestRate),
            Security("RECEIVED", 2, 4, 5, [0], Received),
            Security("PRICEDISC", 2, 4, 5, [0], PriceDiscount),
            Security("YIELDDISC", 2, 4, 5, [0], YieldDiscount),
            Security("PRICEMAT", 3, 5, 6, [0], PriceAtMaturity),
            Security("YIELDMAT", 3, 5, 6, [0], YieldAtMaturity),
            Security("TBILLEQ", 2, 3, 3, [], BillEquivalent),
            Security("TBILLPRICE", 2, 3, 3, [], BillPrice),
            Security("TBILLYIELD", 2, 3, 3, [], BillYield),
            Security("ACCRINTM", 2, 4, 5, [0], AccruedAtMaturity, par: 3),
            new("ACCRINT", 6, 8, Kinds.Value, Accrued),
            Security("ODDFPRICE", 4, 8, 9, [0], OddFirstPrice),
            Security("ODDFYIELD", 4, 8, 9, [0], OddFirstYield),
            Security("ODDLPRICE", 3, 7, 8, [0], OddLastPrice),
            Security("ODDLYIELD", 3, 7, 8, [0], OddLastYield),
        ];

        /// <summary>
        /// A function whose first arguments are dates: every argument read as a number first (no booleans, as these began in
        /// Excel's Analysis ToolPak), then the dates cut to whole days, which gives #NUM! outside the calendar. An argument
        /// <paramref name="par"/> left empty is 1000.
        /// </summary>
        private static FormulaFunction Security(string name, int dates, int min, int max, double[] fallbacks, Func<double[], EvalContext, EvalValue> body, int par = -1) =>
            new(name, min, max, Kinds.Value, (a, c) =>
            {
                double[] values = new double[max];
                for (int i = 0; i < max; i++)
                {
                    if (i >= a.Length)
                    {
                        values[i] = fallbacks[i - min];
                    }
                    else if (i == par && a[i].Kind == EvalKind.Missing)
                    {
                        values[i] = 1000;
                    }
                    else if (!Strict(a[i], c, true, out values[i], out EvalValue error))
                    {
                        return error;
                    }
                }

                for (int i = 0; i < dates; i++)
                {
                    values[i] = Math.Truncate(values[i]);
                    if (values[i] < 0 || values[i] > LastSerial(c))
                    {
                        return Error(CellError.Number);
                    }
                }

                return body(values, c);
            });

        private static bool ValidFrequency(ref double frequency)
        {
            frequency = Math.Truncate(frequency);
            return frequency is 1 or 2 or 4;
        }

        private static bool ValidBasis(double value, out int basis)
        {
            basis = (int)Math.Truncate(Math.Clamp(value, -1, 5));
            return basis is >= 0 and <= 4;
        }

        /// <summary>
        /// The days between two dates by a basis: 30/360 in the two ways, or as they are. In the US way the securities functions
        /// keep a 31st at the end unless the start was truly the 30th or 31st, so a start on February's last day doesn't count,
        /// unlike DAYS360 and YEARFRAC.
        /// </summary>
        internal static double Days(double start, double end, int basis, EvalContext c)
        {
            if (basis is not (0 or 4))
            {
                return end - start;
            }

            (int y1, int m1, int d1) = Parts(start, c.Date1904);
            (int y2, int m2, int d2) = Parts(end, c.Date1904);
            if (basis == 4)
            {
                d1 = Math.Min(d1, 30);
                d2 = Math.Min(d2, 30);
            }
            else
            {
                bool firstLast = IsLastOfFebruary(y1, m1, d1);
                if (d2 == 31 && d1 >= 30)
                {
                    d2 = 30;
                }

                if (firstLast && IsLastOfFebruary(y2, m2, d2))
                {
                    d2 = 30;
                }

                if (firstLast || d1 == 31)
                {
                    d1 = 30;
                }
            }

            return ((y2 - y1) * 360) + ((m2 - m1) * 30) + (d2 - d1);
        }

        /// <summary>The days in a year by a basis, for a basis that doesn't look at the calendar.</summary>
        private static double YearDays(int basis) => basis == 3 ? 365 : 360;

        /// <summary>The part of a year between two dates: by the days and the year's length, or as YEARFRAC finds it on actual/actual.</summary>
        internal static double YearPart(double start, double end, int basis, EvalContext c) =>
            basis == 1
                ? YearFrac([EvalValue.FromNumber(start), EvalValue.FromNumber(end), EvalValue.FromNumber(basis)], c).Number
                : Days(start, end, basis, c) / YearDays(basis);

        /// <summary>A quasi-coupon date: an anchor date some months on, on the month's last day if the anchor is, else on its day or the month's last.</summary>
        private static double Shifted(int year, int month, int day, bool endOfMonth, int months, EvalContext c)
        {
            (int y, int m) = Month(year, month, months);
            return SerialOf(y, m, endOfMonth ? DateTime.DaysInMonth(y, m) : Math.Min(day, DateTime.DaysInMonth(y, m)), c);
        }

        private static (int Year, int Month) Month(int year, int month, int months)
        {
            int total = (year * 12) + month - 1 + months;
            int y = (int)Math.Floor(total / 12.0);
            return (y, total - (y * 12) + 1);
        }

        /// <summary>A date's serial, counting on below day 0 for the coupons before 1900 a date's period can reach back to.</summary>
        private static double SerialOf(int year, int month, int day, EvalContext c)
        {
            DateTime date = new(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)));
            if (c.Date1904)
            {
                return (date - new DateTime(1904, 1, 1)).Days;
            }

            return date < new DateTime(1900, 3, 1) ? (date - new DateTime(1899, 12, 31)).Days : (date - new DateTime(1899, 12, 30)).Days;
        }

        private static bool IsMonthEnd(int year, int month, int day) => day >= DateTime.DaysInMonth(year, month);

        /// <summary>A settlement date's place among the coupons counted back from maturity.</summary>
        private readonly struct Place
        {
            public double Previous { get; init; }

            public double Next { get; init; }

            public double Count { get; init; }

            /// <summary>Whether the previous coupon falls within the calendar: COUPNUM's count means nothing otherwise.</summary>
            public bool InRange { get; init; }

            public double Accrued { get; init; }

            public double Length { get; init; }

            public double ToNext { get; init; }

            /// <summary>The part of a period left to the next coupon, as prices and yields take it: what hasn't accrued.</summary>
            public double Fraction => (Coupon - Accrued) / Coupon;

            /// <summary>The length of the period as prices and yields take it: on actual/actual, the days between the coupons.</summary>
            public double Coupon { get; init; }
        }

        private static Place Locate(double settlement, double maturity, double frequency, int basis, EvalContext c)
        {
            (int y, int m, int d) = Parts(maturity, c.Date1904);
            bool endOfMonth = IsMonthEnd(y, m, d);
            int step = 12 / (int)frequency;
            double next = maturity;
            int count = 1;
            double previous = Shifted(y, m, d, endOfMonth, -step, c);
            while (previous > settlement)
            {
                next = previous;
                count++;
                previous = Shifted(y, m, d, endOfMonth, -count * step, c);
            }

            double length = basis == 1 ? ActualLength(settlement, y, m, d, step, c) : YearDays(basis) / frequency;
            bool before1900 = previous < 0;
            return new Place
            {
                Previous = Math.Max(previous, 0),
                Next = next,
                Count = count,
                InRange = !before1900,
                Accrued = Days(Math.Max(previous, 0), settlement, basis, c),
                Length = length,
                Coupon = basis == 1 ? next - previous : length,
                ToNext = basis switch
                {
                    0 when before1900 => ThirtyBefore1900(settlement, next, c),
                    0 => length - (endOfMonth ? Days(previous, settlement, 0, c) : ThirtyFromDay(previous, Math.Min(d, 30), settlement, c)),
                    4 => Days(settlement, next, 4, c),
                    _ => next - settlement,
                },
            };
        }

        /// <summary>
        /// The days since the previous coupon on the US basis as COUPDAYSNC takes them away from the period when maturity isn't on
        /// a month's end: the coupon on maturity's own day of the month even where that month is too short for it, and a
        /// settlement on February's last day as the 30th.
        /// </summary>
        private static double ThirtyFromDay(double previous, int day, double settlement, EvalContext c)
        {
            (int y1, int m1, int d1) = Parts(previous, c.Date1904);
            (int y2, int m2, int d2) = Parts(settlement, c.Date1904);
            if ((IsLastOfFebruary(y1, m1, d1) && IsLastOfFebruary(y2, m2, d2)) || (d2 == 31 && day >= 30))
            {
                d2 = 30;
            }

            return ((y2 - y1) * 360) + ((m2 - m1) * 30) + (d2 - day);
        }

        /// <summary>The days to the next coupon on the US basis when the previous one would be before 1900: the plain count, February's 28th as the 30th.</summary>
        private static double ThirtyBefore1900(double settlement, double next, EvalContext c)
        {
            (int y1, int m1, int d1) = Parts(settlement, c.Date1904);
            (int y2, int m2, int d2) = Parts(next, c.Date1904);
            if (m2 == 2 && d2 == DateTime.DaysInMonth(y2, 2))
            {
                d2 = 30;
            }

            return ((y2 - y1) * 360) + ((m2 - m1) * 30) + (d2 - d1);
        }

        /// <summary>
        /// The length of the coupon period on actual/actual, as Excel finds it. It is not always the days between the dates
        /// COUPPCD and COUPNCD give. Alongside those coupon dates runs a second chain, stepped back from maturity one period at
        /// a time, whose day of the month drops for good at the first short month it meets. The period is measured back from the
        /// first date above settlement in either chain, on a month-end if that date is on one. Found with Excel, over 4716
        /// settlement dates: all but two match. Prices and yields take the plain length instead.
        /// </summary>
        private static double ActualLength(double settlement, int year, int month, int day, int step, EvalContext c)
        {
            bool endOfMonth = IsMonthEnd(year, month, day);
            (int cy, int cm, int cd) = (year, month, day);
            double drift = SerialOf(cy, cm, cd, c), coupon = drift, driftAbove = drift, couponAbove = drift;
            for (int k = 1; ; k++)
            {
                (int py, int pm) = Month(cy, cm, -step);
                int pd = Math.Min(cd, DateTime.DaysInMonth(py, pm));
                double driftBelow = SerialOf(py, pm, pd, c);
                double couponBelow = Shifted(year, month, day, endOfMonth, -k * step, c);
                if (driftBelow <= settlement && couponBelow <= settlement)
                {
                    break;
                }

                (driftAbove, couponAbove) = (drift, coupon);
                (drift, coupon) = (driftBelow, couponBelow);
                (cy, cm, cd) = (py, pm, pd);
            }

            if (drift <= settlement && settlement < coupon && driftAbove != couponAbove)
            {
                return driftAbove - drift;
            }

            double above = drift > settlement && (drift < coupon || coupon <= settlement) ? drift : coupon;
            double below = StepBack(above, step, c);
            if (below > settlement)
            {
                above = below;
                below = StepBack(above, step, c);
            }

            return above - below;
        }

        /// <summary>A period before a date, from a month's end to a month's end.</summary>
        private static double StepBack(double date, int step, EvalContext c)
        {
            (int y, int m, int d) = date < 0 ? BeforeZero(date) : Parts(date, c.Date1904);
            return Shifted(y, m, d, IsMonthEnd(y, m, d), -step, c);
        }

        private static (int Year, int Month, int Day) BeforeZero(double date)
        {
            DateTime day = new DateTime(1899, 12, 31).AddDays(date);
            return (day.Year, day.Month, day.Day);
        }

        private static EvalValue Coupon(double settlement, double maturity, double frequency, double basisValue, EvalContext c, Func<Place, int, EvalValue> result)
        {
            if (settlement >= maturity || !ValidFrequency(ref frequency) || !ValidBasis(basisValue, out int basis))
            {
                return Error(CellError.Number);
            }

            return result(Locate(settlement, maturity, frequency, basis, c), basis);
        }

        /// <summary>The clean price of a bond at a yield; one period left is simple interest.</summary>
        private static double BondPrice(in Place p, double rate, double yield, double redemption, double frequency)
        {
            double coupon = 100 * rate / frequency;
            double accrued = coupon * p.Accrued / p.Coupon;
            double fraction = p.Fraction;
            if (p.Count <= 1)
            {
                return ((redemption + coupon) / (1 + (fraction * yield / frequency))) - accrued;
            }

            double step = 1 + (yield / frequency);
            double total = redemption / Math.Pow(step, p.Count - 1 + fraction);
            for (int k = 1; k <= p.Count; k++)
            {
                total += coupon / Math.Pow(step, k - 1 + fraction);
            }

            return total - accrued;
        }

        private static EvalValue Price(double[] v, EvalContext c)
        {
            double frequency = v[5];
            if (v[0] >= v[1] || v[2] < 0 || v[3] < 0 || v[4] <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[6], out int basis))
            {
                return Error(CellError.Number);
            }

            return Number(BondPrice(Locate(v[0], v[1], frequency, basis, c), v[2], v[3], v[4], frequency));
        }

        private static EvalValue Yield(double[] v, EvalContext c)
        {
            double frequency = v[5], rate = v[2], price = v[3], redemption = v[4];
            if (v[0] >= v[1] || rate < 0 || price <= 0 || redemption <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[6], out int basis))
            {
                return Error(CellError.Number);
            }

            Place place = Locate(v[0], v[1], frequency, basis, c);
            if (place.Count <= 1)
            {
                double coupon = rate / frequency;
                bool actual = basis is 1 or 2 or 3;
                double length = actual ? place.Next - place.Previous : place.Length;
                double accrued = actual ? v[0] - place.Previous : place.Accrued;
                double paid = (price / 100) + (accrued / length * coupon);
                double got = (redemption / 100) + coupon;
                return Number((got - paid) / paid * frequency * length / Days(v[0], place.Next, actual ? 1 : basis, c));
            }

            return Root(y => BondPrice(place, rate, y, redemption, frequency) - price, rate, frequency);
        }

        private static EvalValue Duration(double[] v, EvalContext c, bool modified)
        {
            double frequency = v[4], rate = v[2], yield = v[3];
            if (v[0] >= v[1] || rate < 0 || yield < 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[5], out int basis))
            {
                return Error(CellError.Number);
            }

            Place place = Locate(v[0], v[1], frequency, basis, c);
            double coupon = 100 * rate / frequency;
            double step = 1 + (yield / frequency);
            double fraction = place.Fraction;
            double weighted = 0, total = 0;
            for (int k = 1; k <= place.Count; k++)
            {
                double time = k - 1 + fraction;
                double flow = (k == place.Count ? 100 + coupon : coupon) / Math.Pow(step, time);
                weighted += time * flow;
                total += flow;
            }

            double years = weighted / total / frequency;
            return Number(modified ? years / step : years);
        }

        private static bool Span(double settlement, double maturity, double basisValue, EvalContext c, out double years, out int basis)
        {
            years = 0;
            if (settlement >= maturity || !ValidBasis(basisValue, out basis))
            {
                basis = 0;
                return false;
            }

            years = YearPart(settlement, maturity, basis, c);
            return true;
        }

        private static EvalValue Discount(double[] v, EvalContext c) =>
            !Span(v[0], v[1], v[4], c, out double years, out _) || v[2] <= 0 || v[3] <= 0 ? Error(CellError.Number) : Number((v[3] - v[2]) / v[3] / years);

        private static EvalValue InterestRate(double[] v, EvalContext c) =>
            !Span(v[0], v[1], v[4], c, out double years, out _) || v[2] <= 0 || v[3] <= 0 ? Error(CellError.Number) : Number((v[3] - v[2]) / v[2] / years);

        private static EvalValue Received(double[] v, EvalContext c)
        {
            if (!Span(v[0], v[1], v[4], c, out double years, out _) || v[2] <= 0 || v[3] <= 0)
            {
                return Error(CellError.Number);
            }

            double part = 1 - (v[3] * years);
            return part <= 0 ? Error(CellError.Number) : Number(v[2] / part);
        }

        private static EvalValue PriceDiscount(double[] v, EvalContext c) =>
            !Span(v[0], v[1], v[4], c, out double years, out _) || v[2] <= 0 || v[3] <= 0 ? Error(CellError.Number) : Number(v[3] * (1 - (v[2] * years)));

        private static EvalValue YieldDiscount(double[] v, EvalContext c) =>
            !Span(v[0], v[1], v[4], c, out double years, out _) || v[2] <= 0 || v[3] <= 0 ? Error(CellError.Number) : Number((v[3] - v[2]) / v[2] / years);

        private static EvalValue PriceAtMaturity(double[] v, EvalContext c)
        {
            double settlement = v[0], maturity = v[1], issue = v[2], rate = v[3], yield = v[4];
            if (settlement >= maturity || issue >= settlement || rate < 0 || yield < 0 || !ValidBasis(v[5], out int basis))
            {
                return Error(CellError.Number);
            }

            (double fromIssue, double toMaturity, double accrued) = MaturityParts(issue, settlement, maturity, basis, c);
            return Number(((100 + (fromIssue * rate * 100)) / (1 + (toMaturity * yield))) - (accrued * rate * 100));
        }

        /// <summary>
        /// The parts of a year from issue to maturity, from settlement to maturity and from issue to settlement, in PRICEMAT and
        /// YIELDMAT: the last two make up the first by construction, and actual/actual counts every year as long as YEARFRAC finds the years from issue to settlement.
        /// </summary>
        private static (double FromIssue, double ToMaturity, double Accrued) MaturityParts(double issue, double settlement, double maturity, int basis, EvalContext c)
        {
            double accrued = Days(issue, settlement, basis, c);
            double year = basis == 1 ? accrued / YearPart(issue, settlement, 1, c) : YearDays(basis);
            double fromIssue = Days(issue, maturity, basis, c);
            return (fromIssue / year, (fromIssue - accrued) / year, accrued / year);
        }

        private static EvalValue YieldAtMaturity(double[] v, EvalContext c)
        {
            double settlement = v[0], maturity = v[1], issue = v[2], rate = v[3], price = v[4];
            if (settlement >= maturity || issue >= settlement || rate < 0 || price <= 0 || !ValidBasis(v[5], out int basis))
            {
                return Error(CellError.Number);
            }

            (double fromIssue, double toMaturity, double accrued) = MaturityParts(issue, settlement, maturity, basis, c);
            double paid = (price / 100) + (accrued * rate);
            return Number(((1 + (fromIssue * rate)) - paid) / paid / toMaturity);
        }

        private static bool BillDays(double settlement, double maturity, EvalContext c, out double days)
        {
            days = maturity - settlement;
            if (days <= 0)
            {
                return false;
            }

            // Up to a year on, by the calendar.
            (int y, int m, int d) = Parts(settlement, c.Date1904);
            return maturity <= Shifted(y, m, d, false, 12, c);
        }

        private static EvalValue BillEquivalent(double[] v, EvalContext c)
        {
            if (!BillDays(v[0], v[1], c, out double days) || v[2] <= 0)
            {
                return Error(CellError.Number);
            }

            double price = 1 - (v[2] * days / 360);
            if (price <= 0)
            {
                return Error(CellError.Number);
            }

            if (days <= 182)
            {
                return Number(365 * v[2] / (360 - (v[2] * days)));
            }

            double t = days / (days == 366 ? 366 : 365);
            double half = t - 0.5;
            return Number((Math.Sqrt((t * t) - (2 * half * (1 - (1 / price)))) - t) / half);
        }

        private static EvalValue BillPrice(double[] v, EvalContext c)
        {
            if (!BillDays(v[0], v[1], c, out double days) || v[2] <= 0)
            {
                return Error(CellError.Number);
            }

            double price = 100 * (1 - (v[2] * days / 360));
            return price <= 0 ? Error(CellError.Number) : Number(price);
        }

        private static EvalValue BillYield(double[] v, EvalContext c) =>
            !BillDays(v[0], v[1], c, out double days) || v[2] <= 0 ? Error(CellError.Number) : Number((100 - v[2]) / v[2] * 360 / days);

        private static EvalValue AccruedAtMaturity(double[] v, EvalContext c)
        {
            if (v[0] > v[1] || v[2] <= 0 || v[3] <= 0 || !ValidBasis(v[4], out int basis))
            {
                return Error(CellError.Number);
            }

            return Number(v[0] == v[1] ? 0 : v[3] * v[2] * YearPart(v[0], v[1], basis, c));
        }

        /// <summary>
        /// ACCRINT: the interest from issue (or the first interest date) to settlement, added up over the quasi-coupon periods
        /// counted from the first interest date.
        /// </summary>
        private static EvalValue Accrued(EvalValue[] a, EvalContext c)
        {
            Span<double> v = stackalloc double[7];
            for (int i = 0; i < 7; i++)
            {
                if (i >= a.Length)
                {
                    v[i] = 0;
                }
                else if (i == 4 && a[i].Kind == EvalKind.Missing)
                {
                    v[i] = 1000;
                }
                else if (!Strict(a[i], c, true, out v[i], out EvalValue error))
                {
                    return error;
                }
            }

            if (a.Length > 7 && a[7].Kind == EvalKind.Text)
            {
                return Error(CellError.Value);
            }

            if (!OptionalFlag(a, 7, true, out bool fromIssue, out EvalValue flagError))
            {
                return flagError;
            }

            double issue = Math.Truncate(v[0]), first = Math.Truncate(v[1]), settlement = Math.Truncate(v[2]);
            double rate = v[3], par = v[4], frequency = v[5];
            if (issue < 0 || first < 0 || settlement < 0 || Math.Max(issue, Math.Max(first, settlement)) > LastSerial(c) || issue >= settlement || rate <= 0 || par <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[6], out int basis))
            {
                return Error(CellError.Number);
            }

            (int y, int m, int d) = Parts(first, c.Date1904);
            bool endOfMonth = IsMonthEnd(y, m, d);
            int step = 12 / (int)frequency;
            double Coupon(int k) => Shifted(y, m, d, endOfMonth, k * step, c);

            // Whatever the settlement, the days past a coupon are counted in one go over the period up to the first interest date.
            double lastPeriod = basis == 1 ? first - Coupon(-1) : YearDays(basis) / frequency;
            double before = Coupon(-1);
            double total;
            if (issue >= first || (!fromIssue && issue >= before))
            {
                total = Days(issue, settlement, basis, c) / lastPeriod;
            }
            else if (!fromIssue)
            {
                // Whole periods before the last one drop out; a broken one at the issue doesn't.
                int k = -2;
                while (Coupon(k) > issue)
                {
                    k--;
                }

                total = (issue > Coupon(k) ? OddPart(issue, Coupon(k), Coupon(k + 1), frequency, basis, c) : 0) + (Days(before, settlement, basis, c) / lastPeriod);
            }
            else
            {
                // Up to the coupon before the first interest date whatever the settlement, then on to the settlement.
                total = 0;
                for (int k = -2; Coupon(k + 1) > issue && k + 1 < 0; k--)
                {
                    total += OddPart(issue, Coupon(k), Coupon(k + 1), frequency, basis, c);
                }

                double current = Math.Max(issue, before);
                for (int k = 0; Month(y, m, k * step).Year > 9999 || Coupon(k) < settlement; k++)
                {
                    if (Month(y, m, k * step).Year > 9999)
                    {
                        return Error(CellError.Number);
                    }

                    total += OddPart(current, Coupon(k - 1), Coupon(k), frequency, basis, c);
                    current = Coupon(k);
                }

                total += Days(current, settlement, basis, c) / lastPeriod;
            }

            return Number(par * rate / frequency * total);
        }

        /// <summary>The part of a coupon period, from a date or from its start, in ACCRINT.</summary>
        private static double OddPart(double issue, double from, double to, double frequency, int basis, EvalContext c)
        {
            if (issue <= from)
            {
                return 1;
            }

            double length = basis switch
            {
                1 => to - from,
                4 => Days(from, to, 4, c),
                _ => YearDays(basis) / frequency,
            };
            return Days(issue, to, basis, c) / length;
        }

        /// <summary>A security with an odd first period: its discounting exponent and its parts of a coupon.</summary>
        private readonly struct OddFirst
        {
            public double After { get; init; }

            public double Exponent { get; init; }

            public double CouponPart { get; init; }

            public double AccruedPart { get; init; }

            public double Price(double rate, double yield, double redemption, double frequency)
            {
                double coupon = 100 * rate / frequency;
                double step = 1 + (yield / frequency);
                double total = (coupon * CouponPart) / Math.Pow(step, Exponent);
                for (int k = 1; k <= After; k++)
                {
                    total += coupon / Math.Pow(step, k + Exponent);
                }

                total += redemption / Math.Pow(step, After + Exponent);
                return total - (coupon * AccruedPart);
            }
        }

        private static bool ReadOddFirst(double settlement, double maturity, double issue, double first, double frequency, int basis, EvalContext c, out OddFirst odd)
        {
            odd = default;
            int step = 12 / (int)frequency;
            (int fy, int fm, int fd) = Parts(first, c.Date1904);
            (int my, int mm, int md) = Parts(maturity, c.Date1904);
            bool firstAtEnd = IsMonthEnd(fy, fm, fd);
            int months = ((my - fy) * 12) + mm - fm;
            if (months % step != 0 || (IsMonthEnd(my, mm, md) ? !firstAtEnd : fd != md))
            {
                return false;
            }

            // A first period of exactly normal length isn't odd.
            double even = YearDays(basis) / frequency;
            double before = Shifted(fy, fm, fd, firstAtEnd, -step, c);
            double normal = basis == 1 ? first - before : even;
            double firstPeriod = Days(issue, first, basis, c);
            if (firstPeriod == normal)
            {
                return false;
            }

            // The parts of a coupon go over periods stepped back one from the other, so that a month's end, once lost, stays lost.
            double couponPart = 0, accruedPart = 0;
            for (double to = first; to > issue;)
            {
                (int y, int m, int d) = Parts(to, c.Date1904);
                double from = Shifted(y, m, d, false, -step, c);
                double length = firstPeriod < normal ? normal : basis == 1 ? to - from : even;
                double start = Math.Max(from, issue);
                couponPart += issue < from ? 1 : Days(start, to, basis, c) / length;
                if (settlement > start)
                {
                    accruedPart += Days(start, Math.Min(settlement, to), basis, c) / length;
                }

                to = from;
            }

            odd = new OddFirst
            {
                After = months / step,
                Exponent = firstPeriod < normal ? Days(settlement, first, basis, c) / normal : LongFirstExponent(settlement, first, step, even, basis, firstAtEnd, c),
                CouponPart = couponPart,
                AccruedPart = accruedPart,
            };
            return true;
        }

        /// <summary>
        /// The periods from settlement to a first coupon over a long first period, on coupons counted from that one and keeping a
        /// month's end. A first coupon on a month's end has its whole periods counted by months, one too many for a settlement not
        /// on a month's end itself, unless that's in the month of a coupon.
        /// </summary>
        private static double LongFirstExponent(double settlement, double first, int step, double even, int basis, bool firstAtEnd, EvalContext c)
        {
            (int fy, int fm, int fd) = Parts(first, c.Date1904);
            int k = 1;
            while (Shifted(fy, fm, fd, firstAtEnd, -k * step, c) > settlement)
            {
                k++;
            }

            double previous = Shifted(fy, fm, fd, firstAtEnd, -k * step, c), next = Shifted(fy, fm, fd, firstAtEnd, -(k - 1) * step, c);
            double part = basis switch
            {
                1 => (next - settlement) / (next - previous),
                0 or 4 => (even - Days(previous, settlement, basis, c)) / even,
                _ => (next - settlement) / even,
            };
            if (!firstAtEnd)
            {
                return k - 1 + part;
            }

            (int sy, int sm, int sd) = Parts(settlement, c.Date1904);
            int months = ((fy - sy) * 12) + fm - sm;
            int periods = (months + step - 1) / step;
            return part + (IsMonthEnd(sy, sm, sd) ? periods - 1 : Math.Max(periods, 1));
        }

        private static EvalValue OddFirstPrice(double[] v, EvalContext c)
        {
            double frequency = v[7];
            if (!(v[1] > v[3] && v[3] > v[0] && v[0] > v[2]) || v[4] < 0 || v[5] < 0 || v[6] <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[8], out int basis)
                || !ReadOddFirst(v[0], v[1], v[2], v[3], frequency, basis, c, out OddFirst odd))
            {
                return Error(CellError.Number);
            }

            return Number(odd.Price(v[4], v[5], v[6], frequency));
        }

        private static EvalValue OddFirstYield(double[] v, EvalContext c)
        {
            double frequency = v[7], rate = v[4], price = v[5], redemption = v[6];
            if (!(v[1] > v[3] && v[3] > v[0] && v[0] > v[2]) || rate < 0 || price <= 0 || redemption <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[8], out int basis)
                || !ReadOddFirst(v[0], v[1], v[2], v[3], frequency, basis, c, out OddFirst odd))
            {
                return Error(CellError.Number);
            }

            return Root(y => odd.Price(rate, y, redemption, frequency) - price, rate, frequency);
        }

        /// <summary>The US days as the odd last period's lengths take them: any 31st and February's last day are the 30th, at both ends.</summary>
        private static double LongThirty(double start, double end, EvalContext c)
        {
            (int y1, int m1, int d1) = Parts(start, c.Date1904);
            (int y2, int m2, int d2) = Parts(end, c.Date1904);
            d1 = IsLastOfFebruary(y1, m1, d1) ? 30 : Math.Min(d1, 30);
            d2 = IsLastOfFebruary(y2, m2, d2) ? 30 : Math.Min(d2, 30);
            return ((y2 - y1) * 360) + ((m2 - m1) * 30) + (d2 - d1);
        }

        /// <summary>The sums over the quasi-coupon periods from the last interest date to maturity, as Excel adds them up per basis.</summary>
        private static bool ReadOddLast(double settlement, double maturity, double last, double frequency, int basis, EvalContext c, out double coupon, out double toPay, out double accrued)
        {
            coupon = toPay = accrued = 0;
            int step = 12 / (int)frequency;
            double from = last;
            (int maturityYear, int maturityMonth, _) = Parts(maturity, c.Date1904);
            (int lastYear, int lastMonth, int lastDay) = Parts(last, c.Date1904);
            bool lastAtEnd = IsMonthEnd(lastYear, lastMonth, lastDay);
            bool final = false;
            while (!final)
            {
                // Each quasi-coupon from the one before, and no month's end kept: the 31st, once shortened, stays short. From a last
                // interest date on a month's end, a coupon in maturity's own month ends the run even short of maturity, and what's
                // still to pay then stops there.
                (int y, int m, int d) = Parts(from, c.Date1904);
                double to = Shifted(y, m, d, false, step, c);
                (int toYear, int toMonth, _) = Parts(to, c.Date1904);
                final = to >= maturity || (toYear == maturityYear && toMonth == maturityMonth && lastAtEnd);
                double paidEnd = final ? maturity : to, owedEnd = Math.Min(to, maturity);
                switch (basis)
                {
                    case 0:
                        double length = LongThirty(from, to, c);
                        coupon += LongThirty(from, paidEnd, c) / length;
                        accrued += settlement > from ? Days(from, Math.Min(settlement, to), 0, c) / length : 0;
                        toPay += settlement < owedEnd ? Days(Math.Max(settlement, from), owedEnd, 0, c) / length : 0;
                        break;
                    case 4:
                        double period = Days(from, to, 4, c);
                        coupon += Days(from, paidEnd, 4, c) / period;
                        accrued += settlement > from ? Days(from, Math.Min(settlement, to), 4, c) / period : 0;
                        toPay += settlement < owedEnd ? Days(Math.Max(settlement, from), owedEnd, 4, c) / period : 0;
                        break;
                    default:
                        double days = to - from;
                        coupon += (paidEnd - from) / days;
                        accrued += settlement > from ? (Math.Min(settlement, to) - from) / days : 0;
                        toPay += settlement < owedEnd ? (owedEnd - Math.Max(settlement, from)) / days : 0;
                        break;
                }

                from = to;
            }

            return true;
        }

        private static EvalValue OddLastPrice(double[] v, EvalContext c)
        {
            double frequency = v[6];
            if (!(v[1] > v[0] && v[0] > v[2]) || v[3] < 0 || v[4] < 0 || v[5] <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[7], out int basis)
                || !ReadOddLast(v[0], v[1], v[2], frequency, basis, c, out double coupon, out double toPay, out double accrued))
            {
                return Error(CellError.Number);
            }

            double each = 100 * v[3] / frequency;
            return Number(((v[5] + (coupon * each)) / (1 + (toPay * v[4] / frequency))) - (accrued * each));
        }

        private static EvalValue OddLastYield(double[] v, EvalContext c)
        {
            double frequency = v[6];
            if (!(v[1] > v[0] && v[0] > v[2]) || v[3] < 0 || v[4] <= 0 || v[5] <= 0 || !ValidFrequency(ref frequency) || !ValidBasis(v[7], out int basis)
                || !ReadOddLast(v[0], v[1], v[2], frequency, basis, c, out double coupon, out double toPay, out double accrued))
            {
                return Error(CellError.Number);
            }

            double each = 100 * v[3] / frequency;
            double paid = v[4] + (accrued * each);
            return Number(((v[5] + (coupon * each) - paid) / paid) * frequency / toPay);
        }

        /// <summary>
        /// The yield that makes a price function nothing: a price falls as the yield rises, so the root is bracketed and closed
        /// in on by false position, with bisection when that stalls.
        /// </summary>
        private static EvalValue Root(Func<double, double> function, double start, double frequency)
        {
            double low = -frequency * 0.999, high = Math.Max(start, 0.1);
            double lowValue = function(low), highValue = function(high);
            for (int i = 0; i < 200 && highValue > 0; i++)
            {
                low = high;
                lowValue = highValue;
                high *= 2;
                highValue = function(high);
            }

            if (!(lowValue >= 0 && highValue <= 0))
            {
                return Error(CellError.Number);
            }

            for (int i = 0; i < 300 && high - low > 1e-15 * Math.Max(Math.Abs(high), 1e-3); i++)
            {
                double mid = (low + high) / 2;
                if (i % 2 == 0 && lowValue != highValue && double.IsFinite(lowValue))
                {
                    double trial = low + ((high - low) * lowValue / (lowValue - highValue));
                    if (trial > low && trial < high)
                    {
                        mid = trial;
                    }
                }

                double value = function(mid);
                if (value == 0)
                {
                    return Number(mid);
                }

                if (value > 0)
                {
                    low = mid;
                    lowValue = value;
                }
                else
                {
                    high = mid;
                    highValue = value;
                }
            }

            return Number((low + high) / 2);
        }
    }
}
