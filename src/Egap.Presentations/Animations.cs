using System.Globalization;
using System.Xml.Linq;

using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// The animations of a slide (<c>p:timing</c>) as a list of effects, read when asked. Egap keeps the timing tree whole and
/// does not play it; it can remove all of it, or the effects on one shape. Removing a shape removes its effects too.
/// </summary>
public sealed class SlideTiming
{
    internal SlideTiming(IReadOnlyList<AnimationEffect> effects)
    {
        Effects = effects;
    }

    /// <summary>The effects, in the order the timing tree holds them: the order the show plays them in.</summary>
    public IReadOnlyList<AnimationEffect> Effects { get; }
}

/// <summary>One animation effect: what it animates, which of PowerPoint's effects it is, and when it starts.</summary>
/// <param name="ShapeId">The id of the shape it animates (<c>p:spTgt/@spid</c>), or null when it animates none (a sound, say).</param>
/// <param name="PresetClass">The kind of effect (<c>presetClass</c>).</param>
/// <param name="PresetId">PowerPoint's number for the effect within its kind (<c>presetID</c>): 10 is Fade, 2 Fly In, and so on.</param>
/// <param name="PresetSubtype">The effect's variant (<c>presetSubtype</c>), such as the direction a Fly In comes from.</param>
/// <param name="Trigger">What starts it (<c>nodeType</c>).</param>
/// <param name="DelayMilliseconds">How long after its trigger it starts, in milliseconds; 0 when the file gives none.</param>
/// <param name="DurationMilliseconds">How long it lasts, in milliseconds: its longest behaviour; null when it repeats without end or gives none.</param>
/// <param name="TriggerShapeId">For an effect a click on a shape starts, that shape's id; otherwise null.</param>
public sealed record AnimationEffect(
    uint? ShapeId,
    AnimationClass PresetClass,
    int PresetId,
    int PresetSubtype,
    AnimationTrigger Trigger,
    int DelayMilliseconds,
    int? DurationMilliseconds,
    uint? TriggerShapeId);

/// <summary>The kinds of animation effect PowerPoint has.</summary>
public enum AnimationClass
{
    /// <summary>A kind the file does not name, or one Egap does not know.</summary>
    Other,

    /// <summary>An entrance (<c>entr</c>).</summary>
    Entrance,

    /// <summary>An exit (<c>exit</c>).</summary>
    Exit,

    /// <summary>An emphasis (<c>emph</c>).</summary>
    Emphasis,

    /// <summary>A motion path (<c>path</c>).</summary>
    MotionPath,

    /// <summary>An OLE verb (<c>verb</c>).</summary>
    Verb,

    /// <summary>A media action: play, pause or stop (<c>mediacall</c>).</summary>
    Media,
}

/// <summary>What starts an animation effect.</summary>
public enum AnimationTrigger
{
    /// <summary>Something the file does not name.</summary>
    Other,

    /// <summary>A click (<c>clickEffect</c>).</summary>
    OnClick,

    /// <summary>With the effect before it (<c>withEffect</c>).</summary>
    WithPrevious,

    /// <summary>After the effect before it (<c>afterEffect</c>).</summary>
    AfterPrevious,
}

/// <summary>Reading and pruning a slide part's <c>p:timing</c>.</summary>
internal static class Animations
{
    private static readonly HashSet<string> TimeNodes = new(StringComparer.Ordinal)
    {
        "par", "seq", "excl", "anim", "animClr", "animEffect", "animMotion", "animRot", "animScale", "cmd", "set", "audio", "video",
    };

    public static SlideTiming Read(XElement root)
    {
        List<AnimationEffect> effects = [];
        if (root.Element(PptxNames.P + "timing")?.Element(PptxNames.P + "tnLst") is { } list)
        {
            foreach (XElement node in list.Descendants(PptxNames.P + "cTn"))
            {
                if (node.Attribute("presetClass") is null && node.Attribute("presetID") is null)
                {
                    continue;
                }

                effects.Add(new AnimationEffect(
                    ShapeIds(node.Parent!).FirstOrDefault() is uint id and > 0 ? id : null,
                    (string?)node.Attribute("presetClass") switch
                    {
                        "entr" => AnimationClass.Entrance,
                        "exit" => AnimationClass.Exit,
                        "emph" => AnimationClass.Emphasis,
                        "path" => AnimationClass.MotionPath,
                        "verb" => AnimationClass.Verb,
                        "mediacall" => AnimationClass.Media,
                        _ => AnimationClass.Other,
                    },
                    Int(node.Attribute("presetID")) ?? 0,
                    Int(node.Attribute("presetSubtype")) ?? 0,
                    (string?)node.Attribute("nodeType") switch
                    {
                        "clickEffect" => AnimationTrigger.OnClick,
                        "withEffect" => AnimationTrigger.WithPrevious,
                        "afterEffect" => AnimationTrigger.AfterPrevious,
                        _ => AnimationTrigger.Other,
                    },
                    Int(node.Element(PptxNames.P + "stCondLst")?.Elements(PptxNames.P + "cond").FirstOrDefault()?.Attribute("delay")) ?? 0,
                    Duration(node),
                    TriggerShape(node)));
            }
        }

        return new SlideTiming(effects);
    }

