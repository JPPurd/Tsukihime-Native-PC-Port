// The chime of the achievement notice (v0.1.9, the owner's pick): a soft pop, then two quick rising bell notes (C6, then G6 95 ms later, with a C7 shimmer over
// it), four short echoes, a five-tap smoothing, a fade over the last quarter second and a peak of -6 dBFS; about 1.1 s. It is made here, from that recipe, once
// per run and at the mixer's rate: no sound file is in the repository or the release, and nothing of any product's sound is in it. The recipe is the owner's
// pop_ding.py; a test compares these samples with its output (AchievementChimeTests), so a change here must keep to it. The reference's 0.3 s of silence before
// and after the sound belong to its .wav file only: the chime starts at once.
//
// Where it plays: on a mixer voice of its own (Voice), which nothing else plays on, at the gain the story's sound effects play at (Config > Sound, "Sound
// Effects": volume byte 0xE, AppServices.SoundEffectGain) and 6 dB below it. It never takes a voice of the story, the music or the menus, and never lowers or
// stops one.
using Tsuki.Engine.Contracts;
using Tsuki.Media.Mixer;

namespace Tsuki.App.Play;

/// <summary>The achievement notice's chime: its samples, made at run time from the owner's recipe, and where and how loud it plays.</summary>
public static class AchievementChime
{
    /// <summary>
    /// The mixer voice the chime plays on (0x2B): below the system sounds' 0x2C..0x2F and above the right halves of the system sounds (26..29); no stream, system
    /// sound or movie of the port plays there (MixerVoices).
    /// </summary>
    public const int Voice = 0x2B;

    /// <summary>The chime's gain against a sound effect of the story at the same setting: 6 dB below (a factor of about 0.501).</summary>
    public static readonly float Attenuation = MathF.Pow(10f, -6f / 20f);

    /// <summary>The fewest steps between two chimes (1.5 s at 60 Hz): a run of notices never stacks its chimes.</summary>
    public const int MinimumGapFrames = 90;

    /// <summary>The length of the sound in seconds (the recipe's <c>total</c>).</summary>
    public const double Seconds = 1.10;

    private static readonly Lazy<AudioClip> Made = new(() => new AudioClip(AudioBank.Vag, -1, 1, AudioMixer.SampleRate, Render(AudioMixer.SampleRate), null), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The chime as a mono clip at the mixer's rate, made the first time it is asked for (about 13 ms in a debug build; the runtime asks on a worker as soon as a
    /// notice with the sound on is put in line) and kept for the run. Its bank and entry (vag, -1) are no entry of the game's data: they only name it in a failure.
    /// </summary>
    public static AudioClip Clip => Made.Value;

    /// <summary>True once <see cref="Clip"/> has been made.</summary>
    public static bool Ready => Made.IsValueCreated;

    /// <summary>The chime's samples at <paramref name="sampleRate"/> Hz, mono, peak -6 dBFS: the recipe of pop_ding.py, step by step, in double precision.</summary>
    public static float[] Render(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        int sr = sampleRate;
        var output = new double[(int)(sr * Seconds)];

        // the soft pop: a 50 ms blip rising from 520 Hz, 2 ms attack, under the first note
        var pop = new double[(int)(sr * 0.05)];
        for (int i = 0; i < pop.Length; i++)
        {
            double t = i / (double)sr;
            pop[i] = 0.35 * Math.Sin(2 * Math.PI * (520 + (900 * t / 0.05)) * t) * Math.Exp(-60 * t) * Math.Min(1.0, t / 0.002);
        }

        Place(output, pop, (int)(sr * 0.0));
        Place(output, Bell(sr, 1046.50, 0.9, 0.55, 7.0), (int)(sr * 0.010));       // C6
        Place(output, Bell(sr, 1567.98, 1.0, 0.62, 7.0), (int)(sr * 0.105));       // G6, a fifth up, 95 ms later
        Place(output, Bell(sr, 2093.00, 0.8, 0.22, 9.0), (int)(sr * 0.110));       // C7, the shimmer on top

        // the airy tail: four quiet echoes of the dry sound
        double[] dry = (double[])output.Clone();
        foreach ((double delay, double gain) in new[] { (0.061, 0.22), (0.113, 0.15), (0.173, 0.10), (0.241, 0.06) })
        {
            int k = (int)(sr * delay);
            for (int i = k; i < output.Length; i++)
            {
                output[i] += gain * dry[i - k];
            }
        }

        // a gentle low-pass: the mean of five samples around each (numpy's convolve, mode "same": the samples beyond either end count as 0)
        var smooth = new double[output.Length];
        for (int i = 0; i < output.Length; i++)
        {
            double sum = 0;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(output.Length - 1, i + 2); j++)
            {
                sum += output[j] * 0.2;
            }

            smooth[i] = sum;
        }

        // the fade: the last quarter second goes from 1 to 0 squared
        int n = (int)(sr * 0.25);
        for (int j = 0; j < n; j++)
        {
            double f = 1.0 - (j / (double)(n - 1));
            smooth[smooth.Length - n + j] *= f * f;
        }

        // no offset, and the peak at -6 dBFS
        double mean = 0;
        foreach (double v in smooth)
        {
            mean += v;
        }

        mean /= smooth.Length;
        double peak = 0;
        for (int i = 0; i < smooth.Length; i++)
        {
            smooth[i] -= mean;
            peak = Math.Max(peak, Math.Abs(smooth[i]));
        }

        double scale = Math.Pow(10, -6 / 20.0) / peak;
        var samples = new float[smooth.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(smooth[i] * scale);
        }

        return samples;
    }

    // a bell: four partials (1, 2, 3.01 and 4.2 times the note, each fainter and shorter than the last), a 4 ms attack so that it does not click
    private static double[] Bell(int sr, double freq, double seconds, double amp, double decay)
    {
        ReadOnlySpan<(double Mult, double Gain)> partials = [(1.0, 1.0), (2.0, 0.28), (3.01, 0.12), (4.2, 0.05)];
        var bell = new double[(int)(sr * seconds)];
        foreach ((double mult, double gain) in partials)
        {
            double fall = decay * Math.Pow(mult, 0.7);
            double w = 2 * Math.PI * freq * mult;
            for (int i = 0; i < bell.Length; i++)
            {
                double t = i / (double)sr;
                bell[i] += gain * Math.Sin(w * t) * Math.Exp(-fall * t);
            }
        }

        for (int i = 0; i < bell.Length; i++)
        {
            double t = i / (double)sr;
            bell[i] = amp * bell[i] * Math.Min(1.0, t / 0.004);
        }

        return bell;
    }

    // adds a part into the sound from sample index at on; what runs past the end is cut off
    private static void Place(double[] buffer, double[] part, int at)
    {
        int n = Math.Min(part.Length, Math.Max(0, buffer.Length - at));
        for (int i = 0; i < n; i++)
        {
            buffer[at + i] += part[i];
        }
    }
}
