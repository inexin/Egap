using System.CommandLine;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>The permissions of <c>egap encrypt</c>.</summary>
internal static class EncryptCommands
{
    /// <summary>The permission names, as <c>info</c> shows them.</summary>
    private static readonly (string Name, PdfPermissions Flag)[] Permissions =
    [
        ("print", PdfPermissions.Print), ("print-high-quality", PdfPermissions.PrintHighQuality), ("modify", PdfPermissions.Modify),
        ("assemble", PdfPermissions.Assemble), ("copy-content", PdfPermissions.CopyContent),
        ("extract-for-accessibility", PdfPermissions.ExtractForAccessibility), ("annotate", PdfPermissions.Annotate), ("fill-forms", PdfPermissions.FillForms),
    ];

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
}

/// <summary><c>egap encrypt</c> and the <c>encrypt</c> step: AES-256 with user and owner passwords and permissions for every later save.</summary>
internal sealed class EncryptEdit : Edit
{
    private readonly SecretOptions _user = new("user-password", "password that opens the file (empty lets anyone open it)");
    private readonly SecretOptions _owner = new("owner-password", "password that unlocks every permission");
    private readonly Option<string?> _allow = new("--allow")
    {
        Description = "What someone without the owner password may do: all (the default), none, or a list of print, print-high-quality, modify, assemble, copy-content, extract-for-accessibility, annotate, fill-forms.",
        HelpName = "permissions",
    };

    /// <inheritdoc/>
    public override string Name => "encrypt";

    /// <inheritdoc/>
    public override string Description => "Encrypt with AES-256: a user password to open the file, an owner password for full rights, and permissions.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        _user.AddTo(command);
        _owner.AddTo(command);
        command.Options.Add(_allow);
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string user = _user.Read(session, result) ?? SecretOptions.Prompt(session, "User password (empty for none)") ?? string.Empty;
        string owner = _owner.Read(session, result) ?? SecretOptions.Prompt(session, "Owner password")
            ?? throw new UsageException("encrypt needs an owner password: --owner-password-env, --owner-password-file or --owner-password.");
        if (owner.Length == 0)
        {
            throw new UsageException("The owner password must not be empty; anyone could then lift the permissions.");
        }

        PdfEncryption encryption = PdfEncryption.Aes256(user, owner, EncryptCommands.ParsePermissions(result.GetValue(_allow)));
        return (document, context) =>
        {
            context.Encryption = encryption;
            return document;
        };
    }
}

/// <summary><c>egap decrypt</c> and the <c>decrypt</c> step: every later save writes the file without encryption.</summary>
internal sealed class DecryptEdit : Edit
{
    /// <inheritdoc/>
    public override string Name => "decrypt";

    /// <inheritdoc/>
    public override string Description => "Remove the encryption, given the owner password (or the user password when the file allows changes).";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        return (document, context) =>
        {
            if (document.IsEncrypted && !document.IsOwner)
            {
                session.Warn($"{context.File.Display}: opened with the user password; the file's permissions no longer apply once it is decrypted.");
            }

            context.Encryption = PdfEncryption.None;
            return document;
        };
    }
}
