using Tsuki.Engine.Contracts;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.Tests.Data.Ui;

/// <summary>
/// Package K9 on the user's own data: the key caps, Xbox glyphs and the controls page replacement against the real guide sheet, the real controls pages and the
/// real glyph pages in all four languages.
/// </summary>
public sealed class PromptRealDataTests
{
    private static readonly Language[] Languages = [Language.Ja, Language.En, Language.Zc, Language.Zt];

    private static readonly string[] Scenes = ["guides", "save", "load", "config-controls", "config-controls-cursor", "config-display", "backlog", "confirm", "title-press", "gallery-cg-view", "flowchart-game"];

    public static TheoryData<string, Language, PromptSetting> Cases()
    {
        var data = new TheoryData<string, Language, PromptSetting>();
        foreach (string scene in Scenes)
        {
            foreach (Language l in Languages)
            {
                foreach (PromptSetting s in new[] { PromptSetting.Keyboard, PromptSetting.Xbox })
                {
                    data.Add(scene, l, s);
                }
            }
        }

        return data;
    }

    private static DrawCmd[] DrawList(UiRoot root)
    {
        var list = new DisplayList(2048);
        list.Reset(root.Context.Frame);
        root.Draw(list);
        list.Seal();
        return list.Commands.ToArray();
    }

    [GameDataTheory]
    [MemberData(nameof(Cases))]
    public void EveryScreenDrawsItsPromptsOnTheRealSheetsAndGlyphPages(string scene, Language lang, PromptSetting setting)
    {
        var services = new PreviewServices(lang) { PromptSetting = setting, DecideIsCross = true };
        UiRoot root = UiPreview.Open(scene, SharedSource.Source, services);
        DrawCmd[] cmds = DrawList(root);
        Assert.Empty(root.Context.Assets.Missing);
        Assert.Empty(root.Context.Assets.MissingPictures);
        Assert.Equal(setting == PromptSetting.Keyboard ? PromptStyle.Keyboard : PromptStyle.Xbox, root.Painter.PromptStyle);
        // the guide icon sheet is not drawn at all: the caps and glyphs replace it
        Assert.DoesNotContain(cmds, c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)UiSet.DispParts && t.C == 16);
        if (scene is "guides" or "save" or "load" or "config-controls" or "backlog")
        {
            // the English glyph pages carry the cap labels whatever the UI language is
            Assert.Contains(cmds, c => c.Texture is { Kind: TextureKind.GlyphPage } t && t.A == (int)Language.En);
            foreach (DrawCmd c in cmds.Where(c => c.Texture.Kind == TextureKind.GlyphPage && c.Texture.A == (int)Language.En).Take(40))
            {
                Assert.Equal(1024, SharedSource.Source.FontPage(Language.En, c.Texture.B).Width);
            }
        }

