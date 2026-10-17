using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>Settings, theme, document properties and embedded fonts.</summary>
internal sealed partial class DocxWriter
{
    private static readonly string[] SettingsOrder =
    [
        "writeProtection", "view", "zoom", "removePersonalInformation", "removeDateAndTime", "doNotDisplayPageBoundaries", "displayBackgroundShape",
        "printPostScriptOverText", "printFractionalCharacterWidth", "printFormsData", "embedTrueTypeFonts", "embedSystemFonts", "saveSubsetFonts",
        "saveFormsData", "mirrorMargins", "alignBordersAndEdges", "bordersDoNotSurroundHeader", "bordersDoNotSurroundFooter", "gutterAtTop",
        "hideSpellingErrors", "hideGrammaticalErrors", "activeWritingStyle", "proofState", "formsDesign", "attachedTemplate", "linkStyles",
        "stylePaneFormatFilter", "stylePaneSortMethod", "documentType", "mailMerge", "revisionView", "trackRevisions", "doNotTrackMoves",
        "doNotTrackFormatting", "documentProtection", "autoFormatOverride", "styleLockTheme", "styleLockQFSet", "defaultTabStop", "autoHyphenation",
        "consecutiveHyphenLimit", "hyphenationZone", "doNotHyphenateCaps", "showEnvelope", "summaryLength", "clickAndTypeStyle", "defaultTableStyle",
        "evenAndOddHeaders", "bookFoldRevPrinting", "bookFoldPrinting", "bookFoldPrintingSheets", "drawingGridHorizontalSpacing",
        "drawingGridVerticalSpacing", "displayHorizontalDrawingGridEvery", "displayVerticalDrawingGridEvery", "doNotUseMarginsForDrawingGridOrigin",
        "drawingGridHorizontalOrigin", "drawingGridVerticalOrigin", "doNotShadeFormData", "noPunctuationKerning", "characterSpacingControl",
        "printTwoOnOne", "strictFirstAndLastChars", "noLineBreaksAfter", "noLineBreaksBefore", "savePreviewPicture", "doNotValidateAgainstSchema",
        "saveInvalidXml", "ignoreMixedContent", "alwaysShowPlaceholderText", "doNotDemarcateInvalidXml", "saveXmlDataOnly", "useXSLTWhenSaving",
        "saveThroughXslt", "showXMLTags", "alwaysMergeEmptyNamespace", "updateFields", "hdrShapeDefaults", "footnotePr", "endnotePr", "compat",
        "docVars", "rsids", "mathPr", "attachedSchema", "themeFontLang", "clrSchemeMapping", "doNotIncludeSubdocsInStats",
        "doNotAutoCompressPictures", "forceUpgrade", "captions", "readModeInkLockDown", "smartTagType", "schemaLibrary", "shapeDefaults",
        "doNotEmbedSmartTags", "decimalSymbol", "listSeparator",
    ];

