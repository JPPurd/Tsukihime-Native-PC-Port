using Tsuki.Engine.Contracts;
using Tsuki.Render.Compositor;
using Tsuki.Tests.Render.Smoke;

namespace Tsuki.Tests.Render.Compositor;

/// <summary>
/// v0.1.9, the port's overlays (the toast and the achievement notice) are no part of the game's picture: the UI marks where they begin
/// (<see cref="DrawKind.OverlaysBegin"/>), the executor copies the frame aside there, a transition that starts in the next frame takes that copy as its
/// outgoing picture, and the screenshot key and the save thumbnails read it. A frame without the mark runs as it always did. On the GPU, 1920 x 1080, solid
/// fills for the scene and a quad in the notice's corner for the notice.
/// </summary>
[Collection(SdlCollection.Name)]
public sealed class OverlayFrameTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static readonly (int R, int G, int B) Scene = (40, 90, 160);
    private static readonly (int R, int G, int B) NextScene = (10, 160, 60);
    private static readonly (int R, int G, int B) Notice = (240, 200, 30);

    // the notice's box at the top right (UiTrophyNotice: right edge 1880, top 18, 84 high) and a point inside it
    private const float BoxX0 = 1500f;
    private const float BoxY0 = 18f;
    private const float BoxX1 = 1880f;
    private const float BoxY1 = 102f;
    private const int CornerX = 1700;
    private const int CornerY = 60;

    private static DrawCmd Fill((int R, int G, int B) c, float x0 = 0, float y0 = 0, float x1 = W, float y1 = H) =>
        Quads.Fill(x0, y0, x1, y1, Quads.Rgba((byte)c.R, (byte)c.G, (byte)c.B, 255), DrawEffect.Normal);

    private static DrawCmd CaptureDrawn(int surface, byte alpha) =>
        Quads.Textured(TextureRef.ForCapture(surface), 0, 0, W, H, 0, 0, 1, 1, Quads.White(alpha), DrawEffect.Normal);

    private static (int R, int G, int B) At(byte[] frame, int x, int y, int width = W) =>
        (frame[((y * width) + x) * 4], frame[(((y * width) + x) * 4) + 1], frame[(((y * width) + x) * 4) + 2]);

    private static ExecuteStats Run(DisplayListExecutor executor, long frame, params DrawCmd[] commands)
    {
        var list = new DisplayList(16);
        list.Reset(frame);
        foreach (DrawCmd c in commands)
        {
            list.Add(c);
        }

        list.Seal();
        return executor.Execute(list);
    }

    // frame 1: the scene, the mark, the notice in its corner (the frame as shown has it)
    private static void SceneUnderTheNotice(GpuCompositor c, DisplayListExecutor executor, bool mark = true)
    {
        DrawCmd[] cmds = mark
            ? [Fill(Scene), Quads.OverlaysBegin(), Fill(Notice, BoxX0, BoxY0, BoxX1, BoxY1)]
            : [Fill(Scene), Fill(Notice, BoxX0, BoxY0, BoxX1, BoxY1)];
        Run(executor, 1, cmds);
        Assert.Equal(Notice, At(c.Read(c.Frame), CornerX, CornerY));
        Assert.Equal(mark ? 1 : 0, c.Counters.CleanCopies);
    }

    // frame 2: the transition start of _FADS(kind) (the alpha fill of effect 56, the captures), the new scene, the outgoing picture over it at full strength
    private static ExecuteStats TransitionStart(DisplayListExecutor executor, int kind)
    {
        var list = new DisplayList(16);
        list.Reset(2);
        FadsCommands.Begin(list, kind, W, H);
        list.Add(Fill(NextScene));
        list.Add(CaptureDrawn(0, 255));
        list.Seal();
        return executor.Execute(list);
    }

    [Theory]
    [InlineData(0)]         // the cross-fade: surface 0
    [InlineData(2)]         // the rule wipe: surfaces 0 and 15
    public void ATransitionThatStartsWhileTheNoticeIsUpTakesTheFrameWithoutIt(int kind)
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var c = new GpuCompositor(ctx.Device.Gl, W, H);
        var executor = new DisplayListExecutor(c, new TestTextureSource());
        SceneUnderTheNotice(c, executor);

        ExecuteStats stats = TransitionStart(executor, kind);
        byte[] frame = c.Read(c.Frame);
        Assert.Equal(Scene, At(frame, CornerX, CornerY));                                  // no copy of the notice in the outgoing picture
        Assert.Equal(Scene, At(frame, 960, 540));
        Assert.Equal(Scene, At(c.Read(c.CaptureSurface(0)), CornerX, CornerY));
        if (FadsCommands.CapturesSurface15(kind))
        {
            Assert.Equal(Scene, At(c.Read(c.CaptureSurface(15)), CornerX, CornerY));
        }

        Assert.Equal(1, c.Counters.CleanRestores);
        Assert.Equal(FadsCommands.CapturesSurface15(kind) ? 2 : 1, stats.Captures);
        Assert.Null(c.CleanFrame);                                                       // that copy was this frame's; this frame drew no overlay
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AFrameWithoutTheMarkIsCapturedAsItAlwaysWas()
    {
        // the frame without the mark (any frame without the toast or the notice, and the PS4's own): no copy, nothing put back, the capture is the frame as drawn
        using GlContext ctx = GlContext.CreateHidden();
        using var c = new GpuCompositor(ctx.Device.Gl, W, H);
        var executor = new DisplayListExecutor(c, new TestTextureSource());
        SceneUnderTheNotice(c, executor, mark: false);
        Assert.Null(c.CleanFrame);
        Assert.Same(c.Frame, c.Picture);

        TransitionStart(executor, 0);
        Assert.Equal(Notice, At(c.Read(c.Frame), CornerX, CornerY));
        Assert.Equal(0, c.Counters.CleanRestores);
        Assert.Equal(0, c.Counters.CleanCopies);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void TheCopyIsPutBackOnlyBeforeAListThatReadsThePreviousFrameAtItsHead()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var c = new GpuCompositor(ctx.Device.Gl, W, H);
        var source = new TestTextureSource();
        var executor = new DisplayListExecutor(c, source);
        var layer = TextureRef.ForPictureTile(0, 1, 0);
        byte[] solid = new byte[4 * 4 * 4];
        for (int i = 0; i < solid.Length; i += 4)
        {
            (solid[i], solid[i + 1], solid[i + 2], solid[i + 3]) = ((byte)NextScene.R, (byte)NextScene.G, (byte)NextScene.B, 255);
        }

        source.Textures[layer] = c.CreateTexture(4, 4, solid);

        // a list that draws a picture first and captures later (the ghost, the engine screens, the menu's blurred backdrop) captures its own drawing: nothing is
        // put back
        SceneUnderTheNotice(c, executor);
        Run(executor, 2, Quads.Textured(layer, 0, 540, W, H, 0, 0, 1, 1, Quads.White(), DrawEffect.Normal), Quads.Capture(3), CaptureDrawn(3, 255));
        Assert.Equal(0, c.Counters.CleanRestores);
        Assert.Null(c.CleanFrame);
        byte[] frame = c.Read(c.Frame);
        Assert.Equal(Notice, At(frame, CornerX, CornerY));                                 // the previous frame's pixels the list did not draw over stay as they were
        Assert.Equal(NextScene, At(frame, 960, 800));
        c.DeleteTexture(source.Textures[layer]);

        // a notice that stays up: every frame has its own copy, and the frame after it that is no transition start puts nothing back
        SceneUnderTheNotice(c, executor);
        Run(executor, 3, Fill(NextScene), Quads.OverlaysBegin(), Fill(Notice, BoxX0, BoxY0, BoxX1, BoxY1));
        Assert.Equal((0, 1), (c.Counters.CleanRestores, c.Counters.CleanCopies));
        Assert.Equal(NextScene, At(c.Read(c.CleanFrame!), CornerX, CornerY));

        // the head: fills (the alpha fill) and other targets do not end it; a picture drawn into the frame, a clear of it or the mark does
        Assert.True(DisplayListExecutor.ReadsPreviousFrame([Quads.Fill(0, 0, W, H, Quads.White(), DrawEffect.AlphaUnion), Quads.Capture(0)]));
        Assert.True(DisplayListExecutor.ReadsPreviousFrame([new DrawCmd { Kind = DrawKind.Clear, Target = 5 }, Quads.Fill(0, 0, 4, 4, Quads.White(), 0, target: 5), Quads.Capture(0), Quads.Capture(15)]));
        Assert.False(DisplayListExecutor.ReadsPreviousFrame([Quads.Textured(TextureRef.ForCapture(1), 0, 0, W, H, 0, 0, 1, 1, Quads.White(), 0), Quads.Capture(0)]));
        Assert.False(DisplayListExecutor.ReadsPreviousFrame([new DrawCmd { Kind = DrawKind.Clear }, Quads.Capture(0)]));
        Assert.False(DisplayListExecutor.ReadsPreviousFrame([Quads.OverlaysBegin(), Quads.Capture(0)]));
        Assert.False(DisplayListExecutor.ReadsPreviousFrame([Quads.Capture(2, target: 4), Fill(Scene)]));
        Assert.False(DisplayListExecutor.ReadsPreviousFrame([]));
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void AClearOrALoadOfTheFrameForgetsTheCopy()
    {
        // the game clears the frame before the first frame of a story (its transition start captures black, as it always did) and outside the story
        using GlContext ctx = GlContext.CreateHidden();
        using var c = new GpuCompositor(ctx.Device.Gl, W, H);
        var executor = new DisplayListExecutor(c, new TestTextureSource());
        SceneUnderTheNotice(c, executor);
        Assert.NotNull(c.CleanFrame);
        c.Clear(0, 0, 0, 0, 255);
        Assert.Null(c.CleanFrame);
        TransitionStart(executor, 0);
        Assert.Equal((0, 0, 0), At(c.Read(c.CaptureSurface(0)), CornerX, CornerY));
        Assert.Equal(0, c.Counters.CleanRestores);

        // a clear of another target keeps it; a load of the frame forgets it
        SceneUnderTheNotice(c, executor);
        c.Clear(7, 0, 0, 0, 0);
        Assert.NotNull(c.CleanFrame);
        c.Load(c.Frame, new byte[W * H * 4]);
        Assert.Null(c.CleanFrame);
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void OnlyTheFirstMarkOfAFrameCounts()
    {
        using GlContext ctx = GlContext.CreateHidden();
        using var c = new GpuCompositor(ctx.Device.Gl, W, H);
        var executor = new DisplayListExecutor(c, new TestTextureSource());
        Run(executor, 1, Fill(Scene), Quads.OverlaysBegin(), Fill(Notice, BoxX0, BoxY0, BoxX1, BoxY1), Quads.OverlaysBegin(), Fill(NextScene, 0, 1000, W, H));
        Assert.Equal(1, c.Counters.CleanCopies);
        byte[] clean = c.Read(c.CleanFrame!);
        Assert.Equal(Scene, At(clean, CornerX, CornerY));
        Assert.Equal(Scene, At(clean, 960, 1040));
        Assert.Null(ctx.Device.TakeError());
    }

    [Fact]
    public void ThePlayersPicturesOfTheFrameLeaveTheOverlaysOut()
    {
        // the save thumbnails read Picture; the screenshot key reads the window's back buffer through ReadPicture. Both take the frame before the mark: a list drawn
        // before the key was pressed (the loop held it for its textures) still has the notice in it
        using GlContext ctx = GlContext.CreateHidden();
        using var c = new GpuCompositor(ctx.Device.Gl, W, H);
        var executor = new DisplayListExecutor(c, new TestTextureSource());
        SceneUnderTheNotice(c, executor);
        Assert.Same(c.CleanFrame, c.Picture);
        Assert.Equal(Scene, At(c.Read(c.Picture), CornerX, CornerY));
        Assert.Equal(Notice, At(c.Read(c.Frame), CornerX, CornerY));

        // the window: 320 x 180, so the corner's point is at a sixth of its place
        (int ww, int wh) = (320, 180);
        (int wx, int wy) = (CornerX / 6, CornerY / 6);
        c.Present(ww, wh);
        Assert.Equal(Notice, At(c.ReadWindow(ww, wh), wx, wy, ww));                      // what the window shows
        byte[] picture = c.ReadPicture(ww, wh);
        Assert.Equal(Scene, At(picture, wx, wy, ww));
        Assert.Equal(Scene, At(picture, 160, 90, ww));
        Assert.All(Enumerable.Range(0, picture.Length / 4), i => Assert.Equal(255, picture[(i * 4) + 3]));
        Assert.Equal(Notice, At(c.ReadWindow(ww, wh), wx, wy, ww));                      // and shows again afterwards

        // a frame without overlays is its own picture
        Run(executor, 2, Fill(NextScene));
        Assert.Same(c.Frame, c.Picture);
        c.Present(ww, wh);
        Assert.Equal(NextScene, At(c.ReadPicture(ww, wh), wx, wy, ww));
        Assert.Null(ctx.Device.TakeError());
    }
}
