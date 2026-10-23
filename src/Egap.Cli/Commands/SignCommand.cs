using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Egap.Cli.Host;
using Egap.Pdf;

using RectF = Egami.Drawing.RectF;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap sign</c> (CLI design Q8): an approval or certification signature from a PFX, a PEM pair or the Windows and macOS
/// certificate store, PAdES B-B to B-LTA, a timestamp, an optional visible appearance. Always an incremental update, and the
/// timestamp authority and OCSP or CRL fetches are the only network calls egap makes on its own.
/// </summary>
internal static class SignCommand
{
    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        SecretOptions certPassword = new("cert-password", "password of the PFX file or the encrypted PEM key");
        Option<string?> cert = new("--cert") { Description = "The certificate: a PFX (.pfx, .p12) with its key, or a PEM certificate with --key.", HelpName = "file" };
        Option<string?> key = new("--key") { Description = "The PEM private key that goes with a PEM --cert.", HelpName = "file" };
        Option<string?> store = new("--cert-store") { Description = "Sign with the certificate of this SHA-1 thumbprint from the user's store (Windows and macOS).", HelpName = "thumbprint" };
        Option<string[]> chain = new("--chain") { Description = "A PEM or DER certificate to include in the chain; give it more than once.", HelpName = "file", AllowMultipleArgumentsPerToken = false };
        Option<string?> level = new("--level") { Description = "PAdES level: b-b (the default), b-t (with a timestamp), b-lt (plus revocation data) or b-lta (plus an archive timestamp).", HelpName = "level" };
        Option<string?> timestamp = new("--timestamp") { Description = "The RFC 3161 timestamp authority's URL, needed for b-t, b-lt and b-lta.", HelpName = "url" };
        Option<string?> reason = new("--reason") { Description = "Why it was signed.", HelpName = "text" };
        Option<string?> location = new("--location") { Description = "Where it was signed.", HelpName = "text" };
        Option<string?> contact = new("--contact") { Description = "How to reach the signer.", HelpName = "text" };
        Option<string?> name = new("--name") { Description = "The signer's name as shown; the certificate's by default.", HelpName = "text" };
        Option<string?> field = new("--field") { Description = "The signature field to sign; a new invisible one, Signature<n>, by default.", HelpName = "name" };
        Option<string?> visible = new("--visible") { Description = "Show the signature: page:x,y,width,height in lengths from the page's top left, such as 1:20mm,250mm,60mm,20mm.", HelpName = "page:x,y,w,h" };
        Option<string?> image = new("--image") { Description = "A picture shown at the left of a visible signature, such as a scanned signature.", HelpName = "file" };
        Option<string?> certify = new("--certify") { Description = "Make it a certification signature that allows no-changes, form-filling or annotations afterwards.", HelpName = "allowed" };
        Option<bool> pkcs7 = new("--pkcs7") { Description = "Write an adbe.pkcs7.detached signature instead of PAdES (ETSI.CAdES.detached)." };
        Option<string?> digest = new("--digest") { Description = "The digest: sha256 (the default), sha384 or sha512.", HelpName = "algorithm" };
        Option<bool> rsaPss = new("--rsa-pss") { Description = "Sign with RSASSA-PSS instead of PKCS #1 v1.5 (RSA keys)." };
        Option<bool> allowInvalidating = new("--allow-invalidating-signatures") { Description = "Sign even when the change breaks an earlier signature's permissions." };
        Command command = new("sign", "Sign PDFs: PAdES B-B to B-LTA from a PFX, a PEM certificate and key, or the certificate store; always an incremental update.");
        inputs.AddTo(command);
        output.AddTo(command);
        foreach (Option option in (Option[])[cert, key, store, chain, level, timestamp, reason, location, contact, name, field, visible, image, certify, pkcs7, digest, rsaPss, allowInvalidating])
        {
            command.Options.Add(option);
        }

        certPassword.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            PadesLevel padesLevel = result.GetValue(level) switch
            {
                null or "b-b" => PadesLevel.BaselineB,
                "b-t" => PadesLevel.BaselineT,
                "b-lt" => PadesLevel.BaselineLT,
                "b-lta" => PadesLevel.BaselineLTA,
                string other => throw new UsageException($"--level '{other}' is not b-b, b-t, b-lt or b-lta."),
            };
            string? timestampUrl = result.GetValue(timestamp);
            if (padesLevel != PadesLevel.BaselineB && timestampUrl is null)
            {
                throw new UsageException($"--level {result.GetValue(level)} needs a timestamp authority: --timestamp <url>.");
            }

