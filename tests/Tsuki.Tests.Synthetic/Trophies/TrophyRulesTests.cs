using Tsuki.Engine.Trophies;

namespace Tsuki.Tests.Synthetic.Trophies;

/// <summary>
/// v0.1.9: when a trophy is earned, against the PS4 program's logic (docs/formats/gallery.md 8): the hook after a completed scene (0xB7CC0), the <c>_TRPY</c>
/// filter (0x9704F), the ending list (0x68D29), and the same conditions read back from a save's bits.
/// </summary>
public sealed class TrophyRulesTests
{
    // A made-up table (the real one is the eboot's): index i is the scene of trophy i + 1. It holds the lowest and the highest flowchart node (1, 575) and the two
    // neighbours of the lesson nodes (532, 558); none of its scenes is a lesson.
    private static readonly int[] Table = [40, 7, 512, 99, 3, 575, 120, 121, 300, 301, 18, 532, 558, 250, 1, 77, 400, 401, 402, 403, 404, 405, 406, 560, 561, 562, 563];

    private static readonly int[] Lessons = [.. Enumerable.Range(533, 25)];

    private static Func<int, bool> Bits(IEnumerable<int> set)
    {
        var bits = new HashSet<int>(set);
        return bits.Contains;
    }

    private static readonly Func<int, bool> None = _ => false;
    private static readonly Func<int, bool> Every = _ => true;

    // -- _RDED: the lesson branch ---------------------------------------------------------------------------------------

    [Fact]
    public void ALessonWith24Of25DoneEarnsNothingAndWithAll25Earns29InStoryAndInAReplay()
    {
        var rules = new TrophyRules(Table);
        foreach (bool replay in new[] { false, true })
        {
            foreach (int missing in Lessons)
            {
                Func<int, bool> done = Bits(Lessons.Where(n => n != missing));
                foreach (int node in Lessons)
                {
                    Assert.Null(rules.NodeCompleted(node, replay, done));
                }
            }

            foreach (int node in Lessons)
            {
                Assert.Equal(29, rules.NodeCompleted(node, replay, Bits(Lessons)));
            }
        }
    }

    [Fact]
    public void The29TestRunsOnlyWhenTheCompletedNodeIsItselfALesson()
    {
        // every lesson is done, and so is everything else: a scene that is not a lesson still never earns 29, in story or in a replay
        var rules = new TrophyRules(Table);
        foreach (int node in new[] { 2, 100, 531, 559, 0, -1, 576, 1000, 1024 })
        {
            Assert.Null(rules.NodeCompleted(node, archiveMode: false, Every));
            Assert.Null(rules.NodeCompleted(node, archiveMode: true, Every));
        }

        // the two neighbours of the lesson range are ordinary scenes: here they are chapter scenes 12 and 13
        Assert.Equal(12, rules.NodeCompleted(532, archiveMode: false, Every));
        Assert.Equal(13, rules.NodeCompleted(558, archiveMode: false, Every));
        Assert.Null(rules.NodeCompleted(532, archiveMode: true, Every));
        Assert.Null(rules.NodeCompleted(558, archiveMode: true, Every));
    }

    [Fact]
    public void ALessonNodeNeverReachesTheChapterTable()
    {
        // the PS4 leaves the hook after the lesson branch: a table that named a lesson node would still not earn a chapter trophy for it
        int[] table = [.. Table];
        table[3] = 540;
        var rules = new TrophyRules(table);
        Assert.Null(rules.NodeCompleted(540, archiveMode: false, n => n == 540));
        Assert.Equal(29, rules.NodeCompleted(540, archiveMode: false, Bits(Lessons)));
        Assert.Equal(29, rules.NodeCompleted(540, archiveMode: false, Every));
    }

    // -- _RDED: the chapter table ---------------------------------------------------------------------------------------

