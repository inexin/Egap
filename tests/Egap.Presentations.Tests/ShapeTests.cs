using Egap.Documents;

namespace Egap.Presentations.Tests;

/// <summary>Shape trees: the kinds of shape, ids, names, transforms, placeholders, groups, pictures, connectors, frames and text.</summary>
public sealed class ShapeTests
{
    private const string Group =
        "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"10\" name=\"Group 9\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm rot=\"5400000\"><a:off x=\"1000\" y=\"2000\"/><a:ext cx=\"3000\" cy=\"4000\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"6000\" cy=\"8000\"/></a:xfrm></p:grpSpPr>"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"11\" name=\"Inner\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm flipH=\"1\"><a:off x=\"100\" y=\"200\"/><a:ext cx=\"300\" cy=\"400\"/></a:xfrm><a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom></p:spPr></p:sp>"
        + "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"12\" name=\"Nested\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"13\" name=\"Deep\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/></p:sp></p:grpSp>"
        + "</p:grpSp>";

    private const string Picture =
        "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Picture 3\" descr=\"A red dot\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>"
        + "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>"
        + "<p:spPr><a:xfrm><a:off x=\"127000\" y=\"254000\"/><a:ext cx=\"1270000\" cy=\"635000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>";

    private const string Connector =
        "<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"5\" name=\"Straight Arrow Connector 4\"/><p:cNvCxnSpPr><a:stCxn id=\"2\" idx=\"3\"/><a:endCxn id=\"4\" idx=\"1\"/></p:cNvCxnSpPr><p:nvPr/></p:nvCxnSpPr>"
        + "<p:spPr><a:xfrm flipV=\"1\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"500\" cy=\"600\"/></a:xfrm><a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom></p:spPr></p:cxnSp>";

    [Fact]
    public void EachKindOfShapeIsRead()
    {
        PptxBuilder builder = new() { Image = true };
        builder.Slides.Add(PptxBuilder.Rectangle(2, "Box", 914400, 457200, 1828800, 914400, "Hi") + Frame(3, "Table 2", "http://schemas.openxmlformats.org/drawingml/2006/table")
            + Picture + Connector + Group + Frame(6, "Chart 5", "http://schemas.openxmlformats.org/drawingml/2006/chart")
            + Frame(7, "Diagram 6", "http://schemas.openxmlformats.org/drawingml/2006/diagram") + Frame(8, "Object 7", "http://schemas.openxmlformats.org/presentationml/2006/ole")
            + Frame(9, "Ink 8", "http://example.com/ink"));

        Presentation presentation = PresentationTests.Load(builder);
        SlideShapeCollection shapes = presentation.Slides[0].Shapes;

        Assert.Equal(["Box", "Table 2", "Picture 3", "Straight Arrow Connector 4", "Group 9", "Chart 5", "Diagram 6", "Object 7", "Ink 8"], shapes.Select(s => s.Name));
        Assert.Equal([2u, 3u, 4u, 5u, 10u, 6u, 7u, 8u, 9u], shapes.Select(s => s.Id));
        Assert.All(shapes, s => Assert.Same(presentation.Slides[0], s.Owner));
        Assert.All(shapes, s => Assert.Null(s.Group));

        AutoShape box = Assert.IsType<AutoShape>(shapes[0]);
        Assert.Equal("rect", box.PresetGeometry);
        Assert.False(box.IsTextBox);
        Assert.Equal(new ShapeTransform(914400, 457200, 1828800, 914400), box.Transform);
        Assert.Equal(72, box.Transform!.Value.XPoints);
        Assert.Equal(36, box.Transform.Value.YPoints);
        Assert.Equal(144, box.Transform.Value.WidthPoints);
        Assert.Equal(72, box.Transform.Value.HeightPoints);
        Assert.Equal("Hi", box.TextBody!.Text);
        Assert.Null(box.Placeholder);

        Assert.Equal(
            [GraphicFrameKind.Table, GraphicFrameKind.Chart, GraphicFrameKind.Diagram, GraphicFrameKind.OleObject, GraphicFrameKind.Other],
            shapes.OfType<GraphicFrame>().Select(f => f.Kind));
        GraphicFrame table = Assert.IsType<GraphicFrame>(shapes[1]);
        Assert.Equal("http://schemas.openxmlformats.org/drawingml/2006/table", table.GraphicDataUri);
        Assert.Equal(new ShapeTransform(10, 20, 30, 40), table.Transform);

        PictureShape picture = Assert.IsType<PictureShape>(shapes[2]);
        Assert.Equal("A red dot", picture.Description);
        Assert.Equal("/ppt/media/image1.png", picture.ImagePartName);
        Assert.Equal("image/png", picture.ImageContentType);
        Assert.Equal(Convert.FromBase64String(PptxBuilder.PngBase64), picture.GetImageData());
        Assert.Equal(ShapeTransform.FromPoints(10, 20, 100, 50), picture.Transform);

        ConnectorShape connector = Assert.IsType<ConnectorShape>(shapes[3]);
        Assert.Equal("straightConnector1", connector.PresetGeometry);
        Assert.Equal(new ShapeConnection(2, 3), connector.Start);
        Assert.Equal(new ShapeConnection(4, 1), connector.End);
        Assert.True(connector.Transform!.Value.FlipVertical);
        Assert.False(connector.Transform.Value.FlipHorizontal);
        Assert.Same(box, shapes.FindById(connector.Start!.Value.ShapeId));
        Assert.Same(picture, shapes.FindById(connector.End!.Value.ShapeId));
    }

