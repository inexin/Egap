using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Egap.Pdf.Fonts;

namespace Egap.Pdf.Stamping;

/// <summary>
/// Stamping (Phase 7 design 7.3, Q10 and Q11, Build Plan tasks 12 and 13): each stamp is drawing added over (or under) a
/// page's content without touching it, wrapped in an <c>/Artifact</c> marked-content sequence of type Pagination with the
/// subtype Header, Footer or Watermark, as ISO 32000-2 14.8.2.2 marks page furniture, so tagged files stay valid and text
/// extraction can skip it. The sequence's property list also holds <c>/EgapStamp true</c>, which is how
/// <see cref="Remove"/> finds what Egap added and nothing else. On request the sequence sits in an optional content
/// sequence, so viewers can hide the stamps as a layer.
/// </summary>
internal static partial class Stamper
{
    /// <summary>The key in a stamp's property list that marks it as Egap's.</summary>
    internal const string StampKey = "EgapStamp";

    private const double HelveticaAscent = 0.718, HelveticaDescent = 0.207;

    public static PdfStampResult Apply(PdfDocument document, PdfStamp stamp)
    {
        int part = PdfA.PdfAWriter.ClaimedPart(document);

        // A PDF/A file whose output intent is CMYK or gray may not paint DeviceRGB: text takes the intent's device space. One
        // without an output intent may paint no device colour at all, so it gets the sRGB intent a new PDF/A file gets.
        if (part > 0 && !(document.Catalog.Get<PdfArray>("OutputIntents") ?? []).Any(i => i.Resolve() is PdfDictionary d && d.GetName("S")?.Value == "GTS_PDFA1"))
        {
            PdfA.PdfAWriter.AddOutputIntent(document);
        }

        string? intent = part > 0 ? PdfA.ColorScan.IntentSpace(document) ?? "GRAY" : null;
        if (intent is "RGB")
        {
            intent = null;
        }

        if (part > 0)
        {
            if (stamp.Kind == PdfStampKind.Text && stamp.Font is null)
            {
                throw new InvalidOperationException("A PDF/A document needs embedded fonts: give the stamp a Font.");
            }

            if (part == 1 && stamp.Opacity < 1)
            {
                throw new InvalidOperationException("PDF/A-1 does not allow transparency: give the stamp an opacity of 1.");
            }

            if (stamp.Kind == PdfStampKind.Image && intent is not null)
            {
                throw new InvalidOperationException("The PDF/A document has no RGB output intent, which an RGB image stamp needs.");
            }
        }

        int count = document.Pages.Count;
        HashSet<int>? chosen = stamp.Pages is null ? null : [.. stamp.Pages];
        if (stamp.PageRange is not null)
        {
            HashSet<int> ranged = [.. stamp.PageRange.Resolve(count).Select(i => i + 1)];
            if (chosen is null)
            {
                chosen = ranged;
            }
            else
            {
                chosen.IntersectWith(ranged);
            }
        }
        CultureInfo culture = stamp.Culture ?? CultureInfo.InvariantCulture;
        PdfReference? helvetica = null, state = null, layer = null;
        PdfFormXObject? shared = null;
        string? firstBates = null, lastBates = null;
        int stamped = 0;
        string title = document.Metadata.Title ?? string.Empty;
        string file = document.FileName ?? string.Empty;
        DateTimeOffset now = document.Clock.GetLocalNow();
        for (int index = 0; index < count; index++)
        {
            if (chosen is not null && !chosen.Contains(index + 1))
            {
                continue;
            }

            PdfPage page = document.Pages[index];
            string? bates = stamp.Sequence?.Format(stamp.Sequence.Take());
            firstBates ??= bates;
            lastBates = bates ?? lastBates;

            // The stamp's drawing in its own box, y up from its bottom left.
            Action<PdfContentWriter> draw;
            double width, height;
            if (stamp.Kind == PdfStampKind.Text && stamp.Font is null)
            {
                string text = Fill(stamp.Template!, index, count, document, title, file, now, bates, culture);
                byte[] codes = WinAnsiEncoding.TryEncode(text) ?? WinAnsiEncoding.TryEncode(new string([.. text.Select(c => WinAnsiEncoding.Encode(c) is null ? '?' : c)]))!;
                width = Standard14Font.Get("Helvetica")!.Measure(codes, stamp.FontSize);
                height = (HelveticaAscent + HelveticaDescent) * stamp.FontSize;
                helvetica ??= Helvetica(document);
                Rgba8 c = stamp.Color;
                PdfName fontName = page.AddResource("Font", helvetica);
                draw = w => w.Rgb(c.R / 255.0, c.G / 255.0, c.B / 255.0).BeginText().Font(fontName, stamp.FontSize)
                    .MoveText(0, HelveticaDescent * stamp.FontSize).ShowText(codes).EndText();
            }
            else
            {
                PdfFormXObject form = stamp.Kind switch
                {
                    PdfStampKind.Text => TextForm(document, stamp, intent, Fill(stamp.Template!, index, count, document, title, file, now, bates, culture)),
                    PdfStampKind.Image => shared ??= ImageForm(document, stamp),
                    _ => shared ??= stamp.SourcePage!.ToFormXObject(document),
                };
                double scale = stamp.Kind == PdfStampKind.Text ? 1 : stamp.Scale;
                width = form.Width * scale;
                height = form.Height * scale;
                PdfName formName = page.AddResource("XObject", form.Reference);
                draw = w => w.Transform(scale, 0, 0, scale, 0, 0).Draw(formName);
            }

            if (stamp.Opacity < 1)
            {
                state ??= document.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("ExtGState"), ["ca"] = new PdfReal(stamp.Opacity), ["CA"] = new PdfReal(stamp.Opacity) });
            }

