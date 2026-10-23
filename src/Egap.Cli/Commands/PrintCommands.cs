using System.CommandLine;
using System.Globalization;
using System.Text.Json;

using Egami;
using Egami.Colors;
using Egami.Pixels;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// The print production commands (CLI design Q17, Phase 8): <c>preflight</c>, <c>pdfx check</c> and <c>convert</c>,
/// <c>separations</c> and <c>color</c>.
/// </summary>
internal static class PrintCommands
{
    /// <summary>The version of the <c>preflight --json</c>, <c>pdfx check --json</c> and <c>separations --json</c> schemas.</summary>
    public const int Schema = 1;

    private static readonly (string Name, PdfXConformance Level)[] Levels =
    [
        ("x-1a", PdfXConformance.PdfX1a), ("x-3", PdfXConformance.PdfX3), ("x-4", PdfXConformance.PdfX4), ("x-4p", PdfXConformance.PdfX4p),
        ("x-5g", PdfXConformance.PdfX5g), ("x-5pg", PdfXConformance.PdfX5pg), ("x-5n", PdfXConformance.PdfX5n),
        ("x-6", PdfXConformance.PdfX6), ("x-6p", PdfXConformance.PdfX6p), ("x-6n", PdfXConformance.PdfX6n),
    ];

    public static Command Preflight(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<string?> profile = new("--profile") { Description = "The checks: pdf-x4 (the default), sheetfed-coated, web-offset or newspaper.", HelpName = "profile" };
        Option<double?> maxInk = new("--max-ink") { Description = "The most total ink coverage allowed, in percent, such as 300.", HelpName = "percent" };
        Option<double?> minDpi = new("--min-image-dpi") { Description = "The lowest image resolution allowed.", HelpName = "dpi" };
        Option<string?> minLine = new("--min-line-width") { Description = "The thinnest line allowed, such as 0.25pt.", HelpName = "length" };
        Option<string?> bleed = new("--bleed") { Description = "The bleed every page needs, such as 3mm.", HelpName = "length" };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-preflight-1.json)." };
        Command command = new("preflight", "Check PDFs for print: output intent, color spaces, image resolution, fonts, transparency, overprint, hairlines, bleed and ink coverage; exit code 1 when a check fails.");
        inputs.AddTo(command);
        foreach (Option option in (Option[])[profile, maxInk, minDpi, minLine, bleed, json])
        {
            command.Options.Add(option);
        }

        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            PdfPrintProfile checks = result.GetValue(profile) switch
            {
                null or "pdf-x4" => PdfPrintProfile.PdfX4,
                "sheetfed-coated" => PdfPrintProfile.SheetfedCoated,
                "web-offset" => PdfPrintProfile.WebOffset,
                "newspaper" => PdfPrintProfile.Newspaper,
                string other => throw new UsageException($"--profile '{other}' is not pdf-x4, sheetfed-coated, web-offset or newspaper."),
            };
            checks = checks with
            {
                MaxInkCoverage = result.GetValue(maxInk) ?? checks.MaxInkCoverage,
                MinImageDpi = result.GetValue(minDpi) ?? checks.MinImageDpi,
                MinLineWidth = result.GetValue(minLine) is { } line ? Values.Points(line, "--min-line-width") : checks.MinLineWidth,
                RequiredBleed = result.GetValue(bleed) is { } b ? Values.Points(b, "--bleed") : checks.RequiredBleed,
            };
            string? secret = password.Read(session, result);
            PdfPreflightReport?[] reports = new PdfPreflightReport?[files.Count];
            bool failed = false;
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                PdfPreflightReport report = PdfPreflight.Run(document, checks, session.Cancellation);
                reports[file.Index] = report;
                failed |= !report.Passed;
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
                        if (reports[i] is not { } report)
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteString("path", files[i].Display);
                        writer.WriteString("profile", report.Profile.Name);
                        writer.WriteBoolean("passed", report.Passed);
                        writer.WriteStartArray("spots");
                        foreach (string spot in report.Spots)
                        {
                            writer.WriteStringValue(spot);
                        }

