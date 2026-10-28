using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>Reads and writes the settings part's <c>w:compat</c>: the compatibility mode, the flags and Word's other options.</summary>
internal static class DocxCompatibility
{
    private const string WordUri = "http://schemas.microsoft.com/office/word";

    /// <summary>CT_Compat's children in schema order (ECMA-376 Part 1, 17.15.3); the <c>w:compatSetting</c> entries come after them.</summary>
    private static readonly string[] Order =
    [
        "useSingleBorderforContiguousCells", "wpJustification", "noTabHangInd", "noLeading", "spaceForUL", "noColumnBalance",
        "balanceSingleByteDoubleByteWidth", "noExtraLineSpacing", "doNotLeaveBackslashAlone", "ulTrailSpace", "doNotExpandShiftReturn",
        "spacingInWholePoints", "lineWrapLikeWord6", "printBodyTextBeforeHeader", "printColBlack", "wpSpaceWidth", "showBreaksInFrames",
        "subFontBySize", "suppressBottomSpacing", "suppressTopSpacing", "suppressSpacingAtTopOfPage", "suppressTopSpacingWP",
        "suppressSpBfAfterPgBrk", "swapBordersFacingPages", "convMailMergeEsc", "truncateFontHeightsLikeWP6", "mwSmallCaps",
        "usePrinterMetrics", "doNotSuppressParagraphBorders", "wrapTrailSpaces", "footnoteLayoutLikeWW8", "shapeLayoutLikeWW8",
        "alignTablesRowByRow", "forgetLastTabAlignment", "adjustLineHeightInTable", "autoSpaceLikeWord95", "noSpaceRaiseLower",
        "doNotUseHTMLParagraphAutoSpacing", "layoutRawTableWidth", "layoutTableRowsApart", "useWord97LineBreakRules",
        "doNotBreakWrappedTables", "doNotSnapToGridInCell", "selectFldWithFirstOrLastChar", "applyBreakingRules", "doNotWrapTextWithPunct",
        "doNotUseEastAsianBreakRules", "useWord2002TableStyleRules", "growAutofit", "useFELayout", "useNormalStyleForList",
        "doNotUseIndentAsNumberingTabStop", "useAltKinsokuLineBreakRules", "allowSpaceOfSameStyleInTable", "doNotSuppressIndentation",
        "doNotAutofitConstrainedTables", "autofitToFirstFixedWidthCell", "underlineTabInNumList", "displayHangulFixedWidth",
        "splitPgBreakAndParaMark", "doNotVertAlignCellWithSp", "doNotBreakConstrainedForcedTable", "doNotVertAlignInTxbx",
        "useAnsiKerningPairs", "cachedColBalance", "compatSetting",
    ];

    /// <summary>
    /// Fills <paramref name="settings"/>' compatibility mode and flags from a settings part. Settings without a mode are Word
    /// 2007's, which Word lays out in its mode 12.
    /// </summary>
    public static void Read(XElement root, DocumentSettings settings)
    {
        settings.CompatibilityMode = 12;
        bool?[] values = new bool?[WordCompatibility.Flags.Length];
        List<KeyValuePair<string, string>> others = [];
        foreach (XElement child in root.Element(Ns.W + "compat")?.Elements() ?? [])
        {
            if (child.Name == Ns.W + "compatSetting")
            {
                if (child.W("name") is not { } name)
                {
                    continue;
                }

                if (name == "compatibilityMode")
                {
                    if (DocxUnits.Int(child.W("val")) is int mode && mode is >= 0 and <= 100)
                    {
                        settings.CompatibilityMode = mode;
                    }
                }
                else if (FlagIndex(name, setting: true) is int flag)
                {
                    values[flag] = child.W("val") is string val ? val is "1" or "true" or "on" : null;
                }
                else
                {
                    others.Add(new(name, child.W("val") ?? ""));
                }
            }
            else if (child.Name.Namespace == Ns.W && FlagIndex(child.Name.LocalName, setting: false) is int flag)
            {
                values[flag] = DocxUnits.OnOff(child);
            }
            else
            {
                others.Add(new(child.Name.LocalName, child.W("val") ?? "1"));
            }
        }

        settings.WordCompatibility.Load(values, others);
    }

    /// <summary>
    /// Writes the flags that differ from <paramref name="read"/> (all set ones when null, for a new part) into the
    /// settings part's <c>w:compat</c>, made by <paramref name="compat"/> when there is none. Returns whether it changed.
    /// </summary>
    public static bool Write(XElement root, WordCompatibility current, WordCompatibility? read, Func<XElement> compat)
    {
        bool changed = false;
        for (int id = 0; id < WordCompatibility.Flags.Length; id++)
        {
            (string name, bool isSetting) = WordCompatibility.Flags[id];
            bool? value = current.GetExplicit(id);
            if (name.Length == 0 || (read is null ? value is null : read.GetExplicit(id) == value))
            {
                continue;
            }

            XElement element = root.Element(Ns.W + "compat") ?? compat();
            if (isSetting)
            {
                XElement? setting = element.Elements(Ns.W + "compatSetting").FirstOrDefault(s => s.W("name") == name);
                if (value is not bool on)
                {
                    setting?.Remove();
                }
                else
                {
                    setting ??= Add(element, NewSetting(name));
                    setting.SetAttributeValue(Ns.W + "val", on ? "1" : "0");
                }
            }
            else
            {
                // An element that is there is on: a flag turned off is an element taken away.
                element.Element(Ns.W + name)?.Remove();
                if (value == true)
                {
                    Add(element, new XElement(Ns.W + name));
                }
            }

            changed = true;
        }

        if (read is null)
        {
            foreach ((string name, string value) in current.OtherSettings)
            {
                XElement element = root.Element(Ns.W + "compat") ?? compat();
                if (Array.IndexOf(Order, name) >= 0)
                {
                    Add(element, value == "1" ? new XElement(Ns.W + name) : new XElement(Ns.W + name, new XAttribute(Ns.W + "val", value)));
                }
                else
                {
                    Add(element, NewSetting(name)).SetAttributeValue(Ns.W + "val", value);
                }

                changed = true;
            }
        }

        return changed;
    }

    public static XElement NewSetting(string name)
    {
        return new XElement(Ns.W + "compatSetting", new XAttribute(Ns.W + "name", name), new XAttribute(Ns.W + "uri", WordUri));
    }

    private static int? FlagIndex(string name, bool setting)
    {
        for (int i = 0; i < WordCompatibility.Flags.Length; i++)
        {
            if (WordCompatibility.Flags[i].IsSetting == setting && WordCompatibility.Flags[i].Name == name)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Puts <paramref name="child"/> into <c>w:compat</c> in schema order: after every child that comes before it.</summary>
    private static XElement Add(XElement compat, XElement child)
    {
        int rank = Array.IndexOf(Order, child.Name.LocalName);
        XElement? before = compat.Elements().FirstOrDefault(e => e.Name.Namespace == Ns.W && Array.IndexOf(Order, e.Name.LocalName) > rank);
        if (before is not null)
        {
            before.AddBeforeSelf(child);
        }
        else
        {
            compat.Add(child);
        }

        return child;
    }
}
