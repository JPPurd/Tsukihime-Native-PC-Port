using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// v0.1.9: the UI marks where the port's overlays begin (<see cref="DrawKind.OverlaysBegin"/>): the toast and the achievement notice, which are no part of the
/// game's picture. A frame that draws one of them has the mark once, right before the first; everything before it is the frame as it is without them, everything
/// after it is theirs. A frame that draws neither (none on show, one still at alpha 0, or a frame drawn for the screenshot key) has no mark and is the list it
/// always was. The renderer keeps the frame as it was at the mark for the next transition's outgoing picture, the screenshot key and the save thumbnails.
/// </summary>
public sealed class OverlayMarkTests
{
    private static UiRoot Open()
    {
        UiRoot root = UiTestKit.Root(new PreviewServices(Language.En));
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            root.Context.Assets.SetTrophyIcon(id, null);
        }

        root.Push(new ModePanelScreen());
        UiTestKit.Run(root, 30);
        return root;
    }

    private static int[] Marks(DisplayList list) =>
        [.. Enumerable.Range(0, list.Count).Where(i => list.Commands[i].Kind == DrawKind.OverlaysBegin)];

    private static bool Inside(in DrawCmd c, RectF box, float slack) =>
        c.V0.X >= box.X - slack && c.V3.X <= box.Right + slack && c.V0.Y >= box.Y - slack && c.V3.Y <= box.Bottom + slack;

    // the same frame drawn for the screenshot key (the overlays left out) and as shown: the shown one is the other, the mark, then the overlays
    private static (DrawCmd[] Without, DrawCmd[] Shown, int Mark) DrawBoth(UiRoot root)
    {
        root.HideToast = true;
        DisplayList hidden = UiTestKit.Draw(root);
        root.HideToast = false;
        DisplayList shown = UiTestKit.Draw(root);
        Assert.Empty(Marks(hidden));
        int[] marks = Marks(shown);
        Assert.Single(marks);
        return (hidden.Commands.ToArray(), shown.Commands.ToArray(), marks[0]);
    }

    [Fact]
    public void AFrameWithoutTheToastOrTheNoticeHasNoMark()
    {
        UiRoot root = Open();
        Assert.Empty(Marks(UiTestKit.Draw(root)));

        // shown but not stepped yet: both are at alpha 0 and draw nothing, so there is nothing to mark
        root.ShowToast("Screenshot saved");
        root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "A Chapter", 1);
        Assert.Equal((0f, 0f), (root.Toast!.Alpha, root.TrophyNotice!.Alpha));
        Assert.Empty(Marks(UiTestKit.Draw(root)));

        // gone again: no mark
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames + 2);
        Assert.Null(root.Toast);
        Assert.Null(root.TrophyNotice);
        Assert.Empty(Marks(UiTestKit.Draw(root)));
    }

    [Fact]
    public void TheNoticeFollowsTheMarkAndTheFrameBeforeItIsTheFrameWithoutIt()
    {
        UiRoot root = Open();
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        UiTestKit.Run(root, 3);                                                 // on its way in: the mark is there from its first visible frame
        Assert.InRange(notice.Alpha, 0.1f, 0.9f);
        foreach (int frames in new[] { 0, UiTrophyNotice.FadeInFrames, UiTrophyNotice.HoldFrames })
        {
            UiTestKit.Run(root, frames);
            (DrawCmd[] without, DrawCmd[] shown, int mark) = DrawBoth(root);
            Assert.Equal(without, shown[..mark]);
            Assert.True(shown.Length > mark + 4, "the notice draws its box, its picture and its words after the mark");
            RectF box = notice.Box(root.Painter);
            foreach (DrawCmd c in shown[(mark + 1)..])
            {
                Assert.True(Inside(c, new RectF(box.X, box.Y, box.W + 24f, box.H), 2f), $"a command after the mark ({c.V0.X}, {c.V0.Y})-({c.V3.X}, {c.V3.Y}) is not the notice's (box {box})");
            }
        }
    }

    [Fact]
    public void TheToastAndTheNoticeTogetherFollowOneMark()
    {
        UiRoot root = Open();
        root.ShowToast("Screenshot saved");
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        (DrawCmd[] without, DrawCmd[] shown, int mark) = DrawBoth(root);
        Assert.Equal(without, shown[..mark]);
        RectF toast = root.Toast!.Box(root.Painter);
        RectF box = notice.Box(root.Painter);
        Assert.All(shown[(mark + 1)..], c => Assert.True(Inside(c, toast, 2f) || Inside(c, box, 2f), $"a command after the mark ({c.V0.X}, {c.V0.Y}) is neither the toast's nor the notice's"));
        Assert.Contains(shown[(mark + 1)..], c => Inside(c, toast, 2f));
        Assert.Contains(shown[(mark + 1)..], c => Inside(c, box, 2f));

        // the toast alone (the screenshot key's own) is marked the same way
        UiTestKit.Run(root, UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames + 2);
        Assert.Null(root.TrophyNotice);
        root.ShowToast("Screenshot saved");
        UiTestKit.Run(root, UiToast.FadeInFrames);
        (without, shown, mark) = DrawBoth(root);
        Assert.Equal(without, shown[..mark]);
        toast = root.Toast!.Box(root.Painter);
        Assert.All(shown[(mark + 1)..], c => Assert.True(Inside(c, toast, 2f)));
    }

    [Fact]
    public void TheMarkIsAPlainCommandThatNamesNoTexture()
    {
        // the feeder pins and uploads the textures a list names and the frozen story frame keeps only the story's commands: the mark is neither
        UiRoot root = Open();
        root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "A Chapter", 1);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        DisplayList list = UiTestKit.Draw(root);
        DrawCmd mark = list.Commands[Marks(list)[0]];
        Assert.Equal(new DrawCmd { Kind = DrawKind.OverlaysBegin }, mark);
        Assert.Equal(TextureKind.None, mark.Texture.Kind);
        Assert.Equal(0, mark.Target);
    }
}
