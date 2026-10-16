namespace Egap.Documents;

/// <summary>
/// A part of a document with its own page setup (Build Plan task 3), a body and up to three headers and three footers
/// (first page, odd or every page, even pages).
/// </summary>
public sealed class Section : Node
{
    /// <summary>A section of <paramref name="document"/> with an empty body, not yet in the tree.</summary>
    public Section(Document document)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Body = new Body(document);
        Body.Attach(this, null, 0);
        HeadersFooters = new HeaderFooterCollection(this);
        PageSetup = Own(new PageSetup());
    }

    /// <summary>The section's paper, margins, columns and page numbering; unset values are Word's (US Letter, 1 inch margins).</summary>
    public PageSetup PageSetup { get; }

    /// <summary>The main text of the section. Always there; it cannot be removed or replaced.</summary>
    public Body Body { get; }

    /// <summary>The section's headers and footers, at most one of each <see cref="HeaderFooterKind"/>.</summary>
    public HeaderFooterCollection HeadersFooters { get; }

    internal override int ChildCount => 1 + HeadersFooters.Count;

    /// <inheritdoc/>
    public override Section Clone(bool deep = true)
    {
        return (Section)base.Clone(deep);
    }

    internal override Node GetChild(int index)
    {
        return index == 0 ? Body : HeadersFooters[index - 1];
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitSectionStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitSectionEnd(this);
    }

    private protected override string FormatPropertyPrefix(FormatBase format)
    {
        return "PageSetup.";
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        Section copy = new(context.Target);
        copy.PageSetup.CopyRaw(PageSetup);
        return copy;
    }

    private protected override void CloneChildren(Node copy, CloneContext context, Stack<(Node Source, Node Copy)> pending)
    {
        Section section = (Section)copy;
        pending.Push((Body, section.Body));
        foreach (HeaderFooter story in HeadersFooters)
        {
            section.HeadersFooters.AddTrusted(CloneChild(story, context, pending));
        }
    }
}

/// <summary>Which pages a header or footer is for.</summary>
public enum HeaderFooterKind
{
    /// <summary>The header on every page, or on odd pages when the document has different odd and even headers.</summary>
    HeaderPrimary,

    /// <summary>The header on the section's first page, when the section has a different first page.</summary>
    HeaderFirst,

    /// <summary>The header on even pages, when the document has different odd and even headers.</summary>
    HeaderEven,

    /// <summary>The footer on every page, or on odd pages when the document has different odd and even footers.</summary>
    FooterPrimary,

    /// <summary>The footer on the section's first page, when the section has a different first page.</summary>
    FooterFirst,

    /// <summary>The footer on even pages, when the document has different odd and even footers.</summary>
    FooterEven,
}

/// <summary>A section's headers and footers: at most one of each kind.</summary>
public sealed class HeaderFooterCollection : NodeCollection<HeaderFooter>
{
    internal HeaderFooterCollection(Section owner)
        : base(owner)
    {
    }

    /// <summary>The header or footer of <paramref name="kind"/>, or null when the section has none.</summary>
    public HeaderFooter? this[HeaderFooterKind kind]
    {
        get
        {
            foreach (HeaderFooter story in this)
            {
                if (story.Kind == kind)
                {
                    return story;
                }
            }

            return null;
        }
    }

    /// <summary>The header or footer of <paramref name="kind"/>, added empty when the section has none.</summary>
    public HeaderFooter GetOrAdd(HeaderFooterKind kind)
    {
        return this[kind] ?? AddTrusted(new HeaderFooter(Owner.Document, kind));
    }

    private protected override void Validate(HeaderFooter node)
    {
        if (this[node.Kind] is not null)
        {
            throw new ArgumentException($"The section already has a {node.Kind} header or footer.", nameof(node));
        }
    }
}
