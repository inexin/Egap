using System.Xml.Linq;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>Reads and writes sheetProtection, protectedRanges (ECMA-376 Part 1, 18.3.1.85 and 18.3.1.70) and workbookProtection (18.2.29).</summary>
internal static class XlsxProtection
{
    /// <summary>Each action's attribute and whether the attribute's default protects it, in schema order.</summary>
    private static readonly (string Name, SheetActions Action, bool Default)[] Flags =
    [
        ("objects", SheetActions.EditObjects, false),
        ("scenarios", SheetActions.EditScenarios, false),
        ("formatCells", SheetActions.FormatCells, true),
        ("formatColumns", SheetActions.FormatColumns, true),
        ("formatRows", SheetActions.FormatRows, true),
        ("insertColumns", SheetActions.InsertColumns, true),
        ("insertRows", SheetActions.InsertRows, true),
        ("insertHyperlinks", SheetActions.InsertHyperlinks, true),
        ("deleteColumns", SheetActions.DeleteColumns, true),
        ("deleteRows", SheetActions.DeleteRows, true),
        ("selectLockedCells", SheetActions.SelectLockedCells, false),
        ("sort", SheetActions.Sort, true),
        ("autoFilter", SheetActions.AutoFilter, true),
        ("pivotTables", SheetActions.PivotTables, true),
        ("selectUnlockedCells", SheetActions.SelectUnlockedCells, false),
    ];

    public static void ReadSheet(SheetProtection protection, XElement element)
    {
        SheetActions allowed = SheetActions.None;
        foreach ((string name, SheetActions action, bool protectedByDefault) in Flags)
        {
            if (!XlsxNames.Bool((string?)element.Attribute(name), protectedByDefault))
            {
                allowed |= action;
            }
        }

        protection.Set(XlsxNames.Bool((string?)element.Attribute("sheet"), false), allowed, Hash(element, ""));
    }

