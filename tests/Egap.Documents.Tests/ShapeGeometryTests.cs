using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;

using Egap.Documents.DrawingML;

namespace Egap.Documents.Tests;

/// <summary>DrawingML geometries: the guide formulas, the 187 presets and custom geometry.</summary>
public sealed class ShapeGeometryTests
{
    private static readonly XNamespace A = DrawingNs.A;

    [Fact]
    public void EveryPresetBuildsItsPaths()
    {
        List<string> names = [.. PresetGeometries.Names];
        Assert.Equal(187, names.Count);
        foreach (string name in names)
        {
            GeometryLayout layout = ShapeGeometry.Preset(name)!.Layout(144, 72);
            Assert.NotEmpty(layout.Paths);
            foreach (GeometryPath path in layout.Paths)
            {
                Assert.False(path.Path.IsEmpty, name);
                RectF bounds = path.Path.Bounds;
                Assert.True(float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) && float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height), name);
            }
        }
    }

    [Fact]
    public void RightArrowFollowsItsGuides()
    {
        // 200 x 100 points, adj1 50000 and adj2 50000: the shaft is half the height, the head half the shorter side long.
        GeometryLayout layout = ShapeGeometry.Preset("rightArrow")!.Layout(200, 100);
        Vector2[] points = [.. layout.Paths[0].Path.Points.ToArray()];

        Assert.Contains(new Vector2(0, 25), points);
        Assert.Contains(new Vector2(150, 25), points);
        Assert.Contains(new Vector2(150, 0), points);
        Assert.Contains(new Vector2(200, 50), points);
        Assert.Contains(new Vector2(150, 100), points);
        Assert.Contains(new Vector2(0, 75), points);

        // The text rectangle runs from the left to where the head's slope meets the shaft: x2 = x1 + y1 * dx1 / hd2 = 150 + 25.
        Assert.Equal(new RectF(0, 25, 175, 50), layout.TextRect);
    }

    [Fact]
    public void AdjustValuesFromTheFileReplaceTheDefaults()
    {
        XElement avLst = new(A + "avLst", new XElement(A + "gd", new XAttribute("name", "adj"), new XAttribute("fmla", "val 25000")));

        GeometryLayout layout = ShapeGeometry.Preset("roundRect")!.Layout(100, 40, avLst);

        // The corner is adj of the shorter side: 25 % of 40.
        Assert.Contains(layout.Paths[0].Path.Points.ToArray(), p => Vector2.Distance(p, new Vector2(10, 0)) < 0.001f);
        Assert.DoesNotContain(layout.Paths[0].Path.Points.ToArray(), p => Vector2.Distance(p, new Vector2(40 * 0.16667f, 0)) < 0.001f);
    }

    [Fact]
    public void ArcsFollowTheEllipse()
    {
        RectF bounds = ShapeGeometry.Preset("ellipse")!.Layout(120, 60).Paths[0].Path.TightBounds;

        Assert.Equal(0, bounds.X, 0.01f);
        Assert.Equal(0, bounds.Y, 0.01f);
        Assert.Equal(120, bounds.Width, 0.01f);
        Assert.Equal(60, bounds.Height, 0.01f);
    }

    [Fact]
    public void PieEndsOnItsAngles()
    {
        // pie's defaults run from 0 to 270 degrees, clockwise with y down: from the right middle round to the top middle.
        Vector2[] points = ShapeGeometry.Preset("pie")!.Layout(100, 100).Paths[0].Path.Points.ToArray();

        Assert.Equal(100, points[0].X, 0.01f);
        Assert.Equal(50, points[0].Y, 0.01f);
        Assert.Contains(points, p => Math.Abs(p.X - 50) < 0.01f && Math.Abs(p.Y) < 0.01f);
    }

    [Fact]
    public void ArcAnglesAreOnThePathsOwnEllipse()
    {
        // A circle in the path's 100 x 100 space, stretched to 200 x 100: the 45-degree point is the circle's, then stretched.
        XElement spPr = XElement.Parse(
            """
            <wps:spPr xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <a:custGeom><a:pathLst><a:path w="100" h="100">
                <a:moveTo><a:pt x="100" y="50"/></a:moveTo><a:arcTo wR="50" hR="50" stAng="0" swAng="2700000"/>
              </a:path></a:pathLst></a:custGeom>
            </wps:spPr>
            """);

        Vector2[] points = ShapeGeometry.Read(spPr)!.Layout(200, 100).Paths[0].Path.Points.ToArray();

        Assert.Equal(100 + (100 * MathF.Cos(MathF.PI / 4)), points[^1].X, 0.01f);
        Assert.Equal(50 + (50 * MathF.Sin(MathF.PI / 4)), points[^1].Y, 0.01f);
    }

    [Fact]
    public void ReadsCustomGeometry()
    {
        XElement spPr = XElement.Parse(
            """
            <wps:spPr xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <a:custGeom>
                <a:avLst/>
                <a:gdLst><a:gd name="mid" fmla="*/ w 1 2"/></a:gdLst>
                <a:rect l="l" t="t" r="r" b="b"/>
                <a:pathLst>
                  <a:path w="1000" h="1000">
                    <a:moveTo><a:pt x="0" y="1000"/></a:moveTo>
                    <a:lnTo><a:pt x="500" y="0"/></a:lnTo>
                    <a:lnTo><a:pt x="1000" y="1000"/></a:lnTo>
                    <a:close/>
                  </a:path>
                </a:pathLst>
              </a:custGeom>
            </wps:spPr>
            """);

        GeometryLayout layout = ShapeGeometry.Read(spPr)!.Layout(50, 20);

        Assert.Equal([new Vector2(0, 20), new Vector2(25, 0), new Vector2(50, 20)], layout.Paths[0].Path.Points.ToArray());
        Assert.Equal(new RectF(0, 0, 50, 20), layout.TextRect);
    }

    [Fact]
    public void UnknownPresetsAreNull()
    {
        Assert.Null(ShapeGeometry.Preset("noSuchShape"));
        Assert.Null(ShapeGeometry.Read(new XElement(A + "spPr")));
    }

    [Fact]
    public void PathsSayHowTheyFill()
    {
        GeometryLayout can = ShapeGeometry.Preset("can")!.Layout(60, 100);

        Assert.Contains(can.Paths, p => p.Fill == PathFill.Lighten);
        Assert.Contains(can.Paths, p => p.Fill == PathFill.None && p.Stroke);
    }
}
