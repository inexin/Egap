using System.CommandLine;
using System.Globalization;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap attach list</c>, <c>add</c>, <c>extract</c> and <c>remove</c>: the files embedded in PDFs; and <c>egap invoice
/// embed</c> and <c>read</c>: ZUGFeRD and Factur-X e-invoices.
/// </summary>
internal static class AttachCommands
{
    /// <summary>The relationship names, as PDF/A-3 writes them.</summary>
    private static readonly string[] Relationships = ["unspecified", "source", "data", "alternative", "supplement", "encrypted-payload", "form-data", "schema"];

    public static Command Attach(EgapSession session)
    {
        Command command = new("attach", "List, add, extract and remove the files embedded in PDFs.");
        command.Subcommands.Add(List(session));
        command.Subcommands.Add(new AttachEdit().CreateCommand(session, "add"));
        command.Subcommands.Add(Extract(session));
        command.Subcommands.Add(new DetachEdit().CreateCommand(session, "remove"));
        return command;
    }

    public static Command Invoice(EgapSession session)
    {
        Command command = new("invoice", "Embed or read a ZUGFeRD or Factur-X e-invoice: the XML inside a PDF/A-3.");
        command.Subcommands.Add(new InvoiceEdit().CreateCommand(session, "embed"));
        command.Subcommands.Add(ReadInvoice(session));
        return command;
    }

    /// <summary>A relationship by its name, such as <c>data</c>.</summary>
    internal static PdfAttachmentRelationship Relationship(string text)
    {
        int index = Array.IndexOf(Relationships, text);
        return index >= 0 ? (PdfAttachmentRelationship)index
            : throw new UsageException($"--relationship '{text}' is not one of {string.Join(", ", Relationships)}.");
    }

    private static Command List(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Command command = new("list", "List the embedded files: name, size, type, relationship and description.");
        inputs.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            string? secret = password.Read(session, result);
            string?[] lines = new string?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                System.Text.StringBuilder text = new();
                text.Append(CultureInfo.InvariantCulture, $"{file.Display}: {document.Attachments.Count} attachment{(document.Attachments.Count == 1 ? string.Empty : "s")}\n");
                foreach (PdfAttachment attachment in document.Attachments)
                {
                    string size = attachment.Size is { } bytes ? $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes" : "size unknown";
                    string details = string.Join(", ", ((string?[])[size, attachment.MimeType, attachment.Relationship, attachment.Description]).Where(d => !string.IsNullOrEmpty(d)));
                    text.Append(CultureInfo.InvariantCulture, $"  {attachment.FileName} ({details})\n");
                }

                lines[file.Index] = text.ToString();
                return [];
            });

            foreach (string? text in lines)
            {
                session.Console.Text.Write(text);
            }

            return code;
        });
        return command;
    }

    private static Command Extract(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<string?> output = new("--output", "-o") { Description = "The folder to write into; with several inputs, a template such as out/{name}/.", HelpName = "folder", Required = true };
        Option<string[]> names = new("--name") { Description = "Extract only this attachment; give it more than once.", HelpName = "name", AllowMultipleArgumentsPerToken = false };
        Command command = new("extract", "Write embedded files out, each under its own name, byte for byte.");
        inputs.AddTo(command);
        command.Options.Add(output);
        command.Options.Add(names);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate folder = OutputTemplate.Parse(result.GetValue(output)!, "page");
            if (files.Count > 1 && !folder.IsPerInput)
            {
                throw new UsageException("With several inputs, give a folder template that names each, such as out/{name}/.");
            }

            string[] wanted = result.GetValue(names) ?? [];
            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                string target = session.ResolvePath(folder.Resolve(file, "pdf"));
                List<string> written = [];
                foreach (string name in wanted)
                {
                    if (!document.Attachments.TryGet(name, out _))
                    {
                        throw new InvalidOperationException($"There is no attachment '{name}'; egap attach list shows them.");
                    }
                }

                foreach (PdfAttachment attachment in document.Attachments)
                {
                    if (wanted.Length > 0 && !wanted.Contains(attachment.FileName) && !wanted.Contains(attachment.Name))
                    {
                        continue;
                    }

                    // The name comes from the file, so it may not climb out of the folder.
                    string safe = Path.GetFileName(attachment.FileName.Replace('\\', '/'));
                    if (safe.Length == 0 || safe is "." or "..")
                    {
                        session.Warn($"{file.Display}: skipped an attachment with no usable file name");
                        continue;
                    }

                    byte[] bytes = attachment.GetBytes() ?? throw new InvalidOperationException($"The attachment '{attachment.FileName}' has no data.");
                    string path = Path.Combine(target, safe);
                    Output.Write(session, path, stream => stream.Write(bytes));
                    written.Add(path);
                }

                return written;
            });
        });
        return command;
    }

    private static Command ReadInvoice(EgapSession session)
    {
        Argument<string> input = new("input") { Description = "The PDF." };
        Option<string?> output = new("--output", "-o") { Description = "Write the XML to this file instead of standard output.", HelpName = "file" };
        SecretOptions password = new("password", "password that opens an encrypted input");
        Command command = new("read", "Write the e-invoice XML a PDF carries, and say which standard and profile it follows.");
        command.Arguments.Add(input);
        command.Options.Add(output);
        password.AddTo(command);
        command.SetAction(result =>
        {
            List<InputFile> files = Globber.Expand(session, [result.GetValue(input)!], recursive: false);
            if (files.Count != 1)
            {
                throw new UsageException("invoice read takes one file.");
            }

            using PdfDocument document = PdfInput.Open(session, files[0], password.Read(session, result));
            PdfInvoice invoice = PdfInvoice.Read(document) ?? throw new InvalidOperationException($"{files[0].Display} carries no ZUGFeRD or Factur-X invoice.");
            session.Info($"{files[0].Display}: {invoice.Standard}{(invoice.Profile is { } profile ? $" {profile}" : string.Empty)}, {invoice.FileName}");
            string path = result.GetValue(output) is { } o ? session.ResolvePath(o) : "-";
            Output.Write(session, path, stream => stream.Write(invoice.Xml));
            return ExitCodes.Success;
        });
        return command;
    }
}

