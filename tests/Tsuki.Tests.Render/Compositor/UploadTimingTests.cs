using System.Diagnostics;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Platform;
using Tsuki.Render.Compositor;
using Tsuki.Render.Textures;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// The upload scheduler with S4's five rules, timed (PACE 7.2 #5 D1 "upload with 0 frames over 16.7 ms"; S4.md 5): the
/// largest picture IMG1481 (allpac1 #1259, 330 live tiles, 330 MiB) is decoded by the decode workers and uploaded in
/// 16 MiB slices while a visible 1280 x 720 window presents at vsync and draws every resident tile through the
/// display-list executor. No presented frame of the load may take more than 16.7 ms. Holds artifacts/gpu.lock
/// (decision 4). The window closes itself well within 60 s.
/// Each run's warm-up frames 60..119 (nothing loading) are its idle control: when more than half of them are over the limit,
/// this PC is not presenting the window at 60 Hz or faster and the limit cannot tell an upload stall from the presentation rate
/// (<see cref="UploadTimingGate"/>, v0.1.9 FIXES D10 Q-2). The test then stops after that run, keeps its functional checks
/// (the load finishes, run 0's tiles read back equal, no GL error), still fails for a load frame over the limit in a run before it whose control passed
/// (each such run is judged as before), and is otherwise skipped with the idle numbers. When every control passes it asserts as before.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class UploadTimingTests(ITestOutputHelper output)
{
    private const int Runs = 5;
    private const int WarmFrames = UploadTimingGate.WarmFrames;
    private const int TailFrames = 30;
    private const double FrameLimitMs = UploadTimingGate.FrameLimitMs;

    [GameDataFact]
    public void UploadingTheLargestPictureKeepsEveryFrameUnder16_7Milliseconds() => Measure(prewarm: true, runs: Runs);

    /// <summary>
    /// The same load with lazily allocated textures (one glTexImage2D per uploaded tile; S4 rule 4 without the pool).
    /// It runs in every suite run (it holds gpu.lock and shows the window for about 2 s) and asserts the lazy path
    /// functionally: every tile is uploaded, the tiles of run 0 read back equal to a fresh decode, no GL error. Its
    /// frame times are reported, not asserted: on this shared machine at 93-100 % CPU it gave 0-1 frames over
    /// 16.7 ms per 113 load frames (docs/status/wave2/D1.md), which is why the runtime pre-creates the pool.
    /// </summary>
    [GameDataFact]
    public void LazyAllocationUploadsEveryTileAndReportsItsFrameTimes() => Measure(prewarm: false, runs: 1, assert: false);

    private void Measure(bool prewarm, int runs, bool assert = true)
    {
        string game = GameFolder.Require();
        using MrgArchive arc = MrgArchive.Open(Path.Combine(game, "image", "allpac1"));
        ImageMzp img = ImageMzp.Open(arc.OpenContainer(1259));
        ImageHeader h = img.Header;
        int[] live = [.. Enumerable.Range(0, h.Tiles).Where(h.IsLive)];
        output.WriteLine($"IMG1481: {h.DisplayWidth} x {h.DisplayHeight}, tiles {h.TilesX} x {h.TilesY} of {h.TileWidth} x {h.TileHeight}, live {live.Length}, {(long)live.Length * h.TileRgbaBytes >> 20} MiB");

        using GpuLock gpuLock = GpuLock.Acquire("D1 upload timing (UploadTimingTests)", TimeSpan.FromMinutes(8));
        using GlContext ctx = GlContext.Create(new WindowOptions("Tsuki D1 upload timing (closes itself)", 1280, 720, Hidden: false, Resizable: false, SwapInterval: 1));
        using var compositor = new GpuCompositor(ctx.Device.Gl);
        var pump = new EventPump(ctx.Window, null);
        var events = new List<PlatformEvent>();
        var watchdog = Stopwatch.StartNew();
        using var priority = new ThreadPriorityScope(ThreadPriority.AboveNormal);   // the frame thread above the BelowNormal decode workers
        var scratch = new ThreadLocal<TileScratch>(() => new TileScratch());

        var allLoad = new List<double>();
        var measured = new List<GateRun>();                                         // each run's idle control and load frames over the limit, for the gate's verdict
        int over = 0, idleFrames = 0, idleOver = 0;
        for (int run = 0; run < runs && UploadTimingGate.MeasuresAnotherRun(measured) && watchdog.Elapsed < TimeSpan.FromSeconds(45); run++)
        {
            using var cache = new TextureCache(ctx.Device.Gl, spareCapacityBytes: 512L << 20);
            var pool = new StagingPool();
            var sched = new UploadScheduler(cache, pool);
            var exec = new DisplayListExecutor(compositor, cache);
            var list = new DisplayList(512);
            var intervals = new List<double>();
            var idle = new List<double>();
            var uploadMs = new List<double>();
            var slow = new List<string>();
            (ulong Idle, ulong Total, TimeSpan Own) cpu0 = default;
            int loadStart = -1, loadEnd = -1;
            long t0 = Stopwatch.GetTimestamp(), decodeStart = 0, lastTile = 0;
            double machineBusy = double.NaN, ownBusy = double.NaN;
            using (var workers = new DecodeWorkers(sched))
            {
                for (int frame = 0; watchdog.Elapsed < TimeSpan.FromSeconds(50); frame++)
                {
                    pump.Pump(events);
                    events.Clear();
                    if (prewarm && frame < WarmFrames)
                    {
                        cache.Prewarm(h.TileWidth, h.TileHeight, 8);               // texture pool over the warm-up frames (S4 rule 4)
                    }

                    if (frame == WarmFrames)
                    {
                        loadStart = frame;
                        cpu0 = CpuSample();
                        decodeStart = Stopwatch.GetTimestamp();
                        foreach (int t in live)
                        {
                            int tile = t;
                            workers.Enqueue(new DecodeJob(Key(run, tile), h.TileWidth, h.TileHeight, px => img.DecodeTile(tile, scratch.Value!, px)));
                        }
                    }

                    UploadSliceStats up = sched.UploadSlice();
                    if (loadStart >= 0 && loadEnd < 0)
                    {
                        uploadMs.Add(up.CpuMilliseconds);
                        if (sched.UploadedTiles == live.Length)
                        {
                            loadEnd = frame;
                            lastTile = Stopwatch.GetTimestamp();
                        }
                    }

                    long td = Stopwatch.GetTimestamp();
                    BuildList(list, frame, h, live, run, cache);
                    exec.Execute(list);
                    compositor.Present(1280, 720);
                    long ts = Stopwatch.GetTimestamp();
                    ctx.Window.Swap();
                    long now = Stopwatch.GetTimestamp();
                    if (UploadTimingGate.IsIdleFrame(frame))
                    {
                        idle.Add(Stopwatch.GetElapsedTime(t0, now).TotalMilliseconds);  // idle control: no load yet
                    }

                    if (loadStart >= 0 && (loadEnd < 0 || frame == loadEnd))
                    {
                        double ms = Stopwatch.GetElapsedTime(t0, now).TotalMilliseconds;   // frames from the decode start to the last upload
                        intervals.Add(ms);
                        if (ms > FrameLimitMs)
                        {
                            slow.Add(Report.Fmt($"frame {frame - loadStart}: {ms:F1} ms = upload {up.CpuMilliseconds:F1} ({up.Tiles} tiles, {up.Allocations} new) + draw {Stopwatch.GetElapsedTime(td, ts).TotalMilliseconds:F1} + swap {Stopwatch.GetElapsedTime(ts, now).TotalMilliseconds:F1}"));
                        }
                    }

                    if (loadEnd >= 0 && frame == loadEnd)
                    {
                        (ulong Idle, ulong Total, TimeSpan Own) cpu1 = CpuSample();
                        double total = cpu1.Total - cpu0.Total;
                        machineBusy = total > 0 ? 100.0 * (1 - ((cpu1.Idle - cpu0.Idle) / total)) : double.NaN;
                        ownBusy = total > 0 ? 100.0 * (cpu1.Own - cpu0.Own).Ticks / total : double.NaN;
                    }

                    t0 = now;
                    if (loadEnd >= 0 && frame >= loadEnd + TailFrames)
                    {
                        break;
                    }
                }

                Assert.Null(workers.Fault);
            }

            Assert.True(loadEnd >= 0, $"run {run}: the load did not finish ({sched.UploadedTiles} of {live.Length} tiles)");
            if (run == 0)
            {
                // Every tile on the GPU equals a fresh CPU decode (the copy is right, not only fast).
                var buf = new byte[h.TileRgbaBytes];
                foreach (int t in live)
                {
                    img.DecodeTile(t, scratch.Value!, buf);
                    Assert.True(buf.AsSpan().SequenceEqual(cache.ReadBack(Key(run, t))), $"tile {t} differs on the GPU");
                }
            }

            double[] iv = [.. intervals.Order()];
            int runOver = iv.Count(x => x > FrameLimitMs);
            int runIdleOver = idle.Count(x => x > FrameLimitMs);
            measured.Add(new GateRun(idle, runOver));                               // a run whose control failed is the last run (MeasuresAnotherRun)
            over += runOver;
            idleFrames += idle.Count;
            idleOver += runIdleOver;
            allLoad.AddRange(intervals);
            output.WriteLine(
                Report.Fmt($"run {run}: load frames {intervals.Count}, decode start to last tile {Stopwatch.GetElapsedTime(decodeStart, lastTile).TotalMilliseconds:F1} ms; ") +
                Report.Fmt($"frame p50 {Pct(iv, 0.5):F2} / p99 {Pct(iv, 0.99):F2} / max {iv[^1]:F2} ms; over {FrameLimitMs} ms: {runOver}; idle control {runIdleOver} of {idle.Count} over; ") +
                Report.Fmt($"upload CPU per frame max {uploadMs.Max():F2} ms; staging peak {pool.PeakTotalBytes >> 20} MiB, waits {pool.Waits}; ") +
                Report.Fmt($"textures {cache.Stats.Resident} ({cache.Stats.ResidentBytes >> 20} MiB), allocated {cache.Stats.Allocations}, recycled {cache.Stats.Recycles}; machine CPU busy {machineBusy:F0} % (this process {ownBusy:F0} %)"));
            foreach (string line in slow)
            {
                output.WriteLine("  over the limit: " + line);
            }
        }

        double[] all = [.. allLoad.Order()];
        output.WriteLine(Report.Fmt($"all runs: {all.Length} load frames, p50 {Pct(all, 0.5):F2}, max {all[^1]:F2} ms, over {FrameLimitMs} ms: {over}; idle control (warm-up frames {UploadTimingGate.IdleFirstFrame}..{WarmFrames - 1}): {idleOver} of {idleFrames} over; GL {ctx.Device.Info.Version}; watchdog {watchdog.Elapsed.TotalSeconds:F1} s"));
        output.WriteLine(prewarm ? "textures pre-created over the warm-up frames" : "textures allocated lazily at upload");
        Assert.Null(ctx.Device.TakeError());
        Assert.True(all.Length > 0);
        GateVerdict verdict = UploadTimingGate.Judge(measured);
        if (verdict.Skip is { } skip)
        {
            output.WriteLine(skip);
        }

        if (assert)
        {
            Assert.Equal(0, verdict.JudgedOver);                                    // every run whose idle control passed is judged as before (all of them: over)
            if (verdict.Skip is { } why)
            {
                Assert.Skip(why);                                                   // the functional checks and the judged runs have passed; the rest says nothing here
            }
        }
    }

    private static TextureRef Key(int run, int tile) => TextureRef.ForPictureTile(1, 1259 + (run * 100000), tile);

    /// <summary>Every resident tile on its inner rectangle, the picture fitted into the frame (S4's draw).</summary>
    private static void BuildList(DisplayList list, long frame, ImageHeader h, int[] live, int run, TextureCache cache)
    {
        list.Reset(frame);
        list.Add(Quads.Fill(0, 0, 1920, 1080, Quads.Rgba(16, 16, 32, 255), DrawEffect.Normal, blendState: 0));
        float s = MathF.Min(1920f / h.DisplayWidth, 1080f / h.DisplayHeight);
        foreach (int t in live)
        {
            TextureRef key = Key(run, t);
            if (!cache.Contains(key))
            {
                continue;
            }

            var (sx, sy, w, ht, x, y) = h.TileWindow(t);
            list.Add(Quads.Textured(key, x * s, y * s, (x + w) * s, (y + ht) * s,
                (float)sx / h.TileWidth, (float)sy / h.TileHeight, (float)(sx + w) / h.TileWidth, (float)(sy + ht) / h.TileHeight,
                Quads.White(), DrawEffect.Normal));
        }

        list.Seal();
    }

    private static (ulong Idle, ulong Total, TimeSpan Own) CpuSample()
    {
        (ulong idle, ulong total) = CpuTopology.SystemTimes() ?? (0, 0);
        using var p = Process.GetCurrentProcess();
        return (idle, total, p.TotalProcessorTime);
    }

    private static double Pct(double[] sorted, double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1)))];
}

