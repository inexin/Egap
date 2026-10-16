using System.Buffers.Binary;

namespace Egap.Pdf.PdfA;

/// <summary>The rules Egap checks but cannot fix: implementation limits, DeviceN colorants and JPEG 2000 data.</summary>
internal sealed partial class PdfARules
{
    /// <summary>
    /// Implementation limits (ISO 19005-2 6.1.13): integers within 32 bits, reals within single precision. Values outside
    /// them cannot be changed without changing what they mean.
    /// </summary>
    private void Limits()
    {
        int integers = 0, reals = 0;
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfReference reference in document.Objects)
        {
            Count(document.Objects[reference], 0);
        }

        void Count(PdfObject? value, int depth)
        {
            switch (value)
            {
                case PdfInteger number when number.Value is > int.MaxValue or < int.MinValue:
                    integers++;
                    break;
                case PdfReal real when Math.Abs(real.Value) > 3.403e38:
                    reals++;
                    break;
                case PdfStream stream when depth <= MaxDepth && seen.Add(stream):
                    Count(stream.Dictionary, depth + 1);
                    break;
                case PdfDictionary dictionary when depth <= MaxDepth && seen.Add(dictionary):
                    foreach ((PdfName _, PdfObject item) in dictionary.Entries)
                    {
                        if (item is not PdfReference)
                        {
                            Count(item, depth + 1);
                        }
                    }

                    break;
                case PdfArray array when depth <= MaxDepth && seen.Add(array):
                    foreach (PdfObject item in array)
                    {
                        if (item is not PdfReference)
                        {
                            Count(item, depth + 1);
                        }
                    }

                    break;
            }
        }

        if (integers > 0 || reals > 0)
        {
            Issue("6.1.13", $"the file holds {integers} integers beyond 32 bits and {reals} reals beyond single precision, past PDF/A's limits");
        }
    }

    /// <summary>
    /// DeviceN colour spaces name each spot colorant in their attributes' /Colorants (ISO 19005-2 6.2.4.4); Egap cannot
    /// make up a colorant's appearance, so a missing one is unmet.
    /// </summary>
    private void DeviceN(List<PdfDictionary> resources)
    {
        HashSet<string> process = ["Cyan", "Magenta", "Yellow", "Black", "None", "All", "Red", "Green", "Blue", "Gray"];
        int missing = 0;
        foreach (PdfDictionary resource in resources)
        {
            foreach (PdfObject value in ResourceWalker.Values(resource.Get<PdfDictionary>("ColorSpace")))
            {
                if (value is PdfArray { Count: >= 4 } space && space.Get<PdfName>(0)?.Value == "DeviceN" && space[1].Resolve() is PdfArray names)
                {
                    PdfDictionary? colorants = space.Count > 4 ? space.Get<PdfDictionary>(4)?.Get<PdfDictionary>("Colorants") : null;
                    if (names.Count > 1)
                    {
                        missing += names.Select(n => n.Resolve()).OfType<PdfName>().Count(n => !process.Contains(n.Value) && colorants?.Get(n) is null);
                    }
                }
            }
        }

        if (missing > 0)
        {
            Issue("6.2.4.4", $"DeviceN colour spaces use {missing} spot colorants they do not define in /Colorants");
        }
    }

    /// <summary>
    /// JPEG 2000 images (ISO 19005-2 6.2.8.3): 1, 3 or 4 channels, one bit depth for all, from 1 to 38 bits, read from the
    /// JP2 header box. A codestream without one is judged by its SIZ marker.
    /// </summary>
    private void Jpeg2000()
    {
        int bad = 0;
        foreach (PdfReference reference in document.Objects)
        {
            if (document.Objects[reference] is not PdfStream stream || stream.Dictionary.GetName("Subtype")?.Value != "Image" || !FilterNames(stream.Dictionary).Contains("JPXDecode"))
            {
                continue;
            }

            byte[]? data;
            try
            {
                data = stream.GetEncodedBytes().ToArray();
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                data = null;
            }

            if (data is null || Jp2Channels(data) is not (int channels, bool sameDepth, int depth) || channels is not (1 or 3 or 4) || !sameDepth || depth is < 1 or > 38)
            {
                bad++;
            }
        }

        if (bad > 0)
        {
            Issue("6.2.8.3", $"{bad} JPEG 2000 images have channel counts or bit depths PDF/A does not allow, or cannot be read");
        }
    }

    /// <summary>The channel count and bit depth of a JPEG 2000 file (JP2 ihdr and bpcc boxes) or bare codestream (SIZ marker).</summary>
    private static (int Channels, bool SameDepth, int Depth)? Jp2Channels(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51)
        {
            return Siz(data[4..]);
        }

        // Boxes: length, type, content; the header (jp2h) holds ihdr and, when depths differ, bpcc.
        for (int at = 0; at + 8 <= data.Length;)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]);
            int header = 8;
            if (length == 1 && at + 16 <= data.Length)
            {
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
                header = 16;
            }
            else if (length == 0)
            {
                length = data.Length - at;
            }

            if (length < header || at + length > data.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> content = data.Slice(at + header, (int)length - header);
            if (type == 0x6A703268)
            {
                return Ihdr(content);
            }

            if (type == 0x6A703263)
            {
                return content.Length >= 4 && content[0] == 0xFF && content[1] == 0x4F ? Siz(content[4..]) : null;
            }

            at += (int)length;
        }

        return null;
    }

    private static (int, bool, int)? Ihdr(ReadOnlySpan<byte> header)
    {
        int channels = -1, depth = 0;
        bool same = true;
        for (int at = 0; at + 8 <= header.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header[at..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header[(at + 4)..]);
            if (length < 8 || at + length > header.Length)
            {
                break;
            }

            ReadOnlySpan<byte> box = header.Slice(at + 8, length - 8);
            if (type == 0x69686472 && box.Length >= 14)
            {
                channels = BinaryPrimitives.ReadUInt16BigEndian(box[8..]);
                byte bpc = box[10];
                if (bpc == 0xFF)
                {
                    same = false;
                }
                else
                {
                    depth = (bpc & 0x7F) + 1;
                }
            }
            else if (type == 0x62706363 && box.Length > 0)
            {
                same = box.ToArray().Distinct().Count() == 1;
                depth = (box[0] & 0x7F) + 1;
            }

            at += length;
        }

        return channels < 0 ? null : (channels, same, depth);
    }

    private static (int, bool, int)? Siz(ReadOnlySpan<byte> siz)
    {
        // Lsiz, Rsiz, sizes and offsets (36 bytes after Lsiz), then Csiz and three bytes per component.
        if (siz.Length < 38)
        {
            return null;
        }

        int components = BinaryPrimitives.ReadUInt16BigEndian(siz[36..]);
        if (siz.Length < 38 + (3 * components) || components == 0)
        {
            return null;
        }

        HashSet<int> depths = [];
        for (int c = 0; c < components; c++)
        {
            depths.Add((siz[38 + (3 * c)] & 0x7F) + 1);
        }

        return (components, depths.Count == 1, depths.First());
    }
}
