using System.Globalization;
using System.Text;
using System.Text.Json;

using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>
/// Layout as DoclingDocument JSON (design Q14, Build Plan task 18), the format Docling writes, schema version
/// <see cref="SchemaVersion"/>: texts, tables, pictures and list groups in flat arrays, linked into reading order from the
/// body by JSON pointers; roles as Docling's labels; bounds as provenance with the page number and a box from the page's
/// bottom left, as PDF measures; page headers and footers in the furniture layer; and Egap's role and confidence in each
/// item's <c>meta</c>. A DoclingDocument from Docling reads back into page layouts.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("report.pdf");
/// File.WriteAllText("report.json", DoclingJson.Write(PdfLayoutAnalyzer.Analyze(pdf), new DoclingJsonOptions { Name = "report" }));
/// IReadOnlyList&lt;PdfPageLayout&gt; back = DoclingJson.Read(File.ReadAllText("report.json"));
/// </code>
/// </example>
public static class DoclingJson
{
    /// <summary>The DoclingDocument schema version written, and the newest read.</summary>
    public const string SchemaVersion = "1.10.0";

    /// <summary>The pages' layout as DoclingDocument JSON.</summary>
    public static string Write(IReadOnlyList<PdfPageLayout> pages, DoclingJsonOptions? options = null)
    {
        using MemoryStream output = new();
        Write(pages, output, options);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// Writes the pages' layout to a file, pictures with <see cref="DoclingImageMode.Files"/> into a folder beside it
    /// (<c>&lt;name&gt;.images</c> unless <see cref="DoclingJsonOptions.ImagesFolder"/> says).
    /// </summary>
    public static void Write(IReadOnlyList<PdfPageLayout> pages, string path, DoclingJsonOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= DoclingJsonOptions.Default;
        if (options.Images == DoclingImageMode.Files && options.ImagesFolder is null)
        {
            options = options with { ImagesFolder = System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(path), ".images") };
        }

        using FileStream output = File.Create(path);
        Write(pages, output, options);
    }

    /// <summary>Writes the pages' layout to <paramref name="output"/> as UTF-8 DoclingDocument JSON.</summary>
    /// <exception cref="InvalidOperationException">Pictures go to files but no <see cref="DoclingJsonOptions.ImagesFolder"/> is given.</exception>
    public static void Write(IReadOnlyList<PdfPageLayout> pages, Stream output, DoclingJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(output);
        options ??= DoclingJsonOptions.Default;
        if (options.Images == DoclingImageMode.Files && options.ImagesFolder is null && options.ImageResolver is null)
        {
            throw new InvalidOperationException("Pictures written as files need an ImagesFolder (or write to a path, or give an ImageResolver).");
        }

        DoclingWriter writer = new(pages, options);
        using Utf8JsonWriter json = new(output, new JsonWriterOptions { Indented = options.Indented });
        writer.Write(json);
    }

    /// <summary>
    /// Reads DoclingDocument JSON (from Docling or from <see cref="Write(IReadOnlyList{PdfPageLayout}, DoclingJsonOptions?)"/>) into page
    /// layouts: texts become blocks with their roles, tables tables with their cells, pictures figures, in reading order.
    /// Blocks have one line per text, its words spread over the box, since the JSON keeps no line breaks or fonts.
    /// </summary>
    /// <exception cref="JsonException">The text is not JSON, or not a DoclingDocument.</exception>
    public static IReadOnlyList<PdfPageLayout> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        return DoclingReader.Read(document.RootElement);
    }

    /// <summary>
    /// Reads DoclingDocument JSON into a flow document: texts as paragraphs in the styles of their roles, list items in lists,
    /// tables with their cells and spans, pictures with the images the JSON carries, page headers and footers in each
    /// section's header and footer, a section per page size.
    /// </summary>
    public static Egap.Documents.Document ToDocument(string json)
    {
        return LayoutDocument.Build(Read(json));
    }

    /// <summary>Reads DoclingDocument JSON from a UTF-8 stream; see <see cref="Read(string)"/>.</summary>
    public static IReadOnlyList<PdfPageLayout> Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        return DoclingReader.Read(document.RootElement);
    }
}

