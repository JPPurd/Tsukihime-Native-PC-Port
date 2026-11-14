// The poll's look at what the settings pages show (v0.1.9), without any WPF type so that the tests compile it. The game writes both files the pages show while
// the launcher is open: settings.ini (trophy_notice and trophy_sound from its Config; at its start it takes the launcher's language= and fullscreen= lines out)
// and save.dat (the config block, where that language and window mode then live, and the slots). LauncherModel shows LauncherProfile, settings.ini's values
// over save.dat's, so following only one of the files mixes a new one with an old one: after PLAY the launcher showed the language and window mode of before
// the player's choice.
//
// So the poll looks at both (two file lookups) and reads, on a worker thread, only what changed since the profile on show was read: settings.ini alone (opened
// so that a writer can replace it meanwhile, never through LauncherProfile, whose reading writes a probe file) and save.dat's summary (shared the same way). A
// file that could not be read just now (another program holds it, a writer's File.Replace has it away for an instant) leaves the pages as they were and is read
// again at the next poll; nothing waits for it. A save.dat that reads as damaged twice at the same stamp is shown as damaged. Nothing here writes.
using Tsuki.App.Services;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>Whether a file is there, its size and its last-write time: one lookup, nothing opened.</summary>
/// <param name="Exists">The file is there.</param>
/// <param name="Length">Its size; -1 when the lookup itself failed (such a look is never taken as the file's: the next poll looks again).</param>
/// <param name="WriteUtc">Its last-write time.</param>
public readonly record struct FileLook(bool Exists, long Length, DateTime WriteUtc)
{
    /// <summary>True when the lookup failed.</summary>
    public bool Failed => Length < 0;

    /// <summary>Looks at a file.</summary>
    public static FileLook Of(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new FileLook(true, file.Length, file.LastWriteTimeUtc) : new FileLook(false, 0, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new FileLook(false, -1, default);
        }
    }
}

/// <summary>The two files of a profile that the settings pages show, as one look.</summary>
/// <param name="Settings">settings.ini.</param>
/// <param name="Save">save.dat.</param>
public readonly record struct ProfileLook(FileLook Settings, FileLook Save);

/// <summary>What a poll is to read: the look it took and which of the two files changed since the profile on show was read.</summary>
/// <param name="Look">The look, taken before the reading (a write during the reading is seen by the next poll).</param>
/// <param name="Generation">Which reading of the whole profile was on show (a reading of the poll that a newer one overtook is not shown).</param>
/// <param name="Settings">settings.ini is to be read.</param>
/// <param name="Save">save.dat is to be read.</param>
public sealed record ProfilePoll(ProfileLook Look, int Generation, bool Settings, bool Save);

/// <summary>How a poll's reading of settings.ini went.</summary>
public enum SettingsFileState
{
    /// <summary>Read: its values are the file's.</summary>
    Read,

    /// <summary>There is no file: the defaults.</summary>
    Missing,

    /// <summary>The file is there and could not be read just now: what it holds is not known, so the pages keep what they show.</summary>
    Unreadable,
}

/// <summary>What a poll read (on a worker thread): hand it to <see cref="ProfileWatch.Apply"/>.</summary>
/// <param name="Poll">What was to be read.</param>
/// <param name="SettingsState">How the reading of settings.ini went (when it was to be read).</param>
/// <param name="Settings">Its values (when it was read).</param>
/// <param name="Save">save.dat's summary, or null when it was not to be read.</param>
public sealed record ProfileReading(ProfilePoll Poll, SettingsFileState SettingsState, AppSettingsData Settings, SaveSummary? Save);

/// <summary>When the settings pages have to follow the profile's files, and what they show then. Used from one thread (the window's); <see cref="Take"/> runs on another.</summary>
public sealed class ProfileWatch
{
    // how old a writer's temporary file may be to count as a write under way (AppSettings' own rule)
    private static readonly TimeSpan WriteUnderWay = TimeSpan.FromSeconds(5);

    private readonly LauncherPaths _paths;
    private ProfileLook _shown;
    private FileLook? _saveInDoubt;
    private int _generation;

    /// <summary>Watches a profile. Until <see cref="Took"/> is told of a reading, every file counts as changed.</summary>
    public ProfileWatch(LauncherPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _shown = new ProfileLook(new FileLook(false, -1, default), new FileLook(false, -1, default));
    }

    /// <summary>How often a poll read settings.ini (the self-test: once per change made behind the launcher, never for the launcher's own write).</summary>
    public int SettingsReadings { get; private set; }

    /// <summary>How often a poll read save.dat's summary.</summary>
    public int SaveReadings { get; private set; }

