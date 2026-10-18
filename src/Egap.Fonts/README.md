# Egap.Fonts

Open, metric-compatible substitutes for the fonts documents name most, for the Egap libraries and any
[Egami.Fonts](https://www.nuget.org/packages/Egami.Fonts) user: Liberation Sans, Serif and Mono for Helvetica, Arial, Times,
Times New Roman, Courier and Courier New, Carlito for Calibri and Caladea for Cambria. Text set in a substitute keeps the
original's line breaks, because every glyph has the same width. The fonts are under the SIL Open Font License 1.1; their
licences ship in the package.

```csharp
using Egami.Fonts;
using Egap.Fonts;

Typeface? arial = StandardFonts.Find("Arial", FontStyle.Normal);   // Liberation Sans
FontCollection all = StandardFonts.Collection;                    // every bundled face
```

Source and changelog: [github.com/inexin/Egap](https://github.com/inexin/Egap).