                        writer.WriteEndArray();
                        writer.WriteStartArray("issues");
                        foreach (PdfAIssue issue in report.Issues)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("check", issue.Clause);
                            writer.WriteString("description", issue.Description);
                            writer.Number("page", issue.Page is int p ? p + 1 : null);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteStartArray("items");
                        foreach (PdfPreflightItem item in report.Items)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("kind", InfoCommand.Kebab(item.Kind.ToString()));
                            writer.Number("page", item.Page is int p ? p + 1 : null);
                            writer.WriteString("description", item.Description);
                            writer.WriteNumber("count", item.Count);
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
                    if (reports[i] is not { } report)
                    {
                        continue;
                    }

                    session.Console.Text.WriteLine($"{files[i].Display}: {(report.Passed ? "passed" : "failed")} {report.Profile.Name}{(report.Spots.Count > 0 ? $", spot colors {string.Join(", ", report.Spots)}" : string.Empty)}");
                    foreach (PdfAIssue issue in report.Issues)
                    {
                        session.Console.Text.WriteLine($"  {issue.Clause}{(issue.Page is int p ? $" (page {p + 1})" : string.Empty)}: {issue.Description}");
                    }
                }
            }

            return code != ExitCodes.Success ? code : failed ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    public static Command Pdfx(EgapSession session)
    {
        Command command = new("pdfx", "Check PDFs against PDF/X, or convert them to PDF/X.");
        command.Subcommands.Add(PdfxCheck(session));
        command.Subcommands.Add(new PdfxEdit().CreateCommand(session, "convert"));
        return command;
    }

    /// <summary>A PDF/X level by name, such as x-4 or x-1a.</summary>
    internal static PdfXConformance Level(string text)
    {
        (string Name, PdfXConformance Level) found = Levels.FirstOrDefault(l => l.Name == text.ToLowerInvariant().Replace("pdf/", string.Empty, StringComparison.Ordinal));
        return found.Name is not null ? found.Level
            : throw new UsageException($"'{text}' is not a PDF/X level: {string.Join(", ", Levels.Select(l => l.Name))}.");
    }

    /// <summary>PDF/X-4 for <see cref="PdfXConformance.PdfX4"/>.</summary>
    internal static string Name(PdfXConformance level) => "PDF/X-" + level.ToString()["PdfX".Length..];

    private static Command PdfxCheck(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<string> level = new("--level") { Description = "The PDF/X level: x-1a, x-3, x-4 (the default), x-4p, x-5g, x-5pg, x-5n, x-6, x-6p or x-6n.", HelpName = "level", DefaultValueFactory = _ => "x-4" };
        Option<bool> json = new("--json") { Description = $"Write JSON (schema {Schema}, docs/schemas/egap-pdfa-1.json, which pdfx shares)." };
        Command command = new("check", "Check PDFs against a PDF/X level; exit code 1 when a clause is not met.");
        inputs.AddTo(command);
        command.Options.Add(level);
        command.Options.Add(json);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            PdfXConformance wanted = Level(result.GetValue(level)!);
            string? secret = password.Read(session, result);
            PdfAReport?[] reports = new PdfAReport?[files.Count];
            bool failed = false;
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                PdfAReport report = PdfXConverter.Check(document, wanted, session.Cancellation);
                reports[file.Index] = report;
                failed |= report.Unmet.Any();
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
                        if (reports[i] is not { } report)
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteString("path", files[i].Display);
                        writer.WriteString("level", Name(wanted));
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
                    if (reports[i] is not { } report)
                    {
                        continue;
                    }

                    List<PdfAIssue> unmet = [.. report.Unmet];
                    session.Console.Text.WriteLine($"{files[i].Display}: {(unmet.Count == 0 ? "meets" : "does not meet")} {Name(wanted)}");
                    foreach (PdfAIssue issue in unmet)
                    {
                        session.Console.Text.WriteLine($"  {issue.Clause}{(issue.Page is int p ? $" (page {p + 1})" : string.Empty)}: {issue.Description}{(issue.Fixable ? " [pdfx convert fixes it]" : string.Empty)}");
                    }
                }
            }

            return code != ExitCodes.Success ? code : failed ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    public static Command Separations(EgapSession session)
    {
        InputOptions inputs = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<string> output = new("--output", "-o") { Description = "The folder for the plates, written as <name>-<page>-<plate>.png.", HelpName = "folder", Required = true };
        PageOption pages = new("The pages to separate");
        Option<double> dpi = new("--dpi") { Description = "The resolution of the plates; 150 by default.", HelpName = "dpi", DefaultValueFactory = _ => 150 };
        Option<bool> coverage = new("--coverage") { Description = "Also write a heat map of the total area coverage, <name>-<page>-coverage.png." };
        Option<double> limit = new("--limit-tac") { Description = "The total area coverage the heat map marks as too much, in percent; 300 by default.", HelpName = "percent", DefaultValueFactory = _ => 300 };
        Option<bool> noPlates = new("--no-plates") { Description = "Write no plate images, only the coverage (with --coverage or --json)." };
        Option<bool> spotsToProcess = new("--spots-to-process") { Description = "Print spot colors with process inks instead of plates of their own." };
        Option<string[]> disable = new("--disable-spot") { Description = "Leave out this spot plate; give it more than once.", HelpName = "name", AllowMultipleArgumentsPerToken = false };
        Option<bool> json = new("--json") { Description = $"Write each page's coverage per plate and its total area coverage as JSON (schema {Schema}, docs/schemas/egap-separations-1.json)." };
        Command command = new("separations", "Render each page as one gray image per ink (cyan, magenta, yellow, black and each spot color), with the total area coverage.");
        inputs.AddTo(command);
        foreach (Option option in (Option[])[output, pages.Pages, dpi, coverage, limit, noPlates, spotsToProcess, disable, json])
        {
            command.Options.Add(option);
        }

        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            string folder = result.GetValue(output)!;
            PdfPageRange? range = pages.Range(result);
            PdfRenderOptions render = RenderCommand.Options(result.GetValue(dpi), null, transparent: false, null);
            PdfSeparationOptions separation = PdfSeparationOptions.Default with
            {
                SpotsToProcess = result.GetValue(spotsToProcess),
                DisabledSpots = result.GetValue(disable) ?? [],
            };
            bool writeCoverage = result.GetValue(coverage), writePlates = !result.GetValue(noPlates), asJson = result.GetValue(json);
            double tac = result.GetValue(limit);
            string? secret = password.Read(session, result);
            List<(int Page, double Max, double Over, IReadOnlyDictionary<string, double> Plates)>?[] found = new List<(int, double, double, IReadOnlyDictionary<string, double>)>?[files.Count];
            int code = Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                List<string> written = [];
                List<(int, double, double, IReadOnlyDictionary<string, double>)> pagesFound = [];
                foreach (int index in PageOption.Indices(document, range))
                {
                    using PdfSeparations plates = document.Pages[index].RenderSeparations(render, separation, session.Cancellation);
                    string prefix = $"{file.Name}-{(index + 1).ToString(CultureInfo.InvariantCulture)}";
                    if (writePlates)
                    {
                        foreach (PdfInkPlate plate in plates.Plates)
                        {
                            string path = session.ResolvePath(Path.Combine(folder, $"{prefix}-{Safe(plate.Name)}.png"));
                            Output.Write(session, path, stream => plate.Raster.Save(stream, "png"));
                            written.Add(path);
                        }
                    }

                    if (writeCoverage)
                    {
                        string path = session.ResolvePath(Path.Combine(folder, $"{prefix}-coverage.png"));
                        using Raster<Rgba8> map = plates.CoverageMap(tac);
                        Output.Write(session, path, stream => map.Save(stream, "png"));
                        written.Add(path);
                    }

                    pagesFound.Add((index + 1, plates.MaxCoverage, plates.ShareOver(tac), plates.CoveragePerPlate));
                }

                found[file.Index] = pagesFound;
                return written;
            });

            if (asJson)
            {
                JsonOut.Write(session.Console.Text, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema", Schema);
                    writer.WriteNumber("limit", tac);
                    writer.WriteStartArray("files");
                    for (int i = 0; i < files.Count; i++)
                    {
                        if (found[i] is not { } list)
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteString("path", files[i].Display);
                        writer.WriteStartArray("pages");
                        foreach ((int page, double max, double over, IReadOnlyDictionary<string, double> perPlate) in list)
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("page", page);
                            writer.WriteNumber("maxCoverage", Math.Round(max, 2));
                            writer.WriteNumber("shareOverLimit", Math.Round(over, 6));
                            writer.WriteStartObject("plates");
                            foreach ((string name, double value) in perPlate)
                            {
                                writer.WriteNumber(name, Math.Round(value, 4));
                            }

                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }

            return code;
        });
        return command;
    }

    /// <summary>A plate's name made safe for a file name: PANTONE 185 C becomes PANTONE_185_C.</summary>
    private static string Safe(string name)
    {
        char[] text = [.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')];
        return text.Length == 0 ? "_" : new string(text);
    }

    /// <summary>An output condition by identifier (FOGRA39, FOGRA51, FOGRA52, GRACoL2013, SWOP2013, newspaper) or an ICC file.</summary>
    internal static PdfOutputCondition? Condition(EgapSession session, string? condition, string? icc)
    {
        PdfOutputCondition? found = condition is null ? null : PdfOutputCondition.Find(condition)
            ?? throw new UsageException($"'{condition}' is not a known output condition: {string.Join(", ", PdfOutputCondition.Known.Select(c => c.Identifier))}.");
        if (icc is null)
        {
            return found;
        }

        ColorProfile profile = ColorProfile.FromIcc(File.ReadAllBytes(session.ResolvePath(icc)));
        return found is null ? new PdfOutputCondition(Path.GetFileNameWithoutExtension(icc), Path.GetFileName(icc), profile) : found.WithProfile(profile);
    }
}

