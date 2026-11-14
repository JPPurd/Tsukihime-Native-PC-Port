// The pictures of the rows of the Achievements page of the Windows launcher (v0.1.9, second part), without any WPF type so that the tests compile it: which
// picture each row's square shows, and the making of the icons.
//
// An earned row shows its icon, a square cut from one of the player's own pictures (Achievements.Icon); a locked row shows the locked tile of its grade and never a
// game picture, so the list does not spoil later chapters. While an icon is being made the row shows the blank tile of its grade; only when the icon cannot be
// made (no game folder, a missing picture) does the row show the emblem of its grade, a trophy cup drawn by the port's own code in the tiles' frame
// (Achievements.EmblemIcon). The emblems are the summary card's pictures of the four grades too. No picture of the PS4's trophy file is shown.
//
// The icons are made one after the other on a worker thread (a picture is decoded for each: tens of milliseconds), never on the caller's. Each is turned into the
// toolkit's bitmap there too, once, and kept: a row asks for the same object as often as it likes. After Forget (the launcher's Check again, the game's end) every
// icon is made again; the row keeps the picture it shows until then, and keeps it for good when the new one has the same pixels.
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>What the picture square of a row of the Achievements page shows.</summary>
public enum AchievementArt
{
    /// <summary>Nothing yet: the page was never shown, so no picture was made for it.</summary>
    None,

    /// <summary>Not earned: the locked tile of its grade (a padlock drawn by code). Never a game picture.</summary>
    Locked,

    /// <summary>Earned, and its icon is being made: the blank tile of its grade.</summary>
    Blank,

    /// <summary>Earned: its icon, cut from the player's own pictures.</summary>
    Icon,

    /// <summary>Earned, and the icon cannot be made (no game folder, the picture is missing): the emblem of its grade, drawn by code. Never a game picture.</summary>
    Emblem,
}

