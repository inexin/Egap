# Presentations

Egap.Presentations loads and makes PowerPoint files and lets you read and edit their slides from code: the slide
masters, their layouts and themes, the slides, and the shapes on each, with their ids, names, positions, placeholders and
text, tables, notes, comments and sections, transitions and animations, and the file's properties. It reads
PPTX, PPTM, POTX, POTM, PPSX and PPSM files, in transitional or Strict namespaces, encrypted or not. The types are in
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

`Presentation.Load` reads a file from a path or a stream. `Save` to a path writes the kind of file its extension names;
`Save` to a stream writes `Presentation.Format`, the kind it was loaded as until you set it (see
[Passwords and file kinds](#passwords-and-file-kinds)). Everything the model does not hold, from charts and fills to notes
pages and parts it has never heard of, is written back as it was: a part the model did not change keeps its bytes, and a part it
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

## Passwords and file kinds

`PresentationLoadOptions.Password` opens an encrypted presentation, by agile encryption (PowerPoint 2010 and later) or
standard encryption (PowerPoint 2007). A missing or wrong password throws `DocumentPasswordException`, whose
`WrongPassword` tells the two apart. `PresentationSaveOptions.Password` encrypts the file it saves, with agile encryption
(AES-256 and SHA-512, as PowerPoint writes it); without one, a save writes the file unencrypted, even when it was loaded
encrypted.

The password to modify is not encryption: PowerPoint opens the file read-only unless you give it. `SetModifyPassword` sets
it (a salted SHA-512 hash, as PowerPoint 2013 and later write it) or removes it with null, `HasModifyPassword` tells whether
there is one and `VerifyModifyPassword` checks one.

Saving as another kind, with `PresentationSaveOptions.Format`, the extension of the path, or `Presentation.Format`, changes
the main part's content type: a presentation becomes a template (`.potx`) or a show (`.ppsx`), which PowerPoint opens
straight into the slide show. Saving a deck with macros (`.pptm`, `.potm`, `.ppsm`) as a kind without them leaves its VBA
project and the parts only it uses out of the file; the presentation keeps them, so a later save with macros has them
again.

```csharp
using Egap.Documents;
using Egap.Presentations;

try
{
    Presentation deck = Presentation.Load("board.pptx", new PresentationLoadOptions { Password = "open sesame" });
    deck.SetModifyPassword("editors only");
    deck.Save("board-show.ppsx", new PresentationSaveOptions { Password = "open sesame" });
    deck.Save("board-template.potx");
}
catch (DocumentPasswordException e) when (e.WrongPassword)
{
    Console.WriteLine("That is not the password.");
}
```

## Transitions and animations

Egap keeps transitions and animations and lets you edit them; it does not play them.

`Slide.Transition` is the slide's transition, or null when it has none: its `Type` (the effects of PowerPoint 2007, 2010
and 2013, and Morph), `Speed` or `DurationMilliseconds`, `Direction`, `AdvanceOnClick` and `AdvanceAfterMilliseconds`, and
the `SoundName` of a sound it plays. Setting it replaces the transition and null removes it. Effects newer than PowerPoint
2007, and durations, are written as PowerPoint writes them, with a fade for older readers. Change a transition you read
with a `with` expression, so that its sound and the effect's own settings stay.

`Slide.Timing` lists the slide's animation effects in the order the show plays them: the shape each animates, its kind
(`Entrance`, `Exit`, `Emphasis`, `MotionPath` and the rest), PowerPoint's preset number and variant, what starts it, its
delay and duration, and for an effect a click on a shape starts, that shape. The timing tree itself is kept whole.
`RemoveAnimations()` removes all of a slide's animations and `RemoveAnimations(shape)` those of one shape (and of the
shapes in it, for a group). Removing a shape removes its animations too.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("pitch.pptx");
foreach (Slide slide in deck.Slides)
{
    slide.Transition = slide.Transition is { } transition
        ? transition with { AdvanceAfterMilliseconds = 5000 }
        : new SlideTransition(SlideTransitionType.Fade) { AdvanceAfterMilliseconds = 5000 };
    foreach (AnimationEffect effect in slide.Timing.Effects)
    {
        Console.WriteLine($"{effect.Trigger} {effect.PresetClass} {effect.PresetId} on shape {effect.ShapeId}");
    }
}

