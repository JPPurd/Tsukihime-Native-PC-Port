// When a trophy is earned (v0.1.9), read from the PS4 program (docs/formats/gallery.md 8):
//   _RDED(n), n a lesson (533..557), in story or replay: all 25 lessons done -> 29. No other test on this branch.
//   _RDED(n), any other n, outside a replay: n is record i of the eboot's trophy scene table and is done -> i + 1.
//   _TRPY(n): passed on outside a replay; inside one only n == 29.
//   A replay of script scene 1000..1024 started from the title's ending list -> 28, before any script runs.
//   0 (platinum) is never asked for by the game: it is earned when 1..29 all are.
// The same conditions can be read back from a save alone (scene_done and scene_seen bits), which is how progress made before achievements were recorded counts.
// The class holds no state: every answer can be asked again (the events fire again after a load or a rewind), so the caller's record decides what is new.
namespace Tsuki.Engine.Trophies;

/// <summary>The unlock rules of the 30 trophies over the eboot's trophy scene table.</summary>
public sealed class TrophyRules
{
    // the highest flowchart node that has a done / seen bit
    private const int MaxNode = 0x23F;

    private readonly int[] _scenes;

    /// <summary>Creates the rules.</summary>
    /// <param name="trophyScenes">
    /// The eboot table (<c>IGameFlowTables.TrophyScenes</c>): index i is the scene of trophy i + 1. It is taken only when it has exactly the game's 27 records; a
    /// list of any other length is not used at all (<see cref="HasTable"/>), and no chapter trophy can then be earned or read back from a save.
    /// </param>
    public TrophyRules(IReadOnlyList<int> trophyScenes)
    {
        ArgumentNullException.ThrowIfNull(trophyScenes);

        // Record i is trophy i + 1 only in the table the game has. On an eboot that is not a known build the table is found by its content and its length is
        // counted back from what follows it: a count that came out otherwise (a damaged record, a pair-like value in front of the table) would give the records
        // behind the fault to other chapters' trophies, and the record of achievements only grows. Both known eboots give 27.
        _scenes = trophyScenes.Count == TrophyTable.LastChapter ? [.. trophyScenes] : [];
    }

    /// <summary>True when the trophy scene table was taken: it had exactly 27 records. False: the chapter trophies 1..27 cannot be earned; the lesson trophies can.</summary>
    public bool HasTable => _scenes.Length != 0;

    /// <summary>
    /// The trophy that completing flowchart node <paramref name="node"/> earns (the <c>_RDED</c> moment, after its done bit is set): 29 when the node is a lesson and
    /// all 25 lessons are done; a chapter trophy 1..27 when the node is in the table, is done and this is not a replay; else null.
    /// </summary>
    /// <param name="node">The node <c>_RDED</c> completed.</param>
    /// <param name="archiveMode">True in a replay (F024 is not 0).</param>
    /// <param name="sceneDone">scene_done of a node.</param>
    public int? NodeCompleted(int node, bool archiveMode, Func<int, bool> sceneDone)
    {
        ArgumentNullException.ThrowIfNull(sceneDone);
        if (node is >= TrophyTable.LessonNodeFirst and <= TrophyTable.LessonNodeLast)
        {
            // the lesson branch comes first and ends here, as on the PS4
            return AllLessonsDone(sceneDone) ? TrophyTable.AllLessons : null;
        }

        if (archiveMode)
        {
            return null;
        }

        for (int i = 0; i < _scenes.Length; i++)
        {
            if (_scenes[i] == node && IsNode(node) && sceneDone(node))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>The trophy a script's <c>_TRPY(id)</c> earns: <paramref name="id"/> when it is 1..29 and this is not a replay (in a replay only 29 passes); else null.</summary>
    public int? ScriptRequest(int id, bool archiveMode) =>
        id is >= TrophyTable.FirstChapter and <= TrophyTable.AllLessons && (!archiveMode || id == TrophyTable.AllLessons) ? id : null;

    /// <summary>The trophy a replay started from the title's ending list earns: 28 for script scene 1000..1024, else null.</summary>
    public int? ReplayStarted(int scriptScene) =>
        scriptScene is >= TrophyTable.LessonSceneFirst and <= TrophyTable.LessonSceneLast ? TrophyTable.FirstLesson : null;

    /// <summary>
    /// The trophies a save alone satisfies, ascending: i in 1..27 when the table's scene i - 1 is done; 28 when any lesson was seen; 29 when all 25 lessons are done;
    /// 0 when 1..29 all hold.
    /// </summary>
    /// <param name="sceneDone">scene_done of a node (G[300 + n / 16] bit n % 16).</param>
    /// <param name="sceneSeen">scene_seen of a node (G[110 + n / 16] bit n % 16).</param>
    public IReadOnlyList<int> Holding(Func<int, bool> sceneDone, Func<int, bool> sceneSeen)
    {
        ArgumentNullException.ThrowIfNull(sceneDone);
        ArgumentNullException.ThrowIfNull(sceneSeen);
        var held = new bool[TrophyTable.Count];
        for (int i = 0; i < _scenes.Length; i++)
        {
            held[i + 1] = IsNode(_scenes[i]) && sceneDone(_scenes[i]);
        }

        for (int n = TrophyTable.LessonNodeFirst; n <= TrophyTable.LessonNodeLast && !held[TrophyTable.FirstLesson]; n++)
        {
            held[TrophyTable.FirstLesson] = sceneSeen(n);
        }

        held[TrophyTable.AllLessons] = AllLessonsDone(sceneDone);
        held[TrophyTable.Platinum] = PlatinumHolds(id => held[id]);
        var ids = new List<int>(TrophyTable.Count);
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            if (held[id])
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>True when the platinum holds: every one of 1..29 is earned.</summary>
    /// <param name="earned">Whether a trophy 1..29 is earned.</param>
    public static bool PlatinumHolds(Func<int, bool> earned)
    {
        ArgumentNullException.ThrowIfNull(earned);
        for (int id = TrophyTable.FirstChapter; id <= TrophyTable.AllLessons; id++)
        {
            if (!earned(id))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNode(int node) => node is > 0 and <= MaxNode;

    private static bool AllLessonsDone(Func<int, bool> sceneDone)
    {
        for (int n = TrophyTable.LessonNodeFirst; n <= TrophyTable.LessonNodeLast; n++)
        {
            if (!sceneDone(n))
            {
                return false;
            }
        }

        return true;
    }
}
