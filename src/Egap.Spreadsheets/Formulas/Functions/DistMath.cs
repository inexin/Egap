namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// The special functions behind the probability distributions. Written from the published mathematics: the Stirling series
/// and Loader's saddle-point form of the binomial and Poisson terms (C. Loader, "Fast and Accurate Computation of Binomial
/// Probabilities", 2000), Taylor series and Laplace's continued fraction for the normal distribution (Marsaglia, 2004),
/// Legendre's and the DLMF 8.17.22 continued fractions for the incomplete gamma and beta functions, and Newton's method
/// inside a bracket for the inverses.
/// </summary>
internal static class DistMath
{
    private const double LnSqrt2Pi = 0.918938533204672741780329736406;
    private const double Sqrt2Pi = 2.50662827463100050241576528481;
    private const double EulerGamma = 0.577215664901532860606512090082;
    private const double Tiny = 1e-300;
    private const double Epsilon = 1e-17;
    private const int MaxIterations = 20_000_000;
    private const double UniformGammaFrom = 1e12;
    private const long MaxInverseWork = 300_000_000;

    /// <summary>The series steps taken on this thread since it was last reset: an inverse gives up when its tails cost too much.</summary>
    [ThreadStatic]
    private static long work;

    /// <summary>ln(1 + x), accurate for small x (Kahan's form).</summary>
    public static double Log1P(double x)
    {
        double u = 1 + x;
        return u == 1 ? x : double.IsInfinity(u) ? u : Math.Log(u) * x / (u - 1);
    }

    /// <summary>e^x - 1, accurate for small x (Kahan's form).</summary>
    public static double ExpM1(double x)
    {
        double u = Math.Exp(x);
        if (u == 1)
        {
            return x;
        }

        double less = u - 1;
        return less == -1 || double.IsInfinity(u) ? less : less * x / Math.Log(u);
    }

    /// <summary>
    /// The Stirling error: ln Γ(x + 1) less (x + ½) ln x - x + ½ ln 2π. The series for large x; below that the step from x to
    /// x + 1, which is atanh(t) / t - 1 with t = 1 / (2x + 1).
    /// </summary>
    public static double StirlingError(double x)
    {
        if (x < 1e-3)
        {
            return LogGammaOnePlusSmall(x) - ((x + 0.5) * Math.Log(x)) + x - LnSqrt2Pi;
        }

        double steps = 0;
        while (x < 16)
        {
            double t = 1 / ((2 * x) + 1);
            steps += StirlingStep(t);
            x += 1;
        }

        double r = 1 / x, r2 = r * r;
        double series = r * ((1.0 / 12) - (r2 * ((1.0 / 360) - (r2 * ((1.0 / 1260) - (r2 * ((1.0 / 1680) - (r2 * ((1.0 / 1188) - (r2 * ((691.0 / 360360) - (r2 / 156))))))))))));
        return steps + series;
    }

    private static double StirlingStep(double t)
    {
        // The series is plain arithmetic, so it gives the same bits everywhere; only below x = 0.5 is it too slow.
        if (t > 0.5)
        {
            return (Math.Atanh(t) / t) - 1;
        }

        double t2 = t * t, power = t2, total = 0;
        for (int k = 3; k < 60; k += 2)
        {
            double term = power / k;
            total += term;
            if (term < 1e-18 * total)
            {
                break;
            }

            power *= t2;
        }

        return total;
    }

    /// <summary>ζ(k) - 1 for k from 2 on.</summary>
    private static readonly double[] ZetaLessOne =
    [
        0.64493406684822641, 0.20205690315959429, 0.082323233711138186, 0.036927755143369927,
        0.01734306198444914, 0.0083492773819228271, 0.0040773561979443396, 0.0020083928260822143,
        0.00099457512781808526, 0.00049418860411946453, 0.00024608655330804832, 0.00012271334757848915,
        6.1248135058704828e-05, 3.0588236307020493e-05, 1.5282259408651871e-05, 7.6371976378997626e-06,
        3.8172932649998402e-06, 1.908212716553939e-06, 9.5396203387279621e-07, 4.7693298678780645e-07,
        2.38450502727733e-07, 1.1921992596531106e-07, 5.960818905125948e-08, 2.9803503514652279e-08,
        1.4901554828365043e-08, 7.4507117898354301e-09,
    ];

