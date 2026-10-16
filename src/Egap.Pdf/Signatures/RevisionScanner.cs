using Egap.Pdf.IO;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Signatures;

/// <summary>
/// Finds a file's revisions (Phase 5 design, 5.1): the original file and each incremental update after it (ISO 32000-2,
/// 7.5.6). A revision ends at the <c>%%EOF</c> after the <c>startxref</c> that names its cross-reference section. Sections no
/// <c>startxref</c> names, such as the main section of a linearized file, belong to the newer revision that leads to them
/// through <c>/Prev</c>, so a linearized file is one revision.
/// </summary>
internal static class RevisionScanner
{
    /// <summary>One revision: where its newest cross-reference section is, where its <c>%%EOF</c> ends, and its length with the end-of-line after it.</summary>
    internal readonly record struct Found(long XrefOffset, long EofEnd, long Length);

    public static List<Found> Scan(PdfSource source, XrefTable xref, bool rebuilt)
    {
        List<XrefSection> chain = [.. xref.Sections.Where(s => !s.IsHybrid)];
        if (rebuilt || chain.Count == 0)
        {
            long eof = SourceSearch.LastIndexOf(source, "%%EOF"u8, source.Length);
            long end = eof < 0 ? source.Length : eof + 5;
            return [new Found(chain.Count > 0 ? chain[0].Offset : 0, end, EndOfLine(source, end))];
        }

        // Each revision's startxref normally follows its section closely, so it is looked for from there; only a section no later
        // startxref names makes the whole file be searched, once.
        Dictionary<long, List<long>>? named = null;
        List<Found> found = [];
        Found? current = null;
        foreach (XrefSection section in chain)
        {
            // The first startxref after the section that names it ends the revision; an update repeats no offset, so one is enough.
            long eofEnd = NamingStartXref(source, section.Offset);
            if (eofEnd < 0 && (named ??= StartXrefs(source)).TryGetValue(section.Offset, out List<long>? ends))
            {
                foreach (long end in ends)
                {
                    if (end > section.Offset)
                    {
                        eofEnd = end;
                        break;
                    }
                }
            }

            if (eofEnd >= 0)
            {
                current = new Found(section.Offset, eofEnd, EndOfLine(source, eofEnd));
                found.Add(current.Value);
            }
            else if (current is null)
            {
                // The newest section is named by no startxref: the file was damaged after it, so its revision is the whole file.
                current = new Found(section.Offset, source.Length, source.Length);
                found.Add(current.Value);
            }
        }

        // Oldest first, and a later revision always ends after an earlier one.
        found.Sort((a, b) => a.Length.CompareTo(b.Length));
        return [.. found.DistinctBy(f => f.Length)];
    }

    /// <summary>
    /// The end of the <c>%%EOF</c> after the first <c>startxref</c> past <paramref name="offset"/> that names it (the end of the
    /// file when none follows), or -1 when none does.
    /// </summary>
    private static long NamingStartXref(PdfSource source, long offset)
    {
        WarningLog log = new(false);
        for (long at = SourceSearch.IndexOf(source, "startxref"u8, offset); at >= 0; at = SourceSearch.IndexOf(source, "startxref"u8, at + 9))
        {
            Lexer lexer = new(new SourceReader(source, at + 9), log);
            Token token = lexer.Next();
            if (token.Kind == TokenKind.Integer && token.Integer == offset)
            {
                long eof = SourceSearch.IndexOf(source, "%%EOF"u8, lexer.Position);
                return eof < 0 ? source.Length : eof + 5;
            }
        }

        return -1;
    }

    /// <summary>
    /// Every <c>startxref</c> in the file: the offset it names, mapped to where the <c>%%EOF</c> after it ends (the end of the
    /// file when none follows), in file order.
    /// </summary>
    private static Dictionary<long, List<long>> StartXrefs(PdfSource source)
    {
        // A startxref inside a stream's data may be followed by anything; what the lexer finds there is no repair to report.
        WarningLog log = new(false);
        Dictionary<long, List<long>> named = [];
        for (long at = SourceSearch.IndexOf(source, "startxref"u8, 0); at >= 0; at = SourceSearch.IndexOf(source, "startxref"u8, at + 9))
        {
            Lexer lexer = new(new SourceReader(source, at + 9), log);
            Token token = lexer.Next();
            if (token.Kind != TokenKind.Integer || token.Integer < 0)
            {
                continue;
            }

            long eof = SourceSearch.IndexOf(source, "%%EOF"u8, lexer.Position);
            long end = eof < 0 ? source.Length : eof + 5;
            if (!named.TryGetValue(token.Integer, out List<long>? ends))
            {
                ends = [];
                named[token.Integer] = ends;
            }

            ends.Add(end);
        }

        return named;
    }

    /// <summary>The position after one end-of-line (CR LF, LF or CR) at <paramref name="at"/>, or <paramref name="at"/> itself.</summary>
    private static long EndOfLine(PdfSource source, long at)
    {
        byte[] next = source.ReadBytes(at, 2);
        return next switch
        {
            [(byte)'\r', (byte)'\n'] => at + 2,
            [(byte)'\r', ..] or [(byte)'\n', ..] => at + 1,
            _ => at,
        };
    }
}