/// <summary>How <see cref="DoclingJson"/> writes.</summary>
public sealed record DoclingJsonOptions
{
    /// <summary>The defaults: name "document", no origin, compact JSON.</summary>
    public static DoclingJsonOptions Default { get; } = new();

    /// <summary>The document's name, Docling's <c>name</c>.</summary>
    public string Name { get; init; } = "document";

    /// <summary>The source file's name for Docling's <c>origin</c>; with <see cref="Source"/>, the origin is written.</summary>
    public string? FileName { get; init; }

    /// <summary>The source file's bytes, hashed for the origin's <c>binary_hash</c> as Docling does; null to leave the origin out.</summary>
    public ReadOnlyMemory<byte>? Source { get; init; }

    /// <summary>Whether the JSON is indented for reading.</summary>
    public bool Indented { get; init; }

    /// <summary>How pictures carry their image (Phase 6 Q20): as data URIs by default, as files, or not at all.</summary>
    public DoclingImageMode Images { get; init; } = DoclingImageMode.DataUris;

    /// <summary>The folder pictures are written to with <see cref="DoclingImageMode.Files"/>; their URIs name it by its last part.</summary>
    public string? ImagesFolder { get; init; }

    /// <summary>
    /// Called for each picture's image unless <see cref="Images"/> is <see cref="DoclingImageMode.Omit"/>: the caller stores it
    /// and returns the URI to write, or null to leave the picture without one.
    /// </summary>
    public Func<DoclingImage, string?>? ImageResolver { get; init; }

    /// <summary>The resolution, in pixels per inch, at which figures drawn from the page become images. 200 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 10 and 1200.</exception>
    public double FigureResolution
    {
        get;
        init => field = value is >= 10 and <= 1200 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Figures are drawn at 10 to 1200 dpi.");
    } = 200;

    /// <summary>Figures smaller than this on both sides, in points, get no image (bullets, rules, specks). 6 by default.</summary>
    public double MinImageSize { get; init; } = 6;
}

/// <summary>How <see cref="DoclingJson"/> writes pictures' images.</summary>
public enum DoclingImageMode
{
    /// <summary>Each image inside the JSON as a data URI, so the file stands alone.</summary>
    DataUris = 0,

    /// <summary>Each image a file in <see cref="DoclingJsonOptions.ImagesFolder"/>, named in reading order, the same image written once.</summary>
    Files = 1,

    /// <summary>Pictures keep their place and box but no image.</summary>
    Omit = 2,
}

/// <summary>A picture's image for <see cref="DoclingJsonOptions.ImageResolver"/>.</summary>
/// <param name="Bytes">The image file: a JPEG as the PDF stores it, or a PNG of the figure drawn from the page.</param>
/// <param name="ContentType">Its media type.</param>
/// <param name="SuggestedName">A file name in reading order, such as <c>image-003.png</c>.</param>
/// <param name="Figure">The figure it shows.</param>
public sealed record DoclingImage(ReadOnlyMemory<byte> Bytes, string ContentType, string SuggestedName, LayoutFigure Figure);

/// <summary>Writes the JSON: items collected per array first, so every pointer is known before anything is written.</summary>
internal sealed class DoclingWriter(IReadOnlyList<PdfPageLayout> pages, DoclingJsonOptions options)
{
    private readonly List<Action<Utf8JsonWriter, string>> texts = [];
    private readonly List<Action<Utf8JsonWriter, string>> tables = [];
    private readonly List<Action<Utf8JsonWriter, string>> pictures = [];
    private readonly List<(string Label, string Name, List<string> Children)> groups = [];
    private readonly List<string> body = [];
    private readonly List<string> furniture = [];

