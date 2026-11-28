using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Presentations.Drawing;

namespace Egap.Presentations;

/// <summary>How a slide, layout or master is drawn: the size of the picture, the fonts and the date fields show.</summary>
public sealed class SlideRenderOptions
{
    /// <summary>The resolution <see cref="SlideBase.Render"/> draws at, in dots per inch: 96 by default, which draws a 13.33 inch wide slide 1,280 pixels wide.</summary>
    public double Dpi { get; init; } = 96;

    /// <summary>The width in pixels <see cref="SlideBase.Render"/> draws at, with the height following the slide's shape; null to go by <see cref="Dpi"/>.</summary>
    public int? Width { get; init; }

    /// <summary>The fonts text is set in: where to find them and what stands in for missing ones; null for the installed fonts and the standard substitutes.</summary>
    public FontSettings? Fonts { get; init; }

    /// <summary>The date and time date fields show; null for now, as PowerPoint shows the day the file is opened.</summary>
    public DateTime? Date { get; init; }
}

public sealed partial class Presentation
{
    private RenderCache? renderCache;

    /// <summary>What drawing the slides reuses from one to the next.</summary>
    internal RenderCache RenderCache
    {
        get
        {
            if (renderCache is null)
            {
                Interlocked.CompareExchange(ref renderCache, new RenderCache(this), null);
            }

            return renderCache;
        }
    }
}

public abstract partial class SlideBase
{
    /// <summary>
    /// Draws the slide, layout or master onto <paramref name="canvas"/>, its top-left corner at the canvas's origin and one canvas
    /// unit to the point: the background, the master's and layout's shapes where the slide shows them, then its own shapes back
    /// to front with their text. Any Egami canvas works, such as a raster, an SVG or a PDF page. Tables, charts, SmartArt
    /// diagrams, embedded objects and media frames are left out for now.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="options">The fonts and date; null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> is null.</exception>
    public void Draw(Canvas canvas, SlideRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        new SlideRenderer(this, canvas, options ?? new SlideRenderOptions()).Draw();
    }

    /// <summary>The slide, layout or master drawn as a picture, at <see cref="SlideRenderOptions.Dpi"/> or <see cref="SlideRenderOptions.Width"/>.</summary>
    /// <param name="options">The size, fonts and date; null for 96 dots per inch.</param>
    /// <returns>The picture, as wide and high as the slide at that size, rounded up to whole pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The options' resolution or width is not positive.</exception>
    public Raster<Rgba8> Render(SlideRenderOptions? options = null)
    {
        options ??= new SlideRenderOptions();
        (double width, double height) = SizePoints;
        double scale;
        if (options.Width is int pixels)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixels, nameof(options));
            scale = pixels / Math.Max(width, 1e-6);
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Dpi, nameof(options));
            scale = options.Dpi / 72;
        }

        int w = options.Width ?? Math.Max(1, (int)Math.Ceiling((width * scale) - 1e-6));
        int h = Math.Max(1, (int)Math.Ceiling((height * scale) - 1e-6));
        Raster<Rgba8> raster = Raster.Create(w, h, new Rgba8(255, 255, 255, 255));
        using RasterCanvas<Rgba8> canvas = new(raster);
        canvas.Scale((float)scale, (float)scale);
        Draw(canvas, options);
        return raster;
    }

    /// <summary>The page's width and height in points: the presentation's slide size, or its notes size for a notes page or master.</summary>
    internal (double Width, double Height) SizePoints
    {
        get
        {
            SlideSize size = this is NotesSlide or NotesMaster ? Presentation.NotesSize : Presentation.SlideSize;
            return (size.WidthPoints, size.HeightPoints);
        }
    }
}