    /// <summary>ln Γ(1 + x) + ln(1 + x) for |x| of ¼ or less: its Taylor series in ζ(k) - 1.</summary>
    private static double LogGammaSeries(double x)
    {
        double power = -x, total = 0;
        for (int i = 0; i < ZetaLessOne.Length; i++)
        {
            power *= -x;
            total += ZetaLessOne[i] * power / (i + 2);
        }

        return total + (x * (1 - EulerGamma));
    }

    /// <summary>ln Γ(1 + x) for |x| of ¼ or less.</summary>
    private static double LogGammaOnePlusSmall(double x) => LogGammaSeries(x) - Log1P(x);

    /// <summary>ln Γ(x) for x above 0.</summary>
    public static double LogGamma(double x)
    {
        if (x == 1 || x == 2)
        {
            return 0;
        }

        if (x < 1e-3)
        {
            return LogGammaOnePlusSmall(x) - Math.Log(x);
        }

        if (Math.Abs(x - 1) <= 0.25)
        {
            return LogGammaOnePlusSmall(x - 1);
        }

        if (Math.Abs(x - 2) <= 0.25)
        {
            return LogGammaSeries(x - 2);
        }

        return StirlingError(x) + ((x - 0.5) * Math.Log(x)) - x + LnSqrt2Pi;
    }

    /// <summary>Γ(x) for any x it exists for; infinity or NaN past that.</summary>
    public static double Gamma(double x)
    {
        if (x <= 0)
        {
            if (x == Math.Floor(x))
            {
                return double.NaN;
            }

            // Reflection: Γ(x) Γ(1 - x) = π / sin(πx), with the sine taken near its nearest multiple of π.
            double fraction = x - Math.Round(x);
            double sine = Math.Sin(Math.PI * fraction);
            if (Math.Round(x) % 2 != 0)
            {
                sine = -sine;
            }

            return Math.PI / sine / Gamma(1 - x);
        }

        if (x == Math.Floor(x) && x <= 171)
        {
            double product = 1;
            for (int i = 2; i < (int)x; i++)
            {
                product *= i;
            }

            return product;
        }

        if (x > 172)
        {
            return double.PositiveInfinity;
        }

        // Up to where the Stirling series is sharp, dividing the steps back out.
        double divisor = 1, shifted = x;
        while (shifted < 20)
        {
            divisor *= shifted;
            shifted += 1;
        }

        double half = Math.Pow(shifted, (shifted - 0.5) / 2);
        return half * Math.Exp(-shifted) * half * Sqrt2Pi * Math.Exp(StirlingError(shifted)) / divisor;
    }

    /// <summary>The deviance x ln(x / m) + m - x, by its series when x is near m.</summary>
    public static double Deviance(double x, double m)
    {
        if (Math.Abs(x - m) < 0.1 * (x + m))
        {
            double v = (x - m) / (x + m), v2 = v * v, power = 2 * x * v * v2, total = (x - m) * v;
            for (int k = 3; k < 1000; k += 2)
            {
                double next = total + (power / k);
                if (next == total)
                {
                    return next;
                }

                total = next;
                power *= v2;
            }

            return total;
        }

        return (x * Math.Log(x / m)) + m - x;
    }

    /// <summary>The Poisson term e^-λ λ^x / Γ(x + 1), for any x of 0 or more.</summary>
    public static double PoissonTerm(double x, double lambda)
    {
        if (lambda == 0)
        {
            return x == 0 ? 1 : 0;
        }

        if (x == 0)
        {
            return Math.Exp(-lambda);
        }

        if (x < 0.25)
        {
            return Math.Exp((x * Math.Log(lambda)) - lambda - LogGammaOnePlusSmall(x));
        }

        double deviance = Deviance(x, lambda);
        if (deviance > 2 && x <= 170)
        {
            // Far from the mean the deviance is a large number with a large error; the term itself keeps its digits.
            double power = Math.Pow(lambda, x), half = Math.Exp(-lambda / 2);
            if (double.IsFinite(power) && power > 0)
            {
                return power / Gamma(x + 1) * half * half;
            }
        }

        return Math.Exp(-StirlingError(x) - deviance) / Math.Sqrt(2 * Math.PI * x);
    }

