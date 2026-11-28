using System.Xml.Linq;

namespace Egap.Presentations.Tests;

/// <summary>Slide transitions and the animation timing tree: read, set, removed, and kept when the model leaves them be.</summary>
public sealed class TransitionTests
{
    private const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";

    // PowerPoint's own shape: a click starts a fade on shape 2 with a fly-in of shape 3 after 250 ms, and a click on shape 3
    // starts an emphasis of shape 2.
    private const string Timing = "<p:timing><p:tnLst><p:par><p:cTn id=\"1\" dur=\"indefinite\" restart=\"never\" nodeType=\"tmRoot\"><p:childTnLst>"
        + "<p:seq concurrent=\"1\" nextAc=\"seek\"><p:cTn id=\"2\" dur=\"indefinite\" nodeType=\"mainSeq\"><p:childTnLst>"
        + "<p:par><p:cTn id=\"3\" fill=\"hold\"><p:stCondLst><p:cond delay=\"indefinite\"/></p:stCondLst><p:childTnLst>"
        + "<p:par><p:cTn id=\"4\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
        + "<p:par><p:cTn id=\"5\" presetID=\"10\" presetClass=\"entr\" presetSubtype=\"0\" fill=\"hold\" grpId=\"0\" nodeType=\"clickEffect\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
        + "<p:set><p:cBhvr><p:cTn id=\"6\" dur=\"1\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst></p:cTn><p:tgtEl><p:spTgt spid=\"2\"/></p:tgtEl><p:attrNameLst><p:attrName>style.visibility</p:attrName></p:attrNameLst></p:cBhvr><p:to><p:strVal val=\"visible\"/></p:to></p:set>"
        + "<p:animEffect transition=\"in\" filter=\"fade\"><p:cBhvr><p:cTn id=\"7\" dur=\"500\"/><p:tgtEl><p:spTgt spid=\"2\"/></p:tgtEl></p:cBhvr></p:animEffect>"
        + "</p:childTnLst></p:cTn></p:par>"
        + "<p:par><p:cTn id=\"8\" presetID=\"2\" presetClass=\"entr\" presetSubtype=\"4\" fill=\"hold\" grpId=\"0\" nodeType=\"withEffect\"><p:stCondLst><p:cond delay=\"250\"/></p:stCondLst><p:childTnLst>"
        + "<p:set><p:cBhvr><p:cTn id=\"9\" dur=\"1\" fill=\"hold\"/><p:tgtEl><p:spTgt spid=\"3\"/></p:tgtEl><p:attrNameLst><p:attrName>style.visibility</p:attrName></p:attrNameLst></p:cBhvr><p:to><p:strVal val=\"visible\"/></p:to></p:set>"
        + "</p:childTnLst></p:cTn></p:par>"
        + "</p:childTnLst></p:cTn></p:par>"
        + "</p:childTnLst></p:cTn></p:par>"
        + "</p:childTnLst></p:cTn><p:prevCondLst><p:cond evt=\"onPrev\" delay=\"0\"><p:tgtEl><p:sldTgt/></p:tgtEl></p:cond></p:prevCondLst></p:seq>"
        + "<p:seq concurrent=\"1\" nextAc=\"seek\"><p:cTn id=\"10\" restart=\"whenNotActive\" fill=\"hold\" evtFilter=\"cancelBubble\" nodeType=\"interactiveSeq\"><p:stCondLst><p:cond evt=\"onClick\" delay=\"0\"><p:tgtEl><p:spTgt spid=\"3\"/></p:tgtEl></p:cond></p:stCondLst><p:childTnLst>"
        + "<p:par><p:cTn id=\"11\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
        + "<p:par><p:cTn id=\"12\" presetID=\"26\" presetClass=\"emph\" presetSubtype=\"0\" fill=\"hold\" nodeType=\"clickEffect\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
        + "<p:animEffect transition=\"out\" filter=\"fade\"><p:cBhvr><p:cTn id=\"13\" dur=\"300\"/><p:tgtEl><p:spTgt spid=\"2\"/></p:tgtEl></p:cBhvr></p:animEffect>"
        + "</p:childTnLst></p:cTn></p:par>"
        + "</p:childTnLst></p:cTn></p:par>"
        + "</p:childTnLst></p:cTn><p:nextCondLst><p:cond evt=\"onClick\" delay=\"0\"><p:tgtEl><p:spTgt spid=\"3\"/></p:tgtEl></p:cond></p:nextCondLst></p:seq>"
        + "</p:childTnLst></p:cTn></p:par></p:tnLst>"
        + "<p:bldLst><p:bldP spid=\"2\" grpId=\"0\"/><p:bldP spid=\"3\" grpId=\"0\"/></p:bldLst></p:timing>";