    public void Write(Utf8JsonWriter json)
    {
        Collect();
        json.WriteStartObject();
        json.WriteString("schema_name", "DoclingDocument");
        json.WriteString("version", DoclingJson.SchemaVersion);
        json.WriteString("name", options.Name);
        if (options.Source is ReadOnlyMemory<byte> source)
        {
            json.WriteStartObject("origin");
            json.WriteString("mimetype", "application/pdf");
            json.WriteNumber("binary_hash", Hash(source.Span));
            json.WriteString("filename", options.FileName ?? options.Name + ".pdf");
            json.WriteEndObject();
        }

        Root(json, "furniture", "furniture", furniture);
        Root(json, "body", "body", body);
        json.WriteStartArray("groups");
        for (int i = 0; i < groups.Count; i++)
        {
            (string label, string name, List<string> children) = groups[i];
            json.WriteStartObject();
            json.WriteString("self_ref", "#/groups/" + i.ToString(CultureInfo.InvariantCulture));
            Ref(json, "parent", "#/body");
            Refs(json, children);
            json.WriteString("content_layer", "body");
            json.WriteString("name", name);
            json.WriteString("label", label);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        Items(json, "texts", texts);
        Items(json, "pictures", pictures);
        Items(json, "tables", tables);
        json.WriteStartArray("key_value_items");
        json.WriteEndArray();
        json.WriteStartArray("form_items");
        json.WriteEndArray();
        json.WriteStartObject("pages");
        foreach (PdfPageLayout page in pages)
        {
            string number = (page.PageIndex + 1).ToString(CultureInfo.InvariantCulture);
            json.WriteStartObject(number);
            json.WriteStartObject("size");
            json.WriteNumber("width", page.Width);
            json.WriteNumber("height", page.Height);
            json.WriteEndObject();
            json.WriteNumber("page_no", page.PageIndex + 1);
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>Every page's elements into the arrays, in reading order: furniture to its layer, list items into list groups.</summary>
    private void Collect()
    {
        int list = -1;
        foreach (PdfPageLayout page in pages)
        {
            foreach (LayoutElement element in page.Elements)
            {
                if (element is LayoutBlock { Role: LayoutRole.ListItem } item)
                {
                    if (list < 0)
                    {
                        groups.Add(("list", "list", []));
                        list = groups.Count - 1;
                        body.Add("#/groups/" + list.ToString(CultureInfo.InvariantCulture));
                    }

                    string parent = "#/groups/" + list.ToString(CultureInfo.InvariantCulture);
                    groups[list].Children.Add(Text(page, item, parent, "body"));
                    continue;
                }

                // Anything else in the body ends the list; furniture and notes come between pages without ending it.
                if (element is not LayoutBlock { Role: LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber or LayoutRole.Footnote })
                {
                    list = -1;
                }

                switch (element)
                {
                    case LayoutBlock { Role: LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber } block:
                        furniture.Add(Text(page, block, "#/furniture", "furniture"));
                        break;
                    case LayoutBlock block:
                        body.Add(Text(page, block, "#/body", "body"));
                        break;
                    case LayoutTable table:
                        body.Add(Table(page, table));
                        break;
                    case LayoutFigure figure:
                        body.Add(Picture(page, figure));
                        break;
                }
            }
        }
    }

    private string Text(PdfPageLayout page, LayoutBlock block, string parent, string layer)
    {
        int index = texts.Count;
        string self = "#/texts/" + index.ToString(CultureInfo.InvariantCulture);
        (string label, string egap) = block.Role switch
        {
            LayoutRole.Title => ("title", "title"),
            LayoutRole.Heading => ("section_header", "heading"),
            LayoutRole.ListItem => ("list_item", "list_item"),
            LayoutRole.Caption => ("caption", "caption"),
            LayoutRole.Footnote => ("footnote", "footnote"),
            LayoutRole.Quote => ("text", "quote"),
            LayoutRole.Code => ("code", "code"),
            LayoutRole.PageHeader => ("page_header", "page_header"),
            LayoutRole.PageFooter => ("page_footer", "page_footer"),
            LayoutRole.PageNumber => (block.Bounds.Y < page.Height / 2 ? "page_header" : "page_footer", "page_number"),
            _ => ("text", "text"),
        };

        string text = block.Text;
        texts.Add((json, _) =>
        {
            json.WriteStartObject();
            json.WriteString("self_ref", self);
            Ref(json, "parent", parent);
            Refs(json, []);
            json.WriteString("content_layer", layer);
            Meta(json, egap, block.Confidence, block.ContinuesPrevious, level: block.Role == LayoutRole.ListItem ? block.Level : 0);
            json.WriteString("label", label);
            Prov(json, page, block.Bounds, text.Length);
            json.WriteString("orig", text);
            json.WriteString("text", text);
            switch (block.Role)
            {
                case LayoutRole.Heading:
                    json.WriteNumber("level", Math.Clamp(block.Level, 1, 100));
                    break;
                case LayoutRole.ListItem:
                    string marker = block.ListMarker ?? "-";
                    json.WriteBoolean("enumerated", marker.Any(char.IsLetterOrDigit));
                    json.WriteString("marker", marker);
                    break;
                case LayoutRole.Code:
                    Refs(json, [], "captions");
                    Refs(json, [], "references");
                    Refs(json, [], "footnotes");
                    break;
            }

            json.WriteEndObject();
        });
        return self;
    }

    private string Table(PdfPageLayout page, LayoutTable table)
    {
        string self = "#/tables/" + tables.Count.ToString(CultureInfo.InvariantCulture);
        tables.Add((json, _) =>
        {
            json.WriteStartObject();
            json.WriteString("self_ref", self);
            Ref(json, "parent", "#/body");
            Refs(json, []);
            json.WriteString("content_layer", "body");
            Meta(json, table.IsRuled ? "ruled_table" : "table", table.Confidence, table.ContinuesPrevious);
            json.WriteString("label", "table");
            Prov(json, page, table.Bounds, 0);
            Refs(json, [], "captions");
            Refs(json, [], "references");
            Refs(json, [], "footnotes");
            json.WriteStartObject("data");
            json.WriteStartArray("table_cells");
            int headerRows = 0;
            while (headerRows < table.RowCount && table.Cells.Where(c => c.Row == headerRows).ToList() is { Count: > 0 } row && row.All(c => c.IsHeader))
            {
                headerRows++;
            }

            foreach (LayoutTableCell cell in table.Cells)
            {
                json.WriteStartObject();
                json.WriteStartObject("bbox");
                json.WriteNumber("l", Round(Math.Clamp(cell.Bounds.X, 0, page.Width)));
                json.WriteNumber("t", Round(Math.Clamp(cell.Bounds.Y, 0, page.Height)));
                json.WriteNumber("r", Round(Math.Clamp(cell.Bounds.X + cell.Bounds.Width, 0, page.Width)));
                json.WriteNumber("b", Round(Math.Clamp(cell.Bounds.Y + cell.Bounds.Height, 0, page.Height)));
                json.WriteString("coord_origin", "TOPLEFT");
                json.WriteEndObject();
                json.WriteNumber("row_span", cell.RowSpan);
                json.WriteNumber("col_span", cell.ColumnSpan);
                json.WriteNumber("start_row_offset_idx", cell.Row);
                json.WriteNumber("end_row_offset_idx", cell.Row + cell.RowSpan);
                json.WriteNumber("start_col_offset_idx", cell.Column);
                json.WriteNumber("end_col_offset_idx", cell.Column + cell.ColumnSpan);
                json.WriteString("text", cell.Text);
                json.WriteBoolean("column_header", cell.IsHeader || cell.Row < headerRows);
                json.WriteBoolean("row_header", false);
                json.WriteBoolean("row_section", false);
                json.WriteBoolean("fillable", false);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteNumber("num_rows", table.RowCount);
            json.WriteNumber("num_cols", table.ColumnCount);
            json.WriteEndObject();
            json.WriteEndObject();
        });
        return self;
    }

    private string Picture(PdfPageLayout page, LayoutFigure figure)
    {
        string self = "#/pictures/" + pictures.Count.ToString(CultureInfo.InvariantCulture);
        pictures.Add((json, _) =>
        {
            json.WriteStartObject();
            json.WriteString("self_ref", self);
            Ref(json, "parent", "#/body");
            Refs(json, []);
            json.WriteString("content_layer", figure.IsDecorative ? "background" : "body");
            Meta(json, "figure", figure.Confidence, false, figure.AltText);
            json.WriteString("label", "picture");
            Prov(json, page, figure.Bounds, 0);
            Refs(json, [], "captions");
            Refs(json, [], "references");
            Refs(json, [], "footnotes");
            if (Image(figure) is (string uri, string type, int width, int height))
            {
                json.WriteStartObject("image");
                json.WriteString("mimetype", type);
                json.WriteNumber("dpi", (int)Math.Round(options.FigureResolution));
                json.WriteStartObject("size");
                json.WriteNumber("width", width);
                json.WriteNumber("height", height);
                json.WriteEndObject();
                json.WriteString("uri", uri);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        });
        return self;
    }

    private readonly Dictionary<string, string> written = [];

    /// <summary>A picture's image under the options: its URI, media type and size in pixels; null when it gets none.</summary>
    private (string Uri, string Type, int Width, int Height)? Image(LayoutFigure figure)
    {
        if (options.Images == DoclingImageMode.Omit || (figure.Bounds.Width < options.MinImageSize && figure.Bounds.Height < options.MinImageSize)
            || figure.Image(options.FigureResolution) is not (Egap.Documents.ImageData image, int width, int height))
        {
            return null;
        }

        string extension = image.ContentType == "image/jpeg" ? ".jpg" : ".png";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes.Span));
        string name = written.TryGetValue(hash, out string? known) ? known : "image-" + (written.Count + 1).ToString("000", CultureInfo.InvariantCulture) + extension;
        string? uri;
        if (options.ImageResolver is { } resolver)
        {
            uri = resolver(new DoclingImage(image.Bytes, image.ContentType, name, figure));
        }
        else if (options.Images == DoclingImageMode.Files)
        {
            string folder = options.ImagesFolder!;
            if (!written.ContainsKey(hash))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(System.IO.Path.Combine(folder, name), image.Bytes.ToArray());
            }

            uri = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder)) + "/" + name;
        }
        else
        {
            uri = "data:" + image.ContentType + ";base64," + Convert.ToBase64String(image.Bytes.Span);
        }

        written.TryAdd(hash, name);
        return uri is null ? null : (uri, image.ContentType, width, height);
    }

