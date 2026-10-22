using System.CommandLine;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary><c>egap encrypt</c> and <c>egap decrypt</c>: AES-256 with user and owner passwords and permissions.</summary>
internal static class EncryptCommands
{
    /// <summary>The permission names, as <c>info</c> shows them.</summary>
    private static readonly (string Name, PdfPermissions Flag)[] Permissions =
    [
        ("print", PdfPermissions.Print), ("print-high-quality", PdfPermissions.PrintHighQuality), ("modify", PdfPermissions.Modify),
        ("assemble", PdfPermissions.Assemble), ("copy-content", PdfPermissions.CopyContent),
        ("extract-for-accessibility", PdfPermissions.ExtractForAccessibility), ("annotate", PdfPermissions.Annotate), ("fill-forms", PdfPermissions.FillForms),
    ];

    public static Command Encrypt(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        SecretOptions user = new("user-password", "password that opens the file (empty lets anyone open it)");
        SecretOptions owner = new("owner-password", "password that unlocks every permission");
        SecretOptions password = new("password", "password that opens inputs that are already encrypted");
        Option<string?> allow = new("--allow")
        {
            Description = $"What someone without the owner password may do: all (the default), none, or a list of {string.Join(", ", Permissions.Select(p => p.Name))}.",
            HelpName = "permissions",
        };
        SaveOptions save = new();
        Command command = new("encrypt", "Encrypt PDFs with AES-256: a user password to open them, an owner password for full rights, and permissions.");
        inputs.AddTo(command);
        output.AddTo(command);
        user.AddTo(command);
        owner.AddTo(command);
        command.Options.Add(allow);
        save.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            string userPassword = user.Read(session, result) ?? SecretOptions.Prompt(session, "User password (empty for none)") ?? string.Empty;
            string ownerPassword = owner.Read(session, result) ?? SecretOptions.Prompt(session, "Owner password")
                ?? throw new UsageException("encrypt needs an owner password: --owner-password-env, --owner-password-file or --owner-password.");
            if (ownerPassword.Length == 0)
            {
                throw new UsageException("The owner password must not be empty; anyone could then lift the permissions.");
            }

            PdfSaveOptions options = save.Build(result) with { Encryption = PdfEncryption.Aes256(userPassword, ownerPassword, ParsePermissions(result.GetValue(allow))) };
            string? secret = password.Read(session, result);
            return Run(session, files, batch, template, secret, options);
        });
        return command;
    }

    public static Command Decrypt(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        SecretOptions password = new("password", "owner password, or the user password when the file allows changes");
        SaveOptions save = new();
        Command command = new("decrypt", "Remove the encryption from PDFs, given their password.");
        inputs.AddTo(command);
        output.AddTo(command);
        password.AddTo(command);
        save.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            PdfSaveOptions options = save.Build(result) with { Encryption = PdfEncryption.None };
            return Run(session, files, batch, template, password.Read(session, result), options);
        });
        return command;
    }

    /// <summary>A permission list: <c>all</c>, <c>none</c>, or names joined by commas.</summary>
    internal static PdfPermissions ParsePermissions(string? text)
    {
        if (text is null || text == "all")
        {
            return PdfPermissions.All;
        }

        PdfPermissions permissions = PdfPermissions.None;
        foreach (string name in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (name == "none")
            {
                continue;
            }

            (string Name, PdfPermissions Flag) found = Permissions.FirstOrDefault(p => p.Name == name);
            permissions |= found.Name is not null ? found.Flag
                : throw new UsageException($"--allow: '{name}' is not a permission; they are all, none, {string.Join(", ", Permissions.Select(p => p.Name))}.");
        }

        return permissions;
    }

    private static int Run(EgapSession session, List<InputFile> files, BatchOptions batch, OutputTemplate? template, string? password, PdfSaveOptions options)
    {
        return Batch.Run(session, files, batch, file =>
        {
            string path = template is null ? file.Path ?? throw new UsageException("--in-place needs files, not standard input.") : OutputOptions.PathFor(session, template, file, "pdf");
            using PdfDocument document = PdfInput.Open(session, file, password, inMemory: template is null);
            if (document.IsEncrypted && !document.IsOwner && options.Encryption.Kind == PdfEncryptionKind.None)
            {
                session.Warn($"{file.Display}: opened with the user password; the file's permissions no longer apply once it is decrypted.");
            }

            SaveOptions.Save(session, document, path, options);
            return [path];
        });
    }
}
