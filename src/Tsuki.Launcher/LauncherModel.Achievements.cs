// The Achievements page (v0.1.9): what LauncherModel binds for it. The reading and the counting are AchievementsState's and the pictures of the rows are
// AchievementPictures' (no WPF type in either, so the tests compile them); this file adds the bitmaps and the lists the page binds, and keeps them up to date.
//
// Live update: the window calls PollAchievementsAsync every two seconds while it is not minimised and whenever it is activated, and RefreshAsync (the start, Check
// again, a chosen folder, the game's end) calls LoadAchievementsAsync. A poll costs four file lookups; only a changed trophies.ini or save.dat is read again, on
// a worker thread, and a settings.ini or save.dat that changed behind the settings pages is read again, on a worker thread too, so that the notice and sound
// switches follow the game's Config and the language and window mode follow the game's start (FollowProfileAsync, ProfileWatch). The poll never reads the
// profile through LauncherProfile (that writes a probe file on every call).
//
// The rows are changed in place (a changed row is replaced, a new one inserted, a gone one removed), and the three tabs are the same objects for the launcher's
// life, so a refresh keeps the list where the player scrolled it and the tab the player chose.
//
// The picture of a row: an earned achievement shows its icon, cut from the player's own game pictures; a locked one shows the locked tile of its grade and never a
// game picture. Nothing of this is made at the launcher's start: the first time the page is shown the tiles are drawn and the earned rows' icons are asked for,
// all on a worker thread, and from then on a row that becomes earned gets its icon the same way. Until its icon arrives a row shows the blank tile of its grade;
// when the icon cannot be made (no game folder, a missing picture) it shows the emblem of its grade, a trophy cup drawn by the port's own code, which is also the
// summary card's picture of the grade. A bitmap is made once and set into its row in place, so a poll neither makes one again nor redraws a row. Nothing on the
// page comes from the PS4's trophy file.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>The colours of the grade words of the Achievements page.</summary>
public static class GradeColors
{
    private static readonly Brush Platinum = Frozen(0xBF, 0xD9, 0xF2);
    private static readonly Brush Gold = Frozen(0xE6, 0xC3, 0x6A);
    private static readonly Brush Silver = Frozen(0xC3, 0xCB, 0xDB);
    private static readonly Brush Bronze = Frozen(0xCB, 0x8F, 0x62);

