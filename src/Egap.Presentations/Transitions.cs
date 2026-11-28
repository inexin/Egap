using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// How a slide comes onto the screen in the slide show (<c>p:transition</c>): the effect, its speed, and when the show moves
/// on. Egap keeps and edits transitions; it does not play them. Take one from <see cref="Slide.Transition"/> and change it
/// with a <c>with</c> expression to keep what the model does not hold (its sound and the effect's own settings).
/// </summary>
/// <param name="Type">The effect.</param>
public sealed record SlideTransition(SlideTransitionType Type)
{
    /// <summary>The speed PowerPoint 2007 knew (<c>spd</c>); <see cref="DurationMilliseconds"/> wins where it is set.</summary>
    public TransitionSpeed Speed { get; init; } = TransitionSpeed.Fast;

    /// <summary>How long the effect takes, in milliseconds (<c>p14:dur</c>), or null for the length <see cref="Speed"/> gives.</summary>
    public int? DurationMilliseconds { get; init; }

    /// <summary>Whether a click moves the show on to the next slide (<c>advClick</c>); true unless the file says otherwise.</summary>
    public bool AdvanceOnClick { get; init; } = true;

    /// <summary>The time after which the show moves on by itself, in milliseconds (<c>advTm</c>), or null when it waits.</summary>
    public int? AdvanceAfterMilliseconds { get; init; }

    /// <summary>
    /// The direction of the effect as the file spells it (<c>dir</c>: <c>l</c>, <c>r</c>, <c>u</c>, <c>d</c>, <c>horz</c>,
    /// <c>vert</c>, <c>in</c>, <c>out</c> and the like), or null for the effect's default.
    /// </summary>
    public string? Direction { get; init; }

    /// <summary>
    /// The name of the sound played with the transition, or null when there is none. A sound is kept from the file: build the
    /// transition from the one read, with a <c>with</c> expression, to keep it; a new transition has none.
    /// </summary>
    public string? SoundName => Sound?.Element(PptxNames.P + "stSnd")?.Element(PptxNames.P + "snd")?.Attribute("name")?.Value;

    /// <summary>
    /// The name of a PowerPoint 2013 effect when <see cref="Type"/> is <see cref="SlideTransitionType.Preset"/>
    /// (<c>p15:prstTrans/@prst</c>: <c>fallOver</c>, <c>drape</c>, <c>curtains</c>, <c>wind</c>, <c>prestige</c>,
    /// <c>fracture</c>, <c>crush</c>, <c>peelOff</c>, <c>pageCurlDouble</c>, <c>pageCurlSingle</c>, <c>airplane</c>,
    /// <c>origami</c>); null otherwise, and Fall Over when it is null on a preset.
    /// </summary>
    public string? Preset { get; init; }

    /// <summary>The effect element as read, so that settings the model does not hold come back when the type stays.</summary>
    internal XElement? Effect { get; init; }

    /// <summary>The sound action (<c>p:sndAc</c>) as read.</summary>
    internal XElement? Sound { get; init; }
}

/// <summary>The speeds a transition can have in the PowerPoint 2007 schema.</summary>
public enum TransitionSpeed
{
    /// <summary>Slow (<c>slow</c>).</summary>
    Slow,

    /// <summary>Medium (<c>med</c>).</summary>
    Medium,

    /// <summary>Fast (<c>fast</c>), the default.</summary>
    Fast,
}

/// <summary>The transition effects: those of ECMA-376, of PowerPoint 2010 (<c>p14</c>), 2013 (<c>p15</c>) and Morph.</summary>
public enum SlideTransitionType
{
    /// <summary>An effect Egap does not name; it is kept as it is.</summary>
    Other,

    /// <summary>No effect: the slide just appears, though the transition can still time the show (a <c>p:transition</c> with no effect).</summary>
    None,

    /// <summary>Cut (<c>p:cut</c>).</summary>
    Cut,

    /// <summary>Fade (<c>p:fade</c>).</summary>
    Fade,

    /// <summary>Push (<c>p:push</c>).</summary>
    Push,

    /// <summary>Wipe (<c>p:wipe</c>).</summary>
    Wipe,

    /// <summary>Split (<c>p:split</c>).</summary>
    Split,

    /// <summary>Uncover (<c>p:pull</c>).</summary>
    Uncover,

    /// <summary>Cover (<c>p:cover</c>).</summary>
    Cover,

    /// <summary>Random bars (<c>p:randomBar</c>).</summary>
    RandomBars,