/// <summary>
/// The pictures of the rows. Safe from any thread; <see cref="Changed"/> is raised where <see cref="Request"/> was called from when that thread has a
/// synchronisation context (the window's thread has one), else on a worker thread.
/// </summary>
/// <typeparam name="TPicture">The toolkit's bitmap (the tests use a stand-in).</typeparam>
public sealed class AchievementPictures<TPicture>
    where TPicture : class
{
    private static readonly TrophyGrade[] Grades = Enum.GetValues<TrophyGrade>();
    private static readonly StringComparison FolderNames = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Func<AchievementIcon, TPicture> _toPicture;
    private readonly Func<string?, int, AchievementIcon?> _icon;
    private readonly object _gate = new();
    private readonly Slot[] _slots = new Slot[TrophyTable.Count];
    private readonly int[] _asked = new int[TrophyTable.Count];
    private readonly TPicture?[] _locked = new TPicture?[Grades.Length];
    private readonly TPicture?[] _blank = new TPicture?[Grades.Length];
    private readonly TPicture?[] _emblem = new TPicture?[Grades.Length];
    private string? _folder;
    private int _generation;
    private Task _work = Task.CompletedTask;

    /// <summary>Creates the pictures of a page; nothing is made yet.</summary>
    /// <param name="toPicture">Turns the pixels of an icon or a tile into the toolkit's bitmap. Called on a worker thread for the icons.</param>
    /// <param name="icon">Where the icons come from: <see cref="Achievements.Icon"/> unless a test counts the requests.</param>
    public AchievementPictures(Func<AchievementIcon, TPicture> toPicture, Func<string?, int, AchievementIcon?>? icon = null)
    {
        ArgumentNullException.ThrowIfNull(toPicture);
        _toPicture = toPicture;
        _icon = icon ?? Achievements.Icon;
    }

    /// <summary>The icon of this trophy id was made, or was found impossible to make: <see cref="Of"/> has a new answer for its row.</summary>
    public event Action<int>? Changed;

    /// <summary>What one id's row has: its bitmap, the pixels it was made from, and how far the making is.</summary>
    private struct Slot
    {
        /// <summary>The icon; null when there is none (yet). Kept over <see cref="Forget"/> until the icon is made again.</summary>
        public TPicture? Picture;

        /// <summary>The pixels <see cref="Picture"/> was made from: an icon made again with the same pixels keeps the bitmap.</summary>
        public byte[]? Pixels;

        /// <summary>True once the icon was made or was found impossible to make.</summary>
        public bool Settled;

        /// <summary>True while the worker has it on its list.</summary>
        public bool Asking;
    }

    /// <summary>How many icons were asked for since the launcher started (the tests and the self-test count with it).</summary>
    public int Requests
    {
        get
        {
            int sum = 0;
            for (int id = 0; id < _asked.Length; id++)
            {
                sum += Volatile.Read(ref _asked[id]);
            }

            return sum;
        }
    }

    /// <summary>How often the icon of one trophy id was asked for; 0 for an id that is no trophy's.</summary>
    public int RequestsOf(int trophyId) => (uint)trophyId < (uint)_asked.Length ? Volatile.Read(ref _asked[trophyId]) : 0;

    /// <summary>The making of the icons asked for so far: it ends when the last of them is settled. Never faults.</summary>
    public Task Work
    {
        get
        {
            lock (_gate)
            {
                return _work;
            }
        }
    }

    /// <summary>
    /// What the square of a row shows now, and the bitmap of it: the locked tile, the blank tile, the icon or the emblem. The same bitmap object is returned for as
    /// long as the answer stays the same; the emblem of a grade is the very object <see cref="Emblem"/> hands out.
    /// </summary>
    public (AchievementArt Art, TPicture? Picture) Of(AchievementRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            if (!row.Earned)
            {
                return (AchievementArt.Locked, Tile(_locked, row.Grade, Achievements.LockedIcon));
            }

            if ((uint)row.Id < (uint)_slots.Length)
            {
                Slot slot = _slots[row.Id];
                if (slot.Picture is not null)
                {
                    return (AchievementArt.Icon, slot.Picture);
                }

                if (slot.Settled)
                {
                    return (AchievementArt.Emblem, Tile(_emblem, row.Grade, Achievements.EmblemIcon));
                }
            }

            return (AchievementArt.Blank, Tile(_blank, row.Grade, Achievements.BlankIcon));
        }
    }

    /// <summary>
    /// The emblem of a grade: a trophy cup drawn by code in the tiles' frame, the summary card's picture of the grade (and of an earned row whose icon cannot be
    /// made). Made once and kept, like the tiles; needs no game folder.
    /// </summary>
    public TPicture Emblem(TrophyGrade grade)
    {
        lock (_gate)
        {
            return Tile(_emblem, grade, Achievements.EmblemIcon);
        }
    }

    /// <summary>
    /// Asks for the icons of the earned rows among <paramref name="rows"/> that are neither made nor on their way; a locked row is never asked for. Returns at
    /// once: the icons are made one after the other on a worker thread and <see cref="Changed"/> tells of each. Without a game folder nothing is asked for and
    /// every earned row has the emblem at once. Another game folder than the last call's drops what was made from that one.
    /// </summary>
    /// <param name="gameFolder">The game folder, or null.</param>
    /// <param name="rows">The achievements as last read.</param>
    /// <returns><see cref="Work"/>.</returns>
    public Task Request(string? gameFolder, IEnumerable<AchievementRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string? folder = string.IsNullOrWhiteSpace(gameFolder) ? null : gameFolder;
        lock (_gate)
        {
            if (!string.Equals(folder, _folder, FolderNames))
            {
                // another folder's pictures are not this folder's: nothing of them stays on show
                _generation++;
                Array.Clear(_slots);
                _folder = folder;
            }

            List<(int Id, byte[]? Pixels, TPicture? Picture)>? ask = null;
            foreach (AchievementRow row in rows)
            {
                if (!row.Earned || (uint)row.Id >= (uint)_slots.Length || _slots[row.Id].Settled || _slots[row.Id].Asking)
                {
                    continue;
                }

                if (folder is null)
                {
                    _slots[row.Id].Settled = true;
                    continue;
                }

                _slots[row.Id].Asking = true;
                (ask ??= []).Add((row.Id, _slots[row.Id].Pixels, _slots[row.Id].Picture));
            }

            if (ask is not null)
            {
                _work = MakeAsync(_work, folder!, _generation, ask);
            }

            return _work;
        }
    }

    /// <summary>
    /// Every icon is to be made again at the next <see cref="Request"/> (the launcher's Check again, the game's end: the game folder may have been repaired). A row
    /// keeps the picture it shows until its icon was made again. The tiles need no game folder and stay.
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            _generation++;
            for (int id = 0; id < _slots.Length; id++)
            {
                _slots[id].Settled = false;
                _slots[id].Asking = false;
            }
        }
    }

    /// <summary>
    /// Draws the twelve tiles (locked, blank and the emblem, four grades) now, on the calling thread, so that neither <see cref="Of"/> nor <see cref="Emblem"/> has
    /// to. No game folder is needed.
    /// </summary>
    public void MakeTiles()
    {
        foreach (TrophyGrade grade in Grades)
        {
            lock (_gate)
            {
                _ = Tile(_locked, grade, Achievements.LockedIcon);
                _ = Tile(_blank, grade, Achievements.BlankIcon);
                _ = Tile(_emblem, grade, Achievements.EmblemIcon);
            }
        }
    }

    /// <summary>Ends when no icon is being made any more, those asked for meanwhile included (the off-screen render waits with it).</summary>
    public async Task WhenIdle()
    {
        while (true)
        {
            Task work = Work;
            await work;
            if (ReferenceEquals(work, Work))
            {
                return;
            }
        }
    }

    // under _gate: a tile of a grade, turned into the toolkit's bitmap the first time it is wanted
    private TPicture Tile(TPicture?[] tiles, TrophyGrade grade, Func<TrophyGrade, AchievementIcon> draw) => tiles[(int)grade] ??= _toPicture(draw(grade));

    private async Task MakeAsync(Task before, string folder, int generation, List<(int Id, byte[]? Pixels, TPicture? Picture)> ask)
    {
        // one worker, one icon at a time: the player's PC may be busy with the game
        await before;
        foreach ((int id, byte[]? keptPixels, TPicture? kept) in ask)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;         // forgotten meanwhile: whoever forgot asks again
                }
            }

            (byte[]? pixels, TPicture? picture) = await Task.Run(() => Make(folder, id, keptPixels, kept));
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _slots[id] = new Slot { Picture = picture, Pixels = pixels, Settled = true };
            }

            Changed?.Invoke(id);
        }
    }

    // on the worker thread
    private (byte[]? Pixels, TPicture? Picture) Make(string folder, int id, byte[]? keptPixels, TPicture? kept)
    {
        _ = Interlocked.Increment(ref _asked[id]);
        try
        {
            if (_icon(folder, id) is not { } icon)
            {
                return (null, null);
            }

            // made again after Forget: the same pixels keep the bitmap the row already shows
            if (kept is not null && keptPixels is not null && (ReferenceEquals(icon.Rgba, keptPixels) || icon.Rgba.AsSpan().SequenceEqual(keptPixels)))
            {
                return (icon.Rgba, kept);
            }

            return (icon.Rgba, _toPicture(icon));
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // whatever went wrong costs only the icon: the row shows the emblem
            return (null, null);
        }
    }
}
