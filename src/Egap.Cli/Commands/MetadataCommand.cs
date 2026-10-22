using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Egap.Cli.Host;
using Egap.Pdf;

using SaveOptions = Egap.Cli.Host.SaveOptions;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap metadata show</c>, <c>set</c> and <c>strip</c>: the document information, the XMP packet and the outline
/// (bookmarks), which <c>show --json</c> writes and <c>set --outline</c> reads back (schema egap-metadata-1.json).
/// </summary>
internal static class MetadataCommand
{
    /// <summary>The version of the <c>metadata show --json</c> schema and of the outline file.</summary>
    public const int Schema = 1;

    public static Command Create(EgapSession session)
    {
        Command command = new("metadata", "Show, set or strip document information, XMP and the outline (bookmarks).");
        command.Subcommands.Add(Show(session));
        command.Subcommands.Add(Set(session));
        command.Subcommands.Add(Strip(session));
        return command;
    }

    private static Command Show(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-metadata-1.json) instead of text." };
        Option<bool> xmp = new("--xmp") { Description = "Also show the XMP packet." };
        Option<bool> outline = new("--outline") { Description = "Also show the outline (bookmarks)." };
        Command command = new("show", "Show the document information, and with --xmp and --outline the XMP packet and the bookmarks.");
        inputs.AddTo(command);
        command.Options.Add(json);
        command.Options.Add(xmp);
        command.Options.Add(outline);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            string? secret = password.Read(session, result);
            bool asJson = result.GetValue(json), withXmp = result.GetValue(xmp), withOutline = result.GetValue(outline);
            string?[] reports = new string?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                reports[file.Index] = asJson ? Json(file, document, withXmp, withOutline) : Text(file, document, withXmp, withOutline);
                return [];
            });

            if (asJson)
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", Schema);
                    writer.WriteStartArray("files");
                    foreach (string? report in reports)
                    {
                        if (report is not null)
                        {
                            using JsonDocument one = JsonDocument.Parse(report);
                            one.RootElement.WriteTo(writer);
                        }
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                foreach (string? report in reports)
                {
                    session.Console.Text.Write(report);
                }
            }

            return code;
        });
        return command;
    }

    private static Command Set(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        (string Name, Option<string?> Option)[] fields =
        [
            .. ((string[])["title", "author", "subject", "keywords", "creator", "producer"]).Select(name =>
                (name, new Option<string?>("--" + name) { Description = $"Set the {name}; an empty value removes it.", HelpName = "text" })),
        ];
        Option<string?> created = new("--created") { Description = "Set the creation date, ISO 8601 such as 2026-10-22T09:30:00Z.", HelpName = "date" };
        Option<string?> modified = new("--modified") { Description = "Set the modification date, ISO 8601; without it a change sets it to now.", HelpName = "date" };
        Option<string?> xmp = new("--xmp") { Description = "Replace the XMP packet with this XML file.", HelpName = "file" };
        Option<string?> outline = new("--outline") { Description = "Replace the outline (bookmarks) with this JSON file, as metadata show --outline --json writes it.", HelpName = "file" };
        Command command = new("set", "Set document information fields, the XMP packet or the outline (bookmarks).");
        inputs.AddTo(command);
        output.AddTo(command);
        foreach ((_, Option<string?> option) in fields)
        {
            command.Options.Add(option);
        }

        command.Options.Add(created);
        command.Options.Add(modified);
        command.Options.Add(xmp);
        command.Options.Add(outline);
        save.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            Dictionary<string, string?> values = [];
            foreach ((string name, Option<string?> option) in fields)
            {
                if (result.GetResult(option) is not null)
                {
                    values[name] = result.GetValue(option) is { Length: > 0 } value ? value : null;
                }
            }

            DateTimeOffset? createdDate = Date(result.GetValue(created), "--created"), modifiedDate = Date(result.GetValue(modified), "--modified");
            XDocument? packet = result.GetValue(xmp) is { } xmpFile ? XDocument.Load(session.ResolvePath(xmpFile)) : null;
            List<OutlineEntry>? entries = result.GetValue(outline) is { } outlineFile ? ReadOutline(File.ReadAllBytes(session.ResolvePath(outlineFile)), outlineFile) : null;
            if (values.Count == 0 && createdDate is null && modifiedDate is null && packet is null && entries is null)
            {
                throw new UsageException("Nothing to set: give --title, --author, --subject, --keywords, --creator, --producer, --created, --modified, --xmp or --outline.");
            }

            PdfSaveOptions saveOptions = save.Build(result);
            if (modifiedDate is not null)
            {
                saveOptions = saveOptions with { UpdateModificationDate = false };
            }

            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                string path = template is null ? file.Path ?? throw new UsageException("--in-place needs files, not standard input.") : OutputOptions.PathFor(session, template, file, "pdf");
                using PdfDocument document = PdfInput.Open(session, file, secret, inMemory: template is null);
                PdfMetadata info = document.Metadata;
                foreach ((string name, string? value) in values)
                {
                    switch (name)
                    {
                        case "title": info.Title = value; break;
                        case "author": info.Author = value; break;
                        case "subject": info.Subject = value; break;
                        case "keywords": info.Keywords = value; break;
                        case "creator": info.Creator = value; break;
                        default: info.Producer = value; break;
                    }
                }

                if (createdDate is { } c)
                {
                    info.CreationDate = c;
                }

                if (modifiedDate is { } m)
                {
                    info.ModificationDate = m;
                }

                if (packet is not null)
                {
                    info.SetXmp(new XDocument(packet));
                }

                if (entries is not null)
                {
                    WriteOutline(document, entries);
                }

                SaveOptions.Save(session, document, path, saveOptions);
                return [path];
            });
        });
        return command;
    }

    private static Command Strip(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Command command = new("strip", "Remove the document information and every XMP packet, the document's and each object's.");
        inputs.AddTo(command);
        output.AddTo(command);
        save.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            PdfSaveOptions saveOptions = save.Build(result) with { UpdateModificationDate = false };
            PdfSanitizeOptions metadataOnly = new()
            {
                Metadata = true,
                JavaScript = false,
                Attachments = false,
                HiddenLayers = false,
                Comments = false,
                FormData = false,
                Thumbnails = false,
                PrivateData = false,
            };
            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                string path = template is null ? file.Path ?? throw new UsageException("--in-place needs files, not standard input.") : OutputOptions.PathFor(session, template, file, "pdf");
                using PdfDocument document = PdfInput.Open(session, file, secret, inMemory: template is null);
                PdfSanitizeResult removed = document.Sanitize(metadataOnly);
                session.Info($"{file.Display}: removed {removed.Metadata} metadata dictionaries and streams");
                SaveOptions.Save(session, document, path, saveOptions);
                return [path];
            });
        });
        return command;
    }

    /// <summary>One outline item as the outline file holds it.</summary>
    internal sealed record OutlineEntry(string Title, int? Page, bool Bold, bool Italic, bool Open, List<OutlineEntry> Children);

    private static string Text(InputFile file, PdfDocument document, bool withXmp, bool withOutline)
    {
        StringBuilder text = new();
        text.Append(file.Display).Append(":\n");
        foreach ((string key, string value) in Fields(document.Metadata))
        {
            text.Append(CultureInfo.InvariantCulture, $"  {key,-10} {value}\n");
        }

        if (withXmp && document.Metadata.GetXmp() is { } xmp)
        {
            text.Append("  xmp:\n");
            foreach (string line in xmp.ToString().Split('\n'))
            {
                text.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
            }
        }

        if (withOutline)
        {
            void Walk(IReadOnlyList<PdfOutlineItem> items, int depth)
            {
                foreach (PdfOutlineItem item in items)
                {
                    int? page = PageOf(document, item);
                    text.Append(' ', 2 + (2 * depth)).Append(item.Title).Append(page is { } p ? $" (page {p})" : string.Empty).Append('\n');
                    Walk(item.Children, depth + 1);
                }
            }

            text.Append("  outline:\n");
            Walk(document.Outline.Children, 1);
        }

        return text.ToString();
    }

    private static string Json(InputFile file, PdfDocument document, bool withXmp, bool withOutline)
    {
        StringWriter text = new() { NewLine = "\n" };
        JsonOut.Write(text, json =>
        {
            json.WriteStartObject();
            json.WriteString("path", file.Display);
            json.WriteStartObject("info");
            foreach ((string key, string value) in Fields(document.Metadata))
            {
                json.WriteString(key, value);
            }

            json.WriteEndObject();
            if (withXmp)
            {
                json.Text("xmp", document.Metadata.GetXmp()?.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            }

            if (withOutline)
            {
                json.WriteStartObject("outline");
                json.WriteNumber("egap-outline", Schema);
                json.WritePropertyName("items");
                WriteItems(json, document, document.Outline.Children);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        });
        return text.ToString();
    }

    private static void WriteItems(Utf8JsonWriter json, PdfDocument document, IReadOnlyList<PdfOutlineItem> items)
    {
        json.WriteStartArray();
        foreach (PdfOutlineItem item in items)
        {
            json.WriteStartObject();
            json.WriteString("title", item.Title);
            json.Number("page", PageOf(document, item));
            if (item.IsBold)
            {
                json.WriteBoolean("bold", true);
            }

            if (item.IsItalic)
            {
                json.WriteBoolean("italic", true);
            }

            if (item.IsOpen)
            {
                json.WriteBoolean("open", true);
            }

            if (item.Children.Count > 0)
            {
                json.WritePropertyName("children");
                WriteItems(json, document, item.Children);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>The page an item goes to, from 1, or <see langword="null"/> for an item that goes elsewhere.</summary>
    private static int? PageOf(PdfDocument document, PdfOutlineItem item)
    {
        try
        {
            return item.Destination is { } destination && destination.GetPageIndex(document) is int index and >= 0 ? index + 1 : null;
        }
        catch (Exception error) when (error is PdfException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<(string Key, string Value)> Fields(PdfMetadata info)
    {
        foreach ((string key, string? value) in ((string, string?)[])[
            ("title", info.Title), ("author", info.Author), ("subject", info.Subject), ("keywords", info.Keywords),
            ("creator", info.Creator), ("producer", info.Producer),
            ("created", info.CreationDate?.ToString("o", CultureInfo.InvariantCulture)), ("modified", info.ModificationDate?.ToString("o", CultureInfo.InvariantCulture))])
        {
            if (!string.IsNullOrEmpty(value))
            {
                yield return (key, value);
            }
        }
    }

    private static DateTimeOffset? Date(string? text, string option)
    {
        return text is null ? null
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? date
            : throw new UsageException($"{option} '{text}' is not an ISO 8601 date such as 2026-10-22 or 2026-10-22T09:30:00Z.");
    }

    /// <summary>Reads an outline file: the <c>outline</c> object of <c>metadata show --json</c>, or a file's whole report.</summary>
    internal static List<OutlineEntry> ReadOutline(byte[] json, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("files", out JsonElement reportFiles) && reportFiles.GetArrayLength() == 1)
            {
                root = reportFiles[0];
            }

            if (root.TryGetProperty("outline", out JsonElement outline))
            {
                root = outline;
            }

            if (!root.TryGetProperty("egap-outline", out JsonElement version) || version.GetInt32() != Schema || !root.TryGetProperty("items", out JsonElement items))
            {
                throw new UsageException($"{name} is not an outline file: {{ \"egap-outline\": 1, \"items\": [ {{ \"title\": \"Intro\", \"page\": 1 }} ] }}.");
            }

            return Entries(items, name);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw new UsageException($"{name}: {error.Message}");
        }
    }

    private static List<OutlineEntry> Entries(JsonElement items, string name)
    {
        List<OutlineEntry> entries = [];
        foreach (JsonElement item in items.EnumerateArray())
        {
            string title = item.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()!
                : throw new UsageException($"{name}: every outline item needs a title.");
            int? page = item.TryGetProperty("page", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
            bool Flag(string key) => item.TryGetProperty(key, out JsonElement f) && f.ValueKind == JsonValueKind.True;
            List<OutlineEntry> children = item.TryGetProperty("children", out JsonElement c) ? Entries(c, name) : [];
            entries.Add(new OutlineEntry(title, page, Flag("bold"), Flag("italic"), Flag("open"), children));
        }

        return entries;
    }

    private static void WriteOutline(PdfDocument document, List<OutlineEntry> entries)
    {
        foreach (PdfOutlineItem item in document.Outline.Children.ToList())
        {
            item.Remove();
        }

        void Add(PdfOutlineNode parent, List<OutlineEntry> items)
        {
            foreach (OutlineEntry entry in items)
            {
                if (entry.Page is { } page && (page < 1 || page > document.Pages.Count))
                {
                    throw new InvalidOperationException($"The outline item '{entry.Title}' goes to page {page}, but the file has {document.Pages.Count} pages.");
                }

                PdfOutlineItem item = parent.Add(entry.Title, entry.Page is { } p ? PdfDestination.ToPageIndex(p - 1) : null);
                item.IsBold = entry.Bold;
                item.IsItalic = entry.Italic;
                item.IsOpen = entry.Open;
                Add(item, entry.Children);
            }
        }

        Add(document.Outline, entries);
    }
}