/// <summary>
/// What the upload timing gate may conclude from its own idle control (v0.1.9, FIXES D10 Q-2). Pure: no window, no GL, no game data, so
/// <see cref="UploadTimingGateTests"/> pins it in every Render run, also in the runs that leave <see cref="UploadTimingTests"/> out.
/// </summary>
internal static class UploadTimingGate
{
    /// <summary>One frame at 60 Hz, with a little room: the gate's limit for a presented frame.</summary>
    public const double FrameLimitMs = 16.7;

    /// <summary>The first frame of a run's idle control (the frames before it let the window and its swap chain settle).</summary>
    public const int IdleFirstFrame = 60;

    /// <summary>A run's warm-up frames: the load starts at this frame, so the idle control is frames <see cref="IdleFirstFrame"/>..WarmFrames - 1.</summary>
    public const int WarmFrames = 120;

    /// <summary>Whether frame <paramref name="frame"/> of a run (0 the first) belongs to its idle control: the window presents at vsync and nothing loads.</summary>
    public static bool IsIdleFrame(int frame) => frame is >= IdleFirstFrame and < WarmFrames;

    /// <summary>
    /// Whether the gate measures another run after <paramref name="done"/>: not after a run whose idle control failed, whose frame times say nothing (the
    /// window is then shown for that one run, a few seconds, and not for all of them).
    /// </summary>
    public static bool MeasuresAnotherRun(IReadOnlyList<GateRun> done) => done.All(r => !CannotJudge(r.IdleMs));

