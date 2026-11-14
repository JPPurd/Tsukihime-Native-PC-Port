// v0.1.9, second part: the pictures of the rows of the Windows launcher's Achievements page. AchievementPictures of src/Tsuki.Launcher is compiled into these tests
// because it holds no WPF type: the bitmap is a type parameter, here a stand-in that remembers which pixels it was made from, and the icons come through a seam
// that counts what is asked for (no game folder is ever opened here). The page, the view model and the off-screen render around it are WPF: they are pinned in
// their source and XAML, and the real launcher shows them in its self-test (WindowsAchievementsPageTests.TheRealLauncherDrivesItsAchievementsPageInTheSelfTest).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class WindowsAchievementPicturesTests : IDisposable
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly DateTime When = new(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-wpf-pictures-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private readonly string _folder;
    private readonly IconSource _source = new();
    private readonly ConcurrentQueue<int> _changed = new();
    private int _conversions;

    public WindowsAchievementPicturesTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(_paths.DataDirectory);

        // a game folder only by name: the seam answers for it, nothing looks into it
        _folder = Path.Combine(_root, "CUSA47999");
    }

    public void Dispose()
    {
        _source.ReleaseAll();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the temp folder is the system's to clean
        }
    }

    /// <summary>The stand-in for the toolkit's bitmap: it remembers the pixels it was made from.</summary>
    private sealed class Shot(AchievementIcon from)
    {
        public AchievementIcon From { get; } = from;
    }

    /// <summary>The seam in place of Achievements.Icon: it counts what is asked for, can hold an answer back, and can have no icon for an id.</summary>
    private sealed class IconSource
    {
        private readonly ConcurrentDictionary<int, ManualResetEventSlim> _held = new();

        /// <summary>Every request in the order it came.</summary>
        public ConcurrentQueue<(string? Folder, int Id)> Asked { get; } = new();

        /// <summary>The ids of <see cref="Asked"/>.</summary>
        public IEnumerable<int> Ids => Asked.Select(a => a.Id);

        /// <summary>Ids whose picture is not in the game folder: no icon.</summary>
        public ConcurrentDictionary<int, bool> Missing { get; } = new();

        /// <summary>Ids whose making fails with an exception.</summary>
        public ConcurrentDictionary<int, bool> Faulty { get; } = new();

        /// <summary>A number that goes into the pixels of an id's icon: another number is another picture.</summary>
        public ConcurrentDictionary<int, byte> Version { get; } = new();

        /// <summary>The icon last handed out for an id.</summary>
        public ConcurrentDictionary<int, AchievementIcon> Made { get; } = new();

        /// <summary>From now on a request for this id waits until <see cref="Release"/>.</summary>
        public void Hold(int id) => _held[id] = new ManualResetEventSlim(false);

        public void Release(int id) => _held[id].Set();

        public void ReleaseAll()
        {
            foreach (ManualResetEventSlim gate in _held.Values)
            {
                gate.Set();
            }
        }

        public AchievementIcon? Icon(string? folder, int id)
        {
            Asked.Enqueue((folder, id));
            if (_held.TryGetValue(id, out ManualResetEventSlim? gate) && !gate.Wait(Limit))
            {
                throw new TimeoutException("the test never released the icon of achievement " + id);
            }

            if (Faulty.ContainsKey(id))
            {
                throw new InvalidDataException("a picture nobody can read");
            }

            if (Missing.ContainsKey(id))
            {
                return null;
            }

            // a new object with new arrays every time, as after Achievements.Forget: only the pixels tell whether it is the same picture
            byte version = Version.GetValueOrDefault(id);
            var icon = new AchievementIcon(2, [(byte)id, version, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, (byte)id, version, 12, 255]);
            Made[id] = icon;
            return icon;
        }
    }

    private AchievementPictures<Shot> Pictures()
    {
        var pictures = new AchievementPictures<Shot>(
            icon =>
            {
                _ = Interlocked.Increment(ref _conversions);
                return new Shot(icon);
            },
            _source.Icon);
        pictures.Changed += _changed.Enqueue;
        return pictures;
    }

    /// <summary>The 30 achievements, those of <paramref name="earned"/> earned.</summary>
    private static AchievementRow[] Rows(params int[] earned) =>
        [.. Enumerable.Range(0, TrophyTable.Count).Select(id => new AchievementRow(id, TrophyTable.Grade(id), "name " + id, "detail " + id, earned.Contains(id), null))];

    /// <summary>Writes trophies.ini as the game does, with these ids earned.</summary>
    private void WriteLog(params int[] earned)
    {
        var log = new TrophyLog();
        foreach (int id in earned)
        {
            Assert.True(log.Add(id, When.AddMinutes(id)));
        }

        log.Write(_paths.TrophiesFile);
    }

    /// <summary>Waits without holding a thread of the test runner: the continuations of the code under test may need one.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Limit, "it never happened: " + what);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static Task Done(Task work) => work.WaitAsync(Limit, TestContext.Current.CancellationToken);

    // -- which rows are asked for ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEarnedRowAsksForItsIconAndALockedRowNeverDoes()
    {
        AchievementPictures<Shot> pictures = Pictures();
        AchievementRow[] rows = Rows(1, 5, 14);

        // looking at a row asks for nothing: what it shows is answered from what is there
        foreach (AchievementRow row in rows)
        {
            _ = pictures.Of(row);
        }

        Assert.Empty(_source.Asked);
        Assert.Equal(0, pictures.Requests);

        await Done(pictures.Request(_folder, rows));

        // the three earned ones, in the list's order, each once, from the game folder that was named; no other id
        Assert.Equal([1, 5, 14], _source.Ids);
        Assert.All(_source.Asked, a => Assert.Equal(_folder, a.Folder));
        Assert.Equal(3, pictures.Requests);
        Assert.All(rows, row => Assert.Equal(row.Earned ? 1 : 0, pictures.RequestsOf(row.Id)));
        Assert.Equal(0, pictures.RequestsOf(-1));
        Assert.Equal(0, pictures.RequestsOf(TrophyTable.Count));

        // a locked row shows the locked tile of its grade: drawn by code, one object per grade, and never a picture of the game
        AchievementRow[] locked = [.. rows.Where(r => !r.Earned)];
        Assert.Equal(27, locked.Length);
        foreach (AchievementRow row in locked)
        {
            (AchievementArt art, Shot? tile) = pictures.Of(row);
            Assert.Equal(AchievementArt.Locked, art);
            Assert.NotNull(tile);
            Assert.Same(Achievements.LockedIcon(row.Grade), tile.From);
            Assert.Same(tile, pictures.Of(row).Picture);
        }

        Assert.Equal(4, locked.Select(r => r.Grade).Distinct().Count());
        Assert.Equal(4, locked.Select(r => pictures.Of(r).Picture).Distinct().Count());

        // asked again and again, as every reading and every change of tab does: nothing more is asked for
        for (int poll = 0; poll < 4; poll++)
        {
            await Done(pictures.Request(_folder, rows));
            await Done(pictures.Request(_folder, locked));
        }

        Assert.Equal([1, 5, 14], _source.Ids);

        // a profile with nothing earned asks for nothing at all
        AchievementPictures<Shot> none = Pictures();
        Task work = none.Request(_folder, Rows());
        Assert.True(work.IsCompleted);
        Assert.Equal(0, none.Requests);
        Assert.Equal([1, 5, 14], _source.Ids);
    }

    [Fact]
    public async Task ManyRequestsAtOnceAskForEachIconOnce()
    {
        AchievementPictures<Shot> pictures = Pictures();
        AchievementRow[] rows = Rows(1, 2, 3, 4, 5, 9, 14, 15, 18, 22, 26, 28, 29);
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 6 }, round =>
        {
            _ = pictures.Request(_folder, rows);
            _ = pictures.Of(rows[5]);
            _ = pictures.Of(rows[6]);
        });
        await Done(pictures.WhenIdle());

        Assert.Equal([1, 2, 3, 4, 5, 9, 14, 15, 18, 22, 26, 28, 29], _source.Ids.Order());
        Assert.Equal(13, pictures.Requests);
        Assert.All(rows.Where(r => r.Earned), r => Assert.Equal(AchievementArt.Icon, pictures.Of(r).Art));
        Assert.Equal(13, _changed.Count);
        Assert.Equal(13, _changed.Distinct().Count());
    }

    // -- the blank tile, then the icon ----------------------------------------------------------------------------------

    [Fact]
    public async Task ARowShowsTheBlankTileUntilItsIconArrivesAndThenTheIcon()
    {
        AchievementPictures<Shot> pictures = Pictures();
        AchievementRow[] rows = Rows(1, 14);
        _source.Hold(1);
        _source.Hold(14);

        // earned, and nobody asked yet: already the blank tile, not the emblem
        Assert.Equal(AchievementArt.Blank, pictures.Of(rows[1]).Art);

        Task work = pictures.Request(_folder, rows);
        await Until(() => _source.Asked.Count == 1, "the worker asks for the first icon");
        Assert.False(work.IsCompleted);
        foreach (int id in new[] { 1, 14 })
        {
            (AchievementArt art, Shot? tile) = pictures.Of(rows[id]);
            Assert.Equal(AchievementArt.Blank, art);
            Assert.NotNull(tile);
            Assert.Same(Achievements.BlankIcon(rows[id].Grade), tile.From);         // the grade's blank tile (bronze, gold): its ring, no lock, no picture
            Assert.Same(tile, pictures.Of(rows[id]).Picture);
        }

        Assert.NotSame(pictures.Of(rows[1]).Picture, pictures.Of(rows[14]).Picture);
        Assert.Empty(_changed);

        // the first icon arrives: its row shows it and the page is told; the second is asked for only now (one at a time) and its row still waits
        _source.Release(1);
        await Until(() => _source.Asked.Count == 2, "the worker asks for the second icon");
        Assert.Equal([1], _changed);
        (AchievementArt first, Shot? icon) = pictures.Of(rows[1]);
        Assert.Equal(AchievementArt.Icon, first);
        Assert.NotNull(icon);
        Assert.Same(_source.Made[1], icon.From);
        Assert.Equal(AchievementArt.Blank, pictures.Of(rows[14]).Art);
        Assert.False(work.IsCompleted);

        _source.Release(14);
        await Done(work);
        Assert.Equal([1, 14], _changed);
        (AchievementArt second, Shot? gold) = pictures.Of(rows[14]);
        Assert.Equal(AchievementArt.Icon, second);
        Assert.Same(_source.Made[14], gold!.From);

        // from now on the row is handed the same object every time
        Assert.Same(icon, pictures.Of(rows[1]).Picture);
        Assert.Same(gold, pictures.Of(rows[14]).Picture);
        await Done(pictures.WhenIdle());
    }

    // -- the emblem when there can be no icon ---------------------------------------------------------------------------

    /// <summary>What a row shows when its icon cannot be made: the emblem of its grade, drawn by code, the very bitmap the summary card shows for that grade.</summary>
    private static void AssertEmblem(AchievementPictures<Shot> pictures, AchievementRow row)
    {
        (AchievementArt art, Shot? emblem) = pictures.Of(row);
        Assert.Equal(AchievementArt.Emblem, art);
        Assert.NotNull(emblem);
        Assert.Same(Achievements.EmblemIcon(row.Grade), emblem.From);
        Assert.Same(pictures.Emblem(row.Grade), emblem);
    }

    [Fact]
    public async Task ARowShowsTheEmblemOfItsGradeWhenItsIconCannotBeMade()
    {
        AchievementPictures<Shot> pictures = Pictures();
        AchievementRow[] rows = Rows(1, 2, 3, 14);
        _source.Missing[2] = true;          // its picture is not in this game folder
        _source.Faulty[3] = true;           // whatever goes wrong costs only the icon
        _source.Missing[14] = true;

        await Done(pictures.Request(_folder, rows));
        Assert.Equal(AchievementArt.Icon, pictures.Of(rows[1]).Art);
        AssertEmblem(pictures, rows[2]);
        AssertEmblem(pictures, rows[3]);
        AssertEmblem(pictures, rows[14]);
        Assert.Same(pictures.Of(rows[2]).Picture, pictures.Of(rows[3]).Picture);          // two silver ones: one bitmap
        Assert.NotSame(pictures.Of(rows[2]).Picture, pictures.Of(rows[14]).Picture);      // silver and gold: two
        Assert.Equal([1, 2, 3, 14], _changed);              // the page is told of each, to put the emblem in place of the blank tile

        // it is not tried again at every poll
        await Done(pictures.Request(_folder, rows));
        await Done(pictures.Request(_folder, rows));
        Assert.Equal([1, 2, 3, 14], _source.Ids);

        // no game folder at all: nothing is asked for, and every earned row has the emblem at once (no blank tile first)
        foreach (string? none in new[] { null, string.Empty, "   " })
        {
            AchievementPictures<Shot> without = Pictures();
            Task work = without.Request(none, rows);
            Assert.True(work.IsCompleted);
            Assert.All(rows.Where(r => r.Earned), r => AssertEmblem(without, r));
            Assert.All(rows.Where(r => !r.Earned), r => Assert.Equal(AchievementArt.Locked, without.Of(r).Art));
            Assert.Equal(0, without.Requests);

            // the player then chooses the game folder: the icons are asked for
            await Done(without.Request(_folder, rows));
            Assert.Equal(4, without.Requests);
            Assert.Equal(AchievementArt.Icon, without.Of(rows[1]).Art);
            AssertEmblem(without, rows[2]);
        }

        Assert.Equal([1, 2, 3, 14, 1, 2, 3, 14, 1, 2, 3, 14, 1, 2, 3, 14], _source.Ids);
    }

    [Fact]
    public void TheEmblemOfAGradeIsOneBitmapMadeOnceFromTheDrawnEmblem()
    {
        AchievementPictures<Shot> pictures = Pictures();

        // the summary card asks for the four grades' emblems at every reading: each is turned into a bitmap once, and the same object comes back
        Shot[] first = [.. Enum.GetValues<TrophyGrade>().Select(pictures.Emblem)];
        Assert.Equal(4, _conversions);
        for (int reading = 0; reading < 3; reading++)
        {
            Assert.Equal(first, Enum.GetValues<TrophyGrade>().Select(pictures.Emblem));
        }

        Assert.Equal(4, _conversions);
        Assert.Equal(4, first.Distinct().Count());
        Assert.All(Enum.GetValues<TrophyGrade>(), g => Assert.Same(Achievements.EmblemIcon(g), pictures.Emblem(g).From));

        // drawn by code at the pages' size, in the tiles' frame; it is neither the locked nor the blank tile, and needs no game folder
        Assert.All(Enum.GetValues<TrophyGrade>(), g =>
        {
            AchievementIcon emblem = pictures.Emblem(g).From;
            Assert.Equal(240, emblem.Size);
            Assert.Equal(240 * 240 * 4, emblem.Rgba.Length);
            Assert.False(emblem.Rgba.AsSpan().SequenceEqual(Achievements.BlankIcon(g).Rgba));
            Assert.False(emblem.Rgba.AsSpan().SequenceEqual(Achievements.LockedIcon(g).Rgba));
        });
        Assert.Empty(_source.Asked);

        // Check again leaves it alone (it is not of the game folder)
        pictures.Forget();
        Assert.Same(first[0], pictures.Emblem(Enum.GetValues<TrophyGrade>()[0]));
        Assert.Equal(4, _conversions);
    }

    // -- a poll that changed nothing ------------------------------------------------------------------------------------

    [Fact]
    public async Task APollThatChangedNothingKeepsTheBitmapOfEveryRow()
    {
        WriteLog(1, 2, 5, 14);
        var state = new AchievementsState(_paths);
        Assert.True(state.Refresh(_folder));
        AchievementPictures<Shot> pictures = Pictures();
        await Done(pictures.Request(_folder, state.Summary.Rows));
        Shot?[] before = [.. state.Summary.Rows.Select(r => pictures.Of(r).Picture)];
        Assert.All(before, shot => Assert.NotNull(shot));
        Assert.Equal([1, 2, 5, 14], _source.Ids);
        int made = _conversions;
        Assert.Equal(4 + 4, made);              // four icons, and the locked tiles of the four grades; a blank tile was never looked at

        // the 2 s poll: the files did not change, nothing is read
        Assert.False(state.Refresh(_folder));
        Assert.Equal(1, state.Readings);

        // the game writes the same file again: it is read, and says what it said
        File.SetLastWriteTimeUtc(_paths.TrophiesFile, File.GetLastWriteTimeUtc(_paths.TrophiesFile).AddSeconds(10));
        Assert.True(state.Due(_folder, out _));
        Assert.False(state.Refresh(_folder));
        Assert.Equal(2, state.Readings);

        // what the page does after a reading, and at every change of tab: no icon is asked for again and no bitmap is made again
        for (int poll = 0; poll < 5; poll++)
        {
            await Done(pictures.Request(_folder, state.Summary.Rows));
            foreach (AchievementFilter tab in AchievementsState.Filters)
            {
                state.Filter = tab;
                Assert.All(state.Visible, row => Assert.Same(before[row.Id], pictures.Of(row).Picture));
            }
        }

        Assert.Equal([1, 2, 5, 14], _source.Ids);
        Assert.Equal(made, _conversions);
        Assert.Empty(_changed.Skip(4));
    }

    // -- Check again, another game folder -------------------------------------------------------------------------------

    [Fact]
    public async Task CheckAgainHasTheIconsMadeAgainAndARowKeepsItsPictureMeanwhile()
    {
        AchievementPictures<Shot> pictures = Pictures();
        AchievementRow[] rows = Rows(1, 5);
        await Done(pictures.Request(_folder, rows));
        Shot one = pictures.Of(rows[1]).Picture!;
        Shot five = pictures.Of(rows[5]).Picture!;
        int made = _conversions;

        // Check again (or the game's end): the launcher forgets what it kept of the game folder. The folder was repaired meanwhile: achievement 5's picture is another.
        _source.Version[5] = 9;
        _source.Hold(1);
        pictures.Forget();
        Assert.Same(one, pictures.Of(rows[1]).Picture);         // forgetting alone changes nothing on the page
        Assert.Equal([1, 5], _source.Ids);
        Task work = pictures.Request(_folder, rows);
        await Until(() => _source.Asked.Count == 3, "the worker asks for the first icon again");

        // while they are made again the rows show what they showed: no blank tile flashes by
        Assert.Equal((AchievementArt.Icon, one), pictures.Of(rows[1]));
        Assert.Equal((AchievementArt.Icon, five), pictures.Of(rows[5]));
        _source.Release(1);
        await Done(work);
        Assert.Equal([1, 5, 1, 5], _source.Ids);
        Assert.Equal(2, pictures.RequestsOf(1));
        Assert.Equal(2, pictures.RequestsOf(5));

        // the same pixels keep the bitmap (the same object, nothing was made); other pixels are a new bitmap
        Assert.Same(one, pictures.Of(rows[1]).Picture);
        Shot repaired = pictures.Of(rows[5]).Picture!;
        Assert.NotSame(five, repaired);
        Assert.Same(_source.Made[5], repaired.From);
        Assert.Equal(made + 1, _conversions);

        // an icon that cannot be made any more (the picture is gone from the folder): the emblem takes its place
        _source.Missing[1] = true;
        pictures.Forget();
        await Done(pictures.Request(_folder, rows));
        AssertEmblem(pictures, rows[1]);
        Assert.Same(repaired, pictures.Of(rows[5]).Picture);

        // another game folder: nothing of the first folder's pictures stays on show while the new ones are made
        string other = Path.Combine(_root, "another", "CUSA47999");
        _source.Missing.Clear();
        _source.Hold(1);
        Task second = pictures.Request(other, rows);
        await Until(() => _source.Asked.Count == 7, "the worker asks the other folder");
        Assert.Equal(AchievementArt.Blank, pictures.Of(rows[1]).Art);
        Assert.Equal(AchievementArt.Blank, pictures.Of(rows[5]).Art);
        _source.Release(1);
        await Done(second);
        Assert.Equal([(other, 1), (other, 5)], _source.Asked.Skip(6));
        Assert.Equal(AchievementArt.Icon, pictures.Of(rows[1]).Art);
        Assert.NotSame(repaired, pictures.Of(rows[5]).Picture);
    }

    [Fact]
    public async Task AnIconForgottenWhileItIsMadeIsDroppedAndMadeAgain()
    {
        AchievementPictures<Shot> pictures = Pictures();
        AchievementRow[] rows = Rows(1);
        _source.Hold(1);
        Task first = pictures.Request(_folder, rows);
        await Until(() => _source.Asked.Count == 1, "the worker asks for the icon");

        // Check again while the worker is busy: what it brings back is of the folder as it was, and is not shown
        pictures.Forget();
        Task again = pictures.Request(_folder, rows);
        Assert.Equal(AchievementArt.Blank, pictures.Of(rows[1]).Art);
        _source.Release(1);
        await Done(again);
        await Done(first);
        Assert.Equal([1, 1], _source.Ids);
        Assert.Equal([1], _changed);                    // the page was told once: of the icon made after the forgetting
        Assert.Same(_source.Made[1], pictures.Of(rows[1]).Picture!.From);
        await Done(pictures.WhenIdle());
    }

    // -- an achievement earned while the page is open -------------------------------------------------------------------

    [Fact]
    public async Task ARowThatBecomesEarnedWhileThePageIsOpenGetsItsIcon()
    {
        WriteLog(1, 2);
        var state = new AchievementsState(_paths);
        Assert.True(state.Refresh(_folder));
        AchievementPictures<Shot> pictures = Pictures();
        await Done(pictures.Request(_folder, state.Summary.Rows));
        Shot?[] before = [.. state.Summary.Rows.Select(r => pictures.Of(r).Picture)];
        Assert.Equal(AchievementArt.Locked, pictures.Of(state.Summary.Rows[9]).Art);
        Assert.Equal([1, 2], _source.Ids);

        // the game earns achievement 9; the page's poll finds the changed file and reads it
        WriteLog(1, 2, 9);
        Assert.True(state.Refresh(_folder));
        AchievementRow nine = state.Summary.Rows[9];
        Assert.True(nine.Earned);
        _source.Hold(9);
        Task work = pictures.Request(_folder, state.Summary.Rows);
        await Until(() => _source.Asked.Count == 3, "the worker asks for the new icon");

        // until it arrives: the blank tile of its grade (silver), not the locked tile it had and not the emblem
        (AchievementArt waiting, Shot? tile) = pictures.Of(nine);
        Assert.Equal(AchievementArt.Blank, waiting);
        Assert.Same(Achievements.BlankIcon(TrophyGrade.Silver), tile!.From);
        _source.Release(9);
        await Done(work);

        // only the new one was asked for; it shows its icon, and every other row kept its bitmap
        Assert.Equal([1, 2, 9], _source.Ids);
        Assert.Equal([1, 2, 9], _changed);
        (AchievementArt art, Shot? icon) = pictures.Of(nine);
        Assert.Equal(AchievementArt.Icon, art);
        Assert.Same(_source.Made[9], icon!.From);
        Assert.All(state.Summary.Rows.Where(r => r.Id != 9), row => Assert.Same(before[row.Id], pictures.Of(row).Picture));
    }

    // -- the tiles ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTwelveTilesAreDrawnOnceAndNeedNoGameFolder()
    {
        // the locked and the blank tile and the emblem of each of the four grades
        AchievementPictures<Shot> pictures = Pictures();
        pictures.MakeTiles();
        Assert.Equal(12, _conversions);
        pictures.MakeTiles();
        AchievementRow[] rows = Rows(1, 5, 14);
        foreach (AchievementRow row in rows)
        {
            (AchievementArt art, Shot? tile) = pictures.Of(row);
            Assert.Equal(row.Earned ? AchievementArt.Blank : AchievementArt.Locked, art);
            Assert.Same(row.Earned ? Achievements.BlankIcon(row.Grade) : Achievements.LockedIcon(row.Grade), tile!.From);
        }

        Assert.All(Enum.GetValues<TrophyGrade>(), g => Assert.Same(Achievements.EmblemIcon(g), pictures.Emblem(g).From));

        // looking at the rows and the card drew nothing more, and Check again leaves the tiles alone (they are not of the game folder)
        Assert.Equal(12, _conversions);
        Shot locked = pictures.Of(rows[0]).Picture!;
        pictures.Forget();
        Assert.Same(locked, pictures.Of(rows[0]).Picture);
        Assert.Equal(12, _conversions);
        Assert.Empty(_source.Asked);
        Assert.True(pictures.Work.IsCompleted);
    }

    // -- the page, the view model and the render (WPF: pinned in their source) ------------------------------------------

    private static string LauncherSource(params string[] parts) => Path.Combine([global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", .. parts]);

    [Fact]
    public void TheRowsSquareIsTheRowsBitmapScaledDownWithHighQuality()
    {
        XDocument page = XDocument.Load(LauncherSource("Views", "AchievementsPage.xaml"));
        string? Attr(XElement e, string name) => (string?)e.Attribute(name);
        XElement template = page.Descendants(Xaml + "DataTemplate").Single(t => (string?)t.Attribute(X + "Key") == "AchievementRowTemplate");

        // one square per row: the row's bitmap (icon, tile or emblem), 240 px scaled down to the square with the high-quality filter, so the grade's ring in it
        // stays even at every display scale
        XElement image = template.Descendants(Xaml + "Image").Single();
        Assert.Equal("{Binding Picture}", Attr(image, "Source"));
        Assert.Equal("Uniform", Attr(image, "Stretch"));
        Assert.Equal("HighQuality", Attr(image, "RenderOptions.BitmapScalingMode"));
        XElement square = image.Parent!;
        Assert.Equal(Xaml + "Grid", square.Name);
        Assert.InRange(int.Parse(Attr(square, "Width")!, System.Globalization.CultureInfo.InvariantCulture), 56, 64);
        Assert.Equal(Attr(square, "Width"), Attr(square, "Height"));
        Assert.Equal("Center", Attr(square, "VerticalAlignment"));

        // the square and its margins are whole and even numbers of units, so its edges fall on whole pixels at 100, 150 and 200 %
        int side = int.Parse(Attr(square, "Width")!, System.Globalization.CultureInfo.InvariantCulture);
        int rowHeight = int.Parse(Attr(square.Parent!, "MinHeight")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, side % 4);
        Assert.Equal(0, (rowHeight - side) % 4);
        Assert.InRange(rowHeight - side, 12, 24);

        // the square is that one picture: nothing is drawn over it or in its place (the emblem is a bitmap like the tiles)
        Assert.Single(square.Elements());
        Assert.Empty(template.Descendants(Xaml + "ContentControl"));

        // a locked row: its words are dimmed as before; its square is not (the locked tile is its own picture, the grey cup is gone)
        XElement locked = template.Descendants(Xaml + "DataTrigger").Single(t => Attr(t, "Binding") == "{Binding Earned}" && Attr(t, "Value") == "False");
        Assert.Contains(locked.Elements(Xaml + "Setter"), s => Attr(s, "TargetName") == "NameText" && Attr(s, "Property") == "Foreground");
        Assert.DoesNotContain(template.Descendants(Xaml + "Setter"), s => Attr(s, "Property") == "Opacity");
        Assert.Single(template.Descendants(Xaml + "DataTrigger"));

        // the summary card's four grades: each its emblem, a 240 px bitmap scaled down with the same high-quality filter as the rows' squares
        XElement grades = page.Descendants(Xaml + "ItemsControl").Single(l => Attr(l, "ItemsSource") == "{Binding AchievementGrades}");
        XElement emblem = grades.Descendants(Xaml + "Image").Single();
        Assert.Equal("{Binding Picture}", Attr(emblem, "Source"));
        Assert.Equal("Uniform", Attr(emblem, "Stretch"));
        Assert.Equal("HighQuality", Attr(emblem, "RenderOptions.BitmapScalingMode"));
        Assert.Equal(Attr(emblem, "Width"), Attr(emblem, "Height"));
        Assert.Equal("{x:Type local:AchievementGradeView}", Attr(grades.Descendants(Xaml + "DataTemplate").Single(), "DataType"));

        // the row's view: the bitmap is set in place and tells the binding, so an icon that arrives redraws one square and not the list
        string model = File.ReadAllText(LauncherSource("LauncherModel.Achievements.cs"));
        Assert.Contains("public sealed class AchievementRowView(AchievementRow row) : INotifyPropertyChanged", model, StringComparison.Ordinal);
        Assert.Contains("grade.Picture = _achievementPicturesWanted ? _pictures.Emblem(grade.Grade) : null;", model, StringComparison.Ordinal);
        Assert.Contains("if (_art == art && ReferenceEquals(_picture, picture))", model, StringComparison.Ordinal);
        Assert.Contains("PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));", model, StringComparison.Ordinal);
        Assert.DoesNotContain("Grey", model, StringComparison.Ordinal);

        // the bitmap: straight alpha premultiplied into a frozen Pbgra32 picture, which the worker thread may make
        string art = File.ReadAllText(LauncherSource("LauncherArt.cs"));
        string bitmap = art[art.IndexOf("public static BitmapSource IconBitmap(AchievementIcon icon)", StringComparison.Ordinal)..];
        bitmap = bitmap[..bitmap.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("bgra[i] = (byte)(((rgba[i + 2] * a) + 127) / 255);", bitmap, StringComparison.Ordinal);
        Assert.Contains("bgra[i + 2] = (byte)(((rgba[i] * a) + 127) / 255);", bitmap, StringComparison.Ordinal);
        Assert.Contains("PixelFormats.Pbgra32", bitmap, StringComparison.Ordinal);
        Assert.Contains("bitmap.Freeze();", bitmap, StringComparison.Ordinal);
        Assert.Contains("private readonly AchievementPictures<BitmapSource> _pictures = new(LauncherArt.IconBitmap);", model, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIconsAreMadeFromTheFirstShowingOfThePageAndForgottenWithTheGameFolder()
    {
        string model = File.ReadAllText(LauncherSource("LauncherModel.cs"));
        string partial = File.ReadAllText(LauncherSource("LauncherModel.Achievements.cs"));
        static string Body(string source, string signature)
        {
            string from = source[source.IndexOf(signature, StringComparison.Ordinal)..];
            return from[..(from.IndexOf("\n    }", StringComparison.Ordinal) + 1)];
        }

        // nothing at the launcher's start: the rows get pictures, and icons are asked for, only once the page was shown (the Page setter says when)
        string page = Body(model, "public LauncherPage Page");
        Assert.Contains("if (value == LauncherPage.Achievements && _page != value)", page, StringComparison.Ordinal);
        Assert.Contains("OpenAchievements();", page, StringComparison.Ordinal);
        string open = Body(partial, "private void OpenAchievements()");
        Assert.Contains("_achievementPicturesWanted = true;", open, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(_pictures.MakeTiles);", open, StringComparison.Ordinal);          // the tiles are drawn on a worker thread too
        Assert.Contains("ShowAchievements();", open, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(partial, @"_achievementPicturesWanted = true;"));
        Assert.DoesNotContain("_achievementPicturesWanted", model, StringComparison.Ordinal);

        // from then on every reading the page shows asks for the icons of what is earned (the class asks only for the new ones), with the game folder of the moment
        string show = Body(partial, "private void ShowAchievements()");
        Assert.Contains("if (_achievementPicturesWanted)", show, StringComparison.Ordinal);
        Assert.Contains("_ = _pictures.Request(AchievementsFolder, _achievements.Summary.Rows);", show, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(partial, @"_pictures\.Request\("));
        string picture = Body(partial, "private void ShowPicture(AchievementRowView view)");
        Assert.Contains("if (!_achievementPicturesWanted)", picture, StringComparison.Ordinal);
        Assert.Contains("view.Show(art, picture);", picture, StringComparison.Ordinal);
        Assert.Contains("_pictures.Changed += OnAchievementIcon;", model, StringComparison.Ordinal);

        // the 2 s poll shows nothing when nothing changed; Check again, a chosen folder and the game's end forget the icons with the rest and show the page again
        string load = Body(partial, "public Task LoadAchievementsAsync()");
        Assert.Contains("Achievements.Forget();", load, StringComparison.Ordinal);
        Assert.Contains("_pictures.Forget();", load, StringComparison.Ordinal);
        Assert.Contains("return ReadAchievementsAsync(force: true);", load, StringComparison.Ordinal);
        Assert.Contains("if (changed || force)", Body(partial, "private async Task<bool> ReadAchievementsAsync(bool force)"), StringComparison.Ordinal);

        // the off-screen render takes the page's pictures when the icons are there, not of the blank tiles; and it draws the three tabs at both sizes
        string render = File.ReadAllText(LauncherSource("OffscreenRender.cs"));
        int shown = render.IndexOf("model.Page = item.Page;", StringComparison.Ordinal);
        int waits = render.IndexOf("await model.AchievementPicturesSettledAsync();", shown, StringComparison.Ordinal);
        int draws = render.IndexOf("await DrawAsync(shell, Path.Combine(outDirectory, name + \".png\"));", shown, StringComparison.Ordinal);
        Assert.True(shown >= 0 && waits > shown && draws > waits, "the render waits for the pictures before it draws the page");
        string settled = Body(partial, "public async Task AchievementPicturesSettledAsync()");
        Assert.Contains("await _achievementPicturesStart;", settled, StringComparison.Ordinal);
        Assert.Contains("await _pictures.WhenIdle();", settled, StringComparison.Ordinal);
        Assert.Contains("string picture = \"achievements_\" + tab.Filter.ToString().ToLowerInvariant() + \"_min.png\";", render, StringComparison.Ordinal);
        Assert.Contains("await DrawAsync(shell, Path.Combine(outDirectory, picture), MainWindowMinWidth, minHeight);", render, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPictureOfThePs4TrophyFileIsShownAnywhereOnTheWindowsLauncher()
    {
        // D6 (the owner, 2026-11-12): the PS4's four cup pictures go from every place. The Windows launcher reads nothing of the trophy file and draws no cup of
        // its own in markup: the grades are the emblems Core draws (Achievements.EmblemIcon), bitmaps like the tiles. Core is the shared code; this is the rest.
        string[] files = [.. Directory.EnumerateFiles(LauncherSource(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Core" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
        Assert.Contains(files, f => f.EndsWith("AchievementsPage.xaml", StringComparison.Ordinal));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            // the names of every way a cup picture came in (Core's CupPngs and NoCups, both launchers' DecodeCup and LoadCups, the game's TrophyCups) and the file itself
            foreach (string gone in new[] { "CupPng", "NoCups", "DecodeCup", "LoadCups", "TrophyCups", "TrophyPack", "PngReader", "trophy00", ".trp", "sce_sys/trophy", "DrawnCup", "CupArt", "CupColors", "AchievementCup", "AchievementArt.Cup" })
            {
                Assert.False(text.Contains(gone, StringComparison.Ordinal), $"{Path.GetFileName(file)} still names {gone}");
            }
        }

        // the page draws no picture of its own in markup: no vector path, canvas or geometry; every picture is a bound bitmap
        XDocument page = XDocument.Load(LauncherSource("Views", "AchievementsPage.xaml"));
        Assert.DoesNotContain(page.Descendants(), e => e.Name.LocalName is "Path" or "Canvas" or "Viewbox" or "ControlTemplate" or "Geometry" or "PathGeometry");
        Assert.All(page.Descendants(Xaml + "Image"), i => Assert.Equal("{Binding Picture}", (string?)i.Attribute("Source")));
        Assert.Equal(2, page.Descendants(Xaml + "Image").Count());          // a row's square and a grade's emblem
    }
}
