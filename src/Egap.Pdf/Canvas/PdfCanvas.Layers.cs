using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using DrawingPath = Egami.Drawing.Path;

namespace Egap.Pdf;

/// <summary>
/// Layers, pictures, meshes and patches on the PDF canvas. A layer becomes a transparency group form XObject drawn with the
/// layer's opacity and blend mode, <c>/I</c> for an isolated one and a soft mask group for its mask, the mask's transfer
/// curve sampled into a function. A picture becomes a form XObject written once and drawn wherever it is used; a picture
/// paint a tiling pattern of it. Meshes with colours or values become free-form shadings (type 4) and patches Coons and
/// tensor-product shadings (types 6 and 7). What PDF cannot express falls back to an image: a layer with a filter is
/// recorded and drawn through Egami, and textured meshes go through Egami's own default.
/// </summary>
/// <remarks>
/// Content inside a layer, a mask or a picture is written to a form of its own exactly as at the page's level: device
/// coordinates with transforms grouped lazily. A layer's form is drawn where the layer began, in device coordinates; a
/// picture's form under the transform it is drawn with.
/// </remarks>
public sealed partial class PdfCanvas
{
    private readonly Stack<Frame> frames = new();
    private Dictionary<string, PdfDictionary>? formResources;

    /// <summary>
    /// When a call happens inside a layer that is being recorded, passes it on to the recording instead of writing it;
    /// <see langword="false"/> when nothing is being recorded.
    /// </summary>
    private bool Forward(Action<Canvas> call)
    {
        if (recorder is null)
        {
            return false;
        }

        call(recorder);
        return true;
    }

    /// <summary>Records a layer PDF cannot express, from its start to the matching restore, to be drawn through Egami.</summary>
    private PictureRecorder? recorder;
    private LayerOptions? recordedLayer;
    private Matrix3x2 recordedMatrix;
    private int recordedDepth;

    /// <summary>Adds a resource to what the content being written uses: the page's, or the form's when a layer, mask or picture is open.</summary>
    private PdfName AddResource(PdfName category, PdfObject value)
    {
        if (formResources is null)
        {
            return Page.AddResource(category, value);
        }

        if (!formResources.TryGetValue(category.Value, out PdfDictionary? entries))
        {
            entries = [];
            formResources[category.Value] = entries;
        }

        foreach ((PdfName key, PdfObject existing) in entries.Entries)
        {
            if (existing.Equals(value))
            {
                return key;
            }
        }

        PdfName name = Pages.PageDrawing.FreeName(entries, category);
        entries[name] = value;
        return name;
    }

    /// <summary>Starts writing a form: a new content writer and resources, the outer ones kept on the stack.</summary>
    private void BeginForm(LayerOptions? layer, RectF bounds)
    {
        CloseGroup();
        frames.Push(new Frame(content, formResources, layer, bounds));
        content = new PdfContentWriter();
        formResources = [];
    }

