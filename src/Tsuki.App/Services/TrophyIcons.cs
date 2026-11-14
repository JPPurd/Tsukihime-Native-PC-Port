// The icons of the achievement notice (v0.1.9): the square of one of the player's own pictures that stands for an achievement (Tsuki.Data's TrophyIconTable),
// cut, reduced and framed at the size the notice draws it at, so that it is drawn pixel for pixel in the notice. One is made when its achievement is earned, on a
// worker (AppRuntime.TrophyIconMaker), and lives in memory only. A picture that is missing or cannot be read costs only the icon: the notice shows the grade's
// emblem (UiAssets.TrophyEmblem, drawn by the port).
using Tsuki.Data;
using Tsuki.Data.Trophies;
using Tsuki.Engine.Trophies;
using Tsuki.Ui;

namespace Tsuki.App.Services;

/// <summary>Makes the icons of the achievement notice from the player's game folder.</summary>
public static class TrophyIcons
{
    /// <summary>
    /// The icon of achievement <paramref name="id"/> for its notice, <see cref="UiTrophyNotice.IconSize"/> pixels a side, or null when this game folder cannot
    /// give it (the picture is missing or not readable, the id is no achievement's, the source is closed). Never throws for what the game folder holds; safe on a
    /// worker thread, and beside the source's disposal.
    /// </summary>
    public static UiComposedPicture? Notice(GameFolderSource source, int id)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (id is < 0 or >= TrophyTable.Count)
        {
            return null;
        }

        const int Size = UiTrophyNotice.IconSize;
        return TrophyIconArt.Render(source, id, Size) is { } rgba ? new UiComposedPicture(Size, Size, rgba) : null;
    }
}
