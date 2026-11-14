using Tsuki.App.Play;
using Tsuki.Platform.Timing;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The --exit-after limit of a verification run (<see cref="StepLimit"/>): the loop takes, in one pass, every step that is due by the clock, two to five after a
/// slow frame. Checked only once a pass was done, the limit let such a pass take the run past it (a Data test of issue #13 failed so now and then in a full
/// parallel run); the loop now asks before every step. GameAppWiringTests pins that the loop asks this rule.
/// </summary>
public sealed class StepLimitTests
{
    [Theory]
    [InlineData(0, 0, false)]                       // no limit
    [InlineData(5000, 0, false)]
    [InlineData(0, 101, false)]
    [InlineData(100, 101, false)]
    [InlineData(101, 101, true)]                    // the step it names has run
    [InlineData(102, 101, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, -1, false)]
    public void TheLimitIsReachedOnceTheStepItNamesHasRun(long frame, long exitAfter, bool reached) =>
        Assert.Equal(reached, StepLimit.Reached(frame, exitAfter));

    // the loop's passes as GameApp takes them, on a clock that comes three steps on between passes (every frame slow): the steps due in a pass, then the check
    private static long RunTo(long exitAfter, bool askBeforeEveryStep)
    {
        var clock = new FixedStepClock(60, 0);
        long steps = 0;
        long now = 0;
        for (int pass = 0; pass < 10_000; pass++)
        {
            now += 3;
            while ((!askBeforeEveryStep || !StepLimit.Reached(steps, exitAfter)) && clock.TryNextTick(now, out LogicTick tick))
            {
                steps = tick.Frame + 1;
            }

            if (StepLimit.Reached(steps, exitAfter))
            {
                return steps;
            }
        }

        throw new InvalidOperationException("the limit was never reached");
    }

    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(1200)]
    public void APassBehindTheClockEndsAtTheStepTheLimitNames(long exitAfter)
    {
        Assert.Equal(exitAfter, RunTo(exitAfter, askBeforeEveryStep: true));

        // checked only after the pass, the same passes end one or two steps past it at 101 and 1200 (three steps a pass: 4, 7, ... 100, 103)
        if (exitAfter % 3 != 1)
        {
            Assert.True(RunTo(exitAfter, askBeforeEveryStep: false) > exitAfter);
        }
    }
}