    /// <summary>The two files now: two lookups. Take it before the launcher reads the whole profile and hand it to <see cref="Took"/> after.</summary>
    public ProfileLook Look() => new(FileLook.Of(_paths.SettingsFile), FileLook.Of(_paths.SaveFile));

    /// <summary>
    /// The launcher read the whole profile (LauncherProfile.Read) after <paramref name="look"/>: at its start, a refresh, its own write of settings.ini, a restore
    /// or an import. Changes are counted from there, and a reading of a poll that was under way meanwhile is not shown.
    /// </summary>
    public void Took(ProfileLook look)
    {
        _shown = look;
        _saveInDoubt = null;
        _generation++;
    }

    /// <summary>
    /// True when the poll has something to read: settings.ini or save.dat changed since the profile on show was read (existence, size or time), or the last
    /// reading of one of them could not be taken. Two file lookups; nothing is opened.
    /// </summary>
    public bool Due(out ProfilePoll poll)
    {
        ProfileLook now = Look();
        poll = new ProfilePoll(now, _generation, now.Settings != _shown.Settings || now.Settings.Failed, now.Save != _shown.Save || now.Save.Failed);
        return poll.Settings || poll.Save;
    }

    /// <summary>
    /// Reads what <paramref name="poll"/> names, on any thread: settings.ini alone (no probe file, as LauncherProfile's reading makes) and save.dat's summary.
    /// One try each, nothing waits: a file that cannot be read now is read again at the next poll.
    /// </summary>
    public static ProfileReading Take(LauncherPaths paths, ProfilePoll poll)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(poll);
        AppSettingsData settings = default;
        SettingsFileState state = poll.Settings ? ReadSettings(paths.SettingsFile, poll.Look.Settings, out settings) : SettingsFileState.Missing;
        SaveSummary? save = null;
        if (poll.Save)
        {
            try
            {
                // save.dat is opened sharing reading, writing and deleting (SaveFile.OpenForReading), so the running game's commit goes through meanwhile
                save = SaveTools.Summarize(paths.SaveFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                save = new SaveSummary(true, 0, 0, null, e.Message);
            }
        }

        return new ProfileReading(poll, state, settings, save);
    }

    /// <summary>Takes a poll's reading.</summary>
    /// <param name="reading">What <see cref="Take"/> read.</param>
    /// <param name="shown">The profile the pages show.</param>
    /// <returns>The profile the pages show from now on, or null when they keep <paramref name="shown"/> (nothing differs, or the launcher read the whole profile meanwhile).</returns>
    public LauncherProfile? Apply(ProfileReading reading, LauncherProfile shown)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(shown);
        if (reading.Poll.Generation != _generation)
        {
            return null;
        }

        ProfileLook look = reading.Poll.Look;
        AppSettingsData settings = shown.Settings;
        SaveSummary saves = shown.Saves;
        if (reading.Poll.Settings)
        {
            SettingsReadings++;
            if (reading.SettingsState != SettingsFileState.Unreadable)
            {
                settings = reading.Settings;
                _shown = _shown with { Settings = look.Settings };
            }
        }

        if (reading.Save is { } save)
        {
            SaveReadings++;

            // a summary that says the file is damaged, or gone while the look found it, may be an instant of the game's commit or another program's hold: it is
            // shown when the next reading of the same file says the same
            bool whole = save.Error is null && (save.Exists || !look.Save.Exists);
            if (whole || _saveInDoubt == look.Save)
            {
                saves = save with { WriteError = shown.Saves.WriteError };      // the probe of the folder is the full reading's: the poll writes nothing
                _shown = _shown with { Save = look.Save };
                _saveInDoubt = null;
            }
            else
            {
                _saveInDoubt = look.Save;
            }
        }

        var profile = new LauncherProfile(settings, saves);
        return profile == shown ? null : profile;
    }

    // settings.ini as AppSettings.Load reads it, in one try: Unreadable for a file that is there and could not be read now (also for one the look found and the
    // open did not, and while a writer's temporary file says a new one is on its way in), Missing for no file at all
    private static SettingsFileState ReadSettings(string path, FileLook looked, out AppSettingsData settings)
    {
        settings = default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            settings = AppSettings.Parse(reader.ReadToEnd());
            return SettingsFileState.Read;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return looked.Exists || BeingWritten(path) ? SettingsFileState.Unreadable : SettingsFileState.Missing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return SettingsFileState.Unreadable;
        }
    }

    private static bool BeingWritten(string path)
    {
        try
        {
            string temp = path + ".tmp";
            return File.Exists(temp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) < WriteUnderWay;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
