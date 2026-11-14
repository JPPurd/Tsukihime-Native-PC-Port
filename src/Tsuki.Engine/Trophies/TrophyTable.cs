// The fixed facts of the 30 trophies (v0.1.9): their ids, grades and the chapter each chapter trophy is named after. The grades are not in the eboot; they are public
// facts, confirmed once by hand against the four picture groups of a trophy file (the same cup picture for every trophy of a grade); the port no longer reads that
// file. The scenes that earn the chapter trophies are NOT here: they are an eboot table (IGameFlowTables.TrophyScenes).
namespace Tsuki.Engine.Trophies;

/// <summary>Ids, grades and chapter numbers of the 30 trophies.</summary>
public static class TrophyTable
{
    /// <summary>Number of trophies (ids 0..29).</summary>
    public const int Count = 30;

    /// <summary>The trophy for earning all the others. The game never asks for it.</summary>
    public const int Platinum = 0;

    /// <summary>The first chapter trophy.</summary>
    public const int FirstChapter = 1;

    /// <summary>The last chapter trophy.</summary>
    public const int LastChapter = 27;

    /// <summary>The trophy for starting a lesson.</summary>
    public const int FirstLesson = 28;

    /// <summary>The trophy for completing all 25 lessons.</summary>
    public const int AllLessons = 29;

    /// <summary>The flowchart node of the first lesson.</summary>
    public const int LessonNodeFirst = 533;

    /// <summary>The flowchart node of the last lesson.</summary>
    public const int LessonNodeLast = 557;

    /// <summary>The script scene of the first lesson (what the title's ending list replays).</summary>
    public const int LessonSceneFirst = 1000;

    /// <summary>The script scene of the last lesson.</summary>
    public const int LessonSceneLast = 1024;

    /// <summary>The ui_text id of a chapter title is <c>ChapterTextBase + ChapterPhase(id)</c>.</summary>
    public const int ChapterTextBase = 358;

    // F023 (the chapter number the scene's script sets) of trophies 1..27
    private static readonly int[] Phases = [3, 5, 7, 9, 11, 14, 16, 18, 20, 22, 25, 27, 29, 31, 13, 15, 17, 19, 21, 23, 26, 28, 30, 33, 34, 35, 36];

    /// <summary>The grade of trophy <paramref name="id"/>: 0 platinum; 1..4 bronze; 14, 26, 27 and 29 gold; the others silver.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not 0..29.</exception>
    public static TrophyGrade Grade(int id)
    {
        if (id is < 0 or >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "trophy ids are 0..29");
        }

        return id switch
        {
            Platinum => TrophyGrade.Platinum,
            >= 1 and <= 4 => TrophyGrade.Bronze,
            14 or 26 or 27 or AllLessons => TrophyGrade.Gold,
            _ => TrophyGrade.Silver,
        };
    }

    /// <summary>The chapter number (F023) of chapter trophy <paramref name="id"/> (1..27); 0 for every other id.</summary>
    public static int ChapterPhase(int id) => id is >= FirstChapter and <= LastChapter ? Phases[id - FirstChapter] : 0;

    /// <summary>
    /// A chapter title of ui_text without its day number: drops a leading "NN " (two digits and a space; full-width digits and the ideographic space count too)
    /// and trims. Null reads as empty.
    /// </summary>
    public static string ChapterName(string uiText)
    {
        string text = (uiText ?? string.Empty).Trim();
        if (text.Length >= 3 && char.IsDigit(text[0]) && char.IsDigit(text[1]) && char.IsWhiteSpace(text[2]))
        {
            text = text[3..].Trim();
        }

        return text;
    }

    /// <summary>How many trophies have <paramref name="grade"/>: 1 platinum, 4 gold, 21 silver, 4 bronze.</summary>
    public static int TotalOf(TrophyGrade grade)
    {
        int n = 0;
        for (int id = 0; id < Count; id++)
        {
            if (Grade(id) == grade)
            {
                n++;
            }
        }

        return n;
    }
}
