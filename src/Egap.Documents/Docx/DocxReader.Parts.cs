using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

namespace Egap.Documents.Docx;

/// <summary>The parts around the body: theme, styles, numbering, settings, properties, fonts, notes and comments.</summary>
internal sealed partial class DocxReader
{
    private OpcPart? Related(string type)
    {
        OpcPart? part = package.GetRelatedPart(main.Relationships, type);
        if (part is not null)
        {
            data.MappedParts.Add(part.Name);
        }

        return part;
    }

    private void ReadTheme()
    {
        if (Related(OpcRelationshipTypes.Theme)?.LoadXml().Root is not { } root)
        {
            return;
        }

        Theme theme = document.Theme;
        if ((string?)root.Attribute("name") is { } name)
        {
            theme.Name = name;
        }

        XElement? elements = root.Element(Ns.A + "themeElements");
        if (elements?.Element(Ns.A + "clrScheme") is { } scheme)
        {
            (string Name, ThemeColor Color)[] slots =
            [
                ("dk1", ThemeColor.Dark1), ("lt1", ThemeColor.Light1), ("dk2", ThemeColor.Dark2), ("lt2", ThemeColor.Light2),
                ("accent1", ThemeColor.Accent1), ("accent2", ThemeColor.Accent2), ("accent3", ThemeColor.Accent3),
                ("accent4", ThemeColor.Accent4), ("accent5", ThemeColor.Accent5), ("accent6", ThemeColor.Accent6),
                ("hlink", ThemeColor.Hyperlink), ("folHlink", ThemeColor.FollowedHyperlink),
            ];
            foreach ((string slot, ThemeColor color) in slots)
            {
                XElement? value = scheme.Element(Ns.A + slot)?.Elements().FirstOrDefault();
                string? hex = value?.Name.LocalName switch
                {
                    "srgbClr" => (string?)value.Attribute("val"),
                    "sysClr" => (string?)value.Attribute("lastClr"),
                    _ => null,
                };
                if (DocxProperties.ParseHex(hex) is Rgba8 rgb)
                {
                    theme.SetColor(color, rgb);
                }
            }
        }

        if (elements?.Element(Ns.A + "fontScheme") is { } fonts)
        {
            XElement? major = fonts.Element(Ns.A + "majorFont");
            XElement? minor = fonts.Element(Ns.A + "minorFont");
            theme.MajorLatinFont = Typeface(major, "latin") ?? theme.MajorLatinFont;
            theme.MinorLatinFont = Typeface(minor, "latin") ?? theme.MinorLatinFont;
            theme.MajorEastAsiaFont = Typeface(major, "ea") ?? theme.MajorEastAsiaFont;
            theme.MinorEastAsiaFont = Typeface(minor, "ea") ?? theme.MinorEastAsiaFont;
            theme.MajorComplexScriptFont = Typeface(major, "cs") ?? theme.MajorComplexScriptFont;
            theme.MinorComplexScriptFont = Typeface(minor, "cs") ?? theme.MinorComplexScriptFont;
        }

        static string? Typeface(XElement? font, string script)
        {
            return (string?)font?.Element(Ns.A + script)?.Attribute("typeface");
        }
    }

    private void ReadStylesAndNumbering()
    {
        XElement? styles = Related(OpcRelationshipTypes.Styles)?.LoadXml().Root;
        XElement? numbering = Related(OpcRelationshipTypes.Numbering)?.LoadXml().Root;

        // Styles first as empty shells, so numbering can link to them and their formats can name lists.
        List<(XElement Element, Style Style)> read = [];
        foreach (XElement element in styles?.Elements(Ns.W + "style") ?? [])
        {
            string? id = element.W("styleId");
            if (string.IsNullOrEmpty(id) || document.Styles[id] is not null)
            {
                continue;
            }

            string name = element.Element(Ns.W + "name").Val() ?? id;
            Style style = element.W("type") switch
            {
                "character" => document.Styles.AddCharacterStyle(id, name),
                "table" => document.Styles.AddTableStyle(id, name),
                "numbering" => document.Styles.AddListStyle(id, name),
                _ => document.Styles.AddParagraphStyle(id, name),
            };
            read.Add((element, style));
            data.Styles[id] = element;
        }

        ReadNumbering(numbering);
        ResolveStyleLinks();

        if (styles?.Element(Ns.W + "docDefaults") is { } defaults)
        {
            DocxProperties.ReadRun(defaults.Element(Ns.W + "rPrDefault")?.Element(Ns.W + "rPr"), document.Styles.DefaultRunFormat);
            DocxProperties.ReadParagraph(defaults.Element(Ns.W + "pPrDefault")?.Element(Ns.W + "pPr"), document.Styles.DefaultParagraphFormat);
        }

        foreach ((XElement element, Style style) in read)
        {
            ReadStyle(element, style);
        }
    }

