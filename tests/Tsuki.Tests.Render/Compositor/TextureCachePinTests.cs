using System.Collections.Concurrent;
using Tsuki.Engine.Contracts;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// Issue #13 ("The game freezes at a certain scene"): one frame of a scene draws 566 MiB of picture tiles, and the cache of a card with 1.5 GB or less holds
/// 512 MiB. The frame's last uploads evicted its own first tiles, the frame was never complete, and the game kept the picture before it on the screen for ever.
/// The cache now keeps the textures of the frame being prepared (BeginFrame / Pin / TrimUnpinned, PinnedBytes) and the upload scheduler reports every uploaded
/// tile (Uploaded). The tiles here are synthetic and the sizes are the issue's scaled down (18 tiles of 256 KiB against a cap of 16): the freeze needs no game
/// file, only a frame with more bytes than the cap. Needs the local GPU; never skips.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class TextureCachePinTests(ITestOutputHelper output)
{
    private const int Side = 256;                               // 256 KiB RGBA8 tiles
    private const int TileBytes = Side * Side * 4;
    private const int CapTiles = 16;
    private const long Capacity = (long)CapTiles * TileBytes;   // 4 MiB
    private const int FrameTiles = 18;                          // 4.5 MiB in one frame: the 566 MiB against 512 MiB of the issue
    private const int Older = 100;                              // ids of the pictures of earlier scenes
    private const int Extra = 200;                              // ids of textures the frame does not draw

    [Fact]
    public void AFrameLargerThanTheCapKeepsEveryOneOfItsTextures()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 4L * TileBytes);
        for (int i = 0; i < CapTiles; i++)
        {
            Upload(cache, Older + i);                           // the cache is at its cap with the pictures of the scenes before
        }

        Assert.Equal(Capacity, cache.ResidentBytes);
        cache.BeginFrame();
        for (int i = 0; i < FrameTiles; i++)
        {
            Assert.False(cache.Pin(Key(i)));                    // named, not resident yet
        }

        Assert.Equal(0, cache.PinnedBytes);
        cache.TrimUnpinned();
        Assert.Equal(CapTiles, cache.Count);                    // within the cap: nothing to trim

        for (int i = 0; i < FrameTiles; i++)
        {
            Upload(cache, i);
            for (int j = 0; j <= i; j++)
            {
                Assert.True(cache.Contains(Key(j)), $"the upload of the frame's tile {i} evicted the frame's own tile {j}");
            }

            for (int j = 0; j < CapTiles; j++)
            {
                // the older pictures make room, least recently used first
                Assert.True(cache.Contains(Key(Older + j)) == j > i, $"after the frame's tile {i}: older picture {j} is {(j > i ? "gone" : "still resident")}");
            }

            Assert.Equal((i + 1L) * TileBytes, cache.PinnedBytes);
            Assert.Equal(Math.Max(Capacity, (i + 1L) * TileBytes), cache.ResidentBytes);
        }

        Assert.Equal(FrameTiles, cache.Count);
        Assert.Equal((long)FrameTiles * TileBytes, cache.ResidentBytes);
        Assert.True(cache.ResidentBytes > cache.CapacityBytes, "the frame has to be larger than the cap");
        Assert.Equal(cache.ResidentBytes, cache.PinnedBytes);
        Assert.Equal(CapTiles, cache.Stats.Evictions);
        Assert.True(cache.Stats.SpareBytes <= cache.SpareCapacityBytes);

        // every texture of the frame can be drawn and holds its own pixels
        var textures = new HashSet<uint>();
        for (int i = 0; i < FrameTiles; i++)
        {
            Assert.True(cache.TryGetTexture(Key(i), out uint texture, out _));
            Assert.True(texture != 0 && textures.Add(texture), $"tile {i} has texture {texture}");
            Assert.Equal(TextureUploadTests.Sha1(TextureUploadTests.Pixels(i, Side, Side)), TextureUploadTests.Sha1(cache.ReadBack(Key(i))));
        }

        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AFrameLargerThanTheCapIsCompleteAfterOnePassOfTheFeedersLoop()
    {
        // the loop of the game's texture feeder, pass after pass as the game repeats it while it shows the picture before: name the frame's textures, queue the
        // missing ones that are not queued, upload in slices, count what is missing. Before the fix every pass ended with two of the eighteen missing.
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 4L * TileBytes);
        var pool = new StagingPool();
        var queued = new HashSet<TextureRef>();
        var sched = new UploadScheduler(cache, pool, maxBytesPerFrame: 4L * TileBytes) { Uploaded = key => queued.Remove(key) };
        for (int i = 0; i < CapTiles; i++)
        {
            Upload(cache, Older + i);
        }

        var missingAfterPass = new List<int>();
        for (int pass = 0; pass < 30; pass++)
        {
            cache.BeginFrame();
            for (int i = 0; i < FrameTiles; i++)
            {
                if (!cache.Pin(Key(i)) && queued.Add(Key(i)))
                {
                    sched.Enqueue(Tile(pool, i));
                }
            }

            cache.TrimUnpinned();
            while (sched.Pending > 0)
            {
                Assert.InRange(sched.UploadSlice().Tiles, 1, 4);
            }

            int missing = Enumerable.Range(0, FrameTiles).Count(i => !cache.Contains(Key(i)));
            missingAfterPass.Add(missing);
            if (missing == 0)
            {
                break;
            }
        }

        Assert.True(missingAfterPass is [0], $"the frame was not complete after its first pass; textures missing after each pass: {string.Join(' ', missingAfterPass)}");
        Assert.Equal(FrameTiles, sched.UploadedTiles);
        Assert.Equal((long)FrameTiles * TileBytes, cache.PinnedBytes);
        Assert.True(cache.PinnedBytes > cache.CapacityBytes);
        Assert.Empty(queued);
        Assert.Equal(0, pool.OutstandingBytes);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void UnpinnedTexturesAreEvictedLeastRecentlyUsedFirstPastThePinnedOnes()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, 8L * TileBytes, 0);
        cache.BeginFrame();
        for (int p = 0; p < 4; p++)
        {
            Assert.False(cache.Pin(Key(p)));
        }

        for (int p = 0; p < 4; p++)
        {
            Upload(cache, p);                                   // the frame's textures are the least recently used entries from here on
        }

        for (int u = 0; u < 4; u++)
        {
            Upload(cache, Extra + u);
        }

        Assert.Equal(8L * TileBytes, cache.ResidentBytes);      // at the cap
        Assert.Equal(0, cache.Stats.Evictions);

        Upload(cache, Extra + 4);                               // one too many: the walk passes the four pinned at the tail and takes the oldest unpinned
        AssertResident(cache, [0, 1, 2, 3, Extra + 1, Extra + 2, Extra + 3, Extra + 4], gone: [Extra]);
        Upload(cache, Extra + 5);
        AssertResident(cache, [0, 1, 2, 3, Extra + 2, Extra + 3, Extra + 4, Extra + 5], gone: [Extra, Extra + 1]);

        Assert.True(cache.Touch(Key(Extra + 2)));               // used again: Extra + 3 is now the oldest unpinned
        Upload(cache, Extra + 6);
        AssertResident(cache, [0, 1, 2, 3, Extra + 2, Extra + 4, Extra + 5, Extra + 6], gone: [Extra + 3]);

        Assert.True(cache.TryGetTexture(Key(Extra + 4), out _, out _));     // drawn: Extra + 5 is now the oldest unpinned
        Upload(cache, Extra + 7);
        AssertResident(cache, [0, 1, 2, 3, Extra + 2, Extra + 4, Extra + 6, Extra + 7], gone: [Extra + 5]);
        Assert.Equal(4, cache.Stats.Evictions);

        Upload(cache, Extra + 8, 2 * Side, Side);               // two tiles' bytes: the two oldest unpinned go
        AssertResident(cache, [0, 1, 2, 3, Extra + 4, Extra + 7, Extra + 8], gone: [Extra + 2, Extra + 6]);
        Assert.Equal(6, cache.Stats.Evictions);
        Assert.Equal(8L * TileBytes, cache.ResidentBytes);
        Assert.Equal(4L * TileBytes, cache.PinnedBytes);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void APinnedResidentTextureIsTheNewestAsATouchedOne()
    {
        // package K15 through Pin: the feeder names a resident tile with Pin where it called Touch before, and the order it leaves is the order the frames after it evict in
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, 4L * TileBytes, 0);
        for (int i = 0; i < 4; i++)
        {
            Upload(cache, i);
        }

        cache.BeginFrame();
        Assert.True(cache.Pin(Key(0)));                         // the oldest becomes the newest
        Assert.False(cache.Pin(Key(99)));                       // a missing key is not created
        Assert.Equal(4, cache.Count);
        cache.BeginFrame();                                     // that frame is over: nothing is pinned
        for (int i = 10; i < 13; i++)
        {
            Upload(cache, i);
        }

        AssertResident(cache, [0, 10, 11, 12], gone: [1, 2, 3, 99]);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void TheLeftoversOfALargerFrameGoOnceTheNextFrameIsNamed()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 0);
        PrepareLargeFrame(cache);
        Assert.Equal((long)FrameTiles * TileBytes, cache.ResidentBytes);

        // the next frame is smaller: four tiles of the larger one (its least recently used) and two new pictures
        cache.BeginFrame();
        Assert.Equal(0, cache.PinnedBytes);
        Assert.Equal(FrameTiles, cache.Count);                  // BeginFrame alone evicts nothing: the frame is not named yet
        for (int i = 0; i < 4; i++)
        {
            Assert.True(cache.Pin(Key(i)));
        }

        Assert.False(cache.Pin(Key(Extra)));
        Assert.False(cache.Pin(Key(Extra + 1)));
        Assert.Equal(4L * TileBytes, cache.PinnedBytes);
        Assert.Equal(FrameTiles, cache.Count);                  // nor does Pin

        cache.TrimUnpinned();
        Assert.True(cache.ResidentBytes <= cache.CapacityBytes, $"{cache.ResidentBytes} bytes resident after the trim, the cap is {cache.CapacityBytes}");
        Assert.Equal(Capacity, cache.ResidentBytes);            // no more than needed
        AssertResident(cache, [0, 1, 2, 3, .. Enumerable.Range(6, 12)], gone: [4, 5]);      // the oldest leftovers went, not what the frame draws
        Assert.Equal(2, cache.Stats.Evictions);

        Upload(cache, Extra);
        Upload(cache, Extra + 1);
        AssertResident(cache, [0, 1, 2, 3, Extra, Extra + 1, .. Enumerable.Range(8, 10)], gone: [4, 5, 6, 7]);
        Assert.Equal(Capacity, cache.ResidentBytes);
        Assert.Equal(6L * TileBytes, cache.PinnedBytes);

        cache.TrimUnpinned();                                   // within the cap: nothing more goes
        Assert.Equal(CapTiles, cache.Count);
        Assert.Equal(4, cache.Stats.Evictions);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AFrameThatDrawsNoTextureLetsALargerFramesLeftoversGoDownToTheCap()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 0);
        PrepareLargeFrame(cache);
        cache.BeginFrame();
        cache.TrimUnpinned();
        Assert.Equal(Capacity, cache.ResidentBytes);
        Assert.Equal(0, cache.PinnedBytes);
        AssertResident(cache, [.. Enumerable.Range(2, 16)], gone: [0, 1]);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void PinnedBytesCountsTheResidentTexturesOfTheFrameOnce()
    {
        using GlContext ctx = GlContext.CreateHidden();
        var cache = new TextureCache(ctx.Device.Gl, 64L << 20, 0);
        TextureRef a = Key(1), b = Key(2), c = Key(3);
        const long B16 = 16 * 16 * 4, B32 = 32 * 32 * 4, B64 = 64 * 64 * 4, B128 = 128 * 128 * 4;

        cache.BeginFrame();
        Assert.False(cache.Pin(a));                             // pin before upload: named, nothing to count yet
        Assert.Equal(0, cache.PinnedBytes);
        cache.Upload(a, 64, 64, new byte[B64]);                 // its upload counts it
        Assert.Equal(B64, cache.PinnedBytes);

        cache.Upload(b, 32, 32, new byte[B32]);                 // not named: not counted
        Assert.Equal(B64, cache.PinnedBytes);
        Assert.True(cache.Pin(b));                              // pin after upload
        Assert.Equal(B64 + B32, cache.PinnedBytes);

        Assert.True(cache.Pin(b));                              // pin twice (two quads of a frame draw the same tile)
        Assert.True(cache.Pin(a));
        Assert.Equal(B64 + B32, cache.PinnedBytes);

        cache.Upload(c, 16, 16, new byte[B16]);                 // a texture the frame does not draw
        Assert.Equal(B64 + B32, cache.PinnedBytes);
        Assert.Equal(B64 + B32 + B16, cache.ResidentBytes);

        cache.Upload(a, 128, 128, new byte[B128]);              // a pinned key replaced by a larger texture (a movie frame, a composed picture)
        Assert.Equal(B128 + B32, cache.PinnedBytes);
        Assert.Equal(B128 + B32 + B16, cache.ResidentBytes);
        cache.Upload(a, 16, 16, new byte[B16]);                 // and by a smaller one
        Assert.Equal(B16 + B32, cache.PinnedBytes);
        cache.Upload(c, 64, 64, new byte[B64]);                 // an unpinned key replaced
        Assert.Equal(B16 + B32, cache.PinnedBytes);
        Assert.Equal(B16 + B32 + B64, cache.ResidentBytes);

        Assert.True(cache.Remove(b));                           // Remove of a pinned key
        Assert.Equal(B16, cache.PinnedBytes);
        Assert.False(cache.Remove(b));                          // of one that is not resident
        Assert.False(cache.Pin(b));                             // still named by the frame, nothing to count
        Assert.Equal(B16, cache.PinnedBytes);
        cache.Upload(b, 64, 64, new byte[B64]);                 // uploaded again within the frame: counted again
        Assert.Equal(B16 + B64, cache.PinnedBytes);
        Assert.True(cache.Remove(c));                           // Remove of an unpinned key
        Assert.Equal(B16 + B64, cache.PinnedBytes);
        Assert.Equal(B16 + B64, cache.ResidentBytes);

        cache.BeginFrame();                                     // the next frame has named nothing
        Assert.Equal(0, cache.PinnedBytes);
        Assert.Equal(2, cache.Count);
        cache.Upload(a, 32, 32, new byte[B32]);                 // a key of the frame before is not one of this frame's
        Assert.Equal(0, cache.PinnedBytes);
        Assert.True(cache.Pin(a));
        Assert.Equal(B32, cache.PinnedBytes);

        cache.Dispose();
        Assert.Equal(0, cache.PinnedBytes);
        Assert.Equal(0, cache.ResidentBytes);
        Assert.Equal(0, cache.Count);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void PinnedBytesAndTheFramesTexturesHoldOverRandomFrames()
    {
        // random frames, uploads, removals and draws against what the cache says: PinnedBytes is the bytes of the resident textures the frame named, no named
        // texture is evicted, and the cache is above its cap only with nothing left to evict
        using GlContext ctx = GlContext.CreateHidden();
        const long Cap = 24L << 10;
        using var cache = new TextureCache(ctx.Device.Gl, Cap, 8L << 10);
        (int W, int H)[] shapes = [(16, 16), (32, 16), (32, 32), (64, 32)];
        var rng = new Random(13);
        var size = new Dictionary<TextureRef, long>();          // the bytes last uploaded under each key
        var named = new HashSet<TextureRef>();                  // the frame being prepared
        var keep = new List<TextureRef>();
        int frames = 0, aboveCap = 0, uploads = 0;
        for (int step = 0; step < 6000; step++)
        {
            TextureRef key = Key(rng.Next(48));
            int op = rng.Next(100);
            keep.Clear();
            keep.AddRange(named.Where(k => cache.Contains(k)));
            bool trims = false;
            string what;
            if (op < 3)
            {
                what = "BeginFrame";
                cache.BeginFrame();
                named.Clear();
                keep.Clear();
                frames++;
            }
            else if (op < 38)
            {
                what = $"Pin {key.C}";
                Assert.Equal(cache.Contains(key), cache.Pin(key));
                named.Add(key);
            }
            else if (op < 78)
            {
                (int w, int h) = shapes[rng.Next(shapes.Length)];
                what = $"Upload {key.C} {w} x {h}";
                cache.Upload(key, w, h, new byte[w * h * 4]);
                size[key] = w * h * 4;
                uploads++;
                trims = true;
                Assert.True(!named.Contains(key) || cache.Contains(key), $"step {step}: the upload of the named texture {key.C} did not leave it resident");
            }
            else if (op < 84)
            {
                what = "TrimUnpinned";
                cache.TrimUnpinned();
                trims = true;
            }
            else if (op < 90)
            {
                what = $"Remove {key.C}";
                Assert.Equal(cache.Contains(key), cache.Remove(key));
                keep.Remove(key);
            }
            else if (op < 95)
            {
                what = $"Touch {key.C}";
                Assert.Equal(cache.Contains(key), cache.Touch(key));
            }
            else
            {
                what = $"TryGetTexture {key.C}";
                Assert.Equal(cache.Contains(key), cache.TryGetTexture(key, out _, out _));
            }

            foreach (TextureRef k in keep)
            {
                Assert.True(cache.Contains(k), $"step {step} ({what}): the frame's texture {k.C} was evicted");
            }

            long resident = 0, pinned = 0;
            foreach ((TextureRef k, long bytes) in size)
            {
                if (cache.Contains(k))
                {
                    resident += bytes;
                    pinned += named.Contains(k) ? bytes : 0;
                }
            }

            Assert.True(resident == cache.ResidentBytes, $"step {step} ({what}): ResidentBytes {cache.ResidentBytes}, the resident textures have {resident}");
            Assert.True(pinned == cache.PinnedBytes, $"step {step} ({what}): PinnedBytes {cache.PinnedBytes}, the resident textures of the frame have {pinned}");
            if (trims && cache.ResidentBytes > Cap)
            {
                aboveCap++;
                Assert.True(cache.Count == 1 || pinned == resident, $"step {step} ({what}): {resident} bytes resident above the cap of {Cap} with {resident - pinned} bytes the frame does not draw");
            }
        }

        output.WriteLine($"{frames} frames, {uploads} uploads, {cache.Stats.Evictions} evictions, {aboveCap} trims that left the cache above its cap");
        Assert.True(frames > 50 && cache.Stats.Evictions > 500, $"{frames} frames, {cache.Stats.Evictions} evictions");
        Assert.True(aboveCap > 100, $"only {aboveCap} trims left the cache above its cap: the run has to reach frames larger than the cap");
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AnUnpinnedTextureUploadedWhileTheFrameFillsTheCacheIsEvictedAtOnce()
    {
        // what happens to a texture nobody named (a look-ahead tile) when the frame alone is larger than the cap: there is no room for it, so its own upload
        // evicts it. That is why the feeder stops its look-ahead for such a frame, and why it must not wait to see an uploaded tile resident.
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 0);
        PrepareLargeFrame(cache);
        Assert.Equal(0, cache.Stats.Evictions);

        Upload(cache, Extra);
        Assert.False(cache.Contains(Key(Extra)));
        Assert.False(cache.TryGetTexture(Key(Extra), out _, out _));
        Assert.Equal(1, cache.Stats.Evictions);
        Assert.Equal(FrameTiles, cache.Count);
        Assert.Equal((long)FrameTiles * TileBytes, cache.ResidentBytes);
        Assert.Equal((long)FrameTiles * TileBytes, cache.PinnedBytes);
        AssertResident(cache, [.. Enumerable.Range(0, FrameTiles)], gone: [Extra]);

        // naming it afterwards finds nothing; from then on it is one of the frame's textures and its next upload stays
        Assert.False(cache.Pin(Key(Extra)));
        Assert.Equal((long)FrameTiles * TileBytes, cache.PinnedBytes);
        Upload(cache, Extra);
        Assert.True(cache.Contains(Key(Extra)));
        Assert.Equal((FrameTiles + 1L) * TileBytes, cache.PinnedBytes);
        Assert.Equal(1, cache.Stats.Evictions);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AnUnpinnedTextureStaysWhileThereIsRoomBesideTheFramesTextures()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 0);
        cache.BeginFrame();
        for (int i = 0; i < CapTiles - 1; i++)
        {
            cache.Pin(Key(i));
            Upload(cache, i);                                   // the frame leaves room for one tile
        }

        Upload(cache, Extra);
        Assert.True(cache.Contains(Key(Extra)));
        Assert.Equal(Capacity, cache.ResidentBytes);
        Upload(cache, Extra + 1);                               // the next one takes its place
        AssertResident(cache, [.. Enumerable.Range(0, CapTiles - 1), Extra + 1], gone: [Extra]);
        Assert.True(cache.Remove(Key(Extra + 1)));

        // a texture larger than the room that is left is evicted at once, although the frame is below the cap
        Assert.True(cache.PinnedBytes < cache.CapacityBytes);
        Upload(cache, Extra + 2, 2 * Side, Side);
        Assert.False(cache.Contains(Key(Extra + 2)));
        Assert.Equal((CapTiles - 1L) * TileBytes, cache.ResidentBytes);
        Assert.Equal(cache.ResidentBytes, cache.PinnedBytes);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void TheSchedulerReportsEveryUploadedTileOnceWithItsKey()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl);
        var pool = new StagingPool();
        var reported = new List<(int Id, bool Resident)>();
        var sched = new UploadScheduler(cache, pool, maxBytesPerFrame: 4L * TileBytes) { Uploaded = key => reported.Add((key.C, cache.Contains(key))) };
        for (int i = 0; i < 6; i++)
        {
            sched.Enqueue(Tile(pool, i));                       // tiles a frame waits for, 256 KiB each: four fill a slice
        }

        for (int i = 20; i < 26; i++)
        {
            sched.EnqueueBackground(Tile(pool, i, 512, 512));   // look-ahead tiles, 1 MiB each: four go up with each slice
        }

        sched.EnqueueBackground(Tile(pool, 21, 512, 512));      // decoded twice: the second copy is dropped, not uploaded
        Assert.True(sched.Promote(Key(25)));                    // a frame names it: it joins the urgent tiles
        Assert.Equal(12, sched.Pending);
        Assert.Empty(reported);                                 // queued is not uploaded

        Assert.Equal(8, sched.UploadSlice().Tiles);
        Assert.Equal([0, 1, 2, 3, 20, 21, 22, 23], reported.Select(r => r.Id));
        Assert.Equal(3, sched.UploadSlice().Tiles);             // tile 4 was carried over from the first slice, the promoted tile 25 is carried to the third
        Assert.Equal([0, 1, 2, 3, 20, 21, 22, 23, 4, 5, 24], reported.Select(r => r.Id));
        Assert.Equal(1, sched.UploadSlice().Tiles);
        Assert.Equal([0, 1, 2, 3, 20, 21, 22, 23, 4, 5, 24, 25], reported.Select(r => r.Id));
        Assert.Equal(0, sched.UploadSlice().Tiles);             // nothing queued: nothing reported
        Assert.Equal(12, reported.Count);

        Assert.All(reported, r => Assert.True(r.Resident, $"tile {r.Id} was reported before it was in the cache"));
        Assert.Equal(12, sched.UploadedTiles);
        Assert.Equal(0, sched.Pending);
        Assert.Equal(0, pool.OutstandingBytes);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void ATileTheCacheEvictsWithItsOwnUploadIsStillReported()
    {
        // the feeder forgot a queued key only when it saw the tile resident: a tile the cache evicted before that stayed "queued" for ever, and the frame that
        // named it later waited for a decode that never came (issue #13). The report comes whether or not the cache keeps the tile.
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl, Capacity, 0);
        PrepareLargeFrame(cache);
        var pool = new StagingPool();
        var reported = new List<(int Id, bool Resident)>();
        var sched = new UploadScheduler(cache, pool) { Uploaded = key => reported.Add((key.C, cache.Contains(key))) };
        sched.EnqueueBackground(Tile(pool, Extra));             // a look-ahead tile
        sched.Enqueue(Tile(pool, Extra + 1));                   // a tile of a frame that is not the one being prepared any more
        sched.Enqueue(Tile(pool, 5));                           // a tile of the frame, decoded again
        Assert.Equal(3, sched.UploadSlice().Tiles);

        Assert.Equal([(Extra + 1, false), (5, true), (Extra, false)], reported);
        AssertResident(cache, [.. Enumerable.Range(0, FrameTiles)], gone: [Extra, Extra + 1]);
        Assert.Equal(0, sched.Pending);
        Assert.Equal(0, pool.OutstandingBytes);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void TilesDecodedOnWorkerThreadsAreReportedOnTheThreadThatUploads()
    {
        // the feeder's callback changes a set that only the GL thread touches
        using GlContext ctx = GlContext.CreateHidden();
        using var cache = new TextureCache(ctx.Device.Gl);
        var pool = new StagingPool();
        int glThread = Environment.CurrentManagedThreadId;
        var reported = new ConcurrentQueue<(int Id, int Thread)>();
        var decodeThreads = new ConcurrentDictionary<int, bool>();
        var sched = new UploadScheduler(cache, pool) { Uploaded = key => reported.Enqueue((key.C, Environment.CurrentManagedThreadId)) };
        int[] urgent = [.. Enumerable.Range(0, 12)], background = [.. Enumerable.Range(20, 8)];
        using (var workers = new DecodeWorkers(sched))
        {
            foreach (int id in urgent)
            {
                workers.Enqueue(new DecodeJob(Key(id), Side, Side, px => Decode(px, id)));
            }

            foreach (int id in background)
            {
                workers.EnqueueBackground(new DecodeJob(Key(id), Side, Side, px => Decode(px, id)));
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (workers.Pending > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }

            Assert.Null(workers.Fault);
            Assert.Equal(0, workers.Pending);
            Assert.Equal(urgent.Length + background.Length, sched.Pending);
            Assert.Empty(reported);                             // decoded and queued on the workers: nothing is reported until the GL thread uploads
            while (sched.Pending > 0)
            {
                sched.UploadSlice();
            }
        }

        Assert.Equal([.. urgent, .. background], reported.Select(r => r.Id).Order());
        Assert.All(reported, r => Assert.True(r.Thread == glThread, $"tile {r.Id} was reported on thread {r.Thread}, the uploads run on thread {glThread}"));
        Assert.NotEmpty(decodeThreads);
        Assert.DoesNotContain(glThread, decodeThreads.Keys);
        Assert.Null(ctx.Device.TakeError());

        void Decode(Span<byte> px, int id)
        {
            decodeThreads[Environment.CurrentManagedThreadId] = true;
            TextureUploadTests.Fill(px, id);
        }
    }

    private static TextureRef Key(int id) => TextureUploadTests.Key(id);

    private static void Upload(TextureCache cache, int id, int width = Side, int height = Side) =>
        cache.Upload(Key(id), width, height, TextureUploadTests.Pixels(id, width, height));

    private static DecodedTile Tile(StagingPool pool, int id, int width = Side, int height = Side)
    {
        StagingBuffer b = pool.Rent(width * height * 4);
        TextureUploadTests.Fill(b.Span, id);
        return new DecodedTile(Key(id), width, height, b);
    }

    // a frame of FrameTiles tiles in a cache whose cap is CapTiles of them, the way the feeder prepares it: begin, name every texture, trim, upload the missing ones
    private static void PrepareLargeFrame(TextureCache cache)
    {
        cache.BeginFrame();
        for (int i = 0; i < FrameTiles; i++)
        {
            cache.Pin(Key(i));
        }

        cache.TrimUnpinned();
        for (int i = 0; i < FrameTiles; i++)
        {
            Upload(cache, i);
        }

        for (int i = 0; i < FrameTiles; i++)
        {
            Assert.True(cache.Contains(Key(i)), $"tile {i} of the larger frame is not resident");
        }
    }

    private static void AssertResident(TextureCache cache, int[] resident, int[] gone)
    {
        foreach (int id in resident)
        {
            Assert.True(cache.Contains(Key(id)), $"texture {id} was evicted");
        }

        foreach (int id in gone)
        {
            Assert.False(cache.Contains(Key(id)), $"texture {id} is still resident");
        }

        Assert.Equal(resident.Length, cache.Count);
    }
}
