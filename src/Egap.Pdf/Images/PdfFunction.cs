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

    /// <summary>The domain's first input range, the one a one-input function's table spans.</summary>
    public (double Start, double End) FirstDomain => (Domain[0], Math.Max(Domain[0], Domain[1]));

    /// <summary>
    /// A one-input function sampled at <paramref name="count"/> even steps over its domain (Phase 4 design Q10), for tint
    /// transforms and shadings that evaluate it many times; <paramref name="outputs"/> numbers per sample.
    /// </summary>
    public FunctionTable Sample(int count, int outputs)
    {
        (double start, double end) = FirstDomain;
        double[] values = new double[count * outputs];
        Span<double> input = stackalloc double[1];
        for (int i = 0; i < count; i++)
        {
            input[0] = start + ((end - start) * i / (count - 1));
            Evaluate(input, values.AsSpan(i * outputs, outputs));
        }

        return new FunctionTable(start, end, outputs, values);
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
                // A caller with fewer inputs than the domain (a shading's one-input use of a two-input function) gives the
                // missing ones their domain's start; a non-finite encoding lands on the first sample.
                double x = i < input.Length ? input[i] : Domain[2 * i];
                double e = Interpolate(x, Domain[2 * i], Domain[(2 * i) + 1], encode[2 * i], encode[(2 * i) + 1]);
                position[i] = double.IsFinite(e) ? Math.Clamp(e, 0, size[i] - 1) : 0;
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

    /// <summary>
    /// Type 4: a PostScript calculator program (7.10.5), parsed once into a flat list of instructions with the procedures of
    /// <c>if</c> and <c>ifelse</c> turned into jumps, then run on a fixed stack of numbers and booleans without allocating.
    /// </summary>
    private sealed class PostScriptFunction : PdfFunction
    {
        private const int MaxStack = 100;

        private readonly Instruction[] program;
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
            List<Instruction> code = [];
            Compile(Parse(text, ref at, 0), code);
            program = [.. code];
        }

        public override int Outputs => outputs;

        private enum Op : byte
        {
            Push,
            PushTrue,
            PushFalse,
            JumpIfFalse,
            Jump,
            Abs,
            Ceiling,
            Cos,
            Cvi,
            Cvr,
            Floor,
            Ln,
            Log,
            Neg,
            Round,
            Sin,
            Sqrt,
            Truncate,
            Add,
            Atan,
            Div,
            Exp,
            Idiv,
            Mod,
            Mul,
            Sub,
            And,
            Bitshift,
            Eq,
            Ge,
            Gt,
            Le,
            Lt,
            Ne,
            Or,
            Xor,
            Not,
            Copy,
            Dup,
            Exch,
            Index,
            Pop,
            Roll,
        }

        protected override void Run(ReadOnlySpan<double> input, Span<double> output)
        {
            Span<double> values = stackalloc double[MaxStack];
            Span<bool> booleans = stackalloc bool[MaxStack];
            int count = 0;
            foreach (double v in input[..Math.Min(input.Length, MaxStack)])
            {
                values[count++] = v;
            }

            // A program that fails part way gives what the stack holds, as viewers do.
            Execute(values, booleans, ref count);
            int start = Math.Max(0, count - outputs);
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = start + i < count ? values[start + i] : 0;
            }
        }

        private void Execute(Span<double> v, Span<bool> b, ref int n)
        {
            Instruction[] code = program;
            for (int pc = 0; pc < code.Length; pc++)
            {
                Instruction ins = code[pc];
                if (ins.Op <= Op.Jump)
                {
                    if (!Control(ins, v, b, ref n, ref pc))
                    {
                        return;
                    }
                }
                else if (ins.Op <= Op.Truncate)
                {
                    // One number in, one out.
                    if (n < 1)
                    {
                        return;
                    }

                    double x = v[n - 1];
                    b[n - 1] = false;
                    v[n - 1] = ins.Op switch
                    {
                        Op.Abs => Math.Abs(x),
                        Op.Neg => -x,
                        Op.Ceiling => Math.Ceiling(x),
                        Op.Floor => Math.Floor(x),
                        Op.Round => Math.Floor(x + 0.5),
                        Op.Truncate or Op.Cvi => Math.Truncate(x),
                        Op.Sqrt => Math.Sqrt(Math.Max(x, 0)),
                        Op.Sin => Math.Sin(x * Math.PI / 180),
                        Op.Cos => Math.Cos(x * Math.PI / 180),
                        Op.Ln => x > 0 ? Math.Log(x) : 0,
                        Op.Log => x > 0 ? Math.Log10(x) : 0,
                        _ => x,
                    };
                }
                else if (ins.Op <= Op.Xor)
                {
                    if (n < 2)
                    {
                        return;
                    }

                    n--;
                    (v[n - 1], b[n - 1]) = Binary(ins.Op, v[n - 1], v[n], b[n - 1] && b[n]);
                }
                else if (!Stack(ins.Op, v, b, ref n))
                {
                    return;
                }
            }
        }

        private static bool Control(Instruction ins, Span<double> v, Span<bool> b, ref int n, ref int pc)
        {
            switch (ins.Op)
            {
                case Op.Push or Op.PushTrue or Op.PushFalse:
                    if (n == MaxStack)
                    {
                        return false;
                    }

                    v[n] = ins.Op == Op.Push ? ins.Value : ins.Op == Op.PushTrue ? 1 : 0;
                    b[n++] = ins.Op != Op.Push;
                    return true;
                case Op.Jump:
                    pc = (int)ins.Value - 1;
                    return true;
                default:
                    if (n < 1)
                    {
                        return false;
                    }

                    if (v[--n] == 0)
                    {
                        pc = (int)ins.Value - 1;
                    }

                    return true;
            }
        }

        private static (double Value, bool IsBool) Binary(Op op, double a, double c, bool logical)
        {
            return op switch
            {
                Op.Add => (a + c, false),
                Op.Sub => (a - c, false),
                Op.Mul => (a * c, false),
                Op.Div => (c == 0 ? 0 : a / c, false),
                Op.Idiv => ((long)c == 0 ? 0 : (double)((long)a / (long)c), false),
                Op.Mod => ((long)c == 0 ? 0 : (double)((long)a % (long)c), false),
                Op.Atan => (Atan(a, c), false),
                Op.Exp => (Math.Pow(a, c), false),
                Op.Bitshift => (c >= 0 ? (long)a << (int)Math.Min(c, 63) : (long)a >> (int)Math.Min(-c, 63), false),
                Op.And => logical ? (a != 0 && c != 0 ? 1 : 0, true) : ((long)a & (long)c, false),
                Op.Or => logical ? (a != 0 || c != 0 ? 1 : 0, true) : ((long)a | (long)c, false),
                Op.Xor => logical ? ((a != 0) ^ (c != 0) ? 1 : 0, true) : ((long)a ^ (long)c, false),
                Op.Eq => (a == c ? 1 : 0, true),
                Op.Ne => (a != c ? 1 : 0, true),
                Op.Gt => (a > c ? 1 : 0, true),
                Op.Ge => (a >= c ? 1 : 0, true),
                Op.Lt => (a < c ? 1 : 0, true),
                _ => (a <= c ? 1 : 0, true),
            };
        }

        /// <summary>The stack operators; false when one reaches past the stack, which ends the program.</summary>
        private static bool Stack(Op op, Span<double> v, Span<bool> b, ref int n)
        {
            switch (op)
            {
                case Op.Not:
                    if (n < 1)
                    {
                        return false;
                    }

                    v[n - 1] = b[n - 1] ? (v[n - 1] == 0 ? 1 : 0) : ~(long)v[n - 1];
                    return true;
                case Op.Dup:
                    if (n < 1 || n == MaxStack)
                    {
                        return false;
                    }

                    v[n] = v[n - 1];
                    b[n] = b[n - 1];
                    n++;
                    return true;
                case Op.Pop:
                    if (n < 1)
                    {
                        return false;
                    }

                    n--;
                    return true;
                case Op.Exch:
                    if (n < 2)
                    {
                        return false;
                    }

                    (v[n - 1], v[n - 2]) = (v[n - 2], v[n - 1]);
                    (b[n - 1], b[n - 2]) = (b[n - 2], b[n - 1]);
                    return true;
                case Op.Copy:
                    {
                        if (n < 1)
                        {
                            return false;
                        }

                        double count = v[--n];
                        if (!(count >= 0) || count > n || n + (int)count > MaxStack)
                        {
                            return false;
                        }

                        int k = (int)count;
                        v.Slice(n - k, k).CopyTo(v[n..]);
                        b.Slice(n - k, k).CopyTo(b[n..]);
                        n += k;
                        return true;
                    }

                case Op.Index:
                    {
                        if (n < 1)
                        {
                            return false;
                        }

                        double depth = v[--n];
                        if (!(depth >= 0) || depth >= n)
                        {
                            return false;
                        }

                        int from = n - 1 - (int)depth;
                        v[n] = v[from];
                        b[n] = b[from];
                        n++;
                        return true;
                    }

                default:
                    {
                        // roll: n j roll turns the top n items j places up.
                        if (n < 2)
                        {
                            return false;
                        }

                        double shift = v[--n], count = v[--n];
                        if (!(count >= 0) || count > n || !double.IsFinite(shift))
                        {
                            return false;
                        }

                        int k = (int)count;
                        if (k > 0)
                        {
                            int j = (int)((((long)shift % k) + k) % k);
                            Rotate(v.Slice(n - k, k), j);
                            Rotate(b.Slice(n - k, k), j);
                        }

                        return true;
                    }
            }
        }

        /// <summary>Moves the last <paramref name="j"/> items to the front, as roll does to the top of the stack.</summary>
        private static void Rotate<T>(Span<T> items, int j)
        {
            items.Reverse();
            items[..j].Reverse();
            items[j..].Reverse();
        }

        /// <summary>Flattens parsed items into instructions: a procedure before <c>if</c> or <c>ifelse</c> becomes a jump around it.</summary>
        private static void Compile(List<object> items, List<Instruction> code)
        {
            for (int i = 0; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case double number:
                        code.Add(new Instruction(Op.Push, number));
                        break;
                    case List<object> then when i + 1 < items.Count && items[i + 1] is "if":
                        {
                            int jump = code.Count;
                            code.Add(default);
                            Compile(then, code);
                            code[jump] = new Instruction(Op.JumpIfFalse, code.Count);
                            i++;
                            break;
                        }

                    case List<object> then when i + 2 < items.Count && items[i + 1] is List<object> otherwise && items[i + 2] is "ifelse":
                        {
                            int toElse = code.Count;
                            code.Add(default);
                            Compile(then, code);
                            int toEnd = code.Count;
                            code.Add(default);
                            code[toElse] = new Instruction(Op.JumpIfFalse, code.Count);
                            Compile(otherwise, code);
                            code[toEnd] = new Instruction(Op.Jump, code.Count);
                            i += 2;
                            break;
                        }

                    case List<object>:
                        throw new InvalidDataException("A PostScript procedure is not followed by if or ifelse.");
                    case string word:
                        code.Add(word switch
                        {
                            "true" => new Instruction(Op.PushTrue, 0),
                            "false" => new Instruction(Op.PushFalse, 0),
                            _ => word.All(char.IsAsciiLetterLower) && Enum.TryParse(word, ignoreCase: true, out Op op) && op > Op.Jump
                                ? new Instruction(op, 0)
                                : throw new InvalidDataException($"A PostScript function uses the unknown operator {word}."),
                        });
                        break;
                }

                if (code.Count > 100_000)
                {
                    throw new InvalidDataException("A PostScript function is too long.");
                }
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

        private static double Atan(double num, double den)
        {
            double angle = Math.Atan2(num, den) * 180 / Math.PI;
            return angle < 0 ? angle + 360 : angle;
        }

        private readonly record struct Instruction(Op Op, double Value);
    }

    protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax)
    {
        return xMax == xMin ? yMin : yMin + ((x - xMin) * (yMax - yMin) / (xMax - xMin));
    }
}
