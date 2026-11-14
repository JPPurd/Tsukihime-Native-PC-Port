// The Achievements page (v0.1.9): what both launchers show of the 30 achievements (the PS4 trophies). Only plain data: no WPF or Avalonia types.
//
// An achievement is earned when the game recorded it in trophies.ini, OR when save.dat alone already satisfies its condition (TrophyRules.Holding over the system
// block's scene_done / scene_seen bits). The second half makes the page right before the game has run with this version, and after a PS4 save was imported.
// The game writes the file, and records what the save implies at its next start. The launcher's reading never writes; only a restore or an import of a save
// does, while no game runs (KeepBeforeSaveChange, UniteBackupRecord): what the outgoing save implied is recorded first, so the save change takes nothing away
// that the page showed, and a restore adds what the backup's record holds.
//
// Names: the 27 chapter achievements carry the game's own English chapter titles, read from the player's game folder; the other three, and all of them while no
// game folder is set, carry the port's own words (LauncherTexts). A grade is shown by its emblem, a cup drawn by the port's own code (EmblemIcon); the PS4's
// trophy file (sce_sys/trophy) is not read at all.
//
// What comes from the game folder (names, the trophy scene table) is read once per folder and kept; save.dat is read again only when its
// size or time changes, or while it is so fresh that its size and time cannot yet tell (SettleTime). So Read is cheap enough for every change of
// AchievementStamp, which is what the pages poll.
//
// Icons (second part of v0.1.9): an earned achievement shows a square cut from one of the player's own pictures (TrophyIconArt), a locked one a drawn tile
// with a padlock and never a game picture. The icons of one game folder are made on demand and kept in memory; nothing is written to disk. The game folder
// is opened for the first icon and closed again a few seconds after the last, so a page that asks for thirty opens it once and the launcher does not hold
// the player's files open while it idles.
using Tsuki.Data;
using Tsuki.Data.IO;
using Tsuki.Data.Trophies;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Trophies;

namespace Tsuki.Launcher.Core;

/// <summary>One achievement as the page shows it.</summary>
/// <param name="Id">The trophy id, 0..29 (0 is the platinum).</param>
/// <param name="Grade">Its grade.</param>
/// <param name="Name">Its name, English.</param>
/// <param name="Detail">How it is earned, one line.</param>
/// <param name="Earned">True when it is earned.</param>
/// <param name="EarnedUtc">When it was earned (UTC); null when it is locked, or earned at a time nobody recorded.</param>
public sealed record AchievementRow(int Id, TrophyGrade Grade, string Name, string Detail, bool Earned, DateTime? EarnedUtc)
{
    /// <summary>"Platinum", "Gold", "Silver" or "Bronze".</summary>
    public string GradeLabel => LauncherTexts.AchievementGrade(Grade);

    /// <summary>"Locked", or the local date and time it was earned ("12 Nov 2026, 14:03"), or "Earned" when no date is known.</summary>
    public string StateLabel => !Earned ? LauncherTexts.AchievementLocked
        : EarnedUtc is { } time ? LauncherTexts.AchievementEarnedAt(time)
        : LauncherTexts.AchievementEarned;
}

/// <summary>The achievements of a profile. Two summaries with the same rows are equal, so a page can tell whether a new reading changed anything.</summary>
/// <param name="Rows">The 30 achievements by id, the platinum first.</param>
public sealed record AchievementSummary(IReadOnlyList<AchievementRow> Rows)
{
    /// <summary>How many there are (30).</summary>
    public int Total => Rows.Count;

    /// <summary>How many are earned.</summary>
    public int Earned => Rows.Count(r => r.Earned);

    /// <summary>The earned share in whole percent, rounded down (100 only when all are earned).</summary>
    public int Percent => Total == 0 ? 0 : Earned * 100 / Total;

    /// <summary>How many of a grade are earned.</summary>
    public int EarnedOf(TrophyGrade grade) => Rows.Count(r => r.Earned && r.Grade == grade);

    /// <summary>How many of a grade there are (1 platinum, 4 gold, 21 silver, 4 bronze).</summary>
    public int TotalOf(TrophyGrade grade) => Rows.Count(r => r.Grade == grade);

    /// <summary>"12 of 30 earned".</summary>
    public string CountLine => LauncherTexts.AchievementCount(Earned, Total);

