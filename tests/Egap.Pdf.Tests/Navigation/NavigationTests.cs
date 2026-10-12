using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Navigation;

/// <summary>Destinations, named destinations and actions (12.3.2, 12.6): Phase 2 task 6.</summary>
public sealed class NavigationTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>Two pages, 3 and 4, with the catalog entries and extra objects given.</summary>
    private static byte[] File(string catalog = "", params (int Number, string Body)[] extra)
    {
        FileBuilder file = new FileBuilder()
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R {catalog} >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>")
            .Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        foreach ((int number, string body) in extra)
        {
            file.Object(number, body);
        }

        return file.StartXref(file.XrefTable($"/Size {Math.Max(5, extra.Select(e => e.Number + 1).DefaultIfEmpty(0).Max())} /Root 1 0 R")).ToArray();
    }

    private static PdfDestination? Parse(string array)
    {
        using PdfDocument pdf = PdfDocument.Open(File("", (5, array)));
        return PdfDestination.FromArray((PdfArray)pdf.Objects.Get(5));
    }

    [Theory]
    [InlineData("[3 0 R /XYZ 10 20 1.5]", PdfDestinationFit.Xyz, 10.0, 20.0, null, null, 1.5)]
    [InlineData("[3 0 R /XYZ null null 0]", PdfDestinationFit.Xyz, null, null, null, null, 0.0)]
    [InlineData("[3 0 R /Fit]", PdfDestinationFit.Fit, null, null, null, null, null)]
    [InlineData("[3 0 R /FitH 700]", PdfDestinationFit.FitH, null, 700.0, null, null, null)]
    [InlineData("[3 0 R /FitV 5]", PdfDestinationFit.FitV, 5.0, null, null, null, null)]
    [InlineData("[3 0 R /FitR 1 2 3 4]", PdfDestinationFit.FitR, 1.0, 4.0, 3.0, 2.0, null)]
    [InlineData("[3 0 R /FitB]", PdfDestinationFit.FitB, null, null, null, null, null)]
    [InlineData("[3 0 R /FitBH null]", PdfDestinationFit.FitBH, null, null, null, null, null)]
    [InlineData("[3 0 R /FitBV 7]", PdfDestinationFit.FitBV, 7.0, null, null, null, null)]
    [InlineData("[3 0 R /XYZ]", PdfDestinationFit.Xyz, null, null, null, null, null)]
    [InlineData("[3 0 R]", PdfDestinationFit.Fit, null, null, null, null, null)]
    [InlineData("[3 0 R /FitR 1 3 4]", PdfDestinationFit.FitR, 1.0, null, 4.0, 3.0, null)]
    public void ReadsEveryFitType(string array, PdfDestinationFit fit, double? left, double? top, double? right, double? bottom, double? zoom)
    {
        PdfDestination destination = Parse(array)!;

        Assert.Equal((fit, left, top, right, bottom, zoom), (destination.Fit, destination.Left, destination.Top, destination.Right, destination.Bottom, destination.Zoom));
        Assert.Equal(3, destination.PageReference!.ObjectNumber);
        Assert.Equal(destination, PdfDestination.FromArray(destination.ToArray()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[/Fit]")]
    [InlineData("[3 0 R /Zoom]")]
    [InlineData("[-1 /Fit]")]
    public void RejectsWhatIsNotADestination(string array)
    {
        Assert.Null(Parse(array));
    }

    [Fact]
    public void FindsTheDestinationsPage()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        Assert.Equal(1, PdfDestination.ToPage(pdf.Pages[1]).GetPageIndex(pdf));
        Assert.Equal(1, PdfDestination.ToPageIndex(1).GetPageIndex(pdf));
        Assert.Equal(-1, PdfDestination.ToPageIndex(5).GetPageIndex(pdf));
        Assert.Equal("[4 0 R /XYZ 0 50 null]", (PdfDestination.ToPage(pdf.Pages[1], PdfDestinationFit.Xyz) with { Left = 0, Top = 50 }).ToArray().ToString());
    }

    [Fact]
    public void FindsNamedDestinationsThroughBothLookups()
    {
        using PdfDocument pdf = PdfDocument.Open(
            File(
                "/Names << /Dests 5 0 R >> /Dests << /old [4 0 R /Fit] /both [4 0 R /FitB] >>",
                (5, "<< /Names [(both) [3 0 R /Fit] (chapter) << /D [4 0 R /FitH 90] >> (indirect) 6 0 R] >>"),
                (6, "[3 0 R /XYZ 1 2 3]")),
            Strict);
        PdfNamedDestinations named = pdf.NamedDestinations;

        Assert.True(named.TryGet("chapter", out PdfDestination chapter));
        Assert.Equal((PdfDestinationFit.FitH, 90.0, 1), (chapter.Fit, chapter.Top, chapter.GetPageIndex(pdf)));
        Assert.True(named.TryGet("old", out PdfDestination old));
        Assert.Equal(1, old.GetPageIndex(pdf));
        Assert.True(named.TryGet("both", out PdfDestination both));
        Assert.Equal(PdfDestinationFit.Fit, both.Fit);
        Assert.True(named.TryGet("indirect", out PdfDestination indirect));
        Assert.Equal(3.0, indirect.Zoom);
        Assert.False(named.TryGet("missing", out _));
        Assert.Equal(["both", "chapter", "indirect", "old"], named.GetNames());
    }

    [Fact]
    public void SetsAndRemovesNamedDestinations()
    {
        using PdfDocument pdf = PdfDocument.Open(File("/Dests << /old [4 0 R /Fit] >>"));

        pdf.NamedDestinations.Set("intro", PdfDestination.ToPage(pdf.Pages[0]));
        pdf.NamedDestinations.Set("old", PdfDestination.ToPage(pdf.Pages[0], PdfDestinationFit.FitB));
        pdf.NamedDestinations.Set("Ωmega", PdfDestination.ToPage(pdf.Pages[1]));
        Assert.True(pdf.NamedDestinations.Remove("intro"));
        Assert.False(pdf.NamedDestinations.Remove("intro"));

        using MemoryStream output = new();
        pdf.Save(output);
        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        Assert.Equal(["old", "Ωmega"], reopened.NamedDestinations.GetNames());
        Assert.True(reopened.NamedDestinations.TryGet("old", out PdfDestination old));
        Assert.Equal((PdfDestinationFit.FitB, 0), (old.Fit, old.GetPageIndex(reopened)));
        Assert.Empty(reopened.Catalog.Get<PdfDictionary>("Dests")!);
    }

    [Fact]
    public void TypesActionsBySubtype()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        (string Body, Type Type)[] cases =
        [
            ("<< /S /GoTo /D [3 0 R /Fit] >>", typeof(PdfGoToAction)),
            ("<< /S /GoToR /F (other.pdf) /D [2 /Fit] /NewWindow true >>", typeof(PdfGoToRemoteAction)),
            ("<< /S /GoToE /T << /R /C /N (a.pdf) >> /D (x) >>", typeof(PdfGoToEmbeddedAction)),
            ("<< /S /URI /URI (https://example.com/) >>", typeof(PdfUriAction)),
            ("<< /S /Named /N /NextPage >>", typeof(PdfNamedAction)),
            ("<< /S /Launch /F << /Type /Filespec /F (a.txt) /UF (b.txt) >> >>", typeof(PdfLaunchAction)),
            ("<< /S /SubmitForm /F << /FS /URL /F (https://example.com/submit) >> /Flags 4 >>", typeof(PdfSubmitFormAction)),
            ("<< /S /ResetForm /Fields [(name)] /Flags 1 >>", typeof(PdfResetFormAction)),
            ("<< /S /Hide /T (field) /H false >>", typeof(PdfHideAction)),
            ("<< /S /SetOCGState /State [/OFF 5 0 R] >>", typeof(PdfSetOcgStateAction)),
            ("<< /S /JavaScript /JS (app.alert(1)) >>", typeof(PdfAction)),
            ("<< /S /Sound >>", typeof(PdfAction)),
            ("<< >>", typeof(PdfAction)),
        ];

        foreach ((string body, Type type) in cases)
        {
            using PdfDocument file = PdfDocument.Open(File("", (5, body)));
            Assert.IsType(type, PdfAction.FromDictionary((PdfDictionary)file.Objects.Get(5)));
        }
    }

    [Fact]
    public void ReadsTypedActionProperties()
    {
        using PdfDocument pdf = PdfDocument.Open(File(
            "/Dests << /here [4 0 R /FitV 3] >>",
            (5, "<< /S /GoTo /D /here >>"),
            (6, "<< /S /GoToR /F (other.pdf) /D [2 /Fit] /NewWindow true >>"),
            (7, "<< /S /URI /URI <68747470733A2F2FC3A9> /IsMap true >>"),
            (8, "<< /S /Launch /F << /Type /Filespec /F (a.txt) /UF (b.txt) >> >>"),
            (9, "<< /S /Hide /T [(a) (b)] /H false >>")));
        PdfAction Action(int number) => PdfAction.FromDictionary((PdfDictionary)pdf.Objects.Get(number));

        PdfGoToAction goTo = (PdfGoToAction)Action(5);
        Assert.Equal(("here", (PdfDestination?)null), (goTo.DestinationName, goTo.Destination));
        Assert.Equal(1, goTo.ResolveDestination(pdf)!.GetPageIndex(pdf));

        PdfGoToRemoteAction remote = (PdfGoToRemoteAction)Action(6);
        Assert.Equal(("other.pdf", 2, true), (remote.FileName, remote.Destination!.PageIndex, remote.NewWindow));
        Assert.Equal(("https://é", true), (((PdfUriAction)Action(7)).Uri, ((PdfUriAction)Action(7)).IsMap));
        Assert.Equal("b.txt", ((PdfLaunchAction)Action(8)).FileName);
        Assert.False(((PdfHideAction)Action(9)).Hide);
        Assert.Equal("Hide action", Action(9).ToString());
    }

    [Fact]
    public void FollowsNextChainsAndCatchesLoops()
    {
        // 5 runs 6 and 7; 6 runs 8, which loops back to 5; 7 runs 6 again.
        using PdfDocument pdf = PdfDocument.Open(File(
            "",
            (5, "<< /S /Named /N /FirstPage /Next [6 0 R 7 0 R] >>"),
            (6, "<< /S /Named /N /NextPage /Next 8 0 R >>"),
            (7, "<< /S /JavaScript /JS (x) /Next 6 0 R >>"),
            (8, "<< /S /URI /URI (a) /Next 5 0 R >>")));
        PdfAction first = PdfAction.FromDictionary((PdfDictionary)pdf.Objects.Get(5));

        Assert.Equal(2, first.Next.Count);
        Assert.Equal(["Named", "Named", "URI", "JavaScript"], first.GetSequence().Select(a => a.ActionType));
    }

    [Fact]
    public void CreatesActionsAndChainsThem()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfGoToAction goTo = new(PdfDestination.ToPage(pdf.Pages[1]));
        PdfUriAction uri = new("https://example.com/");
        PdfNamedAction named = new("LastPage");

        goTo.SetNext(pdf, uri, named);
        pdf.Catalog["OpenAction"] = pdf.Objects.Add(goTo.Dictionary);
        Assert.Throws<ArgumentException>(() => goTo.SetNext(pdf, uri));

        using MemoryStream output = new();
        pdf.Save(output);
        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        PdfAction open = PdfAction.FromDictionary(reopened.Catalog.Get<PdfDictionary>("OpenAction")!);
        Assert.Equal(1, ((PdfGoToAction)open).Destination!.GetPageIndex(reopened));
        Assert.Equal(["GoTo", "URI", "Named"], open.GetSequence().Select(a => a.ActionType));
        Assert.Equal("https://example.com/", ((PdfUriAction)open.Next[0]).Uri);
        Assert.Equal("x", new PdfGoToRemoteAction("a.pdf", "x").DestinationName);
        Assert.Equal("a.pdf", new PdfLaunchAction("a.pdf").FileName);
    }

    [Fact]
    public void ViewsAreEqualWhenTheyWrapTheSameDictionary()
    {
        using PdfDocument pdf = PdfDocument.Open(File("", (5, "<< /S /JavaScript /JS (1) >>")));
        PdfDictionary dictionary = (PdfDictionary)pdf.Objects.Get(5);

        Assert.Equal(PdfAction.FromDictionary(dictionary), PdfAction.FromDictionary(dictionary));
        Assert.NotEqual(PdfAction.FromDictionary(dictionary), new PdfNamedAction("x"));
    }
}
