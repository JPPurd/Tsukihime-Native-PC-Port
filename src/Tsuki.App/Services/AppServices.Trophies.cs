// Achievements (v0.1.9): the PS4's 30 trophies, earned by the same rules (TrophyRules) and kept in the profile's trophies.ini beside the save (TrophyLog).
//   - at start, after the flowchart marks were repaired: every trophy the save's progress already satisfies and the file does not hold is added without a date
//     (a profile from before v0.1.9, an imported PS4 save, a restored backup), and reported once, with the first step, as one batch; not reported (only logged)
//     when the file could not be read at start nor a moment later, since it may hold them all already;
//   - during play: _RDED of a chapter's last scene or of a lesson, _TRPY, and a lesson started from the title's ending list earn their trophy at that moment, with
//     the UTC time; the platinum follows the trophy that completed the set;
//   - the file is written the moment something was earned; a write that failed is logged and tried again by the saves (at most every five seconds), with the
//     next earn and at quit.
// The record only grows and belongs to the profile, not to a save. A file that could not be read at start (another program held it) is never written over unread:
// it is read again before every write, and what it holds joins the record; until then the write stays due. A file that is read and is not a record is set aside
// as trophies.ini.broken-<UTC time>, as a damaged save.dat is, and the record takes its place. Without a save file (--no-save, the tests' rigs) nothing is read or
// written: the run keeps its achievements in memory.
using System.Globalization;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Save;
using Tsuki.Engine.Trophies;

namespace Tsuki.App.Services;

public sealed partial class AppServices
{
    /// <summary>
    /// How long the saves leave the achievements file alone after a write of it failed (milliseconds): each try can hold the game thread for a moment while another
    /// program holds the file. An earn and the quit's save try at once.
    /// </summary>
    public const int TrophyRetryMilliseconds = 5000;

    private TrophyRules _trophyRules = new([]);
    private TrophyLog _trophies = new();
    private string? _trophyPath;
    private Func<long> _trophyClock = () => Environment.TickCount64;
    private bool _trophyFileUnread;
    private string _trophyUnreadReason = string.Empty;
    private bool _trophyWriteDue;
    private long _trophyFailedAt;
    private string? _trophyWriteError;
    private IReadOnlyList<int> _trophiesCaughtUp = [];
    private bool _trophyCatchUpWithheld;
    private bool _trophyCatchUpTold;
    private readonly List<string> _trophyStartLines = [];

    /// <summary>The profile's record of earned achievements.</summary>
    public TrophyLog Trophies => _trophies;

    /// <summary>The achievements file beside the save file; null when nothing is written (no save file).</summary>
    public string? TrophyPath => _trophyPath;

    /// <summary>
    /// The achievements this start added because the save's progress already satisfied them (ascending ids, the platinum first, recorded without a date); empty when
    /// there were none. They are recorded even when <see cref="TrophiesFoundInSave"/> is not raised for them (a file that could not be read).
    /// </summary>
    public IReadOnlyList<int> TrophiesCaughtUp => _trophiesCaughtUp;

    /// <summary>What the reading of the achievements file at start found (read, or no file; a file that could not be read; a file that is not a record).</summary>
    public TrophyFileState TrophyFileAtStart { get; private set; }

    /// <summary>True when there was an achievements file at start and it could not be read, or was not a record (the record then started from the save's progress).</summary>
    public bool TrophyFileDamaged => TrophyFileAtStart != TrophyFileState.Read;

    /// <summary>True while a write of the achievements file has failed and is due again.</summary>
    public bool TrophyWriteDue => _trophyWriteDue;

    /// <summary>How many times the achievements file was to be written (each earn and catch-up that added something, each retry; for the tests).</summary>
    internal int TrophyWriteAttempts { get; private set; }

    /// <summary>Raised when an achievement was earned during play (its id 0..29), after it was recorded; the platinum is raised after the trophy that completed the set.</summary>
    public event Action<int>? TrophyEarned;

    /// <summary>
    /// Raised once, with the first step, when the start found achievements the save's progress had already earned (<see cref="TrophiesCaughtUp"/>): one event for
    /// all of them, never one each. Not raised when the achievements file could not be read at start nor when it was read again before the record's write (another
    /// program held it): what it holds is not known, so the catch-up could replay achievements earned long ago; they are recorded silently, and the log says why.
    /// </summary>
    public event Action<IReadOnlyList<int>>? TrophiesFoundInSave;