    /// <summary>Ends the form begun last and returns it, the outer writer and resources back in place.</summary>
    private PdfStream EndForm(PdfDictionary? group)
    {
        CloseGroup();
        Frame frame = frames.Pop();
        PdfDictionary resources = [];
        foreach ((string category, PdfDictionary entries) in formResources!)
        {
            resources[category] = entries;
        }

        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { Real(frame.Bounds.Left), Real(frame.Bounds.Top), Real(frame.Bounds.Right), Real(frame.Bounds.Bottom) },
            ["Resources"] = resources,
        });
        if (group is not null)
        {
            form.Dictionary["Group"] = group;
        }

        form.SetDecodedBytes(content.ToArray());
        content = frame.Content;
        formResources = frame.Resources;
        return form;
    }

    /// <inheritdoc/>
    protected override void OnSaveLayer(LayerOptions options)
    {
        if (recorder is not null)
        {
            recordedDepth++;
            recorder.SaveLayer(options);
            return;
        }

        if (options.Filter is not null)
        {
            // Recorded from here to the matching restore, then drawn through Egami's raster canvas.
            CloseGroup();
            recorder = new PictureRecorder(new RectF(0, 0, Width, Height));
            recorder.SetMatrix(Matrix);
            recordedLayer = options;
            recordedMatrix = Matrix;
            recordedDepth = 0;
            return;
        }

        PdfDictionary? mask = options.Mask is SoftMask soft ? MaskGroup(soft) : null;
        BeginForm(options, DeviceClipBounds);
        frames.Peek().Mask = mask;

        // Warned once the layer is begun, so that in strict mode the throw leaves a layer for the matching restore to end.
        if (options.Backdrop is not null)
        {
            Warn(PdfWarningCode.RasterFallback, "PDF cannot filter what lies behind a layer; the layer was drawn without its backdrop filter.");
        }

        if (this.options.Strict && !PdfBlendModes.ContainsKey(options.BlendMode))
        {
            Warn(PdfWarningCode.RasterFallback, $"PDF cannot merge a layer with the blend mode {options.BlendMode}; it was merged normally.");
        }
    }

    /// <inheritdoc/>
    protected override void OnRestoreLayer()
    {
        if (recorder is not null)
        {
            if (recordedDepth-- > 0)
            {
                recorder.Restore();
                return;
            }

            Picture picture = recorder.Finish();
            recorder.Dispose();
            recorder = null;
            LayerOptions layer = recordedLayer!;
            Matrix3x2 start = recordedMatrix;
            Fallback(
                c =>
                {
                    if (Matrix3x2.Invert(start, out Matrix3x2 inverse))
                    {
                        c.Concat(inverse);
                    }

                    c.SaveLayer(layer);
                    picture.Playback(c);
                    c.Restore();
                },
                "a layer filter");
            return;
        }

        Frame frame = frames.Peek();
        LayerOptions options = frame.Layer!;
        PdfDictionary? mask = frame.Mask;
        PdfStream form = EndForm(new PdfDictionary
        {
            ["S"] = PdfName.Get("Transparency"),
            ["CS"] = PdfName.Get("DeviceRGB"),
            ["I"] = PdfBoolean.Get(options.Isolated),
        });
        PdfName name = AddResource("XObject", Page.Document.Objects.Add(form));
        content.Save();
        if (options.Opacity < 1 || options.BlendMode != BlendMode.SourceOver || mask is not null)
        {
            if (!PdfBlendModes.TryGetValue(options.BlendMode, out string? blend))
            {
                Warn(PdfWarningCode.RasterFallback, $"PDF cannot merge a layer with the blend mode {options.BlendMode}; it was merged normally.");
                blend = "Normal";
            }

            PdfDictionary state = new()
            {
                ["Type"] = PdfName.Get("ExtGState"),
                ["CA"] = new PdfReal(Math.Round(options.Opacity, 4)),
                ["ca"] = new PdfReal(Math.Round(options.Opacity, 4)),
                ["BM"] = PdfName.Get(blend),
            };
            if (mask is not null)
            {
                state["SMask"] = mask;
            }

            content.GraphicsState(AddResource("ExtGState", Page.Document.Objects.Add(state)));
        }

        content.Draw(name).Restore();
    }

    /// <summary>
    /// A soft mask dictionary for a layer: the mask drawn into a group of its own on this canvas, in device coordinates, with
    /// its backdrop and transfer curve.
    /// </summary>
    private PdfDictionary MaskGroup(SoftMask mask)
    {
        BeginForm(null, DeviceClipBounds);
        int count = Save();
        try
        {
            mask.Draw(this);
        }
        finally
        {
            RestoreToCount(count);
        }

        bool luminosity = mask.Kind == SoftMaskKind.Luminosity;
        PdfStream form = EndForm(new PdfDictionary
        {
            ["S"] = PdfName.Get("Transparency"),
            ["CS"] = PdfName.Get("DeviceRGB"),
        });
        PdfDictionary dictionary = new()
        {
            ["Type"] = PdfName.Get("Mask"),
            ["S"] = PdfName.Get(luminosity ? "Luminosity" : "Alpha"),
            ["G"] = Page.Document.Objects.Add(form),
        };
        if (luminosity)
        {
            RgbaF32 b = mask.Backdrop;
            dictionary["BC"] = new PdfArray { Real(Math.Clamp(b.R, 0, 1)), Real(Math.Clamp(b.G, 0, 1)), Real(Math.Clamp(b.B, 0, 1)) };
        }

        if (mask.Transfer is Func<float, float> transfer)
        {
            byte[] samples = new byte[256];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (byte)Math.Round(Math.Clamp(transfer(i / 255f), 0, 1) * 255);
            }

            PdfStream function = new(new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(0),
                ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                ["Range"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                ["Size"] = new PdfArray { new PdfInteger(256) },
                ["BitsPerSample"] = new PdfInteger(8),
            });
            function.SetDecodedBytes(samples);
            dictionary["TR"] = Page.Document.Objects.Add(function);
        }

        return dictionary;
    }

    /// <inheritdoc/>
    protected override void OnDrawPicture(Picture picture, Matrix3x2 matrix, Paint? paint, bool cacheAsImage)
    {
        if (Forward(c => c.DrawPicture(picture, matrix, paint, cacheAsImage)))
        {
            return;
        }

        if (cacheAsImage)
        {
            base.OnDrawPicture(picture, matrix, paint, cacheAsImage);
            return;
        }

        if (paint is not null && (paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode)))
        {
            Unsupported(paint, c => c.DrawPicture(picture, matrix, paint));
            return;
        }

        PdfReference form = PictureForm(picture);
        bool grouped = paint is not null && (paint.Opacity < 1 || paint.BlendMode != BlendMode.SourceOver);
        if (grouped)
        {
            // Opacity and blending apply to the picture as one: a transparency group around the shared form.
            PdfContentWriter wrapper = new();
            wrapper.Draw("Fm0");
            PdfStream group = new(new PdfDictionary
            {
                ["Type"] = PdfName.Get("XObject"),
                ["Subtype"] = PdfName.Get("Form"),
                ["BBox"] = new PdfArray { Real(picture.Bounds.Left), Real(picture.Bounds.Top), Real(picture.Bounds.Right), Real(picture.Bounds.Bottom) },
                ["Group"] = new PdfDictionary { ["S"] = PdfName.Get("Transparency"), ["CS"] = PdfName.Get("DeviceRGB") },
                ["Resources"] = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Fm0"] = form } },
            });
            group.SetDecodedBytes(wrapper.ToArray());
            form = Page.Document.Objects.Add(group);
        }

        PdfName name = AddResource("XObject", form);
        Matrix3x2 saved = Matrix;
        GraphicsGroup g = OpenGroupWith(Compose(matrix, saved));
        if (grouped)
        {
            SetAlpha(g, paint!.Opacity, paint.BlendMode);
        }

        content.Draw(name);
    }

    /// <summary>The form XObject of a picture's calls, written the first time the picture is drawn: picture coordinates, y down.</summary>
    private PdfReference PictureForm(Picture picture)
    {
        return Page.Document.WrittenResources.ForInstance(picture, () =>
        {
            RectF bounds = picture.Bounds;
            BeginForm(null, bounds);
            int count = Save();
            try
            {
                SetMatrix(Matrix3x2.Identity);
                picture.Playback(this);
            }
            finally
            {
                RestoreToCount(count);
            }

            return Page.Document.Objects.Add(EndForm(null));
        });
    }

    /// <summary>A tiling pattern of a picture paint's tile, repeated or mirrored on both axes; <see langword="null"/> for clamping.</summary>
    private PdfReference? PicturePattern(PicturePaint paint, PdfMatrix toPage)
    {
        if (paint.TileX is TileMode.Clamp or TileMode.Decal || paint.TileY is TileMode.Clamp or TileMode.Decal)
        {
            return null;
        }

        PdfReference form = PictureForm(paint.Picture);
        RectF tile = paint.Tile;
        bool mirrorX = paint.TileX == TileMode.Mirror, mirrorY = paint.TileY == TileMode.Mirror;
        PdfContentWriter cells = new();
        for (int i = 0; i < (mirrorX ? 2 : 1); i++)
        {
            for (int j = 0; j < (mirrorY ? 2 : 1); j++)
            {
                // The tile's corner at the cell's; a mirrored cell flips about the edge it shares with the first.
                float a = i == 1 ? -1 : 1, d = j == 1 ? -1 : 1;
                float e = i == 1 ? 2 * (tile.Left + tile.Width) : 0, f = j == 1 ? 2 * (tile.Top + tile.Height) : 0;
                cells.Save().Rectangle(tile.Left + (i * tile.Width), tile.Top + (j * tile.Height), tile.Width, tile.Height).Clip().EndPath()
                    .Transform(a, 0, 0, d, e, f).Draw("Fm0").Restore();
            }
        }

        PdfStream pattern = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Pattern"),
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(1),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray { Real(tile.Left), Real(tile.Top), Real(tile.Left + ((mirrorX ? 2 : 1) * tile.Width)), Real(tile.Top + ((mirrorY ? 2 : 1) * tile.Height)) },
            ["XStep"] = Real((mirrorX ? 2 : 1) * tile.Width),
            ["YStep"] = Real((mirrorY ? 2 : 1) * tile.Height),
            ["Matrix"] = MatrixArray(toPage),
            ["Resources"] = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Fm0"] = form } },
        });
        pattern.SetDecodedBytes(cells.ToArray());
        return Page.Document.Objects.Add(pattern);
    }

    /// <inheritdoc/>
    protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        if (recorder is not null)
        {
            recorder.DrawMesh(mesh, paint, colorBlend, valueStops);
            return;
        }

        bool colored = !mesh.Colors.IsEmpty || !mesh.Values.IsEmpty;
        if (!colored || !mesh.TexCoords.IsEmpty || paint is not SolidPaint || paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode))
        {
            Warn(PdfWarningCode.RasterFallback, "PDF cannot shade a textured or uncoloured mesh; it was drawn as an image.");

            base.OnDrawMesh(mesh, paint, colorBlend, valueStops);
            return;
        }

        List<(Vector2 Position, RgbaF32 Color, float Value)> vertices = Triangles(mesh);
        if (vertices.Count == 0)
        {
            return;
        }

        bool values = !mesh.Values.IsEmpty;
        GradientStop[] stops = valueStops.ToArray();
        bool alpha = values ? stops.Any(s => s.Color.A < 1) : vertices.Any(v => v.Color.A < 1);
        float paintAlpha = ((SolidPaint)paint).Color.A * paint.Opacity;
        DrawShaded(
            gray => MeshShading(vertices, values, stops, gray),
            alpha,
            paintAlpha,
            paint.BlendMode,
            DrawingPath.Rect(mesh.Bounds.Inflate(1, 1)));
    }

    /// <inheritdoc/>
    protected override void OnDrawPatch(Patch patch, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        if (recorder is not null)
        {
            recorder.DrawPatch(patch, paint, colorBlend, valueStops);
            return;
        }

        bool colored = !patch.CornerColors.IsEmpty || !patch.CornerValues.IsEmpty;
        if (!colored || !patch.CornerTexCoords.IsEmpty || paint is not SolidPaint || paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode))
        {
            base.OnDrawPatch(patch, paint, colorBlend, valueStops);
            return;
        }

        bool values = !patch.CornerValues.IsEmpty;
        GradientStop[] stops = valueStops.ToArray();
        bool alpha = values ? stops.Any(s => s.Color.A < 1) : patch.CornerColors.ToArray().Any(c => c.A < 1);
        float paintAlpha = ((SolidPaint)paint).Color.A * paint.Opacity;
        DrawShaded(
            gray => PatchShading(patch, values, stops, gray),
            alpha,
            paintAlpha,
            paint.BlendMode,
            DrawingPath.Rect(patch.Bounds.Inflate(1, 1)));
    }

    /// <summary>
    /// Paints a shading made by <paramref name="shading"/> in local coordinates with <c>sh</c>, inside a clip of its
    /// bounds, with a luminosity soft mask of its gray version when it has alpha.
    /// </summary>
    private void DrawShaded(Func<bool, PdfStream> shading, bool alpha, float paintAlpha, BlendMode blend, DrawingPath bounds)
    {
        GraphicsGroup g = OpenGroup();
        PdfReference colors = Page.Document.Objects.Add(shading(false));
        content.Save();
        if (alpha)
        {
            RectF local = Map(DeviceClipBounds, Matrix3x2.Invert(Matrix, out Matrix3x2 inverse) ? inverse : Matrix3x2.Identity);
            PdfStream mask = new(new PdfDictionary
            {
                ["Type"] = PdfName.Get("XObject"),
                ["Subtype"] = PdfName.Get("Form"),
                ["BBox"] = new PdfArray { Real(local.Left), Real(local.Top), Real(local.Right), Real(local.Bottom) },
                ["Group"] = new PdfDictionary { ["S"] = PdfName.Get("Transparency"), ["CS"] = PdfName.Get("DeviceGray") },
                ["Resources"] = new PdfDictionary { ["Shading"] = new PdfDictionary { ["Sh0"] = Page.Document.Objects.Add(shading(true)) } },
            });
            mask.SetDecodedBytes(new PdfContentWriter().Shading("Sh0").ToArray());
            PdfReference state = Page.Document.Objects.Add(new PdfDictionary
            {
                ["Type"] = PdfName.Get("ExtGState"),
                ["CA"] = new PdfReal(Math.Round(paintAlpha, 4)),
                ["ca"] = new PdfReal(Math.Round(paintAlpha, 4)),
                ["BM"] = PdfName.Get(PdfBlendModes[blend]),
                ["SMask"] = new PdfDictionary { ["Type"] = PdfName.Get("Mask"), ["S"] = PdfName.Get("Luminosity"), ["G"] = Page.Document.Objects.Add(mask) },
            });
            content.GraphicsState(AddResource("ExtGState", state));
        }
        else
        {
            SetAlpha(new GraphicsGroup(g.Matrix) { Alpha = g.Alpha, Blend = g.Blend }, paintAlpha, blend);
        }

        WritePath(bounds);
        content.Clip().EndPath().Shading(AddResource("Shading", colors)).Restore();
    }

    /// <summary>A mesh's triangles as a flat list of vertices, three per triangle, following its mode and indices.</summary>
    private static List<(Vector2 Position, RgbaF32 Color, float Value)> Triangles(Mesh mesh)
    {
        ReadOnlySpan<Vector2> positions = mesh.Positions.Span;
        ReadOnlySpan<RgbaF32> colors = mesh.Colors.Span;
        ReadOnlySpan<float> values = mesh.Values.Span;
        ReadOnlySpan<int> indices = mesh.Indices.Span;
        int count = indices.IsEmpty ? positions.Length : indices.Length;
        List<(Vector2, RgbaF32, float)> list = [];
        static (Vector2, RgbaF32, float) Vertex(int k, ReadOnlySpan<Vector2> positions, ReadOnlySpan<RgbaF32> colors, ReadOnlySpan<float> values, ReadOnlySpan<int> indices)
        {
            int i = indices.IsEmpty ? k : indices[k];
            return (positions[i], colors.IsEmpty ? default : colors[i], values.IsEmpty ? 0 : values[i]);
        }

        for (int t = 0; ; t++)
        {
            (int a, int b, int c) = mesh.Mode switch
            {
                MeshMode.Triangles => (3 * t, (3 * t) + 1, (3 * t) + 2),
                MeshMode.Strip => (t, t + 1, t + 2),
                _ => (0, t + 1, t + 2),
            };
            if (c >= count)
            {
                break;
            }

            list.Add(Vertex(a, positions, colors, values, indices));
            list.Add(Vertex(b, positions, colors, values, indices));
            list.Add(Vertex(c, positions, colors, values, indices));
        }

        return list;
    }

    /// <summary>
    /// A free-form triangle shading (type 4): each vertex with its colour, or with its value and a function from the stops,
    /// or with <paramref name="gray"/> its alpha.
    /// </summary>
    private PdfStream MeshShading(List<(Vector2 Position, RgbaF32 Color, float Value)> vertices, bool values, GradientStop[] stops, bool gray)
    {
        RectF box = RectF.FromPoints([.. vertices.Select(v => v.Position)]).Inflate(1, 1);
        List<byte> data = [];
        foreach ((Vector2 position, RgbaF32 color, float value) in vertices)
        {
            data.Add(0);
            Coordinate(data, position.X, box.Left, box.Right);
            Coordinate(data, position.Y, box.Top, box.Bottom);
            if (values)
            {
                Component(data, Math.Clamp(value, 0, 1));
            }
            else if (gray)
            {
                Component(data, color.A);
            }
            else
            {
                Component(data, color.R);
                Component(data, color.G);
                Component(data, color.B);
            }
        }

        return MeshStream(4, data, box, values, stops, gray);
    }

    /// <summary>A Coons (type 6) or tensor-product (type 7) shading of one patch, its points in the order PDF lists them.</summary>
    private PdfStream PatchShading(Patch patch, bool values, GradientStop[] stops, bool gray)
    {
        ReadOnlySpan<Vector2> points = patch.Points.Span;
        RectF box = patch.Bounds.Inflate(1, 1);
        List<byte> data = [0];
        int[] order = patch.Kind == PatchKind.Coons ? [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] : [0, 1, 2, 3, 7, 11, 15, 14, 13, 12, 8, 4, 5, 6, 10, 9];
        foreach (int i in order)
        {
            Coordinate(data, points[i].X, box.Left, box.Right);
            Coordinate(data, points[i].Y, box.Top, box.Bottom);
        }

        for (int corner = 0; corner < 4; corner++)
        {
            if (values)
            {
                Component(data, Math.Clamp(patch.CornerValues.Span[corner], 0, 1));
            }
            else
            {
                RgbaF32 c = patch.CornerColors.Span[corner];
                if (gray)
                {
                    Component(data, c.A);
                }
                else
                {
                    Component(data, c.R);
                    Component(data, c.G);
                    Component(data, c.B);
                }
            }
        }

        return MeshStream(patch.Kind == PatchKind.Coons ? 6 : 7, data, box, values, stops, gray);
    }

    private PdfStream MeshStream(int type, List<byte> data, RectF box, bool values, GradientStop[] stops, bool gray)
    {
        int components = values ? 1 : gray ? 1 : 3;
        PdfArray decode = [Real(box.Left), Real(box.Right), Real(box.Top), Real(box.Bottom)];
        for (int i = 0; i < components; i++)
        {
            decode.Add(new PdfInteger(0));
            decode.Add(new PdfInteger(1));
        }

        PdfStream stream = new(new PdfDictionary
        {
            ["ShadingType"] = new PdfInteger(type),
            ["ColorSpace"] = PdfName.Get(gray ? "DeviceGray" : "DeviceRGB"),
            ["BitsPerCoordinate"] = new PdfInteger(32),
            ["BitsPerComponent"] = new PdfInteger(16),
            ["BitsPerFlag"] = new PdfInteger(8),
            ["Decode"] = decode,
        });
        if (values)
        {
            // The value is the shading's parameter: the stops' function makes it a colour (or its alpha) after interpolation.
            Ramp ramp = Ramp.For(new LinearGradient(Vector2.Zero, Vector2.UnitX, stops));
            stream.Dictionary["Function"] = ramp.Function(Page.Document, gray, gray ? 1 : 3);
        }

        stream.SetDecodedBytes([.. data]);
        return stream;
    }

    private static void Component(List<byte> data, float value)
    {
        ushort sample = (ushort)Math.Round(Math.Clamp(value, 0, 1) * ushort.MaxValue);
        data.Add((byte)(sample >> 8));
        data.Add((byte)sample);
    }

    private static Matrix3x2 Compose(Matrix3x2 first, Matrix3x2 then)
    {
        Matrix3x2 m = first * then;
        return float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32)
            ? m
            : new Matrix3x2(0, 0, 0, 0, 0, 0);
    }

    /// <summary>What was being written when a form began: its writer and resources, and the layer, bounds and mask of the form.</summary>
    private sealed class Frame(PdfContentWriter content, Dictionary<string, PdfDictionary>? resources, LayerOptions? layer, RectF bounds)
    {
        public PdfContentWriter Content { get; } = content;

        public Dictionary<string, PdfDictionary>? Resources { get; } = resources;

        public LayerOptions? Layer { get; } = layer;

        public RectF Bounds { get; } = bounds;

        public PdfDictionary? Mask { get; set; }
    }
}
