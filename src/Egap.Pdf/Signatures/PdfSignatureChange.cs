namespace Egap.Pdf;

/// <summary>How a change made after a signature is judged (Phase 5 design Q8).</summary>
public enum PdfChangeVerdict
{
    /// <summary>The signature's permissions allow it, or it is long-term validation data, which every signature allows.</summary>
    Allowed,

    /// <summary>It does not change what the signed document shows, but no rule allows it either, such as an object nothing uses.</summary>
    Suspicious,

    /// <summary>The permissions forbid it: page content, resources, a locked field, or anything beyond the certification level.</summary>
    Disallowed,
}

/// <summary>One change found in a revision after a signature, with how it is judged and why.</summary>
/// <param name="Revision">The revision that made it.</param>
/// <param name="ObjectNumber">The object changed, added or removed; 0 for the trailer.</param>
/// <param name="Verdict">How it is judged.</param>
/// <param name="Description">What changed, in a sentence.</param>
public sealed record PdfSignatureChange(int Revision, int ObjectNumber, PdfChangeVerdict Verdict, string Description);