    /// <summary>Egap's own facts in <c>meta</c>, which Docling's schema leaves open: the role it found, its confidence, whether the item carries on the one before.</summary>
    private static void Meta(Utf8JsonWriter json, string role, double confidence, bool continues, string? altText = null, int level = 0)
    {
        // docling-core takes custom fields flat, named namespace__field.
        json.WriteStartObject("meta");
        json.WriteString("egap__role", role);
        json.WriteNumber("egap__confidence", Math.Round(confidence, 3));
        if (continues)
        {
            json.WriteBoolean("egap__continues", true);
        }

        if (altText is not null)
        {
            json.WriteString("egap__alt_text", altText);
        }

        if (level > 0)
        {
            json.WriteNumber("egap__level", level);
        }

        json.WriteEndObject();
    }

    /// <summary>Provenance: the page number from 1 and the box from the page's bottom left, as Docling writes for PDF.</summary>
    private static void Prov(Utf8JsonWriter json, PdfPageLayout page, RectF bounds, int characters)
    {
        json.WriteStartArray("prov");
        json.WriteStartObject();
        json.WriteNumber("page_no", page.PageIndex + 1);
        json.WriteStartObject("bbox");
        // docling-core clamps boxes to the page with a warning; they are clamped here instead.
        json.WriteNumber("l", Round(Math.Clamp(bounds.X, 0, page.Width)));
        json.WriteNumber("t", Round(Math.Clamp(page.Height - bounds.Y, 0, page.Height)));
        json.WriteNumber("r", Round(Math.Clamp(bounds.X + bounds.Width, 0, page.Width)));
        json.WriteNumber("b", Round(Math.Clamp(page.Height - (bounds.Y + bounds.Height), 0, page.Height)));
        json.WriteString("coord_origin", "BOTTOMLEFT");
        json.WriteEndObject();
        json.WriteStartArray("charspan");
        json.WriteNumberValue(0);
        json.WriteNumberValue(characters);
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();
    }

