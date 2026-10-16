using System.Buffers.Binary;
using System.Text;

namespace Egap.Pdf.PdfA;

/// <summary>
/// The sRGB ICC profile PDF/A output intents carry: a version 2.1 display profile (matrix and tone curves), which every
/// PDF/A part accepts, PDF/A-1 included. Written from the sRGB definition (IEC 61966-2-1): its primaries adapted to the D50
/// connection space with the Bradford transform, and its tone curve as a 1024-entry table.
/// </summary>
internal static class SrgbProfile
{
    /// <summary>The identifier output intents give the profile's condition.</summary>
    public const string Condition = "sRGB IEC61966-2.1";

    private static readonly Lazy<byte[]> Bytes = new(Write);

    /// <summary>The profile's bytes.</summary>
    public static ReadOnlySpan<byte> Data => Bytes.Value;

    private static byte[] Write()
    {
        byte[] curve = Curve();
        List<(string Signature, byte[] Data)> tags =
        [
            ("desc", Description("sRGB IEC61966-2.1")),
            ("cprt", Text("No copyright, use freely")),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
            ("rTRC", curve),
            ("gTRC", curve),
            ("bTRC", curve),
        ];

        // The three tone curves are one table, stored once.
        int tableEnd = 128 + 4 + (12 * tags.Count);
        using MemoryStream body = new();
        byte[] table = new byte[4 + (12 * tags.Count)];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tags.Count);
        int curveAt = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            byte[] data = tags[i].Data;
            int at;
            if (ReferenceEquals(data, curve) && curveAt != 0)
            {
                at = curveAt;
            }
            else
            {
                at = tableEnd + (int)body.Length;
                curveAt = ReferenceEquals(data, curve) ? at : curveAt;
                body.Write(data);
                while (body.Length % 4 != 0)
                {
                    body.WriteByte(0);
                }
            }

            Encoding.ASCII.GetBytes(tags[i].Signature, table.AsSpan(4 + (i * 12)));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + (i * 12)), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12 + (i * 12)), (uint)data.Length);
        }

        byte[] profile = new byte[tableEnd + body.Length];
        Span<byte> header = profile;
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 0x02100000);
        Encoding.ASCII.GetBytes("mntr", header[12..]);
        Encoding.ASCII.GetBytes("RGB ", header[16..]);
        Encoding.ASCII.GetBytes("XYZ ", header[20..]);

        // 2026-01-01, a fixed date so the profile's bytes never change.
        BinaryPrimitives.WriteUInt16BigEndian(header[24..], 2026);
        BinaryPrimitives.WriteUInt16BigEndian(header[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[28..], 1);
        Encoding.ASCII.GetBytes("acsp", header[36..]);
        Fixed(header[68..], 0.9642);
        Fixed(header[72..], 1.0);
        Fixed(header[76..], 0.8249);
        table.CopyTo(header[128..]);
        body.ToArray().CopyTo(header[tableEnd..]);
        return profile;
    }

    /// <summary>A <c>textDescriptionType</c>: the ASCII name, and empty Unicode and ScriptCode names.</summary>
    private static byte[] Description(string name)
    {
        byte[] data = new byte[12 + name.Length + 1 + 8 + 3 + 67];
        Encoding.ASCII.GetBytes("desc", data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)(name.Length + 1));
        Encoding.ASCII.GetBytes(name, data.AsSpan(12));
        return data;
    }

    private static byte[] Text(string text)
    {
        byte[] data = new byte[8 + text.Length + 1];
        Encoding.ASCII.GetBytes("text", data);
        Encoding.ASCII.GetBytes(text, data.AsSpan(8));
        return data;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        byte[] data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ", data);
        Fixed(data.AsSpan(8), x);
        Fixed(data.AsSpan(12), y);
        Fixed(data.AsSpan(16), z);
        return data;
    }

    /// <summary>The sRGB tone curve, encoded value to linear light, sampled at 1024 points.</summary>
    private static byte[] Curve()
    {
        const int Count = 1024;
        byte[] data = new byte[12 + (2 * Count)];
        Encoding.ASCII.GetBytes("curv", data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), Count);
        for (int i = 0; i < Count; i++)
        {
            double v = i / (double)(Count - 1);
            double linear = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12 + (2 * i)), (ushort)Math.Round(linear * 65535));
        }

        return data;
    }

    /// <summary>An <c>s15Fixed16Number</c>.</summary>
    private static void Fixed(Span<byte> target, double value)
    {
        BinaryPrimitives.WriteInt32BigEndian(target, (int)Math.Round(value * 65536));
    }
}