/// <summary><c>egap attach add</c> and the <c>attach</c> step: embed files, optionally as PDF/A-3 associated files.</summary>
internal sealed class AttachEdit : Edit
{
    private readonly Option<string[]> _files = new("--file") { Description = "A file to embed; give it more than once.", HelpName = "path", AllowMultipleArgumentsPerToken = false, Required = true };
    private readonly Option<string?> _name = new("--name") { Description = "The name to embed a single file under; its own name by default.", HelpName = "name" };
    private readonly Option<string?> _description = new("--description") { Description = "A description shown by viewers.", HelpName = "text" };
    private readonly Option<string?> _mime = new("--mime-type") { Description = "The file's media type, such as text/csv.", HelpName = "type" };
    private readonly Option<string?> _relationship = new("--relationship") { Description = "Make it a PDF/A-3 associated file: source, data, alternative, supplement, encrypted-payload, form-data, schema or unspecified.", HelpName = "kind" };

    /// <inheritdoc/>
    public override string Name => "attach";

    /// <inheritdoc/>
    public override string Description => "Embed files in PDFs; --relationship makes them PDF/A-3 associated files.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_files, _name, _description, _mime, _relationship])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string[] files = result.GetValue(_files) ?? [];
        string? name = result.GetValue(_name);
        if (name is not null && files.Length != 1)
        {
            throw new UsageException("--name names one file; give --file once with it.");
        }

        List<(string Name, byte[] Bytes, DateTimeOffset Modified)> attachments = [.. files.Select(file =>
        {
            string full = session.ResolvePath(file);
            return (name ?? Path.GetFileName(full), File.ReadAllBytes(full), new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero));
        })];
        string? description = result.GetValue(_description), mime = result.GetValue(_mime);
        PdfAttachmentRelationship? relationship = result.GetValue(_relationship) is { } r ? AttachCommands.Relationship(r) : null;
        return (document, _) =>
        {
            foreach ((string attachmentName, byte[] bytes, DateTimeOffset modified) in attachments)
            {
                if (relationship is { } kind)
                {
                    document.Attachments.Add(attachmentName, bytes, kind, mime, description, modified);
                }
                else
                {
                    document.Attachments.Add(attachmentName, bytes, mime, description, modified);
                }
            }

            return document;
        };
    }
}