    /// <summary>Removes the whole timing tree; returns whether there was one.</summary>
    public static bool RemoveAll(XElement root)
    {
        if (root.Element(PptxNames.P + "timing") is not { } timing)
        {
            return false;
        }

        timing.Remove();
        return true;
    }

    /// <summary>
    /// Removes every effect that animates one of <paramref name="shapeIds"/>, every sequence a click on one of them starts,
    /// and their build entries, then the groups and sequences left empty; returns whether anything went.
    /// </summary>
    public static bool RemoveFor(XElement root, IReadOnlySet<uint> shapeIds)
    {
        if (shapeIds.Count == 0 || root.Element(PptxNames.P + "timing") is not { } timing)
        {
            return false;
        }

        bool changed = false;
        XElement? list = timing.Element(PptxNames.P + "tnLst");
        if (list is not null)
        {
            foreach (XElement target in list.Descendants(PptxNames.P + "spTgt").Where(t => Matches(t, shapeIds)).ToList())
            {
                if (target.Parent is null)
                {
                    continue;
                }

                // A target in a condition is a trigger: the sequence or node it starts goes. Otherwise the effect it animates goes.
                XElement? cond = target.Ancestors(PptxNames.P + "cond").FirstOrDefault();
                XElement? doomed = cond is not null ? TimeNodeOf(cond) : EffectOf(target);
                if (doomed?.Parent is not null)
                {
                    doomed.Remove();
                    changed = true;
                }
            }

            Prune(list);
            if (!list.Descendants(PptxNames.P + "cTn").Any(n => n.Attribute("presetClass") is not null || n.Attribute("presetID") is not null)
                && !list.Descendants().Any(e => e.Name == PptxNames.P + "audio" || e.Name == PptxNames.P + "video"))
            {
                timing.Remove();
                return true;
            }
        }

        if (timing.Element(PptxNames.P + "bldLst") is { } builds)
        {
            foreach (XElement build in builds.Elements().Where(b => PptxNames.UInt(b, "spid") is uint id && shapeIds.Contains(id)).ToList())
            {
                build.Remove();
                changed = true;
            }

            if (!builds.HasElements)
            {
                builds.Remove();
            }
        }

        return changed;
    }

    private static bool Matches(XElement target, IReadOnlySet<uint> shapeIds) => PptxNames.UInt(target, "spid") is uint id && shapeIds.Contains(id);

    /// <summary>The time node that holds the effect a target belongs to: the one whose cTn names a preset, else the nearest.</summary>
    private static XElement? EffectOf(XElement target)
    {
        XElement? nearest = null;
        foreach (XElement ancestor in target.Ancestors())
        {
            if (ancestor.Name.Namespace != PptxNames.P || !TimeNodes.Contains(ancestor.Name.LocalName))
            {
                continue;
            }

            nearest ??= ancestor;
            if (CommonNode(ancestor) is { } node && (node.Attribute("presetClass") is not null || node.Attribute("presetID") is not null))
            {
                return ancestor;
            }

            if (CommonNode(ancestor) is { } c && (string?)c.Attribute("nodeType") is "tmRoot" or "mainSeq" or "interactiveSeq" or "clickPar" or "withGroup" or "afterGroup")
            {
                break;
            }
        }

        return nearest;
    }

    private static XElement? TimeNodeOf(XElement cond)
    {
        return cond.Ancestors().FirstOrDefault(a => a.Name.Namespace == PptxNames.P && TimeNodes.Contains(a.Name.LocalName));
    }

    /// <summary>The common time node (<c>p:cTn</c>) of a time node: its own, or its common behaviour's or media node's.</summary>
    private static XElement? CommonNode(XElement timeNode)
    {
        return timeNode.Element(PptxNames.P + "cTn")
            ?? timeNode.Element(PptxNames.P + "cBhvr")?.Element(PptxNames.P + "cTn")
            ?? timeNode.Element(PptxNames.P + "cMediaNode")?.Element(PptxNames.P + "cTn");
    }

