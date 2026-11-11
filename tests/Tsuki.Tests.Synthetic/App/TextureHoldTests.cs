using Tsuki.App.Play;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// Issue #13: the hold for textures has an end. The game keeps the picture before a frame whose textures are not all resident; a frame that could never be
/// complete (more textures than the cache's cap, or a tile that does not decode) kept it for ever. <see cref="TextureHold"/> is the rule of when the hold is
/// given up, and the two lines the log gets.
/// </summary>
public sealed class TextureHoldTests
{
    [Fact]
    public void TheLimitsAreOneSecondWithNothingOnItsWayAndEightSecondsOtherwise()
    {
        Assert.Equal(1000, TextureHold.IdleLimitMs);
        Assert.Equal(8000, TextureHold.LimitMs);
        Assert.True(TextureHold.IdleLimitMs < TextureHold.LimitMs);
    }

    [Theory]
    [InlineData(0, false)]                          // a hold that has just begun
    [InlineData(16.7, false)]                       // one step
    [InlineData(999, false)]
    [InlineData(999.999, false)]                    // just under the limit
    [InlineData(1000, true)]                        // the limit itself
    [InlineData(1000.001, true)]
    [InlineData(7999, true)]
    [InlineData(8000, true)]
    [InlineData(1e9, true)]
    public void AHoldWithNothingOnItsWayIsGivenUpAtOneSecond(double heldMs, bool givenUp)
    {
        Assert.Equal(givenUp, TextureHold.GiveUp(heldMs, pending: 0));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(999.999, 1, false)]
    [InlineData(1000, 1, false)]                    // the idle limit does not count while one tile is still being decoded or uploaded
    [InlineData(1000.001, 675, false)]
    [InlineData(7999, 1, false)]
    [InlineData(7999.999, 1, false)]                // just under the limit
    [InlineData(7999.999, 675, false)]
    [InlineData(8000, 1, true)]                     // the limit itself
    [InlineData(8000, 675, true)]
    [InlineData(8000.001, 1, true)]
    [InlineData(1e9, int.MaxValue, true)]
    public void AHoldWithTilesInFlightIsGivenUpAtEightSeconds(double heldMs, int pending, bool givenUp)
    {
        Assert.Equal(givenUp, TextureHold.GiveUp(heldMs, pending));
    }

    [Fact]
    public void EveryHoldEndsWhateverIsInFlight()
    {
        // no number of tiles in flight keeps the picture before the frame on the screen past the long limit
        foreach (int pending in new[] { 0, 1, 2, 64, 5000, int.MaxValue })
        {
            Assert.True(TextureHold.GiveUp(TextureHold.LimitMs, pending), $"{pending} tiles in flight");
            Assert.False(TextureHold.GiveUp(TextureHold.IdleLimitMs - 0.001, pending), $"{pending} tiles in flight");
        }
    }

    [Fact]
    public void TheGivenUpLineSaysHowLongWhatIsMissingAndThatNothingIsOnItsWay()
    {
        string line = TextureHold.GaveUpLine(heldMs: 1003.6, missing: 37, pending: 0, script: 70, residentBytes: 511L << 20, capacityBytes: 512L << 20);
        Assert.Equal(
            "gave up holding for textures after 1004 ms: 37 of this frame's textures are not resident and none is on its way (script 70, cache 511 of 512 MiB); the frame is drawn without them",
            line);
    }

    [Fact]
    public void TheGivenUpLineCountsTheTilesStillInFlight()
    {
        string line = TextureHold.GaveUpLine(heldMs: 8000.4, missing: 2, pending: 675, script: 3, residentBytes: (1536L << 20) + 5, capacityBytes: 1536L << 20);
        Assert.Equal(
            "gave up holding for textures after 8000 ms: 2 of this frame's textures are not resident and 675 tiles are still in flight (script 3, cache 1536 of 1536 MiB); the frame is drawn without them",
            line);
    }

    [Fact]
    public void TheOverCapLineNamesTheFramesSizeTheCapAndTheScript()
    {
        // the numbers of the scene of issue #13: 566 MiB in one frame with the 512 MiB cap of a card with 1.5 GB or less
        string line = TextureHold.OverCapLine(frameBytes: (566L << 20) + 123_456, capacityBytes: 512L << 20, script: 70);
        Assert.Equal("this frame draws 566 MiB of textures, more than the cache's 512 MiB cap: they all stay resident for it (script 70)", line);
    }

    [Theory]
    [InlineData(0, 0.5, 0.5)]                       // a pass of the loop during a hold adds what it took
    [InlineData(900, 16.7, 916.7)]
    [InlineData(900, 100, 1000)]                    // up to the cap
    [InlineData(900, 100.001, 1000)]
    [InlineData(900, 8_000_000, 1000)]              // the computer slept for hours during a hold: one pass counts as one cap, the hold is not given up for it
    [InlineData(500, -3, 500)]                      // a clock that stepped back adds nothing
    public void AHoldsAgeCountsThePassesOfTheLoopNotTheTimeItDidNotRun(double age, double sincePass, double expected)
    {
        Assert.Equal(expected, TextureHold.Age(age, sincePass), 6);
        Assert.Equal(100, TextureHold.PassCapMs);
    }

    [Theory]
    [InlineData(566, 512, 70, -1, 0, true)]         // the first frame above the cap of a script
    [InlineData(566, 512, 70, 70, 566, false)]      // the same frame again: said once
    [InlineData(590, 512, 70, 70, 566, false)]      // a little larger
    [InlineData(598, 512, 70, 70, 566, true)]       // 32 MiB larger: said again, so the log has what the scene needs at its most
    [InlineData(566, 512, 71, 70, 798, true)]       // another script
    [InlineData(512, 512, 70, -1, 0, false)]        // at the cap is not above it
    [InlineData(100, 512, 71, 70, 566, false)]
    public void AFrameAboveTheCapIsSaidOncePerScriptAndAgainWhenItHasGrown(int frameMib, int capMib, int script, int loggedScript, int loggedMib, bool said)
    {
        Assert.Equal(said, TextureHold.SayOverCap((long)frameMib << 20, (long)capMib << 20, script, loggedScript, (long)loggedMib << 20));
        Assert.Equal(32L << 20, TextureHold.OverCapStepBytes);
    }
}
