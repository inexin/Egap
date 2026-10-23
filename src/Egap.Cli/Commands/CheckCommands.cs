using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap verify</c>: every signature, its chain, timestamp and revocation, and what changed after it; and <c>egap pdfa
/// check</c> and <c>convert</c>. Both exit with 1 when a check fails, and write JSON with egap-verify-1.json and
/// egap-pdfa-1.json.
/// </summary>
internal static class CheckCommands
{
    /// <summary>The version of the <c>verify --json</c> and <c>pdfa check --json</c> schemas.</summary>
    public const int Schema = 1;

    public static Command Verify(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<string[]> trust = new("--trust") { Description = "A root certificate to trust, PEM or DER; give it more than once.", HelpName = "file", AllowMultipleArgumentsPerToken = false };
        Option<bool> systemTrust = new("--system-trust") { Description = "Also trust the operating system's root certificates." };
        Option<string[]> extra = new("--extra") { Description = "A certificate that may complete a chain the file leaves out; give it more than once.", HelpName = "file", AllowMultipleArgumentsPerToken = false };
        Option<bool> online = new("--online") { Description = "Fetch OCSP responses and CRLs the file does not carry (network)." };
        Option<string?> at = new("--at") { Description = "Check as of this time instead of now, ISO 8601.", HelpName = "time" };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-verify-1.json)." };
        Command command = new("verify", "Check every signature of PDFs: integrity, chain, timestamp, revocation and what changed after it; exit code 1 when one fails or a file has none.");
        inputs.AddTo(command);
        foreach (Option option in (Option[])[trust, systemTrust, extra, online, at, json])
        {
            command.Options.Add(option);
        }

        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            string? secret = password.Read(session, result);
            using HttpClient? http = result.GetValue(online) ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : null;
            PdfVerifyOptions options = PdfVerifyOptions.Default with
            {
                TrustAnchors = [.. (result.GetValue(trust) ?? []).Select(f => X509CertificateLoader.LoadCertificateFromFile(session.ResolvePath(f)))],
                ExtraCertificates = [.. (result.GetValue(extra) ?? []).Select(f => X509CertificateLoader.LoadCertificateFromFile(session.ResolvePath(f)))],
                UseSystemTrust = result.GetValue(systemTrust),
                Revocation = http is null ? PdfVerifyOptions.Default.Revocation : PdfRevocationSource.Online(http),
                VerificationTime = result.GetValue(at) is { } time ? MetadataCommand.Date(time, "--at") : null,
            };
            List<PdfSignatureReport>?[] reports = new List<PdfSignatureReport>?[files.Count];
            bool failed = false;
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                List<PdfSignatureReport> checks = [.. document.Signatures.Select(s => s.Verify(options))];
                reports[file.Index] = checks;
                if (checks.Count == 0 || checks.Any(c => !c.IsValid))
                {
                    failed = true;
                }

