namespace Egap.Documents.Charts;

/// <summary>The numbers along a value axis: its ends, the step between its labels and whether it counts in powers.</summary>
internal readonly record struct ChartScale(double Minimum, double Maximum, double Major, double Minor, double LogBase = 0)
{
    /// <summary>Whether the axis counts in powers of <see cref="LogBase"/>.</summary>
    public bool IsLog => LogBase > 1;

    /// <summary>Where <paramref name="value"/> falls along the axis, from 0 at the minimum to 1 at the maximum.</summary>
    public double Place(double value)
    {
        if (IsLog)
        {
            double low = Math.Log(Minimum, LogBase);
            double high = Math.Log(Maximum, LogBase);
            return value <= 0 || high <= low ? 0 : (Math.Log(value, LogBase) - low) / (high - low);
        }

        return Maximum > Minimum ? (value - Minimum) / (Maximum - Minimum) : 0;
    }

    /// <summary>The label values, from the minimum up; a sane number however large the range is against the step.</summary>
    public List<double> Ticks()
    {
        List<double> ticks = [];
        if (IsLog)
        {
            for (double v = Minimum; v <= Maximum * 1.000001 && ticks.Count < 400; v *= Math.Max(Major, 1.0000001))
            {
                ticks.Add(v);
            }

            return ticks;
        }

        int count = Major > 0 ? (int)Math.Min(Math.Floor(((Maximum - Minimum) / Major) + 1e-9), 1000) : 0;
        for (int i = 0; i <= count; i++)
        {
            // A tick that should be zero can come out a rounding error away from it.
            double tick = Clean(Minimum + (i * Major));
            ticks.Add(Math.Abs(tick) < Major * 1e-9 ? 0 : tick);
        }

        return ticks;
    }

    /// <summary>
    /// Excel's automatic scale for data from <paramref name="low"/> to <paramref name="high"/>: the axis starts at zero unless the data
    /// spans less than a sixth of its largest value, and ends a twentieth of the span past the data, rounded out to the step. The step is
    /// the smallest of 1, 2 and 5 times a power of ten that gives at most ten steps and whose labels <paramref name="fits"/> says have room.
    /// </summary>
    public static ChartScale Automatic(double low, double high, Func<ChartScale, bool> fits, double? fixedMinimum, double? fixedMaximum, double? fixedMajor, double? fixedMinor, double headroom = 0.05)
    {
        if (!double.IsFinite(low) || !double.IsFinite(high))
        {
            low = 0;
            high = 0;
        }

        if (low > high)
        {
            (low, high) = (high, low);
        }

        double span = high - low;
        double bottom;
        double top;
        if (span == 0 && low == 0)
        {
            bottom = 0;
            top = 1;
        }
        else if (low >= 0)
        {
            bottom = span / high > 1.0 / 6 || span == 0 ? 0 : low - (span / 2);
            top = high + (headroom * span);
        }
        else if (high <= 0)
        {
            top = span / -low > 1.0 / 6 || span == 0 ? 0 : high + (span / 2);
            bottom = low - (0.05 * span);
        }
        else
        {
            bottom = low - (0.05 * span);
            top = high + (headroom * span);
        }

        if (fixedMinimum is { } setLow)
        {
            bottom = setLow;
        }

        if (fixedMaximum is { } setHigh)
        {
            top = setHigh;
        }

        if (!(top > bottom))
        {
            top = bottom + 1;
        }

        double major = fixedMajor is > 0 ? fixedMajor.Value : Step(bottom, top, fits, fixedMinimum.HasValue, fixedMaximum.HasValue);
        double minimum = fixedMinimum ?? Clean(Math.Floor((bottom / major) + 1e-9) * major);
        double maximum = fixedMaximum ?? Clean(Math.Ceiling((top / major) - 1e-9) * major);
        if (!(maximum > minimum))
        {
            maximum = minimum + major;
        }

        return new ChartScale(minimum, maximum, major, fixedMinor is > 0 ? fixedMinor.Value : major / (major.ToString("R", System.Globalization.CultureInfo.InvariantCulture)[0] == '2' ? 4 : 5));
    }

    /// <summary>A logarithmic scale: whole powers of the base around the data.</summary>
    public static ChartScale Logarithmic(double low, double high, double logBase, double? fixedMinimum, double? fixedMaximum, double? fixedMajor)
    {
        low = low > 0 && double.IsFinite(low) ? low : 1;
        high = high > 0 && double.IsFinite(high) ? high : low;
        double minimum = fixedMinimum is > 0 ? fixedMinimum.Value : Math.Pow(logBase, Math.Floor(Math.Log(low, logBase) + 1e-9));
        double maximum = fixedMaximum is > 0 ? fixedMaximum.Value : Math.Pow(logBase, Math.Ceiling(Math.Log(high, logBase) - 1e-9));
        if (!(maximum > minimum))
        {
            maximum = minimum * logBase;
        }

        return new ChartScale(minimum, maximum, fixedMajor is > 1 ? fixedMajor.Value : logBase, logBase, logBase);
    }

    /// <summary>A value without the noise adding steps leaves, as Excel shows up to 15 digits.</summary>
    public static double Clean(double value) => value == 0 || !double.IsFinite(value) ? value : double.Parse(value.ToString("G15", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

    private static double Step(double bottom, double top, Func<ChartScale, bool> fits, bool lowFixed, bool highFixed)
    {
        double span = top - bottom;
        double step = Math.Pow(10, Math.Floor(Math.Log10(span)) - 2);
        int[] multiples = [1, 2, 5];
        double last = step;
        for (int round = 0; round < 8; round++)
        {
            foreach (int multiple in multiples)
            {
                double major = step * multiple;
                double from = lowFixed ? bottom : Clean(Math.Floor((bottom / major) + 1e-9) * major);
                double to = highFixed ? top : Clean(Math.Ceiling((top / major) - 1e-9) * major);
                double count = (to - from) / major;
                if (count <= 0)
                {
                    continue;
                }

                last = major;
                if (count <= 10 + 1e-9 && fits(new ChartScale(from, to, major, major)))
                {
                    return major;
                }
            }

            step *= 10;
        }

        return last;
    }
}
