namespace Egap.Documents;

/// <summary>
/// A font file a document carries (Phase 6 design Q10 and Q12): the layout engine looks here first for a family's face.
/// </summary>
/// <param name="Family">The family name the document's text uses for it.</param>
/// <param name="Bold">Whether this is the family's bold face.</param>
/// <param name="Italic">Whether this is the family's italic face.</param>
/// <param name="Data">The font file (TrueType or OpenType), not obfuscated.</param>
public sealed record EmbeddedFont(string Family, bool Bold, bool Italic, ReadOnlyMemory<byte> Data);