    /// <summary>
    /// Settings are patched: the original part keeps everything the model does not map, and only the values the model
    /// changed are set, in schema order. A new document gets Word 2013's defaults.
    /// </summary>
    private void WriteSettings()
    {
        OpcPart? original = OriginalPart(OpcRelationshipTypes.Settings);
        XElement root = original?.LoadXml().Root ?? NewSettings();
        DocumentSettings settings = document.Settings;
        DocumentSettings? read = null;
        if (original is not null || data is not null)
        {
            // What the reader found (a file without settings reads as the defaults), so only changes are written.
            Document scratch = new(blank: true);
            read = scratch.Settings;
            if (original is not null)
            {
                ReadSettingsInto(root, read);
            }
        }

        bool changed = data is null;
        if (read is null || read.DefaultTabStop != settings.DefaultTabStop)
        {
            Set(root, DocxPropertyWriter.Val("defaultTabStop", DocxUnits.Twips(settings.DefaultTabStop)));
            changed = true;
        }

        if (read is null ? settings.EvenAndOddHeaders : read.EvenAndOddHeaders != settings.EvenAndOddHeaders)
        {
            SetFlag(root, "evenAndOddHeaders", settings.EvenAndOddHeaders);
            changed = true;
        }

        if (read is null ? settings.TrackRevisions : read.TrackRevisions != settings.TrackRevisions)
        {
            SetFlag(root, "trackRevisions", settings.TrackRevisions);
            changed = true;
        }

        if (read is null ? settings.MirrorMargins : read.MirrorMargins != settings.MirrorMargins)
        {
            SetFlag(root, "mirrorMargins", settings.MirrorMargins);
            changed = true;
        }

        if (read is null ? settings.GutterAtTop : read.GutterAtTop != settings.GutterAtTop)
        {
            SetFlag(root, "gutterAtTop", settings.GutterAtTop);
            changed = true;
        }

        if (read is null || read.FootnoteNumberStyle != settings.FootnoteNumberStyle || read.FootnoteStartAt != settings.FootnoteStartAt)
        {
            changed |= SetNoteProperties(root, "footnotePr", settings.FootnoteNumberStyle, settings.FootnoteStartAt, NumberStyle.Decimal);
        }

        if (read is null || read.EndnoteNumberStyle != settings.EndnoteNumberStyle || read.EndnoteStartAt != settings.EndnoteStartAt)
        {
            changed |= SetNoteProperties(root, "endnotePr", settings.EndnoteNumberStyle, settings.EndnoteStartAt, NumberStyle.LowerRoman);
        }

        if (read is null || read.CompatibilityMode != settings.CompatibilityMode)
        {
            XElement compat = root.Element(Ns.W + "compat") ?? Set(root, new XElement(Ns.W + "compat"));
            XElement? mode = compat.Elements(Ns.W + "compatSetting").FirstOrDefault(s => s.W("name") == "compatibilityMode");
            if (settings.CompatibilityMode == 0)
            {
                mode?.Remove();
            }
            else
            {
                if (mode is null)
                {
                    mode = new XElement(Ns.W + "compatSetting", new XAttribute(Ns.W + "name", "compatibilityMode"), new XAttribute(Ns.W + "uri", "http://schemas.microsoft.com/office/word"));
                    compat.Add(mode);
                }

                mode.SetAttributeValue(Ns.W + "val", settings.CompatibilityMode);
            }

            changed = true;
        }

        if (!changed)
        {
            if (original is not null)
            {
                writtenParts.Add(original.Name);
            }

            return;
        }

        string name = PartFor(OpcRelationshipTypes.Settings, original?.Name ?? "/word/settings.xml");
        package.AddXmlPart(name, WordType + "settings+xml", Xml(root));
        writtenParts.Add(name);
    }

    private static void ReadSettingsInto(XElement root, DocumentSettings settings)
    {
        if (DocxUnits.Points(root.Element(Ns.W + "defaultTabStop").Val()) is double tab && tab > 0)
        {
            settings.DefaultTabStop = tab;
        }

        settings.EvenAndOddHeaders = root.Element(Ns.W + "evenAndOddHeaders") is { } evenOdd && DocxUnits.OnOff(evenOdd);
        settings.TrackRevisions = root.Element(Ns.W + "trackRevisions") is { } track && DocxUnits.OnOff(track);
        settings.MirrorMargins = root.Element(Ns.W + "mirrorMargins") is { } mirror && DocxUnits.OnOff(mirror);
        settings.GutterAtTop = root.Element(Ns.W + "gutterAtTop") is { } gutterAtTop && DocxUnits.OnOff(gutterAtTop);
        if (root.Element(Ns.W + "footnotePr") is { } footnotePr)
        {
            if (footnotePr.Element(Ns.W + "numFmt").Val() is { } format)
            {
                settings.FootnoteNumberStyle = DocxProperties.NumberFormat(format);
            }

            if (DocxUnits.Int(footnotePr.Element(Ns.W + "numStart").Val()) is int start && start >= 1)
            {
                settings.FootnoteStartAt = start;
            }
        }

        if (root.Element(Ns.W + "endnotePr") is { } endnotePr)
        {
            if (endnotePr.Element(Ns.W + "numFmt").Val() is { } format)
            {
                settings.EndnoteNumberStyle = DocxProperties.NumberFormat(format);
            }

            if (DocxUnits.Int(endnotePr.Element(Ns.W + "numStart").Val()) is int start && start >= 1)
            {
                settings.EndnoteStartAt = start;
            }
        }

        // Settings without a compatibility mode are Word 2007's, which Word lays out in its mode 12.
        settings.CompatibilityMode = 12;
        foreach (XElement setting in root.Element(Ns.W + "compat")?.Elements(Ns.W + "compatSetting") ?? [])
        {
            if (setting.W("name") == "compatibilityMode" && DocxUnits.Int(setting.W("val")) is int mode && mode is >= 0 and <= 100)
            {
                settings.CompatibilityMode = mode;
            }
        }
    }

