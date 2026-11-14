// The achievement notice of the PC port (v0.1.9): when one of the PS4's 30 trophies is earned, a box comes in at the top right corner with the achievement's icon
// (a square the host cuts from one of the player's own pictures), a small heading and the achievement's name, stays for four seconds and fades out. The PS4 shows
// its trophies through the console, so the notice is the port's own look: the toast's palette, in the only strip of the screen the page text never uses (the page
// starts at about y 110; the mode banner is at the top left). It takes no input. Several notices wait their turn (UiRoot keeps the line); while one waits, the
// one on show holds for a shorter time. The notices of the title's catch-up (what a save's progress had already earned, shown once with this version) are short
// ones, one per achievement, and a key the title takes ends their line (UiRoot). The chime the player can switch on is the host's: UiRoot says when a notice starts
// its way in, and the notice says whether its start sounds it (Chime: every notice of its own, the first of the catch-up's line only).
//
// The picture at the left. The host makes the icon on a worker when the achievement is earned, so the notice of one achievement does not start its way in until
// the icon is settled (UiAssets.TrophyIconSettled: it is there, or the host said there is none), for at most IconWaitFrames. At that moment it takes what it will
// show and keeps it until it has gone: the icon, else the emblem of the grade (a trophy cup the port draws on the icons' tile, UiAssets.TrophyEmblem: no game
// data, never waited for). An icon that arrives later is not swapped in: a picture that changes under the player's eyes is worse than the emblem.
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;

namespace Tsuki.Ui;

/// <summary>What an achievement notice shows at its left.</summary>
public enum UiTrophyPicture
{
    /// <summary>Nothing yet: the notice waits for its icon and has not started its way in.</summary>
    Waiting = 0,

    /// <summary>The achievement's own icon (<see cref="UiComposedId.TrophyIcon"/>).</summary>
    Icon = 1,

    /// <summary>The emblem of the grade, drawn by the port (<see cref="UiComposedId.TrophyEmblem"/>): there is no icon.</summary>
    Emblem = 2,
}

/// <summary>
/// An achievement notice: its icon (or the emblem of its grade), a heading and a name in a rounded box at the top right, shown for about 4.6 seconds (a notice of the
/// title's catch-up for about 1.5).
/// </summary>
public sealed class UiTrophyNotice
{
    /// <summary>The most frames the notice of one achievement waits for its icon before it comes in without it (0.75 s).</summary>
    public const int IconWaitFrames = 45;

    /// <summary>Frames of the way in (the box fades in and moves the last few pixels to its place).</summary>
    public const int FadeInFrames = 12;

    /// <summary>Frames the notice stays fully visible (4 s) when nothing waits behind it.</summary>
    public const int HoldFrames = 240;

    /// <summary>Frames it stays fully visible while another notice waits (2.5 s).</summary>
    public const int QueuedHoldFrames = 150;

    /// <summary>Frames of the fade-out.</summary>
    public const int FadeOutFrames = 24;

    /// <summary>Frames of the way in of a notice of the title's catch-up (<see cref="CatchUp"/>): one notice per achievement, about 1.5 s each (v0.1.9, D7).</summary>
    public const int CatchUpFadeInFrames = 10;

    /// <summary>Frames a notice of the catch-up stays fully visible, whatever waits behind it.</summary>
    public const int CatchUpHoldFrames = 70;

    /// <summary>Frames of the fade-out of a notice of the catch-up.</summary>
    public const int CatchUpFadeOutFrames = 12;

    /// <summary>The right edge of the box on the 1920 x 1080 canvas.</summary>
    public const float Right = 1880f;

    /// <summary>The top edge of the box.</summary>
    public const float Top = 18f;

    /// <summary>The height of the box: it ends at y 102, above the first line of the page text (about y 110 in English, 154 in the other languages).</summary>
    public const float Height = 84f;

    /// <summary>The box never reaches further left than this, however long the name is.</summary>
    public const float LeftLimit = 40f;

    /// <summary>The side of the picture's place at the left of the box (the box is this high and a two-pixel border more).</summary>
    public const int PlaceSize = 80;

    /// <summary>
    /// The side of an achievement's icon and of the grade's emblem, which the notice draws pixel for pixel in the middle of the picture's place, with four
    /// pixels of the box around it. A picture that filled the place put its ring in the grade's colour right against the box's border (two rings in two
    /// colours, hard to tell apart for silver and platinum, and corners that crossed). It is the size the icon code is told for the notice
    /// (<see cref="Tsuki.Data.Trophies.TrophyIconArt.NoticeSize"/>).
    /// </summary>
    public const int IconSize = Tsuki.Data.Trophies.TrophyIconArt.NoticeSize;

