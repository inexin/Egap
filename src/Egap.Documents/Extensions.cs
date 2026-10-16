namespace Egap.Documents;

/// <summary>
/// What a format's reader found on a node that the model does not name, kept so the same format's writer can put it back
/// (Phase 6 design Q7): for DOCX, unknown elements and attributes as raw XML with their place among the mapped children.
/// Each format keeps its own data under its name ("docx", "html"); writers of other formats leave it alone. An edit that
/// removes a node removes its data with it.
/// </summary>
public sealed class ExtensionData
{
    private readonly Dictionary<string, object> byFormat = new(StringComparer.Ordinal);

    internal ExtensionData()
    {
    }

    /// <summary>The formats that have kept something here.</summary>
    public IReadOnlyCollection<string> Formats => byFormat.Keys;

    /// <summary>Whether nothing is kept.</summary>
    public bool IsEmpty => byFormat.Count == 0;

    /// <summary>Drops what <paramref name="format"/> kept, so its writer writes only what the model says.</summary>
    /// <returns>False when the format had kept nothing.</returns>
    public bool Remove(string format)
    {
        return byFormat.Remove(format);
    }

    /// <summary>Drops everything kept.</summary>
    public void Clear()
    {
        byFormat.Clear();
    }

    /// <summary>The data <paramref name="format"/> kept, of the type its reader uses, or null.</summary>
    internal T? Get<T>(string format)
        where T : class
    {
        return byFormat.GetValueOrDefault(format) as T;
    }

    /// <summary>Keeps <paramref name="data"/> for <paramref name="format"/>; it must not change once kept, as copies share it.</summary>
    internal void Set(string format, object? data)
    {
        if (data is null)
        {
            byFormat.Remove(format);
        }
        else
        {
            byFormat[format] = data;
        }
    }

    internal ExtensionData Copy()
    {
        ExtensionData copy = new();
        foreach (KeyValuePair<string, object> entry in byFormat)
        {
            copy.byFormat[entry.Key] = entry.Value;
        }

        return copy;
    }
}

/// <summary>What a tracked change did (Phase 6 design Q8: changes are marks on the nodes they change).</summary>
public enum RevisionKind
{
    /// <summary>Inserted.</summary>
    Insertion,

    /// <summary>Deleted; the content stays until the change is accepted.</summary>
    Deletion,

    /// <summary>Moved away from here; like a deletion.</summary>
    MoveFrom,

    /// <summary>Moved to here; like an insertion.</summary>
    MoveTo,
}

/// <summary>A tracked insertion, deletion or move on a node: what, who and when.</summary>
/// <param name="Kind">What the change did.</param>
/// <param name="Author">Who made it.</param>
/// <param name="Date">When, if known.</param>
public sealed record Revision(RevisionKind Kind, string Author, DateTimeOffset? Date = null)
{
    /// <summary>Whether the change removes the content (a deletion or the source of a move).</summary>
    public bool Removes => Kind is RevisionKind.Deletion or RevisionKind.MoveFrom;
}

/// <summary>A tracked formatting change: who changed the formatting, when, and what it was before.</summary>
/// <param name="Author">Who made the change.</param>
/// <param name="Date">When, if known.</param>
/// <param name="OldFormat">The direct formatting before the change; a stand-alone copy.</param>
public sealed record FormatRevision(string Author, DateTimeOffset? Date, FormatBase OldFormat);

/// <summary>Image bytes as a file stores them, with their media type, such as image/png or image/x-emf.</summary>
/// <param name="Bytes">The encoded image.</param>
/// <param name="ContentType">The media type.</param>
public sealed record ImageData(ReadOnlyMemory<byte> Bytes, string ContentType)
{
    /// <summary>Image data from a file's bytes, the media type guessed from its first bytes (PNG, JPEG, GIF, BMP, TIFF, WebP, EMF, WMF, SVG).</summary>
    public static ImageData FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ImageData(bytes, GuessContentType(bytes));
    }

    /// <summary>The media type of the image in <paramref name="bytes"/>, by its signature; application/octet-stream when unknown.</summary>
    public static string GuessContentType(ReadOnlySpan<byte> bytes)
    {
        return bytes switch
        {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
            [0xFF, 0xD8, ..] => "image/jpeg",
            [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
            [(byte)'B', (byte)'M', ..] => "image/bmp",
            [(byte)'I', (byte)'I', 42, 0, ..] or [(byte)'M', (byte)'M', 0, 42, ..] => "image/tiff",
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
            [0x01, 0x00, 0x00, 0x00, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, 0x20, 0x45, 0x4D, 0x46, ..] => "image/x-emf",
            [0xD7, 0xCD, 0xC6, 0x9A, ..] or [0x01, 0x00, 0x09, 0x00, ..] or [0x02, 0x00, 0x09, 0x00, ..] => "image/x-wmf",
            _ when LooksLikeSvg(bytes) => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> head = bytes[..Math.Min(bytes.Length, 512)];
        return head.IndexOf("<svg"u8) >= 0;
    }
}