    private static bool SetNoteProperties(XElement root, string name, NumberStyle style, int start, NumberStyle defaultStyle)
    {
        XElement? properties = root.Element(Ns.W + name);
        if (properties is null && style == defaultStyle && start == 1)
        {
            return false;
        }

        properties ??= Set(root, new XElement(Ns.W + name));
        properties.Element(Ns.W + "numFmt")?.Remove();
        properties.Element(Ns.W + "numStart")?.Remove();
        List<XElement> notes = [.. properties.Elements(Ns.W + "footnote").Concat(properties.Elements(Ns.W + "endnote"))];
        foreach (XElement note in notes)
        {
            note.Remove();
        }

        // CT_FtnDocProps: pos, numFmt, numStart, numRestart, then the separator notes.
        XElement? position = properties.Element(Ns.W + "pos");
        XElement format = DocxPropertyWriter.Val("numFmt", DocxPropertyWriter.NumberFormatName(style));
        if (position is not null)
        {
            position.AddAfterSelf(format);
        }
        else
        {
            properties.AddFirst(format);
        }

        if (start != 1)
        {
            format.AddAfterSelf(DocxPropertyWriter.Val("numStart", start.ToString(CultureInfo.InvariantCulture)));
        }

        properties.Add(notes);
        return true;
    }

    /// <summary>Puts <paramref name="element"/> into settings in schema order, replacing one of the same name.</summary>
    private static XElement Set(XElement root, XElement element)
    {
        if (root.Element(element.Name) is { } existing)
        {
            existing.ReplaceWith(element);
            return element;
        }

        int rank = Array.IndexOf(SettingsOrder, element.Name.LocalName);
        XElement? after = root.Elements().LastOrDefault(e => e.Name.Namespace == Ns.W && Array.IndexOf(SettingsOrder, e.Name.LocalName) is int r && r >= 0 && r < rank);
        if (after is not null)
        {
            after.AddAfterSelf(element);
        }
        else
        {
            root.AddFirst(element);
        }

        return element;
    }

    private static void SetFlag(XElement root, string name, bool on)
    {
        if (on)
        {
            Set(root, new XElement(Ns.W + name));
        }
        else
        {
            root.Element(Ns.W + name)?.Remove();
        }
    }

