using System.Collections.Concurrent;
using Tsuki.Engine.Contracts;

namespace Tsuki.Ui;

/// <summary>A sheet of the UI: child <see cref="Child"/> of set <see cref="Set"/> in the current language (docs/formats/archives_ui_movies.md 5.2).</summary>
/// <param name="Set">The UI set (an allpac3 MRG).</param>
/// <param name="Child">The child picture in the set.</param>
public readonly record struct UiSheet(UiSet Set, int Child);

/// <summary>
/// A picture of an image archive (the caution screens in allpac3, or an entry of the game's <c>parts</c> archive): <see cref="Archive"/> is an image archive id
/// of docs/formats/archives_ui_movies.md 2.1 (0 allpac0 .. 5 allpac4), or <see cref="UiPictureArchive.Parts"/>.
/// </summary>
/// <param name="Archive">The archive id.</param>
/// <param name="Entry">The entry in the archive.</param>
public readonly record struct UiPicture(int Archive, int Entry);

/// <summary>Archive ids beyond the six image archives that the UI names in a <see cref="UiPicture"/> (package K5a; additive to the contract's 0..5).</summary>
public static class UiPictureArchive
{
    /// <summary>The <c>parts</c> archive (parts.hed / parts.mrg: 24 pictures of the boot notices; docs/formats/archives_ui_movies.md 6).</summary>
    public const int Parts = 6;
}

/// <summary>
/// The pictures of the <c>parts</c> archive, which <see cref="IGameData"/> does not serve (it knows the six image archives only). The host passes an
/// implementation to <see cref="UiRoot"/> when it has one; without it the boot notice draws its icons from DISP_PARTS and leaves the text out.
/// </summary>
public interface IUiPictureSource
{
    /// <summary>Picture <paramref name="index"/> of parts.mrg (0..23), decoded; null when there is none.</summary>
    Picture? Parts(int index);
}

/// <summary>One tile of a sheet: where the displayed part lands in the sheet and where it sits in the stored (apron-bordered) texture.</summary>
/// <param name="Index">Tile number.</param>
/// <param name="X">Left edge of the displayed part in sheet texels.</param>
/// <param name="Y">Top edge of the displayed part in sheet texels.</param>
/// <param name="W">Width of the displayed part.</param>
/// <param name="H">Height of the displayed part.</param>
/// <param name="SourceX">Left edge of the displayed part inside the stored texture.</param>
/// <param name="SourceY">Top edge of the displayed part inside the stored texture.</param>
/// <param name="StoredWidth">Width of the stored texture.</param>
/// <param name="StoredHeight">Height of the stored texture.</param>
public readonly record struct SheetTile(int Index, int X, int Y, int W, int H, int SourceX, int SourceY, int StoredWidth, int StoredHeight);

/// <summary>The size and tile layout of a sheet; no pixels (the executor fetches those through <see cref="IGameData.UiSheet"/>).</summary>
/// <param name="Width">Displayed width in texels.</param>
/// <param name="Height">Displayed height in texels.</param>
/// <param name="Tiles">The tiles that hold pixels.</param>
public sealed record SheetGeometry(int Width, int Height, IReadOnlyList<SheetTile> Tiles)
{
    /// <summary>The geometry of a decoded picture.</summary>
    public static SheetGeometry FromPicture(Picture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var tiles = new List<SheetTile>(picture.Tiles.Length);
        foreach (PictureTile t in picture.Tiles)
        {
            if (t.Flags == TileFlags.Empty || t.Source.Width <= 0 || t.Source.Height <= 0)
            {
                continue;
            }

            tiles.Add(new SheetTile(t.Index, t.X, t.Y, t.Source.Width, t.Source.Height, t.Source.X, t.Source.Y, t.StoredWidth, t.StoredHeight));
        }

        return new SheetGeometry(picture.Width, picture.Height, tiles);
    }
}

/// <summary>
/// The sheets and tables the screens draw from, for one UI language: <see cref="IGameData.UiSheet"/> geometry cached per sheet, the
/// <see cref="IUiTables"/>, and the language. Changing the language drops the cache (knife <c>lang_frame</c>: the UI atlases reload).
/// </summary>
public sealed class UiAssets
{
    private readonly ConcurrentDictionary<(UiSet, Language, int), SheetGeometry?> _geometry = new();
    private readonly ConcurrentDictionary<UiPicture, SheetGeometry?> _pictures = new();

