using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

namespace Egap.Documents.DrawingML;

/// <summary>
/// DrawingML's 54 preset patterns (ECMA-376 Part 1, 20.1.10.51): each an 8 by 8 bitmap of foreground and background, one bit to
/// a 96th of an inch. The bitmaps were measured from PowerPoint's own drawing of each preset; PowerPoint lays them on the page,
/// starting at the page's corner and staying upright whatever the shape does.
/// </summary>
internal static class DrawingPatterns
{
    /// <summary>The width of one pattern bit, in points.</summary>
    public const float BitSize = 0.75f;

    private static readonly FrozenDictionary<string, byte[]> Bits = new Dictionary<string, byte[]>
    {
        ["pct5"] = [0b10000000, 0b00000000, 0b00000000, 0b00000000, 0b00001000, 0b00000000, 0b00000000, 0b00000000],
        ["pct10"] = [0b10000000, 0b00000000, 0b00001000, 0b00000000, 0b10000000, 0b00000000, 0b00001000, 0b00000000],
        ["pct20"] = [0b10001000, 0b00000000, 0b00100010, 0b00000000, 0b10001000, 0b00000000, 0b00100010, 0b00000000],
        ["pct25"] = [0b10001000, 0b00100010, 0b10001000, 0b00100010, 0b10001000, 0b00100010, 0b10001000, 0b00100010],
        ["pct30"] = [0b10101010, 0b01000100, 0b10101010, 0b00010001, 0b10101010, 0b01000100, 0b10101010, 0b00010001],
        ["pct40"] = [0b10101010, 0b01010101, 0b10101010, 0b01010001, 0b10101010, 0b01010101, 0b10101010, 0b00010101],
        ["pct50"] = [0b10101010, 0b01010101, 0b10101010, 0b01010101, 0b10101010, 0b01010101, 0b10101010, 0b01010101],
        ["pct60"] = [0b11101110, 0b01010101, 0b10111011, 0b01010101, 0b11101110, 0b01010101, 0b10111011, 0b01010101],
        ["pct70"] = [0b01110111, 0b11011101, 0b01110111, 0b11011101, 0b01110111, 0b11011101, 0b01110111, 0b11011101],
        ["pct75"] = [0b01110111, 0b11111111, 0b11011101, 0b11111111, 0b01110111, 0b11111111, 0b11011101, 0b11111111],
        ["pct80"] = [0b11101111, 0b11111111, 0b11111110, 0b11111111, 0b11101111, 0b11111111, 0b11111110, 0b11111111],
        ["pct90"] = [0b11111111, 0b11111111, 0b11111111, 0b11110111, 0b11111111, 0b11111111, 0b11111111, 0b01111111],
        ["horz"] = [0b11111111, 0b00000000, 0b00000000, 0b00000000, 0b00000000, 0b00000000, 0b00000000, 0b00000000],
        ["vert"] = [0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000],
        ["ltHorz"] = [0b11111111, 0b00000000, 0b00000000, 0b00000000, 0b11111111, 0b00000000, 0b00000000, 0b00000000],
        ["ltVert"] = [0b10001000, 0b10001000, 0b10001000, 0b10001000, 0b10001000, 0b10001000, 0b10001000, 0b10001000],
        ["dkHorz"] = [0b11111111, 0b11111111, 0b00000000, 0b00000000, 0b11111111, 0b11111111, 0b00000000, 0b00000000],
        ["dkVert"] = [0b11001100, 0b11001100, 0b11001100, 0b11001100, 0b11001100, 0b11001100, 0b11001100, 0b11001100],
        ["narHorz"] = [0b11111111, 0b00000000, 0b11111111, 0b00000000, 0b11111111, 0b00000000, 0b11111111, 0b00000000],
        ["narVert"] = [0b01010101, 0b01010101, 0b01010101, 0b01010101, 0b01010101, 0b01010101, 0b01010101, 0b01010101],
        ["dashHorz"] = [0b11110000, 0b00000000, 0b00000000, 0b00000000, 0b00001111, 0b00000000, 0b00000000, 0b00000000],
        ["dashVert"] = [0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b00001000, 0b00001000, 0b00001000, 0b00001000],
        ["cross"] = [0b11111111, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000],
        ["dnDiag"] = [0b10000000, 0b01000000, 0b00100000, 0b00010000, 0b00001000, 0b00000100, 0b00000010, 0b00000001],
        ["upDiag"] = [0b00000001, 0b00000010, 0b00000100, 0b00001000, 0b00010000, 0b00100000, 0b01000000, 0b10000000],
        ["ltDnDiag"] = [0b10001000, 0b01000100, 0b00100010, 0b00010001, 0b10001000, 0b01000100, 0b00100010, 0b00010001],
        ["ltUpDiag"] = [0b00010001, 0b00100010, 0b01000100, 0b10001000, 0b00010001, 0b00100010, 0b01000100, 0b10001000],
        ["dkDnDiag"] = [0b11001100, 0b01100110, 0b00110011, 0b10011001, 0b11001100, 0b01100110, 0b00110011, 0b10011001],
        ["dkUpDiag"] = [0b00110011, 0b01100110, 0b11001100, 0b10011001, 0b00110011, 0b01100110, 0b11001100, 0b10011001],
        ["wdDnDiag"] = [0b11000001, 0b11100000, 0b01110000, 0b00111000, 0b00011100, 0b00001110, 0b00000111, 0b10000011],
        ["wdUpDiag"] = [0b10000011, 0b00000111, 0b00001110, 0b00011100, 0b00111000, 0b01110000, 0b11100000, 0b11000001],
        ["dashDnDiag"] = [0b00000000, 0b00000000, 0b10001000, 0b01000100, 0b00100010, 0b00010001, 0b00000000, 0b00000000],
        ["dashUpDiag"] = [0b00000000, 0b00000000, 0b00010001, 0b00100010, 0b01000100, 0b10001000, 0b00000000, 0b00000000],
        ["diagCross"] = [0b10000001, 0b01000010, 0b00100100, 0b00011000, 0b00011000, 0b00100100, 0b01000010, 0b10000001],
        ["smCheck"] = [0b10011001, 0b01100110, 0b01100110, 0b10011001, 0b10011001, 0b01100110, 0b01100110, 0b10011001],
        ["lgCheck"] = [0b11110000, 0b11110000, 0b11110000, 0b11110000, 0b00001111, 0b00001111, 0b00001111, 0b00001111],
        ["smGrid"] = [0b11111111, 0b10001000, 0b10001000, 0b10001000, 0b11111111, 0b10001000, 0b10001000, 0b10001000],
        ["lgGrid"] = [0b11111111, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000, 0b10000000],
        ["dotGrid"] = [0b10101010, 0b00000000, 0b10000000, 0b00000000, 0b10000000, 0b00000000, 0b10000000, 0b00000000],
        ["smConfetti"] = [0b10000000, 0b00001000, 0b01000000, 0b00000010, 0b00010000, 0b00000001, 0b00100000, 0b00000100],
        ["lgConfetti"] = [0b10110001, 0b00110000, 0b00000011, 0b00011011, 0b11011000, 0b11000000, 0b00001100, 0b10001101],
        ["horzBrick"] = [0b11111111, 0b10000000, 0b10000000, 0b10000000, 0b11111111, 0b00001000, 0b00001000, 0b00001000],
        ["diagBrick"] = [0b00000001, 0b00000010, 0b00000100, 0b00001000, 0b00011000, 0b00100100, 0b01000010, 0b10000001],
        ["solidDmnd"] = [0b00010000, 0b00111000, 0b01111100, 0b11111110, 0b01111100, 0b00111000, 0b00010000, 0b00000000],
        ["openDmnd"] = [0b10000010, 0b01000100, 0b00101000, 0b00010000, 0b00101000, 0b01000100, 0b10000010, 0b00000001],
        ["dotDmnd"] = [0b10000000, 0b00000000, 0b00100010, 0b00000000, 0b00001000, 0b00000000, 0b00100010, 0b00000000],
        ["plaid"] = [0b10101010, 0b01010101, 0b10101010, 0b01010101, 0b11110000, 0b11110000, 0b11110000, 0b11110000],
        ["sphere"] = [0b01110111, 0b10001001, 0b10001111, 0b10001111, 0b01110111, 0b10011000, 0b11111000, 0b11111000],
        ["weave"] = [0b10001000, 0b01010100, 0b00100010, 0b01000101, 0b10001000, 0b00010100, 0b00100010, 0b01010001],
        ["divot"] = [0b00000000, 0b00010000, 0b00001000, 0b00010000, 0b00000000, 0b10000000, 0b00000001, 0b10000000],
        ["shingle"] = [0b00000011, 0b10000100, 0b01001000, 0b00110000, 0b00001100, 0b00000010, 0b00000001, 0b00000001],
        ["wave"] = [0b00000000, 0b00011000, 0b00100101, 0b11000000, 0b00000000, 0b00011000, 0b00100101, 0b11000000],
        ["trellis"] = [0b11111111, 0b01100110, 0b11111111, 0b10011001, 0b11111111, 0b01100110, 0b11111111, 0b10011001],
        ["zigZag"] = [0b10000001, 0b01000010, 0b00100100, 0b00011000, 0b10000001, 0b01000010, 0b00100100, 0b00011000],
    }.ToFrozenDictionary();

