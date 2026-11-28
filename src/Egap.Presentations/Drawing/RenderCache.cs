using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Shaping;

using Egap.Documents;
using Egap.Documents.DrawingML;
using Egap.Documents.Layout;
using Egap.Presentations.Pptx;

namespace Egap.Presentations.Drawing;

/// <summary>
/// What drawing a presentation's slides reuses from one slide to the next: each master's theme under each colour map, the font
/// chain for each font settings, and the decoded pictures. Themes are read-only in the model, so a theme read once stays right.
/// </summary>
internal sealed class RenderCache
{
    private readonly Presentation presentation;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Part, string Map), Theme> themes = [];
    private readonly ConditionalWeakTable<PackagePart, BlipImage> pictures = [];
    private readonly ConditionalWeakTable<FontSettings, SlideFonts> fonts = [];
    private SlideFonts? defaultFonts;

    public RenderCache(Presentation presentation)
    {
        this.presentation = presentation;
    }

    /// <summary>The theme of <paramref name="themePart"/> (null for the standard Office colours and fonts) with <paramref name="map"/> applied.</summary>
    public Theme Theme(PackagePart? themePart, ColorMap map)
    {
        (string, string) key = (themePart?.Name ?? "", map.Key);
        lock (gate)
        {
            if (themes.TryGetValue(key, out Theme? found))
            {
                return found;
            }
        }

        Theme theme = new Document().Theme;
        if (themePart is not null)
        {
            try
            {
                XElement root = presentation.Package.Xml(themePart).Root;
                if (root.Name == PptxNames.A + "theme")
                {
                    theme.Read(root);
                }
            }
            catch (DocumentException)
            {
                // A damaged theme part draws with the standard theme.
            }
        }

        theme.SchemeMap = map.ToSchemeMap();
        lock (gate)
        {
            themes[key] = theme;
        }

        return theme;
    }

    /// <summary>The font chain for <paramref name="settings"/>: installed fonts, then the standard substitutes.</summary>
    public SlideFonts Fonts(FontSettings? settings)
    {
        lock (gate)
        {
            if (settings is null)
            {
                return defaultFonts ??= new SlideFonts(new FontProvider(new Document()));
            }

            return fonts.GetValue(settings, s => new SlideFonts(new FontProvider(new Document(), s)));
        }
    }

    /// <summary>The picture an image part holds, decoded once; null for a format nothing here draws.</summary>
    public BlipImage Picture(PackagePart part)
    {
        lock (gate)
        {
            return pictures.GetValue(part, p => new BlipImage(p.ContentType, p.Data));
        }
    }
}

/// <summary>The font chain one render uses, with what text setting asks of it again and again kept: each font's family and its line height per point.</summary>
internal sealed class SlideFonts
{
    private readonly ConcurrentDictionary<(string Name, FontStyle Style), FontFamily?> families = new();
    private readonly ConcurrentDictionary<(string Name, FontStyle Style), (float Ascent, float Descent)> metrics = new();

    public SlideFonts(FontProvider provider)
    {
        Provider = provider;
    }

    /// <summary>Egap.Documents' font chain: installed fonts, Office's cloud fonts, then the standard substitutes.</summary>
    public FontProvider Provider { get; }

    /// <summary>The fallback for characters the chosen fonts lack.</summary>
    public FontFallback Fallback => Provider.Fallback;

    /// <summary>The family a font name resolves to, or null when nothing stands in for it.</summary>
    public FontFamily? Family(string name, FontStyle style) => families.GetOrAdd((name, style), k => Provider.ResolveFamily(k.Name, k.Style));

    /// <summary>The font's ascent and descent for one point of size, both positive.</summary>
    public (float Ascent, float Descent) Metrics(string name, FontStyle style) => metrics.GetOrAdd((name, style), k =>
    {
        FontExtents extents = new Font(Provider.Resolve(k.Name, k.Style).Typeface, 100).Extents;
        return (Math.Abs(extents.Ascent) / 100, Math.Abs(extents.Descent) / 100);
    });

    /// <summary>
    /// A font's single line in PowerPoint, per point of size: 1.2 times the size, as PowerPoint sets Calibri, Arial and Times
    /// New Roman alike, or the font's ascent and descent where they are taller.
    /// </summary>
    public float SingleLine(string name, FontStyle style)
    {
        (float ascent, float descent) = Metrics(name, style);
        return Math.Max(1.2f, ascent + descent);
    }
}