    private void ReadStyle(XElement element, Style style)
    {
        if (element.Element(Ns.W + "basedOn").Val() is { } basedOn && document.Styles[basedOn] is { } parent && parent.Type == style.Type)
        {
            try
            {
                style.BasedOn = parent;
            }
            catch (ArgumentException)
            {
                // A loop of basedOn links; the style keeps no base.
            }
        }

        style.Hidden = element.Element(Ns.W + "hidden") is { } hidden ? DocxUnits.OnOff(hidden) : element.Element(Ns.W + "semiHidden") is { } semi && DocxUnits.OnOff(semi);
        style.Priority = DocxUnits.Int(element.Element(Ns.W + "uiPriority").Val());
        style.IsPrimary = element.Element(Ns.W + "qFormat") is { } primary && DocxUnits.OnOff(primary);
        if (element.W("default") is "1" or "true" or "on")
        {
            document.Styles.SetDefault(style);
        }

        XElement? pPr = element.Element(Ns.W + "pPr");
        XElement? rPr = element.Element(Ns.W + "rPr");
        switch (style)
        {
            case ParagraphStyle paragraph:
                ReadNumbering(pPr?.Element(Ns.W + "numPr"), paragraph.ParagraphFormat);
                DocxProperties.ReadParagraph(pPr, paragraph.ParagraphFormat);
                DocxProperties.ReadRun(rPr, paragraph.RunFormat);
                if (element.Element(Ns.W + "next").Val() is { } next && document.Styles[next] is ParagraphStyle nextStyle)
                {
                    paragraph.Next = nextStyle;
                }

                if (element.Element(Ns.W + "link").Val() is { } link && document.Styles[link] is CharacterStyle linked)
                {
                    paragraph.Linked = linked;
                }

                break;
            case CharacterStyle character:
                DocxProperties.ReadRun(rPr, character.RunFormat);
                break;
            case TableStyle table:
                DocxProperties.ReadParagraph(pPr, table.ParagraphFormat);
                DocxProperties.ReadRun(rPr, table.RunFormat);
                DocxProperties.ReadTable(element.Element(Ns.W + "tblPr"), table.TableFormat);
                DocxProperties.ReadRow(element.Element(Ns.W + "trPr"), table.RowFormat);
                DocxProperties.ReadCell(element.Element(Ns.W + "tcPr"), table.CellFormat);
                foreach (XElement conditional in element.Elements(Ns.W + "tblStylePr"))
                {
                    TableStyleRegion? region = conditional.W("type") switch
                    {
                        "firstRow" => TableStyleRegion.FirstRow,
                        "lastRow" => TableStyleRegion.LastRow,
                        "firstCol" => TableStyleRegion.FirstColumn,
                        "lastCol" => TableStyleRegion.LastColumn,
                        "band1Horz" => TableStyleRegion.OddRowBand,
                        "band2Horz" => TableStyleRegion.EvenRowBand,
                        "band1Vert" => TableStyleRegion.OddColumnBand,
                        "band2Vert" => TableStyleRegion.EvenColumnBand,
                        "neCell" => TableStyleRegion.TopRightCell,
                        "nwCell" => TableStyleRegion.TopLeftCell,
                        "seCell" => TableStyleRegion.BottomRightCell,
                        "swCell" => TableStyleRegion.BottomLeftCell,
                        _ => null,
                    };
                    if (region is { } r)
                    {
                        TableStyleConditional target = table[r];
                        DocxProperties.ReadParagraph(conditional.Element(Ns.W + "pPr"), target.ParagraphFormat);
                        DocxProperties.ReadRun(conditional.Element(Ns.W + "rPr"), target.RunFormat);
                        DocxProperties.ReadTable(conditional.Element(Ns.W + "tblPr"), target.TableFormat);
                        DocxProperties.ReadRow(conditional.Element(Ns.W + "trPr"), target.RowFormat);
                        DocxProperties.ReadCell(conditional.Element(Ns.W + "tcPr"), target.CellFormat);
                    }
                }

                break;
            case ListStyle list:
                if (DocxUnits.Int(pPr?.Element(Ns.W + "numPr")?.Element(Ns.W + "numId").Val()) is int numId && document.Lists[numId] is { } styleList)
                {
                    list.List = styleList;
                }

                break;
        }
    }