    private static void Root(Utf8JsonWriter json, string name, string layer, List<string> children)
    {
        json.WriteStartObject(name);
        json.WriteString("self_ref", "#/" + name);
        Refs(json, children);
        json.WriteString("content_layer", layer);
        json.WriteString("name", "_root_");
        json.WriteString("label", "unspecified");
        json.WriteEndObject();
    }

    private static void Items(Utf8JsonWriter json, string name, List<Action<Utf8JsonWriter, string>> items)
    {
        json.WriteStartArray(name);
        foreach (Action<Utf8JsonWriter, string> item in items)
        {
            item(json, name);
        }

        json.WriteEndArray();
    }

    private static void Ref(Utf8JsonWriter json, string name, string pointer)
    {
        json.WriteStartObject(name);
        json.WriteString("$ref", pointer);
        json.WriteEndObject();
    }

    private static void Refs(Utf8JsonWriter json, List<string> pointers, string name = "children")
    {
        json.WriteStartArray(name);
        foreach (string pointer in pointers)
        {
            json.WriteStartObject();
            json.WriteString("$ref", pointer);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static double Round(double value)
    {
        return Math.Round(value, 3);
    }

    /// <summary>A 64-bit hash of the source bytes for the origin, as Docling's binary_hash is: FNV-1a here, stable across runs.</summary>
    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 1099511628211;
        }

        return hash;
    }
}

