using Tsuki.Engine.Trophies;

namespace Tsuki.Tests.Synthetic.Trophies;

/// <summary>v0.1.9: the fixed facts of the 30 trophies: ids, grades (1 platinum, 4 gold, 21 silver, 4 bronze) and the chapter each chapter trophy is named after.</summary>
public sealed class TrophyTableTests
{
    [Fact]
    public void TheIdsAndTheLessonNumbersAreThePs4s()
    {
        Assert.Equal(30, TrophyTable.Count);
        Assert.Equal(0, TrophyTable.Platinum);
        Assert.Equal(1, TrophyTable.FirstChapter);
        Assert.Equal(27, TrophyTable.LastChapter);
        Assert.Equal(28, TrophyTable.FirstLesson);
        Assert.Equal(29, TrophyTable.AllLessons);

        // the 25 lessons: flowchart nodes 533..557, script scenes 1000..1024 (node = scene - 467)
        Assert.Equal(533, TrophyTable.LessonNodeFirst);
        Assert.Equal(557, TrophyTable.LessonNodeLast);
        Assert.Equal(1000, TrophyTable.LessonSceneFirst);
        Assert.Equal(1024, TrophyTable.LessonSceneLast);
        Assert.Equal(TrophyTable.LessonSceneFirst - 467, TrophyTable.LessonNodeFirst);
        Assert.Equal(TrophyTable.LessonSceneLast - 467, TrophyTable.LessonNodeLast);
    }

    [Fact]
    public void TheGradesAreOnePlatinumFourBronzeFourGoldAndTwentyOneSilverWithTheseIds()
    {
        int[] Of(TrophyGrade grade) => [.. Enumerable.Range(0, 30).Where(id => TrophyTable.Grade(id) == grade)];

        Assert.Equal([0], Of(TrophyGrade.Platinum));
        Assert.Equal([1, 2, 3, 4], Of(TrophyGrade.Bronze));
        Assert.Equal([14, 26, 27, 29], Of(TrophyGrade.Gold));
        Assert.Equal([5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 28], Of(TrophyGrade.Silver));

        Assert.Equal(1, TrophyTable.TotalOf(TrophyGrade.Platinum));
        Assert.Equal(4, TrophyTable.TotalOf(TrophyGrade.Gold));
        Assert.Equal(21, TrophyTable.TotalOf(TrophyGrade.Silver));
        Assert.Equal(4, TrophyTable.TotalOf(TrophyGrade.Bronze));
        Assert.Equal(0, TrophyTable.TotalOf((TrophyGrade)4));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnIdThatIsNotATrophyHasNoGrade(int id)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyTable.Grade(id));
    }

    [Fact]
    public void TheChapterNumbersOfTrophies1To27AreTheOnesTheirScriptsSet()
    {
        // F023 of the script of each trophy scene: Arcueid's side 1..14, then Ciel's side 15..27 (which slots in between)
        int[] phases = [3, 5, 7, 9, 11, 14, 16, 18, 20, 22, 25, 27, 29, 31, 13, 15, 17, 19, 21, 23, 26, 28, 30, 33, 34, 35, 36];
        Assert.Equal(phases, Enumerable.Range(1, 27).Select(TrophyTable.ChapterPhase));

        // the chapter title of a trophy is ui_text 358 + F023
        Assert.Equal(358, TrophyTable.ChapterTextBase);
        int[] textIds = [361, 363, 365, 367, 369, 372, 374, 376, 378, 380, 383, 385, 387, 389, 371, 373, 375, 377, 379, 381, 384, 386, 388, 391, 392, 393, 394];
        Assert.Equal(textIds, Enumerable.Range(1, 27).Select(id => TrophyTable.ChapterTextBase + TrophyTable.ChapterPhase(id)));

        // the platinum, the lesson trophies and what is no trophy have no chapter
        foreach (int id in new[] { 0, 28, 29, 30, -1, int.MinValue, int.MaxValue })
        {
            Assert.Equal(0, TrophyTable.ChapterPhase(id));
        }
    }

    [Theory]
    [InlineData("03 Title", "Title")]
    [InlineData("Title", "Title")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("3 x", "3 x")]                              // one digit is not a day number
    [InlineData("14 Two Words Here", "Two Words Here")]
    [InlineData("  07 Padded  ", "Padded")]
    [InlineData("07  Wide gap", "Wide gap")]
    [InlineData("07Title", "07Title")]                      // no space: the digits are part of the name
    [InlineData("123 x", "123 x")]                          // three digits are not a day number
    [InlineData("05 06 x", "06 x")]                         // only one number is dropped
    [InlineData("A1 x", "A1 x")]
    [InlineData("1a b", "1a b")]                            // a digit and a letter are not a day number
    [InlineData("12", "12")]
    [InlineData("１４　全角", "全角")]                          // full-width digits and the ideographic space
    [InlineData("12 第十二日", "第十二日")]
    public void AChapterNameIsTheTitleWithoutItsTwoDigitDayNumber(string? uiText, string expected)
    {
        Assert.Equal(expected, TrophyTable.ChapterName(uiText!));
    }
}
