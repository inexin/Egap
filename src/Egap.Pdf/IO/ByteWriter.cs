using System.Buffers;
using System.Globalization;

namespace Egap.Pdf.IO;

/// <summary>A growable byte buffer with helpers for writing PDF syntax.</summary>
internal sealed class ByteWriter(int capacity = 256)
{
    private readonly ArrayBufferWriter<byte> buffer = new(capacity);

    public int Length => buffer.WrittenCount;

    public ReadOnlySpan<byte> WrittenSpan => buffer.WrittenSpan;

    public void Write(byte b)
    {
        buffer.GetSpan(1)[0] = b;
        buffer.Advance(1);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        buffer.Write(bytes);
    }

    /// <summary>Writes ASCII text.</summary>
    public void WriteAscii(string text)
    {
        Span<byte> span = buffer.GetSpan(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            span[i] = (byte)text[i];
        }

        buffer.Advance(text.Length);
    }

    public void WriteInteger(long value)
    {
        Span<byte> span = buffer.GetSpan(20);
        value.TryFormat(span, out int written, default, CultureInfo.InvariantCulture);
        buffer.Advance(written);
    }

    public byte[] ToArray()
    {
        return buffer.WrittenSpan.ToArray();
    }
}
