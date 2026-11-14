// The record of earned trophies (v0.1.9): the small text file trophies.ini beside save.dat. It is kept apart from the save because the save's system block is the
// PS4's byte for byte, and because achievements belong to the profile and only grow: restoring an older save or importing a PS4 save never removes one.
//
//   # comment
//   version=1
//   5=2026-11-12T13:03:11Z      trophy 5, earned at that UTC time
//   7=earlier                   trophy 7, earned before achievements were recorded (no date)
//
// UTF-8, LF. The game writes it (one game per profile). The launchers read it, with sharing that never blocks the game's replace of the file, and write it only
// while no game runs: a restore or an import of a save first records what the outgoing save implies, and a restore adds the backup's record.
using System.Globalization;
using System.Text;

namespace Tsuki.Engine.Trophies;

/// <summary>What a reading of trophies.ini found (<see cref="TrophyLog.ReadFile"/>).</summary>
public enum TrophyFileState
{
    /// <summary>The file was read, or there is none (no file is an empty record): the log is what the file holds, and writing it loses nothing.</summary>
    Read,

    /// <summary>
    /// There is a file and it could not be opened or read: another program holds it, no permission, a folder of that name, a path the file system does not
    /// take. What it holds is not known, so it must not be written over; a later reading may succeed. The log reads as empty.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The file was read and is not a record: it holds lines and not one of them is a version or a trophy line (a file of comments alone is an empty record), it
    /// holds a NUL character, which no record has (the record saved as UTF-16, whose first line still begins with '#'), or it is far larger than any record.
    /// Reading it again gives the same. The log reads as empty.
    /// </summary>
    NotOurs,
}

/// <summary>The profile's record of earned trophies: the file <c>trophies.ini</c> beside <c>save.dat</c>. Not safe for use from several threads at once.</summary>
public sealed class TrophyLog
{
    /// <summary>The file name, beside <c>save.dat</c>.</summary>
    public const string FileName = "trophies.ini";

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string NoDate = "earlier";

    // a real file is under 1 KB; anything larger than this is not ours
    private const int MaxFileBytes = 64 * 1024;

    // how long a write keeps trying to move its file over one that somebody holds open, and how long a read keeps trying to open a file that is in use
    private const int MoveMilliseconds = 150;
    private const int OpenMilliseconds = 120;

    // how old a writer's temporary file may be to count as a write that is under way (the write itself takes milliseconds)
    private const int WriteUnderWaySeconds = 5;

    private readonly bool[] _earned = new bool[TrophyTable.Count];
    private readonly DateTime?[] _times = new DateTime?[TrophyTable.Count];

    /// <summary>Creates an empty log.</summary>
    public TrophyLog()
    {
    }

    /// <summary>
    /// Reads the file. A missing file (or no path) is an empty log. The file is opened so that a writer can replace it meanwhile. Never throws: a file that cannot be
    /// read, or that is not a record (<see cref="TrophyFileState.NotOurs"/>), is <paramref name="damaged"/> and reads as empty. <see cref="ReadFile"/> tells the
    /// two apart.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="damaged">True when there is a file and it could not be read (<see cref="TrophyFileState.Unreadable"/> or <see cref="TrophyFileState.NotOurs"/>).</param>
    public static TrophyLog Read(string path, out bool damaged)
    {
        TrophyLog log = ReadFile(path, out TrophyFileState state);
        damaged = state != TrophyFileState.Read;
        return log;
    }