    /// <summary>A running product held as a mantissa and a power of 2, so long products of powers neither overflow nor lose digits.</summary>
    private struct ScaledProduct
    {
        private double mantissa;
        private double exponent;

        public ScaledProduct()
        {
            mantissa = 1;
        }

        /// <summary>Multiplies by x^y.</summary>
        public void Power(double x, double y)
        {
            int whole = Math.ILogB(x);
            double m = Math.ScaleB(x, -whole);
            exponent += whole * y;
            double left = Math.Abs(y), sign = Math.Sign(y);
            while (left > 0)
            {
                double step = Math.Min(left, 512);
                mantissa *= Math.Pow(m, sign * step);
                int scale = Math.ILogB(mantissa);
                mantissa = Math.ScaleB(mantissa, -scale);
                exponent += scale;
                left -= step;
            }
        }

        public readonly double Value()
        {
            double floor = Math.Floor(exponent);
            if (floor > 2000 || floor < -2000)
            {
                return floor > 0 ? double.PositiveInfinity : 0;
            }

            return Math.ScaleB(mantissa * Math.Pow(2, exponent - floor), (int)floor);
        }
    }

    /// <summary>The binomial term Γ(n + 1) / (Γ(x + 1) Γ(n - x + 1)) p^x q^(n - x), for any x from 0 to n; q is 1 - p.</summary>
    public static double BinomialTerm(double x, double n, double p, double q)
    {
        if (p == 0)
        {
            return x == 0 ? 1 : 0;
        }

        if (q == 0)
        {
            return x == n ? 1 : 0;
        }

        if (x == 0)
        {
            return n == 0 ? 1 : p < 0.1 ? Math.Exp(-Deviance(n, n * q) - (n * p)) : Math.Exp(n * Math.Log(q));
        }

        if (x == n)
        {
            return q < 0.1 ? Math.Exp(-Deviance(n, n * p) - (n * q)) : Math.Exp(n * Math.Log(p));
        }

        double y = n - x;
        double stirling = StirlingError(n) - StirlingError(x) - StirlingError(y), deviance = Deviance(x, n * p) + Deviance(y, n * q);
        double root = Math.Sqrt(n / (2 * Math.PI * x * y));
        if (deviance > 2 && n <= 1e5)
        {
            // Far from the mean the deviance is a large number with a large error; the powers themselves keep their digits.
            ScaledProduct product = new();
            product.Power(n, n);
            product.Power(x, -x);
            product.Power(y, -y);
            product.Power(p, x);
            product.Power(q, y);
            return product.Value() * Math.Exp(stirling) * root;
        }

        return Math.Exp(stirling - deviance) * root;
    }

    /// <summary>The standard normal density, keeping accuracy far out by squaring the part of z that squares exactly.</summary>
    public static double NormalDensity(double z)
    {
        z = Math.Abs(z);
        double whole = Math.Floor(z * 16) / 16, rest = z - whole;
        return Math.Exp(-whole * whole / 2) * Math.Exp(-rest * (whole + z) / 2) / Sqrt2Pi;
    }

    /// <summary>Φ(z) - ½ for |z| below 2, by Taylor series.</summary>
    private static double NormalCentralSeries(double z)
    {
        double z2 = z * z, term = z, total = z;
        for (int k = 3; k < 500; k += 2)
        {
            term *= z2 / k;
            double next = total + term;
            if (next == total)
            {
                break;
            }

            total = next;
        }

        return total * NormalDensity(z);
    }

    /// <summary>Φ(z) - ½.</summary>
    public static double NormalCentral(double z) => NormalLower(z) - 0.5;

    /// <summary>The normal upper tail for z of 0 or more.</summary>
    public static double NormalTail(double z)
    {
        if (z < 1)
        {
            return 0.5 - NormalCentralSeries(z);
        }

        if (z > 39)
        {
            return 0;
        }

        // Laplace's continued fraction, z + 1 / (z + 2 / (z + 3 / ...)), from its far end.
        int terms = 24 + (int)(300 / (z * z));
        double tail = z;
        for (int k = terms; k >= 1; k--)
        {
            tail = z + (k / tail);
        }

        return NormalDensity(z) / tail;
    }

    public static double NormalLower(double z) => z < 0 ? NormalTail(-z) : 1 - NormalTail(z);

