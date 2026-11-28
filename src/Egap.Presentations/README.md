# Egap.Presentations

PowerPoint files for .NET 10: presentations (PPTX, PPTM, POTX, POTM, PPSX, PPSM, transitional or Strict) loaded, read and
edited from code, and saved with what they hold kept. The model has the slide masters with their themes, colour maps and
layouts, and the slides with their shapes: shapes with text, pictures, groups, connectors, and frames for tables, charts,
SmartArt and embedded objects, each with its id, name, placeholder and position. Fully managed and MIT-licensed; part of
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
