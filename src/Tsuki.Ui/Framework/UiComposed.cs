using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>
/// A picture the menus build at run time from the player's own sheets (package K33): straight-alpha RGBA8, top-down. It lives in memory only: it is the
/// player's art rearranged, so it is never written to disk and never part of the repository.
/// </summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Rgba">Width x Height x 4 bytes.</param>
public sealed record UiComposedPicture(int Width, int Height, ReadOnlyMemory<byte> Rgba);

/// <summary>The ids of the composed pictures (<see cref="TextureRef.ForUiComposed"/>, <c>A</c>).</summary>
public static class UiComposedId
{
    /// <summary>EXIT on the title in its normal look (white letters), a 296 x 72 cell like the other items (package K33).</summary>
    public const int TitleExit = 0;

    /// <summary>EXIT on the title under the cursor (cyan letters).</summary>
    public const int TitleExitCursor = 1;

    /// <summary>
    /// The first of the four grade emblems of the achievement notice (v0.1.9): <c>TrophyEmblemFirst + (int)grade</c>. They are not composed from the sheets and
    /// need no game data: the port draws them (<see cref="Tsuki.Data.Trophies.TrophyIconArt.Emblem"/>, in <see cref="UiAssets.Composed(int)"/> on first use); they
    /// are the same in every language.
    /// </summary>
    public const int TrophyEmblemFirst = 16;

    /// <summary>The id of the emblem of <paramref name="grade"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is not one of the four.</exception>
    public static int TrophyEmblem(Tsuki.Engine.Trophies.TrophyGrade grade) =>
        (uint)grade < 4 ? TrophyEmblemFirst + (int)grade : throw new ArgumentOutOfRangeException(nameof(grade), grade, "not a trophy grade");

    /// <summary>True for the ids of the four emblems.</summary>
    public static bool IsTrophyEmblem(int id) => id is >= TrophyEmblemFirst and < TrophyEmblemFirst + 4;

    /// <summary>
    /// The first of the 30 icons of the achievements (v0.1.9): <c>TrophyIconFirst + trophy id</c>. They are the host's: it cuts an icon from the player's own
    /// pictures when the achievement is earned and hands it to <see cref="UiAssets.SetTrophyIcon"/>; they are the same in every language.
    /// </summary>
    public const int TrophyIconFirst = 32;

    /// <summary>The id of the icon of trophy <paramref name="id"/> (0..29).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not a trophy's.</exception>
    public static int TrophyIcon(int id) =>
        (uint)id < Tsuki.Engine.Trophies.TrophyTable.Count
            ? TrophyIconFirst + id
            : throw new ArgumentOutOfRangeException(nameof(id), id, "not a trophy id");

    /// <summary>True for the ids of the 30 icons.</summary>
    public static bool IsTrophyIcon(int id) => id is >= TrophyIconFirst and < TrophyIconFirst + Tsuki.Engine.Trophies.TrophyTable.Count;
}

/// <summary>Helpers for the composed pictures.</summary>
public static class UiComposed
{
    /// <summary>
    /// The displayed picture as one RGBA image: each tile's <see cref="PictureTile.Source"/> window is copied to its displayed position; empty tiles stay
    /// transparent (the same assembly the texture providers do for a whole-sheet quad).
    /// </summary>
    public static byte[] Assemble(Picture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        byte[] rgba = new byte[picture.Width * picture.Height * 4];
        foreach (PictureTile t in picture.Tiles)
        {
            if (t.Flags == TileFlags.Empty || t.Rgba.IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<byte> src = t.Rgba.Span;
            for (int row = 0; row < t.Source.Height; row++)
            {
                int dy = t.Y + row;
                if ((uint)dy >= (uint)picture.Height)
                {
                    continue;
                }

                int w = Math.Min(t.Source.Width, picture.Width - t.X);
                if (w <= 0)
                {
                    continue;
                }

                src.Slice((((t.Source.Y + row) * t.StoredWidth) + t.Source.X) * 4, w * 4).CopyTo(rgba.AsSpan(((dy * picture.Width) + t.X) * 4));
            }
        }

        return rgba;
    }
}