    /// <summary>True when there is a trophies file and it could not be read: the rows then hold only what the save shows (<see cref="LauncherTexts.AchievementsDamagedNote"/>).</summary>
    public bool Damaged { get; init; }

    /// <summary>
    /// True when this reading may be short of what the files hold for a passing reason: trophies.ini is there and could not be opened (another program held it),
    /// or save.dat was busy and the save's part is the last reading's (or none). Such a file can come free without a change of its size or time, so a page reads
    /// again while this is true. Not for a trophies.ini that is not a record (<see cref="Damaged"/> alone): reading it again gives the same.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>True when the chapter names were read from the game folder; false when the rows carry the stand-in names (<see cref="LauncherTexts.AchievementsNoGameNote"/>).</summary>
    public bool GameNames { get; init; }

    /// <inheritdoc/>
    public bool Equals(AchievementSummary? other) =>
        other is not null && Damaged == other.Damaged && Incomplete == other.Incomplete && GameNames == other.GameNames && Rows.SequenceEqual(other.Rows);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Damaged);
        hash.Add(Incomplete);
        hash.Add(GameNames);
        foreach (AchievementRow row in Rows)
        {
            hash.Add(row);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Whether the two files the page is built from are there, their sizes and their last-write times: when it changes, read again.</summary>
/// <param name="TrophiesExists">trophies.ini is there.</param>
/// <param name="TrophiesLength">Its size.</param>
/// <param name="TrophiesWriteUtc">Its last-write time (UTC).</param>
/// <param name="SaveExists">save.dat is there.</param>
/// <param name="SaveLength">Its size.</param>
/// <param name="SaveWriteUtc">Its last-write time (UTC).</param>
public readonly record struct AchievementStamp(bool TrophiesExists, long TrophiesLength, DateTime TrophiesWriteUtc, bool SaveExists, long SaveLength, DateTime SaveWriteUtc);

/// <summary>A picture of the Achievements page: an icon, or the tile that stands in for one. The same object is handed to every caller: do not change the pixels.</summary>
/// <param name="Size">Its side in pixels.</param>
/// <param name="Rgba">Size x Size x 4 bytes, straight-alpha RGBA8, rows top-down (premultiply it for a toolkit that wants that).</param>
public sealed record AchievementIcon(int Size, byte[] Rgba);

/// <summary>
/// Reads the achievements of a profile for the launcher pages. Every member is safe to call from any thread and never throws for what it finds on disk. None
/// writes but <see cref="KeepBeforeSaveChange"/> and <see cref="UniteBackupRecord"/>, which a restore or an import of a save calls.
/// </summary>
public static partial class Achievements
{
    // a folder that could not be read is tried again after this long (the player may be fixing it), not on every reading
    private const long RetryMilliseconds = 5000;
    private const int MaxFolders = 8;

    // SaveFile's own limit: a larger file is not a save
    private const long MaxSaveBytes = 256L * 1024 * 1024;

    // A file's size and time tell a changed file from an unchanged one only once the time is in the past by more than the resolution file times have (two seconds
    // on FAT, a tick of the clock elsewhere): a rewrite of the same size inside it leaves both as they were. So a save read sooner than this after its
    // last-write time is used for that reading and not kept: the next reading looks at the file again.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    private static readonly object FolderGate = new();
    private static readonly object SaveGate = new();
    private static readonly Dictionary<string, (FolderData? Data, long Tick)> Folders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static SaveWords? _save;

    /// <summary>What a game folder gives the page; read once per folder.</summary>
    /// <param name="Names">The English chapter title of each trophy id; null where the game has none (0, 28, 29) or the eboot could not be read.</param>
    /// <param name="Scenes">The eboot's trophy scene table; empty when the eboot could not be read.</param>
    private sealed record FolderData(string?[] Names, IReadOnlyList<int> Scenes);

    /// <summary>The G words of a save.dat as last read, with the stamp they were read at.</summary>
    /// <param name="Path">The file.</param>
    /// <param name="Length">Its size then.</param>
    /// <param name="WriteUtc">Its last-write time then.</param>
    /// <param name="Words">The words; null when it was not a save.</param>
    /// <param name="Settled">True when the file was read later than <see cref="SettleTime"/> after its last-write time, so the same size and time mean the same file.</param>
    private sealed record SaveWords(string Path, long Length, DateTime WriteUtc, ushort[]? Words, bool Settled);

    /// <summary>
    /// The 30 achievements: earned when trophies.ini says so or when save.dat alone satisfies the condition; the platinum when the other 29 are earned either way.
    /// Dates come from trophies.ini only. Names are the game's English chapter titles when <paramref name="gameFolder"/> can be read, the port's own otherwise.
    /// Without a readable game folder the save can only show the two lesson achievements (the chapter scenes are an eboot table).
    /// </summary>
    /// <param name="paths">The profile.</param>
    /// <param name="gameFolder">The game folder, or null.</param>
    public static AchievementSummary Read(LauncherPaths paths, string? gameFolder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        TrophyLog log = TrophyLog.ReadFile(paths.TrophiesFile, out TrophyFileState state);
        FolderData? folder = Folder(gameFolder);
        var earned = new bool[TrophyTable.Count];
        foreach (int id in log.Ids)
        {
            earned[id] = true;
        }

        ushort[]? g = ReadSaveWords(paths.SaveFile, out bool saveBusy);
        foreach (int id in SaveImplies(g, folder))
        {
            earned[id] = true;
        }

        // the two sources together can complete the set that neither completes alone
        earned[TrophyTable.Platinum] |= TrophyRules.PlatinumHolds(id => earned[id]);

        var rows = new AchievementRow[TrophyTable.Count];
        bool gameNames = false;
        for (int id = 0; id < rows.Length; id++)
        {
            string? name = folder?.Names[id];
            gameNames |= name is not null;
            rows[id] = new AchievementRow(
                id, TrophyTable.Grade(id), name ?? LauncherTexts.AchievementFallbackName(id), LauncherTexts.AchievementDetail(id), earned[id], earned[id] ? log.EarnedUtc(id) : null);
        }

        return new AchievementSummary(Array.AsReadOnly(rows))
        {
            Damaged = state != TrophyFileState.Read,
            Incomplete = state == TrophyFileState.Unreadable || saveBusy,
            GameNames = gameNames,
        };
    }

    /// <summary>
    /// Records in trophies.ini, without a date, every achievement the save about to be replaced implies and the record lacks, and the platinum when the record
    /// then holds the other 29: what the page showed from that save alone is kept when a restore or an import replaces it. Without a readable game folder only
    /// the two lesson achievements can be told from a save (the chapter scenes are an eboot table), which is also all the page showed then.
    /// </summary>
    /// <remarks>
    /// <see cref="SaveTools.Restore(LauncherPaths, BackupInfo, DateTime, string)"/> and <see cref="SaveTools.ImportPs4(LauncherPaths, string, DateTime, string)"/>
    /// call it after their backup and before save.dat is replaced, and only while no game runs: the game writes the record while it runs. A record that is there
    /// and could not be opened is left alone (what it holds is not known); one that is not a record is written over (the backup just made keeps a copy). The
    /// file is written only when something was added, and a failed write is not an error: the save change goes on, and the game's next start records what the
    /// save then on disk implies.
    /// </remarks>
    /// <param name="paths">The profile.</param>
    /// <param name="gameFolder">The game folder, or null.</param>
    /// <returns>The ids that were added and written, ascending; empty when nothing was added, or the record could not be read or written.</returns>
    public static IReadOnlyList<int> KeepBeforeSaveChange(LauncherPaths paths, string? gameFolder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        TrophyLog log = TrophyLog.ReadFile(paths.TrophiesFile, out TrophyFileState state);
        if (state == TrophyFileState.Unreadable)
        {
            return [];
        }

        var added = new List<int>();
        foreach (int id in SaveImplies(ReadSaveWords(paths.SaveFile, out _), Folder(gameFolder)))
        {
            if (log.Add(id, null))
            {
                added.Add(id);
            }
        }

        // the record and the save together can complete the set that neither completes alone
        if (TrophyRules.PlatinumHolds(log.IsEarned) && log.Add(TrophyTable.Platinum, null))
        {
            added.Insert(0, TrophyTable.Platinum);
        }

        return added.Count > 0 && TryWrite(log, paths.TrophiesFile) ? added : [];
    }

    /// <summary>
    /// Adds what the trophies.ini of a backup folder holds to the profile's record, after a restore put that backup's save in place: an achievement either holds
    /// is kept, a date beats "earlier", and of two dates the earlier one is kept (the platinum is added, without a date, when the two together hold the other
    /// 29). The record is written only when that changes it. A backup's record that is missing, empty, unreadable or not a record adds nothing, and a profile's
    /// record that is there and could not be opened is left alone. Called only while no game runs; a failed write is not an error.
    /// </summary>
    /// <param name="paths">The profile.</param>
    /// <param name="backupFolder">The backup folder.</param>
    /// <returns>True when the record was written.</returns>
    public static bool UniteBackupRecord(LauncherPaths paths, string backupFolder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFolder);
        TrophyLog theirs = TrophyLog.ReadFile(Path.Combine(backupFolder, TrophyLog.FileName), out TrophyFileState theirState);
        if (theirState != TrophyFileState.Read || theirs.Count == 0)
        {
            return false;
        }

        TrophyLog ours = TrophyLog.ReadFile(paths.TrophiesFile, out TrophyFileState ourState);
        if (ourState == TrophyFileState.Unreadable)
        {
            return false;
        }

        var union = new TrophyLog();
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            if (ours.IsEarned(id) || theirs.IsEarned(id))
            {
                union.Add(id, Earlier(ours.EarnedUtc(id), theirs.EarnedUtc(id)));
            }
        }

        if (TrophyRules.PlatinumHolds(union.IsEarned))
        {
            union.Add(TrophyTable.Platinum, null);
        }

        return union.Format() != ours.Format() && TryWrite(union, paths.TrophiesFile);
    }

    // a date beats "earlier" (null), and of two dates the earlier one is when it was first earned
    private static DateTime? Earlier(DateTime? a, DateTime? b) => a is { } x && b is { } y ? (x <= y ? x : y) : a ?? b;

    private static bool TryWrite(TrophyLog log, string path)
    {
        try
        {
            log.Write(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // the achievements a save's G words satisfy (none without a save); the chapters only with the game folder's scene table
    private static IReadOnlyList<int> SaveImplies(ushort[]? g, FolderData? folder) =>
        g is null ? [] : new TrophyRules(folder?.Scenes ?? []).Holding(n => Bit(g, SystemData.SceneDoneWord, n), n => Bit(g, SystemData.SceneSeenWord, n));

    /// <summary>
    /// The stamp of trophies.ini and save.dat: two file lookups, nothing is opened. For the pages' 2 s poll (LauncherProfile.Read is not for polling: it writes a
    /// probe file on every call).
    /// </summary>
    public static AchievementStamp Stamp(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        (bool trophies, long trophiesLength, DateTime trophiesTime) = FileStamp(paths.TrophiesFile);
        (bool save, long saveLength, DateTime saveTime) = FileStamp(paths.SaveFile);
        return new AchievementStamp(trophies, trophiesLength, trophiesTime, save, saveLength, saveTime);
    }

    /// <summary>
    /// Forgets what was read from game folders and from save.dat, the icons included, so the next reading looks again (the launcher's Recheck, or a game folder
    /// that was repaired in place). The game folder the icons were cut from is closed.
    /// </summary>
    public static void Forget()
    {
        lock (FolderGate)
        {
            Folders.Clear();
        }

        lock (SaveGate)
        {
            _save = null;
        }

        lock (IconGate)
        {
            DropIconFolder();
        }
    }

    // -- the icons ----------------------------------------------------------------------------------------------------

    // the game folder is closed this long after the last icon was made
    private const int IconIdleMilliseconds = 3000;

    private static readonly StringComparison FolderNames = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly object IconGate = new();
    private static readonly object TileGate = new();
    private static readonly AchievementIcon?[] LockedIcons = new AchievementIcon?[4];
    private static readonly AchievementIcon?[] BlankIcons = new AchievementIcon?[4];
    private static readonly AchievementIcon?[] EmblemIcons = new AchievementIcon?[4];
    private static readonly Timer IconIdle = new(_ => CloseIdleIconFolder(), null, Timeout.Infinite, Timeout.Infinite);
    private static IconFolder? _icons;
    private static int _iconFolderOpenings;

    /// <summary>How often a game folder was opened (or tried) for icons since the process started; the tests count with it.</summary>
    internal static int IconFolderOpenings => Volatile.Read(ref _iconFolderOpenings);

    /// <summary>True while a game folder is held open for icons (or is being opened); for the tests.</summary>
    internal static bool IconFolderIsOpen
    {
        get
        {
            lock (IconGate)
            {
                return _icons?.Source is not null;
            }
        }
    }

    /// <summary>The icons of one game folder, and the opened folder while they are being made. Every member is read and written under <see cref="IconGate"/>.</summary>
    private sealed class IconFolder(string key)
    {
        public string Key { get; } = key;

        public AchievementIcon?[] Icons { get; } = new AchievementIcon?[TrophyTable.Count];

        /// <summary>True once the icon of an id was made, or was found impossible to make.</summary>
        public bool[] Settled { get; } = new bool[TrophyTable.Count];

        /// <summary>
        /// The game folder, opened by the first thread that needs it, outside the gate: the others wait on this and nobody waits in the gate for the disk.
        /// Null while it is closed; its value is null when the folder could not be opened.
        /// </summary>
        public Lazy<GameFolderSource?>? Source { get; set; }

        /// <summary>Threads that are opening or reading <see cref="Source"/> now: it is not closed under them.</summary>
        public int Users { get; set; }

        public long LastUse { get; set; }

        /// <summary>When the folder could not be opened; it is tried again after <see cref="RetryMilliseconds"/>.</summary>
        public long? Failed { get; set; }

        /// <summary>True once the cache moved on to another folder or was forgotten: the last render closes the source.</summary>
        public bool Dropped { get; set; }
    }

    /// <summary>
    /// The icon of an earned achievement at <see cref="TrophyIconArt.LauncherSize"/> (240 x 240), cut from the player's own pictures; null when it cannot be made
    /// (no game folder, not a game, the picture is missing or damaged, or the id is no trophy's). Made once per process and game folder and kept: later calls
    /// return the same object. The first call for an id decodes a picture (tens of milliseconds; the first of all also opens the game folder): call it off the UI
    /// thread. A call for an icon that is already made returns at once, also while another thread is opening the folder or decoding. Safe from several threads
    /// at once; never throws for what it finds on disk; never writes. A folder that could not be opened is looked at again after five seconds.
    /// </summary>
    /// <param name="gameFolder">The game folder, or null.</param>
    /// <param name="trophyId">The trophy id, 0..29.</param>
    public static AchievementIcon? Icon(string? gameFolder, int trophyId)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) || (uint)trophyId >= TrophyTable.Count)
        {
            return null;
        }

        string key = gameFolder.Trim();
        IconFolder folder;
        Lazy<GameFolderSource?> opening;
        lock (IconGate)
        {
            if (_icons is null || !string.Equals(_icons.Key, key, FolderNames))
            {
                DropIconFolder();
                _icons = new IconFolder(key);
            }

            folder = _icons;
            if (folder.Settled[trophyId])
            {
                return folder.Icons[trophyId];
            }

            if (folder.Failed is { } failed && Environment.TickCount64 - failed < RetryMilliseconds)
            {
                return null;
            }

            // thirty requests arriving together share one opening of the folder
            opening = folder.Source ??= new Lazy<GameFolderSource?>(() => OpenForIcons(key), LazyThreadSafetyMode.ExecutionAndPublication);
            folder.Users++;
        }

        // The folder is opened and the picture decoded here, outside the gate: a request for an icon that is already made never waits for the disk.
        GameFolderSource? source = null;
        AchievementIcon? icon = null;
        try
        {
            source = opening.Value;
            if (source is not null && TrophyIconArt.Render(source, trophyId, TrophyIconArt.LauncherSize) is { } rgba)
            {
                icon = new AchievementIcon(TrophyIconArt.LauncherSize, rgba);
            }
        }
        catch (Exception e) when (IsDataFault(e))
        {
            // Render answers bad data with null itself; this is for what nobody foresaw
        }

        lock (IconGate)
        {
            folder.Users--;
            folder.LastUse = Environment.TickCount64;
            if (source is null)
            {
                // not a game, or not readable now: nothing is settled, the folder is left alone for a while and then looked at again
                if (ReferenceEquals(folder.Source, opening))
                {
                    folder.Source = null;
                    folder.Failed = Environment.TickCount64;
                }

                return null;
            }

            folder.Failed = null;
            if (folder.Settled[trophyId])
            {
                // two threads made the same icon: everybody gets the first
                icon = folder.Icons[trophyId];
            }
            else
            {
                folder.Icons[trophyId] = icon;
                folder.Settled[trophyId] = true;
            }

            if (folder.Dropped)
            {
                CloseIconSource(folder);
            }
            else
            {
                IconIdle.Change(IconIdleMilliseconds, Timeout.Infinite);
            }
        }

        return icon;
    }

    /// <summary>
    /// The tile of a locked achievement of a grade (a padlock, no game picture) at 240 x 240; made once (about a millisecond) and kept, also across
    /// <see cref="Forget"/>. Needs no game folder and never waits for one: it may be called on the UI thread.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown.</exception>
    public static AchievementIcon LockedIcon(TrophyGrade grade) => Tile(LockedIcons, grade, TrophyIconArt.Locked);

    /// <summary>
    /// The blank tile of a grade at 240 x 240, shown while an earned achievement's icon is being made; made once and kept, like <see cref="LockedIcon"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown.</exception>
    public static AchievementIcon BlankIcon(TrophyGrade grade) => Tile(BlankIcons, grade, TrophyIconArt.Blank);

    /// <summary>
    /// The emblem of a grade at 240 x 240 (a trophy cup drawn by code in the tiles' frame, no game picture): the grade's picture on the summary card, and the
    /// picture of an earned row whose icon cannot be made. Made once (a few milliseconds) and kept, like <see cref="LockedIcon"/>, also across <see cref="Forget"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown.</exception>
    public static AchievementIcon EmblemIcon(TrophyGrade grade) => Tile(EmblemIcons, grade, TrophyIconArt.Emblem);

    private static AchievementIcon Tile(AchievementIcon?[] tiles, TrophyGrade grade, Func<TrophyGrade, int, byte[]> draw)
    {
        if ((uint)grade >= (uint)tiles.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(grade), grade, "not a trophy grade");
        }

        // a gate of their own: a tile never waits for a game folder
        lock (TileGate)
        {
            return tiles[(int)grade] ??= new AchievementIcon(TrophyIconArt.LauncherSize, draw(grade, TrophyIconArt.LauncherSize));
        }
    }

    private static GameFolderSource? OpenForIcons(string folder)
    {
        Interlocked.Increment(ref _iconFolderOpenings);
        try
        {
            GameLayout layout = GameLayoutResolver.Resolve(folder);
            return layout.IsGame ? GameFolderSource.Open(layout) : null;
        }
        catch (Exception e) when (IsDataFault(e))
        {
            return null;
        }
    }

    // under IconGate: the cache lets go of its folder; the source is closed now, or by the last render that still reads it
    private static void DropIconFolder()
    {
        if (_icons is { } folder)
        {
            folder.Dropped = true;
            CloseIconSource(folder);
            _icons = null;
        }
    }

    // under IconGate; with no user left nobody is opening the folder either, so the value is there or was never asked for
    private static void CloseIconSource(IconFolder folder)
    {
        if (folder.Users == 0 && folder.Source is { } opened)
        {
            folder.Source = null;
            try
            {
                if (opened.IsValueCreated)
                {
                    opened.Value?.Dispose();
                }
            }
            catch (Exception e) when (IsDataFault(e))
            {
                // closing a folder must not take the page down
            }
        }
    }

    private static void CloseIdleIconFolder()
    {
        lock (IconGate)
        {
            if (_icons is not { Source: not null } folder)
            {
                return;
            }

            if (folder.Users == 0 && Environment.TickCount64 - folder.LastUse >= IconIdleMilliseconds - 50)
            {
                CloseIconSource(folder);
            }
            else
            {
                // a render is still running, or one ended since this timer was set: look again
                IconIdle.Change(IconIdleMilliseconds, Timeout.Infinite);
            }
        }
    }

    private static bool Bit(ushort[] g, int firstWord, int n)
    {
        int word = firstWord + (n >> 4);
        return n >= 0 && word < g.Length && ((g[word] >> (n & 15)) & 1) != 0;
    }

    private static (bool Exists, long Length, DateTime WriteUtc) FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (true, info.Length, info.LastWriteTimeUtc) : (false, 0, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (false, 0, default);
        }
    }

    // -- the game folder ----------------------------------------------------------------------------------------------

    private static FolderData? Folder(string? gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            return null;
        }

        string key = gameFolder.Trim();
        lock (FolderGate)
        {
            long now = Environment.TickCount64;
            if (Folders.TryGetValue(key, out (FolderData? Data, long Tick) known) && (known.Data is not null || now - known.Tick < RetryMilliseconds))
            {
                return known.Data;
            }

            FolderData? data = LoadFolder(key);
            if (Folders.Count >= MaxFolders)
            {
                Folders.Clear();
            }

            Folders[key] = (data, Environment.TickCount64);
            return data;
        }
    }

    private static FolderData? LoadFolder(string folder)
    {
        GameLayout layout;
        try
        {
            layout = GameLayoutResolver.Resolve(folder);
            if (!layout.IsGame)
            {
                return null;
            }
        }
        catch (Exception e) when (IsDataFault(e))
        {
            return null;
        }

        // the names and the scene table come from the eboot; a build whose tables cannot be read is a game folder without them
        var names = new string?[TrophyTable.Count];
        int[] scenes = [];
        try
        {
            using GameFolderSource source = GameFolderSource.Open(layout);
            scenes = [.. source.Eboot.GameFlow.TrophyScenes];
            IUiTables text = source.Eboot.Ui;
            for (int id = TrophyTable.FirstChapter; id <= TrophyTable.LastChapter; id++)
            {
                string name = TrophyTable.ChapterName(text.Text(Language.En, TrophyTable.ChapterTextBase + TrophyTable.ChapterPhase(id)));
                names[id] = name.Length == 0 ? null : name;
            }
        }
        catch (Exception e) when (IsDataFault(e))
        {
            names = new string?[TrophyTable.Count];
            scenes = [];
        }

        return new FolderData(names, Array.AsReadOnly(scenes));
    }

    // Whatever a game folder holds must not take the page down: any failure of reading it is "no names, no table". Only the faults that are not about the data pass.
    private static bool IsDataFault(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException or ThreadAbortException);

    // -- save.dat -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The persistent G words of save.dat (the scene_done and scene_seen bits are among them); null when there is no save or it is damaged. When the file could
    /// not be read just now (it is being replaced, or another program holds it) the words of the last reading of it are returned, or null, and
    /// <paramref name="busy"/> says so.
    /// </summary>
    private static ushort[]? ReadSaveWords(string path, out bool busy)
    {
        busy = false;
        (bool exists, long length, DateTime written) = FileStamp(path);
        if (!exists)
        {
            return null;
        }

        lock (SaveGate)
        {
            if (_save is { Settled: true } known && known.Path == path && known.Length == length && known.WriteUtc == written)
            {
                return known.Words;
            }
        }

        // taken before the file is opened: what is read is at least as new as this moment
        bool settled = DateTime.UtcNow - written >= SettleTime;
        ushort[]? words = null;
        try
        {
            if (ReadStore(path) is { } store)
            {
                words = new ushort[SystemData.PersistentGlobals];
                for (int i = 0; i < words.Length; i++)
                {
                    words[i] = store.SystemData.Block.GetG(i);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // the file is in the way of being replaced, or cannot be opened right now: keep what the last reading of it showed
            busy = true;
        }

        lock (SaveGate)
        {
            if (busy)
            {
                return _save is { } last && last.Path == path ? last.Words : null;
            }

            _save = new SaveWords(path, length, written, words, settled);
            return words;
        }
    }

    /// <summary>
    /// Reads save.dat, or its previous generation when it is damaged, like SaveFile.ReadWithFallback; null when neither is a save. The files are opened so that the
    /// running game can replace them meanwhile: this is read while the game plays, and a reader that held save.dat without sharing would make the game's save fail.
    /// </summary>
    private static SaveStore? ReadStore(string path)
    {
        try
        {
            return SaveFile.Read(new MemoryStream(ReadShared(path), writable: false));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (SaveFormatException)
        {
            try
            {
                return SaveFile.Read(new MemoryStream(ReadShared(path + ".bak"), writable: false));
            }
            catch (Exception e) when (e is SaveFormatException or FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    // the bytes of a file, with the handle held only for the read itself
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxSaveBytes)
        {
            throw new SaveFormatException("the file is too large to be a save");
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
