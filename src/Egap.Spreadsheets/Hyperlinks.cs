using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>
/// A hyperlink on a cell or range: to a web address or file (<see cref="Address"/>), or to a place in the workbook such as
/// <c>Sheet2!A1</c> or a defined name (<see cref="Location"/>). The cell keeps its own value; Excel shows it as the link's
/// text.
/// </summary>
public sealed class Hyperlink
{
    internal Hyperlink(RangeAddress range, string? address, string? location)
    {
        Range = range;
        Address = address;
        Location = location;
    }

    /// <summary>The cells the link is on.</summary>
    public RangeAddress Range { get; set; }

    /// <summary>The web address, e-mail (<c>mailto:</c>) or file the link opens, or null for a link inside the workbook.</summary>
    public string? Address { get; set; }

    /// <summary>
    /// The place in the workbook the link goes to, such as <c>Sheet2!A1</c> or a defined name; with an
    /// <see cref="Address"/>, the place inside it, such as a web page's anchor.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>The text Excel records as the link's display text, or null; the cell's value is what shows.</summary>
    public string? Display { get; set; }

    /// <summary>The text shown when the pointer rests on the link, or null.</summary>
    public string? ToolTip { get; set; }

    /// <summary>The file's hyperlink element, for the attributes the model does not hold.</summary>
    internal XElement? Source { get; set; }

    /// <summary>The relationship the file's link used for its address, reused while the address is the same.</summary>
    internal (string Id, string Target)? Relationship { get; set; }
}

/// <summary>A sheet's hyperlinks, in the order the file or code gave them.</summary>
public sealed class HyperlinkCollection : IReadOnlyList<Hyperlink>
{
    private readonly List<Hyperlink> links = [];

    internal HyperlinkCollection()
    {
    }

    /// <summary>How many hyperlinks there are.</summary>
    public int Count => links.Count;

    /// <summary>The hyperlink at a position.</summary>
    public Hyperlink this[int index] => links[index];

    /// <summary>Adds a link to a web address, e-mail or file on a cell or range.</summary>
    /// <exception cref="ArgumentException">The address is empty.</exception>
    public Hyperlink Add(RangeAddress range, string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        Hyperlink link = new(range, address, null);
        links.Add(link);
        return link;
    }

    /// <summary>Adds a link to a web address, e-mail or file on a cell or range given in A1 form.</summary>
    /// <exception cref="FormatException">The range is not an A1 reference.</exception>
    /// <exception cref="ArgumentException">The address is empty.</exception>
    public Hyperlink Add(string range, string address) => Add(RangeAddress.Parse(range), address);

    /// <summary>Adds a link to a place in the workbook, such as <c>Sheet2!A1</c> or a defined name.</summary>
    /// <exception cref="ArgumentException">The location is empty.</exception>
    public Hyperlink AddInternal(RangeAddress range, string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        Hyperlink link = new(range, null, location);
        links.Add(link);
        return link;
    }

    /// <summary>The first hyperlink on a cell, or null when it has none.</summary>
    public Hyperlink? Find(CellAddress cell) => links.Find(link => link.Range.Contains(cell));

    /// <summary>Removes a hyperlink.</summary>
    /// <returns>Whether the link was on the sheet.</returns>
    public bool Remove(Hyperlink link) => links.Remove(link);

    /// <summary>Removes every hyperlink.</summary>
    public void Clear() => links.Clear();

    /// <inheritdoc/>
    public IEnumerator<Hyperlink> GetEnumerator() => links.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(Hyperlink link) => links.Add(link);
}
