using System.Text.RegularExpressions;

using Egami.Barcodes;

using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Reports;

/// <summary>Where a container's content goes: a flow of blocks, a table cell or a flex or grid item.</summary>
internal sealed class Slot
{
    private enum Kind
    {
        Flow,
        Cell,
        Item,
    }

    private Kind kind;
    private NodeCollection<Block>? blocks;
    private Cell? cell;
    private BlockContainer? item;
    private int start;
    private bool used;

    private Slot(ReportBuilder report, TextStyle style, double width)
    {
        Report = report;
        Style = style;
        Width = width;
    }

    public ReportBuilder Report { get; }

    public Document Document => Report.Document;

    public TextStyle Style { get; set; }

    public ParagraphAlignment? Alignment { get; set; }

    /// <summary>The width the content has, in points, for pictures and table columns.</summary>
    public double Width { get; private set; }

    /// <summary>The space kept above the content (a column's spacing).</summary>
    public double Gap { get; set; }

    public BoxStyle? Box { get; set; }

    public GroupPaging? Paging { get; set; }

    public static Slot Flow(ReportBuilder report, NodeCollection<Block> blocks, TextStyle style, double width)
    {
        return new Slot(report, style, width) { kind = Kind.Flow, blocks = blocks };
    }

    public static Slot ForCell(ReportBuilder report, Cell cell, TextStyle style, double width)
    {
        return new Slot(report, style, width) { kind = Kind.Cell, cell = cell };
    }

    public static Slot ForItem(ReportBuilder report, BlockContainer item, TextStyle style, double width)
    {
        return new Slot(report, style, width) { kind = Kind.Item, item = item };
    }

    /// <summary>A slot of the same kind of content for a child, with this one's style and alignment.</summary>
    public Slot Child(NodeCollection<Block> target, double width)
    {
        return new Slot(Report, Style, width) { kind = Kind.Flow, blocks = target, Alignment = Alignment };
    }

    /// <summary>The width inside the box: less its margins, borders and padding.</summary>
    public double Inner => Math.Max(1, (Box?.Width.Unit == WidthUnit.Points ? Box.Width.Value : Width - (Box?.MarginsX ?? 0)) - (Box?.Horizontal ?? 0));

    /// <summary>The blocks the content goes into, made once: the box and paging settings applied where the slot keeps them.</summary>
    public NodeCollection<Block> Open()
    {
        if (used)
        {
            throw new InvalidOperationException("A container holds one element; use Column to put several one under another.");
        }

        used = true;
        switch (kind)
        {
            case Kind.Cell:
                ApplyToCell(cell!);
                return cell!.Blocks;
            case Kind.Item:
                {
                    BoxStyle? merged = Merge(item switch { FlexItem f => f.Box, GridItem g => g.Box, _ => null }, Box);
                    switch (item)
                    {
                        case FlexItem flex:
                            flex.Box = merged;
                            break;
                        case GridItem grid:
                            grid.Box = merged;
                            break;
                    }

                    return Paging is null ? item!.Blocks : Group(item!.Blocks, null);
                }

            default:
                if (Box is not null || Paging is not null)
                {
                    BoxStyle? box = Gap > 0 ? (Box ?? new BoxStyle()) with { MarginTop = (Box?.MarginTop ?? 0) + Gap } : Box;
                    Gap = 0;
                    return Group(blocks!, box);
                }

                start = blocks!.Count;
                return blocks;
        }
    }

    /// <summary>After the content: a column's spacing goes on its first block, as space before a paragraph or a margin above anything else.</summary>
    public void Close()
    {
        if (kind != Kind.Flow || Gap <= 0 || blocks is null || start >= blocks.Count)
        {
            return;
        }

        Block first = blocks[start];
        if (first is Paragraph paragraph)
        {
            paragraph.Format.SpaceBefore = (paragraph.Format.SpaceBefore ?? 0) + Gap;
            return;
        }

        ContentControl spaced = new(Document) { Box = new BoxStyle { MarginTop = Gap } };
        spaced.Properties.Kind = ContentControlKind.Group;
        blocks.Insert(start, spaced);
        first.Remove();
        spaced.Blocks.Add(first);
    }

    private NodeCollection<Block> Group(NodeCollection<Block> target, BoxStyle? box)
    {
        ContentControl group = new(Document) { Box = box, Paging = Paging };
        group.Properties.Kind = ContentControlKind.Group;
        target.Add(group);
        return group.Blocks;
    }

