using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// w:documentProtection (ECMA-376 Part 1, 17.15.1.29) and the editable ranges' w:permStart and w:permEnd (17.13.7). The hash
/// is read from Word's attributes (cryptAlgorithmSid, hash, salt) or the newer ones (algorithmName, hashValue, saltValue) and
/// written back in the form it was read, Word's for a new one.
/// </summary>
internal static class DocxProtection
{
    private static readonly string[] HashAttributes =
    [
        "cryptProviderType", "cryptAlgorithmClass", "cryptAlgorithmType", "cryptAlgorithmSid", "cryptSpinCount", "cryptProvider",
        "algIdExt", "algIdExtSource", "cryptProviderTypeExt", "cryptProviderTypeExtSource", "hash", "salt",
        "algorithmName", "hashValue", "saltValue", "spinCount",
    ];

    public static void Read(XElement settings, DocumentProtection protection)
    {
        if (settings.Element(Ns.W + "documentProtection") is not { } element)
        {
            return;
        }

        ProtectionKind kind = element.W("edit") switch
        {
            "readOnly" => ProtectionKind.ReadOnly,
            "comments" => ProtectionKind.Comments,
            "trackedChanges" => ProtectionKind.TrackedChanges,
            "forms" => ProtectionKind.Forms,
            _ => ProtectionKind.None,
        };
        bool enforced = element.W("enforcement") is "1" or "true" or "on";
        if (element.W("hashValue") is { } modernHash)
        {
            protection.Set(kind, enforced, element.W("algorithmName"), Base64(modernHash), Base64(element.W("saltValue")), DocxUnits.Int(element.W("spinCount")) ?? 0, modern: true);
        }
        else
        {
            string? algorithm = DocxUnits.Int(element.W("cryptAlgorithmSid")) is int sid ? DocumentProtection.AlgorithmFromSid(sid) : null;
            protection.Set(kind, enforced, algorithm, Base64(element.W("hash")), Base64(element.W("salt")), DocxUnits.Int(element.W("cryptSpinCount")) ?? 0);
        }
    }

    /// <summary>The element for <paramref name="protection"/>, built on <paramref name="original"/>'s other attributes; null for none.</summary>
    public static XElement? Element(DocumentProtection protection, XElement? original)
    {
        if (protection.Kind == ProtectionKind.None && !protection.IsEnforced && !protection.HasPassword)
        {
            return null;
        }

        XElement element = original is null ? new XElement(Ns.W + "documentProtection") : new XElement(original);
        foreach (string name in HashAttributes)
        {
            element.Attribute(Ns.W + name)?.Remove();
        }

        element.Attribute(Ns.W + "edit")?.Remove();
        element.Attribute(Ns.W + "enforcement")?.Remove();
        List<XAttribute> attributes = [];
        attributes.Add(new XAttribute(Ns.W + "edit", protection.Kind switch
        {
            ProtectionKind.ReadOnly => "readOnly",
            ProtectionKind.Comments => "comments",
            ProtectionKind.TrackedChanges => "trackedChanges",
            ProtectionKind.Forms => "forms",
            _ => "none",
        }));
        attributes.Add(new XAttribute(Ns.W + "enforcement", protection.IsEnforced ? "1" : "0"));
        if (protection.HasPassword && protection.HashAlgorithm is { } algorithm)
        {
            string hash = Convert.ToBase64String(protection.Hash.Span);
            string salt = Convert.ToBase64String(protection.Salt.Span);
            string spins = protection.SpinCount.ToString(CultureInfo.InvariantCulture);
            int sid = DocumentProtection.SidFromAlgorithm(algorithm);
            if (protection.ModernAttributes || sid == 0)
            {
                attributes.Add(new XAttribute(Ns.W + "algorithmName", algorithm));
                attributes.Add(new XAttribute(Ns.W + "hashValue", hash));
                attributes.Add(new XAttribute(Ns.W + "saltValue", salt));
                attributes.Add(new XAttribute(Ns.W + "spinCount", spins));
            }
            else
            {
                attributes.Add(new XAttribute(Ns.W + "cryptProviderType", "rsaAES"));
                attributes.Add(new XAttribute(Ns.W + "cryptAlgorithmClass", "hash"));
                attributes.Add(new XAttribute(Ns.W + "cryptAlgorithmType", "typeAny"));
                attributes.Add(new XAttribute(Ns.W + "cryptAlgorithmSid", sid));
                attributes.Add(new XAttribute(Ns.W + "cryptSpinCount", spins));
                attributes.Add(new XAttribute(Ns.W + "hash", hash));
                attributes.Add(new XAttribute(Ns.W + "salt", salt));
            }
        }

        // Ours first, in Word's order, then any others the original had (formatting, for one).
        List<XAttribute> rest = [.. element.Attributes()];
        element.ReplaceAttributes(rest.Where(a => a.IsNamespaceDeclaration), attributes, rest.Where(a => !a.IsNamespaceDeclaration));
        return element;
    }

    /// <summary>A w:permStart as an editable range start, or null when it has no id.</summary>
    public static EditableRangeStart? ReadStart(Document document, XElement element)
    {
        if (DocxUnits.Int(element.W("id")) is not int id)
        {
            return null;
        }

        EditableRangeStart start;
        if (element.W("ed") is { Length: > 0 } editor)
        {
            start = new EditableRangeStart(document, id, editor);
        }
        else
        {
            EditorGroup group = element.W("edGrp") switch
            {
                "administrators" => EditorGroup.Administrators,
                "contributors" => EditorGroup.Contributors,
                "editors" => EditorGroup.Editors,
                "owners" => EditorGroup.Owners,
                "current" => EditorGroup.Current,
                _ => EditorGroup.Everyone,
            };
            start = new EditableRangeStart(document, id, group);
        }

        if (DocxUnits.Int(element.W("colFirst")) is int first && first >= 0)
        {
            start.FirstColumn = first;
        }

        if (DocxUnits.Int(element.W("colLast")) is int last && last >= 0)
        {
            start.LastColumn = last;
        }

        return start;
    }

    public static XElement StartElement(EditableRangeStart start)
    {
        XElement element = new(Ns.W + "permStart", new XAttribute(Ns.W + "id", start.Id));
        if (start.Editor is { } editor)
        {
            element.Add(new XAttribute(Ns.W + "ed", editor));
        }
        else
        {
            element.Add(new XAttribute(Ns.W + "edGrp", start.Group switch
            {
                EditorGroup.Administrators => "administrators",
                EditorGroup.Contributors => "contributors",
                EditorGroup.Editors => "editors",
                EditorGroup.Owners => "owners",
                EditorGroup.Current => "current",
                _ => "everyone",
            }));
        }

        if (start.FirstColumn is int first)
        {
            element.Add(new XAttribute(Ns.W + "colFirst", first));
        }

        if (start.LastColumn is int last)
        {
            element.Add(new XAttribute(Ns.W + "colLast", last));
        }

        return element;
    }

    private static byte[]? Base64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        byte[] buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out int written) ? buffer[..written] : null;
    }
}
