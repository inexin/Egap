using System.CommandLine;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary><c>egap form list</c>, <c>fill</c>, <c>flatten</c> and <c>reset</c>: the AcroForm fields of PDFs.</summary>
internal static class FormCommands
{
    /// <summary>The version of the <c>form list --json</c> schema, docs/schemas/egap-form-1.json.</summary>
    public const int Schema = 1;

    public static Command Create(EgapSession session)
    {
        Command command = new("form", "List, fill, flatten and reset the form fields of PDFs.");
        command.Subcommands.Add(List(session));
        command.Subcommands.Add(new FillEdit().CreateCommand(session, "fill"));
        command.Subcommands.Add(new FlattenEdit().CreateCommand(session, "flatten"));
        command.Subcommands.Add(new ResetEdit().CreateCommand(session, "reset"));
        return command;
    }

    private static Command List(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-form-1.json); its values can be edited and given to form fill." };
        Command command = new("list", "List the form fields: name, kind, value, choices, and whether each is read-only or required.");
        inputs.AddTo(command);
        command.Options.Add(json);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            string? secret = password.Read(session, result);
            List<FieldInfo>?[] found = new List<FieldInfo>?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                found[file.Index] = [.. document.Form.GetTerminalFields().Select(Describe)];
                return [];
            });

            if (result.GetValue(json))
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", Schema);
                    writer.WriteStartArray("files");
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (found[i] is not { } fields)
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteString("path", files[i].Display);
                        writer.WriteStartArray("fields");
                        foreach (FieldInfo field in fields)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", field.Name);
                            writer.WriteString("kind", field.Kind);
                            writer.Text("value", field.Value);
                            writer.WriteStartArray("choices");
                            foreach (string choice in field.Choices)
                            {
                                writer.WriteStringValue(choice);
                            }

                            writer.WriteEndArray();
                            writer.WriteBoolean("readOnly", field.ReadOnly);
                            writer.WriteBoolean("required", field.Required);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (found[i] is not { } fields)
                    {
                        continue;
                    }

                    session.Console.Text.WriteLine($"{files[i].Display}: {fields.Count} field{(fields.Count == 1 ? string.Empty : "s")}");
                    foreach (FieldInfo field in fields)
                    {
                        string flags = (field.ReadOnly ? ", read-only" : string.Empty) + (field.Required ? ", required" : string.Empty);
                        string choices = field.Choices.Count > 0 ? $" [{string.Join(" | ", field.Choices)}]" : string.Empty;
                        session.Console.Text.WriteLine($"  {field.Name} ({field.Kind}{flags}): {field.Value ?? "(empty)"}{choices}");
                    }
                }
            }

            return code;
        });
        return command;
    }

    /// <summary>A field as form list shows it.</summary>
    private sealed record FieldInfo(string Name, string Kind, string? Value, IReadOnlyList<string> Choices, bool ReadOnly, bool Required);

    private static FieldInfo Describe(PdfField field)
    {
        (string kind, string? value, IReadOnlyList<string> choices) = field switch
        {
            PdfTextField text => ("text", text.Value, (IReadOnlyList<string>)[]),
            PdfCheckBoxField box => ("checkbox", box.IsChecked ? "true" : "false", []),
            PdfRadioButtonField radio => ("radio", radio.Value, radio.Options),
            PdfComboBoxField combo => ("combo", combo.Value, [.. combo.Options.Select(o => o.ExportValue)]),
            PdfListBoxField list => ("list", string.Join(", ", list.Values), [.. list.Options.Select(o => o.ExportValue)]),
            PdfSignatureField => ("signature", null, []),
            PdfPushButtonField => ("button", null, []),
            _ => ("other", field.RawValue?.ToString(), []),
        };
        return new FieldInfo(field.FullName, kind, value, choices, field.IsReadOnly, field.IsRequired);
    }
}

/// <summary>
/// <c>egap form fill</c> and the <c>form-fill</c> step: values from a JSON object of field names, as <c>form list --json</c>
/// names them, or from <c>--set name=value</c>; check boxes take true or false, list boxes a list.
/// </summary>
internal sealed class FillEdit : Edit
{
    private readonly Option<string?> _data = new("--data") { Description = "A JSON file of values: { \"name\": \"Ada\", \"subscribe\": true, \"colours\": [ \"red\" ] }, or the output of form list --json.", HelpName = "file" };
    private readonly Option<string[]> _set = new("--set") { Description = "One value, name=value; give it more than once.", HelpName = "name=value", AllowMultipleArgumentsPerToken = false };
    private readonly Option<bool> _flatten = new("--flatten") { Description = "Flatten the form after filling it, so the values become page content." };

    /// <inheritdoc/>
    public override string Name => "form-fill";

    /// <inheritdoc/>
    public override string Description => "Fill form fields from a JSON file or --set name=value; --flatten fixes the values into the page.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        command.Options.Add(_data);
        command.Options.Add(_set);
        command.Options.Add(_flatten);
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        List<(string Name, JsonElement Value)> values = [];
        if (result.GetValue(_data) is { } data)
        {
            values.AddRange(Read(File.ReadAllBytes(session.ResolvePath(data)), data));
        }

