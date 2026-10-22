using System.CommandLine;
using System.Text;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary><c>egap text</c>: the text of chosen pages, in reading order, or as lines and words with their boxes.</summary>
internal static class TextCommand
{
    /// <summary>The version of the <c>text --json</c> schema, docs/schemas/egap-text-1.json.</summary>
    public const int Schema = 1;

    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new(inPlace: false);
        PageOption pages = new("The pages to read");
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<bool> json = new("--json") { Description = $"Write lines and words with their boxes in points as JSON (schema {Schema}, docs/schemas/egap-text-1.json)." };
        Option<string?> order = new("--order") { Description = "position (reading order, the default) or content (the order the page draws text in).", HelpName = "position|content" };
        Command command = new("text", "Write the text of PDF pages: to standard output, or with -o to a file per input. Pages are separated by a form feed.");
        inputs.AddTo(command);
        output.AddTo(command);
        command.Options.Add(pages.Pages);
        command.Options.Add(order);
        command.Options.Add(json);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files, required: false);
            PdfPageRange? range = pages.Range(result);
            string? secret = password.Read(session, result);
            bool asJson = result.GetValue(json);
            PdfTextOptions options = new()
            {
                Order = result.GetValue(order) switch
                {
                    null or "position" => PdfTextOrder.Position,
                    "content" => PdfTextOrder.Content,
                    string other => throw new UsageException($"--order '{other}' is not position or content."),
                },
            };
            string?[] texts = new string?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                IReadOnlyList<int> indices = PageOption.Indices(document, range);
                string text = asJson ? Json(file, document, indices, options) : Text(document, indices, options);
                if (template is null)
                {
                    texts[file.Index] = text;
                    return [];
                }

                string path = OutputOptions.PathFor(session, template, file, asJson ? "json" : "txt");
                Output.Write(session, path, stream => stream.Write(Encoding.UTF8.GetBytes(text)));
                return [path];
            });

            foreach (string? text in texts)
            {
                if (text is not null)
                {
                    session.Console.Text.Write(text);
                }
            }

            return code;
        });
        return command;
    }

    /// <summary>The pages' text as <see cref="PdfPage.ExtractText"/> gives it, a form feed between pages, ending in a newline.</summary>
    internal static string Text(PdfDocument document, IReadOnlyList<int> indices, PdfTextOptions options)
    {
        StringBuilder text = new();
        for (int i = 0; i < indices.Count; i++)
        {
            if (i > 0)
            {
                text.Append('\f');
            }

            string page = document.Pages[indices[i]].ExtractText(options);
            text.Append(page);
            if (page.Length > 0 && page[^1] != '\n')
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static string Json(InputFile file, PdfDocument document, IReadOnlyList<int> indices, PdfTextOptions options)
    {
        StringWriter text = new() { NewLine = "\n" };
        JsonOut.Write(text, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("schema", Schema);
            json.WriteString("path", file.Display);
            json.WriteStartArray("pages");
            foreach (int index in indices)
            {
                PdfPage page = document.Pages[index];
                PdfTextPage words = page.GetText(options);
                json.WriteStartObject();
                json.WriteNumber("page", index + 1);
                Box(json, "box", page.CropBox);
                json.WriteStartArray("lines");
                foreach (PdfTextLine line in words.Lines)
                {
                    json.WriteStartObject();
                    json.WriteString("text", line.Text);
                    Box(json, "box", line.Box);
                    json.WriteStartArray("words");
                    foreach (PdfTextWord word in line.Words)
                    {
                        json.WriteStartObject();
                        json.WriteString("text", word.Text);
                        Box(json, "box", word.Box);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
        return text.ToString();
    }

    /// <summary>A box in points as [left, bottom, right, top], rounded to a hundredth.</summary>
    private static void Box(Utf8JsonWriter json, string name, PdfRectangle box)
    {
        json.WriteStartArray(name);
        json.WriteNumberValue(Math.Round(box.Left, 2));
        json.WriteNumberValue(Math.Round(box.Bottom, 2));
        json.WriteNumberValue(Math.Round(box.Left + box.Width, 2));
        json.WriteNumberValue(Math.Round(box.Bottom + box.Height, 2));
        json.WriteEndArray();
    }
}
