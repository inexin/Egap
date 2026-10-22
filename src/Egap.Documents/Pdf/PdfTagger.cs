using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// The structure tree of an exported document, built from the model in reading order before any page is drawn, and the
/// observer that marks each drawn line and picture with its element (Build Plan task 22, Phase 5's tagging on the canvas).
/// Headers, footers, borders, shading and frames are artifacts.
/// </summary>
internal sealed class PdfTagger
{
    private static readonly PdfStructureType Div = new("Div");
    private static readonly PdfStructureType[] Headings = [PdfStructureType.H1, PdfStructureType.H2, PdfStructureType.H3, PdfStructureType.H4, PdfStructureType.H5, PdfStructureType.H6];

    private readonly PdfStructureElement root;
    private readonly Dictionary<Node, PdfStructureElement> elements = [];
    private readonly Dictionary<Node, PdfStructureElement> links = [];

    public PdfTagger(PdfDocument pdf, Document document, string? language)
    {
        root = pdf.Structure.Add(PdfStructureType.Document);
        if (!string.IsNullOrWhiteSpace(language))
        {
            root.Language = language;
        }

        foreach (Section section in document.Sections)
        {
            AddBlocks(root, section.Body.Blocks);
        }
    }

    public LayoutDrawObserver ObserverFor(PdfCanvas canvas)
    {
        return new Observer(this, canvas);
    }

    /// <summary>A link annotation as a Link element in its paragraph's element (PDF/UA 7.18.5).</summary>
    public void TagLink(Node node, PdfPage page, PdfLinkAnnotation annotation)
    {
        if (!links.TryGetValue(node, out PdfStructureElement? link))
        {
            PdfStructureElement parent = node.Ancestors().OfType<Paragraph>().FirstOrDefault() is { } paragraph ? ElementFor(paragraph) : root;
            link = parent.Add(PdfStructureType.Link);
            links[node] = link;
        }

        link.AddAnnotation(page, annotation);
        page.Dictionary["Tabs"] = PdfName.Get("S");
    }

    private void AddBlocks(PdfStructureElement parent, IEnumerable<Block> blocks)
    {
        PdfStructureElement? list = null;
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph when paragraph.ListLabel is not null:
                    {
                        list ??= parent.Add(PdfStructureType.L);
                        PdfStructureElement item = list.Add(PdfStructureType.LI);
                        AddParagraph(item.Add(PdfStructureType.LBody), paragraph, item);
                        continue;
                    }

                case Paragraph paragraph:
                    int level = paragraph.GetEffectiveFormat().OutlineLevel;
                    AddParagraph(parent.Add(level < 6 ? Headings[level] : PdfStructureType.P), paragraph, parent);
                    break;
                case Table table:
                    PdfStructureElement tableElement = parent.Add(PdfStructureType.Table);
                    elements[table] = tableElement;
                    foreach (Row row in table.Rows)
                    {
                        PdfStructureElement tr = tableElement.Add(PdfStructureType.TR);
                        bool header = row.Format.IsHeader == true;
                        foreach (Cell cell in row.Cells)
                        {
                            PdfStructureElement td = tr.Add(header ? PdfStructureType.TH : PdfStructureType.TD);
                            elements[cell] = td;
                            AddBlocks(td, cell.Blocks);
                        }
                    }

                    break;
                case ContentControl control:
                    AddBlocks(parent, control.Blocks);
                    break;
                case FlexContainer or GridContainer:
                    {
                        // A layout container groups its items: a Div, and a Div for each item in reading order.
                        PdfStructureElement division = parent.Add(PdfStructureType.Div);
                        elements[block] = division;
                        IEnumerable<BlockContainer> items = block is FlexContainer flex ? flex.Items.OrderBy(i => i.Order) : ((GridContainer)block).Items;
                        foreach (BlockContainer item in items)
                        {
                            PdfStructureElement itemElement = division.Add(PdfStructureType.Div);
                            elements[item] = itemElement;
                            AddBlocks(itemElement, item.Blocks);
                        }

                        break;
                    }

                default:
                    elements[block] = parent;
                    break;
            }

            list = null;
        }
    }

    /// <summary>A paragraph's element, then its notes and text boxes after it (under <paramref name="parent"/>).</summary>
    private void AddParagraph(PdfStructureElement element, Paragraph paragraph, PdfStructureElement parent)
    {
        elements[paragraph] = element;
        foreach (Footnote note in paragraph.Descendants<Footnote>())
        {
            PdfStructureElement noteElement = element.Add(PdfStructureType.Note);
            elements[note] = noteElement;
            AddBlocks(noteElement, note.Story.Blocks);
        }

        foreach (Shape shape in paragraph.Descendants<Shape>())
        {
            if (shape.TextBox is { } story)
            {
                PdfStructureElement box = parent.Add(Div);
                elements[shape] = box;
                AddBlocks(box, story.Blocks);
            }
        }
    }

    /// <summary>The element a node's drawing belongs to: built in advance for blocks, made here for figures and anything late.</summary>
    private PdfStructureElement ElementFor(Node node)
    {
        if (elements.TryGetValue(node, out PdfStructureElement? element))
        {
            return element;
        }

        PdfStructureElement parent = node.Ancestors().FirstOrDefault(elements.ContainsKey) is { } ancestor ? elements[ancestor] : root;
        element = node switch
        {
            Paragraph => parent.Add(PdfStructureType.P),
            Picture or Island or Shape or BlockIsland => parent.Add(PdfStructureType.Figure),
            _ => parent,
        };
        if (element.Type == PdfStructureType.Figure)
        {
            string? alternate = node switch
            {
                Picture picture => picture.Description ?? picture.Title,
                Shape shape => shape.Description ?? shape.Title,
                _ => null,
            };
            element.AlternateText = string.IsNullOrWhiteSpace(alternate) ? null : alternate;
        }

        elements[node] = element;
        return element;
    }

    /// <summary>Marks a page's drawing as the layout draws it.</summary>
    private sealed class Observer(PdfTagger tagger, PdfCanvas canvas) : LayoutDrawObserver
    {
        private int artifactAreas;
        private readonly Stack<bool> marked = new();

        public override void BeginBox(LayoutBox box)
        {
            if (box is AreaBox { Area: PageArea.Header or PageArea.Footer or PageArea.MarginBox })
            {
                artifactAreas++;
            }

            bool leaf = box is LineBox || (box is ObjectBox && box.Children.Count == 0);
            marked.Push(leaf);
            if (!leaf)
            {
                return;
            }

            if (artifactAreas > 0 || box.Node is null || (box.Node is Shape { Description: null, Title: null } && box is ObjectBox))
            {
                canvas.BeginArtifact();
            }
            else
            {
                canvas.BeginTag(tagger.ElementFor(box.Node));
            }
        }

        public override void EndBox(LayoutBox box)
        {
            if (marked.Pop())
            {
                canvas.EndTag();
            }

            if (box is AreaBox { Area: PageArea.Header or PageArea.Footer or PageArea.MarginBox })
            {
                artifactAreas--;
            }
        }

        public override void BeginDecoration()
        {
            canvas.BeginArtifact();
        }

        public override void EndDecoration()
        {
            canvas.EndTag();
        }
    }
}
