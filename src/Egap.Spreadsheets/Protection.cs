using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Egap.Documents;

namespace Egap.Spreadsheets;

/// <summary>What a protected sheet still lets a user do, as Excel's Protect Sheet dialog lists it.</summary>
[Flags]
public enum SheetActions
{
    /// <summary>Nothing: cells cannot even be selected.</summary>
    None = 0,

    /// <summary>Select locked cells.</summary>
    SelectLockedCells = 1 << 0,

    /// <summary>Select unlocked cells.</summary>
    SelectUnlockedCells = 1 << 1,

    /// <summary>Format cells.</summary>
    FormatCells = 1 << 2,

    /// <summary>Format columns, their widths among it.</summary>
    FormatColumns = 1 << 3,

    /// <summary>Format rows, their heights among it.</summary>
    FormatRows = 1 << 4,

    /// <summary>Insert columns.</summary>
    InsertColumns = 1 << 5,

    /// <summary>Insert rows.</summary>
    InsertRows = 1 << 6,

    /// <summary>Insert hyperlinks.</summary>
    InsertHyperlinks = 1 << 7,

    /// <summary>Delete columns.</summary>
    DeleteColumns = 1 << 8,

    /// <summary>Delete rows.</summary>
    DeleteRows = 1 << 9,

    /// <summary>Sort.</summary>
    Sort = 1 << 10,

    /// <summary>Use AutoFilter.</summary>
    AutoFilter = 1 << 11,

    /// <summary>Use PivotTable and PivotChart.</summary>
    PivotTables = 1 << 12,

    /// <summary>Edit objects: pictures, shapes and charts.</summary>
    EditObjects = 1 << 13,

    /// <summary>Edit scenarios.</summary>
    EditScenarios = 1 << 14,

    /// <summary>What Excel allows by default: selecting locked and unlocked cells.</summary>
    Default = SelectLockedCells | SelectUnlockedCells,
}

/// <summary>
/// A sheet's protection, as Excel's Protect Sheet sets it: whether it is on, what users may still do, the hash of the
/// password that lifts it, and the ranges that stay editable. Locked cells (<see cref="CellProtection.Locked"/>, the default)
/// cannot be changed while it is on, and the formulas of hidden cells (<see cref="CellProtection.Hidden"/>) are not shown.
/// </summary>
/// <remarks>
/// Egap does not enforce protection: it reads, writes and checks it, so that Excel enforces it. Protection is not
/// encryption, and anyone with the file can read every cell. <see cref="Protect"/> stores a SHA-512 hash of the password
/// with a random salt and 100,000 spins, as Excel does; <see cref="Verify"/> also checks the 16-bit hashes of older files.
/// </remarks>
/// <example>
/// <code>
/// sheet.Range("B2:B10").UpdateStyle(s => s with { Protection = s.Protection with { Locked = false } });
/// sheet.Protection.Protect("secret", SheetActions.Default | SheetActions.Sort);
/// bool open = sheet.Protection.Unprotect("secret");    // true
/// </code>
/// </example>
public sealed class SheetProtection
{
    private readonly Worksheet sheet;

    internal SheetProtection(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>Whether the sheet is protected.</summary>
    public bool IsProtected { get; private set; }

    /// <summary>Whether a password lifts the protection; without one, anyone may switch it off.</summary>
    public bool HasPassword => Password is not null;

    /// <summary>What users may still do while the sheet is protected. Default <see cref="SheetActions.Default"/>.</summary>
    public SheetActions AllowedActions { get; set; } = SheetActions.Default;

    /// <summary>The ranges users may edit while the sheet is protected, as Excel's Allow Edit Ranges lists them.</summary>
    public EditableRangeCollection EditableRanges { get; } = new();

    /// <summary>The password hash, or null.</summary>
    internal PasswordHash? Password { get; set; }

    /// <summary>
    /// Protects the sheet, with a hash of <paramref name="password"/> made as Excel makes it; a null or empty password lets
    /// anyone switch it off.
    /// </summary>
    public void Protect(string? password = null, SheetActions allowed = SheetActions.Default)
    {
        IsProtected = true;
        AllowedActions = allowed;
        Password = PasswordHash.Create(password);
    }

    /// <summary>
    /// Switches the protection off when <paramref name="password"/> is the one that lifts it, or when there is none, and
    /// removes the password; false, leaving it on, for a wrong password.
    /// </summary>
    /// <exception cref="DocumentException">The file's hash uses an algorithm Egap cannot check or more spins than it runs.</exception>
    public bool Unprotect(string? password = null)
    {
        if (!Verify(password))
        {
            return false;
        }

        IsProtected = false;
        Password = null;
        Kept = false;
        return true;
    }

    /// <summary>Whether <paramref name="password"/> lifts the protection: true when it matches the hash, or when there is no password.</summary>
    /// <exception cref="DocumentException">The file's hash uses an algorithm Egap cannot check or more spins than it runs.</exception>
    public bool Verify(string? password) => Password?.Verify(password) ?? true;

    /// <summary>Whether the file had a sheetProtection element, written back while the sheet is not unprotected.</summary>
    internal bool Kept { get; set; }

    /// <summary>
    /// Whether a user may change a cell without a password: the sheet is not protected, the cell is not locked, or it is in
    /// an editable range that needs no password.
    /// </summary>
    public bool CanEdit(CellAddress cell)
    {
        return !IsProtected || !sheet.Cells[cell.Row, cell.Column].Style.Protection.Locked
            || EditableRanges.Any(r => !r.HasPassword && r.Ranges.Exists(range => range.Contains(cell)));
    }

    /// <summary>Sets what a reader found.</summary>
    internal void Set(bool on, SheetActions allowed, PasswordHash? password)
    {
        IsProtected = on;
        AllowedActions = allowed;
        Password = password;
        Kept = true;
    }
}

/// <summary>A range users may edit while its sheet is protected, with its own optional password.</summary>
public sealed class EditableRange
{
    internal EditableRange(string name, IEnumerable<RangeAddress> ranges, PasswordHash? password)
    {
        Name = name;
        Ranges = [.. ranges];
        Password = password;
    }

