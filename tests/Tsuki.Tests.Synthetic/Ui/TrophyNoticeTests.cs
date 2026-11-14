using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// v0.1.9: the achievement notice. It comes in at the top right, holds four seconds and fades out; its box stays inside the screen and above the page text; it is
/// drawn last and left out while a screenshot is being captured; a second notice waits behind the first instead of replacing it, and the first then holds for a
/// shorter time.
/// The notice of one achievement shows that achievement's icon: it does not come in before the host has settled the icon, for 45 frames at most, then takes the
/// icon, else the emblem of its grade (drawn by the port, no game data, never waited for), and keeps what it took until it has gone. No picture of the PS4's trophy
/// file is drawn anywhere. The notices of the title's catch-up (D7) are short ones, one per achievement, and a key the title takes drops those not yet come in; the
/// root says when a notice starts its way in, the moment the host sounds the chime for one that asks for it.
/// </summary>
public sealed class TrophyNoticeTests
{
    // a UI with the mode panel up. Without icons (the default) its host has said for every achievement that there is no icon, as the app does without a game
    // folder: no notice waits. With icons nothing is settled: the test is the host
    private static UiRoot Open(bool icons = false)
    {
        UiRoot root = UiTestKit.Root(new PreviewServices(Language.En));
        for (int id = 0; id < TrophyTable.Count && !icons; id++)
        {
            root.Context.Assets.SetTrophyIcon(id, null);
        }

        root.Push(new ModePanelScreen());
        UiTestKit.Run(root, 30);
        return root;
    }

    private const int Size = UiTrophyNotice.PlaceSize;
    private const int IconSize = UiTrophyNotice.IconSize;

    // a host's icon
    private static UiComposedPicture Square(int side = IconSize) => new(side, side, new byte[side * side * 4]);

    // the composed pictures the frame draws (the icon or the emblem of the notice), by their composed id
    private static int[] HostPictures(UiRoot root) =>
        [.. UiTestKit.Draw(root).Commands.ToArray().Where(c => c.Texture.Kind == TextureKind.UiComposed).Select(c => c.Texture.A)];

    private static bool Inside(in DrawCmd c, RectF box, float slack) =>
        c.V0.X >= box.X - slack && c.V3.X <= box.Right + slack && c.V0.Y >= box.Y - slack && c.V3.Y <= box.Bottom + slack;