    /// <summary>Creates the assets over a game data source.</summary>
    /// <param name="data">The game data.</param>
    /// <param name="language">The UI language.</param>
    /// <param name="parts">The parts archive, if the host has it.</param>
    public UiAssets(IGameData data, Language language, IUiPictureSource? parts = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Parts = parts;
        Tables = data.Eboot.Ui;
        Gallery = GalleryTablesOf(data);
        AvailableLanguages = AvailableLanguagesOf(data);
        Language = language;
    }

    /// <summary>The selectable languages the data carries (package K25): the config screen offers these only. Every one for a source that does not say (a test rig).</summary>
    public IReadOnlyList<Language> AvailableLanguages { get; }

    private static IReadOnlyList<Language> AvailableLanguagesOf(IGameData data)
    {
        try
        {
            IReadOnlyList<Language>? languages = data.AvailableLanguages;
            return languages is { Count: > 0 } ? languages : [Language.Ja, Language.En, Language.Zc, Language.Zt];
        }
        catch (NotSupportedException)
        {
            return [Language.Ja, Language.En, Language.Zc, Language.Zt];
        }
    }

    /// <summary>The gallery and flowchart tables (package K5b), or null when the data source holds none (the golden slice): the gallery and the flowchart are then not offered.</summary>
    public IUiGalleryTables? Gallery { get; }

