using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.DrawingML;

/// <summary>How a geometry path fills (ECMA-376 part 1, 20.1.10.37): not at all, normally, or a shade lighter or darker than the shape's fill.</summary>
internal enum PathFill
{
    None,
    Normal,
    Lighten,
    LightenLess,
    Darken,
    DarkenLess,
}

/// <summary>One path of a geometry, laid out at a size: its outline, how it fills and whether it is stroked.</summary>
internal readonly record struct GeometryPath(Path Path, PathFill Fill, bool Stroke);

/// <summary>A geometry laid out at a size: its paths, and the rectangle text goes in, in the same units as the size.</summary>
internal sealed record GeometryLayout(IReadOnlyList<GeometryPath> Paths, RectF TextRect);

/// <summary>
/// A DrawingML geometry (ECMA-376 part 1, 20.1.9): adjust values, guides written as formulas, and paths whose points name guides,
/// as a preset (<c>a:prstGeom</c>) or a custom geometry (<c>a:custGeom</c>) gives them.
/// </summary>
internal sealed class ShapeGeometry
{
    private readonly List<(string Name, Formula Formula)> adjusts = [];
    private readonly List<(string Name, Formula Formula)> guides = [];
    private readonly List<PathDefinition> paths = [];
    private string[]? textRect;

    private ShapeGeometry()
    {
    }

    /// <summary>A preset geometry by its name, such as <c>rightArrow</c>; null for a name ECMA-376 does not define.</summary>
    public static ShapeGeometry? Preset(string? name) => PresetGeometries.Get(name);

    /// <summary>The geometry an <c>a:prstGeom</c> or <c>a:custGeom</c> element gives, with its own adjust values; null for neither or an unknown preset.</summary>
    public static ShapeGeometry? Read(XElement? holder)
    {
        if (holder?.Element(DrawingNs.A + "prstGeom") is { } preset)
        {
            return Preset((string?)preset.Attribute("prst"));
        }

        return holder?.Element(DrawingNs.A + "custGeom") is { } custom ? Custom(custom) : null;
    }

    /// <summary>A custom geometry from an <c>a:custGeom</c> element.</summary>
    public static ShapeGeometry Custom(XElement custom)
    {
        ShapeGeometry geometry = new();
        foreach (XElement gd in custom.Element(DrawingNs.A + "avLst")?.Elements(DrawingNs.A + "gd") ?? [])
        {
            if (Guide(gd) is { } guide)
            {
                geometry.adjusts.Add(guide);
            }
        }

        foreach (XElement gd in custom.Element(DrawingNs.A + "gdLst")?.Elements(DrawingNs.A + "gd") ?? [])
        {
            if (Guide(gd) is { } guide)
            {
                geometry.guides.Add(guide);
            }
        }

        foreach (XElement path in custom.Element(DrawingNs.A + "pathLst")?.Elements(DrawingNs.A + "path") ?? [])
        {
            PathDefinition definition = new(
                (string?)path.Attribute("w"),
                (string?)path.Attribute("h"),
                FillOf((string?)path.Attribute("fill")),
                (string?)path.Attribute("stroke") is not ("false" or "0"));
            foreach (XElement command in path.Elements())
            {
                if (command.Name.Namespace != DrawingNs.A)
                {
                    continue;
                }

                string[] points = [.. command.Elements(DrawingNs.A + "pt").SelectMany(p => new[] { (string?)p.Attribute("x") ?? "0", (string?)p.Attribute("y") ?? "0" })];
                switch (command.Name.LocalName)
                {
                    case "moveTo" when points.Length >= 2:
                        definition.Commands.Add(new PathCommand('M', points[..2]));
                        break;
                    case "lnTo" when points.Length >= 2:
                        definition.Commands.Add(new PathCommand('L', points[..2]));
                        break;
                    case "arcTo":
                        definition.Commands.Add(new PathCommand('A', [Attr(command, "wR"), Attr(command, "hR"), Attr(command, "stAng"), Attr(command, "swAng")]));
                        break;
                    case "quadBezTo" when points.Length >= 4:
                        definition.Commands.Add(new PathCommand('Q', points[..4]));
                        break;
                    case "cubicBezTo" when points.Length >= 6:
                        definition.Commands.Add(new PathCommand('C', points[..6]));
                        break;
                    case "close":
                        definition.Commands.Add(new PathCommand('Z', []));
                        break;
                }
            }

            geometry.paths.Add(definition);
        }

        if (custom.Element(DrawingNs.A + "rect") is { } rect)
        {
            geometry.textRect = [Attr(rect, "l"), Attr(rect, "t"), Attr(rect, "r"), Attr(rect, "b")];
        }

        return geometry;

        static string Attr(XElement element, string name) => (string?)element.Attribute(name) ?? "0";
    }

