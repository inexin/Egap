using System.Collections;
using System.Globalization;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// Someone who wrote comments: in the comment authors part of PowerPoint 2007 to 2019 comments (<c>p:cmAuthor</c>), or the
/// authors part of modern, threaded comments ([MS-PPTX] 2.16, <c>p188:author</c>).
/// </summary>
public sealed class CommentAuthor
{
    internal CommentAuthor(Presentation presentation, XElement element)
    {
        Presentation = presentation;
        Element = element;
    }

    /// <summary>The presentation the author is in.</summary>
    public Presentation Presentation { get; }

    /// <summary>The author's name.</summary>
    public string Name => (string?)Element.Attribute("name") ?? "";

    /// <summary>The author's initials; empty when the file gives none.</summary>
    public string Initials => (string?)Element.Attribute("initials") ?? "";

    /// <summary>Whether the author writes modern, threaded comments, rather than the older kind.</summary>
    public bool IsModern => Element.Name.Namespace == Comments.P188;

    /// <summary>The author's id as the file gives it: a number for the older kind, a GUID in braces for modern comments.</summary>
    public string Id => (string?)Element.Attribute("id") ?? "";

    internal XElement Element { get; }
}

/// <summary>
/// The authors of a presentation's comments, of both kinds. <see cref="Add(string, string)"/> adds one of the kind the
/// presentation writes: modern, unless it has authors of the older kind only.
/// </summary>
public sealed class CommentAuthorCollection : IReadOnlyList<CommentAuthor>
{
    private readonly Presentation presentation;
    private readonly Dictionary<XElement, CommentAuthor> authors = [];

    internal CommentAuthorCollection(Presentation presentation)
    {
        this.presentation = presentation;
    }

    /// <summary>The number of authors.</summary>
    public int Count => Elements.Count();

    /// <summary>
    /// Whether new comments are modern, threaded ones ([MS-PPTX] 2.16) that PowerPoint for Microsoft 365 shows: true unless the
    /// presentation has authors of the older kind and none of the modern.
    /// </summary>
    public bool WritesModern => Modern.Any() || !Legacy.Any();

    /// <summary>The author at <paramref name="index"/>, from 0: the older kind first.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no author at that position.</exception>
    public CommentAuthor this[int index] => Elements.Select(Wrap).ElementAtOrDefault(index) ?? throw new ArgumentOutOfRangeException(nameof(index));

    private IEnumerable<XElement> Legacy => Comments.LegacyAuthors(presentation)?.Root.Elements(PptxNames.P + "cmAuthor") ?? [];

    private IEnumerable<XElement> Modern => Comments.ModernAuthors(presentation)?.Root.Elements(Comments.P188 + "author") ?? [];

    private IEnumerable<XElement> Elements => Legacy.Concat(Modern);

    /// <summary>
    /// The author named <paramref name="name"/> of the kind the presentation writes (<see cref="WritesModern"/>), added when
    /// there is none.
    /// </summary>
    /// <exception cref="ArgumentException">The name is null or empty.</exception>
    /// <exception cref="ArgumentNullException">The initials are null.</exception>
    public CommentAuthor Add(string name, string initials = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(initials);
        bool modern = WritesModern;
        if ((modern ? Modern : Legacy).FirstOrDefault(e => (string?)e.Attribute("name") == name) is { } existing)
        {
            return Wrap(existing);
        }

        XmlPart part = modern ? Comments.ModernAuthors(presentation) ?? Comments.AddModernAuthors(presentation) : Comments.LegacyAuthors(presentation)!;
        XElement author = modern
            ? new XElement(Comments.P188 + "author", new XAttribute("id", Comments.NewGuid()), new XAttribute("name", name), new XAttribute("initials", initials),
                new XAttribute("userId", name), new XAttribute("providerId", "None"))
            : new XElement(PptxNames.P + "cmAuthor", new XAttribute("id", PptxNames.Text(Legacy.Select(e => PptxNames.UInt(e, "id") ?? 0).DefaultIfEmpty(0u).Max() + 1)),
                new XAttribute("name", name), new XAttribute("initials", initials), new XAttribute("lastIdx", "0"), new XAttribute("clrIdx", PptxNames.Text(Legacy.Count())));
        part.Root.Add(author);
        part.MarkChanged();
        return Wrap(author);
    }