    private void ReadNumbering(XElement? numbering)
    {
        if (numbering is null)
        {
            return;
        }

        Dictionary<int, ListDefinition> definitions = [];
        Dictionary<int, string> styleLinks = [];
        foreach (XElement abstractNum in numbering.Elements(Ns.W + "abstractNum"))
        {
            if (DocxUnits.Int(abstractNum.W("abstractNumId")) is not int id || id < 0 || definitions.ContainsKey(id))
            {
                continue;
            }

            ListDefinition definition = document.Lists.AddDefinition(id);
            definitions[id] = definition;
            definition.Kind = abstractNum.Element(Ns.W + "multiLevelType").Val() switch
            {
                "singleLevel" => ListKind.SingleLevel,
                "multilevel" => ListKind.Multilevel,
                _ => ListKind.HybridMultilevel,
            };
            definition.Name = abstractNum.Element(Ns.W + "name").Val();
            if (abstractNum.Element(Ns.W + "numStyleLink").Val() is { } link)
            {
                styleLinks[id] = link;
            }

            foreach (XElement lvl in abstractNum.Elements(Ns.W + "lvl"))
            {
                if (DocxUnits.Int(lvl.W("ilvl")) is int index && index is >= 0 and <= 8)
                {
                    ReadLevel(lvl, definition.Levels[index]);
                }
            }
        }

        List<(XElement Element, int AbstractId, List List)> nums = [];
        foreach (XElement num in numbering.Elements(Ns.W + "num"))
        {
            if (DocxUnits.Int(num.W("numId")) is not int numId || numId <= 0 || document.Lists[numId] is not null
                || DocxUnits.Int(num.Element(Ns.W + "abstractNumId").Val()) is not int abstractId || !definitions.TryGetValue(abstractId, out ListDefinition? definition))
            {
                continue;
            }

            nums.Add((num, abstractId, document.Lists.Add(numId, definition)));
        }

        foreach ((XElement num, int abstractId, List list) in nums)
        {
            foreach (XElement levelOverride in num.Elements(Ns.W + "lvlOverride"))
            {
                if (DocxUnits.Int(levelOverride.W("ilvl")) is not int index || index is < 0 or > 8)
                {
                    continue;
                }

                ListLevelOverride target = list.Overrides[index];
                if (DocxUnits.Int(levelOverride.Element(Ns.W + "startOverride").Val()) is int start)
                {
                    target.StartAt = Math.Max(0, start);
                }

                if (levelOverride.Element(Ns.W + "lvl") is { } lvl)
                {
                    ReadLevel(lvl, target.CreateLevel());
                }
            }

            if (styleLinks.TryGetValue(abstractId, out string? styleId))
            {
                styleLinkedLists.Add((list.Id, styleId));
            }
        }
    }

    /// <summary>
    /// A list whose definition only links to a numbering style (numStyleLink) numbers as the list that style names, so
    /// paragraphs in it are read into that list.
    /// </summary>
    private void ResolveStyleLinks()
    {
        foreach ((int numId, string styleId) in styleLinkedLists)
        {
            if (data.Styles.TryGetValue(styleId, out XElement? element)
                && DocxUnits.Int(element.Element(Ns.W + "pPr")?.Element(Ns.W + "numPr")?.Element(Ns.W + "numId").Val()) is int target
                && target != numId && document.Lists[target] is { } list)
            {
                listRedirects[numId] = list;
            }
        }
    }