    /// <summary>The range's title, as Excel's Allow Edit Ranges shows it.</summary>
    public string Name { get; set; }

    /// <summary>The cells it covers.</summary>
    public List<RangeAddress> Ranges { get; }

    /// <summary>Whether a password is needed to edit it.</summary>
    public bool HasPassword => Password is not null;

    internal PasswordHash? Password { get; set; }

    /// <summary>The file's protectedRange element, for the security descriptors the model does not hold.</summary>
    internal System.Xml.Linq.XElement? Source { get; init; }

    /// <summary>Sets or removes the password needed to edit it, hashed as Excel hashes it.</summary>
    public void SetPassword(string? password) => Password = PasswordHash.Create(password);

    /// <summary>Whether <paramref name="password"/> opens the range: true when it matches, or when there is no password.</summary>
    /// <exception cref="DocumentException">The file's hash uses an algorithm Egap cannot check or more spins than it runs.</exception>
    public bool Verify(string? password) => Password?.Verify(password) ?? true;
}

/// <summary>The ranges that stay editable on a protected sheet.</summary>
public sealed class EditableRangeCollection : IReadOnlyList<EditableRange>
{
    private readonly List<EditableRange> ranges = [];

    internal EditableRangeCollection()
    {
    }

    /// <inheritdoc/>
    public int Count => ranges.Count;

    /// <inheritdoc/>
    public EditableRange this[int index] => ranges[index];

    /// <summary>Adds a range users may edit while the sheet is protected, with a password if one is given.</summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public EditableRange Add(string name, RangeAddress range, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EditableRange made = new(name, [range], PasswordHash.Create(password));
        ranges.Add(made);
        return made;
    }

    /// <summary>Adds a range given in A1 style, such as <c>B2:D9</c>.</summary>
    /// <exception cref="FormatException">The range is not an A1 range.</exception>
    public EditableRange Add(string name, string range, string? password = null) => Add(name, RangeAddress.Parse(range), password);

    /// <summary>Removes a range; false when it is not in the collection.</summary>
    public bool Remove(EditableRange range) => ranges.Remove(range);

    /// <summary>Removes every range.</summary>
    public void Clear() => ranges.Clear();

    /// <inheritdoc/>
    public IEnumerator<EditableRange> GetEnumerator() => ranges.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(EditableRange range) => ranges.Add(range);
}

/// <summary>
/// A workbook's protection, as Excel's Protect Workbook sets it: the structure (sheets cannot be added, removed, renamed,
/// moved, hidden or shown) and, in older Excel versions, the windows, with the hash of the password that lifts it.
/// </summary>
/// <remarks>Egap does not enforce it; Excel does. The hash is made and checked as for <see cref="SheetProtection"/>.</remarks>
public sealed class WorkbookProtection
{
    internal WorkbookProtection()
    {
    }

    /// <summary>Whether the structure or the windows are locked.</summary>
    public bool IsProtected => LockStructure || LockWindows;

    /// <summary>Whether sheets cannot be added, removed, renamed, moved, hidden or shown.</summary>
    public bool LockStructure { get; set; }

    /// <summary>Whether the workbook's windows cannot be moved, sized or closed; only Excel 2010 and older use it.</summary>
    public bool LockWindows { get; set; }

