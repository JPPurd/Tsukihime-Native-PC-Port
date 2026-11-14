namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// The upload timing gate's decision from its idle control, which frames that control is, when the gate stops and its verdict over the runs (v0.1.9, FIXES
/// D10 Q-2), without a window, GL or game data. The gate itself
/// (<see cref="UploadTimingTests"/>) shows a real window, so most runs leave it out with <c>FullyQualifiedName!~UploadTimingTests</c>; this class's
/// name does not contain that text, so its tests run in those runs too.
/// </summary>
public sealed class UploadTimingGateTests
{
    [Fact]
    public void APcThatPresentsAt60HzOrFasterIsJudgedAsBefore()
    {
        Assert.False(UploadTimingGate.CannotJudge(Same(60, 6.94)));        // a 144 Hz display
        Assert.False(UploadTimingGate.CannotJudge(Same(60, 16.67)));       // 60 Hz
        Assert.False(UploadTimingGate.CannotJudge(Same(60, 16.7)));        // a frame of exactly the limit is not over it
        Assert.False(UploadTimingGate.CannotJudge(Mixed(60, over: 30)));   // half of them over is not more than half
        Assert.False(UploadTimingGate.CannotJudge(Mixed(60, over: 1)));    // a hitch or two in the warm-up
        Assert.False(UploadTimingGate.CannotJudge([]));                    // no idle frame is no evidence: the gate asserts
    }

    [Fact]
    public void APcThatPresentsSlowerThan60HzCannotJudgeTheGate()
    {
        Assert.True(UploadTimingGate.CannotJudge(Same(60, 30.3)));         // the owner's PC on 2026-11-12, a game running: 300 of 300 at about 30 ms
        Assert.True(UploadTimingGate.CannotJudge(Same(60, 16.71)));
        Assert.True(UploadTimingGate.CannotJudge(Mixed(60, over: 31)));    // more than half
        Assert.True(UploadTimingGate.CannotJudge(Mixed(1, over: 1)));
        Assert.Equal(16.7, UploadTimingGate.FrameLimitMs);
    }

