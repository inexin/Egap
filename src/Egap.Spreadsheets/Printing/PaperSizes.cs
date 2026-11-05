namespace Egap.Spreadsheets.Printing;

/// <summary>The paper sizes of <see cref="PageSetup.PaperSize"/>: ECMA-376 Part 1, 18.18.43's codes, in points, portrait.</summary>
internal static class PaperSizes
{
    private const double Inch = 72;
    private const double Millimetre = 72 / 25.4;

    private static readonly Dictionary<int, (double Width, double Height)> Sizes = new()
    {
        [1] = In(8.5, 11),
        [2] = In(8.5, 11),
        [3] = In(11, 17),
        [4] = In(17, 11),
        [5] = In(8.5, 14),
        [6] = In(5.5, 8.5),
        [7] = In(7.25, 10.5),
        [8] = Mm(297, 420),
        [9] = Mm(210, 297),
        [10] = Mm(210, 297),
        [11] = Mm(148, 210),
        [12] = Mm(250, 353),
        [13] = Mm(176, 250),
        [14] = In(8.5, 13),
        [15] = Mm(215, 275),
        [16] = In(10, 14),
        [17] = In(11, 17),
        [18] = In(8.5, 11),
        [19] = In(3.875, 8.875),
        [20] = In(4.125, 9.5),
        [21] = In(4.5, 10.375),
        [22] = In(4.75, 11),
        [23] = In(5, 11.5),
        [24] = In(17, 22),
        [25] = In(22, 34),
        [26] = In(34, 44),
        [27] = Mm(110, 220),
        [28] = Mm(162, 229),
        [29] = Mm(324, 458),
        [30] = Mm(229, 324),
        [31] = Mm(114, 162),
        [32] = Mm(114, 229),
        [33] = Mm(250, 353),
        [34] = Mm(176, 250),
        [35] = Mm(176, 125),
        [36] = Mm(110, 230),
        [37] = In(3.875, 7.5),
        [38] = In(3.625, 6.5),
        [39] = In(14.875, 11),
        [40] = In(8.5, 12),
        [41] = In(8.5, 13),
        [42] = Mm(250, 353),
        [43] = Mm(200, 148),
        [44] = In(9, 11),
        [45] = In(10, 11),
        [46] = In(15, 11),
        [47] = Mm(220, 220),
        [50] = In(9.275, 12),
        [51] = In(9.275, 15),
        [52] = In(11.69, 18),
        [53] = Mm(236, 322),
        [54] = In(8.275, 11),
        [55] = Mm(210, 297),
        [56] = In(9.275, 12),
        [57] = Mm(227, 356),
        [58] = Mm(305, 487),
        [59] = In(8.5, 12.69),
        [60] = Mm(210, 330),
        [61] = Mm(148, 210),
        [62] = Mm(182, 257),
        [63] = Mm(322, 445),
        [64] = Mm(174, 235),
        [65] = Mm(201, 276),
        [66] = Mm(420, 594),
        [67] = Mm(297, 420),
        [68] = Mm(322, 445),
    };

    /// <summary>The paper of <paramref name="code"/>, portrait, in points; US Letter for a code not in the table.</summary>
    public static (double Width, double Height) Get(int? code) =>
        code is { } c && Sizes.TryGetValue(c, out (double Width, double Height) size) ? size : Sizes[1];

    private static (double, double) In(double width, double height) => (width * Inch, height * Inch);

    private static (double, double) Mm(double width, double height) => (width * Millimetre, height * Millimetre);
}
