namespace Egap.Spreadsheets.Formulas;

internal static partial class FormulaFunctions
{
    /// <summary>Series of payments: their value now and the rate that makes it nothing.</summary>
    private static class CashFlows
    {
        public static EvalValue NetPresentValue(EvalValue[] a, EvalContext c)
        {
            if (!Num(a[0], c, out double rate, out EvalValue error))
            {
                return error;
            }

            List<double> flows = [];
            if (Gather(a, c, flows, first: 1) is { } failed)
            {
                return failed;
            }

            if (rate == -1)
            {
                return Error(CellError.DivisionByZero);
            }

            double total = 0;
            double discount = 1;
            foreach (double flow in flows)
            {
                discount /= 1 + rate;
                total += flow * discount;
            }

            return Number(total);
        }

        /// <summary>The numbers of a cash-flow argument in order: text, booleans and blanks in a range or an array don't count.</summary>
        private static EvalValue? Flows(in EvalValue argument, EvalContext context, List<double> flows)
        {
            foreach ((EvalValue value, bool direct) in Scalars(argument))
            {
                switch (value.Kind)
                {
                    case EvalKind.Error:
                        return value;
                    case EvalKind.Number:
                        flows.Add(value.Number);
                        break;
                    default:
                        if (direct)
                        {
                            return Error(CellError.Value);
                        }

                        break;
                }
            }

            return null;
        }

        /// <summary>IRR: Newton's method on the net present value from the guess; a step past -1 goes halfway there instead.</summary>
        public static EvalValue InternalRate(EvalValue[] a, EvalContext c)
        {
            List<double> flows = [];
            if (Flows(a[0], c, flows) is { } failed)
            {
                // Excel gives #VALUE! for an error among the cash flows, not the error itself, unless it is the one value given.
                return a[0].IsScalar ? failed : Error(CellError.Value);
            }

            if (!Optional(a, 1, 0.1, c, out double guess, out EvalValue error))
            {
                return error;
            }

            if (guess <= -1)
            {
                return Error(CellError.Value);
            }

            if (!flows.Exists(f => f > 0) || !flows.Exists(f => f < 0))
            {
                return Error(CellError.Number);
            }

            double rate = guess;
            for (int i = 0; i < 1000; i++)
            {
                double value = 0, slope = 0, discount = 1, x = 1 + rate;
                for (int t = 0; t < flows.Count; t++)
                {
                    value += flows[t] * discount;
                    discount /= x;
                    slope -= t * flows[t] * discount;
                }

                if (!double.IsFinite(value) || !double.IsFinite(slope) || slope == 0)
                {
                    return Error(CellError.Number);
                }

                double next = rate - (value / slope);
                if (next <= -1)
                {
                    next = (rate - 1) / 2;
                }

                if (Math.Abs(next - rate) <= 1e-7 * Math.Max(Math.Abs(next), 1e-7))
                {
                    double found = Polish(next, flows);
                    return Number(Math.Abs(found) < 1e-15 ? 0 : found);
                }

                rate = next;
            }

            return Error(CellError.Number);
        }

        private static double Polish(double rate, List<double> flows)
        {
            for (int i = 0; i < 3; i++)
            {
                double value = 0, slope = 0, discount = 1, x = 1 + rate;
                for (int t = 0; t < flows.Count; t++)
                {
                    value += flows[t] * discount;
                    discount /= x;
                    slope -= t * flows[t] * discount;
                }

                if (slope == 0 || !double.IsFinite(value / slope))
                {
                    break;
                }

                rate -= value / slope;
            }

            return rate;
        }

        public static EvalValue ModifiedRate(EvalValue[] a, EvalContext c)
        {
            List<double> flows = [];
            if (Flows(a[0], c, flows) is { } failed)
            {
                return failed;
            }

            if (!Num(a[1], c, out double finance, out EvalValue error) || !Num(a[2], c, out double reinvest, out error))
            {
                return error;
            }

            if (reinvest == -1 || flows.Count < 2 || !flows.Exists(f => f < 0))
            {
                return Error(CellError.DivisionByZero);
            }

            if (!flows.Exists(f => f > 0))
            {
                return Number(-1);
            }

            // Both sides by NPV, the way the reference gives it: Excel's digits follow this order of the sums.
            double gains = 0, costs = 0;
            int n = flows.Count;
            for (int t = 0; t < n; t++)
            {
                if (flows[t] > 0)
                {
                    gains += flows[t] / Math.Pow(1 + reinvest, t + 1);
                }
                else
                {
                    costs += flows[t] == 0 ? 0 : flows[t] / Math.Pow(1 + finance, t + 1);
                }
            }

            // A financing rate of -1 leaves only the payments made at the start.
            double spent = finance == -1 ? flows[0] : costs * (1 + finance);
            return Number(Math.Pow(-gains * Math.Pow(1 + reinvest, n) / spent, 1.0 / (n - 1)) - 1);
        }

