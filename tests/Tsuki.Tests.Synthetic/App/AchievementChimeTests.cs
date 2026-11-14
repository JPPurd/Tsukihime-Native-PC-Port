using Tsuki.App.Play;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Sound;
using Tsuki.Engine.Trophies;
using Tsuki.Media.Mixer;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// v0.1.9, D7: the achievement notice's chime. It is made at run time from the owner's recipe (pop_ding.py) and must sound like the recipe's own output; it plays
/// as a notice comes in, only while Achievement Notify and Achievement Sound are both on, on a mixer voice of its own at the player's sound-effect volume
/// (Config > Sound, "Sound Effects") and 6 dB below it, never taking or lowering a voice or the music; it is silent in skip and fast-forward, while the sound
/// effects are muted, in a muted run and in a run that writes pictures, and plays at most once in 1.5 s. The log says "achievement chime" each time.
/// </summary>
public sealed class AchievementChimeTests(ITestOutputHelper output)
{
    // The owner's reference: pop_ding.py rendered at 48 kHz (its achievement_chime.wav, 16-bit, the 0.3 s of silence before and after the sound left out), every
    // 120th sample of the 52 800, and the RMS of the whole sound (-22.955 dBFS; the recipe in double precision gives the same to 0.001 dB)
    private const int ReferenceStride = 120;
    private const int ReferenceLength = 52_800;
    private const double ReferenceRmsDb = -22.955;

    private static readonly short[] Reference =
    [
        3, 2368, 1202, -1747, 11, -141, 5764, -9948, -140, 4434, -4129, 5225, -5263, -1997, 7218, -8190, 2185, 656, -3045, 6857,
        -6282, 73, 3919, -5443, 4035, -3396, -267, 6521, -5311, 1846, 1963, -5037, 5705, -3694, -633, 5464, -5670, 3009, -741, -2841,
        5430, -4761, 1237, -1346, -13926, -2452, -7729, -6606, 1393, -305, 8452, 5809, 3976, 11399, -20, -1065, -7084, -10059, -549, -6052,
        -3123, 2185, 914, 7522, 4455, 3188, 8766, -1344, -1584, -4510, -5195, 360, -5459, -3777, 1821, 135, 6315, 2909, 1887, 6428,
        -2924, -1758, -2577, -4868, 716, -4598, -2159, 2968, 980, 5840, 2784, 271, 2645, -5082, -2708, -2010, -3902, 428, -2098, 483,
        4788, 1384, 4438, 1077, -591, 1683, -4380, -1836, -1792, -3023, 1208, -1432, 1402, 4545, 992, 2768, -681, -2349, 444, -3711,
        -1632, -1080, -1570, 2326, 232, 1425, 3522, 420, 1581, -863, -2130, 205, -2962, -1288, -429, -912, 2166, 586, 1252, 2924,
        379, 1285, -448, -1845, -299, -2634, -1267, -320, -785, 1885, 851, 1075, 2263, -19, 779, -648, -1822, -385, -1962, -829,
        124, -355, 1737, 886, 816, 1636, -333, 244, -685, -1583, -318, -1426, -487, 448, -51, 1483, 743, 542, 1180, -466,
        -90, -693, -1355, -198, -948, -221, 603, 113, 1248, 606, 282, 758, -556, -253, -590, -1101, -92, -588, -25, 668,
        174, 987, 457, 102, 466, -590, -350, -482, -846, 24, -317, 88, 657, 189, 752, 306, -50, 254, -558, -358,
        -364, -632, 107, -123, 154, 600, 161, 546, 189, -148, 106, -510, -328, -243, -442, 164, 0, 176, 521, 118,
        371, 91, -205, 20, -437, -277, -145, -293, 198, 74, 169, 429, 70, 234, 21, -232, -28, -357, -215, -62,
        -181, 207, 111, 147, 340, 25, 130, -23, -232, -47, -280, -156, 0, -98, 201, 121, 115, 258, -10, 54,
        -50, -216, -48, -207, -104, 41, -43, 183, 115, 82, 186, -37, 4, -61, -190, -39, -146, -62, 68, -8,
        158, 99, 51, 128, -54, -26, -61, -159, -26, -95, -30, 80, 11, 130, 80, 24, 83, -63, -42, -54,
        -127, -12, -55, -7, 83, 19, 103, 60, 3, 49, -65, -48, -44, -98, 0, -27, 6, 79, 20, 77,
        41, -11, 24, -58, -43, -29, -63, 8, -5, 11, 57, 14, 42, 19, -16, 6, -39, -29, -14, -33,
        10, 3, 10, 35, 11, 19, 4, -8, -7, -20, -14, -11, -10, 5, 5, 10, 14, 6, 8, 0,
        -7, -5, -11, -7, -4, -3, 4, 4, 5, 6, 2, 3, -2, -4, -3, -5, -2, -1, 0, 2,
        1, 2, 2, 0, 0, -1, -2, -1, -2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double ma = a.Average();
        double mb = b.Average();
        double sab = 0;
        double saa = 0;
        double sbb = 0;
        for (int i = 0; i < a.Count; i++)
        {
            sab += (a[i] - ma) * (b[i] - mb);
            saa += (a[i] - ma) * (a[i] - ma);
            sbb += (b[i] - mb) * (b[i] - mb);
        }

        return sab / Math.Sqrt(saa * sbb);
    }