    /// <summary>Parses a preset in the compact form tools/gen-preset-shapes.py writes.</summary>
    internal static ShapeGeometry Parse(string definition)
    {
        ShapeGeometry geometry = new();
        PathDefinition? path = null;
        foreach (string line in definition.Split('\n'))
        {
            string[] parts = line.Split(' ');
            switch (parts[0])
            {
                case "a":
                    geometry.adjusts.Add((parts[1], new Formula(parts[2..])));
                    break;
                case "g":
                    geometry.guides.Add((parts[1], new Formula(parts[2..])));
                    break;
                case "p":
                    path = new PathDefinition(parts[1] == "-" ? null : parts[1], parts[2] == "-" ? null : parts[2], FillOf(parts[3]), parts[4] == "1");
                    geometry.paths.Add(path);
                    break;
                case "r":
                    geometry.textRect = parts[1..];
                    break;
                default:
                    path!.Commands.Add(new PathCommand(parts[0][0], parts[1..]));
                    break;
            }
        }

        return geometry;
    }

    /// <summary>
    /// Evaluates the guides for a shape <paramref name="width"/> by <paramref name="height"/> and builds its paths at that size, with
    /// the adjust values of <paramref name="adjustList"/> (an <c>a:avLst</c>) in place of the defaults.
    /// </summary>
    public GeometryLayout Layout(float width, float height, XElement? adjustList = null)
    {
        // Guides are worked out in EMUs, the unit a custom geometry's constants are in, and scaled back at the end.
        const double Unit = DrawingNs.EmuPerPoint;
        double w = width * Unit;
        double h = height * Unit;
        Dictionary<string, double> values = Builtins(w, h);
        Dictionary<string, Formula>? overrides = null;
        foreach (XElement gd in adjustList?.Elements(DrawingNs.A + "gd") ?? [])
        {
            if (Guide(gd) is { } guide)
            {
                (overrides ??= [])[guide.Name] = guide.Formula;
            }
        }

        foreach ((string name, Formula formula) in adjusts)
        {
            values[name] = (overrides is not null && overrides.TryGetValue(name, out Formula? given) ? given : formula).Evaluate(values);
        }

        foreach ((string name, Formula formula) in guides)
        {
            values[name] = formula.Evaluate(values);
        }

        List<GeometryPath> built = new(paths.Count);
        foreach (PathDefinition path in paths)
        {
            built.Add(new GeometryPath(path.Build(values, w, h, 1 / Unit), path.Fill, path.Stroke));
        }

        RectF text = new(0, 0, width, height);
        if (textRect is [var l, var t, var r, var b])
        {
            float left = (float)(Value(l, values) / Unit);
            float top = (float)(Value(t, values) / Unit);
            text = new RectF(left, top, (float)(Value(r, values) / Unit) - left, (float)(Value(b, values) / Unit) - top);
        }

        return new GeometryLayout(built, text);
    }

    private static Dictionary<string, double> Builtins(double w, double h)
    {
        double ss = Math.Min(w, h);
        Dictionary<string, double> values = new(StringComparer.Ordinal)
        {
            ["w"] = w,
            ["h"] = h,
            ["l"] = 0,
            ["t"] = 0,
            ["r"] = w,
            ["b"] = h,
            ["hc"] = w / 2,
            ["vc"] = h / 2,
            ["ls"] = Math.Max(w, h),
            ["ss"] = ss,
            ["cd2"] = 10800000,
            ["cd4"] = 5400000,
            ["cd8"] = 2700000,
            ["3cd4"] = 16200000,
            ["3cd8"] = 8100000,
            ["5cd8"] = 13500000,
            ["7cd8"] = 18900000,
        };
        foreach (int n in (ReadOnlySpan<int>)[2, 3, 4, 5, 6, 8, 10, 12, 16, 32])
        {
            values["wd" + n] = w / n;
            values["hd" + n] = h / n;
            values["ssd" + n] = ss / n;
        }

        return values;
    }

