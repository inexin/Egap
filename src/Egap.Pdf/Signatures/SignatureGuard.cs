namespace Egap.Pdf.Signatures;

/// <summary>
/// Checks a save before it is written (Phase 5 design Q7): a rewrite of a signed file breaks every signature, and an update
/// breaks each signature whose permissions forbid one of its changes, judged by the same change analysis verification uses.
/// Each broken signature is a <see cref="PdfWarningCode.SignatureInvalidated"/> warning, which throws in strict mode.
/// </summary>
internal static class SignatureGuard
{
    /// <summary>Warns, or throws in strict mode, for each signature the save would break.</summary>
    public static void Check(PdfDocument document, bool rewrite, byte[]? update)
    {
        foreach (string problem in Problems(document, rewrite, update))
        {
            document.Log.Add(PdfWarningCode.SignatureInvalidated, problem);
        }
    }

    /// <summary>What the save would break, one sentence per signature.</summary>
    public static List<string> Problems(PdfDocument document, bool rewrite, byte[]? update)
    {
        List<string> problems = [];
        IReadOnlyList<PdfSignature> signatures;
        try
        {
            signatures = document.Signatures;
        }
        catch (PdfException)
        {
            return problems;
        }

        if (signatures.Count == 0)
        {
            return problems;
        }

        if (rewrite)
        {
            problems.Add($"Rewriting the file whole breaks its {signatures.Count} signature{(signatures.Count == 1 ? "" : "s")} ({string.Join(", ", signatures.Select(s => s.Name))}); save incrementally to keep them.");
            return problems;
        }

        if (update is null)
        {
            return problems;
        }

        using PdfDocument after = document.OpenWithUpdate(update);
        int added = after.Revisions.Count - 1;
        foreach (PdfSignature signature in after.Signatures)
        {
            if (signature.Revision is not { } revision || revision.Index >= added)
            {
                continue;
            }

            List<PdfSignatureChange> changes = ChangeAnalyzer.Analyze(after, signature, out _);
            List<PdfSignatureChange> forbidden = [.. changes.Where(c => c.Revision == added && c.Verdict == PdfChangeVerdict.Disallowed)];
            if (forbidden.Count > 0)
            {
                problems.Add($"The changes break the signature {signature.Name}: {forbidden[0].Description}");
            }
        }

        return problems;
    }
}
