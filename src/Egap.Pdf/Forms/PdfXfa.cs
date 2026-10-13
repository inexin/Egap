using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Pdf;

/// <summary>
/// The XFA form of a document (12.7.8, hub Q7): the form's <c>/XFA</c>, one XDP stream or an array of named packets. Egap
/// keeps it on save as it is, reads its packets, and keeps the <c>datasets</c> packet in step with the AcroForm field
/// values: setting a field's value writes it into the data too. A static XFA form converts to a plain AcroForm with
/// <see cref="ConvertToAcroForm"/>; a dynamic one, which has no AcroForm fields of its own to fall back on, is reported by
/// <see cref="IsDynamic"/> and not converted.
/// </summary>
/// <remarks>
/// A field maps to the data by its fully qualified name, as XFA forms name their AcroForm fields
/// (<c>form1[0].page1[0].name[0]</c>): each part is an element of that name, the n-th of its siblings with that name,
/// below <c>xfa:data</c>; unnamed subforms (<c>#subform[0]</c>) and the template's areas bind no data and are skipped.
/// XML is read with DTDs prohibited and no external resolution.
/// </remarks>
public sealed partial class PdfXfa
{
    private static readonly XNamespace DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

    private readonly PdfDocument document;

    internal PdfXfa(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>
    /// Whether the form is dynamic: the catalog's <c>/NeedsRendering</c> is true, or the AcroForm has no fields to show
    /// without the XFA. Dynamic forms are kept but not converted.
    /// </summary>
    public bool IsDynamic => document.Catalog.GetBool("NeedsRendering") == true || document.Form.Count == 0;

    /// <summary>The packet names in order, such as <c>preamble</c>, <c>config</c>, <c>template</c> and <c>datasets</c>.</summary>
    public IReadOnlyList<string> PacketNames
    {
        get
        {
            return Value switch
            {
                PdfArray array => [.. Enumerable.Range(0, array.Count / 2).Select(i => array.GetText(2 * i) ?? "")],
                PdfStream stream => Load(Text(stream))?.Root?.Elements().Select(e => e.Name.LocalName).ToList() ?? [],
                _ => [],
            };
        }
    }

    private PdfObject? Value => document.Form.Dictionary?.Get("XFA");

    /// <summary>A packet's XML as text, or <see langword="null"/> when there is no packet of that name.</summary>
    public string? GetPacket(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        switch (Value)
        {
            case PdfArray array:
                for (int i = 0; i + 1 < array.Count; i += 2)
                {
                    if (array.GetText(i) == name && array.Get<PdfStream>(i + 1) is PdfStream stream)
                    {
                        return Text(stream);
                    }
                }

                return null;
            case PdfStream whole:
                return Load(Text(whole))?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.ToString(SaveOptions.DisableFormatting);
            default:
                return null;
        }
    }

    /// <summary>The <c>datasets</c> packet: the form's data as XML, for export. <see langword="null"/> when there is none.</summary>
    public string? GetDatasets()
    {
        return GetPacket("datasets");
    }

    /// <summary>
    /// The value the data holds for a field, by the field's fully qualified name; <see langword="null"/> when the data has no
    /// element for it.
    /// </summary>
    public string? GetDataValue(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        return Datasets() is XDocument datasets && Find(datasets, fieldName, create: false) is XElement element ? element.Value : null;
    }

    /// <summary>
    /// Writes a value into the data for a field, by its fully qualified name, making the elements on its path when they are
    /// missing. Field setters call this already; it is for data without a field.
    /// </summary>
    /// <returns>Whether the data took it: false when there is no datasets packet or the name maps to no data.</returns>
    public bool SetDataValue(string fieldName, string? value)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        if (Datasets() is not XDocument datasets || Find(datasets, fieldName, create: true) is not XElement element)
        {
            return false;
        }

        element.RemoveNodes();
        if (value is not null)
        {
            element.Value = XmlText(value);
        }

        WriteDatasets(datasets);
        return true;
    }

    /// <summary>
    /// Converts a static XFA form to a plain AcroForm (hub Q7): each field whose data holds a different value takes the data's
    /// value, then <c>/XFA</c> and the catalog's <c>/NeedsRendering</c> are removed.
    /// </summary>
    /// <returns>Whether it was converted: false for a dynamic form, which is left as it is.</returns>
    public bool ConvertToAcroForm()
    {
        if (IsDynamic)
        {
            return false;
        }

        if (Datasets() is XDocument datasets)
        {
            foreach (PdfField field in document.Form.GetTerminalFields())
            {
                if (Find(datasets, field.FullName, create: false) is XElement element && !element.HasElements && element.Value != Current(field))
                {
                    Import(field, element.Value);
                }
            }
        }

        document.Form.Dictionary!.Remove("XFA");
        document.Catalog.Remove("NeedsRendering");
        return true;
    }

    /// <summary>Writes a field's value into the data, after a setter changed it.</summary>
    internal void Update(PdfField field)
    {
        if (Value is null || Datasets() is not XDocument datasets)
        {
            return;
        }

        // Elements are made only for names an XFA form gives its fields, with an index on every part; a field of another
        // naming updates data that is already there, if any.
        bool xfaName = field.FullName.Split('.').All(part => part.StartsWith('#') || PartPattern().Match(part).Groups["index"].Success);
        if (Find(datasets, field.FullName, create: xfaName) is XElement element)
        {
            element.RemoveNodes();
            if (Current(field) is string value)
            {
                element.Value = XmlText(value);
            }

            WriteDatasets(datasets);
        }
    }