    private static (string Name, Formula Formula)? Guide(XElement gd) =>
        (string?)gd.Attribute("name") is { } name && ((string?)gd.Attribute("fmla"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } tokens
            ? (name, new Formula(tokens))
            : null;

    private static PathFill FillOf(string? value) => value switch
    {
        "none" => PathFill.None,
        "lighten" => PathFill.Lighten,
        "lightenLess" => PathFill.LightenLess,
        "darken" => PathFill.Darken,
        "darkenLess" => PathFill.DarkenLess,
        _ => PathFill.Normal,
    };

    /// <summary>A guide's name or a number; 0 for a name not defined (yet).</summary>
    private static double Value(string token, Dictionary<string, double> values)
    {
        if (values.TryGetValue(token, out double value))
        {
            return value;
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }

    /// <summary>A guide formula (ECMA-376 part 1, 20.1.10.56 ST_GeomGuideFormula): an operator and up to three arguments.</summary>
    private sealed class Formula(string[] tokens)
    {
        private const double Degrees = 60000;

        public double Evaluate(Dictionary<string, double> values)
        {
            double x = Arg(1);
            double y = Arg(2);
            double z = Arg(3);
            return tokens[0] switch
            {
                "val" => x,
                "*/" => z == 0 ? 0 : x * y / z,
                "+-" => x + y - z,
                "+/" => z == 0 ? 0 : (x + y) / z,
                "?:" => x > 0 ? y : z,
                "abs" => Math.Abs(x),
                "at2" => Math.Atan2(y, x) * 180 / Math.PI * Degrees,
                "cat2" => x * Math.Cos(Math.Atan2(z, y)),
                "sat2" => x * Math.Sin(Math.Atan2(z, y)),
                "cos" => x * Math.Cos(Radians(y)),
                "sin" => x * Math.Sin(Radians(y)),
                "tan" => x * Math.Tan(Radians(y)),
                "max" => Math.Max(x, y),
                "min" => Math.Min(x, y),
                "mod" => Math.Sqrt((x * x) + (y * y) + (z * z)),
                "pin" => y < x ? x : y > z ? z : y,
                "sqrt" => Math.Sqrt(Math.Max(x, 0)),
                _ => 0,
            };

            double Arg(int index) => index < tokens.Length ? Value(tokens[index], values) : 0;
        }

        private static double Radians(double angle) => angle / Degrees * Math.PI / 180;
    }

    private sealed record PathCommand(char Kind, string[] Args);

    private sealed record PathDefinition(string? Width, string? Height, PathFill Fill, bool Stroke)
    {
        public List<PathCommand> Commands { get; } = [];

        public Path Build(Dictionary<string, double> values, double w, double h, double scale)
        {
            // A path with its own size has its points in that space, stretched to the shape's.
            double sx = scale;
            double sy = scale;
            if (Width is not null && Value(Width, values) is var pw and > 0)
            {
                sx = w / pw * scale;
            }

            if (Height is not null && Value(Height, values) is var ph and > 0)
            {
                sy = h / ph * scale;
            }

            PathBuilder builder = new();
            Vector2 current = default;
            bool open = false;
            foreach (PathCommand command in Commands)
            {
                string[] a = command.Args;
                switch (command.Kind)
                {
                    case 'M':
                        current = Point(a[0], a[1]);
                        builder.MoveTo(current);
                        open = true;
                        break;
                    case 'L':
                        current = Point(a[0], a[1]);
                        Ensure();
                        builder.LineTo(current);
                        break;
                    case 'Q':
                        Ensure();
                        current = Point(a[2], a[3]);
                        builder.QuadTo(Point(a[0], a[1]), current);
                        break;
                    case 'C':
                        Ensure();
                        current = Point(a[4], a[5]);
                        builder.CubicTo(Point(a[0], a[1]), Point(a[2], a[3]), current);
                        break;
                    case 'A':
                        Ensure();
                        current = Arc(builder, current, Value(a[0], values) * sx, Value(a[1], values) * sy, Value(a[2], values), Value(a[3], values));
                        break;
                    case 'Z':
                        if (open)
                        {
                            builder.Close();
                            open = false;
                        }

                        break;
                }
            }

            return builder.ToPath();

            Vector2 Point(string x, string y) => new((float)(Value(x, values) * sx), (float)(Value(y, values) * sy));

            void Ensure()
            {
                if (!open)
                {
                    builder.MoveTo(current);
                    open = true;
                }
            }
        }

        /// <summary>
        /// An arc of the ellipse with radii <paramref name="rx"/> and <paramref name="ry"/> that passes through
        /// <paramref name="start"/> at angle <paramref name="startAngle"/>, swept by <paramref name="sweepAngle"/> (both in 60 000ths
        /// of a degree, clockwise with y down), as cubic Béziers of at most a quarter turn; returns where it ends.
        /// </summary>
        private static Vector2 Arc(PathBuilder builder, Vector2 start, double rx, double ry, double startAngle, double sweepAngle)
        {
            if (rx <= 0 || ry <= 0 || sweepAngle == 0)
            {
                return start;
            }

            double from = Parametric(startAngle / 60000 * Math.PI / 180, rx, ry);
            double sweepRadians = sweepAngle / 60000 * Math.PI / 180;
            double to = Parametric((startAngle + sweepAngle) / 60000 * Math.PI / 180, rx, ry);

            // The angles name points on the ellipse; the parametric sweep goes the way the arc does. A whole turn or more stays as asked.
            double sweep = sweepRadians;
            if (Math.Abs(sweepRadians) < (2 * Math.PI) - 1e-9)
            {
                sweep = to - from;
                if (sweepRadians > 0 && sweep <= 0)
                {
                    sweep += 2 * Math.PI;
                }
                else if (sweepRadians < 0 && sweep >= 0)
                {
                    sweep -= 2 * Math.PI;
                }
            }

            double cx = start.X - (rx * Math.Cos(from));
            double cy = start.Y - (ry * Math.Sin(from));
            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
            double step = sweep / segments;
            double k = 4.0 / 3 * Math.Tan(step / 4);
            double angle = from;
            Vector2 end = start;
            for (int i = 0; i < segments; i++)
            {
                double next = angle + step;
                (double s0, double c0) = Math.SinCos(angle);
                (double s1, double c1) = Math.SinCos(next);
                Vector2 c1Point = new((float)(cx + (rx * (c0 - (k * s0)))), (float)(cy + (ry * (s0 + (k * c0)))));
                Vector2 c2Point = new((float)(cx + (rx * (c1 + (k * s1)))), (float)(cy + (ry * (s1 - (k * c1)))));
                end = new Vector2((float)(cx + (rx * c1)), (float)(cy + (ry * s1)));
                builder.CubicTo(c1Point, c2Point, end);
                angle = next;
            }

            return end;
        }

        /// <summary>The ellipse parameter of the point the ray at <paramref name="angle"/> from the centre meets.</summary>
        private static double Parametric(double angle, double rx, double ry)
        {
            (double sin, double cos) = Math.SinCos(angle);
            return Math.Atan2(rx * sin, ry * cos);
        }
    }
}

/// <summary>The 187 preset geometries of ECMA-376, parsed once each when first asked for.</summary>
internal static partial class PresetGeometries
{
    private static readonly FrozenDictionary<string, int> Index;
    private static readonly ShapeGeometry?[] Parsed;

    // A constructor, not field initialisers: those of the generated half may run after these otherwise.
    static PresetGeometries()
    {
        Index = Definitions.Select((d, i) => (d.Name, i)).ToFrozenDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        Parsed = new ShapeGeometry?[Definitions.Length];
    }

    /// <summary>The preset names, in order.</summary>
    public static IEnumerable<string> Names => Definitions.Select(d => d.Name);

    public static ShapeGeometry? Get(string? name)
    {
        if (name is null || !Index.TryGetValue(name, out int index))
        {
            return null;
        }

        return Volatile.Read(ref Parsed[index]) ?? Interlocked.CompareExchange(ref Parsed[index], ShapeGeometry.Parse(Definitions[index].Definition), null) ?? Parsed[index];
    }
}