    /// <inheritdoc/>
    public IEnumerator<CommentAuthor> GetEnumerator() => Elements.Select(Wrap).ToList().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal CommentAuthor? Find(XName kind, string? id) =>
        id is null ? null : Elements.FirstOrDefault(e => e.Name == kind && (string?)e.Attribute("id") == id) is { } element ? Wrap(element) : null;

    private CommentAuthor Wrap(XElement element)
    {
        if (!authors.TryGetValue(element, out CommentAuthor? author))
        {
            author = new CommentAuthor(presentation, element);
            authors[element] = author;
        }

        return author;
    }
}

/// <summary>
/// A comment on a slide, or a reply to one: of the older kind (<c>p:cm</c>, replies linked by <c>p15:threadingInfo</c>) or a
/// modern, threaded one ([MS-PPTX] 2.16, <c>p188:cm</c> and <c>p188:reply</c>).
/// </summary>
public sealed class SlideComment
{
    private readonly List<SlideComment> replies = [];

    internal SlideComment(Slide slide, XElement element, SlideComment? parent)
    {
        Slide = slide;
        Element = element;
        Parent = parent;
    }

    /// <summary>The slide the comment is on.</summary>
    public Slide Slide { get; }

    /// <summary>The comment this one replies to, or null for the first comment of a thread.</summary>
    public SlideComment? Parent { get; }

    /// <summary>The replies, oldest first.</summary>
    public IReadOnlyList<SlideComment> Replies => replies;

    /// <summary>Whether the comment is a modern, threaded one.</summary>
    public bool IsModern => Element.Name.Namespace == Comments.P188;

    /// <summary>The comment's author, or null when the authors part does not list it.</summary>
    public CommentAuthor? Author => Slide.Presentation.CommentAuthors.Find(IsModern ? Comments.P188 + "author" : PptxNames.P + "cmAuthor", (string?)Element.Attribute("authorId"));

    /// <summary>When the comment was written, as the file gives it (<c>dt</c> or <c>created</c>), or null.</summary>
    public DateTime? Created => DateTime.TryParse((string?)Element.Attribute(IsModern ? "created" : "dt"), CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value) ? value : null;

    /// <summary>
    /// The comment's text: for a modern comment, its paragraphs ended by <see cref="ControlChar.ParagraphBreak"/>. Setting it
    /// replaces the text.
    /// </summary>
    /// <exception cref="ArgumentNullException">The text set is null.</exception>
    public string Text
    {
        get
        {
            if (!IsModern)
            {
                return (string?)Element.Element(PptxNames.P + "text") ?? "";
            }

            return string.Join(ControlChar.ParagraphBreak, Element.Element(Comments.P188 + "txBody")?.Elements(PptxNames.A + "p")
                .Select(p => string.Concat(p.Elements().Select(e => e.Name.LocalName == "br" ? ControlChar.LineBreak.ToString() : (string?)e.Element(PptxNames.A + "t") ?? ""))) ?? []);
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == Text)
            {
                return;
            }

            if (IsModern)
            {
                Element.Element(Comments.P188 + "txBody")?.Remove();
                XElement body = TextBody.NewElement(value, Comments.P188 + "txBody");
                if ((Element.Element(PptxNames.P + "extLst") ?? Element.Element(Comments.P188 + "extLst")) is { } extensions)
                {
                    extensions.AddBeforeSelf(body);
                }
                else
                {
                    Element.Add(body);
                }
            }
            else
            {
                XElement text = Element.Element(PptxNames.P + "text") ?? Comments.AddLegacyText(Element);
                text.Value = value;
            }

            Part.MarkChanged();
        }
    }

    /// <summary>Whether a modern comment is marked resolved (<c>status="resolved"</c>); false for the older kind.</summary>
    public bool IsResolved => (string?)Element.Attribute("status") == "resolved";

    internal XElement Element { get; }

    internal XmlPart Part => Slide.Presentation.Package.Xml(Slide.Presentation.Package.GetPart(Element.Document!.Root!.Name.Namespace == Comments.P188
        ? Comments.ModernPartName(Slide)!
        : Comments.LegacyPartName(Slide)!)!);

    internal List<SlideComment> ReplyList => replies;

    /// <summary>Adds a reply by <paramref name="author"/> to the comment's thread, after the replies it has.</summary>
    /// <exception cref="ArgumentNullException">The author or text is null.</exception>
    /// <exception cref="ArgumentException">The author is of the other kind of comment, or of another presentation.</exception>
    public SlideComment Reply(CommentAuthor author, string text)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(text);
        SlideComment thread = Parent ?? this;
        Comments.Check(author, Slide, IsModern);
        return Comments.AddReply(thread, author, text);
    }
}

