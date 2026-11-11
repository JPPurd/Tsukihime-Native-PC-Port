using Tsuki.Engine.Contracts;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// Issue #13, the damaged-file side: a tile whose decode throws never becomes resident. The workers say which keys failed, so the feeder does not wait for
/// them (the hold for textures would end only at its limit, every time a frame draws such a tile), and a job that is being picked up counts as pending.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class DecodeFailureTests
{
    private const int Side = 64;
    private const int TileBytes = Side * Side * 4;

    private static TextureRef Key(int i) => new(TextureKind.PictureTile, 9, 0, i, 0);

    [Fact]
    public void ATileWhoseDecodeThrowsIsKnownAsFailedAndTheOthersStillArrive()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, 64L << 20, 0);
        var pool = new StagingPool(8L * TileBytes);
        var sched = new UploadScheduler(cache, pool);
        using (var workers = new DecodeWorkers(sched, 2))
        {
            Assert.False(workers.HasFailed(Key(0)));
            workers.Enqueue(new DecodeJob(Key(0), Side, Side, rgba => rgba.Fill(0x40)));
            workers.Enqueue(new DecodeJob(Key(1), Side, Side, _ => throw new InvalidDataException("a damaged tile")));
            workers.EnqueueBackground(new DecodeJob(Key(2), Side, Side, _ => throw new InvalidDataException("a damaged look-ahead tile")));
            workers.Enqueue(new DecodeJob(Key(3), Side, Side, rgba => rgba.Fill(0x80)));

            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    sched.UploadSlice();
                    return workers.Pending == 0 && sched.Pending == 0;
                },
                TimeSpan.FromSeconds(20)), "the jobs did not finish");

            Assert.True(cache.Contains(Key(0)));
            Assert.True(cache.Contains(Key(3)));
            Assert.False(cache.Contains(Key(1)));
            Assert.False(cache.Contains(Key(2)));
            Assert.True(workers.HasFailed(Key(1)));                 // not decoded again: the feeder draws the frame without it
            Assert.True(workers.HasFailed(Key(2)));
            Assert.False(workers.HasFailed(Key(0)));
            Assert.False(workers.HasFailed(Key(3)));
            Assert.IsType<InvalidDataException>(workers.Fault);     // the first failure, as before
            Assert.Equal(0, pool.OutstandingBytes);                 // the failed jobs returned their buffers
        }
    }

    [Fact]
    public void AJobCountsAsPendingFromTheMomentItIsQueuedUntilItsTileIsHandedOver()
    {
        // between leaving its queue and being counted as in flight a job was counted nowhere: Pending read 0 with a tile on its way, and the hold for
        // textures (which gives up sooner when nothing is pending) could end a moment before the tile arrived
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, 64L << 20, 0);
        var pool = new StagingPool(64L * TileBytes);
        var sched = new UploadScheduler(cache, pool);
        using var workers = new DecodeWorkers(sched, 4);
        int zeroSeen = 0;
        for (int round = 0; round < 200; round++)
        {
            using var gate = new ManualResetEventSlim(false);
            TextureRef key = Key(100 + round);
            workers.Enqueue(new DecodeJob(key, Side, Side, rgba =>
            {
                gate.Wait(TimeSpan.FromSeconds(20));
                rgba.Clear();
            }));

            // the job is queued or in a worker's hands, and its decode cannot finish before the gate opens: never 0 in between
            for (int look = 0; look < 2000; look++)
            {
                if (workers.Pending + sched.Pending == 0)
                {
                    zeroSeen++;
                }
            }

            gate.Set();
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    sched.UploadSlice();
                    return cache.Contains(key);
                },
                TimeSpan.FromSeconds(20)), $"tile {round} did not arrive");
        }

        Assert.Equal(0, zeroSeen);
    }
}
