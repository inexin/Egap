namespace Egap.Pdf.Filters;

/// <summary>Runs a stream's <c>/Filter</c> chain with its <c>/DecodeParms</c> (ISO 32000-2, 7.4).</summary>
internal static class FilterChain
{
    /// <summary>The image codecs, which Phase 1 passes through undecoded.</summary>
    public static bool IsImageFilter(PdfName name)
    {
        return name.Value is "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode" or "CCF";
    }

    /// <summary>The filter names in order, references followed; a missing or malformed <c>/Filter</c> gives none.</summary>
    public static List<PdfName> Filters(PdfDictionary dictionary)
    {
        return dictionary.Get("Filter") switch
        {
            PdfName name => [name],
            PdfArray array => [.. Enumerable.Range(0, array.Count).Select(array.Get).OfType<PdfName>()],
            _ => [],
        };
    }

    /// <summary>The parameters for the filter at <paramref name="index"/>, or null.</summary>
    private static PdfDictionary? Parameters(PdfDictionary dictionary, int index)
    {
        return dictionary.Get("DecodeParms") switch
        {
            PdfDictionary parameters when index == 0 => parameters,
            PdfArray array when index < array.Count => array.Get<PdfDictionary>(index),
            _ => null,
        };
    }

    /// <summary>
    /// Decodes <paramref name="data"/> through the filters up to the first image codec. Returns the decoded bytes and that
    /// codec's name, or null when every filter ran.
    /// </summary>
    public static (byte[] Data, PdfName? Remaining) Decode(PdfDictionary dictionary, byte[] data, DecodeContext context)
    {
        List<PdfName> filters = Filters(dictionary);
        for (int i = 0; i < filters.Count; i++)
        {
            PdfName filter = filters[i];
            if (IsImageFilter(filter))
            {
                return (data, filter);
            }

            PdfDictionary? parameters = Parameters(dictionary, i);
            data = filter.Value switch
            {
                "FlateDecode" or "Fl" => Predictor.Apply(FlateFilter.Decode(data, context), parameters, context),
                "LZWDecode" or "LZW" => Predictor.Apply(LzwFilter.Decode(data, EarlyChange(parameters, context), context), parameters, context),
                "ASCIIHexDecode" or "AHx" => AsciiFilters.DecodeHex(data, context),
                "ASCII85Decode" or "A85" => AsciiFilters.Decode85(data, context),
                "RunLengthDecode" or "RL" => AsciiFilters.DecodeRunLength(data, context),
                "Crypt" => Crypt(parameters, data),
                _ => throw new PdfException($"The stream uses the unknown filter /{filter.Value}.", null, context.ObjectNumber),
            };
        }

        return (data, null);
    }

    private static int EarlyChange(PdfDictionary? parameters, DecodeContext context)
    {
        long value = parameters?.GetInt("EarlyChange") ?? 1;
        if (value is 0 or 1)
        {
            return (int)value;
        }

        context.Warn(PdfWarningCode.MalformedFilterParameters, $"LZW EarlyChange {value} is not 0 or 1; 1 was used.");
        return 1;
    }

    private static byte[] Crypt(PdfDictionary? parameters, byte[] data)
    {
        // The Identity crypt filter changes nothing. Named crypt filters come with decryption (Build Plan task 14).
        PdfName name = parameters?.GetName("Name") ?? PdfName.Get("Identity");
        return name.Value == "Identity" ? data : throw new PdfException($"The stream uses the crypt filter /{name.Value}, which needs decryption.");
    }
}
