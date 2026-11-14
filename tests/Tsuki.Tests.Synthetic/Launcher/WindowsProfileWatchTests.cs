// v0.1.9, the Windows launcher's settings pages while the game runs beside it: ProfileWatch of src/Tsuki.Launcher (compiled into these tests because it holds no
// WPF type) decides when the poll reads settings.ini and save.dat again and what the pages show then. The game takes the launcher's language and window mode
// out of settings.ini at its start and keeps them in save.dat, so the pages must follow both files (the review of W-3's fix: following settings.ini alone
// showed the language and window mode of before the player's choice after PLAY). A file another program holds when the poll reads it leaves the pages as
// they were and is read again once it is free. The real launcher drives the same in its self-test (WindowsAchievementsPageTests).
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Save;
using Tsuki.Launcher;
using Tsuki.Launcher.Core;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class WindowsProfileWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-wpf-follow-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;
    private DateTime _saveTime = new(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc);

    public WindowsProfileWatchTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(_paths.DataDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the temp folder is the system's to clean
        }
    }

    /// <summary>Commits a save as the game does, with that language and window mode in its config block (each with a later file time).</summary>
    private void WriteSave(Language language, bool fullscreen)
    {
        var store = new SaveStore();
        store.SystemData.Block.Config.Language = (short)language;
        store.SystemData.Block.Config.Bytes[AppSettings.WindowModeOffset] = (byte)(fullscreen ? 1 : 0);
        SaveFile.WriteAtomic(_paths.SaveFile, store);
        _saveTime = _saveTime.AddSeconds(10);
        File.SetLastWriteTimeUtc(_paths.SaveFile, _saveTime);
    }

    /// <summary>The launcher's own reading of the whole profile, as LauncherModel.ReadProfile does it: the look first, then the reading.</summary>
    private LauncherProfile ReadWhole(ProfileWatch watch)
    {
        ProfileLook look = watch.Look();
        LauncherProfile profile = LauncherProfile.Read(_paths);
        watch.Took(look);
        return profile;
    }

    /// <summary>One poll as LauncherModel.FollowProfileAsync makes it (the reading would run on a worker thread): the profile the pages show afterwards.</summary>
    private LauncherProfile Poll(ProfileWatch watch, LauncherProfile shown, out bool changed)
    {
        changed = false;
        if (!watch.Due(out ProfilePoll poll))
        {
            return shown;
        }

        LauncherProfile? next = watch.Apply(ProfileWatch.Take(_paths, poll), shown);
        changed = next is not null;
        return next ?? shown;
    }

    [Fact]
    public void AfterTheGameTookTheLaunchersLanguageAndWindowModeThePagesShowWhatTheGameUses()
    {
        // a save of the game: Japanese, windowed; the launcher shows it
        WriteSave(Language.Ja, fullscreen: false);
        var watch = new ProfileWatch(_paths);
        LauncherProfile shown = ReadWhole(watch);
        Assert.Equal(Language.Ja, shown.Language);
        Assert.False(shown.Fullscreen);
        Assert.False(watch.Due(out _));

        // the player picks English and fullscreen on the launcher (its own write, read back by the launcher itself: nothing for the poll)
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { GameLanguage = Language.En, Fullscreen = true }));
        shown = ReadWhole(watch);
        Assert.Equal(Language.En, shown.Language);
        Assert.True(shown.Fullscreen);
        Assert.False(watch.Due(out _));

        // PLAY: the game takes both lines out of settings.ini (StartupSettings.ApplyToConfig) and, a moment later, commits them into save.dat's config block
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { GameLanguage = null, Fullscreen = null }));
        Assert.True(watch.Due(out ProfilePoll first));
        Assert.True(first.Settings);
        Assert.False(first.Save);
        shown = Poll(watch, shown, out _);
        WriteSave(Language.En, fullscreen: true);

        // the next poll reads save.dat too: the pages show what the game uses, as the profile's own reading of the files says
        Assert.True(watch.Due(out ProfilePoll second));
        Assert.False(second.Settings);
        Assert.True(second.Save);
        shown = Poll(watch, shown, out bool changed);
        Assert.True(changed);
        Assert.Equal(Language.En, shown.Language);
        Assert.True(shown.Fullscreen);
        Assert.Contains("Fullscreen", shown.StatusLine(), StringComparison.Ordinal);
        Assert.Contains("English", shown.StatusLine(), StringComparison.Ordinal);
        LauncherProfile files = LauncherProfile.Read(_paths);
        Assert.Equal(files.Settings, shown.Settings);
        Assert.Equal(files.Saves with { WriteError = null }, shown.Saves with { WriteError = null });
        Assert.False(watch.Due(out _));

        // both at once, as one poll may find them: the same
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { GameLanguage = Language.Zc, Fullscreen = false }));
        shown = ReadWhole(watch);
        Assert.Equal(Language.Zc, shown.Language);
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { GameLanguage = null, Fullscreen = null }));
        WriteSave(Language.Zc, fullscreen: false);
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.Equal(Language.Zc, shown.Language);
        Assert.False(shown.Fullscreen);

        // later the game's Config changes the language in save.dat alone: the Language page follows
        WriteSave(Language.Zt, fullscreen: false);
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.Equal(Language.Zt, shown.Language);
        Assert.Equal(2, watch.SettingsReadings);
        Assert.Equal(3, watch.SaveReadings);
    }

    [Fact]
    public void ThePollNeverProbesTheProfileFolderAndKeepsTheProbesAnswer()
    {
        // LauncherProfile.Read probes the folder (a file made and deleted): the poll, every two seconds, must not; the probe's answer stays the full reading's
        WriteSave(Language.Ja, fullscreen: false);
        var watch = new ProfileWatch(_paths);
        LauncherProfile whole = ReadWhole(watch);
        LauncherProfile shown = whole with { Saves = whole.Saves with { WriteError = "the probe's answer" } };
        var pinned = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string[] Listing() => [.. Directory.EnumerateFileSystemEntries(_paths.DataDirectory).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

        // what the game writes (its files are replaced through temporary files, which change the folder): then the folder's time is pinned again
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophyNotice = false }));
        WriteSave(Language.En, fullscreen: true);
        Directory.SetLastWriteTimeUtc(_paths.DataDirectory, pinned);
        string[] before = Listing();
        shown = Poll(watch, shown, out bool changed);
        Assert.True(changed);
        Assert.Equal(Language.En, shown.Language);
        Assert.False(shown.Settings.TrophyNotice);
        Assert.Equal("the probe's answer", shown.Saves.WriteError);
        Assert.Equal(pinned, Directory.GetLastWriteTimeUtc(_paths.DataDirectory));
        Assert.Equal(before, Listing());
    }

    [Fact]
    public void ASettingsFileAnotherProgramHoldsLeavesThePagesAsTheyWereAndIsReadOnceItIsFree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;         // only Windows refuses an open of a file another program holds without sharing (as the other held-file tests)
        }

        // the player's settings, then the game changes the prompts while another program (an editor, a backup tool) holds the file without sharing
        Assert.True(AppSettings.Save(_paths.SettingsFile, new AppSettingsData(Prompts: PromptSetting.Xbox, VSync: false, WindowWidth: 1280, WindowHeight: 720, TrophyNotice: false)));
        var watch = new ProfileWatch(_paths);
        LauncherProfile shown = ReadWhole(watch);
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { Prompts = PromptSetting.PlayStation }));
        DateTime written = File.GetLastWriteTimeUtc(_paths.SettingsFile);
        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // one try, no defaults: the switches keep what they showed, and the poll looks again next time
            Assert.True(watch.Due(out ProfilePoll poll));
            ProfileReading reading = ProfileWatch.Take(_paths, poll);
            Assert.Equal(SettingsFileState.Unreadable, reading.SettingsState);
            Assert.Null(watch.Apply(reading, shown));
            Assert.True(watch.Due(out _));
            shown = Poll(watch, shown, out bool changed);
            Assert.False(changed);
            Assert.Equal(PromptSetting.Xbox, shown.Settings.Prompts);
            Assert.False(shown.VSync);
            Assert.Equal((1280, 720), shown.WindowSize);
            Assert.False(shown.Settings.TrophyNotice);
        }

        // the holder lets go and leaves the file as it was: no stamp changed, and the next poll reads it
        Assert.Equal(written, File.GetLastWriteTimeUtc(_paths.SettingsFile));
        Assert.True(watch.Due(out _));
        shown = Poll(watch, shown, out bool free);
        Assert.True(free);
        Assert.Equal(PromptSetting.PlayStation, shown.Settings.Prompts);
        Assert.False(shown.VSync);
        Assert.Equal((1280, 720), shown.WindowSize);
        Assert.False(shown.Settings.TrophyNotice);
        Assert.False(watch.Due(out _));
        Assert.Equal(3, watch.SettingsReadings);
    }

    [Fact]
    public void ASaveFileThatIsHeldOrDamagedIsShownOnlyWhenTheNextReadingSaysTheSame()
    {
        WriteSave(Language.Ja, fullscreen: false);
        var watch = new ProfileWatch(_paths);
        LauncherProfile shown = ReadWhole(watch);
        bool changed;
        if (OperatingSystem.IsWindows())
        {
            // another program holds save.dat without sharing when the poll reads it: the pages keep what they showed, and the Saves page calls nothing damaged
            WriteSave(Language.En, fullscreen: true);
            using (new FileStream(_paths.SaveFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                shown = Poll(watch, shown, out changed);
                Assert.False(changed);
                Assert.Equal(Language.Ja, shown.Language);
                Assert.Null(shown.Saves.Error);
                Assert.True(watch.Due(out _));
            }

            shown = Poll(watch, shown, out changed);
            Assert.True(changed);
            Assert.Equal(Language.En, shown.Language);
            Assert.True(shown.Fullscreen);
            Assert.Null(shown.Saves.Error);
            Assert.False(watch.Due(out _));
        }

        // a save.dat that is damaged (and no previous generation to fall back on): said once the second reading of the same file says it too, then left alone
        File.Delete(_paths.SaveFile + ".bak");
        File.WriteAllBytes(_paths.SaveFile, [1, 2, 3, 4, 5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(_paths.SaveFile, _saveTime.AddMinutes(5));
        shown = Poll(watch, shown, out changed);
        Assert.False(changed);
        Assert.Null(shown.Saves.Error);
        Assert.True(watch.Due(out _));
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.NotNull(shown.Saves.Error);
        Assert.False(watch.Due(out _));

        // the game commits a good one again: shown at once
        WriteSave(Language.Zc, fullscreen: false);
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.Null(shown.Saves.Error);
        Assert.Equal(Language.Zc, shown.Language);
    }

    [Fact]
    public void AGoneSettingsFileIsTheDefaultsButNotInTheInstantAWriterReplacesIt()
    {
        Assert.True(AppSettings.Save(_paths.SettingsFile, new AppSettingsData(Prompts: PromptSetting.Keyboard, VSync: false)));
        var watch = new ProfileWatch(_paths);
        LauncherProfile shown = ReadWhole(watch);

        // a writer's File.Replace has taken the old file away and not brought the new one yet: its temporary file lies beside the name
        string kept = File.ReadAllText(_paths.SettingsFile);
        File.Delete(_paths.SettingsFile);
        File.WriteAllText(_paths.SettingsFile + ".tmp", kept);
        Assert.True(watch.Due(out ProfilePoll poll));
        ProfileReading reading = ProfileWatch.Take(_paths, poll);
        Assert.Equal(SettingsFileState.Unreadable, reading.SettingsState);
        Assert.Null(watch.Apply(reading, shown));
        Assert.Equal(PromptSetting.Keyboard, shown.Settings.Prompts);

        // no file and no write under way: the defaults, as the profile's own reading says
        File.Delete(_paths.SettingsFile + ".tmp");
        shown = Poll(watch, shown, out bool changed);
        Assert.True(changed);
        Assert.Equal(default, shown.Settings);
        Assert.True(shown.VSync);
        Assert.False(watch.Due(out _));
    }

    [Fact]
    public void TheGamesAchievementSoundIsFollowedLikeItsNoticeAndNeitherIsOnUntilTheFileSaysSo()
    {
        // a fresh profile: neither key, so both switches of the Achievements page are off (D7)
        var watch = new ProfileWatch(_paths);
        LauncherProfile shown = ReadWhole(watch);
        (bool Notice, bool Sound) Switches() => (shown.Settings.TrophyNoticeOn, shown.Settings.TrophySoundOn);
        Assert.Equal((false, false), Switches());

        // the game's Config, behind the launcher: Achievement Notify on, then Achievement Sound on, each found by its own poll, which keeps the other key
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophyNotice = true }));
        shown = Poll(watch, shown, out bool changed);
        Assert.True(changed);
        Assert.Equal((true, false), Switches());
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophySound = true }));
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.Equal((true, true), Switches());
        Assert.Equal(AppSettings.Load(_paths.SettingsFile), shown.Settings);
        Assert.False(watch.Due(out _));

        // the launcher's own sound switch (its write, read back by the launcher itself): nothing for the poll
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophySound = false }));
        shown = ReadWhole(watch);
        Assert.False(watch.Due(out _));
        Assert.Equal((true, false), Switches());

        // the game turns its sound on again, and the player takes the notice's line out in an editor: absent is off again, the sound stays on
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophySound = true }));
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophyNotice = null }));
        shown = Poll(watch, shown, out changed);
        Assert.True(changed);
        Assert.Null(shown.Settings.TrophyNotice);
        Assert.Equal((false, true), Switches());
        Assert.Equal(4, watch.SettingsReadings);
    }

    [Fact]
    public void TheLaunchersOwnReadingOvertakesAPollUnderWay()
    {
        var watch = new ProfileWatch(_paths);
        LauncherProfile shown = ReadWhole(watch);

        // the game writes; the poll's reading is under way on its worker thread when the player changes a setting (the launcher reads the whole profile)
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { TrophyNotice = false }));
        Assert.True(watch.Due(out ProfilePoll poll));
        ProfileReading reading = ProfileWatch.Take(_paths, poll);
        Assert.True(AppSettings.Update(_paths.SettingsFile, s => s with { VSync = false }));
        shown = ReadWhole(watch);

        // the older reading is not shown over the newer one, and nothing is due: the launcher's reading holds both changes
        Assert.Null(watch.Apply(reading, shown));
        Assert.False(shown.Settings.TrophyNotice);
        Assert.False(shown.VSync);
        Assert.False(watch.Due(out _));
    }
}