/// <summary><c>egap attach remove</c> and the <c>detach</c> step: remove embedded files by name.</summary>
internal sealed class DetachEdit : Edit
{
    private readonly Option<string[]> _names = new("--name") { Description = "The attachment to remove; give it more than once.", HelpName = "name", AllowMultipleArgumentsPerToken = false, Required = true };

    /// <inheritdoc/>
    public override string Name => "detach";

    /// <inheritdoc/>
    public override string Description => "Remove embedded files by name.";

    /// <inheritdoc/>
    public override void AddTo(Command command) => command.Options.Add(_names);

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string[] names = result.GetValue(_names) ?? [];
        return (document, _) =>
        {
            foreach (string name in names)
            {
                if (!document.Attachments.Remove(name))
                {
                    throw new InvalidOperationException($"There is no attachment '{name}'; egap attach list shows them.");
                }
            }

            return document;
        };
    }
}

/// <summary><c>egap invoice embed</c> and the <c>invoice</c> step: a ZUGFeRD or Factur-X XML, with the PDF/A-3 metadata it needs.</summary>
internal sealed class InvoiceEdit : Edit
{
    private readonly Option<string> _xml = new("--xml") { Description = "The invoice XML (Cross Industry Invoice).", HelpName = "file", Required = true };
    private readonly Option<string?> _standard = new("--standard") { Description = "factur-x (the default), zugferd20 or zugferd10.", HelpName = "standard" };
    private readonly Option<string?> _profile = new("--profile") { Description = "minimum, basic-wl, basic, en16931, extended or xrechnung; read from the XML by default.", HelpName = "profile" };
    private readonly Option<string?> _pdfa = new("--pdfa") { Description = "The PDF/A-3 level to claim: 3b (the default), 3u or 3a.", HelpName = "level" };

    /// <inheritdoc/>
    public override string Name => "invoice";

    /// <inheritdoc/>
    public override string Description => "Embed a ZUGFeRD or Factur-X e-invoice XML and the PDF/A-3 metadata it needs.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        command.Options.Add(_xml);
        command.Options.Add(_standard);
        command.Options.Add(_profile);
        command.Options.Add(_pdfa);
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        byte[] xml = File.ReadAllBytes(session.ResolvePath(result.GetValue(_xml)!));
        PdfInvoiceOptions options = PdfInvoiceOptions.Default with
        {
            Standard = result.GetValue(_standard) switch
            {
                null or "factur-x" => PdfInvoiceStandard.FacturX,
                "zugferd20" => PdfInvoiceStandard.Zugferd20,
                "zugferd10" => PdfInvoiceStandard.Zugferd10,
                string other => throw new UsageException($"--standard '{other}' is not factur-x, zugferd20 or zugferd10."),
            },
            Profile = result.GetValue(_profile) switch
            {
                null => null,
                "minimum" => PdfInvoiceProfile.Minimum,
                "basic-wl" => PdfInvoiceProfile.BasicWL,
                "basic" => PdfInvoiceProfile.Basic,
                "en16931" => PdfInvoiceProfile.EN16931,
                "extended" => PdfInvoiceProfile.Extended,
                "xrechnung" => PdfInvoiceProfile.XRechnung,
                string other => throw new UsageException($"--profile '{other}' is not minimum, basic-wl, basic, en16931, extended or xrechnung."),
            },
        };
        if (result.GetValue(_pdfa) is { } level)
        {
            options = options with { Conformance = ConvertCommand.Conformance(level) };
        }

        return (document, context) =>
        {
            PdfAReport report = PdfInvoice.Embed(document, xml, options);
            foreach (PdfAIssue issue in report.Unmet)
            {
                session.Warn($"{context.File.Display}: PDF/A clause {issue.Clause} is not met: {issue.Description}");
            }

            return document;
        };
    }
}
