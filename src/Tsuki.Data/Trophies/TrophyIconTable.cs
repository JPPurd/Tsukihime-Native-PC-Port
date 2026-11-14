// Where the icon of each achievement is cut from (v0.1.9): a picture of the player's own game folder and a square in it. The port ships this table and no art:
// the icon is cut while the program runs (TrophyIconArt) and is never written to disk.
//
// The square is in the pixels of the picture as ImageMzp.Assemble builds it (the displayed picture: tile borders dropped, tiles side by side). One picture
// stacks a band per language, 1270 rows apart; its square lies in the second band (y from 1270), the one the approved proposal was cut from.
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;

namespace Tsuki.Data.Trophies;

/// <summary>What kind of picture an icon is cut from.</summary>
public enum TrophyIconKind
{
    /// <summary>A picture found by its name in the name tables of the image archives.</summary>
    Picture = 0,

    /// <summary>A child of a UI sheet.</summary>
    UiSheet = 1,
}

/// <summary>One row: where the icon of a trophy is cut from.</summary>
/// <param name="Kind">The kind of source.</param>
/// <param name="Name">Picture: the picture's name in the game's name tables, without the extension ("IMG1691"); UiSheet: unused ("").</param>
/// <param name="Sheet">UiSheet: the set, <c>(int)UiSet</c>; Picture: 0.</param>
/// <param name="Child">UiSheet: the child of the set; Picture: 0.</param>
/// <param name="X">Left edge of the square, in the assembled picture's pixels.</param>
/// <param name="Y">Top edge of the square.</param>
/// <param name="Side">Side of the square.</param>
/// <param name="Ground">0 = none; else 0xRRGGBB drawn under a picture with transparency.</param>
public readonly record struct TrophyIconSource(TrophyIconKind Kind, string Name, int Sheet, int Child, int X, int Y, int Side, uint Ground);

/// <summary>The 30 rows, by trophy id.</summary>
public static class TrophyIconTable
{
    /// <summary>Raised when a row changes, so a cache of icons made from an older table can be told apart.</summary>
    public const int Version = 1;

    private static readonly TrophyIconSource[] Rows =
    [
        new(TrophyIconKind.UiSheet, "", (int)UiSet.TitleParts, 9, 1070, 94, 520, 0),                // 0, platinum
        new(TrophyIconKind.Picture, "IMG1779", 0, 0, 337, 605, 1012, 0),                            // 1, bronze
        new(TrophyIconKind.Picture, "IMG0818", 0, 0, 690, 200, 1280, 0),                            // 2, bronze
        new(TrophyIconKind.Picture, "IMG0826", 0, 0, 688, 50, 1563, 0),                             // 3, bronze
        new(TrophyIconKind.Picture, "IMG1093", 0, 0, 649, 197, 834, 0),                             // 4, bronze
        new(TrophyIconKind.Picture, "IMG0964", 0, 0, 465, 318, 860, 0),                             // 5, silver
        new(TrophyIconKind.Picture, "IMG0845", 0, 0, 702, 132, 965, 0),                             // 6, silver
        new(TrophyIconKind.Picture, "IMG1511", 0, 0, 1420, 150, 1180, 0),                           // 7, silver
        new(TrophyIconKind.Picture, "IMG0977", 0, 0, 2021, 221, 821, 0),                            // 8, silver
        new(TrophyIconKind.Picture, "IMG1743", 0, 0, 700, 150, 1100, 0),                            // 9, silver
        new(TrophyIconKind.Picture, "IMG0986", 0, 0, 760, 190, 1000, 0),                            // 10, silver
        new(TrophyIconKind.Picture, "IMG1013", 0, 0, 690, 200, 900, 0),                             // 11, silver
        new(TrophyIconKind.Picture, "IMG1015", 0, 0, 570, 213, 1053, 0),                            // 12, silver
        new(TrophyIconKind.Picture, "IMG1035", 0, 0, 702, 0, 1200, 0),                              // 13, silver
        new(TrophyIconKind.Picture, "IMG1086", 0, 0, 637, 130, 920, 0),                             // 14, gold
        new(TrophyIconKind.Picture, "IMG3947", 0, 0, 300, 90, 820, 0),                              // 15, silver
        new(TrophyIconKind.Picture, "IMG2360", 0, 0, 900, 0, 540, 0),                               // 16, silver
        new(TrophyIconKind.Picture, "IMG1143", 0, 0, 1185, 184, 1527, 0),                           // 17, silver
        new(TrophyIconKind.Picture, "IMG1117", 0, 0, 3080, 290, 1560, 0),                           // 18, silver
        new(TrophyIconKind.Picture, "IMG1870", 0, 0, 560, 90, 1000, 0),                             // 19, silver
        new(TrophyIconKind.Picture, "IMG3511", 0, 0, 640, 560, 950, 0),                             // 20, silver
        new(TrophyIconKind.Picture, "IMG1006", 0, 0, 1820, 280, 1155, 0),                           // 21, silver
        new(TrophyIconKind.Picture, "IMG1295", 0, 0, 1825, 351, 1334, 0),                           // 22, silver
        new(TrophyIconKind.Picture, "IMG3638", 0, 0, 527, 140, 1053, 0),                            // 23, silver
        new(TrophyIconKind.Picture, "IMG1188", 0, 0, 470, 0, 900, 0),                               // 24, silver
        new(TrophyIconKind.Picture, "IMG2347", 0, 0, 955, 56, 749, 0),                              // 25, silver
        new(TrophyIconKind.Picture, "IMG2595", 0, 0, 439, 88, 877, 0),                              // 26, gold
        new(TrophyIconKind.Picture, "IMG1495", 0, 0, 300, 40, 820, 0),                              // 27, gold
        new(TrophyIconKind.Picture, "SIE_A_90_03_N00", 0, 0, 1000, 0, 1440, 0xF8B838),              // 28, silver
        new(TrophyIconKind.Picture, "IMG4026", 0, 0, 0, 1270, 760, 0),                              // 29, gold
    ];

    /// <summary>The row of trophy <paramref name="trophyId"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not 0..29.</exception>
    public static TrophyIconSource Source(int trophyId) =>
        (uint)trophyId < (uint)Rows.Length
            ? Rows[trophyId]
            : throw new ArgumentOutOfRangeException(nameof(trophyId), trophyId, $"trophy ids are 0..{TrophyTable.Count - 1}");
}
