using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary><c>egap info</c>: what PDFs are, without rendering them (CLI design Q11).</summary>
internal static class InfoCommand
{
    /// <summary>The version of the <c>info --json</c> schema, docs/schemas/egap-info-1.json.</summary>
    public const int Schema = 1;

    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<bool> all = new("--all", "-a") { Description = "Also list the metadata, fonts, page sizes, layers, attachments, signatures and revisions." };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-info-1.json) instead of text." };
        Command command = new("info", "Show what PDFs are: version, pages and sizes, encryption, signatures, PDF/A claim, forms and attachments; with --all, much more.");
        inputs.AddTo(command);
        password.AddTo(command);
        command.Options.Add(all);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            string? secret = password.Read(session, result);
            bool everything = result.GetValue(all), asJson = result.GetValue(json);
            Report?[] found = new Report?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                found[file.Index] = Read(file, document, everything);
                return [];
            });

            if (asJson)
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", Schema);
                    writer.WriteStartArray("files");
                    foreach (Report? report in found)
                    {
                        if (report is not null)
                        {
                            WriteJson(writer, report, everything);
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach (Report? report in found)
                {
                    if (report is not null)
                    {
                        WriteText(session.Console.Text, report, everything);
                    }
                }
            }

            return code;
        });
        return command;
    }

    /// <summary>Everything info shows about one file, read while the document is open.</summary>
    private sealed record Report(
        string Path,
        string Version,
        int Pages,
        IReadOnlyList<(double Width, double Height, int Count)> Sizes,
        bool Encrypted,
        string? Encryption,
        PdfPermissions Permissions,
        bool Linearized,
        bool Tagged,
        string? Conformance,
        int FormFields,
        IReadOnlyList<SignatureInfo> Signatures,
        IReadOnlyList<AttachmentInfo> Attachments,
        IReadOnlyList<(string Name, bool Visible)> Layers,
        int Bookmarks,
        int Revisions,
        IReadOnlyList<(string Key, string Value)> Metadata,
        IReadOnlyList<FontInfo> Fonts,
        int Warnings);

    private sealed record SignatureInfo(string Field, string Kind, string? Signer, DateTimeOffset? Time, string? SubFilter, bool CoversWholeFile);

    private sealed record AttachmentInfo(string Name, long? Size, string? MimeType, string? Relationship);

    private sealed record FontInfo(string Name, string Type, bool Embedded, bool Subset);

    private static Report Read(InputFile file, PdfDocument document, bool everything)
    {
        List<(double, double, int)> sizes = [];
        foreach (PdfPage page in document.Pages)
        {
            (double width, double height) = DisplaySize(page);
            int at = sizes.FindIndex(s => s.Item1 == width && s.Item2 == height);
            if (at < 0)
            {
                sizes.Add((width, height, 1));
            }
            else
            {
                sizes[at] = (width, height, sizes[at].Item3 + 1);
            }
        }

        List<SignatureInfo> signatures = [.. document.Signatures.Select(s => new SignatureInfo(
            s.Name, Kebab(s.Kind.ToString()), s.SignerName, s.SigningTime, s.SubFilter, s.CoversWholeFile))];
        List<AttachmentInfo> attachments = [.. document.Attachments.Select(a => new AttachmentInfo(a.FileName, a.Size, a.MimeType, a.Relationship))];
        List<(string, bool)> layers = [.. document.Layers.Select(l => (l.Name, l.IsVisible))];
        List<(string, string)> metadata = [];
        List<FontInfo> fonts = [];
        int bookmarks = 0, revisions = 0;
        if (everything)
        {
            PdfMetadata info = document.Metadata;
            void Add(string key, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    metadata.Add((key, value));
                }
            }

            Add("title", info.Title);
            Add("author", info.Author);
            Add("subject", info.Subject);
            Add("keywords", info.Keywords);
            Add("creator", info.Creator);
            Add("producer", info.Producer);
            Add("created", info.CreationDate?.ToString("o", CultureInfo.InvariantCulture));
            Add("modified", info.ModificationDate?.ToString("o", CultureInfo.InvariantCulture));
            Add("xmp", info.HasXmp ? "yes" : null);
            bookmarks = document.Outline.GetAllItems().Count;
            revisions = document.Revisions.Count;
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (PdfPage page in document.Pages)
            {
                foreach (PdfFont font in page.Fonts.Values)
                {
                    string name = font.BaseFont ?? font.Name ?? "(unnamed)";
                    if (seen.Add(name + "\0" + font.Type))
                    {
                        fonts.Add(new FontInfo(name, Kebab(font.Type.ToString()), font.IsEmbedded, font.IsSubset));
                    }
                }
            }
        }

        return new Report(
            file.Display,
            document.Version,
            document.Pages.Count,
            sizes,
            document.IsEncrypted,
            document.IsEncrypted ? Encryption(document) : null,
            document.Permissions,
            document.IsLinearized,
            document.Structure.Exists,
            document.Conformance == PdfAConformance.None ? null : Conformance(document.Conformance),
            document.Form.Count,
            signatures,
            attachments,
            layers,
            bookmarks,
            revisions,
            metadata,
            fonts,
            document.Warnings.Count);
    }

    /// <summary>The size a viewer shows: the crop box in points, times the user unit, turned by the page's rotation.</summary>
    private static (double Width, double Height) DisplaySize(PdfPage page)
    {
        PdfRectangle box = page.CropBox;
        double unit = page.UserUnit > 0 ? page.UserUnit : 1;
        (double width, double height) = (Math.Round(Math.Abs(box.Width) * unit, 2), Math.Round(Math.Abs(box.Height) * unit, 2));
        return Math.Abs(page.Rotate) % 180 == 90 ? (height, width) : (width, height);
    }

    /// <summary>The input's encryption from its encryption dictionary: <c>rc4-40</c>, <c>rc4-128</c>, <c>aes-128</c> or <c>aes-256</c>.</summary>
    private static string Encryption(PdfDocument document)
    {
        if (document.Trailer["Encrypt"]?.Resolve() is not PdfDictionary encrypt)
        {
            return "unknown";
        }

        long version = encrypt.GetInt("V") ?? 0;
        if (version >= 5)
        {
            return "aes-256";
        }

        if (version == 4)
        {
            string? method = (encrypt.Get("CF")?.Resolve() as PdfDictionary)?.Get("StdCF")?.Resolve() is PdfDictionary filter ? filter.GetName("CFM")?.Value : null;
            return method == "AESV2" ? "aes-128" : "rc4-128";
        }

        return version == 1 ? "rc4-40" : $"rc4-{(encrypt.GetInt("Length") ?? 40).ToString(CultureInfo.InvariantCulture)}";
    }

    private static void WriteText(TextWriter output, Report report, bool everything)
    {
        List<string> parts = [$"PDF {report.Version}", Count(report.Pages, "page")];
        if (report.Sizes.Count == 1)
        {
            parts.Add(Size(report.Sizes[0].Width, report.Sizes[0].Height));
        }
        else if (report.Sizes.Count > 1)
        {
            parts.Add($"{report.Sizes.Count} page sizes");
        }

        if (report.Encrypted)
        {
            parts.Add($"encrypted ({report.Encryption})");
        }

        if (report.Signatures.Count > 0)
        {
            parts.Add(Count(report.Signatures.Count, "signature"));
        }

        if (report.Conformance is { } conformance)
        {
            parts.Add(conformance);
        }

        if (report.FormFields > 0)
        {
            parts.Add($"form ({Count(report.FormFields, "field")})");
        }

        if (report.Attachments.Count > 0)
        {
            parts.Add(Count(report.Attachments.Count, "attachment"));
        }

        if (report.Layers.Count > 0)
        {
            parts.Add(Count(report.Layers.Count, "layer"));
        }

        if (report.Tagged)
        {
            parts.Add("tagged");
        }

        if (report.Linearized)
        {
            parts.Add("linearized");
        }

        if (report.Warnings > 0)
        {
            parts.Add($"repaired ({Count(report.Warnings, "warning")})");
        }

        output.WriteLine($"{report.Path}: {string.Join(", ", parts)}");
        if (!everything)
        {
            return;
        }

        foreach ((string key, string value) in report.Metadata)
        {
            output.WriteLine($"  {key,-14} {value}");
        }

        if (report.Encrypted)
        {
            output.WriteLine($"  {"permissions",-14} {Permissions(report.Permissions)}");
        }

        foreach ((double width, double height, int count) in report.Sizes)
        {
            output.WriteLine($"  {"page size",-14} {Size(width, height)}: {Count(count, "page")}");
        }

        if (report.Bookmarks > 0)
        {
            output.WriteLine($"  {"bookmarks",-14} {report.Bookmarks}");
        }

        output.WriteLine($"  {"revisions",-14} {report.Revisions}");
        foreach (FontInfo font in report.Fonts)
        {
            output.WriteLine($"  {"font",-14} {font.Name} ({font.Type}{(font.Embedded ? font.Subset ? ", embedded subset" : ", embedded" : ", not embedded")})");
        }

        foreach ((string name, bool visible) in report.Layers)
        {
            output.WriteLine($"  {"layer",-14} {name}{(visible ? string.Empty : " (hidden)")}");
        }

        foreach (AttachmentInfo attachment in report.Attachments)
        {
            string size = attachment.Size is { } bytes ? $", {bytes.ToString(CultureInfo.InvariantCulture)} bytes" : string.Empty;
            output.WriteLine($"  {"attachment",-14} {attachment.Name}{size}");
        }

        foreach (SignatureInfo signature in report.Signatures)
        {
            string when = signature.Time is { } time ? $", {time.ToString("o", CultureInfo.InvariantCulture)}" : string.Empty;
            output.WriteLine($"  {"signature",-14} {signature.Field}: {signature.Kind}{(signature.Signer is { } who ? $" by {who}" : string.Empty)}{when}");
        }
    }

    private static void WriteJson(Utf8JsonWriter json, Report report, bool everything)
    {
        json.WriteStartObject();
        json.WriteString("path", report.Path);
        json.WriteString("version", report.Version);
        json.WriteNumber("pages", report.Pages);
        json.WriteStartArray("pageSizes");
        foreach ((double width, double height, int count) in report.Sizes)
        {
            json.WriteStartObject();
            json.WriteNumber("width", width);
            json.WriteNumber("height", height);
            json.Text("name", PaperName(width, height));
            json.WriteNumber("pages", count);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteBoolean("encrypted", report.Encrypted);
        json.Text("encryption", report.Encryption);
        json.WriteStartArray("permissions");
        foreach (string permission in PermissionNames(report.Permissions))
        {
            json.WriteStringValue(permission);
        }

        json.WriteEndArray();
        json.WriteBoolean("linearized", report.Linearized);
        json.WriteBoolean("tagged", report.Tagged);
        json.Text("pdfa", report.Conformance);
        json.WriteNumber("formFields", report.FormFields);
        json.WriteNumber("layers", report.Layers.Count);
        json.WriteNumber("warnings", report.Warnings);
        json.WriteStartArray("signatures");
        foreach (SignatureInfo signature in report.Signatures)
        {
            json.WriteStartObject();
            json.WriteString("field", signature.Field);
            json.WriteString("kind", signature.Kind);
            json.Text("signer", signature.Signer);
            json.Text("time", signature.Time?.ToString("o", CultureInfo.InvariantCulture));
            json.Text("subFilter", signature.SubFilter);
            json.WriteBoolean("coversWholeFile", signature.CoversWholeFile);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("attachments");
        foreach (AttachmentInfo attachment in report.Attachments)
        {
            json.WriteStartObject();
            json.WriteString("name", attachment.Name);
            json.Number("size", attachment.Size);
            json.Text("mimeType", attachment.MimeType);
            json.Text("relationship", attachment.Relationship);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        if (everything)
        {
            json.WriteStartObject("metadata");
            foreach ((string key, string value) in report.Metadata)
            {
                json.WriteString(key, value);
            }

            json.WriteEndObject();
            json.WriteNumber("bookmarks", report.Bookmarks);
            json.WriteNumber("revisions", report.Revisions);
            json.WriteStartArray("layerNames");
            foreach ((string name, bool visible) in report.Layers)
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteBoolean("visible", visible);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("fonts");
            foreach (FontInfo font in report.Fonts)
            {
                json.WriteStartObject();
                json.WriteString("name", font.Name);
                json.WriteString("type", font.Type);
                json.WriteBoolean("embedded", font.Embedded);
                json.WriteBoolean("subset", font.Subset);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    /// <summary>A page size in points, with its paper name and orientation when it has one: <c>A4 portrait (595x842 pt)</c>.</summary>
    internal static string Size(double width, double height)
    {
        string points = $"{Number(width)}x{Number(height)} pt";
        return PaperName(width, height) is { } name ? $"{name} {(width > height ? "landscape" : "portrait")} ({points})" : points;
    }

    /// <summary>The paper a size in points matches within a point, either way round, or <see langword="null"/>.</summary>
    internal static string? PaperName(double width, double height)
    {
        (double small, double large) = width < height ? (width, height) : (height, width);
        foreach ((string name, double w, double h) in Papers)
        {
            if (Math.Abs(small - w) <= 1 && Math.Abs(large - h) <= 1)
            {
                return name;
            }
        }

        return null;
    }

    private static readonly (string Name, double Width, double Height)[] Papers =
    [
        ("A3", 841.89, 1190.55), ("A4", 595.28, 841.89), ("A5", 419.53, 595.28), ("A6", 297.64, 419.53),
        ("B4", 708.66, 1000.63), ("B5", 498.9, 708.66), ("Letter", 612, 792), ("Legal", 612, 1008), ("Tabloid", 792, 1224),
    ];

    private static string Number(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Count(int count, string noun) => $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Conformance(PdfAConformance conformance)
    {
        string name = conformance.ToString();
        return $"PDF/A-{name["PdfA".Length..]}";
    }

    private static string Permissions(PdfPermissions permissions)
    {
        IEnumerable<string> names = PermissionNames(permissions);
        return permissions == PdfPermissions.None ? "none" : string.Join(", ", names);
    }

    private static IEnumerable<string> PermissionNames(PdfPermissions permissions)
    {
        foreach (PdfPermissions flag in (PdfPermissions[])[PdfPermissions.Print, PdfPermissions.PrintHighQuality, PdfPermissions.Modify, PdfPermissions.Assemble,
            PdfPermissions.CopyContent, PdfPermissions.ExtractForAccessibility, PdfPermissions.Annotate, PdfPermissions.FillForms])
        {
            if (permissions.HasFlag(flag))
            {
                yield return Kebab(flag.ToString());
            }
        }
    }

    /// <summary><c>PrintHighQuality</c> as <c>print-high-quality</c>.</summary>
    internal static string Kebab(string name)
    {
        System.Text.StringBuilder text = new();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1]))
            {
                text.Append('-');
            }

            text.Append(char.ToLowerInvariant(name[i]));
        }

        return text.ToString();
    }
}