    /// <summary>
    /// Reads the file, as <see cref="Read"/> does, and says what it found: read (or no file), a file that could not be opened or read (never write over it: what it
    /// holds is not known), or a file that is not a record. Never throws; in both of the last two cases the log is empty.
    /// </summary>
    /// <remarks>
    /// Lines that are not understood are skipped. A trophy line whose time cannot be read still counts as earned, with no date (the record only grows). When an id
    /// is there twice the first line is kept.
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <param name="state">What was found.</param>
    public static TrophyLog ReadFile(string path, out TrophyFileState state)
    {
        state = TrophyFileState.Read;
        var log = new TrophyLog();
        if (string.IsNullOrEmpty(path))
        {
            return log;
        }

        string text;
        try
        {
            using FileStream stream = OpenShared(path);
            if (stream.Length > MaxFileBytes)
            {
                state = TrophyFileState.NotOurs;
                return log;
            }

            byte[] bytes = new byte[(int)stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            text = Encoding.UTF8.GetString(bytes, 0, read);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return log;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            state = TrophyFileState.Unreadable;
            return log;
        }

        // No record holds a NUL. A file that does is another program's, or the record saved in another encoding: UTF-16 without a byte order mark keeps the
        // first line's '#' (and a number before a NUL still parses), so the lines alone would read it as a record with nothing, or with only its first trophy.
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            state = TrophyFileState.NotOurs;
            return log;
        }

        // a file of comments alone is an empty record; one with lines of its own and not one version or trophy line among them is not a record. A line ends at
        // LF or at CR: a record an editor saved with CR alone was one long line that began with the comment's '#', so it read as empty and was written over
        bool uncommented = false;
        bool record = false;
        foreach (string raw in text.Split('\n', '\r'))
        {
            string line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            uncommented = true;
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                record |= int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                continue;
            }

            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id < TrophyTable.Count)
            {
                record = true;
                log.Add(id, ParseTime(value));
            }
        }

        if (uncommented && !record)
        {
            state = TrophyFileState.NotOurs;
            return new TrophyLog();
        }

        return log;
    }

    /// <summary>True when trophy <paramref name="id"/> is in the log (false for an id that is not 0..29).</summary>
    public bool IsEarned(int id) => id is >= 0 and < TrophyTable.Count && _earned[id];

    /// <summary>When trophy <paramref name="id"/> was earned (UTC, whole seconds); null when it is not earned, or earned with no known date.</summary>
    public DateTime? EarnedUtc(int id) => IsEarned(id) ? _times[id] : null;

    /// <summary>The earned ids, ascending.</summary>
    public IReadOnlyList<int> Ids
    {
        get
        {
            var ids = new List<int>(TrophyTable.Count);
            for (int id = 0; id < TrophyTable.Count; id++)
            {
                if (_earned[id])
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    /// <summary>How many trophies are earned.</summary>
    public int Count => _earned.Count(e => e);

    /// <summary>
    /// Records trophy <paramref name="id"/>; false when it was already there (the first record is kept, date and all). The time is kept as UTC in whole seconds
    /// (a local time is converted, a time of no kind is taken as UTC); null is "earned, date not known".
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not 0..29.</exception>
    public bool Add(int id, DateTime? earnedUtc)
    {
        if (id is < 0 or >= TrophyTable.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "trophy ids are 0..29");
        }

        if (_earned[id])
        {
            return false;
        }

        _earned[id] = true;
        _times[id] = earnedUtc is { } time ? WholeSecondsUtc(time) : null;
        return true;
    }

    /// <summary>
    /// Writes the file: the text goes to a temporary file in the same folder (created when missing), is flushed to disk and then takes the file's place, so a crash
    /// leaves the old file or the new one. Throws when that fails (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>); the temporary file is removed.
    /// </summary>
    /// <remarks>
    /// The temporary file is moved over the file: one rename, so whoever opens the file at that moment finds the old file or the new one. On Windows that move is
    /// refused while anyone has the file open, whatever sharing the holder asked for; a launcher holds it only for the moment of its read, so the move is tried
    /// again for a moment. Only when the holder does not let go is the file replaced with <c>File.Replace</c>, which goes through when the holder shares the
    /// file as <see cref="Read"/> does but leaves the name without a file for an instant (measured: a reader looking at that instant finds no file, which is
    /// why <see cref="Read"/> looks again while a temporary file lies beside the name). Either way a reader does not cost the game its write.
    /// </remarks>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        bool replacing = false;
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(Format()));
                stream.Flush(flushToDisk: true);
            }

            if (!TryMoveOver(tmp, full))
            {
                replacing = true;
                File.Replace(tmp, full, null, ignoreMetadataErrors: true);
            }
        }
        catch (Exception first)
        {
            try
            {
                if (replacing && first is IOException or UnauthorizedAccessException && File.Exists(tmp) && !File.Exists(full) && !Directory.Exists(full))
                {
                    // a replace that stopped half way took the old file away and did not bring the new one: the temporary file, whole and flushed, is the record now
                    File.Move(tmp, full);
                    return;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the first failure is the one to report
            }

            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the first failure is the one to report
            }

            throw;
        }
    }

    // Moves the temporary file over the file. False when the file is there and the move stayed refused for MoveMilliseconds (somebody holds it open); a move
    // that cannot work at all (a folder of that name, a folder that went away) throws at once. A record that carries the read-only attribute (Windows refuses
    // the move over it; a profile copied back from a backup, a zip or a read-only medium has it) loses the attribute and the move is tried once more: it is
    // the port's own file in its own profile, and a record that is never written brings the same catch-up at every start and loses the dates of what is earned.
    private static bool TryMoveOver(string tmp, string full)
    {
        long start = Environment.TickCount64;
        bool cleared = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, full, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && !cleared && ClearReadOnly(full))
            {
                cleared = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && HeldOpen(full))
            {
                if (Environment.TickCount64 - start >= MoveMilliseconds)
                {
                    return false;
                }

                Pause(attempt);
            }
        }
    }

    // True when the file (not a folder of that name) carried the read-only attribute and it was taken off. Windows only: elsewhere that mark on a file keeps
    // no rename out, so a refused move has another cause and the file is left as it is.
    private static bool ClearReadOnly(string full)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Directory)) != FileAttributes.ReadOnly)
            {
                return false;
            }

            File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // true when a refused move over the file can be somebody's open handle: the file is there and is not marked read-only
    private static bool HeldOpen(string full)
    {
        try
        {
            return File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Opens the file for reading so that a writer can replace it meanwhile. A writer that is putting a new file in place at this very moment can make the open
    // fail: with a sharing violation or "access denied" while the old file is on its way out, and, when the writer had to fall back on File.Replace, with
    // "not found" in the instant between the old file leaving and the new one arriving. None of these is a damaged or a missing file, so the open is tried
    // again for OpenMilliseconds. A file that is simply not there is not waited for.
    private static FileStream OpenShared(string path)
    {
        long start = Environment.TickCount64;
        int misses = 0;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException) when (Missed(path) && Environment.TickCount64 - start < OpenMilliseconds && BeingReplaced(path, ref misses))
            {
                Pause(attempt);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or (IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
                && Environment.TickCount64 - start < OpenMilliseconds && !Directory.Exists(path))
            {
                // (a folder of that name will never open; anything else may: a file on its way out answers even "is it there" with a refusal)
                Pause(attempt);
            }
        }
    }

    /// <summary>For the tests: called with the path each time a reading's open found no file there, before it decides whether to look again.</summary>
    internal static Action<string>? MissedOpen { get; set; }

    private static bool Missed(string path)
    {
        MissedOpen?.Invoke(path);
        return true;
    }

    // True when a file that was just not found may be on its way in. While Write's fallback replaces the file, its temporary file lies beside the name until
    // the new file has the name: so a temporary file written a moment ago means "look again", and none means the new file has arrived or there is no file at
    // all, which two more looks tell apart. A temporary file that is older is what a game that was stopped in the middle of a write left behind: it is not
    // waited for (nothing removes it, and every reading of a profile without a record would wait). Only Windows has that instant; elsewhere the one rename
    // leaves none.
    private static bool BeingReplaced(string path, ref int misses)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is { } dir && Directory.EnumerateFiles(dir, Path.GetFileName(full) + ".*.tmp").Any(WrittenAMomentAgo))
            {
                misses = 0;
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }

        return ++misses <= 2;
    }

    // (a file that went away between the listing and this look has the time of no file, long ago: the two more looks cover it)
    private static bool WrittenAMomentAgo(string tmp)
    {
        try
        {
            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp)).Duration() < TimeSpan.FromSeconds(WriteUnderWaySeconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    // The first tries follow each other closely (a reader has the file for a fraction of a millisecond, and the moment it lets go is the one to catch); the
    // later ones are a millisecond apart.
    private static void Pause(int attempt)
    {
        if (attempt < 16)
        {
            Thread.SpinWait(200);
            Thread.Yield();
        }
        else
        {
            Thread.Sleep(1);
        }
    }

    /// <summary>The text of the file.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        text.Append("# Tsukihime achievements (written by the game; kept with your saves). One line per achievement: id=the UTC time it was earned, or \"earlier\" when the date is not known\n");
        text.Append("version=1\n");
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            if (_earned[id])
            {
                text.Append(CultureInfo.InvariantCulture, $"{id}={(_times[id] is { } time ? time.ToString(TimeFormat, CultureInfo.InvariantCulture) : NoDate)}\n");
            }
        }

        return text.ToString();
    }

    private static DateTime? ParseTime(string value)
    {
        if (DateTime.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime exact))
        {
            return WholeSecondsUtc(exact);
        }

        // another ISO 8601 spelling (fractions, an offset) a hand edit may leave
        return !value.Equals(NoDate, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset other)
            ? WholeSecondsUtc(other.UtcDateTime)
            : null;
    }

    private static DateTime WholeSecondsUtc(DateTime time)
    {
        DateTime utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