    private static XElement NewSettings()
    {
        return new XElement(
            Ns.W + "settings",
            new XAttribute(XNamespace.Xmlns + "w", Ns.W.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "m", Ns.M.NamespaceName),
            new XElement(Ns.W + "zoom", new XAttribute(Ns.W + "percent", 100)),
            DocxPropertyWriter.Val("defaultTabStop", "720"),
            DocxPropertyWriter.Val("characterSpacingControl", "doNotCompress"),
            new XElement(Ns.W + "footnotePr", new XElement(Ns.W + "footnote", new XAttribute(Ns.W + "id", -1)), new XElement(Ns.W + "footnote", new XAttribute(Ns.W + "id", 0))),
            new XElement(Ns.W + "endnotePr", new XElement(Ns.W + "endnote", new XAttribute(Ns.W + "id", -1)), new XElement(Ns.W + "endnote", new XAttribute(Ns.W + "id", 0))),
            new XElement(Ns.W + "compat", new XElement(Ns.W + "compatSetting", new XAttribute(Ns.W + "name", "compatibilityMode"), new XAttribute(Ns.W + "uri", "http://schemas.microsoft.com/office/word"), new XAttribute(Ns.W + "val", 15))),
            DocxPropertyWriter.Val("decimalSymbol", "."),
            DocxPropertyWriter.Val("listSeparator", ","));
    }

    private void WriteTheme()
    {
        OpcPart? original = OriginalPart(OpcRelationshipTypes.Theme);
        if (KeepAbsent(original))
        {
            return;
        }

        if (original is not null && FormattingUnchanged)
        {
            writtenParts.Add(original.Name);
            return;
        }

        XElement root = original?.LoadXml().Root ?? XElement.Parse(DefaultTheme);
        Theme theme = document.Theme;
        root.SetAttributeValue("name", theme.Name);
        XElement? elements = root.Element(Ns.A + "themeElements");
        if (elements?.Element(Ns.A + "clrScheme") is { } scheme)
        {
            foreach ((string slot, ThemeColor color) in (ReadOnlySpan<(string, ThemeColor)>)
            [
                ("dk1", ThemeColor.Dark1), ("lt1", ThemeColor.Light1), ("dk2", ThemeColor.Dark2), ("lt2", ThemeColor.Light2),
                ("accent1", ThemeColor.Accent1), ("accent2", ThemeColor.Accent2), ("accent3", ThemeColor.Accent3),
                ("accent4", ThemeColor.Accent4), ("accent5", ThemeColor.Accent5), ("accent6", ThemeColor.Accent6),
                ("hlink", ThemeColor.Hyperlink), ("folHlink", ThemeColor.FollowedHyperlink),
            ])
            {
                if (scheme.Element(Ns.A + slot) is not { } entry)
                {
                    continue;
                }

                string hex = DocxPropertyWriter.Hex(theme.Colors[color]);
                XElement? value = entry.Elements().FirstOrDefault();
                if (value?.Name == Ns.A + "sysClr")
                {
                    if (!string.Equals((string?)value.Attribute("lastClr"), hex, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ReplaceNodes(new XElement(Ns.A + "srgbClr", new XAttribute("val", hex)));
                    }
                }
                else if (!string.Equals((string?)value?.Attribute("val"), hex, StringComparison.OrdinalIgnoreCase))
                {
                    entry.ReplaceNodes(new XElement(Ns.A + "srgbClr", new XAttribute("val", hex)));
                }
            }
        }

        if (elements?.Element(Ns.A + "fontScheme") is { } fonts)
        {
            SetTypeface(fonts.Element(Ns.A + "majorFont"), "latin", theme.MajorLatinFont);
            SetTypeface(fonts.Element(Ns.A + "minorFont"), "latin", theme.MinorLatinFont);
            SetTypeface(fonts.Element(Ns.A + "majorFont"), "ea", theme.MajorEastAsiaFont);
            SetTypeface(fonts.Element(Ns.A + "minorFont"), "ea", theme.MinorEastAsiaFont);
            SetTypeface(fonts.Element(Ns.A + "majorFont"), "cs", theme.MajorComplexScriptFont);
            SetTypeface(fonts.Element(Ns.A + "minorFont"), "cs", theme.MinorComplexScriptFont);
        }

        string name = PartFor(OpcRelationshipTypes.Theme, original?.Name ?? "/word/theme/theme1.xml");
        package.AddXmlPart(name, "application/vnd.openxmlformats-officedocument.theme+xml", Xml(root));
        writtenParts.Add(name);

        static void SetTypeface(XElement? font, string script, string typeface)
        {
            if (font?.Element(Ns.A + script) is { } element)
            {
                element.SetAttributeValue("typeface", typeface);
            }
        }
    }

    private void WriteProperties()
    {
        DocumentProperties properties = document.Properties;
        OpcPart? core = data?.Package.GetRelatedPart(data.Package.Relationships, OpcRelationshipTypes.CoreProperties);
        XElement root = core?.LoadXml().Root ?? new XElement(
            Ns.Cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", Ns.Cp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", Ns.Dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dcterms", Ns.DcTerms.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dcmitype", "http://purl.org/dc/dcmitype/"),
            new XAttribute(XNamespace.Xmlns + "xsi", Ns.Xsi.NamespaceName));
        bool changed = core is null && data is null;
        changed |= SetText(root, Ns.Dc + "title", properties.Title);
        changed |= SetText(root, Ns.Dc + "subject", properties.Subject);
        changed |= SetText(root, Ns.Dc + "creator", properties.Author);
        changed |= SetText(root, Ns.Cp + "keywords", properties.Keywords);
        changed |= SetText(root, Ns.Dc + "description", properties.Comments);
        changed |= SetText(root, Ns.Cp + "lastModifiedBy", properties.LastSavedBy);
        changed |= SetDate(root, Ns.Cp + "lastPrinted", properties.Printed);
        changed |= SetDate(root, Ns.DcTerms + "created", properties.Created);
        changed |= SetDate(root, Ns.DcTerms + "modified", properties.Saved);
        changed |= SetText(root, Ns.Cp + "category", properties.Category);
        if (changed)
        {
            string name = core?.Name ?? "/docProps/core.xml";
            if (core is null)
            {
                package.AddRelationship("/", package.NextRelationshipId("/"), OpcRelationshipTypes.CoreProperties, name[1..]);
            }

            package.AddXmlPart(name, "application/vnd.openxmlformats-package.core-properties+xml", Xml(root));
            writtenParts.Add(name);
        }

        WriteCustomProperties();
    }

