using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Egap.Pdf.Signatures.Cms;

namespace Egap.Pdf.Signatures;

/// <summary>
/// The revocation verdict (Phase 5 design Q8): every certificate of the signer's chain below the trust anchor needs an OCSP
/// response or CRL saying it was not revoked at the time the signature is known to exist (its timestamp, else the
/// verification time). Data comes from the file first (the DSS, the CMS's CRLs and Adobe's revocation attribute), then
/// from the options' revocation source, which is the only way anything is fetched.
/// </summary>
internal static class RevocationCheck
{
    private const string OcspNoCheck = "1.3.6.1.5.5.7.48.1.5";

    /// <summary>What the file's Document Security Store holds: certificates, OCSP responses and CRLs.</summary>
    internal sealed record Store(List<X509Certificate2> Certificates, List<ReadOnlyMemory<byte>> Ocsps, List<ReadOnlyMemory<byte>> Crls);

    public static Store ReadDss(PdfDocument document)
    {
        Store store = new([], [], []);
        if (document.Catalog.Get<PdfDictionary>("DSS") is not { } dss)
        {
            return store;
        }

        foreach (byte[] bytes in Streams(dss.Get<PdfArray>("Certs")))
        {
            try
            {
                store.Certificates.Add(X509CertificateLoader.LoadCertificate(bytes));
            }
            catch (CryptographicException)
            {
            }
        }

        store.Ocsps.AddRange(Streams(dss.Get<PdfArray>("OCSPs")).Select(b => (ReadOnlyMemory<byte>)b));
        store.Crls.AddRange(Streams(dss.Get<PdfArray>("CRLs")).Select(b => (ReadOnlyMemory<byte>)b));
        return store;
    }

    public static PdfVerdict Check(IReadOnlyList<X509Certificate2> chain, CmsSignedData? cms, CmsSignerInfo? signer, Store dss, PdfVerifyOptions options, DateTimeOffset time, PdfSignatureReport report)
    {
        if (chain.Count == 0)
        {
            report.Problem("Revocation was not checked: no certificate chain was built.");
            return PdfVerdict.Indeterminate;
        }

        List<ReadOnlyMemory<byte>> ocsps = [.. dss.Ocsps];
        List<ReadOnlyMemory<byte>> crls = [.. dss.Crls];
        if (cms is not null)
        {
            crls.AddRange(cms.Crls.Select(c => (ReadOnlyMemory<byte>)c));
        }

        if (signer is not null)
        {
            ReadArchival(signer, ocsps, crls);
        }

        PdfVerdict verdict = PdfVerdict.Valid;
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            X509Certificate2 certificate = chain[i], issuer = chain[i + 1];
            if (LongTermWriter.IsSelfIssued(certificate) || certificate.Extensions[OcspNoCheck] is not null)
            {
                continue;
            }

            RevocationData? data = PdfRevocationSource.FirstAnswer(ocsps, crls, certificate, issuer);
            if (data is null && options.Revocation is { } source)
            {
                try
                {
                    data = source.GetAsync(certificate, issuer, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (HttpRequestException e)
                {
                    report.Problem($"Revocation data for {certificate.Subject} could not be fetched: {e.Message}");
                }
            }

            string who = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (data is null)
            {
                report.Problem($"There is no revocation data for {who}.");
                verdict = Worse(verdict, PdfVerdict.Indeterminate);
                continue;
            }

            RevocationAnswer answer = data.Answer;
            switch (answer.Status)
            {
                case RevocationStatus.Revoked when answer.RevokedAt is { } revoked && revoked <= time:
                    report.Problem($"{who} was revoked on {revoked:yyyy-MM-dd HH:mm} UTC ({answer.Source}), before the signature is known to exist.");
                    verdict = PdfVerdict.Invalid;
                    break;
                case RevocationStatus.Revoked:
                    report.Warning($"{who} was revoked after the time its timestamp proves ({answer.Source}).");
                    break;
                case RevocationStatus.Unknown:
                    report.Problem($"The OCSP responder does not know {who}.");
                    verdict = Worse(verdict, PdfVerdict.Indeterminate);
                    break;
                default:
                    if (answer.ThisUpdate < time && answer.NextUpdate is { } next && next < time)
                    {
                        report.Problem($"The {answer.Source} data for {who} expired before the signature is known to exist.");
                        verdict = Worse(verdict, PdfVerdict.Indeterminate);
                    }

                    break;
            }
        }

        return verdict;
    }

    private static PdfVerdict Worse(PdfVerdict a, PdfVerdict b)
    {
        return a == PdfVerdict.Invalid || b == PdfVerdict.Invalid ? PdfVerdict.Invalid : a == PdfVerdict.Indeterminate || b == PdfVerdict.Indeterminate ? PdfVerdict.Indeterminate : PdfVerdict.Valid;
    }

    /// <summary>Adobe's revocation information in a signed attribute (ISO 32000-2, 12.8.3.3.2): CRLs [0] and OCSP responses [1].</summary>
    private static void ReadArchival(CmsSignerInfo signer, List<ReadOnlyMemory<byte>> ocsps, List<ReadOnlyMemory<byte>> crls)
    {
        foreach (CmsAttribute attribute in signer.SignedAttributes.Where(a => a.Oid == CmsOids.AdobeRevocationInfoArchival))
        {
            foreach (ReadOnlyMemory<byte> value in attribute.Values)
            {
                try
                {
                    AsnReader archival = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
                    while (archival.HasData)
                    {
                        Asn1Tag tag = archival.PeekTag();
                        AsnReader items = archival.ReadSequence(tag).ReadSequence();
                        while (items.HasData)
                        {
                            (tag.TagValue == 0 ? crls : ocsps).Add(items.ReadEncodedValue());
                        }
                    }
                }
                catch (AsnContentException)
                {
                }
            }
        }
    }

    private static IEnumerable<byte[]> Streams(PdfArray? array)
    {
        foreach (PdfObject item in array ?? [])
        {
            if (item.Resolve() is PdfStream stream)
            {
                byte[]? bytes = null;
                try
                {
                    bytes = stream.GetDecodedBytes();
                }
                catch (PdfException)
                {
                }

                if (bytes is not null)
                {
                    yield return bytes;
                }
            }
        }
    }
}
