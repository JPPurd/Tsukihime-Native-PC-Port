// The --exit-after limit of a verification run: the run ends at the step it names.
namespace Tsuki.App.Play;

/// <summary>
/// The rule of <c>--exit-after N</c>. The loop takes, in one pass, every step that is due by the clock: after a slow frame (a busy machine, a test run beside
/// others, a picture written by the script) that is two to five steps, drawn one after the other in a hidden run. The limit was only checked once a pass was
/// done, so such a pass took the run past it (<c>--exit-after 101</c> ended at step 102 after a slow step 100; the Data test of issue #13, which expects its run
/// to end at step 1200, failed so in a full parallel test run now and then).
/// Now no step is taken once the limit is reached, and the run ends after that pass.
/// </summary>
internal static class StepLimit
{
    /// <summary>True once <paramref name="frame"/> steps have run and <paramref name="exitAfter"/> (0: none) is no more than that.</summary>
    public static bool Reached(long frame, long exitAfter) => exitAfter > 0 && frame >= exitAfter;
}
