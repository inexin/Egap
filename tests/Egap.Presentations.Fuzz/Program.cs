using System.Globalization;
using System.IO.Compression;

using Egap.Documents;
using Egap.Documents.Office;
using Egap.Pdf.Fuzz;
using Egap.Presentations;

using SharpFuzz;

// Fuzz targets for Egap.Presentations: a PPTX package read, walked, edited, saved and read again, and an encrypted one opened
// with a password. Each may refuse its input only with a DocumentException (a DocumentLimitException or a
// DocumentPasswordException among them). Anything else is a finding.
Dictionary<string, Action<Stream>> targets = new(StringComparer.Ordinal)
{
    ["pptx"] = Pptx,
    ["pptxedit"] = PptxEdit,
    ["pptxcrypt"] = Encrypted,
};

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    return Mutator.Run(targets[mutateTarget], TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)), seeds, findings);
}

if (args is ["seeds", string folder])
{
    Seeds.Write(folder);
    return 0;
}

if (args is ["replay", string replayTarget, string replayFolder])
{
    // Runs every finding in a folder and prints each distinct failure once, with its first input.
    Dictionary<string, string> seen = [];
    foreach (string path in Directory.EnumerateFiles(replayFolder).Order(StringComparer.Ordinal))
    {
        try
        {
            using FileStream input = File.OpenRead(path);
            targets[replayTarget](input);
        }
        catch (Exception error)
        {
            string key = $"{error.GetType().Name}: {error.Message}";
            key = key.Length > 160 ? key[..160] : key;
            if (seen.TryAdd(key, path))
            {
                Console.WriteLine($"{key} ({Path.GetFileName(path)})");
            }
        }
    }

    Console.WriteLine($"{seen.Count} distinct failures.");
    return 0;
}

if (args is [string target, string file])
{
    using FileStream input = File.OpenRead(file);
    targets[target](input);
    return 0;
}

if (args is [string stdinTarget])
{
    Fuzzer.OutOfProcess.Run(targets[stdinTarget]);
    return 0;
}

Console.Error.WriteLine($"Usage: Egap.Presentations.Fuzz <target> [file] | mutate <target> <seconds> <seeds> <findings> | replay <target> <folder> | seeds <folder>. Targets: {string.Join(", ", targets.Keys)}.");
return 2;

// Any bytes read as a PPTX package: every slide, layout and master walked, the deck saved and read again.
static void Pptx(Stream input)
{
    try
    {
        Presentation deck = Presentation.Load(new MemoryStream(Bytes(input), writable: false), Options());
        Walk.Deck(deck);
        Walk.Deck(SaveAndLoad(deck, null));
    }
    catch (DocumentException)
    {
    }
}

// The same, with edits between: transitions changed, animations and shapes removed, tables grown, slides added, moved and
// removed, properties and show settings set, then saved as another kind and read again.
static void PptxEdit(Stream input)
{
    try
    {
        Presentation deck = Presentation.Load(new MemoryStream(Bytes(input), writable: false), Options());
        Walk.Deck(deck);
        Edits.Apply(deck);
        Presentation back = SaveAndLoad(deck, deck.Format == PresentationFormat.Pptx ? PresentationFormat.Ppsx : PresentationFormat.Pptx);
        Walk.Deck(back);
        Edits.Apply(back);
        SaveAndLoad(back, null);
    }
    catch (DocumentException)
    {
    }
}

// Any bytes read as an encrypted presentation with the password "Egap pässword 1", then walked and saved.
static void Encrypted(Stream input)
{
    try
    {
        Presentation deck = Presentation.Load(new MemoryStream(Bytes(input), writable: false), new PresentationLoadOptions
        {
            Password = Seeds.Password,
            Limits = Limits(),
        });
        Walk.Deck(deck);
        using MemoryStream saved = new();
        deck.Save(saved);
    }
    catch (DocumentException)
    {
    }
}

static Presentation SaveAndLoad(Presentation deck, PresentationFormat? format)
{
    using MemoryStream saved = new();
    deck.Save(saved, new PresentationSaveOptions { Format = format });
    return Presentation.Load(new MemoryStream(saved.ToArray(), writable: false), Options());
}