        /// <summary>
        /// The payments and whole-day dates of XNPV and XIRR: two arguments of the same size, every date a day of the calendar and
        /// none before the first. <paramref name="forRate"/> is XIRR, which passes over a payment left blank and calls anything
        /// else not a number #VALUE!; XNPV calls those #NUM!. A single value is read as a value.
        /// </summary>
        private static EvalValue? Dated(in EvalValue values, in EvalValue dates, EvalContext c, bool forRate, out double[] flows, out double[] days)
        {
            flows = days = [];
            if (Grid.Of(values) is not { } flowGrid || Grid.Of(dates) is not { } dayGrid)
            {
                return Error(CellError.Value);
            }

            long count = flowGrid.Count;
            if (count != dayGrid.Count)
            {
                return Error(CellError.Number);
            }

            List<double> flowList = new((int)count), dayList = new((int)count);
            EvalValue notNumber = Error(forRate ? CellError.Value : CellError.Number);
            for (int i = 0; i < count; i++)
            {
                EvalValue flow = flowGrid[i / flowGrid.Columns, i % flowGrid.Columns];
                EvalValue day = dayGrid[i / dayGrid.Columns, i % dayGrid.Columns];
                if (flow.IsError)
                {
                    return flow;
                }

                if (day.IsError)
                {
                    return day;
                }

                if (forRate && flow.Kind == EvalKind.Blank)
                {
                    continue;
                }

                double payment = flow.Number, date = day.Number;
                if (count == 1)
                {
                    if (!Strict(flow, c, true, out payment, out EvalValue error) || !Strict(day, c, true, out date, out error))
                    {
                        return error;
                    }
                }
                else if (flow.Kind != EvalKind.Number || day.Kind != EvalKind.Number)
                {
                    return notNumber;
                }

                date = Math.Truncate(date);
                if (date < (forRate ? 0 : 1) || date > LastSerial(c) || (dayList.Count > 0 && date < dayList[0]))
                {
                    return Error(CellError.Number);
                }

                flowList.Add(payment);
                dayList.Add(date);
            }

            flows = [.. flowList];
            days = [.. dayList];
            return null;
        }

        public static EvalValue DatedPresentValue(EvalValue[] a, EvalContext c)
        {
            if (!Strict(a[0], c, true, out double rate, out EvalValue error))
            {
                return error;
            }

            if (Dated(a[1], a[2], c, forRate: false, out double[] flows, out double[] days) is { } failed)
            {
                return failed;
            }

            if (rate <= 0)
            {
                return Error(CellError.Number);
            }

            double total = 0;
            for (int i = 0; i < flows.Length; i++)
            {
                total += flows[i] / Math.Pow(1 + rate, (days[i] - days[0]) / 365);
            }

            return Number(total);
        }

        /// <summary>
        /// XIRR. Excel closes in on the rate by halving, from the guess up when the value there is still on the first payment's
        /// far side and from the guess down to -1 otherwise, so a rate it can't bracket that way is #NUM!, and it stops about 1e-8
        /// short. This finds the same rate in the same bracket, to the last digit. A rate of 0, and a first payment of 0, come out
        /// as the first of the guess's halves under 5e-9, which is what Excel's halving ends on.
        /// </summary>
        public static EvalValue DatedRate(EvalValue[] a, EvalContext c)
        {
            if (Dated(a[0], a[1], c, forRate: true, out double[] flows, out double[] days) is { } failed)
            {
                return failed;
            }

            double guess = 0.1;
            if (a.Length > 2 && !Strict(a[2], c, true, out guess, out EvalValue error))
            {
                return error;
            }

            if (flows.Length < 2)
            {
                return Error(CellError.NotAvailable);
            }

            if (guess <= -1 || !flows.Any(f => f > 0) || !flows.Any(f => f < 0))
            {
                return Error(CellError.Number);
            }

            if (flows[0] == 0)
            {
                return Number(Halved(guess));
            }

            double side = flows[0] < 0 ? 1 : -1;
            double Value(double rate)
            {
                double total = 0;
                for (int t = 0; t < flows.Length; t++)
                {
                    total += flows[t] / Math.Pow(1 + rate, (days[t] - days[0]) / 365);
                }

                return total * side;
            }

            double low, high;
            if (Value(guess) > 0)
            {
                if (guess < 0)
                {
                    return Error(CellError.Number);
                }

                low = guess;
                high = Math.Max(guess * 2, 0.1);
                while (Value(high) > 0)
                {
                    low = high;
                    high *= 2;
                    if (double.IsInfinity(high))
                    {
                        return Error(CellError.Number);
                    }
                }
            }
            else
            {
                high = guess;
                low = -1 + 1e-10;
                if (flows[^1] * side < 0 || Value(low) <= 0)
                {
                    return Error(CellError.Number);
                }
            }

            for (int i = 0; i < 200 && high - low > 1e-15 * Math.Abs(high); i++)
            {
                double middle = (low + high) / 2;
                double value = Value(middle);
                if (value > 0 || double.IsNaN(value))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            double found = (low + high) / 2;
            return Number(Math.Abs(found) < 1e-9 ? Halved(guess) : found);
        }

        /// <summary>The guess halved until under 5e-9; a guess of 0 begins from 1e-5.</summary>
        private static double Halved(double guess)
        {
            double step = guess == 0 ? 1e-5 : Math.Abs(guess);
            while (step >= 5e-9)
            {
                step /= 2;
            }

            return step;
        }

        /// <summary>FVSCHEDULE: blanks grow by nothing; text in cells that reads as a number counts; booleans are #VALUE!.</summary>
        public static EvalValue Schedule(EvalValue[] a, EvalContext c)
        {
            if (!Strict(a[0], c, true, out double value, out EvalValue error))
            {
                return error;
            }

            if (a[1].Kind == EvalKind.Missing)
            {
                return Error(CellError.NotAvailable);
            }

            foreach ((EvalValue item, bool _) in Scalars(a[1]))
            {
                if (!Strict(item, c, true, out double rate, out error))
                {
                    return error;
                }

                value *= 1 + rate;
            }

            return Number(value);
        }
    }
}
