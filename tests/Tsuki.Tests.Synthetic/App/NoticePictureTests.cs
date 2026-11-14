using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// v0.1.9, notice findings N-1 and N-3 over the hand-made game: a save's thumbnail never has the achievement notice in it, also when there is no recent clean
/// page to stand in (a chapter end reached without a page read since the story started; a menu opened long after the last page), and a list drawn before the
/// screenshot key was pressed keeps its notice after the overlay mark. The grab here does what the renderer's does (GameApp.GrabThumbnail reads
/// GpuCompositor.Picture, the frame as it was at the mark; OverlayFrameTests in the Render tests): its picture is the last drawn list up to the mark.
/// </summary>
public sealed class NoticePictureTests
{
    // the PS4's table: the flowchart node whose _RDED earns trophy 1..27
    private static readonly int[] Scenes = [335, 15, 357, 332, 492, 107, 118, 141, 154, 172, 443, 445, 349, 14, 391, 397, 405, 414, 422, 431, 327, 274, 456, 463, 480, 300, 309];

    private const string Page = "_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);";

    // the end of a chapter: node 335 is completed (trophy 1), the script asks whether to save, then pages for a long while
    private const string End = "_WKST(F110,eq,335);_RTST(335);_RDED(335);_ATSV();_SELA(_D000);_WKST(F10,eq,1);";

    private static readonly string Tail = string.Concat(Enumerable.Repeat(Page, 3000)) + "_END_();";

    private const byte Clean = 200;
    private const byte WithTheNotice = 77;

    /// <summary>The runtime and the UI over the rig, stepped as the app steps them, with a grab that reads the last drawn list as the renderer does.</summary>
    private sealed class Rt
    {
        private long _n;

        // the last drawn frame: whether its picture (the list up to the overlay mark) has quads of the notice, and whether the frame drew the notice at all
        private bool _lastPictureHasNotice;
        private bool _lastFrameHasNotice;

        public Rt(string script)
        {
            Rig = new AppRig(script, trophyScenes: Scenes);
            // the player has turned the notice on (v0.1.9: it is off until then)
            Rig.Services.Port = new PreviewPortSettings(new PreviewServices(Language.En)) { TrophyNotice = true };
            Ui = new UiRoot(UiTestKit.Data(), Rig.Services);
            Runtime = new AppRuntime(Rig.Services, Ui, null, null);
            Runtime.Logged += Log.Add;
            Runtime.Grab = () =>
            {
                Grabs.Add((Runtime.Frame, _lastPictureHasNotice, _lastFrameHasNotice));
                var rgba = new byte[320 * 180 * 4];
                Array.Fill(rgba, _lastPictureHasNotice ? WithTheNotice : Clean);
                return (320, 180, rgba);
            };
            Rig.Services.StartScene(100);
        }

        public AppRig Rig { get; }

        public AppServices Services => Rig.Services;

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public List<string> Log { get; } = [];

        /// <summary>Every grab: the step it was made in, whether its picture has the notice, whether the frame it read showed the notice.</summary>
        public List<(long Frame, bool PictureHasNotice, bool FrameHasNotice)> Grabs { get; } = [];

        public void Step(PadButtons b = PadButtons.None, ShellActions shell = ShellActions.None)
        {
            var list = new DisplayList(2048);
            Runtime.Step(new InputFrame { Frame = ++_n, Held = b, Pressed = b, Repeated = b, ShellPressed = shell }, _n * 16_666_667L, list);

            // what the renderer will have of this frame
            RectF? box = Ui.TrophyNotice is { Alpha: > 0f } notice && !Ui.HideToast ? notice.Box(Ui.Painter) : null;
            _lastFrameHasNotice = box is not null;
            _lastPictureHasNotice = box is { } b2 && PictureHas(list, b2);
        }

        public void Run(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Step();
            }
        }

        public void Press(PadButtons b)
        {
            Step(b);
            Step();
        }

