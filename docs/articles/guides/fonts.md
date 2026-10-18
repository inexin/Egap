# Fonts

Egap.Fonts bundles open fonts with the same glyph widths as the fonts documents name most. Text set in a substitute keeps
the original's line breaks.

| Font in the document | Substitute |
| --- | --- |
| Helvetica, Arial | Liberation Sans |
| Times, Times New Roman | Liberation Serif |
| Courier, Courier New | Liberation Mono |
| Calibri | Carlito |
| Cambria | Caladea |

Egap.Pdf and Egap.Documents use them on their own when a font is missing. You can also use them with Egami.Fonts directly:

```csharp
using Egami.Fonts;
using Egap.Fonts;

Typeface? arial = StandardFonts.Find("Arial", FontStyle.Normal);   // Liberation Sans
FontCollection all = StandardFonts.Collection;                    // every bundled face
```

The fonts are under the SIL Open Font License 1.1, and their licences ship in the package.