    /// <summary>The colour of a grade.</summary>
    public static Brush Of(TrophyGrade grade) => grade switch
    {
        TrophyGrade.Platinum => Platinum,
        TrophyGrade.Gold => Gold,
        TrophyGrade.Silver => Silver,
        _ => Bronze,
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One of the page's three tabs. The object stays the same while its label changes with the count, so the selection survives a refresh.</summary>
public sealed class AchievementTabView(AchievementFilter filter, string label) : INotifyPropertyChanged
{
    private string _label = label;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the tab lists.</summary>
    public AchievementFilter Filter { get; } = filter;

    /// <summary>"Earned (12)".</summary>
    public string Label
    {
        get => _label;
        set
        {
            if (_label != value)
            {
                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }
    }

    /// <summary>The label: what a screen reader says for the tab (WPF names a list's item by its ToString; DisplayMemberPath is only what is drawn).</summary>
    public override string ToString() => Label;
}

/// <summary>One of the four grades of the summary card: its emblem, with how many of it are earned.</summary>
public sealed class AchievementGradeView(TrophyGrade grade) : INotifyPropertyChanged
{
    private string _count = string.Empty;
    private ImageSource? _picture;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The grade.</summary>
    public TrophyGrade Grade { get; } = grade;

    /// <summary>"Gold".</summary>
    public string Label { get; } = LauncherTexts.AchievementGrade(grade);

    /// <summary>"1 / 4".</summary>
    public string Count
    {
        get => _count;
        set
        {
            if (_count != value)
            {
                _count = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            }
        }
    }

    /// <summary>The emblem of the grade (a trophy cup drawn by the port's own code, the same bitmap an earned row without an icon shows); null before the page was first shown.</summary>
    public ImageSource? Picture
    {
        get => _picture;
        set
        {
            if (!ReferenceEquals(_picture, value))
            {
                _picture = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
            }
        }
    }

    /// <summary>"Gold, 1 / 4": what a screen reader says for the grade (WPF names an item of a list by its ToString).</summary>
    public override string ToString() => $"{Label}, {Count}";
}

/// <summary>
/// A row of the list: an achievement with the picture of its square. The achievement never changes (a refresh puts a new row in its place when it does); the
/// picture is set in place, so an icon that arrives does not make the list draw the row anew.
/// </summary>
public sealed class AchievementRowView(AchievementRow row) : INotifyPropertyChanged
{
    private AchievementArt _art;
    private ImageSource? _picture;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The achievement.</summary>
    public AchievementRow Row { get; } = row;

    /// <summary>The trophy id, 0..29.</summary>
    public int Id => Row.Id;

    /// <summary>Its name.</summary>
    public string Name => Row.Name;

    /// <summary>How it is earned.</summary>
    public string Detail => Row.Detail;

    /// <summary>"Gold".</summary>
    public string GradeLabel => Row.GradeLabel;

    /// <summary>"Locked", the date and time it was earned, or "Earned".</summary>
    public string StateLabel => Row.StateLabel;

    /// <summary>True when it is earned (the words of a locked row are dimmed).</summary>
    public bool Earned => Row.Earned;

    /// <summary>What the square shows: the locked tile, the blank tile while the icon is being made, the icon, or the emblem of its grade when there can be no icon.</summary>
    public AchievementArt Art => _art;

    /// <summary>The bitmap of the square: a tile, the icon or the emblem; null before the page was first shown.</summary>
    public ImageSource? Picture => _picture;

    /// <summary>The colour of the grade word.</summary>
    public Brush GradeBrush { get; } = GradeColors.Of(row.Grade);

    /// <summary>Sets what the square shows. The same answer as before changes nothing and tells nobody.</summary>
    public void Show(AchievementArt art, ImageSource? picture)
    {
        if (_art == art && ReferenceEquals(_picture, picture))
        {
            return;
        }

        _art = art;
        _picture = picture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Art)));
    }

    /// <summary>"Its name, Gold, Locked": what a screen reader says for the row (WPF names an item of a list by its ToString); its description is the row's own text.</summary>
    public override string ToString() => $"{Name}, {GradeLabel}, {StateLabel}";
}

public sealed partial class LauncherModel
{
    /// <summary>How often the window asks whether the achievements changed, while it is not minimised.</summary>
    public static readonly TimeSpan AchievementsPollInterval = TimeSpan.FromSeconds(2);

    private static readonly TrophyGrade[] GradeOrder = [TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze];

    private readonly AchievementsState _achievements;
    private readonly AchievementPictures<BitmapSource> _pictures = new(LauncherArt.IconBitmap);
    private Task? _achievementsReading;
    private bool _achievementPicturesWanted;
    private Task _achievementPicturesStart = Task.CompletedTask;

    /// <summary>The rows of the tab on show, by id.</summary>
    public ObservableCollection<AchievementRowView> AchievementRows { get; } = [];

    /// <summary>The three tabs: All, Earned, Locked, each with its count.</summary>
    public IReadOnlyList<AchievementTabView> AchievementTabs { get; }

    /// <summary>The four grades of the summary card, each with its emblem and count: platinum, gold, silver, bronze.</summary>
    public IReadOnlyList<AchievementGradeView> AchievementGrades { get; } = [.. GradeOrder.Select(g => new AchievementGradeView(g))];

    /// <summary>The tab on show.</summary>
    public AchievementTabView SelectedAchievementTab
    {
        get => AchievementTabs.First(t => t.Filter == _achievements.Filter);
        set
        {
            // a list that loses its selection for a moment hands over null: the tab stays
            if (value is not null && value.Filter != _achievements.Filter)
            {
                _achievements.Filter = value.Filter;
                ShowAchievements();
                Raise(nameof(SelectedAchievementTab));
            }
        }
    }

    /// <summary>The last reading of the achievements (the self-test looks at it).</summary>
    public AchievementSummary AchievementSummary => _achievements.Summary;

    /// <summary>How many readings were taken (the self-test: a poll with unchanged files takes none).</summary>
    public int AchievementReadings => _achievements.Readings;

    /// <summary>"40%".</summary>
    public string AchievementPercentText => _achievements.PercentText;