    // called by the constructor once the variables hold the save's G words and the flowchart repair has run (it can add scene_done bits to an old profile)
    private void StartTrophies(AppServicesParts parts, Vars vars)
    {
        _trophyClock = parts.Milliseconds ?? (() => Environment.TickCount64);
        IReadOnlyList<int> scenes = TrophyScenesOf(parts.Data);
        _trophyRules = new TrophyRules(scenes);
        if (scenes.Count != 0 && !_trophyRules.HasTable)
        {
            // an eboot that is not a known build, whose table was found with another length (no table at all is a data source without one: nothing to say)
            SayTrophies($"achievements: this game's trophy scene table has {scenes.Count} records, not {TrophyTable.LastChapter}: it is not used, so the end of a chapter earns no achievement here (the two lesson achievements still work)");
        }
        if (parts.SavePath is { } save)
        {
            _trophyPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(save)) ?? string.Empty, TrophyLog.FileName);
            _trophies = TrophyLog.ReadFile(_trophyPath, out TrophyFileState state);
            TrophyFileAtStart = state;
            if (state == TrophyFileState.Unreadable)
            {
                MarkUnread("the file there could not be read, and what it holds is not known: it is not written over until it can be read");
                SayTrophies($"achievements file could not be read ({_trophyPath}): the record starts from the save's progress, and the file is read again before anything is written over it");
            }
            else if (state == TrophyFileState.NotOurs)
            {
                MarkUnread("the file there is not a record of achievements and is set aside first");
                SayTrophies($"achievements file is not a record of achievements ({_trophyPath}): the record starts from the save's progress, and the file is set aside as {TrophyLog.FileName}.broken-<time> before the record is written");
            }
        }

        var added = new List<int>();
        foreach (int id in _trophyRules.Holding(n => SystemData.SceneDone(vars, n), n => SystemData.SceneSeen(vars, n)))
        {
            if (_trophies.Add(id, null))
            {
                added.Add(id);
            }
        }

        // the platinum of a record that already held the other 29 (a file edited by hand, or written by a run that ended between the two)
        if (CompletePlatinum(null))
        {
            added.Insert(0, TrophyTable.Platinum);
        }

        if (added.Count > 0)
        {
            // a file that could not be read a moment ago is read once more before the record is written: what it holds is no news, and the two together may
            // complete the set
            if (_trophyFileUnread)
            {
                if (TakeInUnreadFile(out IReadOnlyList<int> held, out bool unknown))
                {
                    added.RemoveAll(held.Contains);
                    if (CompletePlatinum(null))
                    {
                        added.Insert(0, TrophyTable.Platinum);
                    }
                }
                else if (unknown)
                {
                    // still unreadable (another program holds it): it may hold every one of them already, from earlier runs. They are recorded all the same, and
                    // written once the file can be read, but not shown: a notice that may be a replay of old achievements is worse than none
                    _trophyCatchUpWithheld = true;
                }

                // else: read, not a record, and it could not be set aside (another program holds it for reading). It holds no achievement, so the catch-up is
                // no replay and is shown as usual; the record is written once the file can be moved
            }

            if (added.Count > 0)
            {
                WriteTrophies();
            }
        }

        _trophiesCaughtUp = added;
    }

    private static IReadOnlyList<int> TrophyScenesOf(IGameData data)
    {
        try
        {
            return data.Eboot.GameFlow.TrophyScenes ?? [];
        }
        catch (NotSupportedException)
        {
            return [];                                      // a data source without the table (the golden slice, a test rig): the lesson trophies still work
        }
    }

    private void AttachTrophies(GameHost host)
    {
        host.NodeCompleted += node => Earn(_trophyRules.NodeCompleted(node, host.ArchiveMode, host.IsSceneDone));
        host.TrophyUnlocked += id => Earn(_trophyRules.ScriptRequest(id, host.ArchiveMode));
    }

    // Every rule can answer again (a load, a rewind or a flowchart jump runs the same _RDED twice): only what the record does not hold yet is new. A file that could
    // not be read at start is read first, so that an achievement it holds is not announced again, and the platinum is tested on what the two hold together
    private void Earn(int? trophy)
    {
        if (trophy is not { } id)
        {
            return;
        }

        bool tookIn = _trophyFileUnread && TakeInUnreadFile(out _);
        DateTime now = _now();
        bool added = _trophies.Add(id, now);
        bool platinum = (added || tookIn) && CompletePlatinum(now);
        if (added || platinum || (tookIn && _trophyWriteDue))
        {
            WriteTrophies();
        }

        if (added)
        {
            TrophyEarned?.Invoke(id);
        }

        if (platinum)
        {
            TrophyEarned?.Invoke(TrophyTable.Platinum);
        }
    }

    // the platinum, when the record holds the other 29 and not it yet
    private bool CompletePlatinum(DateTime? when) =>
        !_trophies.IsEarned(TrophyTable.Platinum) && TrophyRules.PlatinumHolds(_trophies.IsEarned) && _trophies.Add(TrophyTable.Platinum, when);

    // Writes the record over the file; never over a file that was not read (it stays due). A failure is said once per error and tried again by the saves
    // (RetryTrophyWrite), the next earn and the quit.
    private void WriteTrophies()
    {
        if (_trophyPath is not { } path)
        {
            return;
        }

        TrophyWriteAttempts++;
        try
        {
            if (_trophyFileUnread)
            {
                throw new IOException(_trophyUnreadReason);
            }

            _trophies.Write(path);
            if (_trophyWriteDue)
            {
                SayTrophies($"achievements file written after an earlier failure ({path})");
            }

            _trophyWriteDue = false;
            _trophyWriteError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _trophyWriteDue = true;
            _trophyFailedAt = _trophyClock();
            if (_trophyWriteError != e.Message)
            {
                // said once per error, not with every save while the folder stays unwritable
                _trophyWriteError = e.Message;
                SayTrophies($"achievements file not written: {e.Message} ({path}); it is tried again with the next save");
            }
        }
    }

    private void MarkUnread(string reason)
    {
        _trophyFileUnread = true;
        _trophyUnreadReason = reason;
    }

    private bool TakeInUnreadFile(out IReadOnlyList<int> held) => TakeInUnreadFile(out held, out _);

    // The file was not taken into the record at start: it is read again. A record: what it holds joins the record (its dates before the dateless ones the start
    // took from the save), and `held` is what it held. Not a record: it is set aside as trophies.ini.broken-<UTC time> (GameApp.Quarantine treats a damaged save.dat
    // so), and `held` is empty. False while it still cannot be read (`unknown`: what it holds is not known), or cannot be set aside: nothing may be written over
    // it then.
    private bool TakeInUnreadFile(out IReadOnlyList<int> held, out bool unknown)
    {
        held = [];
        unknown = false;
        if (!_trophyFileUnread || _trophyPath is not { } path)
        {
            return true;
        }

        TrophyLog onDisk = TrophyLog.ReadFile(path, out TrophyFileState state);
        if (state == TrophyFileState.Unreadable)
        {
            unknown = true;
            return false;
        }

        if (state == TrophyFileState.NotOurs)
        {
            try
            {
                string broken = SetAside(path);
                SayTrophies($"achievements file was not a record of achievements: moved to {broken}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                MarkUnread($"the file there is not a record of achievements and could not be set aside: {e.Message}");
                return false;
            }
        }
        else
        {
            held = onDisk.Ids;
            foreach (int id in _trophies.Ids)
            {
                onDisk.Add(id, _trophies.EarnedUtc(id));
            }

            _trophies = onDisk;
        }

        _trophyFileUnread = false;
        return true;
    }

    // trophies.ini.broken-20261112T063348Z (the UTC time), with -2, -3 ... when that name is taken
    private string SetAside(string path)
    {
        DateTime now = _now();
        string stamp = (now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : now).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string broken = $"{path}.broken-{stamp}";
        for (int n = 2; File.Exists(broken) || Directory.Exists(broken); n++)
        {
            broken = $"{path}.broken-{stamp}-{n}";
        }

        File.Move(path, broken);
        return broken;
    }

    // A write that failed is tried again whenever the save file is written (every scene start, auto save and save, the quit), at most every
    // TrophyRetryMilliseconds: a try can hold the game thread while another program holds the file (a read that waits for it, a move that waits for its holder).
    // The quit always tries. A file that could not be read is read again first; what it holds may complete the set with the record, and the platinum is then earned
    private void RetryTrophyWrite(bool now)
    {
        if (!_trophyWriteDue || (!now && _trophyClock() - _trophyFailedAt < TrophyRetryMilliseconds))
        {
            return;
        }

        bool platinum = _trophyFileUnread && TakeInUnreadFile(out _) && CompletePlatinum(_now());
        WriteTrophies();
        if (platinum)
        {
            TrophyEarned?.Invoke(TrophyTable.Platinum);
        }
    }

    // what the services have to say about the file before anyone listens (the constructor) waits for the first step
    private void SayTrophies(string message)
    {
        if (_trophyCatchUpTold)
        {
            Log(message);
        }
        else
        {
            _trophyStartLines.Add(message);
        }
    }

    // the first step tells what the start found: by then the runtime and the app listen (the constructor ran before anyone could)
    private void TellTrophyCatchUp()
    {
        if (_trophyCatchUpTold)
        {
            return;
        }

        _trophyCatchUpTold = true;
        foreach (string line in _trophyStartLines)
        {
            Log(line);
        }

        _trophyStartLines.Clear();
        if (_trophiesCaughtUp.Count > 0)
        {
            Log($"achievements: {_trophiesCaughtUp.Count} already earned by the save's progress, recorded without a date ({string.Join(", ", _trophiesCaughtUp)})");
            if (_trophyCatchUpWithheld)
            {
                Log($"achievements: no catch-up messages: the achievements file could still not be read ({_trophyPath}), so it may hold these already from earlier runs; they are recorded silently and written once it can be read");
            }
            else
            {
                TrophiesFoundInSave?.Invoke(_trophiesCaughtUp);
            }
        }
    }
}