/// <summary>
/// The comment threads on a slide, of both kinds, oldest first; each with its replies. The list is read when
/// <see cref="Slide.Comments"/> is; read it again after a change made another way.
/// </summary>
public sealed class SlideCommentCollection : IReadOnlyList<SlideComment>
{
    private readonly Slide slide;
    private readonly List<SlideComment> threads = [];

    internal SlideCommentCollection(Slide slide)
    {
        this.slide = slide;
        Comments.Read(slide, threads);
    }

    /// <summary>The number of threads.</summary>
    public int Count => threads.Count;

    /// <summary>The thread at <paramref name="index"/>, from 0: its first comment.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no thread at that position.</exception>
    public SlideComment this[int index] => threads[index];

    /// <summary>Adds a comment by <paramref name="author"/>, of the author's kind, at the slide's top left.</summary>
    /// <exception cref="ArgumentNullException">The author or text is null.</exception>
    /// <exception cref="ArgumentException">The author is of another presentation.</exception>
    public SlideComment Add(CommentAuthor author, string text)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(text);
        Comments.Check(author, slide, author.IsModern);
        SlideComment comment = Comments.AddThread(slide, author, text);
        threads.Add(comment);
        return comment;
    }

    /// <summary>Removes <paramref name="comment"/>: a thread's first comment with its replies, or one reply.</summary>
    /// <returns>Whether the comment was on the slide.</returns>
    /// <exception cref="ArgumentNullException">The comment is null.</exception>
    public bool Remove(SlideComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (comment.Slide != slide || comment.Element.Parent is null)
        {
            return false;
        }

        Comments.Remove(comment);
        if (comment.Parent is { } parent)
        {
            parent.ReplyList.Remove(comment);
        }
        else
        {
            threads.Remove(comment);
        }

        return true;
    }

    /// <inheritdoc/>
    public IEnumerator<SlideComment> GetEnumerator() => threads.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The comments on a slide.</summary>
public sealed partial class Slide
{
    /// <summary>The comment threads on the slide, of both kinds, read now.</summary>
    public SlideCommentCollection Comments => new(this);
}

/// <summary>The parts and XML of both kinds of comment.</summary>
internal static class Comments
{
    public static readonly XNamespace P188 = "http://schemas.microsoft.com/office/powerpoint/2018/8/main";
    public static readonly XNamespace Pc = "http://schemas.microsoft.com/office/powerpoint/2013/main/command";

    // [MS-PPTX] 2.1.5, 2.1.6, 2.2.10.
    private const string ModernAuthorsRelationship = "http://schemas.microsoft.com/office/2018/10/relationships/authors";
    private const string ModernCommentsRelationship = "http://schemas.microsoft.com/office/2018/10/relationships/comments";
    private const string ModernAuthorsContentType = "application/vnd.ms-powerpoint.authors+xml";
    private const string ModernCommentsContentType = "application/vnd.ms-powerpoint.comments+xml";
    private const string CommentRelUri = "{6950BFC3-D8DA-4A85-94F7-54DA5524770B}";
    private const string ThreadingUri = "{C676402C-5697-4E1C-873F-D02D1690AC5C}";
    private const string LegacyAuthorsRelationship = PptxNames.Office + "commentAuthors";
    private const string LegacyCommentsRelationship = PptxNames.Office + "comments";