/// <summary><c>egap pdfx convert</c> and the <c>pdfx</c> step: fix what PDF/X needs, with an output condition, and claim the level.</summary>
internal sealed class PdfxEdit : Edit
{
    private readonly Option<string> _level = new("--level") { Description = "The PDF/X level: x-1a, x-3, x-4 (the default), x-4p, x-5g, x-5pg, x-5n, x-6, x-6p or x-6n.", HelpName = "level", DefaultValueFactory = _ => "x-4" };
    private readonly Option<string?> _condition = new("--output-condition") { Description = "The printing condition: FOGRA39, FOGRA51, FOGRA52, GRACoL2013, SWOP2013 or newspaper.", HelpName = "name" };
    private readonly Option<string?> _icc = new("--icc") { Description = "The output intent's ICC profile, for a condition egap does not bundle.", HelpName = "file" };
    private readonly Option<string?> _bleed = new("--bleed") { Description = "Add a bleed box this far around the trim box, such as 3mm.", HelpName = "length" };
    private readonly Option<bool> _trapped = new("--trapped") { Description = "Say the file has been trapped." };
    private readonly Option<bool> _substitute = new("--allow-font-substitution") { Description = "Embed a stand-in for a font the file does not embed." };
    private readonly Option<bool> _systemFonts = new("--system-fonts") { Description = "Look for fonts the file does not embed among the system's fonts." };