            PdfSigner signer = Signer(session, result.GetValue(cert), result.GetValue(key), result.GetValue(store), result.GetValue(chain) ?? [], () => certPassword.Read(session, result));
            HttpClient? http = timestampUrl is not null || padesLevel >= PadesLevel.BaselineLT ? new HttpClient { Timeout = TimeSpan.FromSeconds(30) } : null;
            PdfSignOptions options = new()
            {
                Signer = signer,
                Level = padesLevel,
                Format = result.GetValue(pkcs7) ? PdfSignatureFormat.Pkcs7 : PdfSignatureFormat.Pades,
                Reason = result.GetValue(reason),
                Location = result.GetValue(location),
                ContactInfo = result.GetValue(contact),
                SignerName = result.GetValue(name),
                Certification = result.GetValue(certify) switch
                {
                    null => null,
                    "no-changes" => PdfCertification.NoChanges,
                    "form-filling" => PdfCertification.FormFillingAndSigning,
                    "annotations" => PdfCertification.AnnotationsFormFillingAndSigning,
                    string other => throw new UsageException($"--certify '{other}' is not no-changes, form-filling or annotations."),
                },
                DigestAlgorithm = result.GetValue(digest) switch
                {
                    null or "sha256" => HashAlgorithmName.SHA256,
                    "sha384" => HashAlgorithmName.SHA384,
                    "sha512" => HashAlgorithmName.SHA512,
                    string other => throw new UsageException($"--digest '{other}' is not sha256, sha384 or sha512."),
                },
                UseRsaPss = result.GetValue(rsaPss),
                AllowInvalidatingSignatures = result.GetValue(allowInvalidating),
                Timestamp = timestampUrl is null ? null : new PdfTimestampClient(new Uri(timestampUrl), http!),
                Revocation = padesLevel >= PadesLevel.BaselineLT ? PdfRevocationSource.Online(http!) : null,
                Appearance = Appearance(session, result.GetValue(visible), result.GetValue(image)),
            };
            string? fieldName = result.GetValue(field);
            string? secret = password.Read(session, result);
            try
            {
                return Batch.Run(session, files, batch, file =>
                {
                    string path = template is null ? file.Path ?? throw new UsageException("--in-place needs files, not standard input.") : OutputOptions.PathFor(session, template, file, "pdf");
                    using PdfDocument document = PdfInput.Open(session, file, secret, inMemory: template is null);
                    string target = fieldName ?? NewField(document);
                    Output.Write(session, path, stream => document.SignAsync(target, options, stream, session.Cancellation).GetAwaiter().GetResult());
                    session.Info($"{file.Display}: signed {target}");
                    return [path];
                });
            }
            finally
            {
                http?.Dispose();
            }
        });
        return command;
    }

    /// <summary>The signer from a PFX, a PEM certificate and key, or the store, with any further chain certificates.</summary>
    internal static PdfSigner Signer(EgapSession session, string? cert, string? key, string? thumbprint, string[] chainFiles, Func<string?> password)
    {
        if ((cert is null) == (thumbprint is null))
        {
            throw new UsageException("Give the certificate with --cert (PFX, or PEM with --key) or --cert-store <thumbprint>.");
        }

        List<X509Certificate2> chain = [.. chainFiles.Select(file => X509CertificateLoader.LoadCertificateFromFile(session.ResolvePath(file)))];
        X509Certificate2 certificate;
        if (thumbprint is not null)
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            {
                throw new UsageException("--cert-store works on Windows and macOS; on Linux, give a PFX or PEM file with --cert.");
            }

            using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal), validOnly: false);
            certificate = found.Count > 0 ? found[0] : throw new UsageException($"No certificate with the thumbprint {thumbprint} is in your personal store.");
        }
        else
        {
            string path = session.ResolvePath(cert!);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".pfx" or ".p12")
            {
                string? secret = password() ?? SecretOptions.Prompt(session, $"Password for {cert}");
                X509Certificate2Collection all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, secret, X509KeyStorageFlags.EphemeralKeySet);
                certificate = all.FirstOrDefault(c => c.HasPrivateKey) ?? throw new UsageException($"{cert} holds no certificate with a private key.");
                chain.AddRange(all.Where(c => !ReferenceEquals(c, certificate)));
            }
            else
            {
                string keyPath = key is not null ? session.ResolvePath(key) : throw new UsageException("A PEM certificate needs its private key: --key <file>.");
                string keyText = File.ReadAllText(keyPath);
                certificate = keyText.Contains("ENCRYPTED", StringComparison.Ordinal)
                    ? X509Certificate2.CreateFromEncryptedPemFile(path, password() ?? SecretOptions.Prompt(session, $"Password for {key}") ?? string.Empty, keyPath)
                    : X509Certificate2.CreateFromPemFile(path, keyPath);
            }
        }

        if (!certificate.HasPrivateKey)
        {
            throw new UsageException("The certificate has no private key to sign with.");
        }

        if (certificate.NotAfter < DateTime.Now)
        {
            session.Warn($"the certificate expired on {certificate.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; the signature will not validate.");
        }

        return PdfSigner.FromCertificate(certificate, chain);
    }

    private static PdfSignatureAppearance? Appearance(EgapSession session, string? visible, string? image)
    {
        if (visible is null)
        {
            return image is null ? null : throw new UsageException("--image goes with --visible.");
        }

        int colon = visible.IndexOf(':', StringComparison.Ordinal);
        string[] parts = colon > 0 ? visible[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries) : [];
        if (colon <= 0 || parts.Length != 4 || !int.TryParse(visible[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int page) || page < 1)
        {
            throw new UsageException($"--visible '{visible}' is not page:x,y,width,height, such as 1:20mm,250mm,60mm,20mm.");
        }

        float[] box = [.. parts.Select(part => (float)Values.Points(part, "--visible"))];
        Egami.Raster? picture = image is null ? null : Egami.Raster.Load(session.ResolvePath(image));
        return PdfSignatureAppearance.Default(page - 1, new RectF(box[0], box[1], box[2], box[3]), picture);
    }

    /// <summary>The first free name of the form Signature1, Signature2 and so on.</summary>
    private static string NewField(PdfDocument document)
    {
        for (int n = 1; ; n++)
        {
            string name = $"Signature{n.ToString(CultureInfo.InvariantCulture)}";
            if (!document.Form.TryGetField(name, out _))
            {
                return name;
            }
        }
    }
}
