using System.Xml.Linq;

namespace Egap.Documents.OfficeMath;

/// <summary>
/// A document's math properties (<c>m:mathPr</c>, ECMA-376 Part 1 §22.1.2.62): the math font, line breaking at binary operators,
/// display defaults, margins, spacing and limit placement, with Word's defaults for what the file leaves out. Lengths are in
/// twentieths of a point, as the file gives them.
/// </summary>
internal sealed class MathSettings
{
    /// <summary>The defaults: a document without <c>m:mathPr</c>.</summary>
    public static readonly MathSettings Default = new(new MathProperties());

    public MathSettings(MathProperties properties) => Properties = properties;

    /// <summary>The properties as read.</summary>
    public MathProperties Properties { get; }

    /// <summary>The math font (<c>m:mathFont</c>); Cambria Math when not set.</summary>
    public string Font => Properties.Get("mathFont") is { Length: > 0 } font ? font : "Cambria Math";

    /// <summary>Where a line breaks at a binary operator (<c>m:brkBin</c>: before, after, repeat); before when not set.</summary>
    public string BreakBinary => Properties.Get("brkBin") ?? "before";

    /// <summary>How a break at a subtraction repeats the sign (<c>m:brkBinSub</c>: --, -+, +-); -- when not set.</summary>
    public string BreakBinarySubtraction => Properties.Get("brkBinSub") ?? "--";

    /// <summary>Fractions in display equations are drawn small (<c>m:smallFrac</c>).</summary>
    public bool SmallFractions => Properties.On("smallFrac");

    /// <summary>Display equations use the default justification and margins (<c>m:dispDef</c>, on when not set).</summary>
    public bool DisplayDefaults => Properties.On("dispDef", absent: true);

    /// <summary>The left margin of display equations (<c>m:lMargin</c>).</summary>
    public int LeftMargin => Properties.Int("lMargin", 0);

    /// <summary>The right margin of display equations (<c>m:rMargin</c>).</summary>
    public int RightMargin => Properties.Int("rMargin", 0);

    /// <summary>The justification of display equations (<c>m:defJc</c>: left, right, center, centerGroup); centerGroup when not set.</summary>
    public string DefaultJustification => Properties.Get("defJc") ?? "centerGroup";

    /// <summary>
    /// The justification display equations get: <see cref="DefaultJustification"/> when <see cref="DisplayDefaults"/> is on;
    /// with it off Word centres the group whatever <c>m:defJc</c> says.
    /// </summary>
    public string DisplayJustification => DisplayDefaults ? DefaultJustification : "centerGroup";

    /// <summary>Space before a display equation (<c>m:preSp</c>).</summary>
    public int SpaceBefore => Properties.Int("preSp", 0);

    /// <summary>Space after a display equation (<c>m:postSp</c>).</summary>
    public int SpaceAfter => Properties.Int("postSp", 0);

    /// <summary>Space between the equations of a math paragraph (<c>m:interSp</c>).</summary>
    public int SpaceBetween => Properties.Int("interSp", 0);

    /// <summary>Space between the lines of one wrapped equation (<c>m:intraSp</c>).</summary>
    public int SpaceWithin => Properties.Int("intraSp", 0);

    /// <summary>The indent of the lines after the first of a wrapped equation (<c>m:wrapIndent</c>); an inch when not set.</summary>
    public int WrapIndent => Properties.Int("wrapIndent", 1440);

    /// <summary>The lines after the first of a wrapped equation align right (<c>m:wrapRight</c>).</summary>
    public bool WrapRight => Properties.On("wrapRight");

    /// <summary>Where integral limits go (<c>m:intLim</c>: subSup, undOvr); subSup when not set.</summary>
    public string IntegralLimits => Properties.Get("intLim") ?? "subSup";

    /// <summary>Where the limits of other n-ary operators go (<c>m:naryLim</c>); undOvr when not set.</summary>
    public string NaryLimits => Properties.Get("naryLim") ?? "undOvr";

    /// <summary>Reads an <c>m:mathPr</c> element.</summary>
    public static MathSettings Read(XElement mathPr) => new(Omml.ReadProperties(mathPr));
}