    /// <inheritdoc/>
    public override string Name => "pdfx";

    /// <inheritdoc/>
    public override string Description => "Convert to PDF/X: an output intent, colors in its CMYK where the level needs it, embedded fonts, page boxes; fails the file when a clause cannot be met.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_level, _condition, _icc, _bleed, _trapped, _substitute, _systemFonts])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        PdfXConformance level = PrintCommands.Level(result.GetValue(_level)!);
        PdfXConvertOptions options = PdfXConvertOptions.Default with
        {
            Trapped = result.GetValue(_trapped),
            AllowFontSubstitution = result.GetValue(_substitute),
            UseSystemFonts = result.GetValue(_systemFonts),
        };
        if (PrintCommands.Condition(session, result.GetValue(_condition), result.GetValue(_icc)) is { } condition)
        {
            options = options with { OutputCondition = condition };
        }

        if (result.GetValue(_bleed) is { } bleed)
        {
            options = options with { Bleed = Values.Points(bleed, "--bleed") };
        }

        return (document, context) =>
        {
            PdfAReport report = PdfXConverter.Convert(document, level, options, session.Cancellation);
            List<PdfAIssue> unmet = [.. report.Unmet];
            if (unmet.Count > 0)
            {
                throw new InvalidOperationException($"It cannot meet {PrintCommands.Name(level)}: {string.Join("; ", unmet.Take(3).Select(i => $"{i.Clause} {i.Description}"))}{(unmet.Count > 3 ? $" and {unmet.Count - 3} more" : string.Empty)}.");
            }

            context.Rewrite = true;
            session.Info($"{context.File.Display}: {PrintCommands.Name(level)}, {report.Fixes.Count()} fixes");
            return document;
        };
    }
}