    /// <summary>Blinds (<c>p:blinds</c>).</summary>
    Blinds,

    /// <summary>Checkerboard (<c>p:checker</c>).</summary>
    Checker,

    /// <summary>Comb (<c>p:comb</c>).</summary>
    Comb,

    /// <summary>Dissolve (<c>p:dissolve</c>).</summary>
    Dissolve,

    /// <summary>Newsflash (<c>p:newsflash</c>).</summary>
    Newsflash,

    /// <summary>Random (<c>p:random</c>).</summary>
    Random,

    /// <summary>Strips (<c>p:strips</c>).</summary>
    Strips,

    /// <summary>Wheel (<c>p:wheel</c>).</summary>
    Wheel,

    /// <summary>Wedge (<c>p:wedge</c>).</summary>
    Wedge,

    /// <summary>Zoom (<c>p:zoom</c>).</summary>
    Zoom,

    /// <summary>Circle (<c>p:circle</c>).</summary>
    Circle,

    /// <summary>Diamond (<c>p:diamond</c>).</summary>
    Diamond,

    /// <summary>Plus (<c>p:plus</c>).</summary>
    Plus,

    /// <summary>Reveal (<c>p14:reveal</c>).</summary>
    Reveal,

    /// <summary>Flash (<c>p14:flash</c>).</summary>
    Flash,

    /// <summary>Vortex (<c>p14:vortex</c>).</summary>
    Vortex,

    /// <summary>Switch (<c>p14:switch</c>).</summary>
    Switch,

    /// <summary>Flip (<c>p14:flip</c>).</summary>
    Flip,

    /// <summary>Ripple (<c>p14:ripple</c>).</summary>
    Ripple,

    /// <summary>Honeycomb (<c>p14:honeycomb</c>).</summary>
    Honeycomb,

    /// <summary>Cube and the other prism effects (<c>p14:prism</c>).</summary>
    Prism,

    /// <summary>Doors (<c>p14:doors</c>).</summary>
    Doors,

    /// <summary>Window (<c>p14:window</c>).</summary>
    Window,

    /// <summary>Ferris wheel (<c>p14:ferris</c>).</summary>
    Ferris,

    /// <summary>Gallery (<c>p14:gallery</c>).</summary>
    Gallery,

    /// <summary>Conveyor (<c>p14:conveyor</c>).</summary>
    Conveyor,

    /// <summary>Pan (<c>p14:pan</c>).</summary>
    Pan,

    /// <summary>Glitter (<c>p14:glitter</c>).</summary>
    Glitter,

    /// <summary>Warp (<c>p14:warp</c>).</summary>
    Warp,

    /// <summary>Fly through (<c>p14:flythrough</c>).</summary>
    FlyThrough,

    /// <summary>Shred (<c>p14:shred</c>).</summary>
    Shred,

    /// <summary>Clock and its kin (<c>p14:wheelReverse</c>).</summary>
    WheelReverse,

    /// <summary>A PowerPoint 2013 effect (<c>p15:prstTrans</c>), named by <see cref="SlideTransition.Preset"/>: Fall Over, Drape, Curtains, Wind, Prestige, Fracture, Crush, Peel Off, Page Curl, Airplane, Origami.</summary>
    Preset,

    /// <summary>Morph (<c>p159:morph</c>).</summary>
    Morph,
}

/// <summary>Reading and writing a slide's <c>p:transition</c>, bare or in an <c>mc:AlternateContent</c> as PowerPoint writes newer effects.</summary>
internal static class Transitions
{
    private static readonly XNamespace P159 = "http://schemas.microsoft.com/office/powerpoint/2015/09/main";

