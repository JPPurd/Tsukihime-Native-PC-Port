using Tsuki.Data.Trophies;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>
/// v0.1.9: the pictures the launchers' Achievements page asks its core for, without a game. An icon needs the player's game folder, so here there is none,
/// whatever is handed in; the locked and the blank tile need nothing and are made once. The icons themselves are tested with the game (Tsuki.Tests.Data/Trophies).
/// </summary>
public sealed class AchievementIconTests : IDisposable
{
    private static readonly TrophyGrade[] Grades = [TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-achievementicons-" + Guid.NewGuid().ToString("N"));

    public AchievementIconTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WithoutAGameFolderThereIsNoIconAndNothingIsThrown()
    {
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        // a folder that looks like a game to a first glance and holds rubbish
        string rubbish = Path.Combine(_root, "rubbish");
        Directory.CreateDirectory(Path.Combine(rubbish, "image"));
        File.WriteAllBytes(Path.Combine(rubbish, "eboot.bin"), [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7))]);
        File.WriteAllBytes(Path.Combine(rubbish, "image", "allpac1.hed"), new byte[64]);
        string file = Path.Combine(_root, "a-file.pkg");
        File.WriteAllBytes(file, new byte[16]);

        string?[] folders = [null, "", "   ", Path.Combine(_root, "not-there"), empty, rubbish, file, "\0", new string('x', 400), "C:\\con\\|<>?*"];
        foreach (string? folder in folders)
        {
            for (int id = -1; id <= TrophyTable.Count; id++)
            {
                Assert.Null(Achievements.Icon(folder, id));
            }
        }

        // the same question again, and after a Forget: still no icon
        Assert.Null(Achievements.Icon(empty, 5));
        Achievements.Forget();
        Assert.Null(Achievements.Icon(empty, 5));
        Assert.Null(Achievements.Icon(rubbish, 0));