    public static double NormalUpper(double z) => z < 0 ? 1 - NormalTail(-z) : NormalTail(z);

    /// <summary>The standard normal quantile: Abramowitz and Stegun 26.2.23 to start, then Halley's steps on the tail.</summary>
    public static double NormalInverse(double p)
    {
        if (p == 0.5)
        {
            return 0;
        }

        bool upper = p > 0.5;
        double tail = upper ? 1 - p : p;
        double w = Math.Sqrt(-2 * Math.Log(tail));
        double x = w - ((2.515517 + (w * (0.802853 + (w * 0.010328)))) / (1 + (w * (1.432788 + (w * (0.189269 + (w * 0.001308)))))));
        for (int i = 0; i < 6; i++)
        {
            double density = NormalDensity(x);
            if (density == 0)
            {
                break;
            }

            double u = (NormalUpper(x) - tail) / density;
            double step = u / (1 - (x * u / 2));
            x += step;
            if (Math.Abs(step) <= 1e-16 * Math.Abs(x))
            {
                break;
            }
        }

        return upper ? x : -x;
    }

    /// <summary>The regularised lower incomplete gamma function P(a, x).</summary>
    public static double GammaLower(double a, double x)
    {
        if (x <= 0)
        {
            return 0;
        }

        return double.IsInfinity(x) ? 1 : a >= UniformGammaFrom ? 1 - GammaUniform(a, x) : GammaBySeries(a, x) ? GammaSeries(a, x) : 1 - GammaFraction(a, x);
    }

    /// <summary>The regularised upper incomplete gamma function Q(a, x).</summary>
    public static double GammaUpper(double a, double x)
    {
        if (x <= 0)
        {
            return 1;
        }

        return double.IsInfinity(x) ? 0 : a >= UniformGammaFrom ? GammaUniform(a, x) : GammaBySeries(a, x) ? 1 - GammaSeries(a, x) : GammaFraction(a, x);
    }

    /// <summary>The series has only positive terms, so it keeps its accuracy up to where the upper tail is small; the fraction takes over past that.</summary>
    private static bool GammaBySeries(double a, double x) => x < a + 1 + (1.5 * Math.Sqrt(a));

    /// <summary>
    /// Q(a, x) for a so large that the series would need too many steps: the first terms of Temme's uniform expansion (DLMF
    /// 8.12.3 to 8.12.10), which leave an error of order 1 / a times the second term.
    /// </summary>
    private static double GammaUniform(double a, double x)
    {
        double mu = (x - a) / a;
        double half = Math.Abs(mu) < 1e-2
            ? mu * mu * ((1.0 / 2) - (mu * ((1.0 / 3) - (mu * ((1.0 / 4) - (mu * ((1.0 / 5) - (mu * ((1.0 / 6) - (mu / 7))))))))))
            : mu - Log1P(mu);
        double eta = Math.Sign(mu) * Math.Sqrt(2 * half);
        double c0 = Math.Abs(eta) < 1e-3 ? (-1.0 / 3) + (eta / 12) - (2 * eta * eta / 135) : (1 / mu) - (1 / eta);
        return NormalUpper(eta * Math.Sqrt(a)) + (Math.Exp(-a * half) / Math.Sqrt(2 * Math.PI * a) * c0);
    }

    private static double GammaSeries(double a, double x)
    {
        // Kahan's compensated sum: with a large a the series runs to many small terms.
        double term = 1, total = 1, carry = 0, denominator = a;
        for (int i = 0; i < MaxIterations; i++)
        {
            denominator += 1;
            term *= x / denominator;
            double adjusted = term - carry, next = total + adjusted;
            carry = next - total - adjusted;
            total = next;

            // Well past where a term rounds away: with a large a so many follow that they still add up.
            if (term < 1e-21 * total)
            {
                work += i;
                break;
            }
        }

        return total * PoissonTerm(a, x);
    }