    private static readonly (SlideTransitionType Type, XName Name)[] Names =
    [
        (SlideTransitionType.Cut, PptxNames.P + "cut"), (SlideTransitionType.Fade, PptxNames.P + "fade"), (SlideTransitionType.Push, PptxNames.P + "push"),
        (SlideTransitionType.Wipe, PptxNames.P + "wipe"), (SlideTransitionType.Split, PptxNames.P + "split"), (SlideTransitionType.Uncover, PptxNames.P + "pull"),
        (SlideTransitionType.Cover, PptxNames.P + "cover"), (SlideTransitionType.RandomBars, PptxNames.P + "randomBar"), (SlideTransitionType.Blinds, PptxNames.P + "blinds"),
        (SlideTransitionType.Checker, PptxNames.P + "checker"), (SlideTransitionType.Comb, PptxNames.P + "comb"), (SlideTransitionType.Dissolve, PptxNames.P + "dissolve"),
        (SlideTransitionType.Newsflash, PptxNames.P + "newsflash"), (SlideTransitionType.Random, PptxNames.P + "random"), (SlideTransitionType.Strips, PptxNames.P + "strips"),
        (SlideTransitionType.Wheel, PptxNames.P + "wheel"), (SlideTransitionType.Wedge, PptxNames.P + "wedge"), (SlideTransitionType.Zoom, PptxNames.P + "zoom"),
        (SlideTransitionType.Circle, PptxNames.P + "circle"), (SlideTransitionType.Diamond, PptxNames.P + "diamond"), (SlideTransitionType.Plus, PptxNames.P + "plus"),
        (SlideTransitionType.Reveal, PptxNames.P14 + "reveal"), (SlideTransitionType.Flash, PptxNames.P14 + "flash"), (SlideTransitionType.Vortex, PptxNames.P14 + "vortex"),
        (SlideTransitionType.Switch, PptxNames.P14 + "switch"), (SlideTransitionType.Flip, PptxNames.P14 + "flip"), (SlideTransitionType.Ripple, PptxNames.P14 + "ripple"),
        (SlideTransitionType.Honeycomb, PptxNames.P14 + "honeycomb"), (SlideTransitionType.Prism, PptxNames.P14 + "prism"), (SlideTransitionType.Doors, PptxNames.P14 + "doors"),
        (SlideTransitionType.Window, PptxNames.P14 + "window"), (SlideTransitionType.Ferris, PptxNames.P14 + "ferris"), (SlideTransitionType.Gallery, PptxNames.P14 + "gallery"),
        (SlideTransitionType.Conveyor, PptxNames.P14 + "conveyor"), (SlideTransitionType.Pan, PptxNames.P14 + "pan"), (SlideTransitionType.Glitter, PptxNames.P14 + "glitter"),
        (SlideTransitionType.Warp, PptxNames.P14 + "warp"), (SlideTransitionType.FlyThrough, PptxNames.P14 + "flythrough"), (SlideTransitionType.Shred, PptxNames.P14 + "shred"),
        (SlideTransitionType.WheelReverse, PptxNames.P14 + "wheelReverse"), (SlideTransitionType.Preset, PptxNames.P15 + "prstTrans"), (SlideTransitionType.Morph, P159 + "morph"),
    ];

    /// <summary>The transition a slide part has, or null.</summary>
    public static SlideTransition? Read(XElement root)
    {
        if (Find(root) is not { } found)
        {
            return null;
        }

        XElement transition = found.Read;
        XElement? effect = transition.Elements().FirstOrDefault(e => e.Name != PptxNames.P + "sndAc" && e.Name != PptxNames.P + "extLst");
        SlideTransitionType type = effect is null ? SlideTransitionType.None : TypeOf(effect.Name);
        return new SlideTransition(type)
        {
            Speed = (string?)transition.Attribute("spd") switch
            {
                "slow" => TransitionSpeed.Slow,
                "med" => TransitionSpeed.Medium,
                _ => TransitionSpeed.Fast,
            },
            DurationMilliseconds = Int(transition.Attribute(PptxNames.P14 + "dur")),
            AdvanceOnClick = PptxNames.Bool(transition, "advClick", fallback: true),
            AdvanceAfterMilliseconds = Int(transition.Attribute("advTm")),
            Direction = (string?)effect?.Attribute("dir"),
            Preset = type == SlideTransitionType.Preset ? (string?)effect?.Attribute("prst") : null,
            Effect = effect,
            Sound = transition.Element(PptxNames.P + "sndAc"),
        };
    }

    /// <summary>Replaces the transition of a slide part (null removes it).</summary>
    public static void Write(XmlPart xml, SlideTransition? value)
    {
        XElement root = xml.Root;
        if (Find(root) is { } old)
        {
            old.Outer.Remove();
        }

        if (value is not null)
        {
            Insert(root, Build(value));
        }

        xml.MarkChanged();
    }

    /// <summary>The element to read and the element in the tree that holds it (itself or its mc:AlternateContent).</summary>
    private static (XElement Read, XElement Outer)? Find(XElement root)
    {
        foreach (XElement child in root.Elements())
        {
            if (child.Name == PptxNames.P + "transition")
            {
                return (child, child);
            }

            if (MarkupCompatibility.IsAlternateContent(child)
                && MarkupCompatibility.Choose(child, ns => PptxNames.Understood(ns) || ns == P159.NamespaceName).FirstOrDefault(e => e.Name == PptxNames.P + "transition") is { } chosen)
            {
                return (chosen, child);
            }
        }

        return null;
    }