    private void ApplyToCell(Cell target)
    {
        if (Box is not { } box)
        {
            return;
        }

        CellFormat format = target.Format;
        format.MarginTop = box.PaddingTop;
        format.MarginBottom = box.PaddingBottom;
        format.MarginLeft = box.PaddingLeft;
        format.MarginRight = box.PaddingRight;
        format.BorderTop = box.BorderTop ?? format.BorderTop;
        format.BorderRight = box.BorderRight ?? format.BorderRight;
        format.BorderBottom = box.BorderBottom ?? format.BorderBottom;
        format.BorderLeft = box.BorderLeft ?? format.BorderLeft;
        if (box.Background is { } background)
        {
            format.Shading = Shading.Solid(background);
        }

        if (Paging is { KeepTogether: true } or { ShowEntire: true } && target.Row is { } row)
        {
            row.Format.CantSplit = true;
        }
    }

    private static BoxStyle? Merge(BoxStyle? under, BoxStyle? over)
    {
        if (under is null || over is null)
        {
            return over ?? under;
        }

        return over with
        {
            Width = over.Width.Unit == WidthUnit.Auto ? under.Width : over.Width,
            MinHeight = Math.Max(over.MinHeight, under.MinHeight),
        };
    }
}

/// <summary>
/// A place in a report that takes one element (text, an image, a column, a row, a table, a grid, a barcode, a form field
/// or a component), with the box, alignment, style and paging settings set on it first: each setting returns the
/// container, and the element ends the chain.
/// </summary>
public sealed partial class Container
{
    private readonly Slot slot;

    internal Container(Slot slot)
    {
        this.slot = slot;
    }

    /// <summary>The document being built.</summary>
    public Document Document => slot.Document;

    // ---- Box --------------------------------------------------------------------------------------------------------------

    /// <summary>The same padding on every side.</summary>
    public Container Padding(Length all) => Padding(all, all, all, all);

    /// <summary>Padding above and below, and left and right.</summary>
    public Container Padding(Length vertical, Length horizontal) => Padding(vertical, horizontal, vertical, horizontal);

    /// <summary>Padding on each side.</summary>
    public Container Padding(Length top, Length right, Length bottom, Length left)
    {
        return Box(b => b with { PaddingTop = b.PaddingTop + top, PaddingRight = b.PaddingRight + right, PaddingBottom = b.PaddingBottom + bottom, PaddingLeft = b.PaddingLeft + left });
    }

    /// <summary>A background colour.</summary>
    public Container Background(DocumentColor color) => Box(b => b with { Background = color });

    /// <summary>A background colour from a hex string such as <c>"F4F8FC"</c>.</summary>
    public Container Background(string hex) => Background(DocumentColor.FromHex(hex));

    /// <summary>A border on every side, black unless <paramref name="color"/> is given.</summary>
    public Container Border(Length width, DocumentColor? color = null)
    {
        Border border = new(BorderStyle.Single, width, color ?? DocumentColor.FromHex("000000"));
        return Box(b => b.WithBorder(border));
    }

    /// <summary>A border along the bottom only.</summary>
    public Container BorderBottom(Length width, DocumentColor? color = null)
    {
        return Box(b => b with { BorderBottom = new Border(BorderStyle.Single, width, color ?? DocumentColor.FromHex("000000")) });
    }

    /// <summary>Rounded corners of <paramref name="radius"/>.</summary>
    public Container CornerRadius(Length radius) => Box(b => b with { CornerRadius = BoxCorners.All(radius) });

    /// <summary>A shadow behind the box.</summary>
    public Container Shadow(BoxShadow shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        return Box(b => b with { Shadow = shadow });
    }

    /// <summary>A width, borders and padding included.</summary>
    public Container Width(Length width) => Box(b => b with { Width = TableWidth.Points(width) });

    /// <summary>A least height, borders and padding included.</summary>
    public Container MinHeight(Length height) => Box(b => b with { MinHeight = height });

    /// <summary>Space above, which a page break leaves out.</summary>
    public Container MarginTop(Length space) => Box(b => b with { MarginTop = b.MarginTop + space });

    private Container Box(Func<BoxStyle, BoxStyle> change)
    {
        slot.Box = change(slot.Box ?? new BoxStyle());
        return this;
    }

    // ---- Alignment and style ----------------------------------------------------------------------------------------------

    /// <summary>Text inside lines up on the left.</summary>
    public Container AlignLeft() => Align(ParagraphAlignment.Left);

    /// <summary>Text inside is centred.</summary>
    public Container AlignCenter() => Align(ParagraphAlignment.Center);

    /// <summary>Text inside lines up on the right.</summary>
    public Container AlignRight() => Align(ParagraphAlignment.Right);

    /// <summary>Text inside is justified.</summary>
    public Container AlignJustify() => Align(ParagraphAlignment.Justify);

    private Container Align(ParagraphAlignment alignment)
    {
        slot.Alignment = alignment;
        return this;
    }

    /// <summary>The text style everything inside starts from.</summary>
    public Container DefaultTextStyle(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        slot.Style = change(slot.Style);
        return this;
    }

