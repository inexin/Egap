using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// Floating table positions (<c>w:tblpPr</c>, <c>w:tblOverlap</c>, ECMA-376 Part 1, 17.4.57 and 17.4.56) and paragraph
/// frames (<c>w:framePr</c>, 17.3.1.11) to and from the model. Anchors the file leaves out are the page's, as the standard
/// says.
/// </summary>
internal static class DocxPositions
{
    public static TablePosition ReadTable(XElement tblpPr, XElement? overlap)
    {
        return new TablePosition
        {
            HorizontalAnchor = Horizontal(tblpPr.W("horzAnchor")),
            HorizontalAlignment = Alignment(tblpPr.W("tblpXSpec")),
            HorizontalOffset = DocxUnits.Points(tblpPr.W("tblpX")) ?? 0,
            VerticalAnchor = Vertical(tblpPr.W("vertAnchor")),
            VerticalAlignment = tblpPr.W("vertAnchor") == "text" ? FloatingAlignment.None : Alignment(tblpPr.W("tblpYSpec")),
            VerticalOffset = DocxUnits.Points(tblpPr.W("tblpY")) ?? 0,
            DistanceLeft = DocxUnits.Points(tblpPr.W("leftFromText")) ?? 0,
            DistanceRight = DocxUnits.Points(tblpPr.W("rightFromText")) ?? 0,
            DistanceTop = DocxUnits.Points(tblpPr.W("topFromText")) ?? 0,
            DistanceBottom = DocxUnits.Points(tblpPr.W("bottomFromText")) ?? 0,
            AllowOverlap = overlap.Val() != "never",
        };
    }

    public static (XElement Position, XElement? Overlap) WriteTable(TablePosition p)
    {
        XElement e = new(Ns.W + "tblpPr");
        Set(e, "leftFromText", p.DistanceLeft);
        Set(e, "rightFromText", p.DistanceRight);
        Set(e, "topFromText", p.DistanceTop);
        Set(e, "bottomFromText", p.DistanceBottom);
        e.SetAttributeValue(Ns.W + "vertAnchor", VerticalName(p.VerticalAnchor));
        e.SetAttributeValue(Ns.W + "horzAnchor", HorizontalName(p.HorizontalAnchor));
        e.SetAttributeValue(Ns.W + "tblpXSpec", AlignmentName(p.HorizontalAlignment, vertical: false));
        Set(e, "tblpX", p.HorizontalAlignment == FloatingAlignment.None ? p.HorizontalOffset : 0);
        e.SetAttributeValue(Ns.W + "tblpYSpec", AlignmentName(p.VerticalAlignment, vertical: true));
        Set(e, "tblpY", p.VerticalAlignment == FloatingAlignment.None ? p.VerticalOffset : 0);
        return (e, p.AllowOverlap ? null : DocxPropertyWriter.Val("tblOverlap", "never"));
    }

    public static FramePosition ReadFrame(XElement framePr)
    {
        return new FramePosition
        {
            Width = DocxUnits.Points(framePr.W("w")) is double w && w > 0 ? w : null,
            Height = DocxUnits.Points(framePr.W("h")) is double h && h > 0 ? h : null,
            HeightRule = framePr.W("hRule") switch
            {
                "atLeast" => RowHeightRule.AtLeast,
                "exact" => RowHeightRule.Exactly,
                _ => RowHeightRule.Auto,
            },
            HorizontalAnchor = Horizontal(framePr.W("hAnchor")),
            HorizontalAlignment = Alignment(framePr.W("xAlign")),
            HorizontalOffset = DocxUnits.Points(framePr.W("x")) ?? 0,
            VerticalAnchor = Vertical(framePr.W("vAnchor")),
            VerticalAlignment = framePr.W("vAnchor") == "text" ? FloatingAlignment.None : Alignment(framePr.W("yAlign")),
            VerticalOffset = DocxUnits.Points(framePr.W("y")) ?? 0,
            DistanceHorizontal = DocxUnits.Points(framePr.W("hSpace")) ?? 0,
            DistanceVertical = DocxUnits.Points(framePr.W("vSpace")) ?? 0,
            Wrap = framePr.W("wrap") switch
            {
                "around" => FrameWrap.Around,
                "notBeside" => FrameWrap.NotBeside,
                "none" => FrameWrap.None,
                "tight" => FrameWrap.Tight,
                "through" => FrameWrap.Through,
                _ => FrameWrap.Auto,
            },
            DropCap = framePr.W("dropCap") switch
            {
                "drop" => DropCap.Drop,
                "margin" => DropCap.Margin,
                _ => DropCap.None,
            },
            DropCapLines = DocxUnits.Int(framePr.W("lines")) is int lines && lines >= 1 ? lines : 1,
            AnchorLock = framePr.W("anchorLock") is "1" or "true" or "on",
        };
    }