    /// <summary>The earned share, 0..100, for the progress bar.</summary>
    public int AchievementPercent => _achievements.Summary.Percent;

    /// <summary>"12 of 30 earned".</summary>
    public string AchievementCountLine => _achievements.Summary.CountLine;

    /// <summary>True when trophies.ini could not be read (the page says so).</summary>
    public bool AchievementsDamaged => _achievements.Summary.Damaged;

    /// <summary>True when the chapter names could not be read from a game folder (the page asks for one).</summary>
    public bool AchievementsNoGame => !_achievements.Summary.GameNames;

    /// <summary>True when the tab on show lists nothing.</summary>
    public bool NoAchievementRows => AchievementRows.Count == 0;

    /// <summary>The sentence in place of an empty list.</summary>
    public string AchievementsEmptyText => _achievements.EmptyText;

    /// <summary>How many icons were asked for since the launcher started (the self-test: none before the page is shown, none for a row that shows its icon).</summary>
    public int AchievementIconRequests => _pictures.Requests;

    /// <summary>How often the icon of one achievement was asked for (the self-test: never for a locked one).</summary>
    public int AchievementIconRequestsOf(int trophyId) => _pictures.RequestsOf(trophyId);

    /// <summary>The notice in the game when an achievement is earned (settings.ini <c>trophy_notice</c>, off unless the file says on). Off still records it.</summary>
    public bool TrophyNotice
    {
        get => _profile.Settings.TrophyNoticeOn;
        set => UpdateSettings(s => s with { TrophyNotice = value });
    }

    /// <summary>
    /// The chime with the notice (settings.ini <c>trophy_sound</c>, off unless the file says on). The game plays it only while the notice is on too; the switch
    /// writes its own key either way and leaves trophy_notice as it is.
    /// </summary>
    public bool TrophySound
    {
        get => _profile.Settings.TrophySoundOn;
        set => UpdateSettings(s => s with { TrophySound = value });
    }

    /// <summary>
    /// Reads the achievements again whatever the files' stamps say: part of <see cref="RefreshAsync"/> (the start, Check again, a chosen folder, the game's end).
    /// What was kept of the game folder is forgotten first, the icons included, so a folder repaired in place is looked at again: once the page was shown the
    /// icons are made again, and a row keeps the picture it shows until then.
    /// </summary>
    public Task LoadAchievementsAsync()
    {
        Achievements.Forget();
        _pictures.Forget();
        return ReadAchievementsAsync(force: true);
    }

    /// <summary>
    /// The page's poll: asks whether trophies.ini or save.dat changed since the last reading (two file lookups) and reads again only then; and whether
    /// settings.ini or save.dat changed behind the launcher since the settings pages read them (two more lookups), so that the notice and sound switches follow
    /// the game's Config and the language and window mode follow the game's start. Called every <see cref="AchievementsPollInterval"/> while the window is not
    /// minimised, and when the window is activated. Every reading runs on a worker thread.
    /// </summary>
    /// <returns>True when the page shows something new.</returns>
    public async Task<bool> PollAchievementsAsync()
    {
        bool settings = await FollowProfileAsync();
        bool achievements = await ReadAchievementsAsync(force: false);
        return settings || achievements;
    }

    /// <summary>
    /// Ends when every picture the page asked for so far is in its row: the tiles are drawn and no icon is being made any more. The off-screen render waits for it
    /// before it takes the page's pictures, so they show the icons and not the blank tiles.
    /// </summary>
    public async Task AchievementPicturesSettledAsync()
    {
        await _achievementPicturesStart;
        await _pictures.WhenIdle();
    }

    private string? AchievementsFolder => _folder.State is FolderState.NotSet or FolderState.Missing ? null : _folder.Path;

    /// <summary>
    /// The Achievements page is about to be shown (the <see cref="Page"/> setter). The first time, its rows get their pictures: the tiles are drawn on a worker
    /// thread while the page is built, then the rows show them and the earned rows' icons are asked for. Before that the launcher makes no picture for the page.
    /// </summary>
    private void OpenAchievements()
    {
        if (_achievementPicturesWanted)
        {
            return;
        }

        _achievementPicturesWanted = true;
        _achievementPicturesStart = StartAsync();

        async Task StartAsync()
        {
            await Task.Run(_pictures.MakeTiles);
            ShowAchievements();
        }
    }