    [Fact]
    public void TheSkipGivesTheIdleNumbersAndSaysWhatTheGateNeeds()
    {
        string first = UploadTimingGate.SkipMessage(0, Same(60, 30.3), overBefore: 0);
        Assert.Contains("in run 0 the idle control (warm-up frames 60..119, nothing loading) had 60 of 60 frames over 16.7 ms (median 30.3 ms, about 33 Hz)", first, StringComparison.Ordinal);
        Assert.Contains("not presented at 60 Hz or faster", first, StringComparison.Ordinal);
        Assert.Contains("cannot tell an upload stall from the presentation rate", first, StringComparison.Ordinal);
        Assert.Contains("The load finished, run 0's tiles read back equal and GL reported no error.", first, StringComparison.Ordinal);
        Assert.EndsWith("The gate needs a PC presenting at 60 Hz or faster (no game or other program busy with the GPU).", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Runs 0..", first, StringComparison.Ordinal);

        // a later run whose control failed also says what the runs before it measured
        string later = UploadTimingGate.SkipMessage(2, Mixed(60, over: 45), overBefore: 3);
        Assert.Contains("in run 2 the idle control (warm-up frames 60..119, nothing loading) had 45 of 60 frames over 16.7 ms", later, StringComparison.Ordinal);
        Assert.Contains("Runs 0..1 passed their idle control, with 3 load frames over 16.7 ms.", later, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdleControlIsTheWarmUpFrames60To119()
    {
        Assert.Equal((60, 120), (UploadTimingGate.IdleFirstFrame, UploadTimingGate.WarmFrames));
        Assert.Equal(Enumerable.Range(60, 60), Enumerable.Range(0, 200).Where(UploadTimingGate.IsIdleFrame));   // 59 and 120 (the load's first frame) are not
        Assert.Contains("(warm-up frames 60..119, nothing loading)", UploadTimingGate.SkipMessage(0, Same(60, 30.3), overBefore: 0), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateMeasuresNoRunAfterOneWhoseControlFailed()
    {
        Assert.True(UploadTimingGate.MeasuresAnotherRun([]));
        Assert.True(UploadTimingGate.MeasuresAnotherRun([Passed(0), Passed(2)]));
        Assert.False(UploadTimingGate.MeasuresAnotherRun([Failed(0)]));                    // the owner's PC: the window is shown for run 0 alone
        Assert.False(UploadTimingGate.MeasuresAnotherRun([Passed(0), Failed(0)]));
    }

    [Fact]
    public void EveryControlPassedIsJudgedAsBefore()
    {
        Assert.Equal(new GateVerdict(0, null), UploadTimingGate.Judge([Passed(0), Passed(0), Passed(0), Passed(0), Passed(0)]));
        Assert.Equal(new GateVerdict(3, null), UploadTimingGate.Judge([Passed(0), Passed(1), Passed(0), Passed(2), Passed(0)]));   // fails: 3 frames over
        Assert.Equal(new GateVerdict(0, null), UploadTimingGate.Judge([]));
    }

    [Fact]
    public void ARunWhoseControlFailedEndsInASkipButTheRunsBeforeItAreStillJudged()
    {
        // run 0's control failed: a skip, whatever its load frames were (they say nothing at 30 Hz)
        GateVerdict first = UploadTimingGate.Judge([Failed(over: 40)]);
        Assert.Equal(0, first.JudgedOver);
        Assert.StartsWith("This PC cannot judge the upload timing gate: in run 0 ", first.Skip, StringComparison.Ordinal);

        // run 0 passed with no frame over, then the PC slowed down: still a skip, and the message says what run 0 measured
        GateVerdict later = UploadTimingGate.Judge([Passed(0), Failed(over: 40)]);
        Assert.Equal(0, later.JudgedOver);
        Assert.Contains("in run 1 the idle control", later.Skip, StringComparison.Ordinal);
        Assert.Contains("Runs 0..0 passed their idle control, with 0 load frames over 16.7 ms.", later.Skip, StringComparison.Ordinal);

        // run 0 passed its control but had a real stall (3 load frames at 25 ms), then the PC slowed down: the gate fails on run 0, as it always did, before
        // any skip (the critic's case of 2026-11-12)
        GateVerdict stalled = UploadTimingGate.Judge([Passed(3), Failed(over: 40)]);
        Assert.Equal(3, stalled.JudgedOver);
        Assert.Contains("Runs 0..0 passed their idle control, with 3 load frames over 16.7 ms.", stalled.Skip, StringComparison.Ordinal);

        // the load frames of the run whose control failed are not counted, and a run after it (the gate measures none) changes nothing
        Assert.Equal(new GateVerdict(1, UploadTimingGate.SkipMessage(2, Same(60, 30.3), overBefore: 1)), UploadTimingGate.Judge([Passed(1), Passed(0), Failed(over: 9), Passed(5)]));
    }

    /// <summary>A run whose idle control passed (a 144 Hz display, two hitches), with <paramref name="over"/> load frames over the limit.</summary>
    private static GateRun Passed(int over) => new(Mixed(60, over: 2), over);

    /// <summary>A run whose idle control failed (the owner's PC on 2026-11-12: every idle frame at about 30 ms).</summary>
    private static GateRun Failed(int over) => new(Same(60, 30.3), over);

    private static double[] Same(int count, double ms) => [.. Enumerable.Repeat(ms, count)];

    /// <summary><paramref name="count"/> idle frames, <paramref name="over"/> of them at 33 ms and the rest at a 144 Hz display's 6.9 ms.</summary>
    private static double[] Mixed(int count, int over) => [.. Enumerable.Range(0, count).Select(i => i < over ? 33.0 : 6.9)];
}