    /// <summary>A field's value as XFA data holds it: text, a button's on state or 0 for off, a choice's export value.</summary>
    private static string? Current(PdfField field)
    {
        return field switch
        {
            PdfTextField text => text.Value,
            PdfCheckBoxField box => box.IsChecked ? box.Value : "0",
            PdfRadioButtonField radio => radio.Value,
            PdfChoiceField choice => choice.Values.Count > 0 ? choice.Values[0] : null,
            _ => null,
        };
    }

    /// <summary>Sets a field from the data without writing the data back.</summary>
    private static void Import(PdfField field, string value)
    {
        try
        {
            switch (field)
            {
                case PdfTextField text:
                    text.SetValue(value, updateData: false);
                    break;
                case PdfCheckBoxField box:
                    box.SetChecked(value is not ("" or "0" or "Off"), updateData: false);
                    break;
                case PdfRadioButtonField radio when radio.Options.Contains(value):
                    radio.SetValue(value, updateData: false);
                    break;
                case PdfComboBoxField combo when combo.IsEditable || combo.Options.Any(o => o.ExportValue == value):
                    combo.SetValue(value, updateData: false);
                    break;
            }
        }
        catch (ArgumentException)
        {
            // Data a field cannot take, such as text past its maximum length, leaves the field as it was.
        }
    }

    private XDocument? Datasets()
    {
        try
        {
            return GetDatasets() is string xml ? Load(xml) : null;
        }
        catch (PdfException)
        {
            // Data in a damaged stream is left alone; reading the packet itself reports the damage.
            return null;
        }
    }

    /// <summary>XML parsed with DTDs prohibited and no external resolution; null when it is not well formed.</summary>
    private static XDocument? Load(string xml)
    {
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The data element for a field name, below <c>xfa:data</c>; null when the name has no data part or the data has no
    /// element for it. A named part with no element of its own is skipped, as for a subform that binds no data (such as
    /// a page). When asked to make it, a missing element is made along the full path.
    /// </summary>
    private static XElement? Find(XDocument datasets, string fieldName, bool create)
    {
        if (datasets.Root?.Element(DataNamespace + "data") is not XElement data)
        {
            return null;
        }

        List<(XName Name, int Index)> parts = [];
        foreach (string part in fieldName.Split('.'))
        {
            Match match = PartPattern().Match(part);
            string name = match.Groups["name"].Value;
            if (!match.Success || name.Length == 0 || name.StartsWith('#'))
            {
                continue;
            }

            try
            {
                parts.Add((XName.Get(XmlConvert.VerifyNCName(name)), match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0));
            }
            catch (XmlException)
            {
                return null;
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        // Greedily: each part with an element descends, one without is skipped; the last part must have one.
        XElement? found = data;
        for (int i = 0; i < parts.Count && found is not null; i++)
        {
            XElement? next = found.Elements(parts[i].Name).ElementAtOrDefault(parts[i].Index);
            found = next ?? (i < parts.Count - 1 ? found : null);
        }

        if (found is not null || !create)
        {
            return found;
        }

        XElement at = data;
        foreach ((XName name, int index) in parts)
        {
            List<XElement> siblings = [.. at.Elements(name)];
            if (index < siblings.Count)
            {
                at = siblings[index];
            }
            else if (index == siblings.Count && index < 10_000)
            {
                XElement made = new(name);
                at.Add(made);
                at = made;
            }
            else
            {
                return null;
            }
        }

        return at;
    }

    private void WriteDatasets(XDocument datasets)
    {
        string xml = datasets.Root!.ToString(SaveOptions.DisableFormatting);
        switch (Value)
        {
            case PdfArray array:
                for (int i = 0; i + 1 < array.Count; i += 2)
                {
                    if (array.GetText(i) == "datasets" && array.Get<PdfStream>(i + 1) is PdfStream stream)
                    {
                        stream.SetDecodedBytes(Encoding.UTF8.GetBytes(xml));
                        return;
                    }
                }

                break;
            case PdfStream whole:
                // A whole XDP is written back from its parse: the other packets keep their content, not their exact bytes.
                if (Load(Text(whole)) is XDocument xdp && xdp.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "datasets") is XElement old)
                {
                    old.ReplaceWith(datasets.Root);
                    whole.SetDecodedBytes(Encoding.UTF8.GetBytes(xdp.ToString(SaveOptions.DisableFormatting)));
                }

                break;
        }
    }

    /// <summary>Text with the characters XML cannot hold, such as most control characters, left out.</summary>
    private static string XmlText(string value)
    {
        StringBuilder kept = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (XmlConvert.IsXmlChar(value[i]))
            {
                kept.Append(value[i]);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], value[i]))
            {
                kept.Append(value, i, 2);
                i++;
            }
        }

        return kept.ToString();
    }

    private static string Text(PdfStream stream)
    {
        byte[] bytes = stream.GetDecodedBytes();
        return bytes is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
    }

    [GeneratedRegex(@"^(?<name>[^\[]*)(\[(?<index>\d{1,9})\])?$")]
    private static partial Regex PartPattern();
}