    [Fact]
    public void AChapterSceneEarnsItsIndexPlusOneOnlyOutsideAReplayAndOnlyWhenItsDoneBitIsSet()
    {
        var rules = new TrophyRules(Table);
        for (int i = 0; i < Table.Length; i++)
        {
            int scene = Table[i];
            Assert.Equal(i + 1, rules.NodeCompleted(scene, archiveMode: false, n => n == scene));
            Assert.Equal(i + 1, rules.NodeCompleted(scene, archiveMode: false, Every));

            // in a replay (F024 is not 0) the table is not even looked at
            Assert.Null(rules.NodeCompleted(scene, archiveMode: true, Every));

            // the bit that counts is the scene's own
            Assert.Null(rules.NodeCompleted(scene, archiveMode: false, None));
            Assert.Null(rules.NodeCompleted(scene, archiveMode: false, n => n != scene));
        }
    }

    [Fact]
    public void ANodeThatIsNotInTheTableEarnsNothing()
    {
        var rules = new TrophyRules(Table);
        foreach (int node in new[] { 2, 4, 39, 41, 119, 251, 564, 574, 576, 1000, int.MaxValue, -1, int.MinValue })
        {
            Assert.Null(rules.NodeCompleted(node, archiveMode: false, Every));
        }
    }