static PresentationLoadOptions Options() => new() { Limits = Limits() };

static DocumentLimits Limits() => new() { MaxPartSize = 16 * 1024 * 1024, MaxTotalSize = 64 * 1024 * 1024 };

static byte[] Bytes(Stream input)
{
    using MemoryStream copy = new();
    input.CopyTo(copy);
    return copy.ToArray();
}

/// <summary>Reads everything the model offers, so that each getter meets the fuzzed XML.</summary>
internal static class Walk
{
    public static void Deck(Presentation deck)
    {
        _ = deck.SlideSize;
        _ = deck.NotesSize;
        _ = deck.Format;
        _ = deck.HasModifyPassword;
        PresentationProperties properties = deck.Properties;
        _ = (properties.Title, properties.Author, properties.Subject, properties.Keywords, properties.Comments, properties.Category, properties.LastSavedBy);
        _ = (properties.Created, properties.Saved, properties.Printed, properties.Company, properties.Manager, properties.Application);
        foreach (string name in properties.CustomNames)
        {
            _ = properties.GetCustom(name);
        }

        _ = (deck.SlideShow.Type, deck.SlideShow.Loop, deck.SlideShow.UseTimings, deck.SlideShow.ShowNarration, deck.SlideShow.WithoutAnimations);
        foreach (SlideMaster master in deck.SlideMasters)
        {
            Base(master);
            foreach (SlideLayout layout in master.Layouts)
            {
                Base(layout);
            }
        }

        _ = deck.NotesMaster?.Shapes.Count;
        foreach (CommentAuthor author in deck.CommentAuthors)
        {
            _ = (author.Name, author.Initials);
        }

        foreach (SlideSection section in deck.Sections)
        {
            _ = (section.Name, section.Slides.Count);
        }

        foreach (Slide slide in deck.Slides)
        {
            Base(slide);
            _ = (slide.Hidden, slide.SlideId, slide.NotesText, slide.Notes?.NotesBody?.Text, slide.ColorMap);
            _ = slide.Transition;
            foreach (AnimationEffect effect in slide.Timing.Effects)
            {
                _ = effect.ShapeId;
            }

            foreach (SlideComment comment in slide.Comments)
            {
                _ = (comment.Text, comment.Author?.Name, comment.Created, comment.Replies.Count);
            }
        }
    }

    private static void Base(SlideBase part)
    {
        _ = (part.Name, part.ColorMap);
        Shapes(part.Shapes);
    }

    private static void Shapes(SlideShapeCollection shapes)
    {
        foreach (SlideShape shape in shapes)
        {
            _ = (shape.Id, shape.Name, shape.Description, shape.Hidden, shape.Placeholder, shape.EffectiveTransform, shape.InheritedPlaceholder?.Id);
            switch (shape)
            {
                case AutoShape auto:
                    _ = (auto.PresetGeometry, auto.TextBody?.Text, auto.TextBody?.Paragraphs.Count);
                    break;
                case GroupShape group:
                    _ = group.ChildArea;
                    Shapes(group.Shapes);
                    break;
                case GraphicFrame frame when frame.Table is { } table:
                    _ = table.StyleId;
                    for (int r = 0; r < table.Rows.Count; r++)
                    {
                        for (int c = 0; c < table.Columns.Count; c++)
                        {
                            _ = table.Cell(r, c).Text;
                        }
                    }

                    break;
            }
        }
    }
}

/// <summary>Edits that reach every part of the model that writes.</summary>
internal static class Edits
{
    public static void Apply(Presentation deck)
    {
        deck.Properties.Title = "Fuzzed";
        deck.Properties.SetCustom("Fuzz", "1");
        deck.SlideShow.Loop = !deck.SlideShow.Loop;
        deck.SetModifyPassword(null);
        foreach (Slide slide in deck.Slides.ToList())
        {
            slide.Transition = slide.Transition is { } transition
                ? transition with { AdvanceAfterMilliseconds = 1000, Speed = TransitionSpeed.Slow }
                : new SlideTransition(SlideTransitionType.Ripple) { DurationMilliseconds = 800 };
            if (slide.Shapes.Count > 0)
            {
                slide.RemoveAnimations(slide.Shapes[0]);
                slide.Shapes.Remove(slide.Shapes[^1]);
            }

            foreach (GraphicFrame frame in slide.Shapes.OfType<GraphicFrame>().ToList())
            {
                if (frame.Table is { Rows.Count: > 0, Columns.Count: > 0 } table)
                {
                    table.AddRow();
                    table.Cell(0, 0).Text = "fuzz";
                }
            }

            slide.NotesText = "Notes";
        }

        if (deck.SlideMasters is [{ Layouts: [SlideLayout layout, ..] }, ..])
        {
            Slide added = deck.Slides.Add(layout);
            deck.Slides.Move(added, 0);
            if (deck.Slides.Count > 1)
            {
                deck.Slides.Remove(deck.Slides[^1]);
            }
        }
    }
}