        foreach (string text in result.GetValue(_set) ?? [])
        {
            int equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"--set '{text}' is not name=value.");
            }

            values.Add((text[..equals], JsonSerializerString(text[(equals + 1)..])));
        }

        if (values.Count == 0)
        {
            throw new UsageException("Nothing to fill: give --data with a JSON file or --set name=value.");
        }

        bool flatten = result.GetValue(_flatten);
        return (document, context) =>
        {
            foreach ((string name, JsonElement value) in values)
            {
                if (!document.Form.TryGetField(name, out PdfField field))
                {
                    throw new InvalidOperationException($"The form has no field '{name}'; egap form list shows the fields.");
                }

                Fill(field, value, name);
            }

            if (flatten)
            {
                document.Form.Flatten();
            }

            session.Info($"{context.File.Display}: filled {values.Count} fields");
            return document;
        };
    }

    private static void Fill(PdfField field, JsonElement value, string name)
    {
        string? Text() => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => throw new UsageException($"The value for '{name}' is not text, a number, true or false."),
        };

        switch (field)
        {
            case PdfTextField text:
                text.Value = Text();
                break;
            case PdfCheckBoxField box:
                box.IsChecked = value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False or JsonValueKind.Null => false,
                    JsonValueKind.String when bool.TryParse(value.GetString(), out bool on) => on,
                    _ => throw new UsageException($"The check box '{name}' takes true or false."),
                };
                break;
            case PdfRadioButtonField radio:
                radio.Value = Text();
                break;
            case PdfComboBoxField combo:
                combo.Value = Text();
                break;
            case PdfListBoxField list:
                list.Select(value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)] : [Text() ?? string.Empty]);
                break;
            default:
                throw new InvalidOperationException($"The field '{name}' is not one egap fills (text, check box, radio, combo or list).");
        }
    }

    /// <summary>The values of a fill file: a flat object, or the fields of one file of <c>form list --json</c>.</summary>
    internal static List<(string Name, JsonElement Value)> Read(byte[] json, string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            List<(string, JsonElement)> values = [];
            if (root.TryGetProperty("files", out JsonElement files) && files.GetArrayLength() == 1)
            {
                foreach (JsonElement field in files[0].GetProperty("fields").EnumerateArray())
                {
                    if (field.GetProperty("kind").GetString() is "signature" or "button")
                    {
                        continue;
                    }

                    JsonElement value = field.GetProperty("value");
                    values.Add((field.GetProperty("name").GetString()!, field.GetProperty("kind").GetString() == "checkbox" && value.ValueKind == JsonValueKind.String
                        ? (value.GetString() == "true" ? JsonTrue : JsonFalse) : value.Clone()));
                }

                return values;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new UsageException($"{source} is not a JSON object of field names and values.");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                values.Add((property.Name, property.Value.Clone()));
            }

            return values;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new UsageException($"{source}: {error.Message}");
        }
    }

    private static JsonElement JsonTrue { get; } = JsonDocument.Parse("true").RootElement.Clone();

    private static JsonElement JsonFalse { get; } = JsonDocument.Parse("false").RootElement.Clone();

    private static JsonElement JsonSerializerString(string text)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStringValue(text);
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }
}

/// <summary><c>egap form flatten</c> and the <c>form-flatten</c> step: fields become page content and leave the form.</summary>
internal sealed class FlattenEdit : Edit
{
    private readonly Option<string[]> _fields = new("--field") { Description = "Flatten only this field; give it more than once. All fields by default.", HelpName = "name", AllowMultipleArgumentsPerToken = false };

    /// <inheritdoc/>
    public override string Name => "form-flatten";

    /// <inheritdoc/>
    public override string Description => "Draw form fields into the page and remove them from the form, so the values can no longer change.";

    /// <inheritdoc/>
    public override void AddTo(Command command) => command.Options.Add(_fields);

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string[] names = result.GetValue(_fields) ?? [];
        return (document, context) =>
        {
            int flattened = document.Form.Flatten(names.Length == 0 ? null : Fields(document, names));
            session.Info($"{context.File.Display}: flattened {flattened} widgets");
            return document;
        };
    }

    /// <summary>The fields named, or an error naming the first that is missing.</summary>
    internal static List<PdfField> Fields(PdfDocument document, IEnumerable<string> names)
    {
        return [.. names.Select(name => document.Form.TryGetField(name, out PdfField field) ? field
            : throw new InvalidOperationException($"The form has no field '{name}'; egap form list shows the fields."))];
    }
}

/// <summary><c>egap form reset</c> and the <c>form-reset</c> step: fields back to their default values.</summary>
internal sealed class ResetEdit : Edit
{
    private readonly Option<string[]> _fields = new("--field") { Description = "Reset only this field; give it more than once. All fields by default.", HelpName = "name", AllowMultipleArgumentsPerToken = false };

    /// <inheritdoc/>
    public override string Name => "form-reset";

    /// <inheritdoc/>
    public override string Description => "Set form fields back to their default values.";

    /// <inheritdoc/>
    public override void AddTo(Command command) => command.Options.Add(_fields);

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string[] names = result.GetValue(_fields) ?? [];
        return (document, _) =>
        {
            document.Form.Reset(names.Length == 0 ? null : FlattenEdit.Fields(document, names));
            return document;
        };
    }
}