    /// <summary>Removes the parallel and sequence nodes whose children all went, from the leaves up; the root stays.</summary>
    private static void Prune(XElement list)
    {
        bool again = true;
        while (again)
        {
            again = false;
            foreach (XElement children in list.Descendants(PptxNames.P + "childTnLst").Where(c => !c.HasElements).ToList())
            {
                XElement? holder = children.Parent?.Parent;
                if (holder is null || holder.Parent is null || holder.Parent == list)
                {
                    continue;
                }

                holder.Remove();
                again = true;
            }
        }
    }

    private static IEnumerable<uint> ShapeIds(XElement timeNode)
    {
        foreach (XElement target in timeNode.Descendants(PptxNames.P + "spTgt"))
        {
            if (target.Ancestors(PptxNames.P + "cond").Any())
            {
                continue;
            }

            if (PptxNames.UInt(target, "spid") is uint id)
            {
                yield return id;
            }
        }
    }

    private static uint? TriggerShape(XElement node)
    {
        foreach (XElement ancestor in node.AncestorsAndSelf(PptxNames.P + "cTn"))
        {
            if ((string?)ancestor.Attribute("nodeType") == "interactiveSeq")
            {
                return ancestor.Element(PptxNames.P + "stCondLst")?.Descendants(PptxNames.P + "spTgt").Select(t => PptxNames.UInt(t, "spid")).FirstOrDefault(i => i is not null);
            }
        }

        return null;
    }

    private static int? Duration(XElement node)
    {
        int? longest = null;
        foreach (XElement inner in node.Descendants(PptxNames.P + "cTn"))
        {
            if (Int(inner.Attribute("dur")) is int duration)
            {
                longest = Math.Max(longest ?? 0, duration);
            }
        }

        return longest ?? Int(node.Attribute("dur"));
    }

    private static int? Int(XAttribute? attribute)
    {
        return int.TryParse((string?)attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }
}

/// <summary>A slide's transition and animations.</summary>
public sealed partial class Slide
{
    /// <summary>
    /// The slide's transition, or null when it has none. Setting one replaces it; null removes it. Effects newer than
    /// PowerPoint 2007, and durations, are written as PowerPoint writes them, with a fade for older readers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A duration or advance time is negative.</exception>
    public SlideTransition? Transition
    {
        get => Transitions.Read(Xml.Root);
        set
        {
            if (value is not null)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value.DurationMilliseconds ?? 0, nameof(value));
                ArgumentOutOfRangeException.ThrowIfNegative(value.AdvanceAfterMilliseconds ?? 0, nameof(value));
            }

            if (value == Transition)
            {
                return;
            }

            Transitions.Write(Xml, value);
        }
    }

    /// <summary>The slide's animation effects, read now from its timing tree; empty when it has none.</summary>
    public SlideTiming Timing => Animations.Read(Xml.Root);

    /// <summary>Removes every animation on the slide (its whole <c>p:timing</c>).</summary>
    /// <returns>Whether the slide had any.</returns>
    public bool RemoveAnimations()
    {
        if (!Animations.RemoveAll(Xml.Root))
        {
            return false;
        }

        Xml.MarkChanged();
        return true;
    }

    /// <summary>
    /// Removes the animations of one shape, and of the shapes in it when it is a group: the effects that animate it, the
    /// sequences a click on it starts, and its build entries.
    /// </summary>
    /// <returns>Whether there were any.</returns>
    /// <exception cref="ArgumentNullException">The shape is null.</exception>
    public bool RemoveAnimations(SlideShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return RemoveAnimationsOf(shape.Element);
    }
}

/// <summary>Animations removal for any slide part.</summary>
public abstract partial class SlideBase
{
    /// <summary>Removes the animations of the shape <paramref name="element"/> and of the shapes in it.</summary>
    internal bool RemoveAnimationsOf(XElement element)
    {
        HashSet<uint> ids = [];
        foreach (XElement properties in element.DescendantsAndSelf().Where(e => e.Name == PptxNames.P + "cNvPr"))
        {
            if (PptxNames.UInt(properties, "id") is uint id)
            {
                ids.Add(id);
            }
        }

        if (!Animations.RemoveFor(Xml.Root, ids))
        {
            return false;
        }

        Xml.MarkChanged();
        return true;
    }
}
