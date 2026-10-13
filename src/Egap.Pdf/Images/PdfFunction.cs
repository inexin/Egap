using System.Globalization;

namespace Egap.Pdf.Images;

/// <summary>
/// A PDF function (7.10): sampled (type 0), exponential (type 2), stitching (type 3) or PostScript calculator (type 4),
/// evaluated as the spec says, with inputs clipped to the domain and outputs to the range. Used for the tint transforms of
/// Separation and DeviceN colour spaces, and later for shadings. A function that cannot be read throws
/// <see cref="InvalidDataException"/> when it is built, never when it is evaluated.
/// </summary>
internal abstract class PdfFunction
{
    private const int MaxDepth = 8;

    protected PdfFunction(PdfDictionary dictionary)
    {
        Domain = Numbers(dictionary.Get<PdfArray>("Domain")) is { Length: >= 2 } domain ? domain : throw new InvalidDataException("A function has no /Domain.");
        Range = Numbers(dictionary.Get<PdfArray>("Range"));
    }

    /// <summary>The number of inputs.</summary>
    public int Inputs => Domain.Length / 2;

    /// <summary>The number of outputs, when the function says (Range, C0, the sample count); -1 when unknown until evaluated.</summary>
    public abstract int Outputs { get; }

    protected double[] Domain { get; }

    protected double[]? Range { get; }

    /// <summary>Reads a function from a dictionary or stream, or an array of one-output functions acting as one.</summary>
    public static PdfFunction Read(PdfObject value)
    {
        return Read(value, 0);
    }

    /// <summary>Evaluates the function; <paramref name="output"/> must hold <see cref="Outputs"/> numbers.</summary>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> clipped = stackalloc double[Math.Min(input.Length, 32)];
        for (int i = 0; i < clipped.Length; i++)
        {
            double v = input[i];
            clipped[i] = 2 * i + 1 < Domain.Length ? Math.Clamp(double.IsNaN(v) ? Domain[2 * i] : v, Domain[2 * i], Math.Max(Domain[2 * i], Domain[(2 * i) + 1])) : v;
        }