    private static XElement Build(SlideTransition value)
    {
        XName name = value.Type switch
        {
            SlideTransitionType.Other => value.Effect?.Name ?? PptxNames.P + "fade",
            SlideTransitionType.None => PptxNames.P + "transition",
            _ => NameOf(value.Type),
        };
        XElement? effect;
        if (value.Effect is { } kept && kept.Name == name)
        {
            effect = new XElement(kept);
        }
        else
        {
            effect = value.Type == SlideTransitionType.None ? null : new XElement(name);
        }

        if (effect is not null)
        {
            effect.SetAttributeValue("dir", value.Direction);
            if (value.Type == SlideTransitionType.Preset)
            {
                effect.SetAttributeValue("prst", value.Preset ?? (string?)effect.Attribute("prst") ?? "fallOver");
            }
        }

        XNamespace ns = effect?.Name.Namespace ?? PptxNames.P;
        bool newer = ns != PptxNames.P || value.DurationMilliseconds is not null;
        XElement Transition(bool withNewer)
        {
            XElement transition = new(PptxNames.P + "transition");
            if (value.Speed != TransitionSpeed.Fast)
            {
                transition.SetAttributeValue("spd", value.Speed == TransitionSpeed.Slow ? "slow" : "med");
            }

            if (withNewer && value.DurationMilliseconds is int duration)
            {
                transition.SetAttributeValue(PptxNames.P14 + "dur", duration.ToString(CultureInfo.InvariantCulture));
            }

            if (!value.AdvanceOnClick)
            {
                transition.SetAttributeValue("advClick", "0");
            }

            if (value.AdvanceAfterMilliseconds is int after)
            {
                transition.SetAttributeValue("advTm", after.ToString(CultureInfo.InvariantCulture));
            }

            if (withNewer || ns == PptxNames.P)
            {
                if (effect is not null)
                {
                    transition.Add(new XElement(effect));
                }
            }
            else
            {
                // PowerPoint 2007 sees a fade in place of an effect it does not know.
                transition.Add(new XElement(PptxNames.P + "fade"));
            }

            if (value.Sound is { } sound)
            {
                transition.Add(new XElement(sound));
            }

            return transition;
        }

        if (!newer)
        {
            return Transition(withNewer: true);
        }

        // Newer effects and durations go in a choice for the applications that know them, with a fallback for the rest.
        string prefix = ns == P159 ? "p159" : ns == PptxNames.P15 ? "p15" : "p14";
        XElement choice = new(MarkupCompatibility.Mc + "Choice", new XAttribute(XNamespace.Xmlns + prefix, ns == PptxNames.P ? PptxNames.P14.NamespaceName : ns.NamespaceName),
            new XAttribute("Requires", prefix), Transition(withNewer: true));
        if (prefix != "p14" && value.DurationMilliseconds is not null)
        {
            choice.Add(new XAttribute(XNamespace.Xmlns + "p14", PptxNames.P14.NamespaceName));
        }

        return new XElement(MarkupCompatibility.Mc + "AlternateContent", new XAttribute(XNamespace.Xmlns + "mc", MarkupCompatibility.Mc.NamespaceName),
            choice, new XElement(MarkupCompatibility.Mc + "Fallback", Transition(withNewer: false)));
    }

    /// <summary>Puts the transition where CT_Slide's sequence wants it: after <c>p:clrMapOvr</c>, before <c>p:timing</c>.</summary>
    private static void Insert(XElement root, XElement transition)
    {
        XElement? after = root.Elements().LastOrDefault(e => e.Name == PptxNames.P + "cSld" || e.Name == PptxNames.P + "clrMapOvr");
        if (after is null)
        {
            root.AddFirst(transition);
        }
        else
        {
            after.AddAfterSelf(transition);
        }
    }

    private static SlideTransitionType TypeOf(XName name)
    {
        foreach ((SlideTransitionType type, XName known) in Names)
        {
            if (known == name)
            {
                return type;
            }
        }

        return SlideTransitionType.Other;
    }

    private static XName NameOf(SlideTransitionType type)
    {
        foreach ((SlideTransitionType known, XName name) in Names)
        {
            if (known == type)
            {
                return name;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(type), type, "Not a transition effect Egap can write.");
    }

    private static int? Int(XAttribute? attribute)
    {
        return int.TryParse((string?)attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0 ? value : null;
    }
}