            if (stamp.Layer is string name)
            {
                layer ??= Layer(document, name);
            }

            PdfContentWriter content = new();
            if (layer is not null)
            {
                content.BeginMarkedContent("OC", page.AddResource("Properties", layer));
            }

            PdfDictionary properties = new()
            {
                ["Type"] = PdfName.Get("Pagination"),
                ["Subtype"] = PdfName.Get((stamp.Subtype ?? SubtypeOf(stamp.Anchor)).ToString()),
                [StampKey] = PdfBoolean.Get(true),
            };
            content.BeginMarkedContent("Artifact", properties).Save();
            if (state is not null)
            {
                content.GraphicsState(page.AddResource("ExtGState", state));
            }

            content.Transform(Placement(page, stamp, width, height));
            draw(content);
            content.Restore().EndMarkedContent();
            if (layer is not null)
            {
                content.EndMarkedContent();
            }

            page.AddContent(content.ToArray(), stamp.Under ? PdfCanvasPlacement.Under : PdfCanvasPlacement.Over);
            stamped++;
        }

        return new PdfStampResult(stamped, firstBates, lastBates);
    }

    /// <summary>The matrix from the stamp's box to the page's user space: anchored on the shown page, offset, turned about its centre.</summary>
    internal static PdfMatrix Placement(PdfPage page, PdfStamp stamp, double width, double height)
    {
        (PdfMatrix shown, double pageWidth, double pageHeight) = PdfPage.Displayed(page.CropBox.Normalize(), page.Rotate, page.UserUnit);
        int column = (int)stamp.Anchor % 3, row = (int)stamp.Anchor / 3;
        double x = column switch
        {
            0 => 0,
            1 => (pageWidth - width) / 2,
            _ => pageWidth - width,
        };
        double y = row switch
        {
            0 => pageHeight - height,
            1 => (pageHeight - height) / 2,
            _ => 0,
        };
        double cx = x + stamp.Offset.X + (width / 2), cy = y + stamp.Offset.Y + (height / 2);
        PdfMatrix inShown = PdfMatrix.Translation(-width / 2, -height / 2) * PdfMatrix.Rotation(stamp.Rotation) * PdfMatrix.Translation(cx, cy);
        return shown.TryInvert(out PdfMatrix back) ? inShown * back : inShown;
    }

    private static PdfStampSubtype SubtypeOf(StampAnchor anchor) => ((int)anchor / 3) switch
    {
        0 => PdfStampSubtype.Header,
        1 => PdfStampSubtype.Watermark,
        _ => PdfStampSubtype.Footer,
    };

    /// <summary>The template with its fields filled for one page; fields it does not know stay as they are.</summary>
    internal static string Fill(string template, int index, int count, PdfDocument document, string title, string file, DateTimeOffset now, string? bates, CultureInfo culture)
    {
        return Field().Replace(template, m =>
        {
            string format = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            return m.Groups[1].Value switch
            {
                "page" => (index + 1).ToString(format, culture),
                "pages" => count.ToString(format, culture),
                "label" => document.Pages.GetLabel(index),
                "date" => now.ToString(format.Length > 0 ? format : "d", culture),
                "title" => title,
                "file" => file,
                "bates" when bates is not null => bates,
                _ => m.Value,
            };
        });
    }

    [GeneratedRegex(@"\{(page|pages|label|date|title|file|bates)(?::([^}]*))?\}")]
    private static partial Regex Field();

    private static PdfReference Helvetica(PdfDocument document)
    {
        return document.WrittenResources.ForValue(("StampFont", "Helvetica"), () => document.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type1"),
            ["BaseFont"] = PdfName.Get("Helvetica"),
            ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
        }));
    }

    /// <summary>Text in a caller's typeface, drawn by the canvas so the face is embedded as a subset.</summary>
    private static PdfFormXObject TextForm(PdfDocument document, PdfStamp stamp, string? intent, string text)
    {
        Typeface face = stamp.Font!;
        Font font = new(face, (float)stamp.FontSize);
        float scale = (float)stamp.FontSize / face.UnitsPerEm;
        float ascent = face.Extents.Ascent * scale, descent = -face.Extents.Descent * scale;
        float width = Math.Max(1, font.MeasureText(text).Advance), height = Math.Max(1, ascent + descent);
        return Form(document, width, height, canvas => canvas.DrawText(text, new Vector2(0, ascent), font, new SolidPaint(new RgbaF32(stamp.Color.R / 255f, stamp.Color.G / 255f, stamp.Color.B / 255f, 1))), intent);
    }

    private static PdfFormXObject ImageForm(PdfDocument document, PdfStamp stamp)
    {
        float width = (float)stamp.ImageWidth, height = (float)stamp.ImageHeight;
        return Form(document, width, height, canvas => canvas.DrawImage(stamp.ImageRaster!, new RectF(0, 0, width, height)));
    }

    private static PdfFormXObject Form(PdfDocument document, float width, float height, Action<PdfCanvas> draw, string? intent = null)
    {
        PdfCanvas canvas = PdfCanvas.ForForm(document, width, height);
        using (canvas)
        {
            draw(canvas);
        }

        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height) },
            ["Resources"] = canvas.FormResources.Clone(),
        });
        form.SetDecodedBytes(intent is null ? canvas.FormContent ?? [] : Recolour(canvas.FormContent ?? [], intent));
        return new PdfFormXObject(form, document.Objects.Add(form), width, height, []);
    }

    /// <summary>The content with each RGB colour (<c>rg</c>, <c>RG</c>) in the output intent's space: luma for GRAY, a plain conversion for CMYK.</summary>
    private static byte[] Recolour(byte[] content, string intent)
    {
        PdfContent operations = PdfContent.Parse(content);
        for (int i = 0; i < operations.Count; i++)
        {
            PdfOperation op = operations[i];
            if (op.Operator is not ("rg" or "RG") || op.GetNumber(0) is not double r || op.GetNumber(1) is not double g || op.GetNumber(2) is not double b)
            {
                continue;
            }

            bool stroke = op.Operator == "RG";
            if (intent == "GRAY")
            {
                operations[i] = new PdfOperation(stroke ? "G" : "g", new PdfReal((0.299 * r) + (0.587 * g) + (0.114 * b)));
            }
            else
            {
                double k = 1 - Math.Max(r, Math.Max(g, b)), w = 1 - k;
                operations[i] = new PdfOperation(stroke ? "K" : "k", new PdfReal(w > 0 ? (w - r) / w : 0), new PdfReal(w > 0 ? (w - g) / w : 0),
                    new PdfReal(w > 0 ? (w - b) / w : 0), new PdfReal(k));
            }
        }

        return operations.ToBytes();
    }

    /// <summary>The optional content group of that name, made and listed (shown, in the viewer's layer list) when there is none.</summary>
    private static PdfReference Layer(PdfDocument document, string name)
    {
        PdfDictionary catalog = document.Catalog;
        if (catalog.Get<PdfDictionary>("OCProperties") is not PdfDictionary properties)
        {
            catalog["OCProperties"] = properties = new PdfDictionary { ["OCGs"] = new PdfArray(), ["D"] = new PdfDictionary { ["Order"] = new PdfArray() } };
        }

        if (properties.Get<PdfArray>("OCGs") is not PdfArray groups)
        {
            properties["OCGs"] = groups = [];
        }

        foreach (PdfObject item in groups)
        {
            if (item is PdfReference existing && existing.Resolve() is PdfDictionary group && group.GetText("Name") == name)
            {
                return existing;
            }
        }

        PdfReference made = document.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("OCG"), ["Name"] = PdfString.FromText(name) });
        groups.Add(made);
        if (properties.Get<PdfDictionary>("D") is not PdfDictionary defaults)
        {
            properties["D"] = defaults = [];
        }

        if (defaults.Get<PdfArray>("Order") is not PdfArray order)
        {
            defaults["Order"] = order = [];
        }

        order.Add(made);
        return made;
    }

    /// <summary>Removes every stamp Egap added: each marked sequence with <c>/EgapStamp</c>, with the layer sequence around it.</summary>
    public static int Remove(PdfDocument document)
    {
        int removed = 0;
        foreach (PdfPage page in document.Pages)
        {
            PdfContent content = page.GetContent();
            List<int> drop = [];
            for (int i = 0; i < content.Count; i++)
            {
                if (!IsStamp(content[i]))
                {
                    continue;
                }

                int depth = 0, end = i;
                for (; end < content.Count; end++)
                {
                    depth += content[end].Operator is "BDC" or "BMC" ? 1 : content[end].Operator == "EMC" ? -1 : 0;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                int start = i;
                if (i > 0 && content[i - 1] is { Operator: "BDC" } before && before.Operands.Count > 0 && before.Operands[0] is PdfName { Value: "OC" }
                    && end + 1 < content.Count && content[end + 1].Operator == "EMC")
                {
                    start = i - 1;
                    end++;
                }

                drop.AddRange(Enumerable.Range(start, Math.Min(end, content.Count - 1) - start + 1));
                removed++;
                i = end;
            }

            if (drop.Count > 0)
            {
                foreach (int at in drop.Distinct().OrderDescending())
                {
                    content.RemoveAt(at);
                }

                page.SetContent(content);
            }
        }

        return removed;
    }

    private static bool IsStamp(PdfOperation operation)
    {
        if (operation.Operator != "BDC" || operation.Operands.Count < 2 || operation.Operands[0] is not PdfName { Value: "Artifact" })
        {
            return false;
        }

        return operation.Operands[1].Resolve() is PdfDictionary properties && properties.GetBool(StampKey) == true;
    }
}