    /// <summary>
    /// What the gate concludes from its runs, in their order. Each run whose idle control passed is judged as before:
    /// <see cref="GateVerdict.JudgedOver"/> counts their load frames over <see cref="FrameLimitMs"/>, and any of them fails the gate. At the first run whose
    /// control failed (the last one measured) the verdict ends with <see cref="GateVerdict.Skip"/>, the skip's words; that run's load frames are not judged. With
    /// every control passed, Skip is null and JudgedOver counts the load frames over the limit of all the runs, as the gate always did.
    /// </summary>
    public static GateVerdict Judge(IReadOnlyList<GateRun> runs)
    {
        int judgedOver = 0;
        for (int run = 0; run < runs.Count; run++)
        {
            if (CannotJudge(runs[run].IdleMs))
            {
                return new GateVerdict(judgedOver, SkipMessage(run, runs[run].IdleMs, judgedOver));
            }

            judgedOver += runs[run].Over;
        }

        return new GateVerdict(judgedOver, null);
    }

    /// <summary>
    /// True when a run's idle frames (its warm-up frames 60..119: the window presents at vsync and nothing loads) show that this PC is not presenting the
    /// window at 60 Hz or faster: more than half of them took longer than <see cref="FrameLimitMs"/>. The limit cannot then tell an upload stall from the
    /// presentation rate. On 2026-11-12 the owner's PC, with a game running, showed 300 of 300 idle frames at about 30 ms. Half or fewer over, or no idle
    /// frame at all, is no such evidence: the gate judges as before.
    /// </summary>
    public static bool CannotJudge(IReadOnlyCollection<double> idleMs) => idleMs.Count(ms => ms > FrameLimitMs) * 2 > idleMs.Count;