        // nothing was written into the folders
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(rubbish, "*", SearchOption.AllDirectories).Count(File.Exists));
    }

    [Fact]
    public void AFolderThatIsNoGameIsNotOpenedAgainForEveryIcon()
    {
        // thirty rows ask at once: the folder is looked at once, and then left alone for a while (as the names and cups are)
        string empty = Path.Combine(_root, "none");
        Directory.CreateDirectory(empty);
        Achievements.Forget();
        int before = Achievements.IconFolderOpenings;
        for (int round = 0; round < 300; round++)
        {
            Assert.Null(Achievements.Icon(empty, round % TrophyTable.Count));
        }

        // once; a test of another class that runs beside this one may make the launcher forget meanwhile, which costs one more look each time
        Assert.InRange(Achievements.IconFolderOpenings - before, 1, 8);
    }

    [Fact]
    public void ManyThreadsAskingAtOnceAllGetTheSameAnswer()
    {
        string empty = Path.Combine(_root, "threads");
        Directory.CreateDirectory(empty);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var tiles = new System.Collections.Concurrent.ConcurrentBag<AchievementIcon>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                Assert.Null(Achievements.Icon(i % 2 == 0 ? empty : null, i % TrophyTable.Count));
                tiles.Add(Achievements.LockedIcon(TrophyGrade.Silver));
                if (i % 16 == 0)
                {
                    Achievements.Forget();
                }
            }
            catch (Exception e)
            {
                faults.Enqueue(e);
            }
        });
        Assert.Empty(faults);
        Assert.Equal(64, tiles.Count);
        Assert.All(tiles, t => Assert.Same(Achievements.LockedIcon(TrophyGrade.Silver), t));
    }

    [Fact]
    public void TheLockedAndTheBlankTileAreMadeOncePerGradeAtTheLaunchersSize()
    {
        Assert.Equal(240, TrophyIconArt.LauncherSize);
        foreach (TrophyGrade grade in Grades)
        {
            AchievementIcon locked = Achievements.LockedIcon(grade);
            AchievementIcon blank = Achievements.BlankIcon(grade);
            Assert.Equal(240, locked.Size);
            Assert.Equal(240, blank.Size);
            Assert.Equal(240 * 240 * 4, locked.Rgba.Length);
            Assert.Equal(240 * 240 * 4, blank.Rgba.Length);
            Assert.Equal(TrophyIconArt.Locked(grade, 240), locked.Rgba);
            Assert.Equal(TrophyIconArt.Blank(grade, 240), blank.Rgba);
            Assert.NotSame(locked, blank);

            // kept: the same object every time, so a page can keep one bitmap per tile; a Forget is about the game folder and leaves them
            Assert.Same(locked, Achievements.LockedIcon(grade));
            Assert.Same(blank, Achievements.BlankIcon(grade));
            Achievements.Forget();
            Assert.Same(locked, Achievements.LockedIcon(grade));
            Assert.Same(blank, Achievements.BlankIcon(grade));
        }

        Assert.Equal(4, Grades.Select(Achievements.LockedIcon).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(4, Grades.Select(Achievements.BlankIcon).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => Achievements.LockedIcon((TrophyGrade)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Achievements.BlankIcon((TrophyGrade)(-1)));
    }

    [Fact]
    public void TheEmblemOfAGradeIsMadeOnceAtTheLaunchersSizeAndKeptAcrossForget()
    {
        foreach (TrophyGrade grade in Grades)
        {
            AchievementIcon emblem = Achievements.EmblemIcon(grade);
            Assert.Equal(240, emblem.Size);
            Assert.Equal(TrophyIconArt.Emblem(grade, 240), emblem.Rgba);
            Assert.NotEqual(Achievements.BlankIcon(grade).Rgba, emblem.Rgba);

            // the same object every time, also after a Forget (it needs no game folder), so a page keeps one bitmap per grade
            Assert.Same(emblem, Achievements.EmblemIcon(grade));
            Achievements.Forget();
            Assert.Same(emblem, Achievements.EmblemIcon(grade));
            Assert.NotSame(Achievements.BlankIcon(grade), emblem);
            Assert.NotSame(Achievements.LockedIcon(grade), emblem);
        }

        Assert.Equal(4, Grades.Select(Achievements.EmblemIcon).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => Achievements.EmblemIcon((TrophyGrade)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Achievements.EmblemIcon((TrophyGrade)(-1)));

        // many threads at once, a Forget among them: all get the one emblem
        var seen = new System.Collections.Concurrent.ConcurrentBag<AchievementIcon>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            seen.Add(Achievements.EmblemIcon(TrophyGrade.Gold));
            if (i % 16 == 0)
            {
                Achievements.Forget();
            }
        });
        Assert.All(seen, e => Assert.Same(Achievements.EmblemIcon(TrophyGrade.Gold), e));
    }

    [Fact]
    public void NoWordOfTheLaunchersSpeaksOfCupPictures()
    {
        // the PS4's cup pictures are shown nowhere (the grades have drawn emblems), so no text may promise them
        var words = typeof(LauncherTexts).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (f.Name, Text: (string?)f.GetValue(null) ?? string.Empty))
            .ToList();
        Assert.Contains(words, w => w.Name == nameof(LauncherTexts.AchievementsNoGameNote));
        Assert.All(words, w => Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(w.Text, @"\bcups?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            $"{w.Name} speaks of cups: {w.Text}"));
    }

    [Fact]
    public void TheSharedCodeNoLongerReadsThePs4TrophyFile()
    {
        // D6: nothing shows the PS4's cup pictures, so the shared launcher code reads nothing of sce_sys/trophy, and the reader of that file and its PNG decoder
        // are gone from Tsuki.Data. The game's side is GameAppWiringTests.TheGameNeverOpensThePs4TrophyFile; each launcher's own code has its own test.
        Assert.DoesNotContain(typeof(Achievements).GetMethods(), m => m.Name.Contains("Cup", StringComparison.Ordinal));
        Assert.Null(typeof(TrophyIconArt).Assembly.GetType("Tsuki.Data.Trophies.TrophyPack"));
        Assert.Null(typeof(TrophyIconArt).Assembly.GetType("Tsuki.Data.Trophies.PngReader"));
        Assert.False(File.Exists(Path.Combine(Repo.Src, "Tsuki.Data", "Trophies", "TrophyPack.cs")));
        Assert.False(File.Exists(Path.Combine(Repo.Src, "Tsuki.Data", "Trophies", "PngReader.cs")));
        string[] core = [.. Directory.EnumerateFiles(Path.Combine(Repo.Src, "Tsuki.Launcher", "Core"), "*.cs", SearchOption.AllDirectories)];
        Assert.Contains(core, f => f.EndsWith("Achievements.cs", StringComparison.Ordinal));
        foreach (string file in core)
        {
            string text = File.ReadAllText(file);
            foreach (string gone in new[] { "CupPng", "NoCups", "TrophyPack", "PngReader", "trophy00", ".trp" })
            {
                Assert.False(text.Contains(gone, StringComparison.Ordinal), $"{Path.GetFileName(file)} still names {gone}");
            }
        }
    }
}
