using System.Globalization;
using System.Text;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>What the HTML reader keeps of an element for the HTML writer (task 26): its tag, id, class and inline style.</summary>
internal sealed record HtmlExtension(string Tag, string? Id, string? ClassName, string? Style)
{
    public const string Format = "html";
}

/// <summary>
/// Maps a parsed HTML document onto the model (Phase 6 Build Plan task 25, design Q15): the cascade of the user agent's,
/// user and page style sheets through Egami.Css, then block boxes as paragraphs (margins as spacing and indents, padding and
/// borders as paragraph borders, backgrounds as shading), inline boxes as runs, lists, tables with spans, pictures, links
/// and bookmarks; @page as the page setup.
/// </summary>
internal sealed partial class HtmlReader
{
    private static readonly Lazy<CssStyleSheet> UserAgentSheet = new(() =>
    {
        using Stream stream = typeof(HtmlReader).Assembly.GetManifestResourceStream("Egap.Documents.Html.html.css")
            ?? throw new InvalidOperationException("The HTML style sheet is missing from the assembly.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return CssStyleSheet.Parse(reader.ReadToEnd());
    });

    // The rendering section's quirks mode rules (HTML Standard 15.3.3, 15.3.8, 15.4.2).
    private static readonly Lazy<CssStyleSheet> QuirksSheet = new(() => CssStyleSheet.Parse(
        "@namespace \"http://www.w3.org/1999/xhtml\";" +
        "form { margin-bottom: 1em; }" +
        "li { list-style-position: inside; }" +
        "table { font-weight: initial; font-style: initial; font-variant: initial; font-size: initial; line-height: initial; white-space: initial; text-align: initial; }" +
        "img[align=left i] { margin-right: 3px; } img[align=right i] { margin-left: 3px; }"));

    private readonly Document document = new();
    private readonly HtmlLoadOptions options;
    private readonly CssCascade cascade;
    private readonly Dictionary<HtmlElement, HtmlStyle> styles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConversionWarningKind, (string Message, int Count)> warnings = [];
    private readonly Dictionary<HtmlStyle, RunFormat> runFormats = new(ReferenceEqualityComparer.Instance);
    private double rootSize = HtmlStyle.MediumSize;

    private HtmlReader(HtmlLoadOptions options, CssCascade cascade)
    {
        this.options = options;
        this.cascade = cascade;
    }

    public static Document Read(HtmlDocument dom, HtmlLoadOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        List<(CssStyleSheet Sheet, CssOrigin Origin)> sheets = [(UserAgentSheet.Value, CssOrigin.UserAgent)];
        if (dom.QuirksMode == HtmlQuirksMode.Quirks)
        {
            sheets.Add((QuirksSheet.Value, CssOrigin.UserAgent));
        }

        foreach (string user in options.UserStyleSheets)
        {
            sheets.Add((CssStyleSheet.Parse(user), CssOrigin.User));
        }

        HtmlReader? reader = null;
        List<string> notLoaded = [];
        foreach (HtmlElement element in dom.Descendants().OfType<HtmlElement>())
        {
            if (element.IsHtml("style") && MediaApplies(element.GetAttribute("media", null), options.Media))
            {
                sheets.Add((CssStyleSheet.Parse(string.Concat(element.Children.OfType<HtmlText>().Select(t => t.Data))), CssOrigin.Author));
            }
            else if (element.IsHtml("link") && element.GetAttribute("rel", null) is { } rel
                && rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase)
                && MediaApplies(element.GetAttribute("media", null), options.Media) && element.GetAttribute("href", null) is { } href)
            {
                if (Fetch(options, href) is { } bytes)
                {
                    sheets.Add((CssStyleSheet.Parse(Encoding.UTF8.GetString(bytes)), CssOrigin.Author));
                }
                else
                {
                    notLoaded.Add(href);
                }
            }
        }

        CssCascade cascade = new(sheets, new CssCascadeOptions
        {
            Media = new CssMediaContext { MediaType = options.Media, Width = 816, Height = 1056 },
        });
        reader = new HtmlReader(options, cascade);
        foreach (string href in notLoaded)
        {
            reader.Warn(ConversionWarningKind.ResourceNotLoaded, "Style sheets and pictures the resolver did not give");
        }

        reader.Build(dom);
        warnings = [.. reader.warnings.Select(w => new ConversionWarning(w.Key, w.Value.Message, w.Value.Count))];
        return reader.document;
    }