/// <summary>The seed decks, made with Egap: one with every kind of content, deflated and stored, and an encrypted one.</summary>
internal static class Seeds
{
    public const string Password = "Egap pässword 1";

    public static void Write(string folder)
    {
        string pptx = Path.Combine(folder, "pptx");
        string encrypted = Path.Combine(folder, "pptx-encrypted");
        Directory.CreateDirectory(pptx);
        Directory.CreateDirectory(encrypted);
        byte[] features = Features();
        File.WriteAllBytes(Path.Combine(pptx, "features.pptx"), features);
        File.WriteAllBytes(Path.Combine(pptx, "features-stored.pptx"), Stored(features));
        using (MemoryStream empty = new())
        {
            Presentation.Create().Save(empty);
            File.WriteAllBytes(Path.Combine(pptx, "empty-stored.pptx"), Stored(empty.ToArray()));
        }

        // One spin, so that each run spends its time in the parser rather than the key derivation.
        using MemoryStream agile = new();
        OfficeEncryption.Encrypt(features, Password, agile, spinCount: 1);
        File.WriteAllBytes(Path.Combine(encrypted, "agile.pptx"), agile.ToArray());
        using MemoryStream standard = new();
        OfficeEncryption.EncryptStandard(features, Password, standard);
        File.WriteAllBytes(Path.Combine(encrypted, "standard.pptx"), standard.ToArray());
    }

    private static byte[] Features()
    {
        Presentation deck = Presentation.Create();
        SlideMaster master = deck.SlideMasters[0];
        Slide title = deck.Slides.Add(master.GetLayout(SlideLayoutType.Title)!);
        ((AutoShape)title.Shapes[0]).TextBody!.Text = "Fuzz seed";
        title.NotesText = "Notes\rSecond paragraph";
        title.Transition = new SlideTransition(SlideTransitionType.Push) { Direction = "u", AdvanceAfterMilliseconds = 3000 };
        Slide shapes = deck.Slides.Add(master.GetLayout(SlideLayoutType.TitleOnly)!);
        AutoShape box = shapes.Shapes.AddShape("roundRect", ShapeTransform.FromPoints(60, 150, 200, 100), "Box");
        AutoShape oval = shapes.Shapes.AddShape("ellipse", ShapeTransform.FromPoints(500, 150, 160, 100), "Oval");
        ConnectorShape line = shapes.Shapes.AddConnector(ShapeTransform.FromPoints(260, 200, 240, 0));
        line.Start = new ShapeConnection(box.Id, 3);
        line.End = new ShapeConnection(oval.Id, 1);
        GroupShape group = shapes.Shapes.AddGroup(ShapeTransform.FromPoints(60, 300, 300, 100));
        group.Shapes.AddShape("rect", ShapeTransform.FromPoints(60, 300, 140, 100), "In group");
        shapes.Transition = new SlideTransition(SlideTransitionType.Vortex) { DurationMilliseconds = 2000, Direction = "r" };
        Slide table = deck.Slides.Add(master.GetLayout(SlideLayoutType.TitleOnly)!);
        SlideTable grid = table.Shapes.AddTable(3, 3, ShapeTransform.FromPoints(60, 150, 600, 120)).Table!;
        grid.Merge(1, 1, 2, 2).Text = "Merged";
        CommentAuthor ada = deck.CommentAuthors.Add("Ada Example", "AE");
        table.Comments.Add(ada, "A comment").Reply(ada, "A reply");
        deck.Sections.Add("One");
        deck.Sections.Add("Two", table);
        deck.Properties.Author = "Egap";
        deck.Properties.SetCustom("Seed", "yes");
        deck.SlideShow.Type = SlideShowType.Kiosk;
        deck.SetModifyPassword("edit");
        using MemoryStream saved = new();
        deck.Save(saved);
        return WithTiming(saved.ToArray(), shapes.PartName[1..], box.Id, oval.Id);
    }

