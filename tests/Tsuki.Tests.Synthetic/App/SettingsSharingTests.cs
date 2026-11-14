using Tsuki.App.Services;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// v0.1.9: settings.ini is read and written by the game and by the launcher while the other one runs (the launcher's "Notify in the game" switch on the
/// Achievements page, the game's own Config rows). On Windows a write moved its temporary file over the file, which is refused while anyone has the file open,
/// and a read at the moment of a write failed and gave the defaults, which the next <see cref="AppSettings.Update"/> then wrote over every other key.
/// </summary>
public sealed class SettingsSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-v019-settings-share-" + Guid.NewGuid().ToString("N"));

    public SettingsSharingTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SettingsPath => Path.Combine(_dir, AppSettings.FileName);

    private static AppSettingsData Start() => new(PromptSetting.Xbox, WindowWidth: 1600, WindowHeight: 900, VSync: false, CopyText: true, TextWebSocketPort: 6700);

    [Fact]
    public void AReaderThatHoldsTheSettingsFileOpenDoesNotStopTheWrite()
    {
        Assert.True(AppSettings.Save(SettingsPath, Start()));
        string first = File.ReadAllText(SettingsPath);

        // the other program has the file open the way Load opens it, and does not let go: the launcher's switch is written all the same
        using (var reader = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.True(AppSettings.Update(SettingsPath, s => s with { TrophyNotice = false }));
            Assert.Equal(Start() with { TrophyNotice = false }, AppSettings.Load(SettingsPath));

            // and the reader still reads the file it opened, whole
            using var text = new StreamReader(reader);
            Assert.Equal(first, text.ReadToEnd());
        }

        // the next write is an ordinary one, and no temporary file stays behind
        Assert.True(AppSettings.Update(SettingsPath, s => s with { TrophyNotice = true }));
        Assert.Equal(Start() with { TrophyNotice = true }, AppSettings.Load(SettingsPath));
        Assert.Equal([AppSettings.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void WritesAndReadsOfTwoProgramsAtTheSameTimeNeverLoseAKey()
    {
        Assert.True(AppSettings.Save(SettingsPath, Start()));
        using var stop = new CancellationTokenSource();
        int readings = 0;
        int wrong = 0;
        string? firstWrong = null;

        // "the game": reads the file over and over, each time through a handle of its own, as another program does
        var reader = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                AppSettingsData read = AppSettings.Load(SettingsPath);
                readings++;
                if (read with { TrophyNotice = null } != Start())
                {
                    wrong++;
                    firstWrong ??= read.ToString();
                }
            }
        });
        reader.Start();
        try
        {
            // "the launcher": flips its switch 300 times. Every write goes through, and every one starts from a whole reading: no other key is ever lost
            for (int i = 0; i < 300; i++)
            {
                bool on = i % 2 == 0;
                Assert.True(AppSettings.Update(SettingsPath, s => s with { TrophyNotice = on }), $"write {i} was refused");
            }
        }
        finally
        {
            stop.Cancel();
            reader.Join();
        }

        Assert.True(readings > 0);
        Assert.True(wrong == 0, $"{wrong} of {readings} readings were not the whole file; the first: {firstWrong}");
        Assert.Equal(Start() with { TrophyNotice = false }, AppSettings.Load(SettingsPath));
        Assert.Equal([AppSettings.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void AReadingInTheInstantOfAReplaceWaitsForTheNewFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // elsewhere the new file takes the name in one rename: there is no instant without a file
        }

        // The instant inside a writer's File.Replace: the old file has left, the new one still lies beside the name as the writer's temporary file, and a helper
        // thread moves it in a moment after the reading began. The reading waits about a tenth of a second; a helper the machine kept from running for longer
        // than that proves nothing about the reading, so then the whole thing is done again.
        string temp = SettingsPath + ".tmp";
        for (int round = 0; ; round++)
        {
            File.Delete(SettingsPath);
            File.WriteAllText(temp, AppSettings.Format(Start()));
            int moved = 0;
            var helper = new Thread(() =>
            {
                Thread.Sleep(8);
                File.Move(temp, SettingsPath);
                Volatile.Write(ref moved, 1);
            });
            helper.Start();
            long start = Environment.TickCount64;
            AppSettingsData read = AppSettings.Load(SettingsPath);
            long waited = Environment.TickCount64 - start;
            bool late = Volatile.Read(ref moved) == 0;
            helper.Join();
            if (read == Start() || !late || waited < 90 || round == 9)
            {
                Assert.Equal(Start(), read);
                break;
            }
        }
    }

    [Fact]
    public void AFileThatArrivesBetweenAMissedOpenAndTheLookForTheTemporaryFileIsRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // elsewhere the new file takes the name in one rename: there is no instant without a file
        }

        // The other instant of a File.Replace: the reading's open found no file, and before it looked for the writer's temporary file the replace finished (the
        // temporary file is gone, the name has its file again). With one look the reading answered "no file": Load gave the defaults, and Update wrote them
        // over every other key. Each round starts with no file, and the hook puts the file in place right after the first open that missed. A reading also
        // stops looking a tenth of a second after its first look: a round the machine stalled that long proves nothing about the looks, so it is done again.
        string whole = AppSettings.Format(Start());
        int missed = 0;
        AppSettings.MissedOpen = path =>
        {
            if (path == SettingsPath && Interlocked.Increment(ref missed) == 1)
            {
                File.WriteAllText(SettingsPath, whole);
            }
        };
        try
        {
            for (int round = 0; ; round++)
            {
                File.Delete(SettingsPath);
                Interlocked.Exchange(ref missed, 0);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                AppSettingsData read = AppSettings.Load(SettingsPath);
                if (read == Start() || clock.ElapsedMilliseconds < 90 || round == 4)
                {
                    Assert.Equal(Start(), read);
                    Assert.Equal(1, Volatile.Read(ref missed));
                    break;
                }
            }

            for (int round = 0; ; round++)
            {
                File.Delete(SettingsPath);
                Interlocked.Exchange(ref missed, 0);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                bool written = AppSettings.Update(SettingsPath, s => s with { TrophyNotice = false });
                long took = clock.ElapsedMilliseconds;
                AppSettingsData after = AppSettings.Load(SettingsPath);
                if (after == Start() with { TrophyNotice = false } || took < 90 || round == 4)
                {
                    Assert.True(written);
                    Assert.Equal(Start() with { TrophyNotice = false }, after);
                    break;
                }
            }
        }
        finally
        {
            AppSettings.MissedOpen = null;
        }
    }

    [Fact]
    public void AFileThatIsSimplyNotThereIsNoFileAfterExactlyTwoMoreLooks()
    {
        // With no temporary file beside the name a reading looks twice more on Windows, and not again elsewhere: the looks are counted, not timed. A reading also
        // stops looking a tenth of a second after its first look, so one the machine stalled that long may have looked fewer times; such a reading is let
        // through, every other one must have looked exactly so often, and none may ever look more often.
        string none = Path.Combine(_dir, "none.ini");
        int looks = 0;
        AppSettings.MissedOpen = path =>
        {
            if (path == none)
            {
                Interlocked.Increment(ref looks);
            }
        };
        try
        {
            int expected = OperatingSystem.IsWindows() ? 3 : 1;
            int counted = 0;
            var clock = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < 200; i++)
            {
                Interlocked.Exchange(ref looks, 0);
                clock.Restart();
                Assert.Equal(default, AppSettings.Load(none));
                long took = clock.ElapsedMilliseconds;
                int n = Volatile.Read(ref looks);
                Assert.True(n == expected || (n >= 1 && n < expected && took >= 90), $"reading {i} looked {n} times in {took} ms; {expected} expected");
                counted += n == expected ? 1 : 0;
            }

            Assert.True(counted > 0, "every reading was stalled");
            Assert.False(File.Exists(none));
        }
        finally
        {
            AppSettings.MissedOpen = null;
        }
    }

    [Fact]
    public void ReadingsWhileEveryWriteFallsBackOnReplaceAlwaysSeeTheWholeFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // elsewhere a move over an open file goes through: there is no fallback and no instant without a file
        }

        // A third program keeps the file open as a reader does, across each write, so every write waits its moment and then replaces the file with File.Replace,
        // while "the game" reads it over and over without a pause. Before the readings looked again, about nine replaces in ten gave one reading the defaults.
        // A reading stops looking a tenth of a second after its first look and then answers "no file", the defaults: one the machine stalled that long inside
        // a replace proves nothing about the looks and is let through, as in the tests above (one of 944 readings on a machine under load). Every other
        // reading must be the whole file, and a stalled one may be nothing but the defaults.
        Assert.True(AppSettings.Save(SettingsPath, Start()));
        using var stop = new CancellationTokenSource();
        int readings = 0;
        int stalled = 0;
        int wrong = 0;
        string? firstWrong = null;
        var reader = new Thread(() =>
        {
            var clock = new System.Diagnostics.Stopwatch();
            while (!stop.IsCancellationRequested)
            {
                clock.Restart();
                AppSettingsData read = AppSettings.Load(SettingsPath);
                long took = clock.ElapsedMilliseconds;
                readings++;
                if (read with { TrophyNotice = null } == Start())
                {
                    continue;
                }

                if (read == default && took >= 90)
                {
                    stalled++;
                }
                else
                {
                    wrong++;
                    firstWrong ??= $"{read} after {took} ms";
                }
            }
        });
        reader.Start();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bool on = i % 2 == 0;
                    Assert.True(AppSettings.Update(SettingsPath, s => s with { TrophyNotice = on }), $"write {i} was refused");
                }
            }
        }
        finally
        {
            stop.Cancel();
            reader.Join();
        }

        Assert.True(readings > stalled, $"every one of {readings} readings was stalled");
        Assert.True(wrong == 0, $"{wrong} of {readings} readings were not the whole file ({stalled} more were stalled); the first: {firstWrong}");
        Assert.Equal(Start() with { TrophyNotice = false }, AppSettings.Load(SettingsPath));
        Assert.Equal([AppSettings.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void AFileSomebodyHoldsWithoutSharingIsNeitherWrittenOverNorLeftWithATemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // only Windows refuses: elsewhere a rename over an open file goes through
        }

        Assert.True(AppSettings.Save(SettingsPath, Start()));
        string first = File.ReadAllText(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // it cannot be read: the defaults, as before; and a change is not written from those defaults over the keys nobody could read
            Assert.Equal(default, AppSettings.Load(SettingsPath));
            Assert.False(AppSettings.Update(SettingsPath, s => s with { TrophyNotice = false }));
            Assert.False(AppSettings.Save(SettingsPath, new AppSettingsData(PromptSetting.Keyboard)));
            Assert.Equal([AppSettings.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
        }

        Assert.Equal(first, File.ReadAllText(SettingsPath));
        Assert.Equal(Start(), AppSettings.Load(SettingsPath));
    }

    [Fact]
    public void ATemporaryFileAStoppedProgramLeftIsNotWaitedForAndTheNextWriteTakesItsPlace()
    {
        // settings.ini.tmp and no settings.ini: a program stopped between writing and moving. That is "no file": the defaults. While the temporary file is a
        // moment old the reading takes it for a write under way and looks again and again for its tenth of a second (Windows, where a replace has an instant
        // without the file); once it is older it is not waited for: twice more on Windows, not again elsewhere. The looks are counted, not timed (a look
        // yields the processor, and on a busy machine 50 readings took nearly as long as the bound that told them from readings that wait). A reading stops
        // looking once its time is over, so one the machine stalled that long may have looked fewer times and is let through.
        File.WriteAllText(SettingsPath + ".tmp", "prompts=xbox\n");
        int looks = 0;
        int expected = OperatingSystem.IsWindows() ? 3 : 1;
        var clock = new System.Diagnostics.Stopwatch();
        AppSettings.MissedOpen = path =>
        {
            if (path == SettingsPath)
            {
                Interlocked.Increment(ref looks);
            }
        };
        try
        {
            clock.Restart();
            Assert.Equal(default, AppSettings.Load(SettingsPath));
            long waited = clock.ElapsedMilliseconds;
            int looked = Volatile.Read(ref looks);
            Assert.True(OperatingSystem.IsWindows() ? looked > expected || waited >= 90 : looked == expected, $"the reading beside a new temporary file looked {looked} times in {waited} ms");

            File.SetLastWriteTimeUtc(SettingsPath + ".tmp", DateTime.UtcNow.AddMinutes(-10));
            int counted = 0;
            for (int i = 0; i < 50; i++)
            {
                Interlocked.Exchange(ref looks, 0);
                clock.Restart();
                Assert.Equal(default, AppSettings.Load(SettingsPath));
                long took = clock.ElapsedMilliseconds;
                int n = Volatile.Read(ref looks);
                Assert.True(n == expected || (n >= 1 && n < expected && took >= 90), $"reading {i} looked {n} times in {took} ms; {expected} expected");
                counted += n == expected ? 1 : 0;
            }

            Assert.True(counted > 0, "every reading was stalled");
        }
        finally
        {
            AppSettings.MissedOpen = null;
        }

        Assert.True(AppSettings.Update(SettingsPath, s => s with { TrophyNotice = false }));
        Assert.Equal(new AppSettingsData(TrophyNotice: false), AppSettings.Load(SettingsPath));
        Assert.Equal([AppSettings.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
}