    public static XmlPart? LegacyAuthors(Presentation presentation) => Part(presentation, presentation.Main.Part.Name, LegacyAuthorsRelationship);

    public static XmlPart? ModernAuthors(Presentation presentation) => Part(presentation, presentation.Main.Part.Name, ModernAuthorsRelationship);

    public static string? LegacyPartName(Slide slide) => RelatedName(slide, LegacyCommentsRelationship);

    public static string? ModernPartName(Slide slide) => RelatedName(slide, ModernCommentsRelationship);

    public static string NewGuid() => Guid.NewGuid().ToString("B").ToUpper(CultureInfo.InvariantCulture);

    public static XmlPart AddModernAuthors(Presentation presentation)
    {
        PackagePart part = presentation.Package.AddXmlPart("/ppt/authors.xml", ModernAuthorsContentType,
            new XDocument(new XElement(P188 + "authorLst", new XAttribute(XNamespace.Xmlns + "p188", P188.NamespaceName))));
        presentation.Package.AddRelationship(presentation.Main.Part.Name, ModernAuthorsRelationship, part.Name);
        return part.Xml!;
    }

    public static void Check(CommentAuthor author, Slide slide, bool modern)
    {
        if (author.Presentation != slide.Presentation)
        {
            throw new ArgumentException("The author belongs to another presentation.", nameof(author));
        }

        if (author.IsModern != modern)
        {
            throw new ArgumentException("A modern comment's author writes modern comments, and an older comment's author older ones.", nameof(author));
        }
    }

    public static void Read(Slide slide, List<SlideComment> threads)
    {
        if (LegacyPartName(slide) is { } legacy)
        {
            XElement root = slide.Presentation.Package.Xml(slide.Presentation.Package.GetPart(legacy)!).Root;
            Dictionary<(string?, string?), SlideComment> byIndex = [];
            List<(SlideComment Comment, XElement Parent)> replies = [];
            foreach (XElement cm in root.Elements(PptxNames.P + "cm"))
            {
                XElement? parent = Threading(cm)?.Element(PptxNames.P15 + "parentCm");
                SlideComment comment = new(slide, cm, null);
                byIndex[((string?)cm.Attribute("authorId"), (string?)cm.Attribute("idx"))] = comment;
                if (parent is null)
                {
                    threads.Add(comment);
                }
                else
                {
                    replies.Add((comment, parent));
                }
            }

            foreach ((SlideComment comment, XElement parent) in replies)
            {
                if (byIndex.TryGetValue(((string?)parent.Attribute("authorId"), (string?)parent.Attribute("idx")), out SlideComment? thread))
                {
                    SlideComment reply = new(slide, comment.Element, thread.Parent ?? thread);
                    (thread.Parent ?? thread).ReplyList.Add(reply);
                }
                else
                {
                    threads.Add(comment);
                }
            }
        }

        if (ModernPartName(slide) is { } modern)
        {
            foreach (XElement cm in slide.Presentation.Package.Xml(slide.Presentation.Package.GetPart(modern)!).Root.Elements(P188 + "cm"))
            {
                SlideComment comment = new(slide, cm, null);
                foreach (XElement reply in cm.Element(P188 + "replyLst")?.Elements(P188 + "reply") ?? [])
                {
                    comment.ReplyList.Add(new SlideComment(slide, reply, comment));
                }

                threads.Add(comment);
            }
        }
    }

    public static SlideComment AddThread(Slide slide, CommentAuthor author, string text)
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        XElement cm;
        if (author.IsModern)
        {
            XmlPart part = ModernPart(slide);
            cm = new XElement(P188 + "cm", new XAttribute("id", NewGuid()), new XAttribute("authorId", author.Id), new XAttribute("created", now),
                new XElement(Pc + "sldMkLst", new XAttribute(XNamespace.Xmlns + "pc", Pc.NamespaceName),
                    new XElement(Pc + "docMk"), new XElement(Pc + "sldMk", new XAttribute("sldId", PptxNames.Text(slide.SlideId)))),
                TextBody.NewElement(text, P188 + "txBody"));
            part.Root.Add(cm);
            part.MarkChanged();
        }
        else
        {
            XmlPart part = LegacyPart(slide);
            cm = NewLegacy(author, now, text);
            part.Root.Add(cm);
            part.MarkChanged();
        }