/// <summary>
/// <c>egap color</c> and the <c>color</c> step: RGB, Lab, gray and ICC-based colors (and spots, if asked) converted to the
/// output intent's CMYK, or to another CMYK profile.
/// </summary>
internal sealed class ColorEdit : Edit
{
    private readonly Option<string?> _target = new("--target") { Description = "The CMYK to convert to: an output condition (FOGRA39, GRACoL2013, ...) or an ICC file; the file's output intent by default.", HelpName = "condition|file" };
    private readonly Option<string?> _intent = new("--intent") { Description = "relative (the default), perceptual, saturation or absolute.", HelpName = "intent" };
    private readonly Option<bool?> _bpc = new("--black-point-compensation") { Description = "Map black to black (true or false)." };
    private readonly Option<string?> _blackText = new("--black-text") { Description = "Black text and thin rules: pure (100% K, the default) or colorimetric.", HelpName = "how" };
    private readonly Option<string?> _spots = new("--spots") { Description = "Spot colors: keep (the default), convert, or convert-but-kept with --keep-spot.", HelpName = "how" };
    private readonly Option<string[]> _keep = new("--keep-spot") { Description = "A spot color to keep with --spots convert-but-kept; give it more than once.", HelpName = "name", AllowMultipleArgumentsPerToken = false };
    private readonly Option<bool> _retarget = new("--retarget-cmyk") { Description = "Also convert CMYK that was made for another printing condition." };

    /// <inheritdoc/>
    public override string Name => "color";

    /// <inheritdoc/>
    public override string Description => "Convert RGB, Lab, gray and ICC colors to the output intent's CMYK, or another CMYK; spot colors on request.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_target, _intent, _bpc, _blackText, _spots, _keep, _retarget])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        PdfColorConversionOptions options = PdfColorConversionOptions.Default with
        {
            Intent = result.GetValue(_intent) switch
            {
                null or "relative" => RenderingIntent.RelativeColorimetric,
                "perceptual" => RenderingIntent.Perceptual,
                "saturation" => RenderingIntent.Saturation,
                "absolute" => RenderingIntent.AbsoluteColorimetric,
                string other => throw new UsageException($"--intent '{other}' is not relative, perceptual, saturation or absolute."),
            },
            BlackText = result.GetValue(_blackText) switch
            {
                null or "pure" => PdfBlackText.PureBlack,
                "colorimetric" => PdfBlackText.Colorimetric,
                string other => throw new UsageException($"--black-text '{other}' is not pure or colorimetric."),
            },
            Spots = result.GetValue(_spots) switch
            {
                null or "keep" => PdfSpotConversion.Keep,
                "convert" => PdfSpotConversion.Convert,
                "convert-but-kept" => PdfSpotConversion.ConvertAllButNamed,
                string other => throw new UsageException($"--spots '{other}' is not keep, convert or convert-but-kept."),
            },
            KeepSpots = result.GetValue(_keep) ?? [],
            RetargetCmyk = result.GetValue(_retarget),
        };
        options = options with { BlackPointCompensation = result.GetValue(_bpc) ?? options.BlackPointCompensation };
        if (result.GetValue(_target) is { } target)
        {
            bool file = File.Exists(session.ResolvePath(target));
            PdfOutputCondition condition = PrintCommands.Condition(session, file ? null : target, file ? target : null)!;
            options = options with { Target = condition.Profile ?? throw new UsageException($"The output condition {target} has no bundled profile; give an ICC file with --target.") };
        }

        return (document, context) =>
        {
            PdfColorConversionResult converted = PdfColorConverter.Convert(document, options, session.Cancellation);
            session.Report(context.File.Display, converted.Warnings);
            session.Info($"{context.File.Display}: {converted.Colors} colors, {converted.Images} images, {converted.Shadings} shadings converted");
            return document;
        };
    }
}
