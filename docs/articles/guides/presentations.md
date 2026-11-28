# Presentations

Egap.Presentations loads and makes PowerPoint files and lets you read and edit their slides from code: the slide
masters, their layouts and themes, the slides, and the shapes on each, with their ids, names, positions, placeholders and
text, tables, notes, comments and sections. It reads
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
points too; it is null for a placeholder that takes its position from its layout (`EffectiveTransform` follows it there). `Placeholder` gives a placeholder's type
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

## Building a deck

`Presentation.Create()` makes a new 16:9 presentation with no slides: one slide master with Egap's own theme (Office-like
colours, Calibri Light and Calibri) and the eleven standard layouts, from Title Slide to Vertical Title and Text.
`SlideMaster.GetLayout` finds a layout by its type. `Slides.Add` and `Slides.Insert` add a slide that follows a layout,
with an empty placeholder for each of the layout's but the date, footer and slide number; `Remove`, `RemoveAt` and `Move`
take a slide out or move it, and a removed slide's notes page, comments and the pictures only it used go with it.

`Shapes.AddShape` adds a preset shape (`rect`, `roundRect`, `ellipse` and the rest) filled in the theme's first accent
colour, `AddTextBox` a text box, `AddPicture` a PNG, JPEG, GIF, BMP or TIFF picture (an image stored once however often it
is used), `AddGroup` a group to add shapes to, `AddConnector` a line whose `Start` and `End` attach it to shapes, and
`AddTable` a table. Each new shape gets an id no other shape on its slide has. Setting `TextBody.Text` replaces a shape's
text, paragraphs ended by `\r` and line breaks as `\v`, keeping the first run's formatting.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Create();
SlideMaster master = deck.SlideMasters[0];

Slide cover = deck.Slides.Add(master.GetLayout(SlideLayoutType.Title)!);
((AutoShape)cover.Shapes[0]).TextBody!.Text = "Quarterly review";
((AutoShape)cover.Shapes[1]).TextBody!.Text = "Third quarter";

Slide shapes = deck.Slides.Add(master.GetLayout(SlideLayoutType.TitleOnly)!);
((AutoShape)shapes.Shapes[0]).TextBody!.Text = "Process";
AutoShape plan = shapes.Shapes.AddShape("roundRect", ShapeTransform.FromPoints(60, 150, 200, 100), "Plan");
AutoShape ship = shapes.Shapes.AddShape("ellipse", ShapeTransform.FromPoints(500, 150, 160, 100), "Ship");
ConnectorShape arrow = shapes.Shapes.AddConnector(ShapeTransform.FromPoints(260, 200, 240, 0));
arrow.Start = new ShapeConnection(plan.Id, 3);
arrow.End = new ShapeConnection(ship.Id, 1);
shapes.Shapes.AddTextBox(ShapeTransform.FromPoints(60, 300, 400, 40), "Two steps\vfrom idea to customer");
shapes.Shapes.AddPicture(File.ReadAllBytes("logo.png"), ShapeTransform.FromPoints(760, 30, 160, 80));

deck.Save("review.pptx");
```

## Placeholders and inheritance

A placeholder on a slide takes what it does not set itself from the matching placeholder on its layout, and that one from
the master's. `InheritedPlaceholder` gives the placeholder one step up: for a slide's placeholder the layout's with the same
index, or else the same type; for a layout's, the master's of the same type, a centred title taking the master's title and
the content kinds (subtitle, content, table, chart, picture) its body. `EffectiveTransform` is where the shape is drawn: its
own `Transform`, or the first one up the chain.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
foreach (SlideShape shape in deck.Slides[0].Shapes)
{
    SlideShape? fromLayout = shape.InheritedPlaceholder;
    Console.WriteLine($"{shape.Name}: drawn at {shape.EffectiveTransform}, own {shape.Transform}, from {fromLayout?.Owner.Name}");
}
```

## Tables

`GraphicFrame.Table` gives a table frame's table: its `Columns` (grid widths), `Rows` (heights) and their `Cells`, one per
grid column. A merged block shows the text of its top left cell: `ColumnSpan` and `RowSpan` give its size, and the cells
it covers say so with `IsCoveredHorizontally` and `IsCoveredVertically`; `Origin` finds the cell whose text a block shows.
`InsertRow`, `AddRow`, `InsertColumn`, `AddColumn`, `RemoveRow` and `RemoveColumn` change the grid and keep merges whole,
`Merge` and `Split` merge cells and split them again, and `StyleId` names the table style.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Create();
Slide slide = deck.Slides.Add(deck.SlideMasters[0].GetLayout(SlideLayoutType.TitleOnly)!);
((AutoShape)slide.Shapes[0]).TextBody!.Text = "Sales";

SlideTable table = slide.Shapes.AddTable(3, 3, ShapeTransform.FromPoints(60, 150, 600, 120)).Table!;
string[] header = ["Region", "Q2", "Q3"];
for (int column = 0; column < 3; column++)
{
    table.Cell(0, column).Text = header[column];
}

table.Cell(1, 0).Text = "North";
table.Cell(2, 0).Text = "South";
table.AddRow().Cells[0].Text = "Total";
table.Merge(3, 1, 1, 2).Text = "Up 12 % on the half";
deck.Save("sales.pptx");
```

## Notes, comments and sections

`Slide.NotesText` gets and sets the speaker's notes; setting it on a slide without notes makes a notes page, and a notes
master when the presentation has none. `Slide.Notes` is the notes page itself.

`Slide.Comments` lists a slide's comment threads, each with its `Replies`. Both kinds are read: the modern, threaded
comments of PowerPoint for Microsoft 365 and the older kind. Comments need an author from
`Presentation.CommentAuthors.Add`; new comments are modern unless the presentation has authors of the older kind only
(`CommentAuthors.WritesModern`). `Comments.Add` starts a thread, `SlideComment.Reply` answers it and `Comments.Remove`
takes a comment (with its replies) away.

`Presentation.Sections` groups the slides in named runs, as PowerPoint's slide sorter shows them. `Sections.Add(name)` adds
a section at the end, `Add(name, slide)` one starting at a slide, `MoveSlide` moves a slide into a section, and `Remove`
merges a section's slides into the one before it. Slides added, moved and removed stay in step with their sections.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("review.pptx");
Slide cover = deck.Slides[0];
cover.NotesText = "Welcome everyone.\rKeep this under a minute.";

CommentAuthor reviewer = deck.CommentAuthors.Add("Ada Example", "AE");
CommentAuthor author = deck.CommentAuthors.Add("Bob Example", "BE");
SlideComment question = cover.Comments.Add(reviewer, "Should the date be on the cover?");
question.Reply(author, "Added it to the subtitle.");

SlideSection opening = deck.Sections.Add("Opening");     // the first section takes every slide
SlideSection detail = deck.Sections.Add("Detail", deck.Slides[1]);
opening.Name = "Introduction";
Console.WriteLine($"{detail.Name}: {detail.Slides.Count} slides");
deck.Save("review.pptx");
```

## Known limits

- Fills, outlines and text formatting are kept and written back but not in the model; new shapes take the theme's look.
  Charts, SmartArt, transitions and animations are kept as they are.
- New placeholders on a slide copy the layout's type and index only; the inherited text and list styles are not worked
  out, only the position (`EffectiveTransform`).
- Removing a slide leaves links to it from other slides as they are.
- Encrypted presentations and PowerPoint 97-2003 files (PPT) are not read.
- Slides are not rendered to images or PDF.