    [Fact]
    public void TheNoticeComesInHoldsFourSecondsAndFadesOut()
    {
        UiRoot root = Open();
        Assert.Null(root.TrophyNotice);
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        Assert.Same(notice, root.TrophyNotice);
        Assert.Equal((5, TrophyGrade.Silver, "Achievement unlocked", "A Chapter"), (notice.Id, notice.Grade, notice.Heading, notice.Name));
        Assert.Equal(0f, notice.Alpha);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames / 2);
        Assert.InRange(notice.Alpha, 0.4f, 0.6f);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames / 2);
        Assert.Equal(1f, notice.Alpha);
        UiTestKit.Run(root, UiTrophyNotice.HoldFrames);
        Assert.Equal(1f, notice.Alpha);                                     // the whole hold at full strength
        Assert.Equal(UiTrophyNotice.HoldFrames, notice.Held);
        Assert.Same(notice, root.TrophyNotice);
        UiTestKit.Run(root, UiTrophyNotice.FadeOutFrames / 2);
        Assert.InRange(notice.Alpha, 0.4f, 0.6f);
        UiTestKit.Run(root, UiTrophyNotice.FadeOutFrames / 2);
        Assert.True(notice.Done);
        Assert.Null(root.TrophyNotice);
        Assert.Equal(0, root.TrophyNoticesWaiting);

        // about 4.6 s at 60 Hz: 12 frames in, 240 up, 24 out
        Assert.Equal((12, 240, 24), (UiTrophyNotice.FadeInFrames, UiTrophyNotice.HoldFrames, UiTrophyNotice.FadeOutFrames));
        Assert.Equal(150, UiTrophyNotice.QueuedHoldFrames);
    }

    [Fact]
    public void TheBoxSitsAtTheTopRightInsideTheScreenAndAboveThePageText()
    {
        UiRoot root = Open();
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        RectF box = notice.Box(root.Painter);
        Assert.Equal(1880f, box.Right);
        Assert.Equal(18f, box.Y);
        Assert.True(box.Bottom <= 104f, $"box {box} reaches the first line of the page text (y 110)");
        Assert.True(box.X >= 1920f / 2f, $"box {box} is not in the right half");
        Assert.True(box.W >= UiTrophyNotice.PlaceSize + 100f, $"box {box} has no room for the words");
        Assert.True(box.X > 416f, "the box reaches the mode banner's column");

        // a longer name widens the box to the left: its right edge, top and height never move
        UiRoot other = Open();
        UiTrophyNotice wide = other.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "A Much Longer Name of an Achievement Than That", 29);
        RectF wideBox = wide.Box(other.Painter);
        Assert.Equal((box.Right, box.Y, box.H), (wideBox.Right, wideBox.Y, wideBox.H));
        Assert.True(wideBox.W > box.W);

        // however long the name is, the box stays right of x 40 and inside the canvas
        UiRoot third = Open();
        UiTrophyNotice huge = third.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", string.Concat(Enumerable.Repeat("An endless name ", 40)), 29);
        RectF hugeBox = huge.Box(third.Painter);
        Assert.True(hugeBox.X >= UiTrophyNotice.LeftLimit, $"box {hugeBox} passes the left limit");
        Assert.Equal(1880f, hugeBox.Right);
        Assert.True(hugeBox.Bottom <= 104f);
        UiTestKit.Run(third, UiTrophyNotice.FadeInFrames);
        DisplayList drawn = UiTestKit.Draw(third);
        int plain = UiTestKit.Draw(Open()).Count;
        Assert.Equal(DrawKind.OverlaysBegin, drawn.Commands[plain].Kind);                // the notice follows the overlay mark (v0.1.9)
        foreach (DrawCmd c in drawn.Commands[(plain + 1)..])
        {
            Assert.True(Inside(c, hugeBox, 2f), $"a quad of the notice ({c.V0.X}, {c.V0.Y})-({c.V3.X}, {c.V3.Y}) lies outside its box {hugeBox}");
        }

        // on its way in the box may be a few pixels to the right of its place, never off the screen
        UiRoot moving = Open();
        UiTrophyNotice early = moving.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTestKit.Run(moving, 1);
        Assert.InRange(early.Alpha, 0.01f, 0.2f);
        DisplayList first = UiTestKit.Draw(moving);
        Assert.True(first.Count > plain);
        Assert.Equal(DrawKind.OverlaysBegin, first.Commands[plain].Kind);
        foreach (DrawCmd c in first.Commands[(plain + 1)..])
        {
            Assert.True(c.V3.X <= 1920f && c.V0.X >= early.Box(moving.Painter).X && c.V3.Y <= 104f && c.V0.Y >= 0f, $"a quad of the notice on its way in is off its strip: ({c.V0.X}, {c.V0.Y})-({c.V3.X}, {c.V3.Y})");
        }
    }

    [Fact]
    public void TheNoticeIsDrawnLastOverEverythingAndNotWhileAFrameIsCaptured()
    {
        UiRoot root = Open();
        int plain = UiTestKit.Draw(root).Count;
        root.ShowToast("Screenshot saved");
        UiTestKit.Run(root, UiToast.FadeInFrames);
        int withToast = UiTestKit.Draw(root).Count;
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "A Chapter", 1);
        Assert.Equal(withToast, UiTestKit.Draw(root).Count);                 // not yet stepped: alpha 0 draws nothing
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        DisplayList list = UiTestKit.Draw(root);
        Assert.True(list.Count > withToast + 4, $"the notice adds its box, its emblem and its glyphs ({list.Count} commands against {withToast})");

        // everything after the toast's commands is the notice's, inside its box: it is the last thing in the frame
        RectF box = notice.Box(root.Painter);
        foreach (DrawCmd c in list.Commands[withToast..])
        {
            Assert.True(Inside(c, box, 2f), $"a command after the toast ({c.V0.X}, {c.V0.Y})-({c.V3.X}, {c.V3.Y}) is not the notice's (box {box})");
        }

        RectF toast = root.Toast!.Box(root.Painter);
        Assert.True(toast.Y > box.Bottom + 500f, "the toast and the notice share no corner");

        root.HideToast = true;
        Assert.Equal(plain, UiTestKit.Draw(root).Count);                      // a captured frame shows neither
        root.HideToast = false;
        Assert.Equal(list.Count, UiTestKit.Draw(root).Count);
    }

    [Fact]
    public void ASecondNoticeWaitsBehindTheFirstWhichThenHoldsForAShorterTime()
    {
        UiRoot root = Open();
        UiTrophyNotice first = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + 20);
        UiTrophyNotice second = root.ShowTrophy(TrophyGrade.Platinum, "Achievement unlocked", "All Achievements", 0);
        UiTrophyNotice third = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "First Lesson", 28);
        Assert.Same(first, root.TrophyNotice);                               // not replaced
        Assert.Equal(1f, first.Alpha);
        Assert.Equal(2, root.TrophyNoticesWaiting);
        Assert.Equal(0f, second.Alpha);

        // the first leaves after the shorter hold, then the others in the order they came
        UiTestKit.Run(root, UiTrophyNotice.QueuedHoldFrames - 20);
        Assert.Equal(1f, first.Alpha);
        Assert.Equal(UiTrophyNotice.QueuedHoldFrames, first.Held);
        UiTestKit.Run(root, 1);
        Assert.True(first.Alpha < 1f, "the first notice still holds although another waits");
        UiTestKit.Run(root, UiTrophyNotice.FadeOutFrames - 1);
        Assert.True(first.Done);
        Assert.Same(second, root.TrophyNotice);
        Assert.Equal(1, root.TrophyNoticesWaiting);
        Assert.Equal(0f, second.Alpha);                                      // it starts from nothing
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames);
        Assert.True(second.Done);
        Assert.Same(third, root.TrophyNotice);
        Assert.Equal(0, root.TrophyNoticesWaiting);

        // the last one has nothing behind it: the full hold
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + 10);
        Assert.Equal(1f, third.Alpha);
        UiTestKit.Run(root, UiTrophyNotice.HoldFrames - UiTrophyNotice.QueuedHoldFrames - 10 + UiTrophyNotice.FadeOutFrames);
        Assert.True(third.Done);
        Assert.Null(root.TrophyNotice);

        // a notice that came while the first was already past the shorter hold starts the fade at once
        UiTrophyNotice late = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + 30);
        Assert.Equal(1f, late.Alpha);
        root.ShowTrophy(TrophyGrade.Platinum, "Achievement unlocked", "All Achievements", 0);
        UiTestKit.Run(root, 1);
        Assert.True(late.Alpha < 1f);
    }

    [Fact]
    public void WithoutAnIconTheNoticeDrawsItsGradesEmblemPixelForPixelInTheIconsPlace()
    {
        // no icon (the host said so): one textured quad of 72 x 72 at whole pixels in the middle of the picture's place, the whole emblem of the grade; no flat
        // quad stands in the place (no disc, nothing drawn for a picture that is missing)
        UiRoot root = Open();
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTestKit.Run(root, 1);
        Assert.Equal((UiTrophyPicture.Emblem, 0), (notice.Picture, notice.Waited));          // never waited for
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        RectF box = notice.Box(root.Painter);
        DrawCmd[] drawn = UiTestKit.Draw(root).Commands.ToArray();
        DrawCmd emblem = Assert.Single(drawn, c => c.Texture.Kind == TextureKind.UiComposed);
        Assert.Equal(UiComposedId.TrophyEmblem(TrophyGrade.Gold), emblem.Texture.A);
        Assert.Equal((box.X + 6f, box.Y + 6f, box.X + 6f + IconSize, box.Y + 6f + IconSize), (emblem.V0.X, emblem.V0.Y, emblem.V3.X, emblem.V3.Y));
        Assert.Equal((MathF.Round(emblem.V0.X), MathF.Round(emblem.V0.Y)), (emblem.V0.X, emblem.V0.Y));
        Assert.Equal((0f, 0f, 1f, 1f), (emblem.V0.U, emblem.V0.V, emblem.V3.U, emblem.V3.V));
        var place = new RectF(box.X + 2f, box.Y + 2f, Size, Size);
        Assert.DoesNotContain(drawn, c => c.Kind == DrawKind.FillQuad && c.V0.X > place.X + 2f && c.V3.X < place.Right - 2f && c.V0.Y > place.Y + 2f && c.V3.Y < place.Bottom - 2f);

        // what the texture provider is given for that key: the port's emblem at the icons' size, the same bytes TrophyIconArt draws, in every language, made
        // once in the process (the same picture for every UI) and kept through a language switch
        UiAssets assets = root.Context.Assets;
        UiComposedPicture picture = Assert.IsType<UiComposedPicture>(assets.Composed(emblem.Texture.A, (Language)emblem.Texture.B));
        Assert.Equal((72, 72), (picture.Width, picture.Height));
        Assert.True(picture.Rgba.Span.SequenceEqual(Tsuki.Data.Trophies.TrophyIconArt.Emblem(TrophyGrade.Gold, 72)), "the notice's emblem is not TrophyIconArt's");
        Assert.Same(picture, assets.Composed(UiComposedId.TrophyEmblem(TrophyGrade.Gold), Language.Ja));
        Assert.Same(picture, UiTestKit.Root(new PreviewServices(Language.Zc)).Context.Assets.Composed(UiComposedId.TrophyEmblem(TrophyGrade.Gold)));
        assets.SetLanguage(Language.Ja);
        Assert.Same(picture, assets.Composed(UiComposedId.TrophyEmblem(TrophyGrade.Gold)));

        // one emblem per grade, under ids of their own (the four before the 30 icons; the title's EXIT cells are no emblem)
        TrophyGrade[] grades = [TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze];
        Assert.Equal([16, 17, 18, 19], grades.Select(UiComposedId.TrophyEmblem));
        Assert.Equal(4, grades.Select(g => assets.Composed(UiComposedId.TrophyEmblem(g))).Distinct().Count());
        Assert.All(grades, g => Assert.True(assets.Composed(UiComposedId.TrophyEmblem(g))!.Rgba.Span.SequenceEqual(Tsuki.Data.Trophies.TrophyIconArt.Emblem(g, 72))));
        Assert.False(UiComposedId.IsTrophyEmblem(UiComposedId.TitleExitCursor));
        Assert.False(UiComposedId.IsTrophyEmblem(UiComposedId.TrophyIcon(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiComposedId.TrophyEmblem((TrophyGrade)4));

        // a notice of another grade shows its own grade's emblem
        UiRoot silver = Open();
        silver.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        UiTestKit.Run(silver, UiTrophyNotice.FadeInFrames);
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Silver)], HostPictures(silver));
    }

    // ---- the icon of one achievement ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheNoticeOfOneAchievementWaitsForItsIconAndThenShowsItInThePicturesPlace()
    {
        UiRoot root = Open(icons: true);
        UiAssets assets = root.Context.Assets;
        Assert.NotNull(assets.Composed(UiComposedId.TrophyEmblem(TrophyGrade.Silver)));     // the emblem is there all along: the notice still waits for the icon
        int plain = UiTestKit.Draw(root).Count;
        Assert.False(assets.TrophyIconSettled(5));
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);

        // the host's worker is still at it: the notice is at the head of the line and has not started. Nothing of it is drawn
        UiTestKit.Run(root, 20);
        Assert.Same(notice, root.TrophyNotice);
        Assert.Equal((0f, UiTrophyPicture.Waiting, 20, 0), (notice.Alpha, notice.Picture, notice.Waited, notice.Held));
        Assert.Equal(plain, UiTestKit.Draw(root).Count);

        // the icon arrives: the next step is the first of the way in, and from there the times are those of every notice
        UiComposedPicture icon = Square(IconSize);
        assets.SetTrophyIcon(5, icon);
        UiTestKit.Run(root, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Icon, 20), (notice.Alpha, notice.Picture, notice.Waited));
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames - 1);
        Assert.Equal(1f, notice.Alpha);

        // one textured quad of the host's: the icon, whole, at its own size and at whole pixels in the middle of the picture's place (72 of the 80 pixels: four
        // pixels of the box show around it, so its ring does not run into the box's border). The emblem is not drawn
        Assert.Equal((80, 72), (Size, IconSize));
        RectF box = notice.Box(root.Painter);
        DrawCmd drawn = Assert.Single(UiTestKit.Draw(root).Commands.ToArray(), c => c.Texture.Kind == TextureKind.UiComposed);
        Assert.Equal(UiComposedId.TrophyIcon(5), drawn.Texture.A);
        Assert.Equal((box.X + 6f, box.Y + 6f, box.X + 6f + IconSize, box.Y + 6f + IconSize), (drawn.V0.X, drawn.V0.Y, drawn.V3.X, drawn.V3.Y));
        Assert.Equal((box.Bottom - 6f, 4f), (drawn.V3.Y, (Size - IconSize) / 2f));
        Assert.Equal((MathF.Round(drawn.V0.X), MathF.Round(drawn.V0.Y)), (drawn.V0.X, drawn.V0.Y));
        Assert.Equal((0f, 0f, 1f, 1f), (drawn.V0.U, drawn.V0.V, drawn.V3.U, drawn.V3.V));
        Assert.Same(icon, assets.Composed(drawn.Texture.A, (Language)drawn.Texture.B));          // what the texture provider is given for that key

        UiTestKit.Run(root, UiTrophyNotice.HoldFrames);
        Assert.Equal((1f, UiTrophyNotice.HoldFrames), (notice.Alpha, notice.Held));
        UiTestKit.Run(root, UiTrophyNotice.FadeOutFrames);
        Assert.True(notice.Done);
        Assert.Null(root.TrophyNotice);

        // an icon that is settled when the notice is asked for costs no frame: the first step is the first of the way in
        UiTrophyNotice again = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        UiTestKit.Run(root, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Icon, 0), (again.Alpha, again.Picture, again.Waited));

        // an id that is no achievement's has no icon to wait for
        UiRoot odd = Open(icons: true);
        UiTrophyNotice stray = odd.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Not one of the thirty", 99);
        UiTestKit.Run(odd, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Emblem, 0), (stray.Alpha, stray.Picture, stray.Waited));
    }

    [Fact]
    public void TheNoticeNeverWaitsLongerThanFortyFiveFramesForItsIcon()
    {
        Assert.Equal(45, UiTrophyNotice.IconWaitFrames);                      // three quarters of a second
        UiRoot root = Open(icons: true);
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        for (int frame = 1; frame <= UiTrophyNotice.IconWaitFrames; frame++)
        {
            UiTestKit.Run(root, 1);
            Assert.Equal((0f, UiTrophyPicture.Waiting, frame), (notice.Alpha, notice.Picture, notice.Waited));
        }

        // the icon never came: the 46th step starts the way in with the emblem of the grade
        UiTestKit.Run(root, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Emblem, UiTrophyNotice.IconWaitFrames), (notice.Alpha, notice.Picture, notice.Waited));
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames - 1);
        Assert.Equal(1f, notice.Alpha);
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Gold)], HostPictures(root));
        Assert.False(root.Context.Assets.TrophyIconSettled(29));

        // the wait is no part of the four seconds: the notice still holds them in full
        UiTestKit.Run(root, UiTrophyNotice.HoldFrames);
        Assert.Equal((1f, UiTrophyNotice.HoldFrames), (notice.Alpha, notice.Held));
        UiTestKit.Run(root, UiTrophyNotice.FadeOutFrames);
        Assert.Null(root.TrophyNotice);
    }

    [Fact]
    public void WhenTheHostSaysThereIsNoIconTheNoticeShowsTheEmblemAtOnce()
    {
        // settled, none: no wait, the emblem of the grade
        UiRoot root = Open(icons: true);
        UiAssets assets = root.Context.Assets;
        assets.SetTrophyIcon(1, null);
        Assert.True(assets.TrophyIconSettled(1));
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "A Chapter", 1);
        UiTestKit.Run(root, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Emblem, 0), (notice.Alpha, notice.Picture, notice.Waited));
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Bronze)], HostPictures(root));

        // another grade's notice takes its own grade's emblem
        UiRoot gold = Open(icons: true);
        gold.Context.Assets.SetTrophyIcon(14, null);
        UiTrophyNotice chapter = gold.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "A Chapter", 14);
        UiTestKit.Run(gold, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Emblem, 0), (chapter.Alpha, chapter.Picture, chapter.Waited));
        UiTestKit.Run(gold, UiTrophyNotice.FadeInFrames);
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Gold)], HostPictures(gold));
    }

    [Fact]
    public void ThePictureTheNoticeTookDoesNotChangeWhileItIsShown()
    {
        // the icon comes after the 45 frames, when the notice is already up with the emblem: it is not swapped in
        UiRoot root = Open(icons: true);
        UiAssets assets = root.Context.Assets;
        UiTrophyNotice notice = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        UiTestKit.Run(root, UiTrophyNotice.IconWaitFrames + UiTrophyNotice.FadeInFrames + 30);
        Assert.Equal((1f, UiTrophyPicture.Emblem), (notice.Alpha, notice.Picture));
        assets.SetTrophyIcon(5, Square(IconSize));
        for (int i = 0; i < UiTrophyNotice.HoldFrames - 30 + UiTrophyNotice.FadeOutFrames - 1; i++)
        {
            UiTestKit.Run(root, 1);
            Assert.Equal(UiTrophyPicture.Emblem, notice.Picture);
            Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Silver)], HostPictures(root));
        }

        UiTestKit.Run(root, 1);
        Assert.True(notice.Done);

        // the next notice of that achievement finds the icon
        UiTrophyNotice next = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames);
        Assert.Equal((UiTrophyPicture.Icon, 0), (next.Picture, next.Waited));
        Assert.Equal([UiComposedId.TrophyIcon(5)], HostPictures(root));

        // a notice that took the icon keeps it when the host stores another picture under the same id, and steps down to the emblem only if the icon is taken away
        assets.SetTrophyIcon(5, Square(IconSize));
        UiTestKit.Run(root, 5);
        Assert.Equal(UiTrophyPicture.Icon, next.Picture);
        assets.SetTrophyIcon(5, null);
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Silver)], HostPictures(root));
        Assert.Equal(UiTrophyPicture.Emblem, next.Picture);
        assets.SetTrophyIcon(5, Square(IconSize));                            // and not back up
        UiTestKit.Run(root, 5);
        Assert.Equal(UiTrophyPicture.Emblem, next.Picture);
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Silver)], HostPictures(root));
    }

    [Fact]
    public void ASecondNoticeStillFollowsANoticeThatWaitedForItsIconAndWaitsOnlyForItsOwn()
    {
        UiRoot root = Open(icons: true);
        UiAssets assets = root.Context.Assets;
        UiTrophyNotice first = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        UiTrophyNotice second = root.ShowTrophy(TrophyGrade.Platinum, "Achievement unlocked", "All Achievements", 0);
        UiTrophyNotice third = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "First Lesson", 28);
        Assert.Equal(2, root.TrophyNoticesWaiting);

        // the first waits ten frames for its icon; the others stay in the line behind it, in their order, and count nothing meanwhile
        UiTestKit.Run(root, 10);
        Assert.Same(first, root.TrophyNotice);
        Assert.Equal((10, UiTrophyPicture.Waiting), (first.Waited, first.Picture));
        Assert.Equal((0, UiTrophyPicture.Waiting, 0f), (second.Waited, second.Picture, second.Alpha));
        Assert.Equal(2, root.TrophyNoticesWaiting);
        assets.SetTrophyIcon(29, Square(IconSize));
        assets.SetTrophyIcon(0, Square(IconSize));                            // the second's worker is done long before its turn

        // from the first step of its way in the first takes the shorter hold of a notice with others behind it: the wait is not part of it
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames);
        Assert.Equal((1f, UiTrophyNotice.QueuedHoldFrames, UiTrophyPicture.Icon), (first.Alpha, first.Held, first.Picture));
        UiTestKit.Run(root, UiTrophyNotice.FadeOutFrames);
        Assert.True(first.Done);

        // the second is up next and does not wait: its icon was settled while it stood in the line
        Assert.Same(second, root.TrophyNotice);
        Assert.Equal(1, root.TrophyNoticesWaiting);
        UiTestKit.Run(root, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Icon, 0), (second.Alpha, second.Picture, second.Waited));
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames - 1 + UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames);
        Assert.True(second.Done);

        // the third's icon was never settled: it waits its 45 frames from the moment it is at the head, not from the moment it was asked for, then shows the emblem
        Assert.Same(third, root.TrophyNotice);
        Assert.Equal(0, root.TrophyNoticesWaiting);
        UiTestKit.Run(root, UiTrophyNotice.IconWaitFrames);
        Assert.Equal((0f, UiTrophyNotice.IconWaitFrames, UiTrophyPicture.Waiting), (third.Alpha, third.Waited, third.Picture));
        UiTestKit.Run(root, 1);
        Assert.Equal((1f / UiTrophyNotice.FadeInFrames, UiTrophyPicture.Emblem), (third.Alpha, third.Picture));
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames - 1 + UiTrophyNotice.HoldFrames + UiTrophyNotice.FadeOutFrames);
        Assert.Null(root.TrophyNotice);
    }

    [Fact]
    public void ThePreviewOfTheNoticeDoesNotWaitForAnIconNobodyMakes()
    {
        // the screenshot tool's previews have no host that makes icons: they say so, and the notice is fully up after the frames the preview always took, with the
        // emblem of its grade (gold: chapter 14)
        UiRoot root = UiPreview.Open("trophy", UiTestKit.Data(), new PreviewServices(Language.En));
        UiTrophyNotice notice = Assert.IsType<UiTrophyNotice>(root.TrophyNotice);
        Assert.Equal((14, 1f, UiTrophyPicture.Emblem, 0, 4), (notice.Id, notice.Alpha, notice.Picture, notice.Waited, notice.Held));
        Assert.Equal([UiComposedId.TrophyEmblem(TrophyGrade.Gold)], HostPictures(root));
        Assert.DoesNotContain("trophy-progress", UiPreview.Screens);                 // the one notice for a save's progress is gone (v0.1.9, D7)
    }

    // ---- the title's catch-up (v0.1.9, D7) -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANoticeOfTheCatchUpComesInHoldsAndGoesInItsShortTimesWhateverWaitsBehindIt()
    {
        UiRoot root = Open();
        UiTrophyNotice first = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "A Chapter", 1, catchUp: true, chime: true);
        UiTrophyNotice second = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Another", 2, catchUp: true, chime: false);
        Assert.Equal((true, true, true, false), (first.CatchUp, first.Chime, second.CatchUp, second.Chime));
        Assert.Same(second, root.NextTrophyNotice);

        // 10 frames in, 70 up although another waits (a notice of its own would hold the shorter 150 then), 12 out: about 1.5 s; the next comes in at once
        UiTestKit.Run(root, 1);
        Assert.Equal(1f / UiTrophyNotice.CatchUpFadeInFrames, first.Alpha);
        UiTestKit.Run(root, UiTrophyNotice.CatchUpFadeInFrames / 2 - 1);
        Assert.InRange(first.Alpha, 0.4f, 0.6f);
        UiTestKit.Run(root, UiTrophyNotice.CatchUpFadeInFrames / 2);
        Assert.Equal(1f, first.Alpha);
        UiTestKit.Run(root, UiTrophyNotice.CatchUpHoldFrames);
        Assert.Equal((1f, UiTrophyNotice.CatchUpHoldFrames), (first.Alpha, first.Held));
        UiTestKit.Run(root, UiTrophyNotice.CatchUpFadeOutFrames / 2);
        Assert.InRange(first.Alpha, 0.4f, 0.6f);
        UiTestKit.Run(root, UiTrophyNotice.CatchUpFadeOutFrames / 2);
        Assert.True(first.Done);
        Assert.Same(second, root.TrophyNotice);
        Assert.Null(root.NextTrophyNotice);
        UiTestKit.Run(root, UiTrophyNotice.CatchUpFadeInFrames + UiTrophyNotice.CatchUpHoldFrames + UiTrophyNotice.CatchUpFadeOutFrames);
        Assert.True(second.Done);
        Assert.Null(root.TrophyNotice);
        Assert.Equal((10, 70, 12), (UiTrophyNotice.CatchUpFadeInFrames, UiTrophyNotice.CatchUpHoldFrames, UiTrophyNotice.CatchUpFadeOutFrames));

        // a notice of its own keeps its times, and the chime is asked for unless the host says otherwise
        UiTrophyNotice own = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "A Chapter", 5);
        Assert.Equal((false, true), (own.CatchUp, own.Chime));
        UiTestKit.Run(root, 1);
        Assert.Equal(1f / UiTrophyNotice.FadeInFrames, own.Alpha);
    }

    [Fact]
    public void TheRootSaysWhenEachNoticeStartsItsWayInAfterItsIconIsSettled()
    {
        // the moment the host sounds the chime: the first frame of a notice's way in, once per notice, not while it waits for its icon or in line behind another
        UiRoot root = Open(icons: true);
        var started = new List<(int Id, long Frame, float Alpha)>();
        root.TrophyNoticeStarted += n => started.Add((n.Id, root.Context.Frame, n.Alpha));
        UiTrophyNotice first = root.ShowTrophy(TrophyGrade.Gold, "Achievement unlocked", "Every Lesson", 29);
        root.ShowTrophy(TrophyGrade.Platinum, "Achievement unlocked", "All Achievements", 0);
        UiTestKit.Run(root, 10);
        Assert.Empty(started);                                                // the first waits for its icon
        root.Context.Assets.SetTrophyIcon(29, Square());
        root.Context.Assets.SetTrophyIcon(0, Square());
        long at = root.Context.Frame;
        UiTestKit.Run(root, 1);
        Assert.Equal([(29, at + 1, 1f / UiTrophyNotice.FadeInFrames)], started);
        UiTestKit.Run(root, UiTrophyNotice.FadeInFrames + UiTrophyNotice.QueuedHoldFrames + UiTrophyNotice.FadeOutFrames - 1);
        Assert.True(first.Done);
        Assert.Single(started);                                               // the second is at the head now, not stepped yet
        UiTestKit.Run(root, 1);
        Assert.Equal(2, started.Count);
        Assert.Equal((0, 1f / UiTrophyNotice.FadeInFrames), (started[1].Id, started[1].Alpha));
        UiTestKit.Run(root, 400);
        Assert.Equal(2, started.Count);
    }

    [Fact]
    public void AKeyTheTitleTakesDropsTheCatchUpNoticesNotYetComeInAndKeepsTheOthers()
    {
        UiRoot root = UiPreview.Open("title", UiTestKit.Data(), new PreviewServices(Language.En));
        TitleScreen title = Assert.IsType<TitleScreen>(root.Top);
        Assert.True(title.MenuShown);
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            root.Context.Assets.SetTrophyIcon(id, null);
        }

        // a catch-up of three on show, an achievement of its own earned meanwhile behind it, then the catch-up's last one
        UiTrophyNotice first = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "One", 1, catchUp: true);
        UiTrophyNotice second = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Two", 2, catchUp: true, chime: false);
        UiTrophyNotice own = root.ShowTrophy(TrophyGrade.Silver, "Achievement unlocked", "Lesson", 28);
        UiTrophyNotice third = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Three", 3, catchUp: true, chime: false);
        UiTestKit.Run(root, 20);
        Assert.Equal((first, 3), (root.TrophyNotice, root.TrophyNoticesWaiting));

        // the pointer moving and the wheel are no answer: nothing is dropped
        UiTestKit.MouseHover(root, 300f, 300f);
        UiTestKit.MouseHover(root, 340f, 320f);
        UiTestKit.MouseWheel(root, 340f, 320f, 1);
        Assert.Equal(3, root.TrophyNoticesWaiting);

        // Down: the title moves its cursor, the catch-up's notices still in line are dropped, the one on show finishes, the achievement of its own follows it
        TitleItem? cursor = title.Cursor;
        UiTestKit.Press(root, PadButtons.Down);
        Assert.NotEqual(cursor, title.Cursor);
        Assert.Same(first, root.TrophyNotice);
        Assert.Equal(1, root.TrophyNoticesWaiting);
        Assert.Same(own, root.NextTrophyNotice);
        UiTestKit.Run(root, UiTrophyNotice.CatchUpHoldFrames + UiTrophyNotice.CatchUpFadeOutFrames);
        Assert.Same(own, root.TrophyNotice);
        Assert.False(second.Started || third.Started);

        // a catch-up notice at the head that has not come in yet (it waits for its icon) is dropped too
        UiRoot other = UiPreview.Open("title", UiTestKit.Data(), new PreviewServices(Language.En));
        UiTrophyNotice waiting = other.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "One", 1, catchUp: true);
        other.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Two", 2, catchUp: true, chime: false);
        UiTestKit.Run(other, 5);
        Assert.Equal((UiTrophyPicture.Waiting, 1), (waiting.Picture, other.TrophyNoticesWaiting));
        UiTestKit.Press(other, PadButtons.Down);
        Assert.Null(other.TrophyNotice);
        Assert.Equal(0, other.TrophyNoticesWaiting);

        // the title on its way in (its menu not up yet) takes no key: nothing is dropped
        UiRoot early = UiTestKit.Root(new PreviewServices(Language.En));
        early.Context.TitleIntroShown = true;
        TitleScreen fading = early.Push(new TitleScreen());
        UiTestKit.Run(early, 5);
        Assert.False(fading.MenuShown);
        early.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "One", 1, catchUp: true);
        early.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Two", 2, catchUp: true, chime: false);
        UiTestKit.Press(early, PadButtons.Down);
        Assert.Equal(1, early.TrophyNoticesWaiting);

        // over another screen (the mode panel, a story) a key drops nothing: only the title's answer ends the line
        UiRoot story = Open();
        story.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "One", 1, catchUp: true);
        story.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Two", 2, catchUp: true, chime: false);
        UiTestKit.Press(story, PadButtons.Down);
        Assert.Equal(1, story.TrophyNoticesWaiting);
    }

    [Fact]
    public void AKeyOrAClickTheTitlesMenuHasNoUseForLeavesTheCatchUpInLine()
    {
        // D7: "a key press or click that the title handles". In its menu the title answers its cursor keys, Decide, Cancel and a click on an item; a shell key
        // (Ctrl, Tab, A, H), a key bound to nothing, a shoulder or face button it does not use, Left and Right, and a click beside the items do nothing there, and
        // the catch-up's notices stay in line
        UiRoot root = UiPreview.Open("title", UiTestKit.Data(), new PreviewServices(Language.En));
        TitleScreen title = Assert.IsType<TitleScreen>(root.Top);
        Assert.True(title.MenuShown && !title.PressMode);
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            root.Context.Assets.SetTrophyIcon(id, null);
        }

        // an answer counts for its own step only: the cursor down and back up before the line is put in drops nothing of it
        TitleItem? cursor = title.Cursor;
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(cursor, title.Cursor);
        UiTrophyNotice first = root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "One", 1, catchUp: true);
        root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Two", 2, catchUp: true, chime: false);
        root.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Three", 3, catchUp: true, chime: false);
        UiTestKit.Run(root, 5);
        Assert.Equal((first, 2), (root.TrophyNotice, root.TrophyNoticesWaiting));
        foreach (ShellActions shell in new[] { ShellActions.FastForward, ShellActions.SkipToggle, ShellActions.AutoToggle, ShellActions.HideWindow, ShellActions.AnyKey })
        {
            root.Step(new InputFrame { Frame = root.Context.Frame + 1, ShellPressed = shell });
            Assert.False(title.Answered, $"the title answered {shell}");
            UiTestKit.Run(root, 1);
        }

        foreach (PadButtons button in new[] { PadButtons.L1, PadButtons.R1, PadButtons.L2, PadButtons.R2, PadButtons.Triangle, PadButtons.Square, PadButtons.Left, PadButtons.Right })
        {
            UiTestKit.Press(root, button);
        }

        UiTestKit.Point(root, 1700f, 200f, click: true);                             // beside the items, under the logo's right end
        Assert.Equal(2, root.TrophyNoticesWaiting);
        Assert.Same(first, root.TrophyNotice);
        Assert.Equal(cursor, title.Cursor);
        Assert.Same(title, root.Top);                                                // the title did nothing with any of them

        // the pointer moving onto another item takes the cursor there, and is no answer either (the cursor starts on CONTINUE when it is offered, else on
        // NEW GAME, the first row: the second row is another item either way)
        RectF second = TitleScreen.ItemRect(1, cursor == TitleItem.Continue ? 6 : 5);
        UiTestKit.MouseHover(root, second.X + (second.W / 2f), second.Y + 36f);
        Assert.NotEqual(cursor, title.Cursor);
        Assert.Equal(2, root.TrophyNoticesWaiting);

        // Decide is an answer: the title chooses the item under its cursor, and the line ends
        root.Step(new InputFrame { Frame = root.Context.Frame + 1, Pressed = PadButtons.Decide, Held = PadButtons.Decide });
        Assert.True(title.Answered);
        Assert.Equal(0, root.TrophyNoticesWaiting);
        Assert.Same(first, root.TrophyNotice);

        // on PRESS ANY BUTTON every key is the title's: Ctrl takes it to the cautions, and that ends the line
        var services = new PreviewServices(Language.En) { Title = new UiTitleInfo(true, false, 0, -1) };
        UiRoot press = UiTestKit.Root(services);
        press.Context.TitleIntroShown = true;
        TitleScreen any = press.Push(new TitleScreen());
        for (int i = 0; i < 400 && !any.MenuShown; i++)
        {
            UiTestKit.Run(press, 1);
        }

        Assert.True(any.MenuShown && any.PressMode);
        press.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "One", 1, catchUp: true);
        press.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "Two", 2, catchUp: true, chime: false);
        press.Step(new InputFrame { Frame = press.Context.Frame + 1, ShellPressed = ShellActions.FastForward });
        Assert.Equal((true, TitlePhase.Caution, 0), (any.Answered, any.Phase, press.TrophyNoticesWaiting));
    }

    [Fact]
    public void TheWordsOfTheNoticeAreThePortsOwnInFourLanguagesAndTheChapterNamesComeFromTheGamesText()
    {
        foreach (Language language in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            var assets = new UiAssets(UiTestKit.Data(), language);
            Assert.NotEmpty(UiTrophyText.Heading(language));
            Assert.Equal(PortText.Get(PortString.AchievementNameAll, language), UiTrophyText.Name(assets, 0));
            Assert.Equal(PortText.Get(PortString.AchievementNameFirstLesson, language), UiTrophyText.Name(assets, 28));
            Assert.Equal(PortText.Get(PortString.AchievementNameEveryLesson, language), UiTrophyText.Name(assets, 29));

            // the stand-in tables answer "text N": trophy 1 is named after chapter text 358 + 3, trophy 27 after 358 + 36
            Assert.Equal("text 361", UiTrophyText.Name(assets, 1));
            Assert.Equal("text 394", UiTrophyText.Name(assets, 27));
        }

        Assert.Equal("Achievement unlocked", UiTrophyText.Heading(Language.En));
        // the words of the one notice for a save's progress are gone with it (v0.1.9, D7): every notice is one achievement's
        Assert.DoesNotContain("AchievementsUnlocked", Enum.GetNames<PortString>());
        Assert.DoesNotContain("AchievementsFromProgress", Enum.GetNames<PortString>());
        Assert.Equal(("All Achievements", "First Lesson", "Every Lesson"), (PortText.Get(PortString.AchievementNameAll, Language.En), PortText.Get(PortString.AchievementNameFirstLesson, Language.En), PortText.Get(PortString.AchievementNameEveryLesson, Language.En)));
    }
}
