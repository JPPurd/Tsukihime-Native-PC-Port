using System.Text;
using Tsuki.Engine.Trophies;

namespace Tsuki.Tests.Synthetic.Trophies;

/// <summary>
/// v0.1.9: trophies.ini, the profile's record of earned trophies: what it holds, what a reader makes of a file it does not understand, and that a write either
/// replaces the whole file or leaves the old one.
/// </summary>
public sealed class TrophyLogTests : IDisposable
{
    private static readonly DateTime T1 = new(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-v019-trophylog-" + Guid.NewGuid().ToString("N"));

    public TrophyLogTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private string FilePath => Path.Combine(_dir, TrophyLog.FileName);

    private TrophyLog ReadText(string text, out bool damaged)
    {
        File.WriteAllText(FilePath, text, new UTF8Encoding(false));
        return TrophyLog.Read(FilePath, out damaged);
    }

    private string[] FolderFiles() => [.. Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    // -- the log itself ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ANewLogIsEmpty()
    {
        var log = new TrophyLog();
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Ids);
        for (int id = -2; id <= 31; id++)
        {
            Assert.False(log.IsEarned(id));
            Assert.Null(log.EarnedUtc(id));
        }
    }

    [Fact]
    public void TheFirstRecordOfATrophyIsKeptDateAndAll()
    {
        var log = new TrophyLog();
        Assert.True(log.Add(5, T1));
        Assert.False(log.Add(5, T2));
        Assert.False(log.Add(5, null));
        Assert.Equal(T1, log.EarnedUtc(5));

        // earned with no date stays "no date": a later event is not when it was earned
        Assert.True(log.Add(7, null));
        Assert.False(log.Add(7, T2));
        Assert.True(log.IsEarned(7));
        Assert.Null(log.EarnedUtc(7));

        Assert.Equal(2, log.Count);
        Assert.Equal([5, 7], log.Ids);
    }

    [Fact]
    public void TheIdsAreAscendingWhateverTheOrderTheyWereEarnedIn()
    {
        var log = new TrophyLog();
        foreach (int id in new[] { 29, 0, 14, 3, 28, 1 })
        {
            Assert.True(log.Add(id, null));
        }

        Assert.Equal([0, 1, 3, 14, 28, 29], log.Ids);
        Assert.Equal(6, log.Count);
        Assert.True(log.IsEarned(0));
        Assert.True(log.IsEarned(29));
        Assert.False(log.IsEarned(2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnIdThatIsNotATrophyCannotBeRecorded(int id)
    {
        var log = new TrophyLog();
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Add(id, T1));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Add(id, null));
        Assert.False(log.IsEarned(id));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void TimesAreKeptAsUtcToTheSecond()
    {
        var log = new TrophyLog();

        // a fraction of a second is dropped, not rounded up
        log.Add(1, new DateTime(2026, 11, 12, 13, 3, 11, 999, DateTimeKind.Utc).AddTicks(9999));
        Assert.Equal(T1, log.EarnedUtc(1));
        Assert.Equal(DateTimeKind.Utc, log.EarnedUtc(1)!.Value.Kind);

        // a local time is the same moment in UTC
        DateTime local = T1.ToLocalTime();
        Assert.Equal(DateTimeKind.Local, local.Kind);
        log.Add(2, local);
        Assert.Equal(T1, log.EarnedUtc(2));
        Assert.Equal(DateTimeKind.Utc, log.EarnedUtc(2)!.Value.Kind);

        // a time of no kind is taken as UTC
        log.Add(3, new DateTime(2026, 11, 12, 13, 3, 11, DateTimeKind.Unspecified));
        Assert.Equal(T1, log.EarnedUtc(3));
        Assert.Equal(DateTimeKind.Utc, log.EarnedUtc(3)!.Value.Kind);
    }

    // -- the file's text --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFileIsACommentTheVersionAndOneLinePerTrophyInUtf8WithLineFeeds()
    {
        var log = new TrophyLog();
        log.Add(7, null);
        log.Add(5, T1);
        log.Add(29, T2);
        log.Write(FilePath);

        byte[] bytes = File.ReadAllBytes(FilePath);
        Assert.Equal((byte)'#', bytes[0]);                          // no byte order mark
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        string[] lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("#", lines[0], StringComparison.Ordinal);
        Assert.Equal(["version=1", "5=2026-11-12T13:03:11Z", "7=earlier", "29=2027-01-02T03:04:05Z"], lines[1..]);
        Assert.Equal(Encoding.UTF8.GetString(bytes), log.Format());
    }

    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        var log = new TrophyLog();
        for (int id = 0; id < 30; id++)
        {
            log.Add(id, id % 3 == 0 ? null : T1.AddSeconds((id * 86_399) + id));
        }

        log.Write(FilePath);
        TrophyLog back = TrophyLog.Read(FilePath, out bool damaged);
        Assert.False(damaged);
        Assert.Equal(30, back.Count);
        Assert.Equal(log.Ids, back.Ids);
        for (int id = 0; id < 30; id++)
        {
            Assert.True(back.IsEarned(id));
            Assert.Equal(log.EarnedUtc(id), back.EarnedUtc(id));
            if (back.EarnedUtc(id) is { } time)
            {
                Assert.Equal(DateTimeKind.Utc, time.Kind);
                Assert.Equal(0, time.Ticks % TimeSpan.TicksPerSecond);
            }
            else
            {
                Assert.Equal(0, id % 3);
            }
        }

        Assert.Equal(log.Format(), back.Format());
    }

    [Fact]
    public void AnEmptyLogWritesAFileThatReadsAsEmptyAndNotDamaged()
    {
        new TrophyLog().Write(FilePath);
        TrophyLog back = TrophyLog.Read(FilePath, out bool damaged);
        Assert.False(damaged);
        Assert.Equal(0, back.Count);
    }

    // -- reading ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingFileAMissingFolderOrNoPathIsAnEmptyLog()
    {
        foreach (string? path in new[] { FilePath, Path.Combine(_dir, "no", "such", "folder", TrophyLog.FileName), null, string.Empty })
        {
            TrophyLog log = TrophyLog.Read(path!, out bool damaged);
            Assert.False(damaged);
            Assert.Equal(0, log.Count);
        }

        Assert.Empty(FolderFiles());                                // and reading made nothing
    }

    [Fact]
    public void AnEmptyFileIsAnEmptyLog()
    {
        TrophyLog log = ReadText(string.Empty, out bool damaged);
        Assert.False(damaged);
        Assert.Equal(0, log.Count);

        // so is one that holds only the comment, or only the version
        Assert.Equal(0, ReadText("# nothing yet\n", out damaged).Count);
        Assert.False(damaged);
        Assert.Equal(0, ReadText("version=1\n", out damaged).Count);
        Assert.False(damaged);
    }

    [Fact]
    public void TheDateIsReadAsUtcAndEarlierIsNoDate()
    {
        TrophyLog log = ReadText("# c\nversion=1\n5=2026-11-12T13:03:11Z\n7=earlier\n8=EARLIER\n", out bool damaged);
        Assert.False(damaged);
        Assert.Equal([5, 7, 8], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(5));
        Assert.Equal(DateTimeKind.Utc, log.EarnedUtc(5)!.Value.Kind);
        Assert.Null(log.EarnedUtc(7));
        Assert.Null(log.EarnedUtc(8));
    }

    [Fact]
    public void OtherSpellingsOfTheSameMomentReadAsThatMoment()
    {
        // what a hand edit or another program may leave: a fraction, an offset, carriage returns, spaces, a byte order mark
        TrophyLog log = ReadText("﻿# c\r\nversion=1\r\n 5 = 2026-11-12T13:03:11.987Z \r\n6=2026-11-12T22:03:11+09:00\r\n", out bool damaged);
        Assert.False(damaged);
        Assert.Equal([5, 6], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(5));
        Assert.Equal(T1, log.EarnedUtc(6));
    }

    [Fact]
    public void ARecordSavedWithCarriageReturnsAloneIsReadLineByLine()
    {
        // An editor that saves with the line ends of the classic Mac (CR alone) left the whole record as one line, and that line began with the game's own
        // comment: it read as an empty record, and the next write replaced it with what the save implies, without the other achievements and their dates.
        var written = new TrophyLog();
        written.Add(5, T1);
        written.Add(6, null);
        written.Add(29, T2);
        TrophyLog log = TrophyLog.ReadFile(WriteText(written.Format().Replace('\n', '\r')), out TrophyFileState state);
        Assert.Equal(TrophyFileState.Read, state);
        Assert.Equal([5, 6, 29], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(5));
        Assert.Null(log.EarnedUtc(6));
        Assert.Equal(T2, log.EarnedUtc(29));

        // without the comment, with mixed ends and with a carriage return in the middle of the file: every line is its own
        log = TrophyLog.ReadFile(WriteText("version=1\r5=2026-11-12T13:03:11Z\r6=earlier"), out state);
        Assert.Equal(TrophyFileState.Read, state);
        Assert.Equal([5, 6], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(5));
        log = TrophyLog.ReadFile(WriteText("# c\rversion=1\r\n5=2026-11-12T13:03:11Z\n\r\r6=earlier\r7=2027-01-02T03:04:05Z\n"), out state);
        Assert.Equal(TrophyFileState.Read, state);
        Assert.Equal([5, 6, 7], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(5));
        Assert.Equal(T2, log.EarnedUtc(7));

        // a comment line still ends at its own line end: what follows the '#' up to there is not read
        log = TrophyLog.ReadFile(WriteText("# 5=earlier\rversion=1\r# 6=earlier\r7=earlier\r"), out state);
        Assert.Equal(TrophyFileState.Read, state);
        Assert.Equal([7], log.Ids);

        // and a file of such lines that is no record is still not ours
        Assert.Equal(0, TrophyLog.ReadFile(WriteText("hello\rworld\r"), out state).Count);
        Assert.Equal(TrophyFileState.NotOurs, state);
    }

    private string WriteText(string text)
    {
        File.WriteAllText(FilePath, text, new UTF8Encoding(false));
        return FilePath;
    }

    [Fact]
    public void WhenAnIdIsThereTwiceTheFirstLineIsKept()
    {
        TrophyLog log = ReadText("version=1\n5=2026-11-12T13:03:11Z\n5=2027-01-02T03:04:05Z\n9=earlier\n9=2027-01-02T03:04:05Z\n", out bool damaged);
        Assert.False(damaged);
        Assert.Equal([5, 9], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(5));
        Assert.Null(log.EarnedUtc(9));
    }

    [Fact]
    public void LinesThatAreNotUnderstoodAreSkippedAndTheRestIsRead()
    {
        TrophyLog log = ReadText(
            "# Tsukihime achievements\nversion=1\ncolour=blue\nhello\n=5\n\n   \n5x=earlier\nfive=earlier\n3=earlier\n[section]\n4=2026-11-12T13:03:11Z\nnotice = on\n",
            out bool damaged);
        Assert.False(damaged);
        Assert.Equal([3, 4], log.Ids);
        Assert.Equal(T1, log.EarnedUtc(4));
    }

    [Fact]
    public void IdsThatAreNotTrophiesAreIgnored()
    {
        TrophyLog log = ReadText("version=1\n30=earlier\n99=2026-11-12T13:03:11Z\n-1=earlier\n+2=earlier\n4294967296=earlier\n99999999999999999999=earlier\n1.0=earlier\n0x5=earlier\n29=earlier\n", out bool damaged);
        Assert.False(damaged);
        Assert.Equal([29], log.Ids);
    }

    [Fact]
    public void ATrophyWhoseDateCannotBeReadIsStillEarned()
    {
        // the record only grows: a line a newer version or a hand edit wrote differently must not cost the player the trophy
        TrophyLog log = ReadText("version=1\n5=yesterday\n6=\n7=2026-13-45T99:99:99Z\n", out bool damaged);
        Assert.False(damaged);
        Assert.Equal([5, 6, 7], log.Ids);
        Assert.Null(log.EarnedUtc(5));
        Assert.Null(log.EarnedUtc(6));
        Assert.Null(log.EarnedUtc(7));
    }

    [Fact]
    public void DatesAtTheEdgesOfTheCalendarNeverMakeReadingThrow()
    {
        // a hand edit can put any date there; an offset can push a date past either end of what a date can hold
        TrophyLog log = ReadText(
            "version=1\n1=0001-01-01T00:00:00Z\n2=9999-12-31T23:59:59Z\n3=0001-01-01T00:00:00+14:00\n4=9999-12-31T23:59:59-14:00\n5=0000-00-00T00:00:00Z\n6=2026-02-30T10:00:00Z\n7=2026-11-12T24:00:00Z\n",
            out bool damaged);
        Assert.False(damaged);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], log.Ids);
        Assert.Equal(new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), log.EarnedUtc(1));
        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc), log.EarnedUtc(2));
        Assert.All(new[] { 3, 4, 5, 6 }, id => Assert.Null(log.EarnedUtc(id)));

        // and what was read is written back as a file that reads the same
        log.Write(FilePath);
        TrophyLog back = TrophyLog.Read(FilePath, out damaged);
        Assert.False(damaged);
        Assert.Equal(log.Format(), back.Format());
    }

    [Fact]
    public void ANewerVersionNumberIsStillRead()
    {
        TrophyLog log = ReadText("version=2\n5=earlier\nfuture_key=1\n", out bool damaged);
        Assert.False(damaged);
        Assert.Equal([5], log.Ids);
    }

    public static TheoryData<string, byte[]> Garbage()
    {
        var random = new Random(19);
        byte[] noise = new byte[700];
        random.NextBytes(noise);
        for (int i = 0; i < noise.Length; i++)
        {
            // keep the noise free of the three characters that could make a line readable by chance
            if (noise[i] is (byte)'#' or (byte)'=' or (byte)'\n')
            {
                noise[i] = 0xA5;
            }
        }

        return new TheoryData<string, byte[]>
        {
            { "zeros, as a crash can leave", new byte[512] },
            { "noise", noise },
            { "words", Encoding.UTF8.GetBytes("this is not the file you are looking for\nat all\n") },
            { "a save file's start", [.. "TSUKISAV"u8, 1, 0, 0, 0, 0x20, 0, 0, 0, 0xAC, 0x18, 0, 0] },
            { "a picture's start", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52] },
            { "one character", [(byte)'x'] },
            { "a key with no name", Encoding.UTF8.GetBytes("=earlier\n") },
        };
    }

    [Theory]
    [MemberData(nameof(Garbage))]
    public void AFileOfGarbageIsDamagedAndReadsAsEmpty(string what, byte[] bytes)
    {
        File.WriteAllBytes(FilePath, bytes);
        TrophyLog log = TrophyLog.Read(FilePath, out bool damaged);
        Assert.True(damaged, what);
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Ids);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));           // reading never repairs or removes the file

        // it was read, and it is not a record: what the game may set aside and write anew
        Assert.Equal(0, TrophyLog.ReadFile(FilePath, out TrophyFileState state).Count);
        Assert.Equal(TrophyFileState.NotOurs, state);
    }

    [Fact]
    public void AFileFarTooLargeToBeOursIsDamagedNotRead()
    {
        var text = new StringBuilder("version=1\n5=earlier\n");
        while (text.Length <= 64 * 1024)
        {
            text.Append("# padding padding padding padding padding padding padding padding\n");
        }

        TrophyLog log = ReadText(text.ToString(), out bool damaged);
        Assert.True(damaged);
        Assert.Equal(0, log.Count);
        Assert.Equal(0, TrophyLog.ReadFile(FilePath, out TrophyFileState state).Count);
        Assert.Equal(TrophyFileState.NotOurs, state);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsDamagedAndNeverThrows()
    {
        // a folder of that name
        Directory.CreateDirectory(FilePath);
        TrophyLog log = TrophyLog.Read(FilePath, out bool damaged);
        Assert.True(damaged);
        Assert.Equal(0, log.Count);
        Assert.Equal(0, TrophyLog.ReadFile(FilePath, out TrophyFileState state).Count);
        Assert.Equal(TrophyFileState.Unreadable, state);
        Directory.Delete(FilePath);

        // a path no file system takes
        log = TrophyLog.Read(Path.Combine(_dir, "bad\0name", TrophyLog.FileName), out damaged);
        Assert.Equal(0, log.Count);

        if (OperatingSystem.IsWindows())
        {
            // another program holds the file and shares nothing: what it holds is not known, so it is neither "no record" nor "not a record"
            new TrophyLog().Write(FilePath);
            using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                log = TrophyLog.Read(FilePath, out damaged);
                Assert.True(damaged);
                Assert.Equal(0, log.Count);
                Assert.Equal(0, TrophyLog.ReadFile(FilePath, out state).Count);
                Assert.Equal(TrophyFileState.Unreadable, state);
            }

            Assert.Equal(0, TrophyLog.ReadFile(FilePath, out state).Count);
            Assert.Equal(TrophyFileState.Read, state);
        }
    }

    [Fact]
    public void AReadingTellsARecordFromAFileThatCouldNotBeReadAndFromOneThatIsNotOurs()
    {
        // read: no path, no file, an empty file, a record
        foreach (string? path in new[] { null, string.Empty, FilePath })
        {
            Assert.Equal(0, TrophyLog.ReadFile(path!, out TrophyFileState none).Count);
            Assert.Equal(TrophyFileState.Read, none);
        }

        File.WriteAllText(FilePath, string.Empty);
        Assert.Equal(0, TrophyLog.ReadFile(FilePath, out TrophyFileState empty).Count);
        Assert.Equal(TrophyFileState.Read, empty);
        var log = new TrophyLog();
        log.Add(4, T1);
        log.Write(FilePath);
        TrophyLog back = TrophyLog.ReadFile(FilePath, out TrophyFileState whole);
        Assert.Equal(TrophyFileState.Read, whole);
        Assert.Equal([4], back.Ids);
        Assert.Equal(T1, back.EarnedUtc(4));

        // a file of comments alone is an empty record: writing it loses no achievement
        File.WriteAllText(FilePath, "# notes only\n\n# and another\n");
        Assert.Equal(0, TrophyLog.ReadFile(FilePath, out TrophyFileState comments).Count);
        Assert.Equal(TrophyFileState.Read, comments);

        // The record saved again in another encoding, or with lines that are not its own, holds achievements a reading cannot see: it is not a record, so it
        // is set aside rather than written over. UTF-16 without a byte order mark keeps the '#' of the first line, and a trophy line first in such a file
        // still gives its number, so neither the comment nor that one number may make it read as a record.
        var dated = new TrophyLog();
        dated.Add(5, T1);
        dated.Add(7, T2);
        string game = dated.Format();
        string trophyLines = string.Concat(game.Split('\n').Where(l => l.Length > 0 && char.IsAsciiDigit(l[0])).Select(l => l + "\n"));
        Assert.Equal("5=2026-11-12T13:03:11Z\n7=2027-01-02T03:04:05Z\n", trophyLines);

        // the two kinds of damage, and the old reading that does not tell them apart
        foreach ((string what, Action make, TrophyFileState expected) in new (string, Action, TrophyFileState)[]
        {
            ("words", () => File.WriteAllText(FilePath, "not a record at all\n"), TrophyFileState.NotOurs),
            ("a comment and words", () => File.WriteAllText(FilePath, "# notes\nwords\n"), TrophyFileState.NotOurs),
            ("a comment and a line that is no trophy's", () => File.WriteAllText(FilePath, "# notes\n30=earlier\nversion=one\n"), TrophyFileState.NotOurs),
            ("the record as UTF-16 without a byte order mark", () => File.WriteAllBytes(FilePath, Encoding.Unicode.GetBytes(game)), TrophyFileState.NotOurs),
            ("its trophy lines as UTF-16 without a byte order mark", () => File.WriteAllBytes(FilePath, Encoding.Unicode.GetBytes(trophyLines)), TrophyFileState.NotOurs),
            ("the record as UTF-16 with a byte order mark", () => File.WriteAllText(FilePath, game, Encoding.Unicode), TrophyFileState.NotOurs),
            ("the record as UTF-16 big-endian", () => File.WriteAllBytes(FilePath, Encoding.BigEndianUnicode.GetBytes(game)), TrophyFileState.NotOurs),
            ("the record with a NUL in it", () => File.WriteAllBytes(FilePath, [.. Encoding.UTF8.GetBytes(game), 0, 0, 0, 0]), TrophyFileState.NotOurs),
            ("a folder", () => { File.Delete(FilePath); Directory.CreateDirectory(FilePath); }, TrophyFileState.Unreadable),
        })
        {
            make();
            IReadOnlyList<int> ids = TrophyLog.ReadFile(FilePath, out TrophyFileState state).Ids;
            Assert.True(expected == state && ids.Count == 0, $"{what}: {state} [{string.Join(",", ids)}]");
            Assert.Equal(0, TrophyLog.Read(FilePath, out bool damaged).Count);
            Assert.True(damaged, what);
        }
    }

    [Fact]
    public void AFileThatIsInUseForAMomentIsWaitedForNotCalledDamaged()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere another program's handle never makes an open fail
            return;
        }

        // a writer that is putting a new file in place, a virus scanner: the file is whole, it only cannot be opened this instant
        var log = new TrophyLog();
        log.Add(4, T1);
        FileStream? holder = null;
        TrophyLog seen = ReadWhileAHelperActs(
            () =>
            {
                log.Write(FilePath);
                holder = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            },
            () => holder!.Dispose(),
            out bool damaged);
        Assert.False(damaged);
        Assert.Equal([4], seen.Ids);
        Assert.Equal(T1, seen.EarnedUtc(4));
        Assert.Equal([4], TrophyLog.ReadFile(FilePath, out TrophyFileState state).Ids);
        Assert.Equal(TrophyFileState.Read, state);
    }

    // A reading that runs while a helper thread, a moment after the reading began, does what makes the file readable. The reading waits about a tenth of a
    // second; a helper that the machine kept from running for longer than that proves nothing about the reading, so then the whole thing is done again. A
    // reading that came back empty without waiting, or after the helper had acted, is the answer (and the test fails on it).
    private TrophyLog ReadWhileAHelperActs(Action prepare, Action act, out bool damaged)
    {
        for (int round = 0; ; round++)
        {
            prepare();
            int acted = 0;
            var helper = new Thread(() =>
            {
                Thread.Sleep(8);
                act();
                Volatile.Write(ref acted, 1);
            });
            helper.Start();
            long start = Environment.TickCount64;
            TrophyLog seen = TrophyLog.Read(FilePath, out damaged);
            long waited = Environment.TickCount64 - start;
            bool late = Volatile.Read(ref acted) == 0;
            helper.Join();
            if (seen.Count > 0 || !late || waited < 90 || round == 9)
            {
                return seen;
            }
        }
    }

    [Fact]
    public void AReadingSharesTheFileWithOneWhoWritesItAndOneWhoRemovesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere an open file is no obstacle to anybody
            return;
        }

        // The game replaces the file while a launcher may be reading it: the replace needs the right to remove the old file, which Windows gives only when every
        // handle on it shares deleting. Whether a reading does is seen from the other side: a second handle opens only when it shares what the first one does.
        var log = new TrophyLog();
        log.Add(4, T1);
        log.Write(FilePath);

        // somebody has the file open for writing
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal([4], TrophyLog.Read(FilePath, out bool damaged).Ids);
            Assert.False(damaged);
        }

        // somebody has it open in order to remove it
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose))
        {
            Assert.Equal([4], TrophyLog.Read(FilePath, out bool damaged).Ids);
            Assert.False(damaged);
        }

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void AFileThatIsJustBeingReplacedIsWaitedForNotReadAsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere the new file takes the name in one rename: there is no instant without a file
            return;
        }

        // the instant inside a replace of a file somebody held open: the old file has left, the new one still lies beside the name as the writer's temporary file
        var log = new TrophyLog();
        log.Add(4, T1);
        string temporary = FilePath + ".0a1b2c3d.tmp";
        TrophyLog seen = ReadWhileAHelperActs(
            () =>
            {
                File.Delete(FilePath);
                File.WriteAllText(temporary, log.Format(), new UTF8Encoding(false));
            },
            () => File.Move(temporary, FilePath),
            out bool damaged);
        Assert.False(damaged);
        Assert.Equal([4], seen.Ids);
        Assert.Equal(T1, seen.EarnedUtc(4));
    }

    [Fact]
    public void AMissingFileIsNotWaitedForAndALeftOverTemporaryFileIsNotTheRecord()
    {
        // The looks are counted, not timed (a look yields the processor, and on a busy machine 200 readings took longer than any bound that still told them
        // from readings that wait): a reading that is not waiting for a file looks twice more on Windows and not again elsewhere, one that waits looks again
        // and again for its tenth of a second. A reading also stops looking once that time is over, so one the machine stalled that long may have looked fewer
        // times; such a reading is let through, every other one must have looked exactly so often, and none may ever look more often.
        int looks = 0;
        int expected = OperatingSystem.IsWindows() ? 3 : 1;
        var clock = new System.Diagnostics.Stopwatch();
        void NotWaitedFor(int readings)
        {
            int counted = 0;
            for (int i = 0; i < readings; i++)
            {
                Interlocked.Exchange(ref looks, 0);
                clock.Restart();
                Assert.Equal(0, TrophyLog.Read(FilePath, out bool damaged).Count);
                long took = clock.ElapsedMilliseconds;
                Assert.False(damaged);
                int n = Volatile.Read(ref looks);
                Assert.True(n == expected || (n >= 1 && n < expected && took >= 90), $"reading {i} looked {n} times in {took} ms; {expected} expected");
                counted += n == expected ? 1 : 0;
            }

            Assert.True(counted > 0, "every reading was stalled");
        }

        TrophyLog.MissedOpen = path =>
        {
            if (path == FilePath)
            {
                Interlocked.Increment(ref looks);
            }
        };
        try
        {
            // no file and nothing beside the name: the answer comes at once
            NotWaitedFor(200);

            // a temporary file a writer that was stopped left behind: the reading waits its moment for the file (on Windows, where a replace has an instant
            // without the file), then it is "no file"; what the temporary file holds is not read as the record
            var log = new TrophyLog();
            log.Add(4, T1);
            File.WriteAllText(FilePath + ".0a1b2c3d.tmp", log.Format(), new UTF8Encoding(false));
            Interlocked.Exchange(ref looks, 0);
            clock.Restart();
            TrophyLog seen = TrophyLog.Read(FilePath, out bool leftOver);
            long waited = clock.ElapsedMilliseconds;
            Assert.False(leftOver);
            Assert.Equal(0, seen.Count);
            int looked = Volatile.Read(ref looks);
            Assert.True(OperatingSystem.IsWindows() ? looked > expected || waited >= 90 : looked == expected, $"the reading beside a new temporary file looked {looked} times in {waited} ms");
            Assert.Equal([TrophyLog.FileName + ".0a1b2c3d.tmp"], FolderFiles());

            // and once that temporary file is no longer a moment old it is not waited for at all: nothing ever removes it, and a profile without a record is
            // read every time its save changes
            File.SetLastWriteTimeUtc(FilePath + ".0a1b2c3d.tmp", DateTime.UtcNow.AddMinutes(-10));
            NotWaitedFor(200);
            Assert.Equal([TrophyLog.FileName + ".0a1b2c3d.tmp"], FolderFiles());
        }
        finally
        {
            TrophyLog.MissedOpen = null;
        }
    }

    // -- writing ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WritingMakesTheFolderAndLeavesOnlyTheFile()
    {
        string path = Path.Combine(_dir, "profile", "deeper", TrophyLog.FileName);
        var log = new TrophyLog();
        log.Add(1, T1);
        log.Write(path);
        Assert.Equal([TrophyLog.FileName], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        Assert.Equal([1], TrophyLog.Read(path, out _).Ids);
    }

    [Fact]
    public void AWriteReplacesTheWholeFileAndLeavesNoTemporaryFile()
    {
        var first = new TrophyLog();
        for (int id = 0; id < 30; id++)
        {
            first.Add(id, T1);
        }

        first.Write(FilePath);
        var second = new TrophyLog();
        second.Add(2, T2);
        second.Write(FilePath);

        // the shorter text is the whole file: nothing of the longer one is left behind it
        Assert.Equal(second.Format(), File.ReadAllText(FilePath));
        Assert.Equal([TrophyLog.FileName], FolderFiles());

        for (int again = 0; again < 20; again++)
        {
            second.Write(FilePath);
        }

        Assert.Equal([TrophyLog.FileName], FolderFiles());
    }

    [Fact]
    public void AReaderHoldingTheFileOpenDoesNotCostTheGameItsWrite()
    {
        // the launcher's page reads the file every time it changes, while the game writes it: on Windows a plain move over a file that is open anywhere is refused
        var old = new TrophyLog();
        old.Add(1, T1);
        old.Write(FilePath);
        string oldText = old.Format();

        using (var reader = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var next = new TrophyLog();
            next.Add(1, T1);
            next.Add(2, T2);
            next.Write(FilePath);
            Assert.Equal([1, 2], TrophyLog.Read(FilePath, out bool damaged).Ids);
            Assert.False(damaged);

            // and again while the first reader still holds what is now the previous file
            next.Add(3, null);
            next.Write(FilePath);
            Assert.Equal([1, 2, 3], TrophyLog.Read(FilePath, out damaged).Ids);
            Assert.False(damaged);

            // the reader that was there first reads the file it opened, whole
            using var text = new StreamReader(reader, Encoding.UTF8);
            Assert.Equal(oldText, text.ReadToEnd());
        }

        Assert.Equal([TrophyLog.FileName], FolderFiles());
        Assert.Equal([1, 2, 3], TrophyLog.Read(FilePath, out _).Ids);
    }

    [Fact]
    public void ReadsDuringWritesSeeAWholeFileOldOrNewAndNeverLoseATrophy()
    {
        // the page's poll against the game's writes: every reading is some whole state of the record, so the count a reader sees never falls
        var log = new TrophyLog();
        log.Add(0, null);
        log.Write(FilePath);
        using var stop = new CancellationTokenSource();
        var faults = new List<string>();
        int readings = 0;
        var reader = new Thread(() =>
        {
            int last = 1;
            while (!stop.IsCancellationRequested)
            {
                TrophyLog seen = TrophyLog.Read(FilePath, out bool damaged);
                readings++;
                if (damaged || seen.Count < last)
                {
                    lock (faults)
                    {
                        faults.Add($"reading {readings}: damaged {damaged}, {seen.Count} earned after {last}");
                    }
                }
                else
                {
                    last = seen.Count;
                }

                Thread.SpinWait(5000);                              // a page reads now and then, not without a pause
            }
        });
        reader.Start();
        try
        {
            for (int round = 0; round < 10; round++)
            {
                for (int id = 1; id < 30; id++)
                {
                    log.Add(id, T1);
                    log.Write(FilePath);
                }
            }
        }
        finally
        {
            stop.Cancel();
            reader.Join();
        }

        Assert.True(readings > 0);
        Assert.True(faults.Count == 0, $"{faults.Count} of {readings} readings: {string.Join("; ", faults.Take(5))}");
        Assert.Equal(30, TrophyLog.Read(FilePath, out _).Count);
        Assert.Equal([TrophyLog.FileName], FolderFiles());
    }

    [Fact]
    public void WhileTheRecordIsRewrittenItsNameAlwaysHasAFile()
    {
        // Not only the launcher's reading looks at the file: its stamp, a backup's copy and the player's own tools do, and none of them waits. The new file takes
        // the name in one step, so nobody who looks is ever told that there is no such file. (For an instant the answer can be "not now": that is counted apart.)
        var log = new TrophyLog();
        log.Add(0, null);
        log.Write(FilePath);
        using var stop = new CancellationTokenSource();
        int looks = 0;
        int missing = 0;
        int busy = 0;
        var watcher = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                looks++;
                try
                {
                    File.GetAttributes(FilePath);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    missing++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    busy++;
                }

                Thread.SpinWait(3000);                              // a look now and then: the watcher is not there to keep the file busy
            }
        });
        watcher.Start();
        try
        {
            for (int round = 0; round < 8; round++)
            {
                for (int id = 1; id < 30; id++)
                {
                    log.Add(id, T1);
                    log.Write(FilePath);
                }
            }
        }
        finally
        {
            stop.Cancel();
            watcher.Join();
        }

        Assert.True(looks > 50, $"{looks} looks");
        Assert.True(missing == 0, $"the file was missing at {missing} of {looks} looks ({busy} more found it busy)");
        Assert.Equal([TrophyLog.FileName], FolderFiles());
    }

    [Fact]
    public void AWriteThatCannotReplaceTheFileThrowsAndTheOldFileStays()
    {
        if (!OperatingSystem.IsWindows())
        {
            // elsewhere a rename over a file somebody holds goes through: there is nothing to refuse
            return;
        }

        var old = new TrophyLog();
        old.Add(1, T1);
        old.Write(FilePath);
        byte[] before = File.ReadAllBytes(FilePath);
        var next = new TrophyLog();
        next.Add(1, T1);
        next.Add(2, T2);

        // a program that holds the file and does not let it be replaced
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception e = Assert.ThrowsAny<Exception>(() => next.Write(FilePath));
            Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
        }

        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal([TrophyLog.FileName], FolderFiles());

        // the same holder over a file that is marked read-only as well: the mark comes off, the write is still refused and the old record stays
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception e = Assert.ThrowsAny<Exception>(() => next.Write(FilePath));
            Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
        }

        Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.Equal([TrophyLog.FileName], FolderFiles());

        // and once nothing is in the way the same write goes through
        next.Write(FilePath);
        Assert.Equal([1, 2], TrophyLog.Read(FilePath, out _).Ids);
    }

    [Fact]
    public void ARecordMarkedReadOnlyIsWrittenAnyway()
    {
        // A profile copied back from a backup, a zip or a read-only medium can carry the read-only attribute on trophies.ini. Windows refuses the move over such
        // a file, so the record was never written again: every start found the same catch-up anew, and what was earned meanwhile came back without its date.
        // It is the port's own file in its own profile: the attribute is taken off and the write goes through. (Elsewhere the rename always went through.)
        var old = new TrophyLog();
        old.Add(5, T1);
        old.Add(6, null);
        old.Write(FilePath);
        File.SetAttributes(FilePath, File.GetAttributes(FilePath) | FileAttributes.ReadOnly);
        TrophyLog next = TrophyLog.Read(FilePath, out bool damaged);
        Assert.False(damaged);
        Assert.True(next.Add(1, T2));
        next.Write(FilePath);
        TrophyLog back = TrophyLog.Read(FilePath, out damaged);
        Assert.False(damaged);
        Assert.Equal([1, 5, 6], back.Ids);
        Assert.Equal(T2, back.EarnedUtc(1));
        Assert.Equal(T1, back.EarnedUtc(5));
        Assert.Equal((FileAttributes)0, File.GetAttributes(FilePath) & FileAttributes.ReadOnly);
        Assert.Equal([TrophyLog.FileName], FolderFiles());

        // with a reader that has the marked file open at that moment, sharing as the launchers do: the mark comes off and the write waits its moment as for any
        // held file
        File.SetAttributes(FilePath, File.GetAttributes(FilePath) | FileAttributes.ReadOnly);
        Assert.True(next.Add(2, T2));
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            next.Write(FilePath);
        }

        Assert.Equal([1, 2, 5, 6], TrophyLog.Read(FilePath, out damaged).Ids);
        Assert.False(damaged);
        Assert.Equal((FileAttributes)0, File.GetAttributes(FilePath) & FileAttributes.ReadOnly);
        Assert.Equal([TrophyLog.FileName], FolderFiles());
    }

    [Fact]
    public void AFolderOfThatNameKeepsItsReadOnlyMark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // elsewhere the mark on a folder is its permissions, and no mark is ever taken off there
        }

        // only the record itself loses the attribute: a folder in its place is not the port's file
        Directory.CreateDirectory(FilePath);
        var folder = new DirectoryInfo(FilePath);
        folder.Attributes |= FileAttributes.ReadOnly;
        try
        {
            var log = new TrophyLog();
            log.Add(1, T1);
            Exception e = Assert.ThrowsAny<Exception>(() => log.Write(FilePath));
            Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
            folder.Refresh();
            Assert.Equal(FileAttributes.ReadOnly, folder.Attributes & FileAttributes.ReadOnly);
            Assert.Equal([TrophyLog.FileName], FolderFiles());
        }
        finally
        {
            folder.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    [Fact]
    public void AWriteOntoAFolderOfThatNameThrowsAndLeavesNoTemporaryFile()
    {
        Directory.CreateDirectory(FilePath);
        var log = new TrophyLog();
        log.Add(1, T1);
        Exception e = Assert.ThrowsAny<Exception>(() => log.Write(FilePath));
        Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
        Assert.True(Directory.Exists(FilePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(FilePath));
        Assert.Equal([TrophyLog.FileName], FolderFiles());          // the folder that is in the way, and no temporary file beside it
    }

    [Fact]
    public void AWriteWhoseTemporaryFileCannotBeMadeThrowsAndTheOldFileStays()
    {
        // the profile "folder" is a file: nothing can be created inside it
        string blocker = Path.Combine(_dir, "profile");
        File.WriteAllText(blocker, "in the way");
        var log = new TrophyLog();
        log.Add(1, T1);
        Exception e = Assert.ThrowsAny<Exception>(() => log.Write(Path.Combine(blocker, TrophyLog.FileName)));
        Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
        Assert.Equal("in the way", File.ReadAllText(blocker));
        Assert.Equal(["profile"], FolderFiles());

        Assert.ThrowsAny<ArgumentException>(() => log.Write(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => log.Write(null!));
    }

    [Fact]
    public void AWriteIntoAFolderThatLetsNothingInThrowsAndTheOldFileStays()
    {
        if (OperatingSystem.IsWindows())
        {
            // a folder's read-only mark does not keep new files out on Windows; the long file name below is the case of a temporary file that cannot be made there
            return;
        }

        string folder = Path.Combine(_dir, "profile");
        string path = Path.Combine(folder, TrophyLog.FileName);
        var old = new TrophyLog();
        old.Add(1, T1);
        old.Write(path);
        byte[] before = File.ReadAllBytes(path);
        var next = new TrophyLog();
        next.Add(1, T1);
        next.Add(2, T2);

        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                // an administrator's account writes where the folder says no: then there is nothing to refuse
                File.WriteAllText(Path.Combine(folder, "probe"), string.Empty);
                File.Delete(Path.Combine(folder, "probe"));
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            Exception e = Assert.ThrowsAny<Exception>(() => next.Write(path));
            Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal([TrophyLog.FileName], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // and once the folder lets files in again the same write goes through
        next.Write(path);
        Assert.Equal([1, 2], TrophyLog.Read(path, out _).Ids);
    }

    [Fact]
    public void AWriteWhoseTemporaryFileCannotBeMadeBesideTheOldFileThrowsAndTheOldFileStays()
    {
        // A file name of 250 characters is one a file system takes (255 is the most); the temporary file's name is 13 characters longer and is not. So the old
        // file is there and the temporary file cannot be made, as in a folder that lets nothing new in.
        string name = new string('t', 246) + ".ini";
        string path = Path.Combine(_dir, name);
        var old = new TrophyLog();
        old.Add(1, T1);
        File.WriteAllText(path, old.Format(), new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);

        var next = new TrophyLog();
        next.Add(1, T1);
        next.Add(2, T2);
        Exception e = Assert.ThrowsAny<Exception>(() => next.Write(path));
        Assert.True(e is IOException or UnauthorizedAccessException, e.GetType().Name);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal([name], FolderFiles());
        Assert.Equal([1], TrophyLog.Read(path, out bool damaged).Ids);
        Assert.False(damaged);
    }
}
