using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// A PDF stream: a dictionary followed by data. A stream is always an indirect object. Its data stays in the file until it
/// is asked for, and an unchanged stream is copied raw when the file is saved.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Named after the PDF object type (design Q1).")]
public sealed class PdfStream : PdfObject, IContainer
{
    private StreamData data;

    /// <summary>An empty stream with an empty dictionary.</summary>
    public PdfStream()
        : this(new PdfDictionary(), StreamData.Empty)
    {
    }

    /// <summary>A stream with the given dictionary and no data yet.</summary>
    /// <param name="dictionary">The dictionary, which must not be in any other place.</param>
    public PdfStream(PdfDictionary dictionary)
        : this(dictionary, StreamData.Empty)
    {
    }

    internal PdfStream(PdfDictionary dictionary, StreamData data)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Containment.Adopt(this, dictionary);
        Dictionary = dictionary;
        this.data = data;
    }

    object? IContainer.Parent { get; set; }

    /// <summary>The stream's dictionary. Its <c>/Length</c> is set from the data when the stream is written.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The number of encoded bytes.</summary>
    public long EncodedLength => data.Length;

    /// <summary>Where the data comes from, for the writer and the filters.</summary>
    internal StreamData Data => data;

    /// <summary>The data as stored in the file, still encoded with the stream's filters.</summary>
    public byte[] GetEncodedBytes()
    {
        return data.Read();
    }

    /// <summary>
    /// Replaces the data with bytes already encoded with the filters the dictionary names. Use this to copy encoded data;
    /// the dictionary's <c>/Filter</c> and <c>/DecodeParms</c> are not changed.
    /// </summary>
    public void SetEncodedBytes(ReadOnlySpan<byte> bytes)
    {
        data = new BytesData(bytes.ToArray());
        Containment.Changed(this);
    }

    /// <summary>A copy with a copied dictionary and the same data, belonging nowhere.</summary>
    public PdfStream Clone()
    {
        return new PdfStream(Dictionary.Clone(), data);
    }
}

/// <summary>A stream's encoded bytes: in memory, or a range of the source file read on demand.</summary>
internal abstract class StreamData
{
    public static StreamData Empty { get; } = new BytesData([]);

    public abstract long Length { get; }

    public abstract byte[] Read();
}

/// <summary>Encoded bytes held in memory.</summary>
internal sealed class BytesData(byte[] bytes) : StreamData
{
    public override long Length => bytes.Length;

    public override byte[] Read()
    {
        return (byte[])bytes.Clone();
    }

    /// <summary>The bytes without a copy, for the writer.</summary>
    public ReadOnlySpan<byte> Span => bytes;
}