    public static XElement WriteFrame(FramePosition f)
    {
        XElement e = new(Ns.W + "framePr");
        e.SetAttributeValue(Ns.W + "dropCap", f.DropCap switch { DropCap.Drop => "drop", DropCap.Margin => "margin", _ => null });
        e.SetAttributeValue(Ns.W + "lines", f.DropCap != DropCap.None && f.DropCapLines != 1 ? f.DropCapLines : null);
        if (f.Width is double width)
        {
            e.SetAttributeValue(Ns.W + "w", DocxUnits.Twips(width));
        }

        if (f.Height is double height)
        {
            e.SetAttributeValue(Ns.W + "h", DocxUnits.Twips(height));
        }

        Set(e, "vSpace", f.DistanceVertical);
        Set(e, "hSpace", f.DistanceHorizontal);
        e.SetAttributeValue(Ns.W + "wrap", f.Wrap switch
        {
            FrameWrap.Around => "around",
            FrameWrap.NotBeside => "notBeside",
            FrameWrap.None => "none",
            FrameWrap.Tight => "tight",
            FrameWrap.Through => "through",
            _ => null,
        });
        e.SetAttributeValue(Ns.W + "hAnchor", HorizontalName(f.HorizontalAnchor));
        e.SetAttributeValue(Ns.W + "vAnchor", VerticalName(f.VerticalAnchor));
        Set(e, "x", f.HorizontalAlignment == FloatingAlignment.None ? f.HorizontalOffset : 0);
        e.SetAttributeValue(Ns.W + "xAlign", AlignmentName(f.HorizontalAlignment, vertical: false));
        Set(e, "y", f.VerticalAlignment == FloatingAlignment.None ? f.VerticalOffset : 0);
        e.SetAttributeValue(Ns.W + "yAlign", AlignmentName(f.VerticalAlignment, vertical: true));
        e.SetAttributeValue(Ns.W + "hRule", f.HeightRule switch { RowHeightRule.AtLeast => "atLeast", RowHeightRule.Exactly => "exact", _ => null });
        e.SetAttributeValue(Ns.W + "anchorLock", f.AnchorLock ? "1" : null);
        return e;
    }

    private static HorizontalAnchor Horizontal(string? value)
    {
        return value switch
        {
            "text" => HorizontalAnchor.Column,
            "margin" => HorizontalAnchor.Margin,
            _ => HorizontalAnchor.Page,
        };
    }

    private static VerticalAnchor Vertical(string? value)
    {
        return value switch
        {
            "text" => VerticalAnchor.Paragraph,
            "margin" => VerticalAnchor.Margin,
            _ => VerticalAnchor.Page,
        };
    }

    private static string HorizontalName(HorizontalAnchor anchor)
    {
        return anchor switch
        {
            HorizontalAnchor.Page => "page",
            HorizontalAnchor.Margin => "margin",
            _ => "text",
        };
    }

    private static string VerticalName(VerticalAnchor anchor)
    {
        return anchor switch
        {
            VerticalAnchor.Page => "page",
            VerticalAnchor.Margin => "margin",
            _ => "text",
        };
    }

    /// <summary>ST_XAlign and ST_YAlign; <c>inline</c> places the frame where its text is, as no alignment does.</summary>
    private static FloatingAlignment Alignment(string? value)
    {
        return value switch
        {
            "left" or "top" => FloatingAlignment.Start,
            "center" => FloatingAlignment.Center,
            "right" or "bottom" => FloatingAlignment.End,
            "inside" => FloatingAlignment.Inside,
            "outside" => FloatingAlignment.Outside,
            _ => FloatingAlignment.None,
        };
    }

    private static string? AlignmentName(FloatingAlignment alignment, bool vertical)
    {
        return alignment switch
        {
            FloatingAlignment.Start => vertical ? "top" : "left",
            FloatingAlignment.Center => "center",
            FloatingAlignment.End => vertical ? "bottom" : "right",
            FloatingAlignment.Inside => "inside",
            FloatingAlignment.Outside => "outside",
            _ => null,
        };
    }

    private static void Set(XElement e, string name, double points)
    {
        e.SetAttributeValue(Ns.W + name, points == 0 ? null : DocxUnits.Twips(points));
    }
}