    public static TheoryData<int[]> TablesOfAnotherLength()
    {
        int[][] tables =
        [
            Table[..1],
            Table[..5],
            Table[..26],                            // one record short: the last chapter's
            Table[1..],                             // one record short: the first chapter's, so every record would be the trophy before its own
            [200, .. Table],                        // a pair-like value in front of the table: every record would be the trophy after its own
            [.. Table, 200],
            [.. Table, 200, 201, 202],              // records 27, 28 and 29 would be "trophies" 28, 29 and 30: the lesson trophies and one that does not exist
            [.. Table, .. Table],
        ];
        var data = new TheoryData<int[]>();
        foreach (int[] table in tables)
        {
            data.Add(table);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TablesOfAnotherLength))]
    public void ATableThatHasNot27RecordsIsNotUsedAtAll(int[] table)
    {
        // The table of an eboot that is not a known build is found by its content, and its length is counted back from what follows it. A count other than
        // the game's 27 means record i may not be trophy i + 1: finishing one chapter would earn another chapter's achievement, in play and from the save at
        // start, and the record only grows. Such a table gives no chapter trophy at all.
        var rules = new TrophyRules(table);
        Assert.False(rules.HasTable);
        foreach (int scene in table.Concat(Table))
        {
            Assert.Null(rules.NodeCompleted(scene, archiveMode: false, Every));
            Assert.Null(rules.NodeCompleted(scene, archiveMode: true, Every));
        }

        // the lessons do not need the table; the platinum needs all of it
        Assert.Equal(29, rules.NodeCompleted(557, archiveMode: false, Every));
        Assert.Equal(28, rules.ScriptRequest(28, archiveMode: false));
        Assert.Equal(28, rules.ReplayStarted(1000));
        Assert.Equal([28, 29], rules.Holding(Every, Every));
        Assert.Empty(rules.Holding(Bits(table), None));
    }

    [Fact]
    public void TheTableOf27RecordsIsUsed()
    {
        var rules = new TrophyRules(Table);
        Assert.True(rules.HasTable);
        Assert.Equal(27, TrophyTable.LastChapter);
        Assert.Equal(1, rules.NodeCompleted(Table[0], archiveMode: false, Every));
        Assert.Equal(27, rules.NodeCompleted(Table[26], archiveMode: false, Every));
        Assert.Equal(Enumerable.Range(0, 30), rules.Holding(Every, Every));
    }

    [Fact]
    public void AnEmptyTableStillEarnsTheTwoLessonTrophies()
    {
        var rules = new TrophyRules([]);
        Assert.False(rules.HasTable);
        Assert.Null(rules.NodeCompleted(40, archiveMode: false, Every));
        Assert.Equal(29, rules.NodeCompleted(533, archiveMode: true, Every));
        Assert.Equal(28, rules.ScriptRequest(28, archiveMode: false));
        Assert.Equal(28, rules.ReplayStarted(1000));
        Assert.Equal([28, 29], rules.Holding(Every, Every));
    }

    [Fact]
    public void ATableRecordThatIsNotAFlowchartNodeNeverMatches()
    {
        // done bits exist for nodes up to 0x23F; the PS4's records are {scene, -1} pairs, and a -1 or a number past the bit set is nobody's scene
        int[] table = [.. Table];
        (table[0], table[1], table[3], table[4], table[6]) = (-1, 576, 4096, int.MinValue, int.MaxValue);
        var rules = new TrophyRules(table);
        Assert.True(rules.HasTable);
        Assert.Null(rules.NodeCompleted(-1, archiveMode: false, Every));
        Assert.Null(rules.NodeCompleted(576, archiveMode: false, Every));
        Assert.Equal(6, rules.NodeCompleted(575, archiveMode: false, Every));
        Assert.Null(rules.NodeCompleted(4096, archiveMode: false, Every));
        Assert.Null(rules.NodeCompleted(int.MinValue, archiveMode: false, Every));
        Assert.Null(rules.NodeCompleted(int.MaxValue, archiveMode: false, Every));
        Assert.Equal([3, 6, .. Enumerable.Range(8, 22)], rules.Holding(Every, Every));
    }

    [Fact]
    public void TheRulesKeepTheirOwnCopyOfTheTableAndAnswerTheSameEveryTime()
    {
        var table = new List<int>(Table);
        var rules = new TrophyRules(table);
        table[0] = 9;
        table.Clear();
        for (int again = 0; again < 3; again++)
        {
            // the events fire again after a load or a rewind: the answer does not wear out
            Assert.Equal(1, rules.NodeCompleted(40, archiveMode: false, Every));
            Assert.Equal(29, rules.NodeCompleted(557, archiveMode: true, Every));
            Assert.Equal(28, rules.ScriptRequest(28, archiveMode: false));
            Assert.Equal(28, rules.ReplayStarted(1024));
        }

        Assert.Null(rules.NodeCompleted(9, archiveMode: false, Every));
    }

    // -- _TRPY ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(28, false, 28)]             // the one retail use: the in-story offer of a lesson
    [InlineData(28, true, null)]            // in a replay everything but 29 is skipped
    [InlineData(29, true, 29)]
    [InlineData(29, false, 29)]
    [InlineData(1, false, 1)]
    [InlineData(14, false, 14)]
    [InlineData(27, false, 27)]
    [InlineData(1, true, null)]
    [InlineData(27, true, null)]
    [InlineData(0, false, null)]            // the platinum is never the game's to ask for
    [InlineData(0, true, null)]
    [InlineData(30, false, null)]
    [InlineData(30, true, null)]
    [InlineData(41, false, null)]           // the PS4's request array has 42 slots; only 30 trophies exist
    [InlineData(-1, false, null)]
    [InlineData(-29, true, null)]
    [InlineData(int.MaxValue, false, null)]
    [InlineData(int.MinValue, false, null)]
    public void AScriptRequestPassesTheReplayFilterAndIsOneOfTheTrophiesAScriptMayAskFor(int id, bool replay, int? expected)
    {
        Assert.Equal(expected, new TrophyRules(Table).ScriptRequest(id, replay));
    }

    // -- the ending list --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(999, null)]
    [InlineData(1000, 28)]
    [InlineData(1012, 28)]
    [InlineData(1024, 28)]
    [InlineData(1025, null)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(533, null)]                 // a lesson's flowchart node is not its script scene
    [InlineData(557, null)]
    [InlineData(240, null)]                 // what the flowchart replays: 240..790 and 2000..2007
    [InlineData(790, null)]
    [InlineData(2000, null)]
    [InlineData(3000, null)]
    public void AReplayOfALessonSceneEarns28AndNoOtherReplayDoes(int scriptScene, int? expected)
    {
        Assert.Equal(expected, new TrophyRules(Table).ReplayStarted(scriptScene));
    }

    // -- what a save alone satisfies ------------------------------------------------------------------------------------

    [Fact]
    public void ASaveWithNoProgressHoldsNothing()
    {
        Assert.Empty(new TrophyRules(Table).Holding(None, None));
    }

    [Fact]
    public void EachChapterSceneThatIsDoneHoldsItsOwnTrophyAndSeenIsNotDone()
    {
        var rules = new TrophyRules(Table);
        for (int i = 0; i < Table.Length; i++)
        {
            Assert.Equal([i + 1], rules.Holding(Bits([Table[i]]), None));
        }

        // a chapter that was started and not finished is not earned
        Assert.Empty(rules.Holding(None, Bits(Table)));
        Assert.Equal(Enumerable.Range(1, 27), rules.Holding(Bits(Table), None));
    }

    [Fact]
    public void AnyOneLessonSeenHolds28AndItsNeighboursDoNot()
    {
        var rules = new TrophyRules(Table);
        foreach (int lesson in Lessons)
        {
            Assert.Equal([28], rules.Holding(None, Bits([lesson])));
        }

        Assert.Empty(rules.Holding(None, Bits([532, 558, 0, 1000, 1024])));
    }

    [Fact]
    public void All25LessonsDoneHold29AndOneShortDoesNot()
    {
        var rules = new TrophyRules(Table);
        Assert.Equal([28, 29], rules.Holding(Bits(Lessons), Bits(Lessons)));
        foreach (int missing in Lessons)
        {
            Assert.Equal([28], rules.Holding(Bits(Lessons.Where(n => n != missing)), Bits(Lessons)));
        }

        // 29 reads the done bits: 25 lessons started are not 25 lessons completed
        Assert.Equal([28], rules.Holding(None, Bits(Lessons)));
    }

    [Fact]
    public void ThePlatinumHoldsOnlyWhenAllOf1To29Hold()
    {
        var rules = new TrophyRules(Table);
        int[] everything = [.. Table, .. Lessons];
        Assert.Equal(Enumerable.Range(0, 30), rules.Holding(Bits(everything), Bits([540])));

        for (int i = 0; i < Table.Length; i++)
        {
            // one chapter short: the other 28, and no platinum
            Assert.Equal(Enumerable.Range(1, 29).Where(id => id != i + 1), rules.Holding(Bits(everything.Where(n => n != Table[i])), Bits([540])));
        }

        // one lesson short: no 29, so no platinum
        Assert.Equal(Enumerable.Range(1, 28), rules.Holding(Bits(everything.Where(n => n != 557)), Bits(Lessons)));
        Assert.Equal(Enumerable.Range(1, 28), rules.Holding(Bits(everything.Where(n => n != 533)), Bits(Lessons)));
    }

    [Fact]
    public void TheHeldIdsAreAscendingAndEachIsThereOnce()
    {
        // a table (of 27 records, as every table that is used) that repeats a scene and is not sorted
        var rules = new TrophyRules([50, 50, 20, 50, 10, .. Table[5..]]);
        Assert.Equal([1, 2, 3, 4, 5, 28], rules.Holding(Bits([50, 20, 10]), Bits([557])));
        Assert.Equal([3, 5], rules.Holding(Bits([20, 10]), None));
    }

    [Fact]
    public void ThePlatinumTestAsksAboutEachOf1To29AndAboutNothingElse()
    {
        var asked = new List<int>();
        Assert.True(TrophyRules.PlatinumHolds(id =>
        {
            asked.Add(id);
            return true;
        }));
        Assert.Equal(Enumerable.Range(1, 29), asked.Order());

        for (int missing = 1; missing <= 29; missing++)
        {
            int without = missing;
            Assert.False(TrophyRules.PlatinumHolds(id => id != without));
        }

        // having the platinum already says nothing about the others
        Assert.False(TrophyRules.PlatinumHolds(id => id == 0));
    }

    [Fact]
    public void MissingArgumentsAreRefused()
    {
        var rules = new TrophyRules(Table);
        Assert.Throws<ArgumentNullException>(() => new TrophyRules(null!));
        Assert.Throws<ArgumentNullException>(() => rules.NodeCompleted(40, archiveMode: false, null!));
        Assert.Throws<ArgumentNullException>(() => rules.Holding(null!, None));
        Assert.Throws<ArgumentNullException>(() => rules.Holding(None, null!));
        Assert.Throws<ArgumentNullException>(() => TrophyRules.PlatinumHolds(null!));
    }
}
