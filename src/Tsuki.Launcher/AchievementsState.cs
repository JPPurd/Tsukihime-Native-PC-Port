// The state behind the Achievements page of the Windows launcher (v0.1.9), without any WPF type so that the tests compile it: the last reading of the profile's
// achievements, the tab on show, the rows of that tab and the words of the summary card. LauncherModel.Achievements.cs wraps it for the bindings.
//
// The page is kept up to date by polling: Due compares the stamp of trophies.ini and save.dat (two file lookups, nothing opened) with the stamp of the last
// reading, and only a changed stamp, or a last reading that another program's hold on a file left incomplete, is followed by a reading. Nothing here writes,
// and nothing here reads the profile through LauncherProfile, whose reading writes a probe file into the profile folder on every call.
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>Which achievements the list shows: the page's three tabs.</summary>
public enum AchievementFilter
{
    /// <summary>Every achievement.</summary>
    All,

    /// <summary>The earned ones.</summary>
    Earned,

    /// <summary>The ones still to earn.</summary>
    Locked,
}

/// <summary>What the Achievements page shows, and when it has to be read again. Used from one thread (the window's); the reading itself may run on another.</summary>
public sealed class AchievementsState
{
    /// <summary>The tabs in the order the page shows them.</summary>
    public static readonly IReadOnlyList<AchievementFilter> Filters = [AchievementFilter.All, AchievementFilter.Earned, AchievementFilter.Locked];

    private readonly LauncherPaths _paths;
    private AchievementStamp _stamp;
    private string? _folder;
    private bool _read;

    /// <summary>Creates the state of a profile: until the first reading it lists the 30 achievements as locked, under the port's own names. Reads nothing.</summary>
    public AchievementsState(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        var rows = new AchievementRow[TrophyTable.Count];
        for (int id = 0; id < rows.Length; id++)
        {
            rows[id] = new AchievementRow(id, TrophyTable.Grade(id), LauncherTexts.AchievementFallbackName(id), LauncherTexts.AchievementDetail(id), false, null);
        }

        Summary = new AchievementSummary(Array.AsReadOnly(rows));
    }

    /// <summary>The last reading.</summary>
    public AchievementSummary Summary { get; private set; }

    /// <summary>The tab on show. A reading never changes it.</summary>
    public AchievementFilter Filter { get; set; }

    /// <summary>How many readings were taken (the tests and the self-test count them: a poll with an unchanged stamp takes none).</summary>
    public int Readings { get; private set; }

    /// <summary>The rows of the tab on show, by id.</summary>
    public IReadOnlyList<AchievementRow> Visible => Filter switch
    {
        AchievementFilter.Earned => [.. Summary.Rows.Where(r => r.Earned)],
        AchievementFilter.Locked => [.. Summary.Rows.Where(r => !r.Earned)],
        _ => Summary.Rows,
    };

    /// <summary>How many rows a tab lists.</summary>
    public int CountOf(AchievementFilter filter) => filter switch
    {
        AchievementFilter.Earned => Summary.Earned,
        AchievementFilter.Locked => Summary.Total - Summary.Earned,
        _ => Summary.Total,
    };

    /// <summary>A tab's label with its count: "All (30)", "Earned (12)", "Locked (18)".</summary>
    public string TabLabel(AchievementFilter filter) => LauncherTexts.AchievementsTab(
        filter switch
        {
            AchievementFilter.Earned => LauncherTexts.AchievementsTabEarned,
            AchievementFilter.Locked => LauncherTexts.AchievementsTabLocked,
            _ => LauncherTexts.AchievementsTabAll,
        },
        CountOf(filter));

    /// <summary>The sentence shown in place of the list when the tab on show has no row; empty when it has rows.</summary>
    public string EmptyText => CountOf(Filter) > 0 ? string.Empty : Filter == AchievementFilter.Locked ? LauncherTexts.AchievementsNothingLocked : LauncherTexts.AchievementsNothingEarned;

    /// <summary>The summary card's large number: "40%".</summary>
    public string PercentText => LauncherTexts.AchievementPercent(Summary.Percent);

    /// <summary>A grade's count under its emblem: "3 / 21".</summary>
    public string GradeCount(TrophyGrade grade) => LauncherTexts.AchievementGradeCount(Summary.EarnedOf(grade), Summary.TotalOf(grade));

    /// <summary>
    /// True when a reading is due: nothing was read yet, the last reading was short of what the files hold for a passing reason (another program held
    /// trophies.ini, or save.dat was busy: <see cref="AchievementSummary.Incomplete"/>; such a file comes free without a change of its size or time), trophies.ini
    /// or save.dat changed since the last reading (existence, size or time), or the game folder is another one. Costs two file lookups; this is what the page's
    /// 2 s timer calls. A trophies.ini that is not a record is not read again until it changes: reading it again gives the same.
    /// </summary>
    /// <param name="gameFolder">The game folder the next reading would use, or null.</param>
    /// <param name="stamp">The stamp of the two files now: hand it to <see cref="Apply"/> with the reading taken after it.</param>
    public bool Due(string? gameFolder, out AchievementStamp stamp)
    {
        stamp = Achievements.Stamp(_paths);
        return !_read || Summary.Incomplete || stamp != _stamp || !SameFolder(gameFolder, _folder);
    }

    /// <summary>Takes a reading. The stamp must have been taken before the reading, so that a change during it is seen by the next <see cref="Due"/>.</summary>
    /// <returns>True when the reading differs from the one before (the page then shows it).</returns>
    public bool Apply(AchievementSummary summary, AchievementStamp stamp, string? gameFolder)
    {
        ArgumentNullException.ThrowIfNull(summary);
        bool changed = !summary.Equals(Summary);
        Summary = summary;
        _stamp = stamp;
        _folder = gameFolder;
        _read = true;
        Readings++;
        return changed;
    }

    /// <summary>Reads again when a reading is due (or <paramref name="force"/> says so), on the calling thread. The window reads on a worker thread instead.</summary>
    /// <returns>True when the page has something new to show.</returns>
    public bool Refresh(string? gameFolder, bool force = false)
    {
        if (!Due(gameFolder, out AchievementStamp stamp) && !force)
        {
            return false;
        }

        return Apply(Achievements.Read(_paths, gameFolder), stamp, gameFolder);
    }

    private static bool SameFolder(string? a, string? b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