        if (scene == "title-press" && setting == PromptSetting.Keyboard)
        {
            Assert.DoesNotContain(cmds, c => c.Texture is { Kind: TextureKind.UiTile } t && t.A == (int)UiSet.TitleParts && t.C == 14);
        }
    }

    [GameDataFact]
    public void TheGuideSheetHasTheIconsAndWordsThePromptTablesAssume()
    {
        var tables = new UiAssets(SharedSource.Source, Language.En).Tables;
        IReadOnlyList<SheetCell> icons = tables.GuideIconCells;
        Assert.Equal(23, icons.Count);
        Assert.All(new[] { PromptBindings.CellEast, PromptBindings.CellWest, PromptBindings.CellNorth, PromptBindings.CellSouth }, c =>
        {
            Assert.InRange(icons[c].Width, 26, 40);                            // the four round face buttons
            Assert.InRange(icons[c].Height, 26, 40);
        });
        Assert.All(new[] { 16, 17, 19, 20, 22 }, c => Assert.True(icons[c].Width > icons[c].Height, $"icon {c} is a wide label: {icons[c]}"));
        Assert.All(Enumerable.Range(0, 7), c => Assert.InRange(Math.Abs(icons[c].Width - icons[c].Height), 0, 6));       // the d-pads are square
        foreach (Language l in Languages)
        {
            Assert.Equal(27, new UiAssets(SharedSource.Source, l).Tables.GuideWordCells(l).Count);
        }
    }

    // ---- the controls page ------------------------------------------------------------------------------------------------------------------------

    private static (int W, int H, byte[] Rgba) Compose(Picture p)
    {
        var rgba = new byte[p.Width * p.Height * 4];
        foreach (PictureTile t in p.Tiles)
        {
            if (t.Flags == TileFlags.Empty)
            {
                continue;
            }

            ReadOnlySpan<byte> src = t.Rgba.Span;
            for (int y = 0; y < t.Source.Height; y++)
            {
                for (int x = 0; x < t.Source.Width; x++)
                {
                    int s = (((t.Source.Y + y) * t.StoredWidth) + t.Source.X + x) * 4;
                    int dx = t.X + x;
                    int dy = t.Y + y;
                    if ((uint)dx < (uint)p.Width && (uint)dy < (uint)p.Height)
                    {
                        src.Slice(s, 4).CopyTo(rgba.AsSpan(((dy * p.Width) + dx) * 4, 4));
                    }
                }
            }
        }

        return (p.Width, p.Height, rgba);
    }

    private static bool Bright(byte[] rgba, int w, int x, int y)
    {
        int i = ((y * w) + x) * 4;
        return rgba[i + 3] > 200 && rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] > 200;
    }

    [GameDataTheory]
    [InlineData(Language.Ja, 16)]
    [InlineData(Language.En, 16)]
    [InlineData(Language.Zc, 17)]
    [InlineData(Language.Zt, 17)]
    [InlineData(Language.En, 17)]
    public void TheIconColumnOfTheControlsPageSitsInTheBoxesTheCapsReplace(Language lang, int child)
    {
        (int w, int h, byte[] px) = Compose(SharedSource.Source.UiSheet(UiSet.ConfParts, lang, child));
        Assert.Equal((1416, 1528), (w, h));
        float[] centres = UiPrompts.ControlRowCentres;
        int colX = (int)UiPrompts.ControlColumnX - 16;
        for (int y = (int)UiPrompts.ControlRowsTop; y < (int)UiPrompts.ControlRowsBottom; y++)
        {
            bool inBox = centres.Any(c => y >= MathF.Floor(c) - 24f && y < MathF.Floor(c) + 24f);
            bool icon = false;
            for (int x = colX; x < colX + 140; x++)
            {
                icon |= Bright(px, w, x, y);
            }

            if (!inBox)
            {
                Assert.False(icon, $"{lang} sheet {child}: an icon pixel at page row {y} is outside every icon box");
            }
        }

        // every row has its icon, centred on the measured centre (within 3 rows)
        foreach (float c in centres)
        {
            var rows = new List<int>();
            for (int y = (int)c - 30; y <= (int)c + 30; y++)
            {
                for (int x = colX; x < colX + 140; x++)
                {
                    if (Bright(px, w, x, y))
                    {
                        rows.Add(y);
                        break;
                    }
                }
            }

            Assert.NotEmpty(rows);
            Assert.InRange((rows.Min() + rows.Max()) / 2f, c - 3f, c + 3f);
        }

        // the filler slices (rows c-27 .. c-21 of the first row below it) are flat band: no icon, and no row line in them
        for (int i = 1; i < centres.Length; i++)
        {
            int y0 = (int)MathF.Floor(centres[i]) - 27;
            for (int y = y0; y < y0 + 6; y++)
            {
                for (int x = colX; x < colX + 140; x++)
                {
                    Assert.False(Bright(px, w, x, y), $"{lang} sheet {child}: slice row {y} holds a bright pixel at x {x}");
                }
            }
        }
    }

    // ---- the words -------------------------------------------------------------------------------------------------------------------------------

    private static int GlyphQuads(Action<UiPainter> draw, Language lang)
    {
        var painter = new UiPainter(new UiAssets(SharedSource.Source, lang));
        var list = new DisplayList(256);
        list.Reset(0);
        painter.Begin(list);
        draw(painter);
        list.Seal();
        return list.Commands.ToArray().Count(c => c.Texture.Kind == TextureKind.GlyphPage);
    }

    [GameDataFact]
    public void EveryCapLabelAndEveryTranslatedWordHasItsGlyphsInTheGlyphPages()
    {
        string[] caps = [.. PromptBindings.Keys.SelectMany(k => k.Keys), "Drag", "LB", "RB", "LT", "RT", "LS", "RS", "Menu", "A", "B", "X", "Y", "Enter", "Space", "WASD", "A D", "W S"];      // the last three: the guide's cursor icons (package K14)
        foreach (string label in caps.Distinct())
        {
            int expected = label.Count(ch => !char.IsWhiteSpace(ch));
            foreach (Language l in Languages)
            {
                // the caps always use the English pages, so the UI language must not matter
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, 100, 34), false, false, 1f), l), expected, label.Length);      // a space may be a quad of its own
            }
        }

        foreach (Language l in Languages)
        {
            string heading = UiPrompts.SettingHeading(l);
            Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, heading, 0, 20, 1f), l), heading.Count(ch => !char.IsWhiteSpace(ch)), heading.Length);      // a space may be a quad of its own
            for (int i = 0; i < 4; i++)
            {
                string label = UiPrompts.SettingLabel(l, i);
                bool translated = i < 2 && l != Language.En;
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, 120, 34), false, false, 1f, translated), l), label.Count(ch => !char.IsWhiteSpace(ch)), label.Length);
            }

            // package K19: the Display Mode and Window Size rows of the System tab (the headings and the mode words in the language's own pages, the sizes in the English pages)
            foreach (string word in new[] { UiPrompts.DisplayHeading(l), UiPrompts.SizeHeading(l) })
            {
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, word, 0, 20, 1f), l), word.Count(ch => !char.IsWhiteSpace(ch)), word.Length);
            }

            for (int i = 0; i < 2; i++)
            {
                string label = UiPrompts.DisplayLabel(l, i);
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, UiPrompts.DisplayPillWidth(i), 34), false, false, 1f, l != Language.En), l), label.Count(ch => !char.IsWhiteSpace(ch)), label.Length);
            }

            foreach ((int w, int h) in Tsuki.Platform.WindowFit.Presets)
            {
                string label = UiPrompts.SizeLabel(w, h);
                Assert.Equal(label.Length, GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, UiPrompts.SizePillWidth, 34), false, false, 1f), l));
            }

            // package K21: the headings of the clipboard, opacity and background-mute rows (a ^ breaks the two-line English ones), the Off / On words, the percent label;
            // package K23: the Text Hooker Output and Text WebSocket headings; package K24: the Screenshot Notify heading
            foreach (string word in new[] { UiPrompts.CopyTextHeading(l), UiPrompts.OpacityHeading(l), UiPrompts.MuteHeading(l), UiPrompts.TextHookerHeading(l), UiPrompts.WebSocketHeading(l), UiPrompts.ScreenshotNoticeHeading(l) })
            {
                int ink = word.Count(ch => !char.IsWhiteSpace(ch) && ch != '^');
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, word, 0, 20, 1f), l), ink, word.Length);
            }

            for (int i = 0; i < 2; i++)
            {
                string label = UiPrompts.SwitchLabel(l, i);
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawPill(p, label, new RectF(0, 0, UiPrompts.SwitchPillWidth, 34), false, false, 1f, l != Language.En), l), label.Length, label.Length);
            }

            Assert.Equal(4, GlyphQuads(p => UiPrompts.DrawText(p, UiPrompts.PercentLabel(100), 0, 20, 1f, translated: false), l));
            // package K24: the two screenshot toasts are short sentences without a file name or an error, drawn by the language's own pages at the toast's scale
            foreach (PortString id in new[] { PortString.ScreenshotSaved, PortString.ScreenshotFailed })
            {
                string toast = PortText.Get(id, l);
                Assert.DoesNotContain("{0}", toast, StringComparison.Ordinal);
                Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, toast, 0, 20, 1f, 0.32), l), toast.Count(ch => !char.IsWhiteSpace(ch)), toast.Length);      // a space may be a quad of its own
            }
            // package K23: the WebSocket toasts ("ws://127.0.0.1:6677" has three colons and two slashes, which may be quads or not)
            string listening = PortText.Format(PortString.TextWebSocketListening, l, 6677);
            Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, listening, 0, 20, 1f), l), listening.Count(ch => !char.IsWhiteSpace(ch)) - 6, listening.Length);
            string failed = PortText.Format(PortString.TextWebSocketFailed, l, 6677, "in use");
            Assert.InRange(GlyphQuads(p => UiPrompts.DrawText(p, failed, 0, 20, 1f), l), failed.Count(ch => !char.IsWhiteSpace(ch)) - 4, failed.Length);
        }

        Assert.True(GlyphQuads(p => UiPrompts.DrawPressAnyKey(p, 1f), Language.En) >= "PRESSANYKEY".Length);      // the glyphs, with their outline copies
    }

    /// <summary>
    /// The characters of <paramref name="text"/> the font of <paramref name="l"/> has no glyph for, in the style the UI draws its words in (style 0): the layout
    /// looks a character up by its UTF-8 bytes, in the "narrow" table (one and two bytes) or the language's wide table, and draws the blank fallback cell for one
    /// that is in neither (LanguageGlyphs.CharGlyph). Counting the quads cannot tell: the fallback is a quad too.
    /// </summary>
    private static string MissingGlyphs(string text, Language l)
    {
        Tsuki.Engine.Text.FontTables fonts = new UiAssets(SharedSource.Source, l).Tables.Fonts;
        string wide = l switch { Language.Ja => "ja_wide", Language.En => "en_wide", Language.Zc => "zc_wide", _ => "zt_wide" };
        var missing = new System.Text.StringBuilder();
        Span<byte> utf8 = stackalloc byte[4];
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsWhiteSpace(rune) || rune.Value == '^')
            {
                continue;                                                       // a space is an advance and ^ a line break: neither is looked up
            }

            int n = rune.EncodeToUtf8(utf8);
            ulong key = 0;
            for (int i = 0; i < n; i++)
            {
                key = (key << 8) | utf8[i];
            }

            if (fonts.Lookup(key >= 0x10000 ? wide : "narrow").Get(key) < 0)
            {
                missing.Append(rune.ToString());
            }
        }

        return missing.ToString();
    }

    // every character of a string the port writes itself has a glyph in the language's own pages, and the string draws at least that many glyph quads
    private static void AssertDrawn(string text, Language l, double scale)
    {
        Assert.True(MissingGlyphs(text, l).Length == 0, $"{l} \"{text}\": the language's font pages have no glyph for \"{MissingGlyphs(text, l)}\"");
        int ink = text.Count(ch => !char.IsWhiteSpace(ch) && ch != '^');
        int quads = GlyphQuads(p => UiPrompts.DrawText(p, text, 0, 20, 1f, scale), l);
        Assert.True(quads >= ink && quads <= text.Length, $"{l} \"{text}\" draws {quads} glyphs for {ink} characters");
    }

    [GameDataFact]
    public void EveryWordOfTheAchievementNoticeHasItsGlyphsAndEveryNameFitsTheBoxInEveryLanguage()
    {
        // v0.1.9. The check can tell a missing character: the Simplified Chinese pages have no 铜 (bronze) and the Traditional Chinese ones no 盃 (cup), which is why
        // the port's words go around them; the pages do have 银 (silver) and 杯 (cup)
        Assert.Equal("铜", MissingGlyphs("白金、金、银、铜", Language.Zc));
        Assert.Equal("盃", MissingGlyphs("獎盃、獎杯", Language.Zt));
        Assert.Equal(string.Empty, MissingGlyphs("Achievement unlocked: 30 / 30!", Language.En));

        // the icons of the 30 achievements as the game makes them for the notice, from the player's own pictures (the same in every language)
        UiComposedPicture[] icons = [.. Enumerable.Range(0, Tsuki.Engine.Trophies.TrophyTable.Count).Select(id =>
        {
            UiComposedPicture? icon = Tsuki.App.Services.TrophyIcons.Notice(SharedSource.Source, id);
            Assert.True(icon is { Width: UiTrophyNotice.IconSize, Height: UiTrophyNotice.IconSize }, $"achievement {id} has no icon of the notice's size in this game folder");
            Assert.Equal(UiTrophyNotice.IconSize * UiTrophyNotice.IconSize * 4, icon!.Rgba.Length);
            return icon;
        })];
        Assert.Null(Tsuki.App.Services.TrophyIcons.Notice(SharedSource.Source, 30));
        Assert.Null(Tsuki.App.Services.TrophyIcons.Notice(SharedSource.Source, -1));

        foreach (Language l in Languages)
        {
            var assets = new UiAssets(SharedSource.Source, l);

            // the two Config rows' headings (the notice and its sound, D7), at the size the rows draw them: two lines in English, one in the other languages; and the
            // Screenshot Notify row's above them, whose English word changed with D9 ("Notify": the f and the y are in the pages too)
            foreach (string heading in new[] { UiPrompts.TrophyNoticeHeading(l), UiPrompts.TrophySoundHeading(l), UiPrompts.ScreenshotNoticeHeading(l) })
            {
                AssertDrawn(heading, l, heading.Contains('^', StringComparison.Ordinal) ? 0.36 : 0.4);
                AssertDrawn(heading, l, 0.4);
            }

            Assert.NotEqual(UiPrompts.TrophyNoticeHeading(l), UiPrompts.TrophySoundHeading(l));

            // the port's own words at the sizes the notice draws them: the heading, the three names, the stand-in name
            AssertDrawn(UiTrophyText.Heading(l), l, 0.32);
            foreach (PortString id in new[] { PortString.AchievementNameAll, PortString.AchievementNameFirstLesson, PortString.AchievementNameEveryLesson })
            {
                Assert.DoesNotContain("{0}", PortText.Get(id, l), StringComparison.Ordinal);
                AssertDrawn(PortText.Get(id, l), l, 0.4);
            }

            AssertDrawn(PortText.Format(PortString.AchievementNameNumbered, l, 27), l, 0.4);

            // the 27 chapter achievements carry the game's own chapter title: ui_text 358 + the chapter's phase without the two digits and the space in front
            var names = new List<string>();
            for (int id = Tsuki.Engine.Trophies.TrophyTable.FirstChapter; id <= Tsuki.Engine.Trophies.TrophyTable.LastChapter; id++)
            {
                string raw = assets.Text(Tsuki.Engine.Trophies.TrophyTable.ChapterTextBase + Tsuki.Engine.Trophies.TrophyTable.ChapterPhase(id));
                string name = UiTrophyText.Name(assets, id);
                Assert.True(name.Length >= 2, $"{l} achievement {id} has no name");
                Assert.Equal(raw.Length - 3, name.Length);
                Assert.EndsWith(name, raw, StringComparison.Ordinal);
                Assert.True(char.IsAsciiDigit(raw[0]) && char.IsAsciiDigit(raw[1]) && char.IsWhiteSpace(raw[2]), $"{l} chapter text of achievement {id} does not start with its day number");
                Assert.NotEqual(PortText.Format(PortString.AchievementNameNumbered, l, id), name);
                Assert.DoesNotContain('^', name);
                AssertDrawn(name, l, 0.4);
                AssertDrawn(name, l, 0.34);
                names.Add(name);
            }

            Assert.Equal(names[23], names[24]);                                 // the two ways through day 14 share their chapter title
            Assert.Equal(26, names.Distinct(StringComparer.Ordinal).Count());

            // every notice the game can show, on the real pages (those of the title's catch-up are the same notices in shorter times): the box keeps its corner and
            // its height, stays in the right half of the screen, and holds both lines whole (every glyph the two lines have is drawn inside it). Each shows its
            // achievement's icon, which the host has made by the time the notice comes in
            var notices = new List<(int Id, string Heading, string Name)>();
            for (int id = 0; id < Tsuki.Engine.Trophies.TrophyTable.Count; id++)
            {
                notices.Add((id, UiTrophyText.Heading(l), UiTrophyText.Name(assets, id)));
            }

            float narrowest = float.MaxValue;
            float widest = 0f;
            foreach ((int id, string heading, string name) in notices)
            {
                UiRoot root = UiPreview.Open("mode", SharedSource.Source, new PreviewServices(l));
                int plain = DrawList(root).Length;
                root.Context.Assets.SetTrophyIcon(id, icons[id]);
                UiTrophyNotice notice = root.ShowTrophy(Tsuki.Engine.Trophies.TrophyTable.Grade(id), heading, name, id);
                for (int i = 0; i < UiTrophyNotice.FadeInFrames; i++)
                {
                    root.Step(new InputFrame { Frame = root.Context.Frame + 1 });
                }

                RectF box = notice.Box(root.Painter);
                Assert.Equal((1880f, 18f, 84f), (box.Right, box.Y, box.H));
                Assert.True(box.Bottom <= 104f);
                Assert.True(box.X >= 960f, $"{l} achievement {id}: the box {box} of \"{name}\" reaches into the left half of the screen");
                narrowest = MathF.Min(narrowest, box.W);
                widest = MathF.Max(widest, box.W);
                DrawCmd[] drawn = DrawList(root);
                Assert.Equal(DrawKind.OverlaysBegin, drawn[plain].Kind);                            // the notice follows the overlay mark (v0.1.9)
                DrawCmd[] added = drawn[(plain + 1)..];
                DrawCmd[] glyphs = [.. added.Where(c => c.Texture.Kind == TextureKind.GlyphPage)];
                int ink = (heading + name).Count(ch => !char.IsWhiteSpace(ch));
                Assert.True(glyphs.Length >= ink, $"{l} achievement {id}: {glyphs.Length} glyphs drawn for the {ink} characters of \"{heading}\" and \"{name}\"");
                Assert.All(glyphs, c => Assert.True(c.V0.X >= box.X + UiTrophyNotice.PlaceSize && c.V3.X <= box.Right - 8f && c.V0.Y >= box.Y && c.V3.Y <= box.Bottom, $"{l} achievement {id}: a glyph of \"{name}\" at ({c.V0.X}, {c.V0.Y})-({c.V3.X}, {c.V3.Y}) is outside the box {box}"));
                Assert.All(added, c => Assert.True(c.V0.X >= box.X - 1f && c.V3.X <= box.Right + 1f && c.V0.Y >= box.Y - 1f && c.V3.Y <= box.Bottom + 1f, $"{l} achievement {id}: a quad is outside the box {box}"));

                // the picture: the achievement's own icon, whole and pixel for pixel in the middle of the picture's place
                DrawCmd picture = Assert.Single(added, c => c.Texture.Kind == TextureKind.UiComposed);
                float inset = 2f + ((UiTrophyNotice.PlaceSize - UiTrophyNotice.IconSize) / 2f);
                Assert.Equal((box.X + inset, box.Y + inset, box.X + inset + UiTrophyNotice.IconSize, box.Y + inset + UiTrophyNotice.IconSize), (picture.V0.X, picture.V0.Y, picture.V3.X, picture.V3.Y));
                Assert.Equal((0f, 0f, 1f, 1f), (picture.V0.U, picture.V0.V, picture.V3.U, picture.V3.V));
                Assert.Equal(UiTrophyPicture.Icon, notice.Picture);
                Assert.Equal(UiComposedId.TrophyIcon(id), picture.Texture.A);
                Assert.Same(icons[id], root.Context.Assets.Composed(picture.Texture.A, (Language)picture.Texture.B));
            }

            Assert.True(narrowest >= 300f && widest <= 920f, $"{l}: the boxes are {narrowest} to {widest} wide");
        }
    }
}
