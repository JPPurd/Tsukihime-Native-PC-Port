// The words of the achievement notice (v0.1.9) in the game's language. The 27 chapter achievements are named after the chapter they finish, with the game's own
// chapter title from ui_text (the day number in front of it dropped); the three others and the heading are the port's own words (PortText). Nothing here comes
// from the console's trophy texts.
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;

namespace Tsuki.Ui;

/// <summary>Names and headings of the achievement notice.</summary>
public static class UiTrophyText
{
    /// <summary>The name of achievement <paramref name="id"/> (0..29) in the language of <paramref name="assets"/>.</summary>
    public static string Name(UiAssets assets, int id)
    {
        ArgumentNullException.ThrowIfNull(assets);
        Language language = assets.Language;
        switch (id)
        {
            case TrophyTable.Platinum:
                return PortText.Get(PortString.AchievementNameAll, language);
            case TrophyTable.FirstLesson:
                return PortText.Get(PortString.AchievementNameFirstLesson, language);
            case TrophyTable.AllLessons:
                return PortText.Get(PortString.AchievementNameEveryLesson, language);
        }

        string title = string.Empty;
        if (TrophyTable.ChapterPhase(id) is > 0 and var phase)
        {
            try
            {
                title = TrophyTable.ChapterName(assets.Text(TrophyTable.ChapterTextBase + phase));
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException or KeyNotFoundException)
            {
                // a data source without the text table (a test rig): the numbered name
            }
        }

        return title.Length > 0 ? title : PortText.Format(PortString.AchievementNameNumbered, language, id);
    }

    /// <summary>The notice's first line ("Achievement unlocked"), the same for every notice, those of the title's catch-up included.</summary>
    public static string Heading(Language language) => PortText.Get(PortString.AchievementUnlocked, language);
}
