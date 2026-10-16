using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace Egap.Pdf.Signatures;

/// <summary>
/// Honours a signature field's seed values when signing it (Phase 5 design Q7; ISO 32000-2, 12.7.5.5): each value whose
/// flag makes it required must be met, or signing stops before anything is written. Values that are only suggestions are
/// left to the caller.
/// </summary>
internal static class SeedValueCheck
{
    private static readonly string[] KeyUsageBits = ["digitalSignature", "nonRepudiation", "keyEncipherment", "dataEncipherment", "keyAgreement", "keyCertSign", "cRLSign", "encipherOnly", "decipherOnly"];

    /// <exception cref="ArgumentException">The options or the signer do not meet a required seed value.</exception>
    public static void Check(PdfSeedValue? seed, PdfSignOptions options)
    {
        if (seed is null)
        {
            return;
        }

        List<string> unmet = [];
        string subFilter = options.Format == PdfSignatureFormat.Pades ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached";
        if (seed.Flags.HasFlag(PdfSeedValueFlags.Filter) && seed.Filter is { } filter && filter != "Adobe.PPKLite")
        {
            unmet.Add($"the handler {filter}");
        }

        if (seed.Flags.HasFlag(PdfSeedValueFlags.SubFilter) && seed.SubFilters.Count > 0 && !seed.SubFilters.Contains(subFilter))
        {
            unmet.Add($"one of the formats {string.Join(", ", seed.SubFilters)}");
        }

        if (seed.Flags.HasFlag(PdfSeedValueFlags.DigestMethod) && seed.DigestMethods.Count > 0
            && !seed.DigestMethods.Any(m => string.Equals(m, options.DigestAlgorithm.Name, StringComparison.OrdinalIgnoreCase)))
        {
            unmet.Add($"one of the digests {string.Join(", ", seed.DigestMethods)}");
        }

        if (seed.Flags.HasFlag(PdfSeedValueFlags.Reasons) && seed.Reasons.Count > 0)
        {
            bool none = seed.Reasons is ["."];
            if (none ? !string.IsNullOrEmpty(options.Reason) : !seed.Reasons.Contains(options.Reason ?? ""))
            {
                unmet.Add(none ? "no reason" : $"one of the reasons {string.Join(", ", seed.Reasons.Select(r => $"'{r}'"))}");
            }
        }

        if (seed.Certification is { } level && options.Certification != level)
        {
            unmet.Add($"a certification signature allowing {level}");
        }

        if (seed.TimestampRequired && (options.Level < PadesLevel.BaselineT || options.Timestamp is null))
        {
            unmet.Add($"a timestamp{(seed.TimestampUrl is { } url ? $" (from {url})" : "")}");
        }

        if (seed.AddRevocationInfo && seed.Flags.HasFlag(PdfSeedValueFlags.AddRevocationInfo) && options.Level < PadesLevel.BaselineLT)
        {
            unmet.Add("revocation information embedded (B-LT or B-LTA)");
        }

        if (seed.Certificate is { } constraints && options.Signer is { } signer)
        {
            CheckCertificate(constraints, signer, unmet);
        }

        if (unmet.Count > 0)
        {
            throw new ArgumentException($"The field's seed values require {string.Join("; ", unmet)}.", nameof(options));
        }
    }

    private static void CheckCertificate(PdfCertificateSeed seed, PdfSigner signer, List<string> unmet)
    {
        X509Certificate2 certificate = signer.Certificate;
        if (seed.Flags.HasFlag(PdfCertificateSeedFlags.Subject) && seed.Subjects.Count > 0 && !seed.Subjects.Any(s => s.AsSpan().SequenceEqual(certificate.RawData)))
        {
            unmet.Add("one of the signer certificates it names");
        }

        if (seed.Flags.HasFlag(PdfCertificateSeedFlags.Issuer) && seed.Issuers.Count > 0 && !signer.Chain.Any(c => seed.Issuers.Any(i => i.AsSpan().SequenceEqual(c.RawData))))
        {
            unmet.Add("a certificate issued by one of the issuers it names, with that issuer in the chain");
        }

        if (seed.Flags.HasFlag(PdfCertificateSeedFlags.Policy) && seed.Policies.Count > 0 && !Policies(certificate).Intersect(seed.Policies).Any())
        {
            unmet.Add($"a certificate under one of the policies {string.Join(", ", seed.Policies)}");
        }

        if (seed.Flags.HasFlag(PdfCertificateSeedFlags.KeyUsage) && seed.KeyUsage.Count > 0 && !seed.KeyUsage.Any(p => KeyUsageMatches(p, certificate)))
        {
            unmet.Add($"a certificate whose key usage matches {string.Join(" or ", seed.KeyUsage)} ({string.Join(", ", KeyUsageBits)})");
        }
    }

    /// <summary>The policy OIDs of the certificate's certificate policies extension (RFC 5280, 4.2.1.4).</summary>
    private static List<string> Policies(X509Certificate2 certificate)
    {
        List<string> policies = [];
        if (certificate.Extensions["2.5.29.32"] is { } extension)
        {
            try
            {
                AsnReader sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
                while (sequence.HasData)
                {
                    policies.Add(sequence.ReadSequence().ReadObjectIdentifier());
                }
            }
            catch (AsnContentException)
            {
                // A broken extension names no policy.
            }
        }

        return policies;
    }

    /// <summary>Whether the certificate's key usage matches a pattern: per bit from digitalSignature, 1 set, 0 clear, X either.</summary>
    private static bool KeyUsageMatches(string pattern, X509Certificate2 certificate)
    {
        X509KeyUsageFlags usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages ?? X509KeyUsageFlags.None;
        X509KeyUsageFlags[] bits =
        [
            X509KeyUsageFlags.DigitalSignature, X509KeyUsageFlags.NonRepudiation, X509KeyUsageFlags.KeyEncipherment, X509KeyUsageFlags.DataEncipherment,
            X509KeyUsageFlags.KeyAgreement, X509KeyUsageFlags.KeyCertSign, X509KeyUsageFlags.CrlSign, X509KeyUsageFlags.EncipherOnly, X509KeyUsageFlags.DecipherOnly,
        ];
        for (int i = 0; i < Math.Min(pattern.Length, bits.Length); i++)
        {
            bool set = (usage & bits[i]) != 0;
            if ((pattern[i] == '1' && !set) || (pattern[i] == '0' && set))
            {
                return false;
            }
        }

        return true;
    }
}