    private static double RmsDb(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float v in samples)
        {
            sum += (double)v * v;
        }

        return 20 * Math.Log10(Math.Sqrt(sum / samples.Length));
    }

    [Fact]
    public void TheChimeMadeAtRunTimeIsTheOwnersRecipe()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        float[] chime = AchievementChime.Render(48_000);
        output.WriteLine($"made in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
        Assert.Equal(ReferenceLength, chime.Length);                               // 1.1 s, and no silence in front: it starts with the notice
        Assert.Equal(ReferenceLength / ReferenceStride, Reference.Length);
        double[] mine = [.. Enumerable.Range(0, Reference.Length).Select(i => (double)chime[i * ReferenceStride])];
        double[] theirs = [.. Reference.Select(v => v / 32767.0)];
        double correlation = Correlation(mine, theirs);
        Assert.True(correlation > 0.99, $"the chime's samples correlate {correlation:F5} with the reference");
        double rms = RmsDb(chime);
        output.WriteLine($"correlation {correlation:F6}, RMS {rms:F3} dBFS");
        Assert.True(Math.Abs(rms - ReferenceRmsDb) < 0.5, $"the chime's RMS is {rms:F3} dBFS, the reference's {ReferenceRmsDb} dBFS");
        Assert.Equal(-6.0, 20 * Math.Log10(chime.Max(v => Math.Abs((double)v))), 3);                     // peak -6 dBFS

        // and to the sample: each sampled value is the reference's 16 bits (the .wav writer truncates), at most one step apart
        for (int i = 0; i < Reference.Length; i++)
        {
            Assert.InRange(Math.Truncate(chime[i * ReferenceStride] * 32767.0) - Reference[i], -1.0, 1.0);
        }

        // the clip the mixer plays: made once, at the mixer's rate, mono, these samples
        AudioClip clip = AchievementChime.Clip;
        Assert.Same(clip, AchievementChime.Clip);
        Assert.True(AchievementChime.Ready);
        Assert.Equal((1, AudioMixer.SampleRate, ReferenceLength, (AudioLoop?)null), (clip.Channels, clip.SampleRate, clip.FrameCount, clip.Loop));
        Assert.True(clip.Samples.Span.SequenceEqual(chime), "the clip is not the recipe at the mixer's rate");
        Assert.Equal(48_000, AudioMixer.SampleRate);
    }

    /// <summary>The services over the hand-made game with the runtime, a UI and a real mixer pulled by the test, the port's switches as the player set them.</summary>
    private sealed class Rig
    {
        private long _n;

        public Rig(bool notice = true, bool sound = true, bool pictureRun = false)
        {
            App = new AppRig(Story);
            App.Services.Port = new PreviewPortSettings(new PreviewServices(Language.En)) { TrophyNotice = notice, TrophySound = sound };
            Ui = new UiRoot(UiTestKit.Data(), App.Services);
            Runtime = new AppRuntime(App.Services, Ui, null, null) { Mixer = Mixer, PictureRun = pictureRun };
            Runtime.Logged += Log.Add;
        }

        public AppRig App { get; }

        public UiRoot Ui { get; }

        public AppRuntime Runtime { get; }

        public AudioMixer Mixer { get; } = new();

        public List<string> Log { get; } = [];

        public IEnumerable<string> Chimes => Log.Where(l => l.Contains("achievement chime", StringComparison.Ordinal));

        public void Step(ShellActions held = ShellActions.None)
        {
            var list = new DisplayList(2048);
            Runtime.Step(new InputFrame { Frame = ++_n, ShellHeld = held }, _n * 16_666_667L, list);
        }

        public void Run(int frames, ShellActions held = ShellActions.None)
        {
            for (int i = 0; i < frames; i++)
            {
                Step(held);
            }
        }

        /// <summary>The story's first page on show.</summary>
        public void Play()
        {
            App.Services.StartScene(100);
            for (int i = 0; i < 400 && App.Text != "alpha line"; i++)
            {
                Step();
            }

            Assert.Equal("alpha line", App.Text);
        }

        /// <summary>Renders <paramref name="frames"/> frames of the mix (interleaved stereo).</summary>
        public float[] Render(int frames)
        {
            var buffer = new float[frames * 2];
            Mixer.Render(buffer);
            return buffer;
        }
    }

    private static readonly float Below = MathF.Pow(10f, -6f / 20f);

    // a story of many pages: a skip or a fast-forward of a few steps never reaches its end
    private static readonly string Story = "_WKST(F822,eq,1);" + string.Concat(Enumerable.Repeat("_WKST(F823,eq,0);_WNTY(1);_ZM00101($000002);", 400)) + "_END_();";

    [Fact]
    public void TheChimeSoundsAsTheNoticeComesInOnAVoiceOfItsOwnAtTheSoundEffectVolumeSixDecibelsBelowIt()
    {
        var rig = new Rig();
        Assert.Equal(AudioStreams.FirstSystemVoice - 1, AchievementChime.Voice);               // 0x2B: below the system sounds, above their right halves (26..29)
        Assert.Equal(0.5012f, AchievementChime.Attenuation, 4);

        // the notice waits for its icon: no chime while it waits, the chime with its first frame
        var held = new TaskCompletionSource<string>();
        Func<string>? work = null;
        rig.Runtime.TrophyIconMaker = _ => null;
        rig.Runtime.TrophyIconStarter = w =>
        {
            work = w;
            return held.Task;
        };
        rig.Runtime.ShowTrophy(5);
        rig.Run(10);
        Assert.Empty(rig.Chimes);
        Assert.Equal(0, rig.Runtime.ChimesPlayed);
        rig.Render(800);
        Assert.False(rig.Mixer.IsPlaying(AchievementChime.Voice));
        held.SetResult(work!());
        rig.Step();
        UiTrophyNotice notice = Assert.IsType<UiTrophyNotice>(rig.Ui.TrophyNotice);
        Assert.Equal(1f / UiTrophyNotice.FadeInFrames, notice.Alpha);
        Assert.Equal(1, rig.Runtime.ChimesPlayed);

        // the player's sound-effect volume (Config > Sound, "Sound Effects": 40 of 100 in a fresh profile): the gain the sound model gives an effect a script
        // plays at full volume (_SEPL with `017:100), 6 dB below it
        float gain = EffectGain(rig) * Below;
        Assert.Equal(0.4f, EffectGain(rig), 5);
        Assert.Equal(EffectGain(rig), rig.App.Services.SoundEffectGain);
        Assert.Equal("achievement chime for achievement 5 (gain 0.200)", Assert.Single(rig.Chimes));

        // what the mixer plays: the chime alone, centred, at that gain, on its voice
        float[] mix = rig.Render(AchievementChime.Clip.FrameCount + 100);
        ReadOnlySpan<float> chime = AchievementChime.Clip.Samples.Span;
        for (int i = 0; i < chime.Length; i += 37)
        {
            Assert.Equal(gain * chime[i], mix[2 * i], 6);
            Assert.Equal(gain * chime[i], mix[(2 * i) + 1], 6);
        }

        Assert.All(mix[(2 * chime.Length)..], v => Assert.Equal(0f, v));            // and it ends: 1.1 s
        Assert.False(rig.Mixer.IsPlaying(AchievementChime.Voice));

        // a louder setting plays it louder: it follows the player's volume
        rig.App.Services.Config.SfxVolume = 60;
        rig.App.Services.ConfigChanged(ConfigChange.Setting);
        Assert.Equal(0.6f, rig.App.Services.SoundEffectGain, 5);
    }

    // the gain the story's sound model gives a sound effect that a script plays at full volume, with the rig's Config as it is now (SoundModel.SeGain)
    private static float EffectGain(Rig rig)
    {
        var model = new SoundModel(SoundConfig.FromBlock(rig.App.Services.Config), seLookup: (name, _) => name == "MOON_SE00001" ? 1 : null);
        _ = model.Run("_SEPL(3,0,MOON_SE00001,,`017:100);");
        return Assert.Single(model.TakeCommands(), c => c.Op == AudioOp.Play).Value0;
    }

    // a notice of achievement id once the last notice is gone and the last chime is 1.5 s past, stepped until it starts its way in; the log holds only what it says
    private static void NextNotice(Rig rig, int id)
    {
        rig.Run(AchievementChime.MinimumGapFrames + 200);
        Assert.Null(rig.Ui.TrophyNotice);
        rig.Log.Clear();
        rig.Runtime.ShowTrophy(id);
        for (int i = 0; i < 20 && rig.Ui.TrophyNotice is not { Started: true }; i++)
        {
            rig.Step();
        }

        Assert.True(rig.Ui.TrophyNotice is { Started: true } n && n.Id == id, $"the notice of achievement {id} did not come in");
    }

    [Fact]
    public void TheChimeFollowsTheSoundEffectVolumeAndNotTheVolumeOfTheMenusSounds()
    {
        // D7: "at the player's SE volume and 6 dB below other effects". The menus' sounds loud (100) and the effects quiet (20): the chime is quiet, 6 dB under
        // an effect of the story, wherever the menus' slider is
        var rig = new Rig();
        rig.App.Services.Config.SystemSeVolume = 100;
        rig.App.Services.Config.SfxVolume = 20;
        rig.App.Services.ConfigChanged(ConfigChange.Setting);
        Assert.Equal(0.2f, EffectGain(rig), 5);
        rig.Runtime.ShowTrophy(5);
        rig.Step();
        Assert.Equal("achievement chime for achievement 5 (gain 0.100)", Assert.Single(rig.Chimes));
        float[] mix = rig.Render(AchievementChime.Clip.FrameCount);
        ReadOnlySpan<float> chime = AchievementChime.Clip.Samples.Span;
        for (int i = 0; i < chime.Length; i += 53)
        {
            Assert.Equal(EffectGain(rig) * Below * chime[i], mix[2 * i], 6);
        }

        // the sound effects muted in Config > Sound (the menus' sounds still on): no chime, and the log says why
        rig.App.Services.Config.SfxMute = 1;
        rig.App.Services.ConfigChanged(ConfigChange.Setting);
        Assert.Equal(0f, rig.App.Services.SoundEffectGain);
        NextNotice(rig, 6);
        Assert.Equal("no achievement chime for achievement 6: the sound effects are muted", Assert.Single(rig.Chimes));
        Assert.Equal(1, rig.Runtime.ChimesPlayed);

        // the menus' sounds muted, the effects on again: the chime sounds, at the effects' volume
        rig.App.Services.Config.SfxMute = 0;
        rig.App.Services.Config.SystemSeMute = 1;
        rig.App.Services.ConfigChanged(ConfigChange.Setting);
        NextNotice(rig, 7);
        Assert.Equal("achievement chime for achievement 7 (gain 0.100)", Assert.Single(rig.Chimes));
        Assert.Equal(2, rig.Runtime.ChimesPlayed);

        // the effects' slider at 0 is as good as muted
        rig.App.Services.Config.SfxVolume = 0;
        rig.App.Services.ConfigChanged(ConfigChange.Setting);
        NextNotice(rig, 8);
        Assert.Equal("no achievement chime for achievement 8: the sound effects are muted", Assert.Single(rig.Chimes));
        Assert.Equal(2, rig.Runtime.ChimesPlayed);
    }

    [Fact]
    public void TheChimeNeverTakesOrLowersAVoiceOfTheStoryOrTheMusic()
    {
        // music on its stream, a voice on its own, both steady: the chime is added to them, they go on at their gains
        var rig = new Rig();
        rig.Runtime.TrophyIconMaker = null;
        var music = new AudioClip(AudioBank.Bgm, 5, 1, AudioMixer.SampleRate, Enumerable.Repeat(0.25f, AudioMixer.SampleRate * 3).ToArray(), null);
        var voice = new AudioClip(AudioBank.Voice, 9, 1, AudioMixer.SampleRate, Enumerable.Repeat(0.125f, AudioMixer.SampleRate * 3).ToArray(), null);
        rig.Mixer.Post(MixerCommand.Play(MixerVoices.Stream(AudioStreams.Bgm0), music, false, 1f, 0f));
        rig.Mixer.Post(MixerCommand.Play(MixerVoices.Stream(AudioStreams.FirstVoice), voice, false, 1f, 0f));
        rig.Render(800);
        long posted = rig.Mixer.CommandsPosted;
        rig.Runtime.ShowTrophy(5);
        rig.Step();
        Assert.Single(rig.Chimes);
        Assert.Equal(posted + 1, rig.Mixer.CommandsPosted);                        // one command: the chime's own play, nothing to any other voice
        float gain = rig.App.Services.SoundEffectGain * Below;
        float[] mix = rig.Render(AchievementChime.Clip.FrameCount);
        ReadOnlySpan<float> chime = AchievementChime.Clip.Samples.Span;
        for (int i = 0; i < chime.Length; i += 41)
        {
            Assert.Equal(0.375f + (gain * chime[i]), mix[2 * i], 5);
        }

        Assert.True(rig.Mixer.IsPlaying(MixerVoices.Stream(AudioStreams.Bgm0)) && rig.Mixer.IsPlaying(MixerVoices.Stream(AudioStreams.FirstVoice)), "the chime stopped the music or the voice");
        Assert.All(rig.Render(400), v => Assert.Equal(0.375f, v, 6));                  // the chime is over, the others are as they were
        Assert.NotEqual(AchievementChime.Voice, MixerVoices.Stream(AudioStreams.Bgm0));
        Assert.DoesNotContain(AchievementChime.Voice, Enumerable.Range(0, AudioStreams.Count).Select(MixerVoices.Stream));
        Assert.DoesNotContain(AchievementChime.Voice, Enumerable.Range(0, MixerVoices.SystemCount).SelectMany(k => new[] { MixerVoices.System(MixerVoices.FirstSystem + k), MixerVoices.SystemRight(MixerVoices.FirstSystem + k) }));
        Assert.DoesNotContain(AchievementChime.Voice, Enumerable.Range(0, MixerVoices.MovieCount).Select(MixerVoices.Movie));
    }

    [Fact]
    public void NoChimeUnlessTheNoticeAndTheSoundAreBothOnAndNoneInARunThatWritesPictures()
    {
        foreach ((bool notice, bool sound, bool pictures, bool chimes) in new[] { (true, true, false, true), (true, false, false, false), (false, true, false, false), (false, false, false, false), (true, true, true, false) })
        {
            var rig = new Rig(notice, sound, pictures);
            rig.Runtime.ShowTrophy(5);
            rig.Run(5);
            rig.Render(800);
            string what = $"notice {notice}, sound {sound}, picture run {pictures}";
            Assert.Equal(chimes ? 1 : 0, rig.Runtime.ChimesPlayed);
            Assert.True(rig.Mixer.IsPlaying(AchievementChime.Voice) == chimes, what);
            Assert.Equal(chimes ? 1 : 0, rig.Chimes.Count());                     // a switch that is off says nothing about a chime
            Assert.Equal(notice && !pictures, rig.Ui.TrophyNotice is not null);
        }
    }

    [Fact]
    public void NoChimeInSkipOrFastForwardOrWhileTheSoundEffectsAreMuted()
    {
        // the skip latch of the story
        var skipping = new Rig();
        skipping.Play();
        skipping.App.Host.SetSkipLatch(true);
        skipping.Runtime.ShowTrophy(5);
        skipping.Step();
        Assert.True(skipping.Ui.TrophyNotice is { Started: true });                // the notice comes in all the same
        Assert.Equal(0, skipping.Runtime.ChimesPlayed);
        Assert.Equal("no achievement chime for achievement 5: skip or fast-forward", Assert.Single(skipping.Chimes));

        // the fast-forward key held
        var forward = new Rig();
        forward.Play();
        forward.Run(3, ShellActions.FastForward);
        Assert.True(forward.App.Host.Mode.SkipActive != 0, "the held key does not fast-forward the story");
        forward.Runtime.ShowTrophy(5);
        forward.Step(ShellActions.FastForward);
        Assert.Equal(0, forward.Runtime.ChimesPlayed);
        Assert.Equal("no achievement chime for achievement 5: skip or fast-forward", Assert.Single(forward.Chimes));
        forward.Render(800);
        Assert.False(forward.Mixer.IsPlaying(AchievementChime.Voice));

        // the sound effects muted in Config > Sound (or at 0): their volume byte is 0
        var muted = new Rig();
        muted.App.Services.Config.SfxMute = 1;
        muted.App.Services.ConfigChanged(ConfigChange.Setting);
        Assert.Equal(0f, muted.App.Services.SoundEffectGain);
        muted.Runtime.ShowTrophy(5);
        muted.Step();
        Assert.Equal(0, muted.Runtime.ChimesPlayed);
        Assert.Equal("no achievement chime for achievement 5: the sound effects are muted", Assert.Single(muted.Chimes));

        // a reading without either: it sounds
        var reading = new Rig();
        reading.Play();
        reading.Runtime.ShowTrophy(5);
        reading.Step();
        Assert.Equal(1, reading.Runtime.ChimesPlayed);
    }

    [Fact]
    public void TheChimeIsMadeOnAWorkerOnceWhenTheFirstNoticeWithTheSoundOnIsPutInLine()
    {
        // its few milliseconds are not spent in the frame the notice comes in: the samples are asked for as the notice is put in line, on a worker, once a run
        var rig = new Rig();
        var asked = new List<long>();
        rig.Runtime.ChimeStarter = work =>
        {
            asked.Add(rig.Runtime.Frame);
            work();
        };
        rig.Run(3);
        Assert.Empty(asked);                                                      // nothing at start
        rig.Runtime.ShowTrophy(5);
        Assert.Equal([3L], asked);                                                // with the notice, before it comes in
        Assert.Equal(0, rig.Runtime.ChimesPlayed);
        rig.Runtime.ShowTrophy(6);
        rig.Run(500);
        Assert.Equal(2, rig.Runtime.ChimesPlayed);
        Assert.Single(asked);                                                     // once
        Assert.True(AchievementChime.Ready);

        // the sound off: never
        var quiet = new Rig(sound: false);
        quiet.Runtime.ChimeStarter = _ => asked.Add(-1);
        quiet.Runtime.ShowTrophy(5);
        quiet.Run(5);
        Assert.Single(asked);
    }

    [Fact]
    public void AMutedRunSaysTheChimeAndPlaysNothing()
    {
        // --mute, a hidden window or no sound device: nothing is played into the mixer, the log says the chime was due
        var rig = new Rig();
        rig.Runtime.Muted = true;
        long posted = rig.Mixer.CommandsPosted;
        rig.Runtime.ShowTrophy(14);
        rig.Step();
        Assert.Equal("achievement chime for achievement 14 (gain 0.200; silent: this run is muted)", Assert.Single(rig.Chimes));
        Assert.Equal(posted, rig.Mixer.CommandsPosted);
        Assert.All(rig.Render(1600), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void AtMostOneChimeInOneAndAHalfSeconds()
    {
        // notices come in at least 1.5 s apart by themselves (the shortest, the catch-up's, every 92 steps; the catch-up chimes once anyway): the rule holds
        // for any that would not, a chime at most every 90 steps
        var rig = new Rig();
        rig.Runtime.ShowTrophy(5);
        rig.Step();
        Assert.Equal(1, rig.Runtime.ChimesPlayed);
        long first = rig.Runtime.Frame;

        // notices of another UI (no line of this one's): the start of each is told to the runtime as its own UI would tell it
        UiRoot other = UiTestKit.Root(new PreviewServices(Language.En));
        UiTrophyNotice Notice(int id, bool chime = true) => other.ShowTrophy(TrophyGrade.Bronze, "Achievement unlocked", "A Chapter", id, catchUp: !chime, chime: chime);
        rig.Run(10);
        rig.Runtime.OnTrophyNoticeStarted(Notice(1));
        Assert.Equal(1, rig.Runtime.ChimesPlayed);
        Assert.Contains("no achievement chime for achievement 1: less than 1.5 s after the last one", rig.Log);
        rig.Run((int)(first + AchievementChime.MinimumGapFrames - 1 - rig.Runtime.Frame));
        rig.Runtime.OnTrophyNoticeStarted(Notice(2));
        Assert.Equal(1, rig.Runtime.ChimesPlayed);                                 // 89 steps after it
        rig.Step();
        rig.Runtime.OnTrophyNoticeStarted(Notice(3));
        Assert.Equal(2, rig.Runtime.ChimesPlayed);                                 // 90 steps
        Assert.Equal(90, AchievementChime.MinimumGapFrames);
        Assert.Equal(2, rig.Log.Count(l => l.StartsWith("achievement chime", StringComparison.Ordinal)));

        // a notice that does not ask for the chime (the catch-up's after its first) never sounds it, and says nothing
        rig.Run(200);
        int lines = rig.Log.Count;
        rig.Runtime.OnTrophyNoticeStarted(Notice(4, chime: false));
        Assert.Equal((2, lines), (rig.Runtime.ChimesPlayed, rig.Log.Count));
    }
}