    /// <summary>The skip's words: the idle numbers of the run whose control failed, what was still checked, and what the gate needs.</summary>
    /// <param name="run">The run whose idle control failed (the test stops after it).</param>
    /// <param name="idleMs">That run's idle frame times in milliseconds.</param>
    /// <param name="overBefore">Load frames over the limit in the runs before it (their controls passed; in the gate's own runs the gate has already failed when this is not 0).</param>
    public static string SkipMessage(int run, IReadOnlyCollection<double> idleMs, int overBefore)
    {
        double[] sorted = [.. idleMs.Order()];
        double median = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        string rate = median > 0 ? Report.Fmt($", about {1000 / median:F0} Hz") : string.Empty;
        string before = run > 0 ? Report.Fmt($" Runs 0..{run - 1} passed their idle control, with {overBefore} load frames over {FrameLimitMs} ms.") : string.Empty;
        return Report.Fmt($"This PC cannot judge the upload timing gate: in run {run} the idle control (warm-up frames {IdleFirstFrame}..{WarmFrames - 1}, nothing loading) had ") +
            Report.Fmt($"{idleMs.Count(ms => ms > FrameLimitMs)} of {idleMs.Count} frames over {FrameLimitMs} ms (median {median:F1} ms{rate}), so the window is not ") +
            Report.Fmt($"presented at 60 Hz or faster and the frame limit cannot tell an upload stall from the presentation rate.{before} The load finished, run 0's ") +
            "tiles read back equal and GL reported no error. The gate needs a PC presenting at 60 Hz or faster (no game or other program busy with the GPU).";
    }
}

/// <summary>One run of the upload timing gate as <see cref="UploadTimingGate.Judge"/> sees it.</summary>
/// <param name="IdleMs">Its idle control's frame times in milliseconds (<see cref="UploadTimingGate.IsIdleFrame"/>).</param>
/// <param name="Over">Its load frames over <see cref="UploadTimingGate.FrameLimitMs"/>.</param>
internal readonly record struct GateRun(IReadOnlyCollection<double> IdleMs, int Over);

/// <summary>The upload timing gate's verdict (<see cref="UploadTimingGate.Judge"/>).</summary>
/// <param name="JudgedOver">Load frames over the limit in the runs whose idle control passed: the gate fails unless it is 0.</param>
/// <param name="Skip">The skip's words when a run's idle control failed; null when every control passed.</param>
internal readonly record struct GateVerdict(int JudgedOver, string? Skip);