    private void ReadLevel(XElement lvl, ListLevel level)
    {
        if (DocxUnits.Int(lvl.Element(Ns.W + "start").Val()) is int start)
        {
            level.StartAt = Math.Max(0, start);
        }

        if (lvl.Element(Ns.W + "numFmt").Val() is { } format)
        {
            level.NumberStyle = DocxProperties.NumberFormat(format);
        }

        if (lvl.Element(Ns.W + "lvlText") is { } text)
        {
            level.LevelText = text.Val() ?? "";
        }

        level.Alignment = lvl.Element(Ns.W + "lvlJc").Val() switch
        {
            "center" => ParagraphAlignment.Center,
            "right" or "end" => ParagraphAlignment.Right,
            _ => ParagraphAlignment.Left,
        };
        level.Suffix = lvl.Element(Ns.W + "suff").Val() switch
        {
            "space" => ListLabelSuffix.Space,
            "nothing" => ListLabelSuffix.Nothing,
            _ => ListLabelSuffix.Tab,
        };
        if (lvl.Element(Ns.W + "lvlRestart") is { } restart)
        {
            level.RestartAfterLevel = DocxUnits.Int(restart.Val()) is int after ? Math.Clamp(after, 0, 9) : null;
        }

        level.IsLegal = lvl.Element(Ns.W + "isLgl") is { } legal && DocxUnits.OnOff(legal);
        if (lvl.Element(Ns.W + "pStyle").Val() is { } styleId && document.Styles[styleId] is ParagraphStyle style)
        {
            level.LinkedStyle = style;
        }

        DocxProperties.ReadParagraph(lvl.Element(Ns.W + "pPr"), level.ParagraphFormat);
        DocxProperties.ReadRun(lvl.Element(Ns.W + "rPr"), level.RunFormat);
    }

    private void ReadSettings()
    {
        if (Related(OpcRelationshipTypes.Settings)?.LoadXml().Root is not { } root)
        {
            return;
        }

        DocumentSettings settings = document.Settings;
        if (DocxUnits.Points(root.Element(Ns.W + "defaultTabStop").Val()) is double tab && tab > 0)
        {
            settings.DefaultTabStop = tab;
        }

        settings.EvenAndOddHeaders = root.Element(Ns.W + "evenAndOddHeaders") is { } evenOdd && DocxUnits.OnOff(evenOdd);
        settings.TrackRevisions = root.Element(Ns.W + "trackRevisions") is { } track && DocxUnits.OnOff(track);
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

        foreach (XElement setting in root.Element(Ns.W + "compat")?.Elements(Ns.W + "compatSetting") ?? [])
        {
            if (setting.W("name") == "compatibilityMode" && DocxUnits.Int(setting.W("val")) is int mode && mode is >= 0 and <= 100)
            {
                settings.CompatibilityMode = mode;
            }
        }
    }

