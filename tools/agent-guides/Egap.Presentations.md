# Egap.Presentations: guide for coding agents

Egap.Presentations loads PowerPoint files (PPTX, PPTM, POTX, POTM, PPSX, PPSM, transitional or Strict) and makes new
ones, reads and edits their slides, layouts, masters, shapes, tables, notes, comments, sections, transitions, animations
and properties, opens and writes encrypted files, and saves them with everything the model does not hold kept as it was.

Namespace: `Egap.Presentations`, with `Egap.Documents` for `DocumentException`, `ThemeColor` and `ThemeFont`, and
`Egami.Pixels` for theme colours (`Rgba8`).

{{shared}}

## Entry points

- `Presentation.Load(path | stream, PresentationLoadOptions)` (`Password` for an encrypted file), `Presentation.Create()`
  (16:9, eleven layouts) and `presentation.Save(path | stream, PresentationSaveOptions)` (`Format`, `Password`); a path's
  extension picks the kind, a stream gets `presentation.Format` (settable).
- `presentation.SetModifyPassword`, `HasModifyPassword`, `VerifyModifyPassword`; `presentation.Properties` (`Title`,
  `Author`, `Company`, `SetCustom`); `presentation.SlideShow` (`Type`, `Loop`, `UseTimings`).
- `slide.Transition` (`SlideTransition(type) { AdvanceAfterMilliseconds = ... }`, null removes),
  `slide.Timing.Effects`, `slide.RemoveAnimations()`, `RemoveAnimations(shape)`.
- `presentation.Slides.Add(layout)`, `Insert`, `Remove`, `RemoveAt`, `Move`; `master.GetLayout(SlideLayoutType.Title)`.
- `slide.Shapes.AddShape(preset, transform, text)`, `AddTextBox`, `AddPicture(bytes)`, `AddGroup`, `AddConnector`,
  `AddTable(rows, columns, transform)`, `Remove`.
- `shape.InheritedPlaceholder`, `shape.EffectiveTransform`; `frame.Table` (`Cell(r, c).Text`, `AddRow`, `Merge`);
  `slide.NotesText`; `presentation.CommentAuthors.Add`, `slide.Comments.Add`, `comment.Reply`;
  `presentation.Sections.Add`, `MoveSlide`.
- `presentation.Slides` (`[0]`, `FindById`), `SlideMasters` (each with `Layouts`, `Theme`, `ColorMap`), `SlideSize`
  (settable, `SlideSize.FromPoints`), `NotesSize`.
- `Slide`: `Layout`, `Master`, `SlideId`, `Hidden`, `NotesPartName`, `ColorMap`, `ColorMapOverride`, `Shapes`.
- `slide.Shapes` holds `SlideShape`s: `AutoShape` (`PresetGeometry`, `TextBody`), `PictureShape` (`GetImageData`),
  `GroupShape` (`Shapes`, `ChildArea`), `ConnectorShape` (`Start`, `End`) and `GraphicFrame` (`Kind`). Each has `Id`,
  `Name`, `Description`, `Placeholder` and `Transform`.
- `TextBody.Paragraphs`, `TextParagraph.Runs`, `TextRun.Text` (settable, keeps the run's formatting).
- `slide.Render(SlideRenderOptions)` (`Dpi`, `Width`, `Fonts`, `Date`) gives a `Raster<Rgba8>`; `slide.Draw(canvas)` draws
  onto any Egami.Drawing `Canvas`, one unit to the point. Layouts, masters and notes pages have the same members.

## Samples

Read a deck:

{{snippet guides/presentations.md#Presentations}}

Change and save:

{{snippet guides/presentations.md#Loading and saving}}

Edit shapes:

{{snippet guides/presentations.md#Shapes}}

Build a deck:

{{snippet guides/presentations.md#Building a deck}}

Tables:

{{snippet guides/presentations.md#Tables}}

Passwords and file kinds:

{{snippet guides/presentations.md#Passwords and file kinds}}

Draw slides as pictures:

{{snippet guides/presentations.md#Drawing slides}}

Transitions and animations:

{{snippet guides/presentations.md#Transitions and animations}}

## Common mistakes

- Expecting `Transform` on a placeholder. A placeholder that takes its position from its layout has a null `Transform`;
  use `EffectiveTransform`, or `InheritedPlaceholder` for the layout's placeholder.
- Treating sizes as points. `ShapeTransform` and `SlideSize` are in EMUs (914,400 to the inch, 12,700 to the point);
  `XPoints`, `WidthPoints` and `FromPoints` convert.
- Setting a run's text to change a whole shape's text. `TextBody.Text` replaces every paragraph; `TextRun.Text` one run.
- Holding table rows or cells across `AddRow`, `RemoveColumn` and the like; read them from the table again.
- Catching the wrong exception. A file that is not a presentation throws `DocumentException` from `Egap.Documents`; a
  missing or wrong password `DocumentPasswordException` (check `WrongPassword`).
- Saving a `.pptm` to a `.pptx` path and expecting the macros: the extension picks the kind, and a kind without macros
  leaves the VBA project out.
- Building a new `SlideTransition` to change one setting of a loaded one; that drops its sound and effect settings. Use
  `slide.Transition with { ... }`.
- Expecting a save to keep the encryption. Without `PresentationSaveOptions.Password` the file is written unencrypted.
- Drawing onto a pixel canvas without scaling it. `Draw` works one canvas unit to the point; scale the canvas by
  dpi / 72 first, or call `Render`, which does.