/// <summary>Reads a DoclingDocument back into page layouts, walking the body from its children in order.</summary>
internal static class DoclingReader
{
    public static IReadOnlyList<PdfPageLayout> Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("body", out JsonElement body))
        {
            throw new JsonException("This is not a DoclingDocument: it has no body.");
        }

        Dictionary<int, (float Width, float Height)> sizes = [];
        if (root.TryGetProperty("pages", out JsonElement pages) && pages.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty page in pages.EnumerateObject())
            {
                JsonElement size = page.Value.GetProperty("size");
                sizes[page.Value.GetProperty("page_no").GetInt32()] = ((float)Number(size, "width"), (float)Number(size, "height"));
            }
        }

        Dictionary<int, List<LayoutElement>> elements = [];
        void Walk(JsonElement node, LayoutRole? within)
        {
            foreach (JsonElement child in Children(node))
            {
                if (Resolve(root, child) is not JsonElement item)
                {
                    continue;
                }

                string pointer = item.TryGetProperty("self_ref", out JsonElement s) ? s.GetString() ?? "" : "";
                if (pointer.StartsWith("#/groups/", StringComparison.Ordinal))
                {
                    Walk(item, Text(item, "label") is "list" or "ordered_list" ? LayoutRole.ListItem : within);
                    continue;
                }

                if (Element(item, pointer, sizes, elements) is (int page, LayoutElement element))
                {
                    elements[page].Add(element);
                }

                Walk(item, within);
            }
        }

        // The furniture comes first on each page as headers and last as footers, around the body, as the analysis gives it.
        Dictionary<int, List<LayoutElement>> after = [];
        if (root.TryGetProperty("furniture", out JsonElement furniture))
        {
            Walk(furniture, null);
            foreach ((int page, List<LayoutElement> found) in elements)
            {
                float height = sizes.TryGetValue(page, out (float Width, float Height) size) ? size.Height : 792f;
                after[page] = [.. found.Where(e => e is LayoutBlock { Role: LayoutRole.PageFooter } || (e is LayoutBlock { Role: LayoutRole.PageNumber } && e.Bounds.Y > height / 2))];
                found.RemoveAll(after[page].Contains);
            }
        }

        Walk(body, null);
        foreach ((int page, List<LayoutElement> footers) in after)
        {
            elements[page].AddRange(footers);
        }
        return [.. sizes.Keys.Union(elements.Keys).Order().Select(n =>
        {
            (float width, float height) = sizes.GetValueOrDefault(n, (612f, 792f));
            List<LayoutElement> found = elements.GetValueOrDefault(n) ?? [];
            List<LayoutElement> ordered = [.. found.Select((e, i) => Renumber(e, i))];
            return new PdfPageLayout(n - 1, width, height, ordered, false, []);
        })];
    }

    /// <summary>One item as a layout element on its page; null for an item with no provenance or of a kind with no counterpart.</summary>
    private static (int Page, LayoutElement Element)? Element(JsonElement item, string pointer, Dictionary<int, (float Width, float Height)> sizes,
        Dictionary<int, List<LayoutElement>> elements)
    {
        if (!item.TryGetProperty("prov", out JsonElement prov) || prov.ValueKind != JsonValueKind.Array || prov.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = prov[0];
        int page = first.GetProperty("page_no").GetInt32();
        float height = sizes.TryGetValue(page, out (float Width, float Height) size) ? size.Height : 792f;
        RectF bounds = Box(first.GetProperty("bbox"), height);
        if (!elements.ContainsKey(page))
        {
            elements[page] = [];
        }

        JsonElement meta = item.TryGetProperty("meta", out JsonElement m) && m.ValueKind == JsonValueKind.Object ? m : default;
        double confidence = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("egap__confidence", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 1;
        bool continues = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("egap__continues", out JsonElement k) && k.ValueKind == JsonValueKind.True;
        string egapRole = Text(meta, "egap__role") ?? "";
        if (pointer.StartsWith("#/tables/", StringComparison.Ordinal))
        {
            JsonElement data = item.GetProperty("data");
            int rows = data.TryGetProperty("num_rows", out JsonElement r) ? r.GetInt32() : 0, columns = data.TryGetProperty("num_cols", out JsonElement cs) ? cs.GetInt32() : 0;
            List<LayoutTableCell> cells = [];
            foreach (JsonElement cell in data.GetProperty("table_cells").EnumerateArray())
            {
                RectF box = cell.TryGetProperty("bbox", out JsonElement bb) && bb.ValueKind == JsonValueKind.Object ? Box(bb, height) : default;
                string text = Text(cell, "text") ?? "";
                int row = cell.GetProperty("start_row_offset_idx").GetInt32(), column = cell.GetProperty("start_col_offset_idx").GetInt32();
                int rowSpan = Math.Max(1, cell.GetProperty("end_row_offset_idx").GetInt32() - row);
                int columnSpan = Math.Max(1, cell.GetProperty("end_col_offset_idx").GetInt32() - column);
                bool header = cell.TryGetProperty("column_header", out JsonElement h) && h.ValueKind == JsonValueKind.True;
                cells.Add(new LayoutTableCell(row, column, rowSpan, columnSpan, header, text, box, text.Length == 0 ? [] : [Line(text, box)]));
            }

            return (page, new LayoutTable(bounds, 0, confidence, rows, columns, egapRole == "ruled_table", continues, false, [.. cells.OrderBy(x => x.Row).ThenBy(x => x.Column)]));
        }

        if (pointer.StartsWith("#/pictures/", StringComparison.Ordinal))
        {
            string? alt = Text(meta, "egap__alt_text");
            bool decorative = Text(item, "content_layer") == "background";
            return (page, new LayoutFigure(bounds, 0, confidence, null, [], [], alt, decorative, false, false) { Embedded = Embedded(item) });
        }

        if (!pointer.StartsWith("#/texts/", StringComparison.Ordinal) || Text(item, "text") is not string content)
        {
            return null;
        }

        string label = Text(item, "label") ?? "text";
        LayoutRole role = (label, egapRole) switch
        {
            (_, "quote") => LayoutRole.Quote,
            (_, "page_number") => LayoutRole.PageNumber,
            ("title", _) => LayoutRole.Title,
            ("section_header", _) => LayoutRole.Heading,
            ("list_item", _) => LayoutRole.ListItem,
            ("caption", _) => LayoutRole.Caption,
            ("footnote", _) => LayoutRole.Footnote,
            ("code", _) => LayoutRole.Code,
            ("page_header", _) => LayoutRole.PageHeader,
            ("page_footer", _) => LayoutRole.PageFooter,
            _ => LayoutRole.Text,
        };
        int level = role switch
        {
            LayoutRole.Heading => item.TryGetProperty("level", out JsonElement l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 1,
            LayoutRole.Title => 0,
            LayoutRole.ListItem => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("egap__level", out JsonElement lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 1,
            _ => 0,
        };
        string? marker = role == LayoutRole.ListItem ? Text(item, "marker") : null;
        return (page, new LayoutBlock(bounds, 0, confidence, role, content, level, marker, continues, content.Length == 0 ? [] : [Line(content, bounds)]));
    }

    /// <summary>A picture's image when the JSON carries it as a base64 data URI; files and other URIs are left to the caller.</summary>
    private static (Egap.Documents.ImageData, int, int)? Embedded(JsonElement item)
    {
        if (!item.TryGetProperty("image", out JsonElement image) || image.ValueKind != JsonValueKind.Object || Text(image, "uri") is not string uri
            || !uri.StartsWith("data:", StringComparison.Ordinal) || uri.IndexOf(";base64,", StringComparison.Ordinal) is not (> 5 and int comma))
        {
            return null;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(uri[(comma + 8)..]);
            int width = image.TryGetProperty("size", out JsonElement size) ? (int)Number(size, "width") : 0;
            int height = image.TryGetProperty("size", out JsonElement size2) ? (int)Number(size2, "height") : 0;
            return (new Egap.Documents.ImageData(bytes, uri[5..comma]), width, height);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A line of text over a box, its words spread across it by their share of the characters.</summary>
    private static LayoutLine Line(string text, RectF box)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int characters = Math.Max(1, words.Sum(w => w.Length + 1));
        float x = box.X;
        List<LayoutWord> placed = [];
        foreach (string word in words)
        {
            float width = box.Width * (word.Length + 1) / characters;
            placed.Add(new LayoutWord(word, new RectF(x, box.Y, width, box.Height), "", Math.Min(box.Height, 12), false, false, PdfColor.Black));
            x += width;
        }

        return new LayoutLine(string.Join(' ', words), box, box.Y + (box.Height * 0.8f), placed);
    }

    private static LayoutElement Renumber(LayoutElement element, int order)
    {
        return element switch
        {
            LayoutBlock b => new LayoutBlock(b.Bounds, order, b.Confidence, b.Role, b.Text, b.Level, b.ListMarker, b.ContinuesPrevious, b.Lines),
            LayoutTable t => new LayoutTable(t.Bounds, order, t.Confidence, t.RowCount, t.ColumnCount, t.IsRuled, t.ContinuesPrevious, t.RepeatsHeader, t.Cells),
            LayoutFigure f => new LayoutFigure(f.Bounds, order, f.Confidence, null, f.Images, f.Labels, f.AltText, f.IsDecorative, f.IsInline, f.IsWrapped) { Embedded = f.Embedded },
            _ => element,
        };
    }

    /// <summary>A box in displayed page space (y down from the top), from Docling's box in either origin.</summary>
    private static RectF Box(JsonElement box, float height)
    {
        double l = Number(box, "l"), t = Number(box, "t"), r = Number(box, "r"), b = Number(box, "b");
        bool bottomLeft = Text(box, "coord_origin") == "BOTTOMLEFT";
        double top = bottomLeft ? height - Math.Max(t, b) : Math.Min(t, b), bottom = bottomLeft ? height - Math.Min(t, b) : Math.Max(t, b);
        return new RectF((float)Math.Min(l, r), (float)top, (float)Math.Abs(r - l), (float)(bottom - top));
    }

    private static List<JsonElement> Children(JsonElement node)
    {
        return node.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array ? [.. children.EnumerateArray()] : [];
    }

    /// <summary>The item a JSON pointer such as <c>#/texts/3</c> names.</summary>
    private static JsonElement? Resolve(JsonElement root, JsonElement reference)
    {
        if (!reference.TryGetProperty("$ref", out JsonElement pointer) || pointer.GetString() is not string path || !path.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonElement at = root;
        foreach (string part in path[2..].Split('/'))
        {
            if (at.ValueKind == JsonValueKind.Object && at.TryGetProperty(part, out JsonElement next))
            {
                at = next;
            }
            else if (at.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < at.GetArrayLength())
            {
                at = at[i];
            }
            else
            {
                return null;
            }
        }

        return at;
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double Number(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }
}
