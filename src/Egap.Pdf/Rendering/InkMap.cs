using System.Runtime.CompilerServices;

using Egami.Colors;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// How colour spaces print on an ink canvas's plates (Phase 8 design Q4, Build Plan task 6): DeviceCMYK and four-component
/// ICCBased colours as process ink amounts, DeviceGray as black ink, Separation and DeviceN colourants on their own plates
/// (a process name on that process plate, All on every plate, None on none), Indexed through its base. A Separation or
/// DeviceN space naming a colourant the canvas lacks goes through its alternate (8.6.6.4); a space with no plan here, RGB,
/// Lab and the rest, is drawn as sRGB and the canvas converts it into the process inks through its profile. With a transfer
/// function every tint goes through its plate's function.
/// </summary>
internal sealed class InkMap(InkSet inks, TransferTable? transfer = null)
{
    private readonly ConditionalWeakTable<ImageColorSpace, Holder> plans = [];

    private ColorTransform? fromSrgb;

    /// <summary>The plates drawn on.</summary>
    public InkSet Inks => inks;

    /// <summary>sRGB into the process inks, as the ink canvas converts it: relative colorimetric with black point compensation.</summary>
    public ColorTransform FromSrgb => LazyInitializer.EnsureInitialized(ref fromSrgb, () => ColorTransform.Create(ColorProfile.Srgb, inks.Profile,
        new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true }));

    /// <summary>How the space prints, or null when it is drawn as sRGB.</summary>
    public InkPlan? Plan(ImageColorSpace space)
    {
        return plans.GetValue(space, s => new Holder(Transferred(Make(s, 0)))).Plan;
    }

    /// <summary>The plan with each tint through its plate's transfer function: process plates by name, spot plates as black.</summary>
    private InkPlan? Transferred(InkPlan? plan)
    {
        if (transfer is null || plan is null || plan.PaintsNothing)
        {
            return plan;
        }

        int[] components = [.. plan.Colorants.Select(c => c.Name switch { "Cyan" => 0, "Magenta" => 1, "Yellow" => 2, _ => 3 })];
        InkPlan.Mapping tints = plan.Tints;
        return plan with
        {
            Tints = (c, t) =>
            {
                tints(c, t);
                for (int i = 0; i < t.Length && i < components.Length; i++)
                {
                    t[i] = transfer.Tint(components[i], t[i]);
                }
            },
        };
    }

    /// <summary>The ink colour for a colour in the space, or null when it is drawn as sRGB.</summary>
    public InkColor? Color(ImageColorSpace space, ReadOnlySpan<double> components)
    {
        return Plan(space)?.Color(components);
    }

    private InkPlan? Make(ImageColorSpace space, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        if (space.PaintsNothing)
        {
            return InkPlan.Nothing;
        }

        switch (space.Family)
        {
            case ColorFamily.Cmyk when space.Components == 4 && Process() is Colorant[] cmyk && !space.IsDevice && space.Profile is { Model: ColorModel.Cmyk, ChannelCount: 4 } own
                && !own.IsEquivalentTo(inks.Profile):
                {
                    // ICCBased CMYK in another press's profile is colour-managed into this one (8.6.5.5), as a RIP does.
                    ColorTransform transform = ColorTransform.Create(own, inks.Profile, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true });
                    return new InkPlan(cmyk, (c, t) =>
                    {
                        Span<float> from = [Unit(c[0]), Unit(c[1]), Unit(c[2]), Unit(c[3])];
                        transform.Apply(from, t[..4]);
                    }, Cmyk: true);
                }

            case ColorFamily.Cmyk when space.Components == 4 && Process() is Colorant[] cmyk:
                return new InkPlan(cmyk, (c, t) =>
                {
                    for (int i = 0; i < 4; i++)
                    {
                        t[i] = Unit(c[i]);
                    }
                }, Cmyk: true);
            case ColorFamily.Gray when space.IsDevice && Index("Black") >= 0:
                return new InkPlan([inks[Index("Black")]], (c, t) => t[0] = 1 - Unit(c[0]), Cmyk: false);
            case ColorFamily.Indexed when space is ImageColorSpace.IndexedSpace indexed && Make(indexed.Base, depth + 1) is InkPlan under:
                return new InkPlan(under.Colorants, (c, t) =>
                {
                    Span<double> components = stackalloc double[indexed.Base.Components];
                    indexed.BaseColor(c[0], components);
                    under.Tints(components, t);
                }, under.IsCmyk);
            case ColorFamily.Separation when space.Colorants is IReadOnlyList<string> names:
                return Colorants(space, names, depth);
            default:
                return null;
        }
    }

    /// <summary>A Separation or DeviceN space: every colourant on its own plate, or the whole colour through the alternate.</summary>
    private InkPlan? Colorants(ImageColorSpace space, IReadOnlyList<string> names, int depth)
    {
        List<Colorant> plates = [];
        List<(int Component, int Plate)> map = [];
        bool all = false;
        int allComponent = -1;
        for (int i = 0; i < names.Count; i++)
        {
            switch (names[i])
            {
                case "None":
                    continue;
                case "All":
                    all = true;
                    allComponent = i;
                    continue;
            }

            int index = names[i].Length > 0 ? Index(names[i]) : -1;
            if (index < 0 || inks.IsDisabled(index))
            {
                return space.Alternate is ImageColorSpace alternate && Make(alternate, depth + 1) is InkPlan through
                    ? new InkPlan(through.Colorants, (c, t) =>
                    {
                        Span<double> alt = stackalloc double[alternate.Components];
                        space.ToAlternate(c, alt);
                        through.Tints(alt, t);
                    }, through.IsCmyk)
                    : null;
            }

            int plate = plates.IndexOf(inks[index]);
            if (plate < 0)
            {
                plates.Add(inks[index]);
                plate = plates.Count - 1;
            }

            map.Add((i, plate));
        }

        if (all)
        {
            // All paints every plate the canvas has (8.6.6.4), registration marks for one.
            Colorant[] every = [.. Enumerable.Range(0, inks.Count).Where(i => !inks.IsDisabled(i)).Select(i => inks[i])];
            return new InkPlan(every, (c, t) => t.Fill(Unit(c[allComponent])), Cmyk: false);
        }

        (int Component, int Plate)[] pairs = [.. map];
        return new InkPlan([.. plates], (c, t) =>
        {
            t.Clear();
            foreach ((int component, int plate) in pairs)
            {
                t[plate] = Math.Max(t[plate], Unit(c[component]));
            }
        }, Cmyk: false);
    }

    /// <summary>Cyan, Magenta, Yellow and Black from the canvas's process inks, or null when it lacks one.</summary>
    private Colorant[]? Process()
    {
        int[] at = [Index("Cyan"), Index("Magenta"), Index("Yellow"), Index("Black")];
        return at.All(i => i >= 0) ? [.. at.Select(i => inks[i])] : null;
    }

    private int Index(string name) => inks.IndexOf(name);

    private static float Unit(double value) => double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : 0;

    private sealed class Holder(InkPlan? plan)
    {
        public InkPlan? Plan { get; } = plan;
    }
}