                return [];
            });

            if (result.GetValue(json))
            {
                WriteJson(session.Console.Text, files, reports);
            }
            else
            {
                WriteText(session.Console.Text, files, reports);
            }

            return code != ExitCodes.Success ? code : failed ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    public static Command Pdfa(EgapSession session)
    {
        Command command = new("pdfa", "Check PDFs against PDF/A or PDF/UA, or convert them to PDF/A.");
        command.Subcommands.Add(PdfaCheck(session));
        command.Subcommands.Add(new PdfaEdit().CreateCommand(session, "convert"));
        return command;
    }

    private static Command PdfaCheck(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<string?> level = new("--level") { Description = "The PDF/A level to check against: 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 or 4f; the file's claim by default.", HelpName = "level" };
        Option<bool> ua = new("--ua") { Description = "Check PDF/UA (accessibility) instead of PDF/A." };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-pdfa-1.json)." };
        Command command = new("check", "Check PDFs against PDF/A or, with --ua, PDF/UA; exit code 1 when a clause is not met.");
        inputs.AddTo(command);
        command.Options.Add(level);
        command.Options.Add(ua);
        command.Options.Add(json);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            PdfAConformance? wanted = result.GetValue(level) is { } l ? ConvertCommand.Conformance(l) : null;
            bool accessibility = result.GetValue(ua);
            if (accessibility && wanted is not null)
            {
                throw new UsageException("Give --level or --ua, not both.");
            }

            string? secret = password.Read(session, result);
            (string Level, PdfAReport Report)?[] reports = new (string, PdfAReport)?[files.Count];
            bool failed = false;
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                PdfAConformance conformance = wanted ?? (document.Conformance != PdfAConformance.None ? document.Conformance
                    : accessibility ? PdfAConformance.None : throw new InvalidOperationException("The file claims no PDF/A level; give --level to check against one."));
                PdfAReport report = accessibility ? PdfAConverter.CheckUniversalAccessibility(document) : PdfAConverter.Check(document, conformance);
                reports[file.Index] = (accessibility ? "PDF/UA-1" : Level(conformance), report);
                if (report.Unmet.Any())
                {
                    failed = true;
                }

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
                        if (reports[i] is not ({ } name, { } report))
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteString("path", files[i].Display);
                        writer.WriteString("level", name);
                        writer.WriteBoolean("valid", !report.Unmet.Any());
                        writer.WriteStartArray("issues");
                        foreach (PdfAIssue issue in report.Issues)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("clause", issue.Clause);
                            writer.WriteString("description", issue.Description);
                            writer.Number("page", issue.Page is int p ? p + 1 : null);
                            writer.WriteBoolean("fixable", issue.Fixable);
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
                    if (reports[i] is not ({ } name, { } report))
                    {
                        continue;
                    }

                    List<PdfAIssue> unmet = [.. report.Unmet];
                    session.Console.Text.WriteLine($"{files[i].Display}: {(unmet.Count == 0 ? "meets" : "does not meet")} {name}{(unmet.Count == 0 ? string.Empty : $" ({unmet.Count} issue{(unmet.Count == 1 ? string.Empty : "s")})")}");
                    foreach (PdfAIssue issue in unmet)
                    {
                        session.Console.Text.WriteLine($"  {issue.Clause}{(issue.Page is int p ? $" (page {p + 1})" : string.Empty)}: {issue.Description}{(issue.Fixable ? " [pdfa convert fixes it]" : string.Empty)}");
                    }
                }
            }

            return code != ExitCodes.Success ? code : failed ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    /// <summary>PDF/A-2b for <see cref="PdfAConformance.PdfA2b"/>.</summary>
    internal static string Level(PdfAConformance conformance) => "PDF/A-" + conformance.ToString()["PdfA".Length..];

    private static string Verdict(PdfVerdict verdict) => InfoCommand.Kebab(verdict.ToString());

    private static void WriteText(TextWriter output, List<InputFile> files, List<PdfSignatureReport>?[] reports)
    {
        for (int i = 0; i < files.Count; i++)
        {
            if (reports[i] is not { } checks)
            {
                continue;
            }

            if (checks.Count == 0)
            {
                output.WriteLine($"{files[i].Display}: no signatures");
                continue;
            }

            output.WriteLine($"{files[i].Display}: {checks.Count(c => c.IsValid)} of {checks.Count} signature{(checks.Count == 1 ? string.Empty : "s")} valid");
            foreach (PdfSignatureReport check in checks)
            {
                string signer = check.SignerCertificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? check.Signature.SignerName ?? "unknown signer";
                output.WriteLine($"  {check.Signature.Name}: {(check.IsValid ? "valid" : "INVALID")}, {InfoCommand.Kebab(check.Signature.Kind.ToString())} by {signer}");
                output.WriteLine($"    integrity {Verdict(check.Integrity)}, chain {Verdict(check.Chain)}, timestamp {Verdict(check.Timestamp)}, revocation {Verdict(check.Revocation)}, changes since {Verdict(check.ChangesSince)}");
                foreach (string problem in check.Problems)
                {
                    output.WriteLine($"    problem: {problem}");
                }

                foreach (string warning in check.Warnings)
                {
                    output.WriteLine($"    warning: {warning}");
                }
            }
        }
    }

    private static void WriteJson(TextWriter output, List<InputFile> files, List<PdfSignatureReport>?[] reports)
    {
        JsonOut.Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("schema", Schema);
            json.WriteStartArray("files");
            for (int i = 0; i < files.Count; i++)
            {
                if (reports[i] is not { } checks)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("path", files[i].Display);
                json.WriteBoolean("valid", checks.Count > 0 && checks.All(c => c.IsValid));
                json.WriteStartArray("signatures");
                foreach (PdfSignatureReport check in checks)
                {
                    WriteSignature(json, check);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    private static void WriteSignature(Utf8JsonWriter json, PdfSignatureReport check)
    {
        json.WriteStartObject();
        json.WriteString("field", check.Signature.Name);
        json.WriteString("kind", InfoCommand.Kebab(check.Signature.Kind.ToString()));
        json.WriteBoolean("valid", check.IsValid);
        json.WriteString("integrity", Verdict(check.Integrity));
        json.WriteString("chain", Verdict(check.Chain));
        json.WriteString("timestamp", Verdict(check.Timestamp));
        json.WriteString("revocation", Verdict(check.Revocation));
        json.WriteString("changesSince", Verdict(check.ChangesSince));
        json.Text("signer", check.SignerCertificate?.Subject);
        json.Text("signingTime", check.Signature.SigningTime?.ToString("o", CultureInfo.InvariantCulture));
        json.Text("timestampTime", check.TimestampTime?.ToString("o", CultureInfo.InvariantCulture));
        json.Text("digest", check.DigestAlgorithm);
        json.Text("algorithm", check.SignatureAlgorithm);
        json.WriteBoolean("coversWholeFile", check.Signature.CoversWholeFile);
        json.WriteStartArray("problems");
        foreach (string problem in check.Problems)
        {
            json.WriteStringValue(problem);
        }

        json.WriteEndArray();
        json.WriteStartArray("warnings");
        foreach (string warning in check.Warnings)
        {
            json.WriteStringValue(warning);
        }

        json.WriteEndArray();
        json.WriteStartArray("changes");
        foreach (PdfSignatureChange change in check.Changes)
        {
            json.WriteStartObject();
            json.WriteNumber("revision", change.Revision);
            json.WriteNumber("object", change.ObjectNumber);
            json.WriteString("description", change.Description);
            json.WriteString("verdict", InfoCommand.Kebab(change.Verdict.ToString()));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }
}

/// <summary><c>egap pdfa convert</c> and the <c>pdfa</c> step: fix what PDF/A needs and claim the level.</summary>
internal sealed class PdfaEdit : Edit
{
    private readonly Option<string?> _level = new("--level") { Description = "1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 or 4f; the best the file can meet by default.", HelpName = "level" };
    private readonly Option<string?> _icc = new("--icc") { Description = "The output intent's ICC profile; sRGB by default.", HelpName = "file" };
    private readonly Option<string?> _condition = new("--output-condition") { Description = "The output condition's name, such as FOGRA39.", HelpName = "name" };
    private readonly Option<bool> _substitute = new("--allow-font-substitution") { Description = "Embed a stand-in for a font the file does not embed." };
    private readonly Option<bool> _systemFonts = new("--system-fonts") { Description = "Look for fonts the file does not embed among the system's fonts." };

    /// <inheritdoc/>
    public override string Name => "pdfa";

    /// <inheritdoc/>
    public override string Description => "Convert to PDF/A: embed fonts, add an output intent and XMP, remove what PDF/A forbids; fails the file when a clause cannot be met.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_level, _icc, _condition, _substitute, _systemFonts])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        PdfAConformance? level = result.GetValue(_level) is { } l ? ConvertCommand.Conformance(l) : null;
        PdfAConvertOptions options = PdfAConvertOptions.Default with
        {
            AllowFontSubstitution = result.GetValue(_substitute),
            UseSystemFonts = result.GetValue(_systemFonts),
        };
        if (result.GetValue(_icc) is { } icc)
        {
            options = options with { OutputIntentProfile = File.ReadAllBytes(session.ResolvePath(icc)), OutputCondition = result.GetValue(_condition) ?? options.OutputCondition };
        }
        else if (result.GetValue(_condition) is not null)
        {
            throw new UsageException("--output-condition names the profile given with --icc.");
        }
        return (document, context) =>
        {
            PdfAReport report = PdfAConverter.Convert(document, level ?? PdfAConverter.DefaultFor(document), options);
            List<PdfAIssue> unmet = [.. report.Unmet];
            if (unmet.Count > 0)
            {
                throw new InvalidOperationException($"It cannot meet {CheckCommands.Level(report.Conformance)}: {string.Join("; ", unmet.Take(3).Select(i => $"{i.Clause} {i.Description}"))}{(unmet.Count > 3 ? $" and {unmet.Count - 3} more" : string.Empty)}.");
            }

            context.Rewrite = true;
            session.Info($"{context.File.Display}: {CheckCommands.Level(report.Conformance)}, {report.Fixes.Count()} fixes");
            return document;
        };
    }
}
