using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Data.Source;

namespace Tsuki.Tests.Data.Trophies;

/// <summary>
/// v0.1.9 on the real game (read only): the eboot gives the 27 scenes of the chapter trophies and a chapter title for each in the four languages; and the
/// launcher's page, given the game folder, carries the game's own English chapter names and reads the chapter achievements out of a save. Nothing of the game
/// is written here: names are compared with what the eboot holds at run time. (The PS4's trophy file is not read by the port any more: D6.)
/// </summary>
[Collection(TrophyIconRealDataTests.LauncherCacheCollection)]
public sealed class TrophyRealDataTests : IDisposable
{
    // The 27 records of the trophy scene table of the retail program (text 0x221400): record i is the flowchart scene whose completion earns trophy i + 1.
    private static readonly int[] RetailScenes =
        [335, 15, 357, 332, 492, 107, 118, 141, 154, 172, 443, 445, 349, 14, 391, 397, 405, 414, 422, 431, 327, 274, 456, 463, 480, 300, 309];

    // The day each chapter trophy's title starts with: days 1..14 of the first side, then days 5..13 of the other side, its two day 14s and its two day 15s.
    private static readonly int[] Days = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 14, 15, 15];

    private static readonly int[] Lessons = [.. Enumerable.Range(533, 25)];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-trophydata-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public TrophyRealDataTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(_paths.DataDirectory);
        Achievements.Forget();
    }

    public void Dispose()
    {
        Achievements.Forget();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSave(IEnumerable<int> done, IEnumerable<int> seen)
    {
        // scene_done(n) is bit n % 16 of G[300 + n / 16], scene_seen(n) the same bit of G[110 + n / 16]
        var store = new SaveStore();
        foreach ((int firstWord, IEnumerable<int> nodes) in new[] { (300, done), (110, seen) })
        {
            foreach (int n in nodes)
            {
                int word = firstWord + (n / 16);
                store.SystemData.Block.SetG(word, (ushort)(store.SystemData.Block.GetG(word) | (1 << (n % 16))));
            }
        }

        SaveFile.WriteAtomic(_paths.SaveFile, store);
    }

    private static int[] EarnedIds(AchievementSummary summary) => [.. summary.Rows.Where(r => r.Earned).Select(r => r.Id)];

    // -- the eboot --------------------------------------------------------------------------------------------------------

    [GameDataFact]
    public void TheEbootsTrophyTableIsThe27RetailScenesInOrder()
    {
        IReadOnlyList<int> scenes = SharedSource.Source.Eboot.GameFlow.TrophyScenes;
        Assert.Equal(RetailScenes, scenes);

        // each is a flowchart node of its own, none of them a lesson: the chapter table and the lesson branch never meet
        Assert.Equal(27, scenes.Distinct().Count());
        Assert.All(scenes, scene => Assert.InRange(scene, 1, 0x23F));
        Assert.DoesNotContain(scenes, scene => scene is >= TrophyTable.LessonNodeFirst and <= TrophyTable.LessonNodeLast);

        // the rules take a table only when it has the game's 27 records (FIXES D11 G-4): this one is taken
        var rules = new TrophyRules(scenes);
        Assert.Equal(TrophyTable.LastChapter, scenes.Count);
        Assert.True(rules.HasTable);
        for (int i = 0; i < RetailScenes.Length; i++)
        {
            int scene = RetailScenes[i];
            Assert.Equal(i + 1, rules.NodeCompleted(scene, archiveMode: false, n => n == scene));
            Assert.Null(rules.NodeCompleted(scene, archiveMode: true, n => n == scene));
            Assert.Equal([i + 1], rules.Holding(n => n == scene, _ => false));
        }

        Assert.Equal(Enumerable.Range(0, 30), rules.Holding(n => RetailScenes.Contains(n) || Lessons.Contains(n), n => n == 533));
    }

    [GameDataFact]
    public void EveryChapterTitleIsThereInTheFourLanguagesAndStartsWithItsDayNumber()
    {
        IUiTables ui = SharedSource.Source.Eboot.Ui;
        foreach (Language language in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            string[] names = new string[TrophyTable.LastChapter + 1];
            for (int id = TrophyTable.FirstChapter; id <= TrophyTable.LastChapter; id++)
            {
                string what = $"{language}, trophy {id}";
                string text = ui.Text(language, TrophyTable.ChapterTextBase + TrophyTable.ChapterPhase(id));
                Assert.False(string.IsNullOrWhiteSpace(text), what);

                // "NN title": two ASCII digits (the day), one space, the title, no line break mark
                Assert.True(text.Length > 3 && char.IsAsciiDigit(text[0]) && char.IsAsciiDigit(text[1]) && text[2] == ' ', what);
                Assert.True(Days[id - 1] == int.Parse(text[..2], System.Globalization.CultureInfo.InvariantCulture), $"{what}: day {text[..2]}");
                Assert.DoesNotContain('^', text);

                string name = TrophyTable.ChapterName(text);
                Assert.Equal(text[3..].Trim(), name);
                Assert.False(string.IsNullOrWhiteSpace(name), what);
                Assert.False(char.IsDigit(name[0]), what);
                Assert.InRange(name.Length, 1, 34);
                names[id] = name;
            }

            // the two day 14s of the second side carry one title; every other chapter has its own
            Assert.Equal(names[24], names[25]);
            Assert.Equal(26, names[1..].Distinct(StringComparer.Ordinal).Count());
        }

        // the other three trophies have no chapter
        Assert.All(new[] { 0, 28, 29 }, id => Assert.Equal(0, TrophyTable.ChapterPhase(id)));
    }

    // -- the launcher's page with a game folder ---------------------------------------------------------------------------

    [GameDataFact]
    public void WithTheGameFolderTheRowsCarryTheGamesEnglishChapterNames()
    {
        string game = GameFolder.Require();
        AchievementSummary summary = Achievements.Read(_paths, game);
        Assert.True(summary.GameNames);
        Assert.False(summary.Damaged);
        Assert.Equal(30, summary.Rows.Count);
        Assert.Empty(EarnedIds(summary));

        IUiTables ui = SharedSource.Source.Eboot.Ui;
        for (int id = TrophyTable.FirstChapter; id <= TrophyTable.LastChapter; id++)
        {
            string title = ui.Text(Language.En, TrophyTable.ChapterTextBase + TrophyTable.ChapterPhase(id));
            AchievementRow row = summary.Rows[id];
            Assert.Equal(title[3..].Trim(), row.Name);
            Assert.NotEqual(LauncherTexts.AchievementFallbackName(id), row.Name);
            Assert.False(char.IsDigit(row.Name[0]), $"trophy {id} kept its day number");
            Assert.All(row.Name, c => Assert.True(c is >= ' ' and <= '~', $"trophy {id}: an English name"));
            Assert.Equal(LauncherTexts.AchievementDetail(id), row.Detail);
            Assert.Equal(TrophyTable.Grade(id), row.Grade);
        }

        // the game has no chapter for these three: they carry the port's own names with or without a game folder
        Assert.All(new[] { 0, 28, 29 }, id => Assert.Equal(LauncherTexts.AchievementFallbackName(id), summary.Rows[id].Name));

        // without the folder the same profile reads with the stand-in names
        AchievementSummary bare = Achievements.Read(_paths, null);
        Assert.False(bare.GameNames);
        Assert.NotEqual(summary, bare);
        Assert.Equal(LauncherTexts.AchievementFallbackName(7), bare.Rows[7].Name);

        // the game folder is only read: nothing appears in the profile either
        Assert.Empty(Directory.EnumerateFileSystemEntries(_paths.DataDirectory));
    }

    [GameDataFact]
    public void WithTheGameFolderASaveShowsTheChapterAchievementsItsDoneBitsImply()
    {
        string game = GameFolder.Require();

        // each chapter scene alone: its own achievement and no other, with no date
        for (int i = 0; i < RetailScenes.Length; i++)
        {
            WriteSave(done: [RetailScenes[i]], seen: []);
            AchievementSummary one = Achievements.Read(_paths, game);
            Assert.Equal([i + 1], EarnedIds(one));
            Assert.Null(one.Rows[i + 1].EarnedUtc);
            Assert.Equal("Earned", one.Rows[i + 1].StateLabel);
        }

        // a chapter that was started and not finished is not earned
        WriteSave(done: [], seen: RetailScenes);
        Assert.Empty(EarnedIds(Achievements.Read(_paths, game)));

        // all 27 chapters and no lesson: 27 of 30
        WriteSave(done: RetailScenes, seen: RetailScenes);
        AchievementSummary chapters = Achievements.Read(_paths, game);
        Assert.Equal(Enumerable.Range(1, 27), EarnedIds(chapters));
        Assert.Equal(90, chapters.Percent);
        Assert.Equal(4, chapters.EarnedOf(TrophyGrade.Bronze));
        Assert.Equal(3, chapters.EarnedOf(TrophyGrade.Gold));
        Assert.Equal(20, chapters.EarnedOf(TrophyGrade.Silver));
        Assert.Equal(0, chapters.EarnedOf(TrophyGrade.Platinum));

        // with the lessons the save alone completes the set
        WriteSave(done: [.. RetailScenes, .. Lessons], seen: [540]);
        AchievementSummary all = Achievements.Read(_paths, game);
        Assert.Equal(Enumerable.Range(0, 30), EarnedIds(all));
        Assert.Equal("30 of 30 earned", all.CountLine);
        Assert.All(all.Rows, row => Assert.Null(row.EarnedUtc));

        // the same save without the game folder: the launcher cannot know the chapter scenes and shows the two lesson achievements only
        Assert.Equal([28, 29], EarnedIds(Achievements.Read(_paths, null)));
    }

    [GameDataFact]
    public void ARestoreOrAnImportFirstRecordsTheChaptersTheReplacedSaveImplied()
    {
        // a profile from before v0.1.9: day 1 finished and one lesson started, no record yet; with the game folder the page shows both from the save alone
        string game = GameFolder.Require();
        WriteSave(done: [RetailScenes[0]], seen: [540]);
        Assert.Equal([1, 28], EarnedIds(Achievements.Read(_paths, game)));
        string image = Path.Combine(_root, "savedata000");
        File.WriteAllBytes(image, SaveFile.ExportPs4Image(new SaveStore()));

        // (no game runs: the test says so itself, whatever this machine runs)
        SaveTools.ImportPs4(_paths, image, new DateTime(2026, 11, 12, 10, 0, 0), game, () => false);

        TrophyLog record = TrophyLog.ReadFile(_paths.TrophiesFile, out TrophyFileState state);
        Assert.Equal(TrophyFileState.Read, state);
        Assert.Equal([1, 28], record.Ids);
        Assert.All(record.Ids, id => Assert.Null(record.EarnedUtc(id)));
        File.SetLastWriteTimeUtc(_paths.SaveFile, DateTime.UtcNow.AddSeconds(4));
        Assert.Equal([1, 28], EarnedIds(Achievements.Read(_paths, game)));

        // a restore of an older backup, the same way: the end of the first side, from its chapter scene
        File.Delete(_paths.TrophiesFile);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 11, 12, 10, 5, 0))!;
        WriteSave(done: [RetailScenes[13]], seen: []);
        Assert.Equal([14], EarnedIds(Achievements.Read(_paths, game)));
        SaveTools.Restore(_paths, old, new DateTime(2026, 11, 12, 10, 10, 0), game, () => false);
        Assert.Equal([14], TrophyLog.Read(_paths.TrophiesFile, out bool damaged).Ids);
        Assert.False(damaged);

        // without the game folder a save tells only the lesson achievements, which is all the page showed then: the chapter goes with the save
        File.Delete(_paths.TrophiesFile);
        WriteSave(done: [RetailScenes[0]], seen: [540]);
        Assert.Equal([28], EarnedIds(Achievements.Read(_paths, null)));
        SaveTools.ImportPs4(_paths, image, new DateTime(2026, 11, 12, 10, 15, 0), null, () => false);
        Assert.Equal([28], TrophyLog.Read(_paths.TrophiesFile, out _).Ids);
    }
}