    private void ReadProperties()
    {
        if (package.GetRelatedPart(package.Relationships, OpcRelationshipTypes.CoreProperties) is { } core && core.LoadXml().Root is { } root)
        {
            data.MappedParts.Add(core.Name);
            DocumentProperties properties = document.Properties;
            properties.Title = Text(root.Element(Ns.Dc + "title"));
            properties.Subject = Text(root.Element(Ns.Dc + "subject"));
            properties.Author = Text(root.Element(Ns.Dc + "creator"));
            properties.Keywords = Text(root.Element(Ns.Cp + "keywords"));
            properties.Comments = Text(root.Element(Ns.Dc + "description"));
            properties.Category = Text(root.Element(Ns.Cp + "category"));
            properties.LastSavedBy = Text(root.Element(Ns.Cp + "lastModifiedBy"));
            properties.Created = Date(root.Element(Ns.DcTerms + "created"));
            properties.Saved = Date(root.Element(Ns.DcTerms + "modified"));
            properties.Printed = Date(root.Element(Ns.Cp + "lastPrinted"));
        }

        if (package.GetRelatedPart(package.Relationships, OpcRelationshipTypes.CustomProperties) is { } custom && custom.LoadXml().Root is { } customRoot)
        {
            data.MappedParts.Add(custom.Name);
            foreach (XElement property in customRoot.Elements())
            {
                if ((string?)property.Attribute("name") is { Length: > 0 } name && property.Elements().FirstOrDefault() is { } value)
                {
                    document.Properties.SetCustom(name, value.Value);
                }
            }
        }

        static string? Text(XElement? element)
        {
            return element is null || element.Value.Length == 0 ? null : element.Value;
        }

        static DateTimeOffset? Date(XElement? element)
        {
            return element is not null && DateTimeOffset.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value) ? value : null;
        }
    }

    private void ReadFonts()
    {
        if (package.GetRelatedPart(main.Relationships, OpcRelationshipTypes.FontTable) is not { } part || part.LoadXml().Root is not { } root)
        {
            return;
        }

        foreach (XElement font in root.Elements(Ns.W + "font"))
        {
            if (font.W("name") is not { Length: > 0 } family)
            {
                continue;
            }

            foreach ((string name, bool bold, bool italic) in (ReadOnlySpan<(string, bool, bool)>)[("embedRegular", false, false), ("embedBold", true, false), ("embedItalic", false, true), ("embedBoldItalic", true, true)])
            {
                if (font.Element(Ns.W + name) is not { } embed
                    || (string?)embed.Attribute(Ns.R + "id") is not { } id
                    || part.GetRelationship(id) is not { External: false } relationship
                    || package.GetPart(relationship.TargetName) is not { } fontPart)
                {
                    continue;
                }

                byte[] bytes = (byte[])fontPart.Data.Clone();
                if (embed.W("fontKey") is { } key && !Deobfuscate(bytes, key))
                {
                    continue;
                }

                document.EmbeddedFonts.Add(new EmbeddedFont(family, bold, italic, bytes));
            }
        }
    }

    /// <summary>
    /// Undoes the obfuscation ECMA-376 Part 1 section 17.8.1 puts on embedded fonts: the first 32 bytes are XORed with the font
    /// key's 16 bytes, taken from its hex digits in reverse order.
    /// </summary>
    internal static bool Deobfuscate(byte[] bytes, string fontKey)
    {
        string hex = fontKey.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (hex.Length != 32 || bytes.Length < 32)
        {
            return false;
        }

        Span<byte> key = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            if (!byte.TryParse(hex.AsSpan(30 - (2 * i), 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < 32; i++)
        {
            bytes[i] ^= key[i % 16];
        }

        return true;
    }

    private void ReadNoteParts()
    {
        ReadNotes(Related(OpcRelationshipTypes.Footnotes), "footnote", footnotes);
        ReadNotes(Related(OpcRelationshipTypes.Endnotes), "endnote", endnotes);

        void ReadNotes(OpcPart? part, string name, Dictionary<string, XElement> into)
        {
            if (part is null)
            {
                return;
            }

            XDocument xml = part.LoadXml();
            xml.AddAnnotation(part);
            NoteIds(xml.Root);
            foreach (XElement note in xml.Root?.Elements(Ns.W + name) ?? [])
            {
                if (note.W("type") is "separator" or "continuationSeparator" or "continuationNotice")
                {
                    data.SpecialNotes.Add(note);
                }
                else if (note.W("id") is { } id)
                {
                    into.TryAdd(id, note);
                }
            }
        }
    }

    private void ReadCommentParts()
    {
        if (Related(OpcRelationshipTypes.Comments) is { } part)
        {
            XDocument xml = part.LoadXml();
            xml.AddAnnotation(part);
            NoteIds(xml.Root);
            foreach (XElement comment in xml.Root?.Elements(Ns.W + "comment") ?? [])
            {
                if (DocxUnits.Int(comment.W("id")) is not int id || !comments.TryAdd(id, comment))
                {
                    // A second comment with the same id (or none) cannot be named from the text; it is kept as read.
                    data.Unreferenced.Add(comment);
                }
            }
        }

        if (Related(OpcRelationshipTypes.CommentsExtended)?.LoadXml().Root is { } extended)
        {
            // Word 2013's preview wrote these in another namespace than its release; the root says which.
            XNamespace w15 = extended.Name.Namespace;
            foreach (XElement comment in extended.Elements(w15 + "commentEx"))
            {
                if ((string?)comment.Attribute(w15 + "paraId") is { } paraId)
                {
                    commentsExtended.TryAdd(paraId, ((string?)comment.Attribute(w15 + "paraIdParent"), (string?)comment.Attribute(w15 + "done") is "1" or "true"));
                }
            }
        }
    }
}