    private async Task<bool> ReadAchievementsAsync(bool force)
    {
        // one reading at a time: a poll that finds one running leaves (the next poll looks again), a forced reading waits for its turn
        while (_achievementsReading is { } running)
        {
            if (!force)
            {
                return false;
            }

            await running;
        }

        string? folder = AchievementsFolder;
        if (!_achievements.Due(folder, out AchievementStamp stamp) && !force)
        {
            return false;
        }

        var done = new TaskCompletionSource();
        _achievementsReading = done.Task;
        try
        {
            LauncherPaths paths = _paths;
            AchievementSummary summary = await Task.Run(() => Achievements.Read(paths, folder));
            bool changed = _achievements.Apply(summary, stamp, folder);

            // a forced reading follows a Forget: the icons are asked for again even when the rows say what they said
            if (changed || force)
            {
                ShowAchievements();
            }

            return changed;
        }
        finally
        {
            _achievementsReading = null;
            done.SetResult();
        }
    }

    /// <summary>Puts the last reading and the tab on show into what the page binds.</summary>
    private void ShowAchievements()
    {
        foreach (AchievementTabView tab in AchievementTabs)
        {
            tab.Label = _achievements.TabLabel(tab.Filter);
        }

        // the summary card: each grade's count under its emblem (drawn by code, made once with the tiles; nothing before the page was first shown)
        foreach (AchievementGradeView grade in AchievementGrades)
        {
            grade.Count = _achievements.GradeCount(grade.Grade);
            grade.Picture = _achievementPicturesWanted ? _pictures.Emblem(grade.Grade) : null;
        }

        // Once the page was shown, every earned achievement has its icon made, whatever tab is on show: one that is made or on its way is not asked for again,
        // so a poll asks only for what was earned since, and a locked one is never asked for.
        if (_achievementPicturesWanted)
        {
            _ = _pictures.Request(AchievementsFolder, _achievements.Summary.Rows);
        }

        // both lists are in id order: rows the tab no longer lists go, new ones come in at their place, changed ones are replaced where they are
        int at = 0;
        foreach (AchievementRow row in _achievements.Visible)
        {
            while (at < AchievementRows.Count && AchievementRows[at].Id < row.Id)
            {
                AchievementRows.RemoveAt(at);
            }

            if (at < AchievementRows.Count && AchievementRows[at].Id == row.Id)
            {
                if (AchievementRows[at].Row.Equals(row))
                {
                    ShowPicture(AchievementRows[at]);
                }
                else
                {
                    AchievementRows[at] = WithPicture(new AchievementRowView(row));
                }
            }
            else
            {
                AchievementRows.Insert(at, WithPicture(new AchievementRowView(row)));
            }

            at++;
        }

        while (AchievementRows.Count > at)
        {
            AchievementRows.RemoveAt(AchievementRows.Count - 1);
        }

        Raise(nameof(AchievementSummary));
        Raise(nameof(AchievementPercentText));
        Raise(nameof(AchievementPercent));
        Raise(nameof(AchievementCountLine));
        Raise(nameof(AchievementsDamaged));
        Raise(nameof(AchievementsNoGame));
        Raise(nameof(NoAchievementRows));
        Raise(nameof(AchievementsEmptyText));
    }

    private AchievementRowView WithPicture(AchievementRowView view)
    {
        ShowPicture(view);
        return view;
    }

    /// <summary>
    /// Sets what a row's square shows now: its tile or its icon, or, when there can be no icon, the emblem of its grade (the summary card's bitmap of that grade).
    /// Nothing before the page was first shown.
    /// </summary>
    private void ShowPicture(AchievementRowView view)
    {
        if (!_achievementPicturesWanted)
        {
            return;
        }

        (AchievementArt art, BitmapSource? picture) = _pictures.Of(view.Row);
        view.Show(art, picture);
    }

    /// <summary>An icon was made, or was found impossible to make: the row of that achievement, when the tab on show lists it, shows it.</summary>
    private void OnAchievementIcon(int trophyId)
    {
        foreach (AchievementRowView view in AchievementRows)
        {
            if (view.Id == trophyId)
            {
                ShowPicture(view);
                return;
            }
        }
    }
}