    /// <summary>Adds a timing tree, as PowerPoint writes one, to a slide: a click fades the first shape in, the second follows.</summary>
    private static byte[] WithTiming(byte[] package, string slide, uint first, uint second)
    {
        static string Effect(int id, uint shape, string node, int preset) => string.Create(CultureInfo.InvariantCulture,
            $"<p:par><p:cTn id=\"{id}\" presetID=\"{preset}\" presetClass=\"entr\" presetSubtype=\"0\" fill=\"hold\" grpId=\"0\" nodeType=\"{node}\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
            + $"<p:set><p:cBhvr><p:cTn id=\"{id + 1}\" dur=\"1\" fill=\"hold\"/><p:tgtEl><p:spTgt spid=\"{shape}\"/></p:tgtEl><p:attrNameLst><p:attrName>style.visibility</p:attrName></p:attrNameLst></p:cBhvr><p:to><p:strVal val=\"visible\"/></p:to></p:set>"
            + $"<p:animEffect transition=\"in\" filter=\"fade\"><p:cBhvr><p:cTn id=\"{id + 2}\" dur=\"500\"/><p:tgtEl><p:spTgt spid=\"{shape}\"/></p:tgtEl></p:cBhvr></p:animEffect></p:childTnLst></p:cTn></p:par>");
        string timing = "<p:timing><p:tnLst><p:par><p:cTn id=\"1\" dur=\"indefinite\" restart=\"never\" nodeType=\"tmRoot\"><p:childTnLst>"
            + "<p:seq concurrent=\"1\" nextAc=\"seek\"><p:cTn id=\"2\" dur=\"indefinite\" nodeType=\"mainSeq\"><p:childTnLst>"
            + "<p:par><p:cTn id=\"3\" fill=\"hold\"><p:stCondLst><p:cond delay=\"indefinite\"/></p:stCondLst><p:childTnLst>"
            + "<p:par><p:cTn id=\"4\" fill=\"hold\"><p:stCondLst><p:cond delay=\"0\"/></p:stCondLst><p:childTnLst>"
            + Effect(5, first, "clickEffect", 10) + Effect(8, second, "withEffect", 10)
            + "</p:childTnLst></p:cTn></p:par></p:childTnLst></p:cTn></p:par>"
            + "</p:childTnLst></p:cTn></p:seq></p:childTnLst></p:cTn></p:par></p:tnLst>"
            + string.Create(CultureInfo.InvariantCulture, $"<p:bldLst><p:bldP spid=\"{first}\" grpId=\"0\"/><p:bldP spid=\"{second}\" grpId=\"0\"/></p:bldLst></p:timing>");
        return Rewrite(package, slide, xml => xml.Replace("</p:sld>", timing + "</p:sld>", StringComparison.Ordinal));
    }

    private static byte[] Rewrite(byte[] package, string name, Func<string, string> change)
    {
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        using (ZipArchive source = new(new MemoryStream(package)))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream read = entry.Open();
                using MemoryStream data = new();
                read.CopyTo(data);
                byte[] bytes = entry.FullName == name ? System.Text.Encoding.UTF8.GetBytes(change(System.Text.Encoding.UTF8.GetString(data.ToArray()))) : data.ToArray();
                using Stream write = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                write.Write(bytes);
            }
        }

        return output.ToArray();
    }

    /// <summary>The package again with every entry stored, so that a mutation lands in the XML rather than the deflate stream.</summary>
    private static byte[] Stored(byte[] package)
    {
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        using (ZipArchive source = new(new MemoryStream(package)))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using Stream read = entry.Open();
                using Stream write = zip.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
                read.CopyTo(write);
            }
        }

        return output.ToArray();
    }
}