    private static double GammaFraction(double a, double x)
    {
        // Q = factor / (x + 1 - a - 1 (1 - a) / (x + 3 - a - 2 (2 - a) / (x + 5 - a - ...))). Lentz's method finds how far it
        // must go; working back from there keeps the rounding of thousands of steps from adding up.
        double denominator = x + 1 - a, front = 1 / Tiny, back = 1 / Guard(denominator);
        int depth = 1;
        double last = 0;
        for (; depth < MaxIterations; depth++)
        {
            double numerator = -depth * (depth - a);
            denominator += 2;
            back = 1 / Guard(denominator + (numerator * back));
            front = Guard(denominator + (numerator / front));

            // With a huge x the product can stay a rounding error away from 1 for good.
            double change = front * back;
            if (Math.Abs(change - 1) < Epsilon || (change == last && Math.Abs(change - 1) < 1e-15))
            {
                break;
            }

            last = change;
        }

        work += 3L * depth;

        // Each step's change can round to nothing while many such steps still add up, so go on a good way further.
        depth = (depth * 2) + 20;
        double value = x + 1 - a + (2.0 * depth);
        for (int i = depth; i >= 1; i--)
        {
            value = x + 1 - a + (2.0 * (i - 1)) + (-i * (i - a) / value);
        }

        return a * PoissonTerm(a, x) / value;
    }

    private static double Guard(double value) => Math.Abs(value) < Tiny ? Tiny : value;

    /// <summary>The regularised incomplete beta function I_x(a, b); y is 1 - x, given apart so it keeps its accuracy.</summary>
    public static double BetaLower(double x, double y, double a, double b)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (y <= 0)
        {
            return 1;
        }