    private const string Vortex = "<mc:AlternateContent xmlns:mc=\"" + Mc + "\"><mc:Choice xmlns:p14=\"" + P14 + "\" Requires=\"p14\">"
        + "<p:transition spd=\"slow\" p14:dur=\"3000\" advClick=\"0\" advTm=\"5000\"><p14:vortex dir=\"r\"/><p:sndAc><p:stSnd><p:snd r:embed=\"rId9\" name=\"chime.wav\"/></p:stSnd></p:sndAc></p:transition>"
        + "</mc:Choice><mc:Fallback><p:transition spd=\"slow\" advClick=\"0\" advTm=\"5000\"><p:fade/><p:sndAc><p:stSnd><p:snd r:embed=\"rId9\" name=\"chime.wav\"/></p:stSnd></p:sndAc></p:transition></mc:Fallback></mc:AlternateContent>";

    [Fact]
    public void ATransitionInAnAlternateContentIsReadFromItsChoice()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Vortex);
        SlideTransition transition = presentation.Slides[0].Transition!;

        Assert.Equal(SlideTransitionType.Vortex, transition.Type);
        Assert.Equal(TransitionSpeed.Slow, transition.Speed);
        Assert.Equal(3000, transition.DurationMilliseconds);
        Assert.False(transition.AdvanceOnClick);
        Assert.Equal(5000, transition.AdvanceAfterMilliseconds);
        Assert.Equal("r", transition.Direction);
        Assert.Equal("chime.wav", transition.SoundName);
    }

    [Fact]
    public void AnUntouchedTransitionAndTimingAreWrittenBackAsTheyWere()
    {
        byte[] file = Build("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Vortex + Timing);
        Presentation presentation = Presentation.Load(new MemoryStream(file));
        Slide slide = presentation.Slides[0];
        slide.Transition = slide.Transition;
        _ = slide.Timing;

        using MemoryStream saved = new();
        presentation.Save(saved);
        Assert.Equal(PptxBuilder.Entry(file, "ppt/slides/slide1.xml"), PptxBuilder.Entry(saved.ToArray(), "ppt/slides/slide1.xml"));
    }

    [Fact]
    public void ChangingATransitionKeepsItsSoundAndEffectSettings()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Vortex + Timing);
        Slide slide = presentation.Slides[0];
        slide.Transition = slide.Transition! with { AdvanceAfterMilliseconds = 2000, AdvanceOnClick = true };

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        SlideTransition transition = back.Transition!;
        Assert.Equal(SlideTransitionType.Vortex, transition.Type);
        Assert.Equal(2000, transition.AdvanceAfterMilliseconds);
        Assert.True(transition.AdvanceOnClick);
        Assert.Equal(3000, transition.DurationMilliseconds);
        Assert.Equal("chime.wav", transition.SoundName);
        Assert.Equal(3, back.Timing.Effects.Count);

        // CT_Slide's order: the transition before the timing.
        XElement root = XElement.Parse(SlideXml(back));
        Assert.Equal(["cSld", "clrMapOvr", "AlternateContent", "timing"], root.Elements().Select(e => e.Name.LocalName));
    }

    [Theory]
    [InlineData(SlideTransitionType.Fade, null, "fade", false)]
    [InlineData(SlideTransitionType.Push, 1200, "push", true)]
    [InlineData(SlideTransitionType.Ripple, null, "ripple", true)]
    [InlineData(SlideTransitionType.Preset, null, "prstTrans", true)]
    [InlineData(SlideTransitionType.Morph, null, "morph", true)]
    [InlineData(SlideTransitionType.None, null, null, false)]
    public void SettingATransitionWritesItAsPowerPointDoes(SlideTransitionType type, int? duration, string? element, bool alternate)
    {
        Presentation presentation = Load();
        presentation.Slides[0].Transition = new SlideTransition(type) { DurationMilliseconds = duration, AdvanceAfterMilliseconds = 4000, Preset = type == SlideTransitionType.Preset ? "curtains" : null };

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        SlideTransition transition = back.Transition!;
        Assert.Equal(type, transition.Type);
        Assert.Equal(duration, transition.DurationMilliseconds);
        Assert.Equal(4000, transition.AdvanceAfterMilliseconds);
        XElement root = XElement.Parse(SlideXml(back));
        XElement outer = root.Elements().Single(e => e.Name.LocalName is "transition" or "AlternateContent");
        Assert.Equal(alternate, outer.Name.LocalName == "AlternateContent");
        if (alternate)
        {
            // Older readers get a fade, or the 2007 effect itself.
            XElement fallback = outer.Elements().Last().Elements().Single();
            Assert.Equal(PptxNamesP + "transition", fallback.Name);
            Assert.NotNull(fallback.Elements().FirstOrDefault(e => e.Name.LocalName is "fade" or "push"));
        }

        Assert.Equal(element, outer.Descendants().FirstOrDefault(e => e.Name.LocalName != "transition" && e.Name.LocalName != "Choice")?.Name.LocalName);
        if (type == SlideTransitionType.Preset)
        {
            Assert.Equal("curtains", transition.Preset);
        }
    }

    [Fact]
    public void RemovingATransitionLeavesNone()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Vortex);
        presentation.Slides[0].Transition = null;

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        Assert.Null(back.Transition);
        Assert.DoesNotContain("transition", SlideXml(back), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTimingListsEachEffect()
    {
        IReadOnlyList<AnimationEffect> effects = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Timing).Slides[0].Timing.Effects;

        Assert.Equal(
            [
                new AnimationEffect(2, AnimationClass.Entrance, 10, 0, AnimationTrigger.OnClick, 0, 500, null),
                new AnimationEffect(3, AnimationClass.Entrance, 2, 4, AnimationTrigger.WithPrevious, 250, 1, null),
                new AnimationEffect(2, AnimationClass.Emphasis, 26, 0, AnimationTrigger.OnClick, 0, 300, 3),
            ],
            effects);
    }

    [Fact]
    public void RemovingAShapesAnimationsLeavesTheOthers()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Timing);
        Slide slide = presentation.Slides[0];
        Assert.True(slide.RemoveAnimations(slide.Shapes.Single(s => s.Id == 3)));
        Assert.False(slide.RemoveAnimations(slide.Shapes.Single(s => s.Id == 3)));

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        AnimationEffect only = Assert.Single(back.Timing.Effects);
        Assert.Equal(2u, only.ShapeId);
        Assert.Equal(AnimationClass.Entrance, only.PresetClass);
        string xml = SlideXml(back);
        Assert.DoesNotContain("spid=\"3\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("interactiveSeq", xml, StringComparison.Ordinal);
        Assert.Contains("mainSeq", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingAShapeRemovesItsAnimations()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Timing);
        Slide slide = presentation.Slides[0];
        slide.Shapes.Remove(slide.Shapes.Single(s => s.Id == 2));

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        AnimationEffect only = Assert.Single(back.Timing.Effects);
        Assert.Equal(3u, only.ShapeId);
        Assert.DoesNotContain("spid=\"2\"", SlideXml(back), StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingTheLastAnimatedShapeRemovesTheTiming()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Timing);
        Slide slide = presentation.Slides[0];
        slide.Shapes.Remove(slide.Shapes.Single(s => s.Id == 2));
        slide.Shapes.Remove(slide.Shapes.Single(s => s.Id == 3));

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        Assert.Empty(back.Timing.Effects);
        Assert.DoesNotContain("timing", SlideXml(back), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAnimationsClearsTheTiming()
    {
        Presentation presentation = Load("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>" + Vortex + Timing);
        Assert.True(presentation.Slides[0].RemoveAnimations());
        Assert.False(presentation.Slides[0].RemoveAnimations());

        Slide back = BuildingTests.Reload(presentation).Slides[0];
        Assert.Empty(back.Timing.Effects);
        Assert.NotNull(back.Transition);
    }

    [Fact]
    public void ANegativeTimeIsRefused()
    {
        Slide slide = Load().Slides[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => slide.Transition = new SlideTransition(SlideTransitionType.Fade) { AdvanceAfterMilliseconds = -1 });
    }

    private static readonly XNamespace PptxNamesP = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private static byte[] Build(string? tail = null)
    {
        PptxBuilder builder = new()
        {
            SlideTail = tail ?? "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>",
        };
        builder.Slides.Add(PptxBuilder.Rectangle(2, "One", 0, 0, 100, 100, "One") + PptxBuilder.Rectangle(3, "Two", 200, 0, 100, 100, "Two"));
        return builder.Build();
    }

    private static Presentation Load(string? tail = null) => Presentation.Load(new MemoryStream(Build(tail)));

    private static string SlideXml(Slide slide)
    {
        using MemoryStream stream = new();
        slide.Presentation.Save(stream);
        return PptxBuilder.Entry(stream.ToArray(), slide.PartName[1..]);
    }
}
