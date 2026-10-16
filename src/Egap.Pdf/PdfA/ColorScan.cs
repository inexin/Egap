using Egap.Pdf.Content;

namespace Egap.Pdf.PdfA;

/// <summary>
/// Which device colour spaces content paints with (ISO 19005-2 6.2.4.3): the colour operators and colour spaces of pages
/// and form XObjects, images, inline images and shadings, through indexed bases and separation alternates. A resource
/// dictionary's DefaultRGB or DefaultCMYK takes the place of the device space there, so it does not count.
/// </summary>
internal sealed class ColorScan(PdfDocument document)
{
    private const int MaxDepth = 16;

    /// <summary>Whether DeviceRGB paints anything.</summary>
    public bool UsesRgb { get; private set; }

    /// <summary>Whether DeviceCMYK paints anything.</summary>
    public bool UsesCmyk { get; private set; }

    /// <summary>The colour space of the document's PDF/A output intent profile (<c>RGB</c>, <c>CMYK</c>, <c>GRAY</c>), or null.</summary>
    public static string? IntentSpace(PdfDocument document)
    {
        foreach (PdfObject item in document.Catalog.Get<PdfArray>("OutputIntents") ?? [])
        {
            if (item.Resolve() is PdfDictionary intent && intent.GetName("S")?.Value == "GTS_PDFA1" && intent.Get<PdfStream>("DestOutputProfile") is PdfStream profile)
            {
                try
                {
                    return ProfileSpace(profile.GetDecodedBytes());
                }
                catch (PdfException e) when (e is not PdfLimitException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>An ICC profile's data colour space from its header: <c>RGB</c>, <c>CMYK</c>, <c>GRAY</c> or another signature.</summary>
    public static string ProfileSpace(ReadOnlySpan<byte> profile)
    {
        return profile.Length >= 20 ? System.Text.Encoding.ASCII.GetString(profile[16..20]).Trim() : "unknown";
    }

    /// <summary>Scans every page and the given content streams (forms, appearances, patterns, groups).</summary>
    public void Run(IEnumerable<PdfStream> streams)
    {
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            Content(Parse(() => page.GetContent()), page.Resources);
            Group(page.Dictionary, page.Resources);
        }

        foreach (PdfStream stream in streams)
        {
            PdfDictionary? resources = stream.Dictionary.Get<PdfDictionary>("Resources");
            Content(Parse(() => PdfContent.Parse(stream.GetDecodedBytes())), resources);
            Group(stream.Dictionary, resources);
        }
    }

    private static PdfContent? Parse(Func<PdfContent> parse)
    {
        try
        {
            return parse();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }

    private void Group(PdfDictionary owner, PdfDictionary? resources)
    {
        if (owner.Get<PdfDictionary>("Group") is PdfDictionary group && group.Get("CS") is PdfObject space)
        {
            Space(space, resources, 0);
        }
    }

    private void Content(PdfContent? content, PdfDictionary? resources)
    {
        if (content is null)
        {
            return;
        }

        foreach (PdfOperation operation in content)
        {
            switch (operation.Operator)
            {
                case "rg" or "RG":
                    Device("DeviceRGB", resources);
                    break;
                case "k" or "K":
                    Device("DeviceCMYK", resources);
                    break;
                case "cs" or "CS" when operation.GetName(0) is PdfName name:
                    Space(name, resources, 0);
                    break;
                case "sh" when operation.GetName(0) is PdfName name && resources?.Get<PdfDictionary>("Shading")?.Get(name)?.Resolve() is PdfObject shading:
                    Shading(shading, resources);
                    break;
                case "Do" when operation.GetName(0) is PdfName name && resources?.Get<PdfDictionary>("XObject")?.Get(name)?.Resolve() is PdfStream { Dictionary: var x }
                    && x.GetName("Subtype")?.Value == "Image" && x.GetBool("ImageMask") != true && x.Get("ColorSpace") is PdfObject space:
                    Space(space, resources, 0);
                    break;
                case "BI" when operation is PdfInlineImage image && image.Parameters.GetBool("IM") != true && image.Parameters.GetBool("ImageMask") != true
                    && (image.Parameters.Get("CS") ?? image.Parameters.Get("ColorSpace")) is PdfObject space:
                    Space(space, resources, 0);
                    break;
            }
        }

        foreach (PdfObject value in ResourceWalker.Values(resources?.Get<PdfDictionary>("Pattern")))
        {
            if (value is PdfDictionary pattern && pattern.Get("Shading")?.Resolve() is PdfObject shading)
            {
                Shading(shading, resources);
            }
        }
    }

    private void Shading(PdfObject shading, PdfDictionary? resources)
    {
        PdfDictionary? dictionary = shading switch
        {
            PdfStream stream => stream.Dictionary,
            PdfDictionary d => d,
            _ => null,
        };
        if (dictionary?.Get("ColorSpace") is PdfObject space)
        {
            Space(space, resources, 0);
        }
    }

    private void Device(string name, PdfDictionary? resources)
    {
        PdfDictionary? spaces = resources?.Get<PdfDictionary>("ColorSpace");
        if (name == "DeviceRGB" && spaces?.Get("DefaultRGB") is null)
        {
            UsesRgb = true;
        }
        else if (name == "DeviceCMYK" && spaces?.Get("DefaultCMYK") is null)
        {
            UsesCmyk = true;
        }
    }

    private void Space(PdfObject space, PdfDictionary? resources, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (space.Resolve())
        {
            case PdfName { Value: "DeviceRGB" or "RGB" }:
                Device("DeviceRGB", resources);
                break;
            case PdfName { Value: "DeviceCMYK" or "CMYK" }:
                Device("DeviceCMYK", resources);
                break;
            case PdfName { Value: "DeviceGray" or "G" or "Pattern" }:
                break;
            case PdfName name when resources?.Get<PdfDictionary>("ColorSpace")?.Get(name) is PdfObject named:
                Space(named, resources, depth + 1);
                break;
            case PdfArray { Count: > 1 } array when array.Get<PdfName>(0)?.Value is string family:
                switch (family)
                {
                    case "Indexed" or "I":
                        Space(array[1], resources, depth + 1);
                        break;
                    case "Separation" or "DeviceN" when array.Count > 2:
                        Space(array[2], resources, depth + 1);
                        break;
                    case "Pattern":
                        Space(array[1], resources, depth + 1);
                        break;
                }

                break;
        }
    }
}