/// <summary>
/// How one colour space prints: the plates its colours name and how components become their tints. Overprint paints only
/// these plates; <paramref name="Cmyk"/> marks a process CMYK colour, for overprint mode 1.
/// </summary>
internal sealed record InkPlan(Colorant[] Colorants, InkPlan.Mapping Tints, bool Cmyk)
{
    /// <summary>A colourant that marks nothing (None).</summary>
    public static InkPlan Nothing { get; } = new([], (_, _) => { }, false);

    /// <summary>Components to the tint of each of <see cref="Colorants"/>, 0 to 1.</summary>
    public delegate void Mapping(ReadOnlySpan<double> components, Span<float> tints);

    public bool IsCmyk => Cmyk;

    /// <summary>Whether colour in the space marks nothing.</summary>
    public bool PaintsNothing => Colorants.Length == 0;

    /// <summary>The ink colour of one colour in the space.</summary>
    public InkColor Color(ReadOnlySpan<double> components)
    {
        if (Colorants.Length == 0)
        {
            return InkColor.None;
        }

        Span<float> tints = stackalloc float[Colorants.Length];
        Tints(components, tints);
        if (Cmyk)
        {
            return InkColor.Cmyk(tints[0], tints[1], tints[2], tints[3]);
        }

        InkTint[] mix = new InkTint[Colorants.Length];
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] = new InkTint(Colorants[i], tints[i]);
        }

        return InkColor.Mix(mix);
    }
}
