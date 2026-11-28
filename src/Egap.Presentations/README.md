# Egap.Presentations

PowerPoint files for .NET 10: presentations (PPTX, PPTM, POTX, POTM, PPSX, PPSM, transitional or Strict) loaded or made
new, read and edited from code, and saved with what they hold kept. The model has the slide masters with their themes,
colour maps and layouts, and the slides with their shapes: shapes with text, pictures, groups, connectors, tables, and
frames for charts, SmartArt and embedded objects, each with its id, name, placeholder and position; and the slides' notes,
comments and sections, their transitions and animations, and the file's properties. Slides and shapes can be added, moved
and removed; transitions set and animations removed. Encrypted files open with their password and save with one, and a deck
saves as another kind (a template, a show, without its macros). Fully managed and MIT-licensed; part of
[Egap](https://github.com/inexin/Egap).

```csharp
using Egap.Presentations;

Presentation deck = Presentation.Load("quarterly.pptx");
foreach (Slide slide in deck.Slides)
{
    foreach (SlideShape shape in slide.Shapes)
    {
        if (shape is AutoShape { TextBody: { } body })
        {
            Console.WriteLine($"{shape.Name}: {body.Text}");
        }
    }
}

deck.Slides[0].Shapes[0].Transform = ShapeTransform.FromPoints(36, 36, 400, 80);
deck.Save("quarterly-edited.pptx");
```

A save writes every part the model did not change byte for byte, and a changed part with every element and attribute but
the values set, so transitions, animations, charts, notes and parts Egap does not know come back as they were.

Guide: [Presentations](https://inexin.github.io/Egap/v2/articles/guides/presentations.html).
