using Tsuki.Engine.Contracts;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Synthetic.Ui;

/// <summary>
/// Package K21: the port's own rows of the config screen. System tab: Copy Text to Clipboard (two pills) and the Text Window Opacity slider under K19's display rows;
/// Sound tab: Mute in Background inside the Sound Settings box, with everything below it moved down. The words exist in the four languages.
/// </summary>
public sealed class ConfigPortTests
{
    private static (UiRoot Root, PreviewServices Services, ConfigScreen Screen) Open(Action<PreviewServices>? tweak = null, int tab = 0)
    {
        var services = new PreviewServices(Language.En);
        tweak?.Invoke(services);
        UiRoot root = UiTestKit.Root(services);
        root.OpenSystemMenu();
        UiTestKit.Run(root, 20);
        root.Context.MenuTab = 2;
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 60);
        var screen = Assert.IsType<ConfigScreen>(root.Top);
        if (tab != 0)
        {
            screen.Change(new Ctl(CtlKind.Tab, tab), 0);
            UiTestKit.Run(root, 40);
        }

        return (root, services, screen);
    }

    private static Ctl At(ConfigScreen c, int cursor) => c.Layout[cursor].Ctl;

    private static void Go(UiRoot root, ConfigScreen screen, Ctl target)
    {
        IReadOnlyList<IReadOnlyList<Ctl>> grid = screen.GridRows();
        (int tr, int tc) = (-1, -1);
        for (int r = 0; r < grid.Count; r++)
        {
            for (int c = 0; c < grid[r].Count; c++)
            {
                if (grid[r][c] == target)
                {
                    (tr, tc) = (r, c);
                }
            }
        }

        Assert.True(tr >= 0, $"{target} is not on the grid");
        for (int guard = 0; guard < 80 && At(screen, screen.Cursor) != target; guard++)
        {
            Ctl cur = At(screen, screen.Cursor);
            (int r, int c) = (-1, -1);
            for (int i = 0; i < grid.Count; i++)
            {
                int j = grid[i].ToList().IndexOf(cur);
                if (j >= 0)
                {
                    (r, c) = (i, j);
                }
            }

            // the opacity slider is alone in its row and takes Left / Right for itself: rows first, then along the row (never from the slider)
            PadButtons b = r < 0 ? PadButtons.Down : r != tr ? (tr > r ? PadButtons.Down : PadButtons.Up) : (tc > c ? PadButtons.Right : PadButtons.Left);
            UiTestKit.Press(root, b);
        }

        Assert.Equal(target, At(screen, screen.Cursor));
    }

    [Fact]
    public void TheSystemTabEndsWithTheClipboardRowAndTheOpacitySliderUnderTheDisplayRows()
    {
        (_, _, ConfigScreen screen) = Open();
        Assert.True(screen.HasPortRows);
        // package K23 adds the Text Hooker Output and Text WebSocket rows between the clipboard row and the slider; package K24 the Screenshot Notify row under the window
        // rows; v0.1.9 the Achievement Notify row right under that one and the Achievement Sound row under it
        Assert.Equal([PortRow.DisplayMode, PortRow.WindowSize, PortRow.ScreenshotNotice, PortRow.TrophyNotice, PortRow.TrophySound, PortRow.CopyText, PortRow.TextHooker, PortRow.TextWebSocket, PortRow.Opacity], screen.SystemBandRows());
        List<(Ctl Ctl, float X, float Y)> layout = screen.Layout;
        (Ctl _, float sx, float sy) = layout.First(t => t.Ctl == new Ctl(CtlKind.WindowSize, 0));
        (Ctl _, float nx, float ny) = layout.First(t => t.Ctl == new Ctl(CtlKind.ScreenshotNotice, 0));
        (Ctl _, float tx, float ty) = layout.First(t => t.Ctl == new Ctl(CtlKind.TrophyNotice, 0));
        (Ctl _, float ax, float ay) = layout.First(t => t.Ctl == new Ctl(CtlKind.TrophySound, 0));
        (Ctl _, float cx, float cy) = layout.First(t => t.Ctl == new Ctl(CtlKind.CopyText, 0));
        (Ctl _, float onX, float onY) = layout.First(t => t.Ctl == new Ctl(CtlKind.CopyText, 1));
        (Ctl _, float hx, float hy) = layout.First(t => t.Ctl == new Ctl(CtlKind.TextHooker, 0));
        (Ctl _, float wx, float wy) = layout.First(t => t.Ctl == new Ctl(CtlKind.TextWebSocket, 0));
        (Ctl _, float ox, float oy) = layout.First(t => t.Ctl == new Ctl(CtlKind.Opacity));
        Assert.Equal(sx, nx);
        Assert.Equal(sx, cx);
        Assert.Equal(sx, hx);
        Assert.Equal(sx, wx);
        Assert.Equal(sx, ox);
        Assert.Equal(sx, tx);
        Assert.Equal(sx, ax);
        Assert.Equal(sy + 56f, ny);
        Assert.Equal(ny + 56f, ty);
        Assert.Equal(ty + 56f, ay);
        Assert.Equal(ay + 56f, cy);
        Assert.Equal(cy, onY);
        Assert.True(onX > cx);
        Assert.Equal(cy + 56f, hy);
        Assert.Equal(hy + 56f, wy);
        Assert.Equal(wy + 56f, oy);
        Assert.Equal(750f + ConfigScreen.BandFor(9), screen.ScrollMaximum);
        IReadOnlyList<IReadOnlyList<Ctl>> grid = screen.GridRows();
        Assert.Equal([new Ctl(CtlKind.ScreenshotNotice, 0), new Ctl(CtlKind.ScreenshotNotice, 1)], grid[^7]);
        Assert.Equal([new Ctl(CtlKind.TrophyNotice, 0), new Ctl(CtlKind.TrophyNotice, 1)], grid[^6]);
        Assert.Equal([new Ctl(CtlKind.TrophySound, 0), new Ctl(CtlKind.TrophySound, 1)], grid[^5]);
        Assert.Equal([new Ctl(CtlKind.CopyText, 0), new Ctl(CtlKind.CopyText, 1)], grid[^4]);
        Assert.Equal([new Ctl(CtlKind.TextHooker, 0), new Ctl(CtlKind.TextHooker, 1)], grid[^3]);
        Assert.Equal([new Ctl(CtlKind.TextWebSocket, 0), new Ctl(CtlKind.TextWebSocket, 1)], grid[^2]);
        Assert.Equal([new Ctl(CtlKind.Opacity)], grid[^1]);
        Assert.Equal((CtlKind.ScreenshotNotice, CtlKind.CopyText, CtlKind.TextHooker, CtlKind.TextWebSocket), (ConfigScreen.SwitchKind(PortRow.ScreenshotNotice), ConfigScreen.SwitchKind(PortRow.CopyText), ConfigScreen.SwitchKind(PortRow.TextHooker), ConfigScreen.SwitchKind(PortRow.TextWebSocket)));
        Assert.True(ConfigScreen.IsSwitch(CtlKind.ScreenshotNotice) && ConfigScreen.IsSwitch(CtlKind.TextHooker) && ConfigScreen.IsSwitch(CtlKind.BackgroundMute) && !ConfigScreen.IsSwitch(CtlKind.Opacity));

        // a game without the port's switches keeps the opacity slider (it is the PS4's own byte) and loses the notice and clipboard rows
        (_, _, ConfigScreen none) = Open(x => x.Port = null);
        Assert.False(none.HasPortRows);
        Assert.Equal([PortRow.DisplayMode, PortRow.WindowSize, PortRow.Opacity], none.SystemBandRows());
        Assert.DoesNotContain(none.Layout, t => t.Ctl.Kind is CtlKind.CopyText or CtlKind.ScreenshotNotice or CtlKind.TrophyNotice or CtlKind.TrophySound);
        Assert.Contains(none.Layout, t => t.Ctl.Kind == CtlKind.Opacity);
        Assert.Equal(750f + ConfigScreen.BandFor(3), none.ScrollMaximum);

        // and a game with neither a window nor the switches: the slider alone, in a band of one row
        (_, _, ConfigScreen bare) = Open(x =>
        {
            x.Port = null;
            x.Display = null;
        });
        Assert.Equal([PortRow.Opacity], bare.SystemBandRows());
        Assert.Equal(750f + ConfigScreen.BandFor(1), bare.ScrollMaximum);
        Assert.Equal(160f, ConfigScreen.BandFor(2));                                // K19's band for its two rows
    }

    [Fact]
    public void TheClipboardPillsSwitchThePortSettingAndShowIt()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.True(screen.IsOn(new Ctl(CtlKind.CopyText, 0)));                     // off by default
        Assert.False(screen.IsOn(new Ctl(CtlKind.CopyText, 1)));

        Go(root, screen, new Ctl(CtlKind.CopyText, 1));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Port!.CopyTextToClipboard);
        Assert.Contains("copy text on", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.CopyText, 1)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.CopyText, 0)));
        Assert.DoesNotContain("commit", s.Calls);                                  // the port saves the switch itself; the config block did not change

        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.False(s.Port.CopyTextToClipboard);
        Assert.Contains("copy text off", s.Calls);

        // a change from outside the screen shows in the row
        s.Port.CopyTextToClipboard = true;
        Assert.True(screen.IsOn(new Ctl(CtlKind.CopyText, 1)));
    }

    [Fact]
    public void TheScreenshotNoticePillsStartOnSwitchThePortSettingAndSitBetweenTheWindowSizeAndAchievementNoticeRows()
    {
        // package K24: On by default; Off saves screenshots without the toast
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.True(screen.IsOn(new Ctl(CtlKind.ScreenshotNotice, 1)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.ScreenshotNotice, 0)));

        Go(root, screen, new Ctl(CtlKind.ScreenshotNotice, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.False(s.Port!.ScreenshotNotice);
        Assert.Contains("screenshot notice off", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.ScreenshotNotice, 0)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.ScreenshotNotice, 1)));
        Assert.DoesNotContain("commit", s.Calls);                                  // the port saves the switch itself; the config block did not change

        UiTestKit.Press(root, PadButtons.Up);                                       // up from the row: the Window Size row
        Assert.Equal(CtlKind.WindowSize, At(screen, screen.Cursor).Kind);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(new Ctl(CtlKind.ScreenshotNotice, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);                                     // down from it: the achievement notice row (v0.1.9), the same column
        Assert.Equal(new Ctl(CtlKind.TrophyNotice, 0), At(screen, screen.Cursor));

        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Port.ScreenshotNotice);
        Assert.Contains("screenshot notice on", s.Calls);

        // a change from outside the screen shows in the row, and the pointer switches it too
        s.Port.ScreenshotNotice = false;
        Assert.True(screen.IsOn(new Ctl(CtlKind.ScreenshotNotice, 0)));
        (Ctl _, float nx, float ny) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.ScreenshotNotice, 1));
        UiTestKit.Point(root, nx + 20f, ny + 10f, click: true);
        Assert.True(s.Port.ScreenshotNotice);
    }

    [Fact]
    public void TheAchievementNoticePillsStartOffSwitchThePortSettingAndSitBetweenTheScreenshotNoticeAndSoundRows()
    {
        // v0.1.9: Off by default (the owner's choice: the player turns it on); Off earns achievements without the notice
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.False(s.Port!.TrophyNotice);
        Assert.True(screen.IsOn(new Ctl(CtlKind.TrophyNotice, 0)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.TrophyNotice, 1)));
        Assert.True(ConfigScreen.IsSwitch(CtlKind.TrophyNotice));
        Assert.Equal(CtlKind.TrophyNotice, ConfigScreen.SwitchKind(PortRow.TrophyNotice));

        Go(root, screen, new Ctl(CtlKind.TrophyNotice, 1));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Port.TrophyNotice);
        Assert.True(s.Port.ScreenshotNotice);                                       // the row above is another switch
        Assert.False(s.Port.TrophySound);                                           // and so is the row below
        Assert.Contains("achievement notice on", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.TrophyNotice, 1)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.TrophyNotice, 0)));
        Assert.DoesNotContain("commit", s.Calls);                                  // the port saves the switch itself; the config block did not change

        UiTestKit.Press(root, PadButtons.Up);                                       // up from the row: the Screenshot Notify row
        Assert.Equal(new Ctl(CtlKind.ScreenshotNotice, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);                                     // down from it: the sound row
        Assert.Equal(new Ctl(CtlKind.TrophySound, 1), At(screen, screen.Cursor));

        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.False(s.Port.TrophyNotice);
        Assert.Contains("achievement notice off", s.Calls);

        // a change from outside the screen (the launcher's switch writes the same key) shows in the row, and the pointer switches it too
        s.Port.TrophyNotice = true;
        Assert.True(screen.IsOn(new Ctl(CtlKind.TrophyNotice, 1)));
        (Ctl _, float nx, float ny) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.TrophyNotice, 0));
        RectF pill = screen.HitRect(new Ctl(CtlKind.TrophyNotice, 0), nx, ny);
        Assert.Equal((UiPrompts.SwitchPillWidth, UiPrompts.PillHeight), (pill.W, pill.H));
        UiTestKit.Point(root, nx + 20f, ny + 10f, click: true);
        Assert.False(s.Port.TrophyNotice);

        // the owner's word (FIXES D9): "Notify", not "Notice", in English; the other languages keep theirs (通知 / 提示 already mean notify)
        Assert.Equal(("Achievement^Notify", "実績の通知", "成就提示", "成就通知"), (UiPrompts.TrophyNoticeHeading(Language.En), UiPrompts.TrophyNoticeHeading(Language.Ja), UiPrompts.TrophyNoticeHeading(Language.Zc), UiPrompts.TrophyNoticeHeading(Language.Zt)));
    }

    [Fact]
    public void TheAchievementSoundPillsStartOffSwitchThePortSettingAndAreDimmedWhileTheNoticeIsOff()
    {
        // v0.1.9: the chime as a notice comes in, Off by default, right under the Achievement Notify row and above the clipboard row
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.False(s.Port!.TrophySound);
        Assert.True(screen.IsOn(new Ctl(CtlKind.TrophySound, 0)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.TrophySound, 1)));
        Assert.True(ConfigScreen.IsSwitch(CtlKind.TrophySound));
        Assert.Equal(CtlKind.TrophySound, ConfigScreen.SwitchKind(PortRow.TrophySound));
        (Ctl _, float x, float y) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.TrophySound, 1));
        Assert.Equal((UiPrompts.SwitchPillWidth, UiPrompts.PillHeight), (screen.HitRect(new Ctl(CtlKind.TrophySound, 1), x, y).W, screen.HitRect(new Ctl(CtlKind.TrophySound, 1), x, y).H));

        // it can be set while the notice is off: it then sounds once the notice is on as well
        Go(root, screen, new Ctl(CtlKind.TrophySound, 1));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Port.TrophySound);
        Assert.False(s.Port.TrophyNotice);                                          // the notice's row is another switch
        Assert.Contains("achievement sound on", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.TrophySound, 1)));
        Assert.DoesNotContain("commit", s.Calls);

        UiTestKit.Press(root, PadButtons.Up);                                       // up: the notice's row; down twice from it: the clipboard row
        Assert.Equal(new Ctl(CtlKind.TrophyNotice, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Down);
        UiTestKit.Press(root, PadButtons.Down);
        Assert.Equal(new Ctl(CtlKind.CopyText, 1), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.False(s.Port.TrophySound);
        Assert.Contains("achievement sound off", s.Calls);

        // the pointer switches it too, and a change from outside the screen (the launcher's switch writes the same key) shows in the row
        (Ctl _, float px, float py) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.TrophySound, 1));
        UiTestKit.Point(root, px + 20f, py + 10f, click: true);
        Assert.True(s.Port.TrophySound);
        s.Port.TrophySound = false;
        Assert.True(screen.IsOn(new Ctl(CtlKind.TrophySound, 0)));

        // while the notice is off the row is drawn dimmed, heading and pills (the chime would not sound), as the size row is in fullscreen; with the notice on it is not
        UiTestKit.Point(root, 100f, 100f);                                          // the pointer away from the pills: no hover
        Assert.True(screen.SoundRowDimmed);
        float dimmed = HeadingAlpha(root, screen);
        s.Port.TrophyNotice = true;
        Assert.False(screen.SoundRowDimmed);
        Assert.Equal(1f, HeadingAlpha(root, screen));
        Assert.True(dimmed is > 0f and < 0.6f, $"the heading of the sound row is drawn at {dimmed} while the notice is off");

        Assert.Equal(("Achievement^Sound", "実績の通知音", "成就提示音", "成就通知音"), (UiPrompts.TrophySoundHeading(Language.En), UiPrompts.TrophySoundHeading(Language.Ja), UiPrompts.TrophySoundHeading(Language.Zc), UiPrompts.TrophySoundHeading(Language.Zt)));
    }

    // the alpha of the glyphs the frame draws left of the pills on the sound row (its heading; the synthetic pages draw English)
    private static float HeadingAlpha(UiRoot root, ConfigScreen screen)
    {
        screen.ScrollNow(screen.ScrollMaximum);
        UiTestKit.Run(root, 2);
        (Ctl _, float x, float y) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.TrophySound, 0));
        // the row's two lines are centred on the pills' middle (y + 19), the lines of the rows above and below are 56 px away
        DrawCmd[] heading = [.. UiTestKit.Draw(root).Commands.ToArray().Where(c => c.Texture.Kind == TextureKind.GlyphPage && c.V3.X < x && (c.V0.Y + c.V3.Y) / 2f is var cy && cy >= y - 8f && cy <= y + 46f)];
        Assert.NotEmpty(heading);
        return heading.Max(c => (c.V0.Rgba >> 24) / 255f);
    }

    [Fact]
    public void TheHookerAndWebSocketPillsSwitchTheirPortSettingsAndStartFromTheGamesDefaults()
    {
        // package K23: the hooker output is on by default, the WebSocket off
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.True(screen.IsOn(new Ctl(CtlKind.TextHooker, 1)));
        Assert.False(screen.IsOn(new Ctl(CtlKind.TextHooker, 0)));
        Assert.True(screen.IsOn(new Ctl(CtlKind.TextWebSocket, 0)));

        Go(root, screen, new Ctl(CtlKind.TextHooker, 0));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.False(s.Port!.TextHookerOutput);
        Assert.Contains("text hooker off", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.TextHooker, 0)));

        UiTestKit.Press(root, PadButtons.Down);                                                        // down from the hooker row: the WebSocket row, the same column
        Assert.Equal(new Ctl(CtlKind.TextWebSocket, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Right);
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Port.TextWebSocket);
        Assert.Contains("text websocket on", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.TextWebSocket, 1)));
        UiTestKit.Press(root, PadButtons.Down);                                                        // and below it the slider
        Assert.Equal(new Ctl(CtlKind.Opacity), At(screen, screen.Cursor));
        Assert.DoesNotContain("commit", s.Calls);

        // the pointer too
        (Ctl _, float hx, float hy) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.TextHooker, 1));
        UiTestKit.Point(root, hx + 20f, hy + 10f, click: true);
        Assert.True(s.Port.TextHookerOutput);
    }

    [Fact]
    public void TheOpacitySliderStepsFivePercentWithLeftAndRightAndWritesThePs4Byte()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        Assert.Equal(255, s.Config.Hud10);                                          // the boot value: the PS4 look
        Assert.Equal(100, screen.WindowOpacityPercent);

        Go(root, screen, new Ctl(CtlKind.Opacity));
        UiTestKit.Press(root, PadButtons.Left);
        Assert.Equal(95, screen.WindowOpacityPercent);
        Assert.Equal(242, s.Config.Hud10);
        Assert.Equal(new Ctl(CtlKind.Opacity), At(screen, screen.Cursor));          // Left stepped the value; the cursor stayed (no side menu)
        Assert.Contains("config Setting", s.Calls);
        int settings = s.Calls.Count(c => c == "config Setting");

        for (int i = 0; i < 25; i++)
        {
            UiTestKit.Press(root, PadButtons.Left);
        }

        Assert.Equal(0, screen.WindowOpacityPercent);                               // clamped at 0 (19 steps did it; the rest changed nothing)
        Assert.Equal(0, s.Config.Hud10);
        Assert.Equal(settings + 19, s.Calls.Count(c => c == "config Setting"));
        UiTestKit.Press(root, PadButtons.Right);
        Assert.Equal(5, screen.WindowOpacityPercent);
        Assert.Equal(13, s.Config.Hud10);
        UiTestKit.Press(root, PadButtons.Decide);                                   // decide alone does nothing on a slider
        Assert.Equal(5, screen.WindowOpacityPercent);

        // every 5 % step round-trips through the byte
        for (int pct = 0; pct <= 100; pct += 5)
        {
            screen.WindowOpacityPercent = pct;
            Assert.Equal(pct, screen.WindowOpacityPercent);
        }

        // closing the screen writes the system data (the byte is in the config block of save.dat)
        UiTestKit.Press(root, PadButtons.Cancel);
        UiTestKit.Run(root, 60);
        Assert.Contains("commit", s.Calls);
    }

    [Fact]
    public void ThePointerClicksAndDragsTheOpacityKnobAndTheSwitchPills()
    {
        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open();
        screen.ScrollNow(screen.ScrollMaximum);
        UiTestKit.Run(root, 2);
        (Ctl _, float ox, float oy) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.Opacity));
        UiTestKit.Click(root, ox + 200f, oy + 17f);                                // the middle of the 400 px track
        Assert.Equal(50, screen.WindowOpacityPercent);
        Assert.Equal(new Ctl(CtlKind.Opacity), At(screen, screen.Cursor));
        UiTestKit.Drag(root, ox + 200f, oy + 17f, ox + 300f, oy + 17f);             // the knob follows the pointer
        Assert.Equal(75, screen.WindowOpacityPercent);
        UiTestKit.Click(root, ox - 8f, oy + 17f);                                   // the knob's half beyond the track's start
        Assert.Equal(0, screen.WindowOpacityPercent);
        UiTestKit.Drag(root, ox + 2f, oy + 17f, ox + 600f, oy + 17f);               // past the end: clamped
        Assert.Equal(100, screen.WindowOpacityPercent);
        UiTestKit.Click(root, ox + 123f, oy + 17f);                                 // between two steps: the nearer 5 %
        Assert.Equal(30, screen.WindowOpacityPercent);

        (Ctl _, float cx, float cy) = screen.Layout.First(t => t.Ctl == new Ctl(CtlKind.CopyText, 1));
        UiTestKit.Point(root, cx + 20f, cy + 10f, click: true);
        Assert.True(s.Port!.CopyTextToClipboard);
    }

    [Fact]
    public void RecommendedSettingsPutTheOpacityBackToThePs4Look()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        screen.WindowOpacityPercent = 40;
        screen.Change(new Ctl(CtlKind.Default), 0);
        UiTestKit.Run(root, 30);
        var dialog = Assert.IsType<ConfirmDialog>(root.Top);
        Assert.False(dialog.CursorOnYes);
        UiTestKit.Press(root, PadButtons.Left);
        UiTestKit.Press(root, PadButtons.Decide);
        UiTestKit.Run(root, 40);
        Assert.Equal(100, screen.WindowOpacityPercent);
    }

    [Fact]
    public void TheSoundTabCarriesTheMuteInBackgroundRowInsideTheSoundSettingsBoxAndMovesTheRestDown()
    {
        // two characters have spoken (their progress words carry bit 4), so the tab has voice rows to move down
        static void Heard(PreviewServices x)
        {
            x.SetGlobal(100, 4);
            x.SetGlobal(103, 4);
        }

        (UiRoot root, PreviewServices s, ConfigScreen screen) = Open(Heard, tab: 1);
        Assert.Equal(1, screen.CurrentTab);
        List<(Ctl Ctl, float X, float Y)> layout = screen.Layout;
        (Ctl _, float offX, float offY) = layout.First(t => t.Ctl == new Ctl(CtlKind.BackgroundMute, 0));
        (Ctl _, float onX, float onY) = layout.First(t => t.Ctl == new Ctl(CtlKind.BackgroundMute, 1));
        float lastSlider = layout.Where(t => t.Ctl.Kind == CtlKind.Slider && t.Ctl.Index < 3).Max(t => t.Y);
        float firstRadio = layout.Where(t => t.Ctl.Kind == CtlKind.Radio).Min(t => t.Y);
        Assert.Equal(offY, onY);
        Assert.True(onX > offX);
        Assert.True(offY > lastSlider, $"the mute row ({offY}) sits under the System Effects slider ({lastSlider})");
        Assert.True(offY + 34f < firstRadio, $"the mute row ({offY}) sits above the Voice Settings box ({firstRadio})");

        // the grid: the three sliders, the mute row, then the voice rows
        IReadOnlyList<IReadOnlyList<Ctl>> grid = screen.GridRows();
        Assert.Equal([new Ctl(CtlKind.BackgroundMute, 0), new Ctl(CtlKind.BackgroundMute, 1)], grid[3]);
        Assert.Equal(CtlKind.Radio, grid[4][0].Kind);

        // without the port's switches the row is gone and everything below the box is 64 px higher
        (_, _, ConfigScreen none) = Open(
            x =>
            {
                Heard(x);
                x.Port = null;
            },
            tab: 1);
        Assert.DoesNotContain(none.Layout, t => t.Ctl.Kind == CtlKind.BackgroundMute);
        float radioWithout = none.Layout.Where(t => t.Ctl.Kind == CtlKind.Radio).Min(t => t.Y);
        float voiceWithout = none.Layout.Where(t => t.Ctl.Kind == CtlKind.Slider && t.Ctl.Index >= 3).Min(t => t.Y);
        float voiceWith = layout.Where(t => t.Ctl.Kind == CtlKind.Slider && t.Ctl.Index >= 3).Min(t => t.Y);
        Assert.Equal(radioWithout + 64f, firstRadio);
        Assert.Equal(voiceWithout + 64f, voiceWith);
        Assert.Equal(none.ScrollMaximum + 64f, screen.ScrollMaximum);
        Assert.Equal(lastSlider, none.Layout.Where(t => t.Ctl.Kind == CtlKind.Slider && t.Ctl.Index < 3).Max(t => t.Y));      // the box above the band did not move

        // the pills switch the setting
        Assert.True(screen.IsOn(new Ctl(CtlKind.BackgroundMute, 0)));
        Go(root, screen, new Ctl(CtlKind.BackgroundMute, 1));
        UiTestKit.Press(root, PadButtons.Decide);
        Assert.True(s.Port!.MuteInBackground);
        Assert.Contains("mute in background on", s.Calls);
        Assert.True(screen.IsOn(new Ctl(CtlKind.BackgroundMute, 1)));
        UiTestKit.Press(root, PadButtons.Down);                                     // down from the row: the first voice radio
        Assert.Equal(CtlKind.Radio, At(screen, screen.Cursor).Kind);
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.BackgroundMute, 0), At(screen, screen.Cursor));
        UiTestKit.Press(root, PadButtons.Up);
        Assert.Equal(new Ctl(CtlKind.Minus, 2), At(screen, screen.Cursor));         // up: the System Effects row
    }

    [Fact]
    public void ThePortRowsDrawTheirHeadingsAndControlsAndHaveWordsInEveryLanguage()
    {
        (UiRoot root, _, ConfigScreen screen) = Open();
        screen.ScrollNow(screen.ScrollMaximum);
        UiTestKit.Run(root, 2);
        DrawCmd[] with = UiTestKit.Draw(root).Commands.ToArray();
        (UiRoot root2, _, ConfigScreen screen2) = Open(x => x.Port = null);
        screen2.ScrollNow(screen2.ScrollMaximum);
        UiTestKit.Run(root2, 2);
        DrawCmd[] without = UiTestKit.Draw(root2).Commands.ToArray();
        Assert.True(with.Length > without.Length + 4, $"the clipboard row adds its pills and heading ({with.Length} commands against {without.Length} without it)");

        (UiRoot root3, _, ConfigScreen sound) = Open(tab: 1);
        DrawCmd[] soundWith = UiTestKit.Draw(root3).Commands.ToArray();
        (UiRoot root4, _, _) = Open(x => x.Port = null, tab: 1);
        DrawCmd[] soundWithout = UiTestKit.Draw(root4).Commands.ToArray();
        Assert.True(soundWith.Length > soundWithout.Length + 4, $"the mute row adds its pills and heading ({soundWith.Length} against {soundWithout.Length})");
        _ = sound;

        foreach (Language lang in new[] { Language.Ja, Language.En, Language.Zc, Language.Zt })
        {
            Assert.NotEmpty(UiPrompts.CopyTextHeading(lang));
            Assert.NotEmpty(UiPrompts.OpacityHeading(lang));
            Assert.NotEmpty(UiPrompts.MuteHeading(lang));
            Assert.NotEmpty(UiPrompts.TextHookerHeading(lang));
            Assert.NotEmpty(UiPrompts.WebSocketHeading(lang));
            Assert.NotEmpty(UiPrompts.ScreenshotNoticeHeading(lang));
            Assert.Equal(lang == Language.Ja, UiPrompts.ScreenshotNoticeHeading(lang).Contains('^'));      // two lines in Japanese only (the one-line word ran up to the Off pill)
            Assert.Contains("WebSocket", UiPrompts.WebSocketHeading(lang), StringComparison.Ordinal);
            Assert.NotEqual(UiPrompts.SwitchLabel(lang, 0), UiPrompts.SwitchLabel(lang, 1));
        }

        // v0.1.9 (FIXES D9): "Screenshot Notify", as the achievement row under it says "Notify" (the owner's word); the other languages keep theirs
        Assert.Equal(("Screenshot Notify", "スクリーンショット^通知", "截图提示", "螢幕截圖通知"), (UiPrompts.ScreenshotNoticeHeading(Language.En), UiPrompts.ScreenshotNoticeHeading(Language.Ja), UiPrompts.ScreenshotNoticeHeading(Language.Zc), UiPrompts.ScreenshotNoticeHeading(Language.Zt)));
        Assert.Equal(("Text Hooker^Output", "テキストフッカー出力", "文本钩子输出", "文字鉤子輸出"), (UiPrompts.TextHookerHeading(Language.En), UiPrompts.TextHookerHeading(Language.Ja), UiPrompts.TextHookerHeading(Language.Zc), UiPrompts.TextHookerHeading(Language.Zt)));
        Assert.Equal(("Text WebSocket", "テキスト WebSocket"), (UiPrompts.WebSocketHeading(Language.En), UiPrompts.WebSocketHeading(Language.Ja)));

        Assert.Equal(("Copy Text to^Clipboard", "Text Window^Opacity", "Mute in Background", "Off", "On"), (UiPrompts.CopyTextHeading(Language.En), UiPrompts.OpacityHeading(Language.En), UiPrompts.MuteHeading(Language.En), UiPrompts.SwitchLabel(Language.En, 0), UiPrompts.SwitchLabel(Language.En, 1)));
        Assert.Equal(("テキストをコピー", "ウィンドウの不透明度", "バックグラウンドで消音", "オフ", "オン"), (UiPrompts.CopyTextHeading(Language.Ja), UiPrompts.OpacityHeading(Language.Ja), UiPrompts.MuteHeading(Language.Ja), UiPrompts.SwitchLabel(Language.Ja, 0), UiPrompts.SwitchLabel(Language.Ja, 1)));
        Assert.Equal(("复制文本到剪贴板", "文本框不透明度", "后台静音", "关", "开"), (UiPrompts.CopyTextHeading(Language.Zc), UiPrompts.OpacityHeading(Language.Zc), UiPrompts.MuteHeading(Language.Zc), UiPrompts.SwitchLabel(Language.Zc, 0), UiPrompts.SwitchLabel(Language.Zc, 1)));
        Assert.Equal(("複製文字", "文字框不透明度", "背景靜音", "關", "開"), (UiPrompts.CopyTextHeading(Language.Zt), UiPrompts.OpacityHeading(Language.Zt), UiPrompts.MuteHeading(Language.Zt), UiPrompts.SwitchLabel(Language.Zt, 0), UiPrompts.SwitchLabel(Language.Zt, 1)));
        Assert.Equal("65%", UiPrompts.PercentLabel(65));
    }
}
