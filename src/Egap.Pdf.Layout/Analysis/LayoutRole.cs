namespace Egap.Pdf.Layout;

/// <summary>What a region of a page is (design Q4), the roles layout analysis finds and a region detector can name.</summary>
public enum LayoutRole
{
    /// <summary>Body text: a paragraph or part of one.</summary>
    Text = 0,

    /// <summary>The document's title.</summary>
    Title = 1,

    /// <summary>A section heading.</summary>
    Heading = 2,

    /// <summary>One item of a bulleted or numbered list.</summary>
    ListItem = 3,

    /// <summary>The caption of a table or figure.</summary>
    Caption = 4,

    /// <summary>A footnote or endnote at the page foot.</summary>
    Footnote = 5,

    /// <summary>A running header repeated at the top of pages.</summary>
    PageHeader = 6,

    /// <summary>A running footer repeated at the bottom of pages.</summary>
    PageFooter = 7,

    /// <summary>A page number.</summary>
    PageNumber = 8,

    /// <summary>A block quote.</summary>
    Quote = 9,

    /// <summary>Program code or other preformatted text.</summary>
    Code = 10,

    /// <summary>A mathematical formula set apart from the text.</summary>
    Formula = 11,

    /// <summary>A table.</summary>
    Table = 12,

    /// <summary>A picture or drawing.</summary>
    Figure = 13,
}