        return x < (a + 1) / (a + b + 2) ? BetaPart(x, y, a, b) : 1 - BetaPart(y, x, b, a);
    }

    /// <summary>1 - I_x(a, b).</summary>
    public static double BetaUpper(double x, double y, double a, double b)
    {
        if (x <= 0)
        {
            return 1;
        }

        if (y <= 0)
        {
            return 0;
        }

        return x < (a + 1) / (a + b + 2) ? 1 - BetaPart(x, y, a, b) : BetaPart(y, x, b, a);
    }

    private static double BetaPart(double x, double y, double a, double b)
    {
        double sum = BetaSum(x, a, b);
        return double.IsNaN(sum) ? BetaFraction(x, y, a, b) : BinomialTerm(a, a + b, x, y) * b / (a + b) * sum;
    }

    /// <summary>
    /// DLMF 8.17.8: 1 + the sum of the products of (a + b + k) x / (a + 1 + k). All its terms are positive, so unlike the
    /// fraction it keeps its accuracy with large a and b; NaN when x is too near 1 for it to end soon. The k-th term holds the
    /// k-th power of x, so with many terms the rounding of x itself shows: <paramref name="xError"/> is that rounding, relative
    /// to x, and is taken out at the end.
    /// </summary>
    private static double BetaSum(double x, double a, double b, double xError = 0)
    {
        double term = 1, total = 1, carry = 0, moment = 0;
        for (int k = 0; k < 4_000_000; k++)
        {
            term *= (a + b + k) / (a + 1 + k) * x;
            double adjusted = term - carry, next = total + adjusted;
            carry = next - total - adjusted;
            total = next;
            moment += (k + 1) * term;
            if (term < 1e-21 * total)
            {
                work += k;
                return total + (xError * moment);
            }
        }

        work += 4_000_000;
        return double.NaN;
    }

    private static double BetaFraction(double x, double y, double a, double b)
    {
        // DLMF 8.17.22: I = factor / (1 + d1 / (1 + d2 / (1 + ...))). Lentz's method finds how far it must go; working back
        // from there keeps the rounding of thousands of steps from adding up.
        double Even(double m) => m * (b - m) * x / ((a + (2 * m) - 1) * (a + (2 * m)));
        double Odd(double m) => -(a + m) * (a + b + m) * x / ((a + (2 * m)) * (a + (2 * m) + 1));
        double front = 1, back = 1 / Guard(1 + Odd(0));
        int depth = 1;
        for (; depth < MaxIterations; depth++)
        {
            back = 1 / Guard(1 + (Even(depth) * back));
            front = Guard(1 + (Even(depth) / front));
            double change = front * back;
            back = 1 / Guard(1 + (Odd(depth) * back));
            front = Guard(1 + (Odd(depth) / front));
            change *= front * back;
            if (Math.Abs(change - 1) < Epsilon)
            {
                break;
            }
        }

        work += 3L * depth;

        // Each step's change can round to nothing while many such steps still add up, so go on a good way further.
        depth = (depth * 2) + 20;
        double value = 1;
        for (int m = depth; m >= 1; m--)
        {
            value = Guard(1 + (Odd(m) / value));
            value = Guard(1 + (Even(m) / value));
        }

        value = Guard(1 + (Odd(0) / value));
        return BinomialTerm(a, a + b, x, y) * b / (a + b) / value;
    }

    /// <summary>The beta density at x, with y = 1 - x.</summary>
    public static double BetaDensity(double x, double y, double a, double b)
    {
        if (x <= 0)
        {
            return a < 1 ? double.PositiveInfinity : a == 1 ? b : 0;
        }

        if (y <= 0)
        {
            return b < 1 ? double.PositiveInfinity : b == 1 ? a : 0;
        }

        return BinomialTerm(a, a + b, x, y) * (a / x) * (b / (a + b)) / y;
    }

    /// <summary>The density of the gamma distribution with scale 1 at x.</summary>
    public static double GammaDensity(double x, double a)
    {
        if (x <= 0)
        {
            return a < 1 ? double.PositiveInfinity : a == 1 ? 1 : 0;
        }

        return PoissonTerm(a, x) * a / x;
    }

    /// <summary>The density of Student's t with df degrees of freedom.</summary>
    public static double StudentDensity(double t, double df)
    {
        if (df == 1)
        {
            return 1 / (Math.PI * (1 + (t * t)));
        }

        // Γ(a + ½) / Γ(a) with a = df / 2, kept to a small exponent through the Stirling errors.
        double a = df / 2;
        double ratio = (a * Log1P(-0.5 / a)) + 0.5 + StirlingError(a - 0.5) - StirlingError(a);
        double q = t / df * t;
        return Math.Exp(ratio - ((a + 0.5) * Log1P(q))) / Sqrt2Pi;
    }

    /// <summary>The upper tail of Student's t for t of 0 or more.</summary>
    public static double StudentTail(double t, double df)
    {
        if (t == 0)
        {
            return 0.5;
        }

        double r = df / t / t;
        if (double.IsInfinity(r))
        {
            return 0.5;
        }

        // The incomplete beta function of x = df / (df + t squared), a = df / 2, b = ½, with its factor taken from the density:
        // with a large df, x^a from a rounded x would lose digits.
        double x = r / (1 + r), y = 1 / (1 + r), a = df / 2, density = StudentDensity(t, df);
        if (density < 1e-280)
        {
            // So far out the density underflows, while the tail, a power of t higher, may still be there.
            return BetaLower(x, y, a, 0.5) / 2;
        }

        if (x < (a + 1) / (a + 2.5))
        {
            // x = df / (df + t t) to more than double precision: what the sum needs to know of the rounding in x.
            double square = t * t, whole = df + square, squareError = Math.FusedMultiplyAdd(t, t, -square);
            double wholeError = df - (whole - square) + squareError;
            x = df / whole;
            double xError = (Math.FusedMultiplyAdd(-x, whole, df) - (x * wholeError)) / df;
            double tailSum = BetaSum(x, a, 0.5, xError);
            if (!double.IsNaN(tailSum))
            {
                return density * (t / df) * tailSum;
            }
        }

        double middleSum = BetaSum(y, 0.5, a);
        return double.IsNaN(middleSum) ? BetaLower(x, y, a, 0.5) / 2 : 0.5 - (density * t * middleSum);
    }

    public static double StudentLower(double t, double df) => t < 0 ? StudentTail(-t, df) : 1 - StudentTail(t, df);

    /// <summary>The positive t whose upper tail is <paramref name="tail"/>, of ½ or less.</summary>
    public static double StudentInverseTail(double tail, double df)
    {
        if (tail >= 0.5)
        {
            return 0;
        }

        if (df == 1)
        {
            return 1 / Math.Tan(Math.PI * tail);
        }

        if (df == 2)
        {
            return (1 - (2 * tail)) / Math.Sqrt(2 * tail * (1 - tail));
        }

        return Invert(1 - tail, tail, x => 1 - StudentTail(x, df), x => StudentTail(x, df), x => StudentDensity(x, df), 0, double.PositiveInfinity, Math.Abs(NormalInverse(tail)));
    }

    public static double FLower(double x, double d1, double d2)
    {
        if (x <= 0)
        {
            return 0;
        }

        double top = d1 * x, bottom = top + d2;
        return BetaLower(top / bottom, d2 / bottom, d1 / 2, d2 / 2);
    }

    public static double FUpper(double x, double d1, double d2)
    {
        if (x <= 0)
        {
            return 1;
        }

        double top = d1 * x, bottom = top + d2;
        return BetaUpper(top / bottom, d2 / bottom, d1 / 2, d2 / 2);
    }

    public static double FDensity(double x, double d1, double d2)
    {
        if (x <= 0)
        {
            return d1 < 2 ? double.PositiveInfinity : d1 == 2 ? 1 : 0;
        }

        double top = d1 * x, bottom = top + d2;
        return BetaDensity(top / bottom, d2 / bottom, d1 / 2, d2 / 2) * (d1 / bottom) * (d2 / bottom);
    }

    /// <summary>The x above 0 where a distribution reaches lower tail p (upper tail q), with x's search range and a first guess.</summary>
    public static double Invert(double p, double q, Func<double, double> lower, Func<double, double> upper, Func<double, double> density, double low, double high, double guess)
    {
        bool useLower = p <= q;
        double target = useLower ? p : q;
        Func<double, double> tail = useLower ? lower : upper;
        double x = guess;
        if (!(x > low && x < high))
        {
            x = double.IsInfinity(high) ? low + 1 : (low + high) / 2;
        }

        double best = x;
        work = 0;
        for (int i = 0; i < 400; i++)
        {
            // With huge parameters every tail takes millions of steps. NaN: Excel's answers there aren't reachable this way.
            if (work > MaxInverseWork)
            {
                return double.NaN;
            }

            double t = tail(x);
            best = x;
            if (t == target)
            {
                return x;
            }

            bool xTooHigh = useLower ? t > target : t < target;
            if (xTooHigh)
            {
                high = x;
            }
            else
            {
                low = x;
            }

            double next = double.NaN;
            double d = density(x);
            if (t > 0 && d > 0 && double.IsFinite(d))
            {
                // Newton's step on the logarithm of the tail, which is close to straight far out.
                double relative = (t - target) / target;
                double logRatio = Math.Abs(relative) < 0.5 ? Log1P(relative) : Math.Log(t / target);
                double step = logRatio * t / d;
                next = useLower ? x - step : x + step;
            }

            if (!(next > low && next < high))
            {
                next = double.IsInfinity(high) ? (x <= 0 ? 1 : x * 4)
                    : low > 0 && high / low > 4 ? Math.Sqrt(low) * Math.Sqrt(high)
                    : low == 0 && high > 1e-300 ? high / 1024
                    : (low + high) / 2;
                if (next <= low || next >= high)
                {
                    return x;
                }
            }

            if (Math.Abs(next - x) <= 2.3e-16 * Math.Abs(x))
            {
                return next;
            }

            x = next;
        }

        return best;
    }

    /// <summary>The gamma quantile with scale 1.</summary>
    public static double GammaInverse(double p, double q, double a)
    {
        // Wilson and Hilferty's cube-root normal approximation to start.
        double z = NormalInverse(p <= q ? p : 1 - q), v = 1 / (9 * a);
        double cube = 1 - v + (z * Math.Sqrt(v));
        double guess = cube > 0 ? a * cube * cube * cube : Math.Exp((Math.Log(Math.Max(p, 1e-300)) + LogGamma(a + 1)) / a);
        return Invert(p, q, x => GammaLower(a, x), x => GammaUpper(a, x), x => GammaDensity(x, a), 0, double.PositiveInfinity, guess);
    }

    /// <summary>The beta quantile on 0 to 1.</summary>
    public static double BetaInverse(double p, double q, double a, double b) =>
        Invert(p, q, x => BetaLower(x, 1 - x, a, b), x => BetaUpper(x, 1 - x, a, b), x => BetaDensity(x, 1 - x, a, b), 0, 1, a / (a + b));

    /// <summary>The F quantile.</summary>
    public static double FInverse(double p, double q, double d1, double d2) =>
        Invert(p, q, x => FLower(x, d1, d2), x => FUpper(x, d1, d2), x => FDensity(x, d1, d2), 0, double.PositiveInfinity, 1);
}