    private static IUiGalleryTables? GalleryTablesOf(IGameData data)
    {
        try
        {
            return data.Eboot.Gallery;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The game data.</summary>
    public IGameData Data { get; }

    /// <summary>The parts archive, or null.</summary>
    public IUiPictureSource? Parts { get; }

    /// <summary>The eboot tables of the menus.</summary>
    public IUiTables Tables { get; }

    /// <summary>The UI language (0 ja, 1 en, 2 zc, 3 zt).</summary>
    public Language Language { get; private set; }

    /// <summary>Sheets that a draw asked for and the data did not have (a test asserts this stays empty on real data).</summary>
    public ConcurrentDictionary<UiSheet, int> Missing { get; } = new();

    /// <summary>Switches the language; the sheet cache is dropped.</summary>
    public void SetLanguage(Language language)
    {
        if (language != Language)
        {
            Language = language;
            _geometry.Clear();
            _composed.Clear();
        }
    }

    private readonly ConcurrentDictionary<(int Id, Language Language), UiComposedPicture?> _composed = new();

    /// <summary>
    /// The picture the menus compose at run time from the player's own sheets (package K33; <see cref="UiComposedId"/>), in the current language, or null when
    /// the sheets are not the art the composer expects (the screen then draws its fallback). Built on the first call and kept; the language switch drops it.
    /// </summary>
    public UiComposedPicture? Composed(int id) => Composed(id, Language);

    /// <summary>The composed picture <paramref name="id"/> of <paramref name="language"/> (the texture providers resolve a key by its own language).</summary>
    public UiComposedPicture? Composed(int id, Language language)
    {
        if (UiComposedId.IsTrophyEmblem(id))
        {
            // the port's own pictures, the same in every language; answered before the composer, which would run again on every call for an id it does not store
            return TrophyEmblem((Tsuki.Engine.Trophies.TrophyGrade)(id - UiComposedId.TrophyEmblemFirst));
        }

        if (UiComposedId.IsTrophyIcon(id))
        {
            return Volatile.Read(ref _icons[id - UiComposedId.TrophyIconFirst]);
        }

        if (_composed.TryGetValue((id, language), out UiComposedPicture? pic))
        {
            return pic;
        }

        BuildComposed(language);
        return _composed.GetValueOrDefault((id, language));
    }

    // the four emblems, made once in a process (on first use, on whichever thread asks first) and shared by every UI: they depend on nothing
    private static readonly UiComposedPicture?[] Emblems = new UiComposedPicture?[4];

    /// <summary>
    /// The emblem of a trophy grade for the achievement notice (v0.1.9; <see cref="UiComposedId.TrophyEmblem"/>): the trophy cup the port draws on the icons' tile
    /// (<see cref="Tsuki.Data.Trophies.TrophyIconArt.Emblem"/>) at the icons' size, <see cref="UiTrophyNotice.IconSize"/>. No game data and no file is read: it is
    /// drawn the first time it is asked for, in a millisecond or two, and kept. It stands in for an icon that cannot be made.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is not one of the four.</exception>
    public static UiComposedPicture TrophyEmblem(Tsuki.Engine.Trophies.TrophyGrade grade)
    {
        int slot = UiComposedId.TrophyEmblem(grade) - UiComposedId.TrophyEmblemFirst;
        if (Volatile.Read(ref Emblems[slot]) is { } made)
        {
            return made;
        }

        const int Size = UiTrophyNotice.IconSize;
        var picture = new UiComposedPicture(Size, Size, Tsuki.Data.Trophies.TrophyIconArt.Emblem(grade, Size));
        return Interlocked.CompareExchange(ref Emblems[slot], picture, null) ?? picture;
    }

    private readonly UiComposedPicture?[] _icons = new UiComposedPicture?[Tsuki.Engine.Trophies.TrophyTable.Count];
    private readonly bool[] _iconSettled = new bool[Tsuki.Engine.Trophies.TrophyTable.Count];

    /// <summary>
    /// Stores the icon of trophy <paramref name="id"/> (0..29) for the achievement notice (v0.1.9; <see cref="UiComposedId.TrophyIcon"/>): the host cuts it from
    /// the player's own pictures, on any thread. Null says "settled, there is none": the notice then draws the grade's emblem. Either way <see cref="TrophyIconSettled"/>
    /// is true afterwards. A later call replaces the picture. The language switch keeps it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not a trophy's.</exception>
    public void SetTrophyIcon(int id, UiComposedPicture? picture)
    {
        if ((uint)id >= (uint)_icons.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "not a trophy id");
        }

        // the picture first: whoever sees "settled" finds the picture that settled it
        Volatile.Write(ref _icons[id], picture);
        Volatile.Write(ref _iconSettled[id], true);
    }

    /// <summary>True once <see cref="SetTrophyIcon"/> was called for trophy <paramref name="id"/>: its icon is there, or the host said there is none. False for an id that is no trophy's.</summary>
    public bool TrophyIconSettled(int id) => (uint)id < (uint)_iconSettled.Length && Volatile.Read(ref _iconSettled[id]);

    private void BuildComposed(Language language)
    {
        (UiComposedPicture Normal, UiComposedPicture Cursor)? exit = null;
        try
        {
            exit = Screens.TitleExitArt.Compose(Data.UiSheet(UiSheets.TitleMenu.Set, language, UiSheets.TitleMenu.Child));
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or KeyNotFoundException or IndexOutOfRangeException or FileNotFoundException or IOException or NotSupportedException or InvalidOperationException or Tsuki.Data.Images.ImageFormatException)
        {
            // no sheet, or not an image: the title keeps its glyph-font EXIT
        }

        _composed[(UiComposedId.TitleExit, language)] = exit?.Normal;
        _composed[(UiComposedId.TitleExitCursor, language)] = exit?.Cursor;
    }

    /// <summary>The geometry of <paramref name="sheet"/>, or null when the set has no such child.</summary>
    public SheetGeometry? Geometry(UiSheet sheet)
    {
        SheetGeometry? g = _geometry.GetOrAdd((sheet.Set, Language, sheet.Child), key =>
        {
            try
            {
                return SheetGeometry.FromPicture(Data.UiSheetLayout(key.Item1, key.Item2, key.Item3));
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or KeyNotFoundException or IndexOutOfRangeException or FileNotFoundException)
            {
                return null;
            }
        });
        if (g is null)
        {
            Missing.AddOrUpdate(sheet, 1, (_, n) => n + 1);
        }

        return g;
    }

    /// <summary>The geometry of a picture of an image archive, or null when it does not exist (the picture is not language-dependent).</summary>
    public SheetGeometry? Geometry(UiPicture picture)
    {
        SheetGeometry? g = _pictures.GetOrAdd(picture, key =>
        {
            try
            {
                Picture? pic = key.Archive == UiPictureArchive.Parts ? Parts?.Parts(key.Entry) : Data.Picture(key.Archive, key.Entry);
                return pic is null ? null : SheetGeometry.FromPicture(pic);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or KeyNotFoundException or IndexOutOfRangeException or FileNotFoundException)
            {
                return null;
            }
        });
        if (g is null)
        {
            MissingPictures.AddOrUpdate(picture, 1, (_, n) => n + 1);
        }

        return g;
    }

    /// <summary>Pictures that a draw asked for and could not be found.</summary>
    public ConcurrentDictionary<UiPicture, int> MissingPictures { get; } = new();


    /// <summary>The <c>ui_text</c> string <paramref name="id"/> in the current language.</summary>
    public string Text(int id) => Tables.Text(Language, id);

    /// <summary>
    /// The <c>ui_text</c> id of knife's SYSMES message number <paramref name="knifeId"/> (the overseas table has one message more from 217 on;
    /// docs/research/knife_tables.md 2).
    /// </summary>
    public static int Sysmes(int knifeId) => knifeId >= 217 ? knifeId + 1 : knifeId;
}