deck.Slides[0].RemoveAnimations();
deck.Save("pitch-timed.pptx");
```

## Properties and show settings

`Presentation.Properties` holds the file's title, subject, author, keywords, comments, category, who saved it last and
when it was made, saved and printed, the company and manager, and custom properties by name (`GetCustom`, `SetCustom`,
`CustomNames`). The names follow `DocumentProperties` in Egap.Documents. They are read from the file each time and written
into it when set, so everything else in those parts stays as it was. Egap does not change the saved date on a save.

`Presentation.SlideShow` is how the show runs: `Type` (a speaker, a reader in a window, or a kiosk, which loops by
itself), `Loop`, `UseTimings`, `ShowNarration` and `WithoutAnimations`.

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("lobby.pptx");
deck.Properties.Title = "Welcome";
deck.Properties.Company = "Example Ltd";
deck.Properties.SetCustom("Screen", "Lobby");
deck.SlideShow.Type = SlideShowType.Kiosk;
foreach (Slide slide in deck.Slides)
{
    slide.Transition = new SlideTransition(SlideTransitionType.Push) { AdvanceAfterMilliseconds = 8000, AdvanceOnClick = false };
}

deck.Save("lobby.ppsx");
```

## Drawing slides

`slide.Render()` draws a slide as an Egami `Raster<Rgba8>`, 96 dots per inch unless `SlideRenderOptions` sets `Dpi` or
`Width`; `slide.Draw(canvas)` draws it onto any Egami.Drawing `Canvas` instead, such as an SVG or PDF page, one unit to
the point. Layouts, masters and notes pages draw the same way.

```csharp
using Egami;
using Egami.Pixels;
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
SlideRenderOptions options = new() { Width = 1920, Date = new DateTime(2026, 11, 28) };
for (int i = 0; i < deck.Slides.Count; i++)
{
    using Raster<Rgba8> picture = deck.Slides[i].Render(options);
    picture.Save($"slide-{i + 1}.png");
}
```

What is drawn:

- The background (the slide's, its layout's or its master's, colours worked out through the colour map), then the master's
  and layout's own shapes unless the slide hides them (`showMasterSp`), then the slide's shapes, back to front.
- Shapes with their preset or custom geometry, fills (solid, gradient, pattern, picture), outlines, effects and theme
  style references; groups; pictures with their cropping, stretching and tiling.
- Text with everything a placeholder inherits from its layout, master and the master's text styles: insets, anchoring,
  columns, vertical text, rotation, the stored autofit scale, alignment, indents, line and paragraph spacing, tab stops,
  right-to-left paragraphs, bullets (characters, automatic numbers, pictures), fonts by script, bold, italic, underline
  styles, strikethrough, superscript and subscript, capitals, character spacing, kerning, solid and gradient text fills,
  outlines, highlights, hyperlink colours, and slide number and date fields.

`SlideRenderOptions.Fonts` sets where fonts come from and what stands in for missing ones, as Egap.Documents'
`FontSettings` does; `Date` the date that date fields show, today when it is null. Lines are spaced as PowerPoint spaces
them, 1.2 times the font size at single spacing.

## Recipes

Short answers to common tasks in this area. Each one is complete: open, change, save.

### Strip the animations and macros from a deck

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("handout.pptm");
foreach (Slide slide in deck.Slides)
{
    slide.RemoveAnimations();
    slide.Transition = null;
}

deck.Save("handout.pptx");     // a .pptx has no macros
```

### Encrypt a deck

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("salaries.pptx");
deck.Save("salaries-locked.pptx", new PresentationSaveOptions { Password = "Long passphrase 2026" });
```

## Known limits

- Fills, outlines and text formatting are kept and written back but not in the model; new shapes take the theme's look.
  Charts and SmartArt are kept as they are.
- Transitions and animations are kept and can be set or removed, but not played; animation effects cannot be added.
- New placeholders on a slide copy the layout's type and index only; the inherited text and list styles are not worked
  out, only the position (`EffectiveTransform`).
- Removing a slide leaves links to it from other slides as they are.
- PowerPoint 97-2003 files (PPT) and files protected with rights management (IRM) are not read.
- Drawing slides leaves out tables, charts, SmartArt diagrams, embedded objects, media and fonts embedded in the file;
  text is drawn with the stored autofit scale, not refitted, and is not hyphenated.