    private static bool SetText(XElement root, XName name, string? value)
    {
        XElement? element = root.Element(name);
        string? current = element is null || element.Value.Length == 0 ? null : element.Value;
        if (current == value)
        {
            return false;
        }

        if (value is null)
        {
            element?.Remove();
        }
        else if (element is not null)
        {
            element.Value = value;
        }
        else
        {
            root.Add(new XElement(name, value));
        }

        return true;
    }

    private static bool SetDate(XElement root, XName name, DateTimeOffset? value)
    {
        XElement? element = root.Element(name);
        DateTimeOffset? current = element is not null && DateTimeOffset.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed : null;
        if (current == value)
        {
            return false;
        }

        element?.Remove();
        if (value is { } date)
        {
            XElement e = new(name, date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (name.Namespace == Ns.DcTerms)
            {
                e.Add(new XAttribute(Ns.Xsi + "type", "dcterms:W3CDTF"));
            }

            root.Add(e);
        }

        return true;
    }

    private void WriteCustomProperties()
    {
        DocumentProperties properties = document.Properties;
        OpcPart? custom = data?.Package.GetRelatedPart(data.Package.Relationships, OpcRelationshipTypes.CustomProperties);
        XElement? originalRoot = custom?.LoadXml().Root;
        Dictionary<string, string> current = new(StringComparer.Ordinal);
        foreach (string name in properties.CustomNames)
        {
            current[name] = properties.GetCustom(name)!;
        }

        Dictionary<string, string> read = new(StringComparer.Ordinal);
        foreach (XElement property in originalRoot?.Elements() ?? [])
        {
            if ((string?)property.Attribute("name") is { Length: > 0 } name && property.Elements().FirstOrDefault() is { } value)
            {
                read.TryAdd(name, value.Value);
            }
        }

        if (current.Count == read.Count && current.All(c => read.TryGetValue(c.Key, out string? v) && v == c.Value))
        {
            return;
        }

        XNamespace ns = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
        XNamespace vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
        XElement root = originalRoot is not null ? new XElement(originalRoot.Name, originalRoot.Attributes())
            : new XElement(ns + "Properties", new XAttribute(XNamespace.Xmlns + "vt", vt.NamespaceName));
        int pid = 2;
        foreach ((string name, string value) in current)
        {
            XElement? kept = originalRoot?.Elements().FirstOrDefault(p => (string?)p.Attribute("name") == name);
            if (kept is not null && read.GetValueOrDefault(name) == value)
            {
                root.Add(new XElement(kept));
            }
            else
            {
                root.Add(new XElement(root.Name.Namespace + "property", new XAttribute("fmtid", "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}"), new XAttribute("pid", pid), new XAttribute("name", name),
                    new XElement(vt + "lpwstr", value)));
            }

            pid++;
        }

        // pids must be unique and start at 2.
        int next = 2;
        foreach (XElement property in root.Elements())
        {
            property.SetAttributeValue("pid", next++);
        }

        string partName = custom?.Name ?? "/docProps/custom.xml";
        if (custom is null)
        {
            package.AddRelationship("/", package.NextRelationshipId("/"), OpcRelationshipTypes.CustomProperties, partName[1..]);
        }

        package.AddXmlPart(partName, "application/vnd.openxmlformats-officedocument.custom-properties+xml", Xml(root));
        writtenParts.Add(partName);
    }

    /// <summary>A new document's embedded fonts go into a font table, obfuscated as ECMA-376 Part 1 section 17.8.1 says.</summary>
    private void WriteFonts()
    {
        if (OriginalPart(OpcRelationshipTypes.FontTable) is not null || document.EmbeddedFonts.Count == 0)
        {
            return;
        }

        string name = PartFor(OpcRelationshipTypes.FontTable, "/word/fontTable.xml");
        XElement root = new(Ns.W + "fonts", new XAttribute(XNamespace.Xmlns + "w", Ns.W.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", Ns.R.NamespaceName));
        foreach (IGrouping<string, EmbeddedFont> family in document.EmbeddedFonts.GroupBy(f => f.Family, StringComparer.Ordinal))
        {
            XElement font = new(Ns.W + "font", new XAttribute(Ns.W + "name", family.Key));
            foreach (EmbeddedFont face in family)
            {
                string key = Guid.NewGuid().ToString("B").ToUpperInvariant();
                byte[] bytes = face.Data.ToArray();
                DocxReader.Deobfuscate(bytes, key);
                string partName = UniquePartName("/word/fonts/font", ".odttf");
                package.AddPart(partName, "application/vnd.openxmlformats-officedocument.obfuscatedFont", bytes);
                writtenParts.Add(partName);
                string id = package.NextRelationshipId(name);
                package.AddRelationship(name, id, OpcRelationshipTypes.Font, RelationshipTarget(name, partName));
                string element = (face.Bold, face.Italic) switch
                {
                    (true, true) => "embedBoldItalic",
                    (true, false) => "embedBold",
                    (false, true) => "embedItalic",
                    _ => "embedRegular",
                };
                font.Add(new XElement(Ns.W + element, new XAttribute(Ns.R + "id", id), new XAttribute(Ns.W + "fontKey", key)));
            }

            root.Add(font);
        }

        package.AddXmlPart(name, WordType + "fontTable+xml", Xml(root));
        writtenParts.Add(name);
    }

    private const string DefaultTheme = """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office Theme"><a:themeElements>
        <a:clrScheme name="Office"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme>
        <a:fontScheme name="Office"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme>
        <a:fmtScheme name="Office"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
        <a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
        <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
        <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>
        </a:themeElements></a:theme>
        """;
}
