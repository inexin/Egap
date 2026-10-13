namespace Egap.Pdf;

/// <summary>
/// A vertical font's metrics for one glyph (9.7.4.3), in thousandths of text space, from <c>/W2</c> or <c>/DW2</c>.
/// </summary>
/// <param name="Advance">The vertical advance: negative, since vertical writing goes down.</param>
/// <param name="OriginX">The horizontal part of the vector from the glyph's horizontal origin to its vertical origin.</param>
/// <param name="OriginY">The vertical part of the vector from the glyph's horizontal origin to its vertical origin.</param>
public readonly record struct PdfVerticalMetrics(double Advance, double OriginX, double OriginY);
