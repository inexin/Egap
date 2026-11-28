# Presentations

Egap.Presentations loads PowerPoint files and lets you read and edit their slides from code: the slide masters, their
layouts and themes, the slides, and the shapes on each, with their ids, names, positions, placeholders and text. It reads
PPTX, PPTM, POTX, POTM, PPSX and PPSM files, in transitional or Strict namespaces. The types are in
`Egap.Presentations`.

> [!NOTE]
> [Known limits](#known-limits) lists what Egap.Presentations does not do yet.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
foreach (Slide slide in deck.Slides)
{
    Console.WriteLine($"Slide {slide.SlideId}: layout {slide.Layout?.Name}");
    foreach (SlideShape shape in slide.Shapes)
    {
        string text = shape is AutoShape { TextBody: { } body } ? body.Text : "";
        Console.WriteLine($"  {shape.Id} {shape.Name} {shape.Transform?.XPoints} {text}");
    }
}
```

## Loading and saving

`Presentation.Load` reads a file from a path or a stream. `Save` writes it back, as the same kind of file it was
(`Presentation.Format`). Everything the model does not hold, from transitions and animations to charts, notes pages and
parts it has never heard of, is written back as it was: a part the model did not change keeps its bytes, and a part it
changed keeps every element and attribute but the values set. A Strict file stays Strict.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
deck.SlideSize = SlideSize.FromPoints(720, 540);     // 4:3
SlideShape title = deck.Slides[0].Shapes[0];
title.Name = "Main title";
title.Description = "The quarter's headline";
deck.Save("quarterly-4x3.pptx");
```

A file that is not a presentation, or one damaged past reading, throws `DocumentException` (from Egap.Documents) with a
message that names the problem and the part. `PresentationLoadOptions.Limits` caps what an untrusted file may make the
reader use; past a limit it throws `DocumentLimitException`.

## Masters, layouts and slides

A presentation has one or more slide masters (`SlideMasters`). Each master has a theme (`PresentationTheme`: twelve
colours and the heading and body fonts), a colour map, and its layouts (`Layouts`). Each slide follows one layout
(`Slide.Layout`, and `Slide.Master` for its master). Slides, layouts and masters all have `Shapes` and a `ColorMap`; a
layout or slide that overrides its master's colour map has it in `ColorMapOverride`. `SlideLayout.LayoutType` says what kind
of layout it is (`SlideLayoutType.Title`, `TitleOnly`, `SectionHeader` and the rest). A slide the presentation lists whose
part is missing from the file is left out of `Slides`.

```csharp
using Egap.Documents;
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
SlideMaster master = deck.SlideMasters[0];
Console.WriteLine($"{master.Theme?.Name}: {master.Theme?.MajorLatinFont} / {master.Theme?.MinorLatinFont}");
foreach (SlideLayout layout in master.Layouts)
{
    Console.WriteLine($"{layout.Name} ({layout.LayoutType})");
}

Slide first = deck.Slides[0];
ThemeColor background = first.ColorMap[ColorMapSlot.Background1];   // Light1, or Dark1 on a dark slide
```

## Shapes

`Shapes` lists a slide's shapes in drawing order, the first at the back. Each is one of:

- `AutoShape`: a shape with a geometry and text (`p:sp`): rectangles, text boxes, placeholders. `PresetGeometry` names
  its preset (`rect`, `ellipse`, `rightArrow`), and `TextBody` has its paragraphs and runs.
- `PictureShape`: a picture; `ImagePartName`, `ImageContentType` and `GetImageData()` give its image.
- `GroupShape`: a group, with its own `Shapes` placed in its `ChildArea`.
- `ConnectorShape`: a line between shapes; `Start` and `End` name the shapes it is attached to.
- `GraphicFrame`: a table, chart, SmartArt diagram or embedded object, kept as it is; `Kind` says which.

`Transform` gives a shape's position, size, rotation and flips in EMUs (914,400 to the inch), with the position and size in
points too; it is null for a placeholder that takes its position from its layout. `Placeholder` gives a placeholder's type
and index, which match it to its layout's placeholder. A shape PowerPoint wrote two ways (`mc:AlternateContent`, for newer
and older readers) is read and changed in the branch Egap understands; the other branch keeps its values.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
Slide slide = deck.Slides[0];
foreach (SlideShape shape in slide.Shapes)
{
    if (shape.Placeholder is { Type: PlaceholderType.Title or PlaceholderType.CenteredTitle } && shape is AutoShape { TextBody: { } body })
    {
        body.Paragraphs[0].Runs[0].Text = "Results for Q3";
    }
}

SlideShape logo = slide.Shapes.FindById(4) ?? throw new InvalidOperationException("No logo");
logo.Transform = ShapeTransform.FromPoints(36, 36, 144, 72);
deck.Save("quarterly.pptx");
```

A run's text is replaced in place, so it keeps its font, size and colour. `TextBody.Text` and `TextParagraph.Text` give the
text with paragraphs ended by `\r` and line breaks as `\v`, as Egap.Documents' text does.

## Known limits

- Slides, shapes and text cannot be added or removed yet; a loaded file's slides and shapes are read and edited in place.
- Fills, outlines, text formatting, tables, charts, notes pages, comments, transitions and animations are kept and written
  back, but not in the model.
- A shape's inherited position and text styles (from its layout and master placeholder) are not worked out; `Transform` is
  the shape's own.
- Encrypted presentations and PowerPoint 97-2003 files (PPT) are not read.
- Slides are not rendered to images or PDF.