    [Fact]
    public void GroupsHoldTheirShapesInChildCoordinates()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(Group);

        SlideShapeCollection shapes = PresentationTests.Load(builder).Slides[0].Shapes;

        GroupShape group = Assert.IsType<GroupShape>(Assert.Single(shapes));
        Assert.Equal(new ShapeTransform(1000, 2000, 3000, 4000, Rotation: 90), group.Transform);
        Assert.Equal(new ShapeTransform(0, 0, 6000, 8000), group.ChildArea);
        Assert.Equal(2, group.Shapes.Count);
        AutoShape inner = Assert.IsType<AutoShape>(group.Shapes[0]);
        Assert.Same(group, inner.Group);
        Assert.Equal("ellipse", inner.PresetGeometry);
        Assert.True(inner.Transform!.Value.FlipHorizontal);
        GroupShape nested = Assert.IsType<GroupShape>(group.Shapes[1]);
        Assert.Same(group, nested.Group);
        Assert.Null(nested.Transform);
        Assert.Null(nested.ChildArea);
        Assert.Equal("Deep", shapes.FindById(13)!.Name);
        Assert.Same(nested, shapes.FindById(13)!.Group);
        Assert.Null(shapes.FindById(99));
        Assert.Null(((AutoShape)nested.Shapes[0]).TextBody);
    }

    [Fact]
    public void PlaceholdersHaveTypeAndIndex()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Placeholder(2, "Title 1", "title", null, null, null, null, null, "Agenda")
            + PptxBuilder.Placeholder(3, "Content Placeholder 2", null, 1, null, null, null, null, "Point")
            + PptxBuilder.Placeholder(4, "Slide Number 3", "sldNum", 12, null, null, null, null)
            + PptxBuilder.Placeholder(5, "Odd 4", "unknownKind", 7, null, null, null, null));
        builder.SlideLayouts.Add(1);

        Presentation presentation = PresentationTests.Load(builder);
        Slide slide = presentation.Slides[0];

        Assert.Equal(
            [new ShapePlaceholder(PlaceholderType.Title, 0), new ShapePlaceholder(PlaceholderType.Content, 1),
                new ShapePlaceholder(PlaceholderType.SlideNumber, 12), new ShapePlaceholder(PlaceholderType.Content, 7)],
            slide.Shapes.Select(s => s.Placeholder));
        Assert.All(slide.Shapes, s => Assert.Null(s.Transform));

        // The layout's and master's placeholders match the slide's by type and index.
        SlideLayout layout = slide.Layout!;
        Assert.Equal([PlaceholderType.Title, PlaceholderType.Content], layout.Shapes.Select(s => s.Placeholder!.Type));
        Assert.Equal(slide.Shapes[1].Placeholder, layout.Shapes[1].Placeholder);
        SlideShape masterTitle = Assert.Single(layout.Master.Shapes, s => s.Placeholder!.Type == PlaceholderType.Title);
        Assert.Equal(new ShapeTransform(838200, 365125, 10515600, 1325563), masterTitle.Transform);
        Assert.Equal("Click to edit Master title style", ((AutoShape)masterTitle).TextBody!.Text);
        Assert.Same(layout.Master, masterTitle.Owner);
    }

    [Fact]
    public void TextHasParagraphsRunsFieldsAndBreaks()
    {
        const string Text =
            "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"TextBox 1\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr/>"
            + "<p:txBody><a:bodyPr/><a:lstStyle/>"
            + "<a:p><a:r><a:rPr lang=\"en-US\" b=\"1\"/><a:t>Bold </a:t></a:r><a:r><a:rPr lang=\"en-US\"/><a:t>plain</a:t></a:r><a:br><a:rPr lang=\"en-US\"/></a:br><a:r><a:t>next line</a:t></a:r></a:p>"
            + "<a:p><a:pPr lvl=\"2\"/><a:r><a:t>Indented</a:t></a:r></a:p>"
            + "<a:p><a:fld id=\"{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}\" type=\"slidenum\"><a:rPr lang=\"en-US\"/><a:t>‹#›</a:t></a:fld><a:endParaRPr lang=\"en-US\"/></a:p>"
            + "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>"
            + "</p:txBody></p:sp>";
        PptxBuilder builder = new();
        builder.Slides.Add(Text);

        AutoShape shape = Assert.IsType<AutoShape>(PresentationTests.Load(builder).Slides[0].Shapes[0]);
        TextBody body = shape.TextBody!;

        Assert.True(shape.IsTextBox);
        Assert.Null(shape.PresetGeometry);
        Assert.Same(shape, body.Shape);
        Assert.Equal(4, body.Paragraphs.Count);
        Assert.Equal($"Bold plain{ControlChar.LineBreak}next line\rIndented\r‹#›\r", body.Text);
        TextParagraph first = body.Paragraphs[0];
        Assert.Same(body, first.Body);
        Assert.Equal(["Bold ", "plain", "next line"], first.Runs.Select(r => r.Text));
        Assert.Equal(0, first.Level);
        Assert.Equal(2, body.Paragraphs[1].Level);
        TextRun field = Assert.Single(body.Paragraphs[2].Runs);
        Assert.True(field.IsField);
        Assert.Equal("slidenum", field.FieldType);
        Assert.Same(body.Paragraphs[2], field.Paragraph);
        Assert.False(first.Runs[0].IsField);
        Assert.Null(first.Runs[0].FieldType);
        Assert.Empty(body.Paragraphs[3].Runs);
        Assert.Equal("", body.Paragraphs[3].Text);
    }

    [Fact]
    public void AlternateContentGivesTheBranchTheReaderUnderstands()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:p14=\"http://schemas.microsoft.com/office/powerpoint/2010/main\">"
            + "<mc:Choice Requires=\"p14\">" + PptxBuilder.Rectangle(2, "Modern", 0, 0, 10, 10, "new") + "</mc:Choice>"
            + "<mc:Fallback>" + PptxBuilder.Rectangle(2, "Fallback", 0, 0, 10, 10, "old") + "</mc:Fallback></mc:AlternateContent>"
            + "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:x9=\"http://example.com/future\">"
            + "<mc:Choice Requires=\"x9\">" + PptxBuilder.Rectangle(3, "Future", 0, 0, 10, 10, "new") + "</mc:Choice>"
            + "<mc:Fallback>" + PptxBuilder.Rectangle(3, "Picked fallback", 0, 0, 10, 10, "old") + "</mc:Fallback></mc:AlternateContent>"
            + "<p:contentPart r:id=\"rId9\"/>");

        SlideShapeCollection shapes = PresentationTests.Load(builder).Slides[0].Shapes;

        Assert.Equal(["Modern", "Picked fallback"], shapes.Select(s => s.Name));
    }

    private static string Frame(int id, string name, string uri) =>
        $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
        + $"<p:xfrm><a:off x=\"10\" y=\"20\"/><a:ext cx=\"30\" cy=\"40\"/></p:xfrm><a:graphic><a:graphicData uri=\"{uri}\"><x:kept xmlns:x=\"http://example.com/kept\"/></a:graphicData></a:graphic></p:graphicFrame>";
}