        Run(clipped, output);
        if (Range is not null)
        {
            for (int i = 0; i < output.Length && (2 * i) + 1 < Range.Length; i++)
            {
                output[i] = Math.Clamp(double.IsNaN(output[i]) ? Range[2 * i] : output[i], Range[2 * i], Math.Max(Range[2 * i], Range[(2 * i) + 1]));
            }
        }
    }

    protected abstract void Run(ReadOnlySpan<double> input, Span<double> output);

    protected static double[]? Numbers(PdfArray? array)
    {
        if (array is null)
        {
            return null;
        }

        double[] numbers = new double[array.Count];
        for (int i = 0; i < numbers.Length; i++)
        {
            numbers[i] = array.GetNumber(i) ?? 0;
        }

        return numbers;
    }

    private static PdfFunction Read(PdfObject value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Functions nest too deep.");
        }

        PdfObject resolved = value.Resolve();
        if (resolved is PdfArray array)
        {
            return new ArrayFunction([.. array.Select(item => Read(item, depth + 1))]);
        }

        PdfDictionary dictionary = resolved switch
        {
            PdfStream stream => stream.Dictionary,
            PdfDictionary d => d,
            _ => throw new InvalidDataException("A function is not a dictionary or stream."),
        };
        return dictionary.GetInt("FunctionType") switch
        {
            0 when resolved is PdfStream stream => new SampledFunction(stream),
            2 => new ExponentialFunction(dictionary),
            3 => new StitchingFunction(dictionary, depth),
            4 when resolved is PdfStream stream => new PostScriptFunction(stream),
            _ => throw new InvalidDataException($"A function has the unknown type {dictionary.GetInt("FunctionType")}."),
        };
    }

    /// <summary>Several one-output functions acting as one, each giving one output (as a shading's /Function array).</summary>
    private sealed class ArrayFunction : PdfFunction
    {
        private readonly PdfFunction[] parts;

        public ArrayFunction(PdfFunction[] parts)
            : base(new PdfDictionary { ["Domain"] = new PdfArray { new PdfReal(double.MinValue), new PdfReal(double.MaxValue) } })
        {
            this.parts = parts.Length > 0 ? parts : throw new InvalidDataException("A function array is empty.");
        }

        public override int Outputs => parts.Length;

        protected override void Run(ReadOnlySpan<double> input, Span<double> output)
        {
            Span<double> one = stackalloc double[Math.Max(1, parts.Max(p => Math.Max(p.Outputs, 1)))];
            for (int i = 0; i < parts.Length && i < output.Length; i++)
            {
                parts[i].Evaluate(input, one[..Math.Max(parts[i].Outputs, 1)]);
                output[i] = one[0];
            }
        }
    }

    /// <summary>Type 0: samples in a table, interpolated linearly in each input (cubic order is read as linear, as pdf.js does).</summary>
    private sealed class SampledFunction : PdfFunction
    {
        private readonly int[] size;
        private readonly double[] encode;
        private readonly double[] decode;
        private readonly double[] samples;
        private readonly int outputs;

        public SampledFunction(PdfStream stream)
            : base(stream.Dictionary)
        {
            PdfDictionary d = stream.Dictionary;
            double[] range = Range ?? throw new InvalidDataException("A sampled function has no /Range.");
            outputs = range.Length / 2;
            size = [.. (Numbers(d.Get<PdfArray>("Size")) ?? throw new InvalidDataException("A sampled function has no /Size.")).Select(v => (int)Math.Clamp(v, 1, 1 << 16))];
            if (size.Length < Inputs || outputs == 0)
            {
                throw new InvalidDataException("A sampled function's /Size or /Range does not fit its /Domain.");
            }

            int bits = (int)(d.GetInt("BitsPerSample") ?? 8);
            if (bits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
            {
                throw new InvalidDataException($"A sampled function has {bits} bits per sample.");
            }

            encode = Numbers(d.Get<PdfArray>("Encode")) is { } e && e.Length >= 2 * Inputs ? e : [.. size.Take(Inputs).SelectMany(s => new double[] { 0, s - 1 })];
            decode = Numbers(d.Get<PdfArray>("Decode")) is { } dec && dec.Length >= 2 * outputs ? dec : range;
            long count = size.Take(Inputs).Aggregate(1L, (a, s) => a * s) * outputs;
            if (count > 1 << 24)
            {
                throw new InvalidDataException("A sampled function has too many samples.");
            }

            byte[] data = stream.GetDecodedBytes();
            samples = new double[count];
            double max = Math.Pow(2, bits) - 1;
            long bit = 0;
            for (long i = 0; i < count; i++, bit += bits)
            {
                samples[i] = Read(data, bit, bits) / max;
            }
        }

        public override int Outputs => outputs;

        protected override void Run(ReadOnlySpan<double> input, Span<double> output)
        {
            int m = Inputs;
            Span<double> position = stackalloc double[m];
            for (int i = 0; i < m; i++)
            {
                double x = input[i];
                double e = Interpolate(x, Domain[2 * i], Domain[(2 * i) + 1], encode[2 * i], encode[(2 * i) + 1]);
                position[i] = Math.Clamp(e, 0, size[i] - 1);
            }

            // Multilinear interpolation over the 2^m corners around the position.
            for (int o = 0; o < outputs && o < output.Length; o++)
            {
                double sum = 0;
                for (int corner = 0; corner < 1 << m; corner++)
                {
                    double weight = 1;
                    long index = 0, stride = 1;
                    for (int i = 0; i < m; i++)
                    {
                        int low = (int)Math.Floor(position[i]);
                        double frac = position[i] - low;
                        int at = (corner >> i & 1) == 1 ? Math.Min(low + 1, size[i] - 1) : low;
                        weight *= (corner >> i & 1) == 1 ? frac : 1 - frac;
                        index += at * stride;
                        stride *= size[i];
                    }

                    if (weight != 0)
                    {
                        sum += weight * samples[(index * outputs) + o];
                    }
                }

                output[o] = Interpolate(sum, 0, 1, decode[2 * o], decode[(2 * o) + 1]);
            }
        }

        private static double Read(byte[] data, long bit, int bits)
        {
            ulong value = 0;
            for (int i = 0; i < bits; i++, bit++)
            {
                long index = bit >> 3;
                int b = index < data.Length ? (data[index] >> (7 - (int)(bit & 7))) & 1 : 0;
                value = (value << 1) | (uint)b;
            }

            return value;
        }
    }

    /// <summary>Type 2: C0 + x^N (C1 − C0).</summary>
    private sealed class ExponentialFunction : PdfFunction
    {
        private readonly double[] c0;
        private readonly double[] c1;
        private readonly double n;

        public ExponentialFunction(PdfDictionary d)
            : base(d)
        {
            c0 = Numbers(d.Get<PdfArray>("C0")) ?? [0];
            c1 = Numbers(d.Get<PdfArray>("C1")) ?? [1];
            n = d.GetNumber("N") ?? 1;
            if (c1.Length != c0.Length)
            {
                throw new InvalidDataException("An exponential function's /C0 and /C1 differ in length.");
            }
        }

        public override int Outputs => c0.Length;

        protected override void Run(ReadOnlySpan<double> input, Span<double> output)
        {
            double x = input.Length > 0 ? input[0] : 0;
            double p = n == 1 ? x : Math.Pow(x, n);
            for (int i = 0; i < c0.Length && i < output.Length; i++)
            {
                output[i] = c0[i] + (p * (c1[i] - c0[i]));
            }
        }
    }

    /// <summary>Type 3: one of several functions by where the input falls, its subdomain encoded onto the function's.</summary>
    private sealed class StitchingFunction : PdfFunction
    {
        private readonly PdfFunction[] functions;
        private readonly double[] bounds;
        private readonly double[] encode;

        public StitchingFunction(PdfDictionary d, int depth)
            : base(d)
        {
            functions = [.. (d.Get<PdfArray>("Functions") ?? throw new InvalidDataException("A stitching function has no /Functions.")).Select(f => Read(f, depth + 1))];
            bounds = Numbers(d.Get<PdfArray>("Bounds")) ?? [];
            encode = Numbers(d.Get<PdfArray>("Encode")) ?? [];
            if (functions.Length == 0 || bounds.Length != functions.Length - 1 || encode.Length < 2 * functions.Length)
            {
                throw new InvalidDataException("A stitching function's /Functions, /Bounds and /Encode do not agree.");
            }
        }

        public override int Outputs => functions[0].Outputs;

        protected override void Run(ReadOnlySpan<double> input, Span<double> output)
        {
            double x = input.Length > 0 ? input[0] : 0;
            int k = 0;
            while (k < bounds.Length && x >= bounds[k])
            {
                k++;
            }

            double low = k == 0 ? Domain[0] : bounds[k - 1];
            double high = k == bounds.Length ? Domain[1] : bounds[k];
            double t = Interpolate(x, low, high, encode[2 * k], encode[(2 * k) + 1]);
            functions[k].Evaluate([t], output);
        }
    }

    /// <summary>Type 4: a PostScript calculator program (7.10.5) run on a stack of numbers and booleans.</summary>
    private sealed class PostScriptFunction : PdfFunction
    {
        private const int MaxStack = 100;
        private const int MaxSteps = 100_000;

        private readonly List<object> program;
        private readonly int outputs;

        public PostScriptFunction(PdfStream stream)
            : base(stream.Dictionary)
        {
            outputs = (Range ?? throw new InvalidDataException("A PostScript function has no /Range.")).Length / 2;
            string text = System.Text.Encoding.Latin1.GetString(stream.GetDecodedBytes());
            int at = text.IndexOf('{', StringComparison.Ordinal);
            if (at < 0)
            {
                throw new InvalidDataException("A PostScript function has no program.");
            }

            at++;
            program = Parse(text, ref at, 0);
        }

        public override int Outputs => outputs;

        protected override void Run(ReadOnlySpan<double> input, Span<double> output)
        {
            List<object> stack = [];
            foreach (double v in input)
            {
                stack.Add(v);
            }

            int steps = 0;
            try
            {
                Execute(program, stack, ref steps);
            }
            catch (InvalidDataException)
            {
                // A program that fails part way gives what the stack holds, as viewers do.
            }

            int start = Math.Max(0, stack.Count - outputs);
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = start + i < stack.Count && stack[start + i] is double d ? d : 0;
            }
        }

        private static List<object> Parse(string text, ref int at, int depth)
        {
            if (depth > 32)
            {
                throw new InvalidDataException("A PostScript function nests too deep.");
            }

            List<object> items = [];
            while (at < text.Length)
            {
                char c = text[at];
                if (char.IsWhiteSpace(c))
                {
                    at++;
                }
                else if (c == '{')
                {
                    at++;
                    items.Add(Parse(text, ref at, depth + 1));
                }
                else if (c == '}')
                {
                    at++;
                    return items;
                }
                else
                {
                    int start = at;
                    while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ('{' or '}'))
                    {
                        at++;
                    }

                    string word = text[start..at];
                    items.Add(double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : word);
                }
            }

            return items;
        }

        private static void Execute(List<object> code, List<object> stack, ref int steps)
        {
            for (int i = 0; i < code.Count; i++)
            {
                if (++steps > MaxSteps || stack.Count > MaxStack)
                {
                    throw new InvalidDataException("A PostScript function runs too long.");
                }

                object item = code[i];
                if (item is double number)
                {
                    stack.Add(number);
                    continue;
                }

                if (item is List<object> block)
                {
                    // A procedure is data for the if or ifelse that follows it.
                    stack.Add(block);
                    continue;
                }

                Operator((string)item, stack, ref steps);
            }
        }

        private static void Operator(string op, List<object> stack, ref int steps)
        {
            switch (op)
            {
                case "if":
                    {
                        List<object> proc = Pop<List<object>>(stack);
                        if (PopBool(stack))
                        {
                            Execute(proc, stack, ref steps);
                        }

                        return;
                    }

                case "ifelse":
                    {
                        List<object> otherwise = Pop<List<object>>(stack), then = Pop<List<object>>(stack);
                        Execute(PopBool(stack) ? then : otherwise, stack, ref steps);
                        return;
                    }

                case "true":
                    stack.Add(true);
                    return;
                case "false":
                    stack.Add(false);
                    return;
                case "pop":
                    Pop<object>(stack);
                    return;
                case "exch":
                    {
                        object b = Pop<object>(stack), a = Pop<object>(stack);
                        stack.Add(b);
                        stack.Add(a);
                        return;
                    }

                case "dup":
                    stack.Add(Peek(stack, 0));
                    return;
                case "copy":
                    {
                        int n = (int)PopNumber(stack);
                        if (n < 0 || n > stack.Count)
                        {
                            throw new InvalidDataException("copy reaches past the stack.");
                        }

                        stack.AddRange(stack.GetRange(stack.Count - n, n));
                        return;
                    }

                case "index":
                    {
                        int n = (int)PopNumber(stack);
                        stack.Add(Peek(stack, n));
                        return;
                    }

                case "roll":
                    {
                        int j = (int)PopNumber(stack), n = (int)PopNumber(stack);
                        if (n < 0 || n > stack.Count)
                        {
                            throw new InvalidDataException("roll reaches past the stack.");
                        }

                        if (n > 0)
                        {
                            List<object> part = stack.GetRange(stack.Count - n, n);
                            stack.RemoveRange(stack.Count - n, n);
                            j = ((j % n) + n) % n;
                            stack.AddRange(part.Skip(n - j).Concat(part.Take(n - j)));
                        }

                        return;
                    }
            }

            if (op is "and" or "or" or "xor")
            {
                object b = Pop<object>(stack), a = Pop<object>(stack);
                if (a is bool x && b is bool y)
                {
                    stack.Add(op == "and" ? x && y : op == "or" ? x || y : x ^ y);
                }
                else
                {
                    long p = (long)Number(a), q = (long)Number(b);
                    stack.Add((double)(op == "and" ? p & q : op == "or" ? p | q : p ^ q));
                }

                return;
            }

            if (op == "not")
            {
                object a = Pop<object>(stack);
                stack.Add(a is bool x ? !x : (double)~(long)Number(a));
                return;
            }

            if (op is "eq" or "ne")
            {
                object b = Pop<object>(stack), a = Pop<object>(stack);
                bool equal = a is bool x && b is bool y ? x == y : Number(a) == Number(b);
                stack.Add(op == "eq" ? equal : !equal);
                return;
            }

            if (op is "gt" or "ge" or "lt" or "le")
            {
                double b = PopNumber(stack), a = PopNumber(stack);
                stack.Add(op switch { "gt" => a > b, "ge" => a >= b, "lt" => a < b, _ => a <= b });
                return;
            }

            if (op is "add" or "sub" or "mul" or "div" or "idiv" or "mod" or "atan" or "exp" or "bitshift")
            {
                double b = PopNumber(stack), a = PopNumber(stack);
                stack.Add(op switch
                {
                    "add" => a + b,
                    "sub" => a - b,
                    "mul" => a * b,
                    "div" => b == 0 ? 0 : a / b,
                    "idiv" => (long)b == 0 ? 0 : (double)((long)a / (long)b),
                    "mod" => (long)b == 0 ? 0 : (double)((long)a % (long)b),
                    "atan" => Atan(a, b),
                    "exp" => Math.Pow(a, b),
                    _ => b >= 0 ? (double)((long)a << (int)Math.Min(b, 63)) : (double)((long)a >> (int)Math.Min(-b, 63)),
                });
                return;
            }

            double v = PopNumber(stack);
            stack.Add(op switch
            {
                "abs" => Math.Abs(v),
                "neg" => -v,
                "ceiling" => Math.Ceiling(v),
                "floor" => Math.Floor(v),
                "round" => Math.Floor(v + 0.5),
                "truncate" => Math.Truncate(v),
                "sqrt" => Math.Sqrt(Math.Max(v, 0)),
                "sin" => Math.Sin(v * Math.PI / 180),
                "cos" => Math.Cos(v * Math.PI / 180),
                "ln" => v > 0 ? Math.Log(v) : 0,
                "log" => v > 0 ? Math.Log10(v) : 0,
                "cvi" => Math.Truncate(v),
                "cvr" => v,
                _ => throw new InvalidDataException($"A PostScript function uses the unknown operator {op}."),
            });
        }

        private static double Atan(double num, double den)
        {
            double angle = Math.Atan2(num, den) * 180 / Math.PI;
            return angle < 0 ? angle + 360 : angle;
        }

        private static T Pop<T>(List<object> stack)
        {
            if (stack.Count == 0 || stack[^1] is not T value)
            {
                throw new InvalidDataException("A PostScript operator found the wrong operand.");
            }

            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static double PopNumber(List<object> stack)
        {
            return Number(Pop<object>(stack));
        }

        private static bool PopBool(List<object> stack)
        {
            return Pop<object>(stack) switch
            {
                bool b => b,
                double d => d != 0,
                _ => false,
            };
        }

        private static double Number(object value)
        {
            return value switch
            {
                double d => d,
                bool b => b ? 1 : 0,
                _ => throw new InvalidDataException("A PostScript operator found a procedure where a number belongs."),
            };
        }

        private static object Peek(List<object> stack, int depth)
        {
            if (depth < 0 || depth >= stack.Count)
            {
                throw new InvalidDataException("A PostScript operator reaches past the stack.");
            }

            return stack[stack.Count - 1 - depth];
        }
    }

    protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax)
    {
        return xMax == xMin ? yMin : yMin + ((x - xMin) * (yMax - yMin) / (xMax - xMin));
    }
}
