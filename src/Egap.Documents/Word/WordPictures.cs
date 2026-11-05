using System.Buffers.Binary;

namespace Egap.Documents.Word;

/// <summary>Picture data as Word stores it, made into files the image readers take.</summary>
internal static class WordPictures
{
    /// <summary>
    /// A Windows metafile as RTF and Office Art hold it (no placeable header) with the placeable header the WMF readers want: the bounds from
    /// the metafile's own window origin and extent, scaled so the picture has the size RTF gives it.
    /// </summary>
    public static byte[] WithPlaceableHeader(byte[] wmf, int picw, int pich, int goalw, int goalh)
    {
        if (wmf.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(wmf) == 0x9AC6CDD7)
        {
            return wmf;
        }

        int left = 0;
        int top = 0;
        int right = 0;
        int bottom = 0;
        if (wmf.Length >= 18)
        {
            int at = BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(2)) * 2;
            for (int records = 0; at + 6 <= wmf.Length && records < 10_000; records++)
            {
                long size = BinaryPrimitives.ReadUInt32LittleEndian(wmf.AsSpan(at)) * 2L;
                int function = BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(at + 4));
                if (size < 6 || at + size > wmf.Length || function == 0)
                {
                    break;
                }

                if (size >= 10 && function is 0x020B or 0x020C)
                {
                    short y = BinaryPrimitives.ReadInt16LittleEndian(wmf.AsSpan(at + 6));
                    short x = BinaryPrimitives.ReadInt16LittleEndian(wmf.AsSpan(at + 8));
                    if (function == 0x020B)
                    {
                        left = x;
                        top = y;
                    }
                    else
                    {
                        right = x;
                        bottom = y;
                    }
                }

                at += (int)size;
            }
        }

        int extentX = Math.Abs(right);
        int extentY = Math.Abs(bottom);
        double twipsWide = goalw > 0 ? goalw : picw * 1440.0 / 2540;
        if (extentX == 0 || extentY == 0)
        {
            // No window extent: the bounds are the picture's size in HIMETRIC.
            left = 0;
            top = 0;
            extentX = Math.Clamp(picw > 0 ? picw : 2540, 1, short.MaxValue);
            extentY = Math.Clamp(pich > 0 ? pich : 2540, 1, short.MaxValue);
            twipsWide = picw > 0 ? picw * 1440.0 / 2540 : 1440;
        }

        int inch = twipsWide > 0 ? (int)Math.Clamp(Math.Round(extentX * 1440.0 / twipsWide), 1, ushort.MaxValue) : 1440;
        byte[] result = new byte[22 + wmf.Length];
        Span<byte> header = result;
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(header[6..], (short)left);
        BinaryPrimitives.WriteInt16LittleEndian(header[8..], (short)top);
        BinaryPrimitives.WriteInt16LittleEndian(header[10..], (short)Math.Clamp(left + extentX, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], (short)Math.Clamp(top + extentY, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)inch);
        ushort checksum = 0;
        for (int i = 0; i < 20; i += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header[i..]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], checksum);
        wmf.CopyTo(result, 22);
        _ = goalh;
        return result;
    }

    /// <summary>A device-independent bitmap with the BMP file header in front, so it reads as a BMP file.</summary>
    public static byte[] WithBitmapFileHeader(byte[] dib)
    {
        if (dib.Length < 12)
        {
            return dib;
        }

        int headerSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(dib), (uint)dib.Length);
        long palette;
        if (headerSize == 12)
        {
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10));
            palette = bits <= 8 ? (1L << bits) * 3 : 0;
        }
        else if (dib.Length >= 36)
        {
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
            uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
            palette = (used > 0 ? Math.Min(used, 256u) : bits <= 8 ? 1L << bits : 0) * 4;
            if (compression == 3 && headerSize == 40)
            {
                palette += 12;
            }
        }
        else
        {
            palette = 0;
        }

        byte[] result = new byte[14 + dib.Length];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)Math.Min(14 + headerSize + palette, result.Length));
        dib.CopyTo(result, 14);
        return result;
    }
}