        return new SlideComment(slide, cm, null);
    }

    public static SlideComment AddReply(SlideComment thread, CommentAuthor author, string text)
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        XElement element;
        if (thread.IsModern)
        {
            XElement list = thread.Element.Element(P188 + "replyLst") ?? AddReplyList(thread.Element);
            element = new XElement(P188 + "reply", new XAttribute("id", NewGuid()), new XAttribute("authorId", author.Id), new XAttribute("created", now),
                TextBody.NewElement(text, P188 + "txBody"));
            list.Add(element);
        }
        else
        {
            element = NewLegacy(author, now, text);
            if (thread.Element.Element(PptxNames.P + "pos") is { } position)
            {
                element.Element(PptxNames.P + "pos")!.ReplaceWith(new XElement(position));
            }

            element.Add(new XElement(PptxNames.P + "extLst", new XElement(PptxNames.P + "ext", new XAttribute("uri", ThreadingUri),
                new XElement(PptxNames.P15 + "threadingInfo", new XAttribute(XNamespace.Xmlns + "p15", PptxNames.P15.NamespaceName), new XAttribute("timeZoneBias", "0"),
                    new XElement(PptxNames.P15 + "parentCm", new XAttribute("authorId", (string?)thread.Element.Attribute("authorId") ?? "0"),
                        new XAttribute("idx", (string?)thread.Element.Attribute("idx") ?? "0"))))));
            XElement last = thread.ReplyList.Count > 0 ? thread.ReplyList[^1].Element : thread.Element;
            last.AddAfterSelf(element);
        }

        thread.Part.MarkChanged();
        SlideComment reply = new(thread.Slide, element, thread);
        thread.ReplyList.Add(reply);
        return reply;
    }

    public static void Remove(SlideComment comment)
    {
        XmlPart part = comment.Part;
        Slide slide = comment.Slide;
        foreach (SlideComment reply in comment.Replies)
        {
            reply.Element.Remove();
        }

        XElement? list = comment.Element.Parent;
        comment.Element.Remove();
        if (list is not null && list.Name == P188 + "replyLst" && !list.HasElements)
        {
            list.Remove();
        }

        part.MarkChanged();
        if (part.Root.HasElements)
        {
            return;
        }

        // The last comment is gone: the part goes, with the slide's link to it.
        PptxPackage package = slide.Presentation.Package;
        string type = part.Root.Name.Namespace == P188 ? ModernCommentsRelationship : LegacyCommentsRelationship;
        foreach (var relationship in package.Relationships(slide.PartName).Where(r => !r.External && r.TargetName == part.Part.Name && Documents.Docx.OpcRelationshipTypes.Same(r.Type, type)).ToList())
        {
            package.RemoveRelationship(slide.PartName, relationship.Id);
            if (type == ModernCommentsRelationship)
            {
                RemoveCommentRel(slide, relationship.Id);
            }
        }

        package.RemovePart(part.Part.Name);
    }

    public static XElement AddLegacyText(XElement cm)
    {
        XElement text = new(PptxNames.P + "text");
        if (cm.Element(PptxNames.P + "pos") is { } pos)
        {
            pos.AddAfterSelf(text);
        }
        else
        {
            cm.AddFirst(text);
        }

        return text;
    }

    private static string? RelatedName(Slide slide, string type) => slide.Presentation.Package.Related(slide.PartName, type)?.Name;

    private static XmlPart? Part(Presentation presentation, string source, string type) =>
        presentation.Package.Related(source, type) is { } part ? presentation.Package.Xml(part) : null;

    private static XElement? Threading(XElement cm) => cm.Element(PptxNames.P + "extLst")?.Elements(PptxNames.P + "ext")
        .FirstOrDefault(e => (string?)e.Attribute("uri") == ThreadingUri)?.Element(PptxNames.P15 + "threadingInfo");

    private static XElement NewLegacy(CommentAuthor author, string now, string text)
    {
        uint index = (PptxNames.UInt(author.Element, "lastIdx") ?? 0) + 1;
        author.Element.SetAttributeValue("lastIdx", PptxNames.Text(index));
        author.Presentation.Package.Xml(author.Presentation.Package.Related(author.Presentation.Main.Part.Name, LegacyAuthorsRelationship)!).MarkChanged();
        return new XElement(PptxNames.P + "cm", new XAttribute("authorId", author.Id), new XAttribute("dt", now), new XAttribute("idx", PptxNames.Text(index)),
            new XElement(PptxNames.P + "pos", new XAttribute("x", "0"), new XAttribute("y", "0")),
            new XElement(PptxNames.P + "text", text));
    }

    private static XElement AddReplyList(XElement cm)
    {
        XElement list = new(P188 + "replyLst");

        // CT_Comment: the anchor, pos, replyLst, then txBody and extLst.
        if ((cm.Element(P188 + "pos") ?? cm.Elements().FirstOrDefault(e => e.Name != P188 + "txBody" && e.Name != P188 + "extLst")) is { } before)
        {
            before.AddAfterSelf(list);
        }
        else
        {
            cm.AddFirst(list);
        }

        return list;
    }

    private static XmlPart LegacyPart(Slide slide)
    {
        PptxPackage package = slide.Presentation.Package;
        if (LegacyPartName(slide) is { } existing)
        {
            return package.Xml(package.GetPart(existing)!);
        }

        PackagePart part = package.AddXmlPart(package.UniqueName("/ppt/comments/comment", ".xml"), DefaultTemplate.Ct + "comments+xml",
            DefaultTemplate.Parse($"<p:cmLst {DefaultTemplate.Namespaces}/>"));
        package.AddRelationship(slide.PartName, LegacyCommentsRelationship, part.Name);
        return part.Xml!;
    }

    private static XmlPart ModernPart(Slide slide)
    {
        PptxPackage package = slide.Presentation.Package;
        if (ModernPartName(slide) is { } existing)
        {
            return package.Xml(package.GetPart(existing)!);
        }

        PackagePart part = package.AddXmlPart(package.UniqueName("/ppt/comments/modernComment", ".xml"), ModernCommentsContentType,
            new XDocument(new XElement(P188 + "cmLst",
                new XAttribute(XNamespace.Xmlns + "a", PptxNames.A.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", PptxNames.R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "p188", P188.NamespaceName))));
        string rid = package.AddRelationship(slide.PartName, ModernCommentsRelationship, part.Name);

        // The slide names its comments part in an extension too ([MS-PPTX] 2.2.10).
        XElement root = slide.Xml.Root;
        XElement extensions = root.Element(PptxNames.P + "extLst") ?? AddLast(root, PptxNames.P + "extLst");
        extensions.Add(new XElement(PptxNames.P + "ext", new XAttribute("uri", CommentRelUri),
            new XElement(P188 + "commentRel", new XAttribute(XNamespace.Xmlns + "p188", P188.NamespaceName), new XAttribute(PptxNames.R + "id", rid))));
        slide.Xml.MarkChanged();
        return part.Xml!;
    }

    private static void RemoveCommentRel(Slide slide, string rid)
    {
        XElement? extensions = slide.Xml.Root.Element(PptxNames.P + "extLst");
        foreach (XElement extension in extensions?.Elements(PptxNames.P + "ext").Where(e => (string?)e.Attribute("uri") == CommentRelUri
            && (string?)e.Element(P188 + "commentRel")?.Attribute(PptxNames.R + "id") == rid).ToList() ?? [])
        {
            extension.Remove();
        }

        if (extensions is { HasElements: false })
        {
            extensions.Remove();
        }

        slide.Xml.MarkChanged();
    }

    private static XElement AddLast(XElement parent, XName name)
    {
        XElement made = new(name);
        parent.Add(made);
        return made;
    }
}
