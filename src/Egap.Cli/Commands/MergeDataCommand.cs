using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap merge-data</c>: mail merge a DOCX template with JSON or CSV records, into one PDF or DOCX for all of them or
/// one file per record.
/// </summary>
internal static class MergeDataCommand
{
    public static Command Create(EgapSession session)
    {
        Argument<string> template = new("template") { Description = "The DOCX template, with MERGEFIELD fields and regions." };
        Option<string> data = new("--data") { Description = "The records: a JSON array of objects (nested arrays fill regions) or a CSV file with a header row.", HelpName = "file", Required = true };
        Option<string?> output = new("--output", "-o") { Description = "The file to write (.pdf or .docx); with --each, a template such as out/letter-{index}.pdf.", HelpName = "path", Required = true };
        Option<bool> each = new("--each") { Description = "Write one file per record; {index} counts records from 0." };
        Option<string?> nameField = new("--name-field") { Description = "With --each, name each file by this field of its record, in place of {name}.", HelpName = "field" };
        Option<bool> removeEmpty = new("--remove-empty-paragraphs") { Description = "Drop paragraphs left empty by fields without a value." };
        SaveOptions save = new();
        Command command = new("merge-data", "Mail merge a DOCX template with JSON or CSV records, into one PDF or DOCX, or one file per record.");
        command.Arguments.Add(template);
        foreach (Option option in (Option[])[data, output, each, nameField, removeEmpty])
        {
            command.Options.Add(option);
        }

        save.AddTo(command, edits: false);
        command.SetAction(result =>
        {
            string templatePath = session.ResolvePath(result.GetValue(template)!);
            string dataPath = session.ResolvePath(result.GetValue(data)!);
            List<Dictionary<string, object?>> records = Path.GetExtension(dataPath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? ReadCsv(File.ReadAllText(dataPath), result.GetValue(data)!)
                : ReadJson(File.ReadAllBytes(dataPath), result.GetValue(data)!);
            if (records.Count == 0)
            {
                throw new UsageException($"{result.GetValue(data)} holds no records.");
            }

            MailMergeOptions options = new()
            {
                RemoveEmptyParagraphs = result.GetValue(removeEmpty),
                OnWarning = warning => session.Warn(warning.Message),
            };
            OutputTemplate target = OutputTemplate.Parse(result.GetValue(output)!, "page");
            string extension = OutputOptions.ExtensionOf(target) ?? throw new UsageException("-o must end in .pdf or .docx.");
            if (extension is not ("pdf" or "docx"))
            {
                throw new UsageException($"merge-data writes .pdf or .docx, not .{extension}.");
            }

            PdfSaveOptions saveOptions = save.Build(result);
            Document templateDocument = DocxFormat.Load(templatePath);
            IEnumerable<IMergeData> merged = MergeData.From(records.Select(r => (IReadOnlyDictionary<string, object?>)r));
            if (!result.GetValue(each))
            {
                Document all = templateDocument.MailMergeAll(merged, options);
                Write(session, all, session.ResolvePath(target.Text), extension, saveOptions);
                return ExitCodes.Success;
            }

            if (target.IsStdout || !target.IsPerInput)
            {
                throw new UsageException("With --each, give a template that names each record, such as out/letter-{index}.pdf.");
            }

            string? field = result.GetValue(nameField);
            int index = 0;
            foreach (Document one in templateDocument.MailMerge(merged, options))
            {
                session.Cancellation.ThrowIfCancellationRequested();
                string name = field is not null && records[index].TryGetValue(field, out object? value) && value is not null
                    ? Safe(Convert.ToString(value, CultureInfo.InvariantCulture)!) : Path.GetFileNameWithoutExtension(templatePath);
                InputFile record = new(Path.Combine(Path.GetDirectoryName(templatePath)!, name + ".docx"), name, string.Empty, index);
                Write(session, one, session.ResolvePath(target.Resolve(record, extension)), extension, saveOptions);
                index++;
                session.Progress?.Invoke(index, records.Count);
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static void Write(EgapSession session, Document document, string path, string extension, PdfSaveOptions save)
    {
        if (extension == "pdf")
        {
            using PdfDocument pdf = document.ToPdf(PdfExportOptions.Default with { Save = save });
            SaveOptions.Save(session, pdf, path, save);
        }
        else
        {
            SaveOptions.WriteFile(session, path, extension, temporary =>
            {
                foreach (ConversionWarning warning in document.Save(temporary))
                {
                    session.Warn(warning.Message);
                }
            });
        }
    }

    /// <summary>A record's field made safe for a file name.</summary>
    private static string Safe(string text)
    {
        StringBuilder name = new();
        foreach (char c in text)
        {
            name.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c);
        }

        return name.Length == 0 || name.ToString() is "." or ".." ? "_" : name.ToString();
    }

    /// <summary>JSON records: an array of objects; a nested array of objects fills a region of that name.</summary>
    internal static List<Dictionary<string, object?>> ReadJson(byte[] json, string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                return [Record(root)];
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new UsageException($"{source} is not a JSON array of records.");
            }

            return [.. root.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? Record(item) : throw new UsageException($"{source}: every record is an object."))];
        }
        catch (JsonException error)
        {
            throw new UsageException($"{source} is not valid JSON: {error.Message}");
        }
    }

    private static Dictionary<string, object?> Record(JsonElement element)
    {
        Dictionary<string, object?> record = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            record[property.Name] = Value(property.Value);
        }

        return record;
    }

    private static object? Value(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out long whole) ? whole : value.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Object)
                ? value.EnumerateArray().Select(v => (IReadOnlyDictionary<string, object?>)Record(v)).ToList()
                : string.Join(", ", value.EnumerateArray().Select(v => v.ToString())),
            JsonValueKind.Object => Record(value),
            _ => null,
        };
    }

    /// <summary>CSV records (RFC 4180): a header row names the fields; quoted values may hold commas, quotes and line breaks.</summary>
    internal static List<Dictionary<string, object?>> ReadCsv(string text, string source)
    {
        List<List<string>> rows = [];
        List<string> row = [];
        StringBuilder cell = new();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else if (c != '﻿' || i > 0)
            {
                cell.Append(c);
            }
        }

        if (quoted)
        {
            throw new UsageException($"{source}: a quoted value is not closed.");
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        rows.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
        if (rows.Count == 0)
        {
            return [];
        }

        List<string> header = rows[0];
        List<Dictionary<string, object?>> records = [];
        for (int r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count != header.Count)
            {
                throw new UsageException($"{source}: row {r + 1} has {rows[r].Count} values, the header {header.Count}.");
            }

            Dictionary<string, object?> record = new(StringComparer.Ordinal);
            for (int c = 0; c < header.Count; c++)
            {
                record[header[c]] = rows[r][c];
            }

            records.Add(record);
        }

        return records;
    }
}
