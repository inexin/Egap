# Egap.Presentations: guide for coding agents

Egap.Presentations loads PowerPoint files (PPTX, PPTM, POTX, POTM, PPSX, PPSM, transitional or Strict), reads and edits
their slides, layouts, masters and shapes, and saves them with everything the model does not hold kept as it was.

Namespace: `Egap.Presentations`, with `Egap.Documents` for `DocumentException`, `ThemeColor` and `ThemeFont`, and
`Egami.Pixels` for theme colours (`Rgba8`).

{{shared}}

## Entry points

- `Presentation.Load(path | stream, PresentationLoadOptions)` and `presentation.Save(path | stream)`; `Format` is the
  kind of file, which a save keeps.
- `presentation.Slides` (`[0]`, `FindById`), `SlideMasters` (each with `Layouts`, `Theme`, `ColorMap`), `SlideSize`
  (settable, `SlideSize.FromPoints`), `NotesSize`.
- `Slide`: `Layout`, `Master`, `SlideId`, `Hidden`, `NotesPartName`, `ColorMap`, `ColorMapOverride`, `Shapes`.
- `slide.Shapes` holds `SlideShape`s: `AutoShape` (`PresetGeometry`, `TextBody`), `PictureShape` (`GetImageData`),
  `GroupShape` (`Shapes`, `ChildArea`), `ConnectorShape` (`Start`, `End`) and `GraphicFrame` (`Kind`). Each has `Id`,
  `Name`, `Description`, `Placeholder` and `Transform`.
- `TextBody.Paragraphs`, `TextParagraph.Runs`, `TextRun.Text` (settable, keeps the run's formatting).

## Samples

Read a deck:

{{snippet guides/presentations.md#Presentations}}

Change and save:

{{snippet guides/presentations.md#Loading and saving}}

Edit shapes:

{{snippet guides/presentations.md#Shapes}}

## Common mistakes

- Expecting `Transform` on a placeholder. A placeholder that takes its position from its layout has a null `Transform`;
  find the layout's placeholder with the same `Placeholder` type and index.
- Treating sizes as points. `ShapeTransform` and `SlideSize` are in EMUs (914,400 to the inch, 12,700 to the point);
  `XPoints`, `WidthPoints` and `FromPoints` convert.
- Adding slides or shapes. The model edits a loaded file in place; it cannot add or remove slides, shapes or text yet.
- Catching the wrong exception. A file that is not a presentation throws `DocumentException` from `Egap.Documents`.