    private static readonly ConcurrentDictionary<(string Name, Rgba8 Ink, Rgba8 Paper), Raster<Rgba8>> Tiles = new();

    private static readonly SamplingOptions Nearest = new() { Filter = ResampleFilter.Nearest };

    /// <summary>Whether <paramref name="name"/> is one of the presets.</summary>
    public static bool Knows(string? name) => name is not null && Bits.ContainsKey(name);

    /// <summary>
    /// A paint that repeats preset <paramref name="name"/> in <paramref name="ink"/> on <paramref name="paper"/>, its bits laid out
    /// by <paramref name="patternToLocal"/>: from pattern bits to the canvas's current coordinates. An unknown name is 50 %.
    /// </summary>
    public static Paint Paint(string? name, Rgba8 ink, Rgba8 paper, Matrix3x2 patternToLocal)
    {
        string key = Knows(name) ? name! : "pct50";
        Raster<Rgba8> tile = Tiles.GetOrAdd((key, ink, paper), static k =>
        {
            byte[] rows = Bits[k.Name];
            Raster<Rgba8> raster = Raster.Create(8, 8, k.Paper);
            for (int y = 0; y < 8; y++)
            {
                Span<Rgba8> row = raster.Row(y);
                for (int x = 0; x < 8; x++)
                {
                    if ((rows[y] & (0x80 >> x)) != 0)
                    {
                        row[x] = k.Ink;
                    }
                }
            }

            return raster;
        });
        return new ImagePaint(tile, TileMode.Repeat, TileMode.Repeat, Nearest) { LocalMatrix = patternToLocal };
    }
}
