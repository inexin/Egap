using System.Runtime.CompilerServices;

using Egap.Pdf.Fonts;
using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// What every render of one document's pages shares, held by the document so callers cannot forget to share it (design
/// Q3): colour spaces now; fonts and decoded images join it in later tasks.
/// </summary>
internal sealed class RenderCache(PdfDocument document)
{
    private ColorCache? withIntent;
    private ColorCache? withoutIntent;
    private readonly ConditionalWeakTable<Egami.Colors.ColorProfile, ColorCache[]> byDefaultCmyk = [];

    private readonly ConditionalWeakTable<PdfFont, SubstituteFont?> withSystem = [];
    private readonly ConditionalWeakTable<PdfFont, SubstituteFont?> bundledOnly = [];

    /// <summary>
    /// The colour spaces, read with the document's output intent or without it (design Q7), DeviceCMYK without a CMYK intent
    /// through <paramref name="defaultCmyk"/> (FOGRA39 when null).
    /// </summary>
    public ColorCache Colors(bool outputIntent, Egami.Colors.ColorProfile? defaultCmyk = null)
    {
        if (defaultCmyk is not null)
        {
            ColorCache[] pair = byDefaultCmyk.GetValue(defaultCmyk, p => [new(ColorContext.ForDocument(document, true, p)), new(ColorContext.ForDocument(document, false, p))]);
            return pair[outputIntent ? 0 : 1];
        }

        return outputIntent
            ? LazyInitializer.EnsureInitialized(ref withIntent, () => new ColorCache(ColorContext.ForDocument(document, useOutputIntent: true)))
            : LazyInitializer.EnsureInitialized(ref withoutIntent, () => new ColorCache(ColorContext.ForDocument(document, useOutputIntent: false)));
    }

    public ImageCache Images { get; } = new();

    private readonly ConditionalWeakTable<PdfObject, ShadingPaint> shadings = [];
    private readonly ConditionalWeakTable<InkMap, ConditionalWeakTable<PdfObject, ShadingPaint>> inkShadings = [];
    private readonly ConditionalWeakTable<Egami.Colors.InkSet, InkMap> inkMaps = [];

    /// <summary>
    /// A shading made ready once per document: its function sampled into stops (design Q10), for plates once per ink set,
    /// with ink stops and vertex inks.
    /// </summary>
    public ShadingPaint Shading(PdfObject shading, InkMap? inks, TransferTable? transfer, Func<ShadingPaint> read)
    {
        ConditionalWeakTable<PdfObject, ShadingPaint> table = inks is not null ? inkShadings.GetValue(inks, _ => [])
            : transfer is not null ? transferShadings.GetValue(transfer, _ => []) : shadings;
        return table.GetValue(shading, _ => read());
    }

    private readonly ConditionalWeakTable<TransferTable, ConditionalWeakTable<PdfObject, ShadingPaint>> transferShadings = [];

    /// <summary>How colour spaces print on plates of these inks, made once per ink set and transfer function.</summary>
    public InkMap Inks(Egami.Colors.InkSet set, TransferTable? transfer = null)
    {
        return transfer is null
            ? inkMaps.GetValue(set, s => new InkMap(s))
            : transferInkMaps.GetValue(transfer, _ => []).GetValue(set, s => new InkMap(s, transfer));
    }

    /// <summary>A transfer function sampled once per document; null when it changes nothing or cannot be read.</summary>
    public TransferTable? Transfer(PdfObject value, out string? problem)
    {
        problem = null;
        Holder holder = transfers.GetValue(value, v =>
        {
            try
            {
                return new Holder(TransferTable.Read(v), null);
            }
            catch (Exception e) when (e is InvalidDataException or PdfException and not PdfLimitException)
            {
                return new Holder(null, e.Message);
            }
        });
        problem = holder.Problem;
        return holder.Table;
    }

    private readonly ConditionalWeakTable<PdfObject, Holder> transfers = [];
    private readonly ConditionalWeakTable<TransferTable, ConditionalWeakTable<Egami.Colors.InkSet, InkMap>> transferInkMaps = [];

    private sealed record Holder(TransferTable? Table, string? Problem);

    /// <summary>The substitute for a font with no usable program, chosen once per font and setting (design Q5).</summary>
    public SubstituteFont? Substitute(PdfFont font, bool systemFonts)
    {
        return (systemFonts ? withSystem : bundledOnly).GetValue(font, f => SubstituteFonts.For(f, systemFonts));
    }
}