    private const float Border = 2f;
    private const float Gap = 16f;                             // between the picture's place and the words
    private const float PadRight = 24f;
    private const float MinWidth = 300f;
    private const float ComfortWidth = 400f;                   // a name up to this wide keeps the large size
    private const int MaxNameLength = 160;                     // no name comes near it (the longest chapter title has 34 characters); it bounds the fitting below
    private const float Slide = 24f;                           // the box comes in from this far to the right
    private const float HeadingCy = 27f;                       // the centre lines of the two rows of words, from the top of the box
    private const float NameCy = 56f;
    private const double HeadingScale = 0.32;
    private static readonly double[] NameScales = [0.4, 0.34, 0.28];
    private static readonly int HeadingColour = TextStyle.Rgb565(158, 219, 255);       // the border's pale blue

    private int _waited;
    private UiTrophyPicture _picture;
    private int _in;
    private int _held;
    private int _out;
    private (Language Language, RectF Box, double Scale, string Name)? _layout;

    internal UiTrophyNotice(int id, TrophyGrade grade, string heading, string name, bool catchUp, bool chime)
    {
        ArgumentNullException.ThrowIfNull(heading);
        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Grade = grade;
        CatchUp = catchUp;
        Chime = chime;
        // one line each: a line break of ui_text (^) would run out of the box
        Heading = heading.Replace('^', ' ');
        Name = name.Replace('^', ' ');
    }

    /// <summary>The trophy (0..29).</summary>
    public int Id { get; }

    /// <summary>
    /// True for a notice of the title's catch-up (what a save's progress had already earned): the short times (<see cref="CatchUpFadeInFrames"/>,
    /// <see cref="CatchUpHoldFrames"/>, <see cref="CatchUpFadeOutFrames"/>), and a key the title takes ends the line of them (<see cref="UiRoot.ShowTrophy"/>).
    /// </summary>
    public bool CatchUp { get; }

    /// <summary>True when the start of this notice's way in sounds the chime (when the player has it on): a notice of its own, or the first of the catch-up's line.</summary>
    public bool Chime { get; }

    /// <summary>True once the notice has started its way in (its picture is chosen); a notice that waits for its icon has not.</summary>
    public bool Started => _picture != UiTrophyPicture.Waiting;

    /// <summary>The grade: the emblem and the icon's ring are this grade's.</summary>
    public TrophyGrade Grade { get; }

    /// <summary>
    /// What stands at the left: <see cref="UiTrophyPicture.Waiting"/> until the notice starts its way in, then the picture it took at that moment. It only ever
    /// steps down (icon to emblem), when the icon can no longer be drawn; it never changes to a better one while the notice is shown.
    /// </summary>
    public UiTrophyPicture Picture => _picture;

    /// <summary>Frames this notice has waited for its icon (0..<see cref="IconWaitFrames"/>).</summary>
    public int Waited => _waited;

    /// <summary>The small first line ("Achievement unlocked").</summary>
    public string Heading { get; }

    /// <summary>The second line: the achievement's name.</summary>
    public string Name { get; }

    /// <summary>The alpha 0..1 of this frame.</summary>
    public float Alpha => _in < InFrames
        ? _in / (float)InFrames
        : _out == 0
            ? 1f
            : MathF.Max(0f, 1f - (_out / (float)OutFrames));

    private int InFrames => CatchUp ? CatchUpFadeInFrames : FadeInFrames;

    private int OutFrames => CatchUp ? CatchUpFadeOutFrames : FadeOutFrames;

    /// <summary>Frames it has been fully visible so far.</summary>
    public int Held => _held;

    /// <summary>True once the notice has gone.</summary>
    public bool Done => _out >= OutFrames;

    /// <summary>
    /// One 60 Hz step; <paramref name="waiting"/> says that another notice waits behind this one (the hold is then <see cref="QueuedHoldFrames"/>; a notice of the
    /// catch-up always holds <see cref="CatchUpHoldFrames"/>). The first steps wait for the achievement's icon in <paramref name="assets"/>; an icon that is settled
    /// costs no step.
    /// </summary>
    internal void Step(bool waiting, UiAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        bool own = Id is >= 0 and < TrophyTable.Count;
        if (_picture == UiTrophyPicture.Waiting)
        {
            if (own && !assets.TrophyIconSettled(Id) && _waited < IconWaitFrames)
            {
                _waited++;
                return;
            }

            _picture = own && assets.Composed(UiComposedId.TrophyIcon(Id)) is not null ? UiTrophyPicture.Icon : UiTrophyPicture.Emblem;
        }

        int hold = CatchUp ? CatchUpHoldFrames : waiting ? QueuedHoldFrames : HoldFrames;
        if (_in < InFrames)
        {
            _in++;
        }
        else if (_out == 0 && _held < hold)
        {
            _held++;
        }
        else
        {
            _out++;
        }
    }