        // a quad of the notice (inside its box; it slides in from 24 px to the right) before the first overlay mark
        private static bool PictureHas(DisplayList list, RectF box)
        {
            foreach (DrawCmd c in list.Commands)
            {
                if (c.Kind == DrawKind.OverlaysBegin)
                {
                    return false;
                }

                if (c.Target == 0 && c.Kind is DrawKind.TexturedQuad or DrawKind.FillQuad
                    && MathF.Min(MathF.Min(c.V0.X, c.V1.X), MathF.Min(c.V2.X, c.V3.X)) >= box.X - 2f && MathF.Max(MathF.Max(c.V0.X, c.V1.X), MathF.Max(c.V2.X, c.V3.X)) <= box.Right + 26f
                    && MathF.Min(MathF.Min(c.V0.Y, c.V1.Y), MathF.Min(c.V2.Y, c.V3.Y)) >= box.Y - 2f && MathF.Max(MathF.Max(c.V0.Y, c.V1.Y), MathF.Max(c.V2.Y, c.V3.Y)) <= box.Bottom + 2f)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public void AChapterEndSavedFromItsPromptWithNoPageSinceTheStartHasACleanThumbnail()
    {
        // skip or fast-forward the whole way: no page was grabbed since the story started, the chapter end earns its achievement and asks whether to save while
        // the notice is up; Yes, the free slot
        var rt = new Rt("_WKST(F822,eq,1);" + End + Tail);
        for (int i = 0; i < 80 && !rt.Services.Host.ChoiceSelectable; i++)
        {
            rt.Step();
        }

        Assert.True(rt.Services.Host.ChoiceSelectable, "the save prompt did not open");
        Assert.Contains("trophy 1 earned", rt.Log);
        Assert.True(rt.Ui.TrophyNotice is { Alpha: > 0f }, "the notice is not up at the prompt");
        rt.Press(PadButtons.Down);
        rt.Press(PadButtons.Decide);                                                // Yes
        rt.Run(40);
        Assert.IsType<SaveLoadScreen>(rt.Ui.Top);
        Assert.True(rt.Ui.TrophyNotice is { Alpha: > 0f }, "the notice has gone before the save screen: the test proves nothing");
        rt.Press(PadButtons.Decide);                                                // the free slot
        rt.Run(200);

        UiSlot slot = Assert.Single(rt.Services.Slots, s => s.Used && s.Index < 100);
        UiThumbnail thumbnail = Assert.IsType<UiThumbnail>(rt.Services.Thumbnail(slot.Index));
        Assert.Contains(rt.Grabs, g => g.FrameHasNotice);                            // a grab was made from a frame that showed the notice ...
        Assert.DoesNotContain(rt.Grabs, g => g.PictureHasNotice);                    // ... and its picture has no notice in it
        Assert.Equal(Clean, thumbnail.Rgba.Span[0]);
    }

    [Fact]
    public void AMenuOpenedLongAfterTheLastPageGrabsTheFrameWithoutTheNotice()
    {
        // the last clean page is older than the 100 s it may stand in for: the frame is grabbed when the system menu opens over the notice
        var rt = new Rt("_WKST(F822,eq,1);" + Page + "_ATSV();_SELA(_D000);_WKST(F10,eq,1);" + Tail);
        rt.Run(200);
        Assert.NotEmpty(rt.Grabs);                                                  // the page was grabbed
        rt.Press(PadButtons.Decide);
        for (int i = 0; i < 80 && !rt.Services.Host.ChoiceSelectable; i++)
        {
            rt.Step();
        }

        rt.Run(6100);                                                               // the prompt stays up: no page is grabbed while a choice is open
        int before = rt.Grabs.Count;
        rt.Runtime.ShowTrophy(5);
        rt.Run(30);
        Assert.True(rt.Ui.TrophyNotice is { Alpha: 1f });
        rt.Step(shell: ShellActions.SystemMenu);
        Assert.IsType<SystemMenuScreen>(rt.Ui.Top);
        (long Frame, bool PictureHasNotice, bool FrameHasNotice)[] made = [.. rt.Grabs.Skip(before)];
        Assert.NotEmpty(made);
        Assert.All(made, g => Assert.True(g.FrameHasNotice, "the menu's grab read a frame without the notice: the test proves nothing"));
        Assert.All(made, g => Assert.False(g.PictureHasNotice, $"the grab of step {g.Frame} has the notice in its picture"));
    }

    [Fact]
    public void AFrameDrawnBeforeTheScreenshotKeyKeepsItsNoticeAfterTheMark()
    {
        // N-1: the loop holds a frame for its textures and draws it after the key was pressed; that list was sealed with the notice in it, after the overlay mark,
        // so the screenshot (GpuCompositor.ReadPicture) leaves it out. A frame drawn after the key has neither the notice nor a mark (HideToast)
        var rt = new Rt("_WKST(F822,eq,1);" + Page + Tail);
        rt.Run(60);
        rt.Runtime.ShowTrophy(5);
        rt.Run(20);
        var held = new DisplayList(2048);
        rt.Runtime.Step(new InputFrame { Frame = rt.Runtime.Frame + 1 }, (rt.Runtime.Frame + 1) * 16_666_667L, held);
        int mark = Enumerable.Range(0, held.Count).Single(i => held.Commands[i].Kind == DrawKind.OverlaysBegin);
        RectF box = rt.Ui.TrophyNotice!.Box(rt.Ui.Painter);
        Assert.True(held.Count > mark + 4);
        Assert.All(held.Commands[(mark + 1)..].ToArray(), c => Assert.True(c.V0.X >= box.X - 2f && c.V0.Y >= box.Y - 2f && c.V3.Y <= box.Bottom + 2f));

        rt.Ui.HideToast = true;                                                     // the key: GameApp.RequestScreenshot
        var fresh = new DisplayList(2048);
        rt.Runtime.Step(new InputFrame { Frame = rt.Runtime.Frame + 1 }, (rt.Runtime.Frame + 1) * 16_666_667L, fresh);
        rt.Ui.HideToast = false;
        Assert.DoesNotContain(fresh.Commands.ToArray(), c => c.Kind == DrawKind.OverlaysBegin);
        Assert.DoesNotContain(fresh.Commands.ToArray(), c => c.Target == 0 && c.V0.X >= box.X - 2f && c.V3.X <= box.Right + 2f && c.V0.Y >= box.Y - 2f && c.V3.Y <= box.Bottom + 2f);
    }
}
