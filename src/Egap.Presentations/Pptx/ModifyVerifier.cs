using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Presentations.Pptx;

/// <summary>
/// The password to modify a presentation (<c>p:modifyVerifier</c>, ECMA-376 Part 1 19.2.1.19): a salted, iterated hash of
/// the password in UTF-16LE, by algorithm name (PowerPoint 2013 and later) or by algorithm number (2007 and 2010).
/// </summary>
internal static class ModifyVerifier
{
    public static XElement Create(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = DocumentProtection.ComputeHash("SHA-512", Encoding.Unicode.GetBytes(password), salt, DocumentProtection.DefaultSpinCount);
        return new XElement(PptxNames.P + "modifyVerifier",
            new XAttribute("algorithmName", "SHA-512"), new XAttribute("hashValue", Convert.ToBase64String(hash)),
            new XAttribute("saltValue", Convert.ToBase64String(salt)), new XAttribute("spinValue", PptxNames.Text(DocumentProtection.DefaultSpinCount)));
    }

    /// <exception cref="DocumentException">More spins than <see cref="DocumentProtection.MaxSpinCount"/>.</exception>
    public static bool Verify(XElement? verifier, string? password)
    {
        if (verifier is null)
        {
            return true;
        }

        string? algorithm = (string?)verifier.Attribute("algorithmName");
        string? hashText = (string?)verifier.Attribute("hashValue");
        string? saltText = (string?)verifier.Attribute("saltValue");
        string? spinText = (string?)verifier.Attribute("spinValue");
        if (algorithm is null && int.TryParse((string?)verifier.Attribute("cryptAlgorithmSid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sid))
        {
            algorithm = DocumentProtection.AlgorithmFromSid(sid);
            hashText = (string?)verifier.Attribute("hashData");
            saltText = (string?)verifier.Attribute("saltData");
            spinText = (string?)verifier.Attribute("spinCount");
        }

        if (string.IsNullOrEmpty(password) || algorithm is null || !TryBase64(hashText, out byte[] hash) || !TryBase64(saltText, out byte[] salt)
            || !int.TryParse(spinText ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int spins) || spins < 0)
        {
            return false;
        }

        if (spins > DocumentProtection.MaxSpinCount)
        {
            throw new DocumentException(string.Create(CultureInfo.InvariantCulture, $"The modify password's hash asks for {spins} spins; at most {DocumentProtection.MaxSpinCount} are run."));
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(DocumentProtection.ComputeHash(algorithm, Encoding.Unicode.GetBytes(password), salt, spins), hash);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or DocumentException)
        {
            return false;
        }
    }

    private static bool TryBase64(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