    private static bool MediaApplies(string? media, string target)
    {
        if (string.IsNullOrWhiteSpace(media))
        {
            return true;
        }

        try
        {
            return CssMediaQueryList.Parse(media).Matches(new CssMediaContext { MediaType = target, Width = 816, Height = 1056 });
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The bytes a URL names: a data: URL decoded, anything else through the caller's resolver only.</summary>
    private static byte[]? Fetch(HtmlLoadOptions options, string url)
    {
        url = url.Trim();
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUrl(url, out _);
        }

        if (options.ResourceResolver is not { } resolver)
        {
            return null;
        }

        Uri? absolute = options.BaseUri is { } baseUri ? (Uri.TryCreate(baseUri, url, out Uri? combined) ? combined : null)
            : Uri.TryCreate(url, UriKind.Absolute, out Uri? direct) ? direct : null;
        return absolute is null ? null : resolver(absolute);
    }

    internal static byte[]? DecodeDataUrl(string url, out string contentType)
    {
        contentType = "text/plain";
        int comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        string header = url[5..comma];
        string data = url[(comma + 1)..];
        bool base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        string type = base64 ? header[..^7] : header;
        if (type.Length > 0)
        {
            contentType = type.Split(';')[0].Trim().ToLowerInvariant();
        }

        try
        {
            return base64 ? Convert.FromBase64String(Uri.UnescapeDataString(data).Replace(" ", "", StringComparison.Ordinal)) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void Warn(ConversionWarningKind kind, string message)
    {
        warnings[kind] = warnings.TryGetValue(kind, out (string Message, int Count) existing) ? (existing.Message, existing.Count + 1) : (message, 1);
    }

    // ---- Styles -------------------------------------------------------------------------------------------------------

    private HtmlStyle StyleOf(HtmlElement element, HtmlStyle? parent)
    {
        if (styles.TryGetValue(element, out HtmlStyle? style))
        {
            return style;
        }

        IReadOnlyList<CssDeclaration>? inline = element.GetAttribute("style", null) is { } css ? CssDeclaration.ParseList(css) : null;
        CssCascadedStyle cascaded = cascade.Cascade(element, PresentationHints(element), inline);
        CssComputedStyle computed = cascade.Compute(cascaded, parent?.Computed);
        style = new HtmlStyle(computed, parent, rootSize, cascaded);
        if (parent is null)
        {
            rootSize = style.FontSize;
        }

        styles[element] = style;
        return style;
    }

    /// <summary>HTML's presentational attributes as CSS (the standard's rendering section): bgcolor, align, width and the like.</summary>
    private static List<CssDeclaration>? PresentationHints(HtmlElement element)
    {
        // A cell takes hints from its table's attributes, so it is looked at even without attributes of its own.
        if (element.NamespaceUri != HtmlElement.HtmlNamespace || (element.AttributeList.Count == 0 && element.LocalName is not ("td" or "th")))
        {
            return null;
        }

        StringBuilder css = new();
        string? Attribute(string name) => element.GetAttribute(name, null);
        if (Attribute("bgcolor") is { } bg)
        {
            css.Append("background-color:").Append(LegacyColor(bg)).Append(';');
        }

        if (Attribute("align") is { } align && !element.IsHtml("img") && !element.IsHtml("table"))
        {
            string value = align.ToLowerInvariant() switch
            {
                "center" or "middle" => "center",
                "right" => "right",
                "justify" => "justify",
                _ => "left",
            };
            css.Append("text-align:").Append(value).Append(';');
        }

        if (element.IsHtml("table") && Attribute("align") is { } tableAlign)
        {
            css.Append(tableAlign.ToLowerInvariant() switch
            {
                "center" => "margin-left:auto;margin-right:auto;",
                "right" => "margin-left:auto;margin-right:0;",
                _ => "",
            });
        }

        if (element.IsHtml("img") && Attribute("align") is { } imageAlign && imageAlign.ToLowerInvariant() is "left" or "right")
        {
            css.Append("float:").Append(imageAlign.ToLowerInvariant()).Append(';');
        }

        if (Attribute("valign") is { } valign)
        {
            css.Append("vertical-align:").Append(valign.ToLowerInvariant() is "center" ? "middle" : valign).Append(';');
        }

        if (element.LocalName is "table" or "td" or "th" or "img" or "col" or "hr" or "iframe" or "embed" or "object" or "video")
        {
            if (Attribute("width") is { } width && LegacyLength(width) is { } w)
            {
                css.Append("width:").Append(w).Append(';');
            }

            if (Attribute("height") is { } height && LegacyLength(height) is { } h && element.LocalName is not "hr")
            {
                css.Append("height:").Append(h).Append(';');
            }
        }

        if (element.IsHtml("font"))
        {
            if (Attribute("color") is { } color)
            {
                css.Append("color:").Append(LegacyColor(color)).Append(';');
            }

            if (Attribute("face") is { } face)
            {
                css.Append("font-family:").Append(face).Append(';');
            }

            if (Attribute("size") is { } size && LegacyFontSize(size) is { } keyword)
            {
                css.Append("font-size:").Append(keyword).Append(';');
            }
        }

        if (element.IsHtml("table"))
        {
            if (Attribute("border") is { } border)
            {
                int width = int.TryParse(border, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 1;
                if (width > 0)
                {
                    css.Append("border:").Append(width).Append("px outset gray;");
                }
            }

            if (Attribute("cellspacing") is { } spacing && LegacyLength(spacing) is { } s)
            {
                css.Append("border-spacing:").Append(s).Append(';');
            }

            if (Attribute("background") is { } _)
            {
                // Background images are not read.
            }
        }

        if (element.LocalName is "td" or "th" && element.Ancestors().OfType<HtmlElement>().FirstOrDefault(e => e.IsHtml("table")) is { } table)
        {
            if (table.GetAttribute("border", null) is { } border && (!int.TryParse(border, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) || width > 0))
            {
                css.Append("border:1px inset gray;");
            }

            if (table.GetAttribute("cellpadding", null) is { } padding && LegacyLength(padding) is { } p)
            {
                css.Append("padding:").Append(p).Append(';');
            }

            if (Attribute("nowrap") is not null)
            {
                css.Append("white-space:nowrap;");
            }
        }

        if (element.IsHtml("hr"))
        {
            if (Attribute("noshade") is not null)
            {
                css.Append("border-style:solid;");
            }

            if (Attribute("size") is { } size && LegacyLength(size) is { } height)
            {
                css.Append("height:").Append(height).Append(';');
            }

            if (Attribute("color") is { } color)
            {
                css.Append("border-color:").Append(LegacyColor(color)).Append(';');
            }
        }

        if (element.LocalName is "ol" or "ul" or "li" && Attribute("type") is { } type)
        {
            string? style = type switch
            {
                "1" => "decimal",
                "a" => "lower-alpha",
                "A" => "upper-alpha",
                "i" => "lower-roman",
                "I" => "upper-roman",
                _ => type.ToLowerInvariant() is "disc" or "circle" or "square" ? type.ToLowerInvariant() : null,
            };
            if (style is not null)
            {
                css.Append("list-style-type:").Append(style).Append(';');
            }
        }

        if (element.IsHtml("body") && Attribute("text") is { } text)
        {
            css.Append("color:").Append(LegacyColor(text)).Append(';');
        }

        return css.Length == 0 ? null : [.. CssDeclaration.ParseList(css.ToString())];
    }

    private static string LegacyColor(string value)
    {
        value = value.Trim();
        return value.Length is 3 or 6 && value.All(Uri.IsHexDigit) ? "#" + value : value;
    }

    private static string? LegacyLength(string value)
    {
        value = value.Trim();
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        {
            return percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        int end = 0;
        while (end < value.Length && (char.IsAsciiDigit(value[end]) || value[end] == '.'))
        {
            end++;
        }

        return end > 0 && double.TryParse(value[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels)
            ? pixels.ToString(CultureInfo.InvariantCulture) + "px" : null;
    }

    private static string? LegacyFontSize(string value)
    {
        value = value.Trim();
        int sign = value.StartsWith('+') ? 1 : value.StartsWith('-') ? -1 : 0;
        if (!int.TryParse(sign == 0 ? value : value[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return null;
        }

        int size = Math.Clamp(sign == 0 ? number : 3 + (sign * number), 1, 7);
        return size switch
        {
            1 => "x-small",
            2 => "small",
            3 => "medium",
            4 => "large",
            5 => "x-large",
            6 => "xx-large",
            _ => "xxx-large",
        };
    }
}