    /// <summary>Whether a password lifts the protection.</summary>
    public bool HasPassword => Password is not null;

    internal PasswordHash? Password { get; set; }

    /// <summary>Locks the structure and, when asked, the windows, with a hash of <paramref name="password"/> made as Excel makes it.</summary>
    public void Protect(string? password = null, bool structure = true, bool windows = false)
    {
        LockStructure = structure;
        LockWindows = windows;
        Password = PasswordHash.Create(password);
    }

    /// <summary>Unlocks the workbook when <paramref name="password"/> lifts the protection, removing the password; false for a wrong one.</summary>
    /// <exception cref="DocumentException">The file's hash uses an algorithm Egap cannot check or more spins than it runs.</exception>
    public bool Unprotect(string? password = null)
    {
        if (!Verify(password))
        {
            return false;
        }

        LockStructure = false;
        LockWindows = false;
        Password = null;
        return true;
    }

    /// <summary>Whether <paramref name="password"/> lifts the protection: true when it matches the hash, or when there is no password.</summary>
    /// <exception cref="DocumentException">The file's hash uses an algorithm Egap cannot check or more spins than it runs.</exception>
    public bool Verify(string? password) => Password?.Verify(password) ?? true;
}

/// <summary>
/// A protection password's hash: Excel's hash of the UTF-16 password with a salt, hashed again for each spin (ECMA-376
/// Part 1, 18.3.1.85), or the 16-bit hash of files from Excel 2007 and older.
/// </summary>
internal sealed class PasswordHash
{
    /// <summary>Excel's spin count.</summary>
    public const int DefaultSpinCount = 100_000;

    private PasswordHash(string? algorithm, byte[]? hash, byte[]? salt, int spinCount, ushort? legacy)
    {
        Algorithm = algorithm;
        Hash = hash;
        Salt = salt;
        SpinCount = spinCount;
        Legacy = legacy;
    }

    public string? Algorithm { get; }

    public byte[]? Hash { get; }

    public byte[]? Salt { get; }

    public int SpinCount { get; }

    /// <summary>The 16-bit hash, or null.</summary>
    public ushort? Legacy { get; }

    /// <summary>A SHA-512 hash of a password under a new random salt, or null for none.</summary>
    public static PasswordHash? Create(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new PasswordHash("SHA-512", DocumentProtection.ComputeHash("SHA-512", Encoding.Unicode.GetBytes(password), salt, DefaultSpinCount), salt, DefaultSpinCount, null);
    }

    /// <summary>The hash a file holds, from its attributes; null when it has none or they are not readable.</summary>
    public static PasswordHash? Read(string? algorithm, string? hash, string? salt, string? spinCount, string? legacy)
    {
        if (algorithm is not null && hash is not null)
        {
            try
            {
                int spins = int.TryParse(spinCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Max(n, 0) : 0;
                return new PasswordHash(algorithm, Convert.FromBase64String(hash), salt is null ? [] : Convert.FromBase64String(salt), spins, null);
            }
            catch (FormatException)
            {
                // Fall back to the 16-bit hash, if there is one.
            }
        }

        return ushort.TryParse(legacy, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort old) && old != 0
            ? new PasswordHash(null, null, null, 0, old)
            : null;
    }

    /// <summary>Whether a password matches.</summary>
    /// <exception cref="DocumentException">The hash uses an algorithm Egap cannot check or more spins than it runs.</exception>
    public bool Verify(string? password)
    {
        password ??= "";
        if (Algorithm is null || Hash is null || Salt is null)
        {
            return Legacy == LegacyHash(password);
        }

        if (SpinCount > DocumentProtection.MaxSpinCount)
        {
            throw new DocumentException(string.Create(CultureInfo.InvariantCulture, $"The protection hash asks for {SpinCount} spins; at most {DocumentProtection.MaxSpinCount} are run."));
        }

        return CryptographicOperations.FixedTimeEquals(DocumentProtection.ComputeHash(Algorithm, Encoding.Unicode.GetBytes(password), Salt, SpinCount), Hash);
    }

    /// <summary>
    /// The 16-bit password hash of Excel 2007 and older ([MS-OFFCRYPTO] 2.3.7.1, the verifier): each character's low byte, last
    /// first, rotated into 15 bits, then the length and 0xCE4B.
    /// </summary>
    public static ushort LegacyHash(string password)
    {
        if (password.Length == 0)
        {
            return 0;
        }

        int hash = 0;
        for (int i = password.Length; i >= 0; i--)
        {
            int b = i == 0 ? password.Length : (byte)password[i - 1];
            hash = ((hash >> 14) & 1) | ((hash << 1) & 0x7FFF);
            hash ^= b;
        }

        return (ushort)(hash ^ 0xCE4B);
    }
}