    /// <summary>The box at rest (the picture, the longer of the two lines and the padding), for the tests and the layout of anything that must avoid it.</summary>
    public RectF Box(UiPainter p) => Layout(p).Box;

    // the name at the largest of its scales that keeps the box right of LeftLimit; a name too long even for the smallest loses its end
    private (RectF Box, double Scale, string Name) Layout(UiPainter p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (_layout is { } known && known.Language == p.Assets.Language)
        {
            return (known.Box, known.Scale, known.Name);
        }

        float fixedWidth = Border + PlaceSize + Gap + PadRight;
        float room = Right - LeftLimit - fixedWidth;
        string name = Name.Length > MaxNameLength ? Name[..MaxNameLength] : Name;
        double scale = NameScales[0];
        bool Fits(float width) => UiPrompts.TextLines(p, name, scale) <= 1 && UiPrompts.TextWidth(p, name, scale) <= width;

        // a name wider than a short title is drawn one size smaller, so that the box stays a notice and not a banner; the smallest size is only for a name
        // that would otherwise push the box past its left limit
        if (!Fits(ComfortWidth))
        {
            scale = NameScales[1];
        }

        for (int i = 2; i < NameScales.Length && !Fits(room); i++)
        {
            scale = NameScales[i];
        }

        while (name.Length > 1 && !Fits(room))
        {
            name = name[..^1].TrimEnd();
        }

        float nameWidth = UiPrompts.TextWidth(p, name, scale);

        float words = MathF.Min(room, MathF.Max(nameWidth, UiPrompts.TextWidth(p, Heading, HeadingScale)));
        float width = MathF.Max(MinWidth, MathF.Ceiling(fixedWidth + words));
        var box = new RectF(Right - width, Top, width, Height);
        _layout = (p.Assets.Language, box, scale, name);
        return (box, scale, name);
    }

    internal void Draw(UiPainter p)
    {
        float a = Alpha;
        if (a <= 0f)
        {
            return;
        }

        (RectF box, double scale, string name) = Layout(p);

        // the way in: the box eases the last pixels to the left, in whole pixels (the picture is drawn pixel for pixel)
        float left = 1f - a;
        float dx = _in < InFrames ? MathF.Round(Slide * left * left) : 0f;
        var r = new RectF(box.X + dx, box.Y, box.W, box.H);
        p.FillRounded(r, 14f, 0.62f, 0.86f, 1f, 0.8f * a);
        p.FillRounded(new RectF(r.X + Border, r.Y + Border, r.W - (2f * Border), r.H - (2f * Border)), 12f, 0.03f, 0.07f, 0.16f, 0.9f * a, smooth: false);

        // the picture the notice took when it started, in the middle of its place: the icon, else the grade's emblem. An icon that can no longer be drawn (the
        // host took it away) gives way to the emblem for good
        const float Inset = Border + ((PlaceSize - IconSize) / 2f);
        var place = new RectF(r.X + Inset, r.Y + Inset, IconSize, IconSize);
        if (_picture == UiTrophyPicture.Icon && !Whole(p, UiComposedId.TrophyIcon(Id), place, a))
        {
            _picture = UiTrophyPicture.Emblem;
        }

        if (_picture == UiTrophyPicture.Emblem)
        {
            Whole(p, UiComposedId.TrophyEmblem(Grade), place, a);
        }

        float x = r.X + Border + PlaceSize + Gap;
        UiPrompts.DrawText(p, Heading, x, r.Y + HeadingCy, a, HeadingScale, colour: HeadingColour);
        UiPrompts.DrawText(p, name, x, r.Y + NameCy, a, scale);
    }

    // the whole of one of the host's pictures in the picture's place; false when the host has none under that id
    private static bool Whole(UiPainter p, int id, RectF place, float alpha) =>
        p.Assets.Composed(id) is { } picture && p.Composed(id, new RectF(0, 0, picture.Width, picture.Height), place, alpha);
}