    public static void ReadRanges(SheetProtection protection, XElement element)
    {
        foreach (XElement range in element.Elements(element.Name.Namespace + "protectedRange"))
        {
            List<RangeAddress> ranges = [];
            foreach (string part in ((string?)range.Attribute("sqref") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (RangeAddress.TryParse(part, out RangeAddress address))
                {
                    ranges.Add(address);
                }
            }

            if (ranges.Count > 0)
            {
                protection.EditableRanges.AddRead(new EditableRange((string?)range.Attribute("name") ?? "Range1", ranges, Hash(range, "")) { Source = range });
            }
        }
    }

    public static void ReadWorkbook(WorkbookProtection protection, XElement? element)
    {
        if (element is null)
        {
            return;
        }

        protection.LockStructure = XlsxNames.Bool((string?)element.Attribute("lockStructure"), false);
        protection.LockWindows = XlsxNames.Bool((string?)element.Attribute("lockWindows"), false);
        protection.Password = PasswordHash.Read(
            (string?)element.Attribute("workbookAlgorithmName"), (string?)element.Attribute("workbookHashValue"),
            (string?)element.Attribute("workbookSaltValue"), (string?)element.Attribute("workbookSpinCount"), (string?)element.Attribute("workbookPassword"));
    }

    public static XElement? WriteSheet(SheetProtection protection, XNamespace ns)
    {
        if (!protection.IsProtected)
        {
            return null;
        }

        XElement element = new(ns + "sheetProtection");
        AddHash(element, protection.Password, "");
        element.Add(new XAttribute("sheet", "1"));
        foreach ((string name, SheetActions action, bool protectedByDefault) in Flags)
        {
            bool isProtected = (protection.AllowedActions & action) == 0;
            if (isProtected != protectedByDefault)
            {
                element.Add(new XAttribute(name, isProtected ? "1" : "0"));
            }
        }

        return element;
    }

    public static XElement? WriteRanges(SheetProtection protection, XNamespace ns)
    {
        if (protection.EditableRanges.Count == 0)
        {
            return null;
        }

        XElement element = new(ns + "protectedRanges");
        foreach (EditableRange range in protection.EditableRanges)
        {
            if (range.Ranges.Count == 0)
            {
                continue;
            }

            XElement made = new(ns + "protectedRange");
            if (range.Password is { Legacy: { } legacy, Algorithm: null })
            {
                made.Add(new XAttribute("password", legacy.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
            }

            made.Add(new XAttribute("sqref", string.Join(' ', range.Ranges)), new XAttribute("name", range.Name));
            if (range.Source?.Attribute("securityDescriptor") is { } descriptor)
            {
                made.Add(new XAttribute(descriptor));
            }

            if (range.Password is { Algorithm: { } algorithm, Hash: { } hash, Salt: { } salt })
            {
                made.Add(new XAttribute("algorithmName", algorithm), new XAttribute("hashValue", Convert.ToBase64String(hash)),
                    new XAttribute("saltValue", Convert.ToBase64String(salt)), new XAttribute("spinCount", XlsxNames.Integer(range.Password.SpinCount)));
            }

            // Excel 2010's security descriptors are child elements, kept as they were.
            made.Add(range.Source?.Elements().Select(e => new XElement(e)));
            element.Add(made);
        }

        return element.HasElements ? element : null;
    }

    /// <summary>Sets the workbookProtection element of a workbook part's root, or removes it when the workbook is not protected.</summary>
    public static void WriteWorkbook(WorkbookProtection protection, XElement root)
    {
        XNamespace ns = XlsxNames.Main;
        XElement? element = root.Element(ns + "workbookProtection");
        string[] modeled = ["workbookPassword", "workbookAlgorithmName", "workbookHashValue", "workbookSaltValue", "workbookSpinCount", "lockStructure", "lockWindows"];
        if (!protection.IsProtected && protection.Password is null)
        {
            // The revision-tracking settings are kept; without them the element goes.
            if (element is not null)
            {
                foreach (string name in modeled)
                {
                    element.Attribute(name)?.Remove();
                }

                if (!element.HasAttributes)
                {
                    element.Remove();
                }
            }

            return;
        }

        XElement made = new(ns + "workbookProtection");
        if (protection.Password is { Legacy: { } legacy, Algorithm: null })
        {
            made.Add(new XAttribute("workbookPassword", legacy.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        }

        made.Add(element?.Attributes().Where(a => !modeled.Contains(a.Name.LocalName) && a.Name.LocalName.StartsWith("revisions", StringComparison.Ordinal)).Select(a => new XAttribute(a)));
        if (protection.LockStructure)
        {
            made.Add(new XAttribute("lockStructure", "1"));
        }

        if (protection.LockWindows)
        {
            made.Add(new XAttribute("lockWindows", "1"));
        }

        if (protection.Password is { Algorithm: { } algorithm, Hash: { } hash, Salt: { } salt } password)
        {
            made.Add(new XAttribute("workbookAlgorithmName", algorithm), new XAttribute("workbookHashValue", Convert.ToBase64String(hash)),
                new XAttribute("workbookSaltValue", Convert.ToBase64String(salt)), new XAttribute("workbookSpinCount", XlsxNames.Integer(password.SpinCount)));
        }

        made.Add(element?.Attributes().Where(a => !modeled.Contains(a.Name.LocalName) && !a.Name.LocalName.StartsWith("revisions", StringComparison.Ordinal)).Select(a => new XAttribute(a)));
        WorkbookOrder.Place(root, made);
    }

    private static PasswordHash? Hash(XElement element, string prefix) => PasswordHash.Read(
        (string?)element.Attribute(prefix + "algorithmName"), (string?)element.Attribute(prefix + "hashValue"),
        (string?)element.Attribute(prefix + "saltValue"), (string?)element.Attribute(prefix + "spinCount"), (string?)element.Attribute(prefix + "password"));

    private static void AddHash(XElement element, PasswordHash? password, string prefix)
    {
        if (password is { Legacy: { } legacy, Algorithm: null })
        {
            element.Add(new XAttribute(prefix + "password", legacy.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (password is { Algorithm: { } algorithm, Hash: { } hash, Salt: { } salt })
        {
            element.Add(new XAttribute(prefix + "algorithmName", algorithm), new XAttribute(prefix + "hashValue", Convert.ToBase64String(hash)),
                new XAttribute(prefix + "saltValue", Convert.ToBase64String(salt)), new XAttribute(prefix + "spinCount", XlsxNames.Integer(password.SpinCount)));
        }
    }
}