    // ---- Paging -----------------------------------------------------------------------------------------------------------

    /// <summary>The content starts on the next page when it does not fit on this one but would on an empty one.</summary>
    public Container KeepTogether() => Pages(p => p with { KeepTogether = true });

    /// <summary>The content starts on the next page when less than <paramref name="height"/> is left on this one.</summary>
    public Container EnsureSpace(Length height) => Pages(p => p with { EnsureSpace = height });

    /// <summary>The content is never split across pages; strict layout fails when it does not fit even on an empty page.</summary>
    public Container ShowEntire() => Pages(p => p with { ShowEntire = true });

    /// <summary>Only what fits on the page the content starts on is shown.</summary>
    public Container StopPaging() => Pages(p => p with { StopPaging = true });

    /// <summary>The content is drawn smaller, down to a fifth, when that makes it fit where it starts.</summary>
    public Container ScaleToFit() => Pages(p => p with { ScaleToFit = true });

    private Container Pages(Func<GroupPaging, GroupPaging> change)
    {
        slot.Paging = change(slot.Paging ?? new GroupPaging());
        return this;
    }

    // ---- Elements ---------------------------------------------------------------------------------------------------------

    /// <summary>A paragraph of <paramref name="text"/>; <see cref="Fields.Page"/> and <see cref="Fields.Pages"/> in it become page fields.</summary>
    public TextDescriptor Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Paragraph paragraph = Paragraph();
        List<Run> runs = AddText(paragraph, text, slot.Style);
        Done();
        return new TextDescriptor(slot.Report, paragraph, runs, slot.Style);
    }

    /// <summary>A paragraph of spans, each styled on its own, with page numbers and links.</summary>
    public void Text(Action<TextBuilder> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        Paragraph paragraph = Paragraph();
        spans(new TextBuilder(slot.Report, paragraph, slot.Style));
        Done();
    }

    /// <summary>
    /// A picture of <paramref name="image"/>: at its own size held to the width there is, or at the size given (one side
    /// alone keeps its proportions).
    /// </summary>
    public Picture Image(ImageData image, Length? width = null, Length? height = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        (double w, double h) = ImageSize.Intrinsic(image.Bytes);
        if (w <= 0 || h <= 0)
        {
            (w, h) = (width?.Points ?? 72, height?.Points ?? 72);
        }

        double ratio = h / w;
        double? wide = width?.Points, high = height?.Points;
        (double shownWidth, double shownHeight) = (wide, high) switch
        {
            (double given, double tall) => (given, tall),
            (double given, null) => (given, given * ratio),
            (null, double tall) => (tall / ratio, tall),
            _ => w > slot.Inner ? (slot.Inner, slot.Inner * ratio) : (w, h),
        };
        Paragraph paragraph = Paragraph();
        Picture picture = paragraph.AddPicture(image, shownWidth, shownHeight);
        Done();
        return picture;
    }

    /// <summary>A barcode or QR code, drawn as vectors, <paramref name="width"/> by <paramref name="height"/>.</summary>
    public Picture Barcode(Barcode barcode, Length width, Length height, BarcodeDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        Paragraph paragraph = Paragraph();
        Picture picture = DocumentBuilder.BarcodePicture(Document, barcode, width, height, options);
        paragraph.Inlines.Add(picture);
        Done();
        return picture;
    }

    /// <summary>A horizontal line across the width, <paramref name="thickness"/> thick.</summary>
    public void Line(Length? thickness = null, DocumentColor? color = null)
    {
        Paragraph paragraph = Paragraph();
        paragraph.Format.BorderBottom = new Border(BorderStyle.Single, thickness?.Points ?? 0.75, color ?? DocumentColor.FromHex("000000"));
        paragraph.Format.LineSpacingRule = LineSpacingRule.Exactly;
        paragraph.Format.LineSpacing = 1;
        Done();
    }

    /// <summary>A page break: what follows starts on the next page.</summary>
    public void PageBreak()
    {
        Paragraph paragraph = Paragraph();
        paragraph.AddBreak(BreakKind.Page);
        Done();
    }

    /// <summary>Elements one under another, with <see cref="ColumnBuilder.Spacing"/> between them.</summary>
    public void Column(Action<ColumnBuilder> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        NodeCollection<Block> blocks = slot.Open();
        column(new ColumnBuilder(slot, blocks));
        Finish(blocks);
    }

    /// <summary>Elements side by side, of fixed widths or sharing the width (a flex container).</summary>
    public void Row(Action<RowBuilder> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        NodeCollection<Block> blocks = slot.Open();
        FlexContainer flex = new(Document);
        blocks.Add(flex);
        row(new RowBuilder(slot, flex));
        Finish(blocks);
    }

    /// <summary>Elements in columns and rows of tracks, placed in order or by line, span and area (a grid container).</summary>
    public void Grid(Action<GridBuilder> grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        NodeCollection<Block> blocks = slot.Open();
        GridContainer container = new(Document);
        blocks.Add(container);
        grid(new GridBuilder(slot, container));
        Finish(blocks);
    }

    /// <summary>A table: its columns, header and footer rows that repeat on every page, and cells filled in order.</summary>
    public void Table(Action<TableBuilder> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        NodeCollection<Block> blocks = slot.Open();
        Table model = new(Document);
        blocks.Add(model);
        TableBuilder builder = new(slot, model);
        table(builder);
        builder.Finish();
        Finish(blocks);
    }

    /// <summary>A reusable component, composed into this container.</summary>
    public void Component(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        component.Compose(this);
    }

    /// <summary>Content added by <paramref name="compose"/>, for a container's settings to wrap a piece of code.</summary>
    public void Element(Action<Container> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        compose(this);
    }

    /// <summary>A text form field named <paramref name="name"/>, <paramref name="width"/> characters wide; PDF export can make it a field.</summary>
    public InlineContentControl TextField(string name, string value = "", int width = 20)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        InlineContentControl control = Control(name, ContentControlKind.PlainText);
        control.Properties.ShowingPlaceholder = value.Length == 0;
        control.AddRun(value.Length >= width ? value : value + new string(' ', Math.Max(0, width - value.Length)));
        return control;
    }

    /// <summary>A check box named <paramref name="name"/>; PDF export can make it a field.</summary>
    public InlineContentControl CheckBox(string name, bool isChecked = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        InlineContentControl control = Control(name, ContentControlKind.CheckBox);
        control.Properties.IsChecked = isChecked;
        control.AddRun("  ");
        return control;
    }

    /// <summary>A drop-down list named <paramref name="name"/> of <paramref name="items"/>; PDF export can make it a field.</summary>
    public InlineContentControl DropDown(string name, IReadOnlyList<string> items, string? selected = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(items);
        InlineContentControl control = Control(name, ContentControlKind.DropDownList);
        control.Properties.Items = [.. items];
        string shown = selected ?? (items.Count > 0 ? items[0] : "");
        int width = Math.Max(4, items.Count > 0 ? items.Max(i => i.Length) + 2 : 4);
        control.AddRun(shown.Length >= width ? shown : shown + new string(' ', width - shown.Length));
        return control;
    }

    private InlineContentControl Control(string name, ContentControlKind kind)
    {
        Paragraph paragraph = Paragraph();
        InlineContentControl control = new(Document);
        control.Properties.Kind = kind;
        control.Properties.Tag = name;
        paragraph.Inlines.Add(control);
        Done();
        return control;
    }

    /// <summary>A new paragraph in the slot, with its alignment and line height.</summary>
    private Paragraph Paragraph()
    {
        NodeCollection<Block> blocks = slot.Open();
        Paragraph paragraph = new(Document);
        blocks.Add(paragraph);
        paragraph.Format.SpaceAfter = 0;
        paragraph.Format.SpaceBefore = 0;
        if (slot.Alignment is { } alignment)
        {
            paragraph.Format.Alignment = alignment;
        }

        slot.Style.ApplyTo(paragraph.Format);
        paragraph.Format.LineSpacingRule ??= LineSpacingRule.Multiple;
        paragraph.Format.LineSpacing ??= 1;
        return paragraph;
    }

    private void Done() => slot.Close();

    private void Finish(NodeCollection<Block> blocks)
    {
        _ = blocks;
        slot.Close();
    }

    /// <summary>Runs for <paramref name="text"/> in <paramref name="style"/>, its page placeholders made fields.</summary>
    internal static List<Run> AddText(Paragraph paragraph, string text, TextStyle style)
    {
        List<Run> runs = [];
        int at = 0;
        foreach (Match match in PageBand.Placeholder().Matches(text))
        {
            if (match.Index > at)
            {
                runs.Add(Styled(paragraph.AddRun(text[at..match.Index]), style));
            }

            at = match.Index + match.Length;
            if (PageBandContext.FieldCode(match) is { } code)
            {
                Field field = paragraph.AddField(code, "1");
                field.Start.IsDirty = true;
                runs.AddRange(field.ResultNodes.OfType<Run>().Select(r => Styled(r, style)));
            }
            else
            {
                runs.Add(Styled(paragraph.AddRun(match.Value == "{{" ? "{" : match.Value), style));
            }
        }

        if (at < text.Length || runs.Count == 0)
        {
            runs.Add(Styled(paragraph.AddRun(text[at..]), style));
        }

        return runs;
    }

    internal static Run Styled(Run run, TextStyle style)
    {
        style.ApplyTo(run.Format, TextStyle.Default);
        return run;
    }
}
