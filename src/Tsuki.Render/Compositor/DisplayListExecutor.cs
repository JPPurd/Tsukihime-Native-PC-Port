// The display-list executor (ARCHITECTURE 3.2, 6.1, 6.3; docs/formats/compositing.md 6): runs the
// commands of one DisplayList in order on the GPU compositor. Textured quads resolve their TextureRef
// through an ITextureSource (the texture cache) or, for TextureKind.Capture, to a capture surface.
using Tsuki.Engine.Contracts;

namespace Tsuki.Render.Compositor;

/// <summary>Resolves the textures a display list names.</summary>
public interface ITextureSource
{
    /// <summary>
    /// The GL texture of <paramref name="key"/> and its transition parameters (texture +0xF0..+0xFC,
    /// compositing 2.2), or false when it is not resident.
    /// </summary>
    bool TryGetTexture(in TextureRef key, out uint texture, out TransitionParams? transition);
}

/// <summary>What one <see cref="DisplayListExecutor.Execute"/> did.</summary>
/// <param name="Commands">Commands read.</param>
/// <param name="Draws">Quads drawn.</param>
/// <param name="Culled">Quads skipped for vertex alpha 0.</param>
/// <param name="Captures">Capture copies.</param>
/// <param name="MissingTextures">Textured quads skipped because the texture was not resident.</param>
/// <param name="Blits">Backdrop copies.</param>
/// <param name="BlitPixels">Pixels copied into the backdrop.</param>
public readonly record struct ExecuteStats(int Commands, int Draws, int Culled, int Captures, int MissingTextures, int Blits, long BlitPixels);

/// <summary>Executes <see cref="DisplayList"/>s on a <see cref="GpuCompositor"/>.</summary>
/// <param name="compositor">The GPU compositor.</param>
/// <param name="textures">Texture resolver (normally the texture cache).</param>
public sealed class DisplayListExecutor(GpuCompositor compositor, ITextureSource textures)
{
    private readonly Vertex[] _quad = new Vertex[4];

    /// <summary>The compositor.</summary>
    public GpuCompositor Compositor { get; } = compositor ?? throw new ArgumentNullException(nameof(compositor));

    /// <summary>The texture resolver.</summary>
    public ITextureSource Textures { get; } = textures ?? throw new ArgumentNullException(nameof(textures));

    /// <summary>
    /// Runs every command of <paramref name="list"/> in order. The targets are not cleared first: the frame
    /// still holds the previous frame, which a transition start captures (compositing 14 open question 2).
    /// The port's overlays are no part of that frame (v0.1.9): where a list's overlays begin (<see cref="DrawKind.OverlaysBegin"/>) the frame is copied
    /// aside, and when the next list reads the previous frame at its head (<see cref="ReadsPreviousFrame"/>) that copy is put back first, so that its alpha
    /// fill and captures see the frame they would have seen had the toast or the notice never been drawn. A list after one without overlays runs as it
    /// always did.
    /// </summary>
    public ExecuteStats Execute(DisplayList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        Compositor.BeginFrame(restoreCleanFrame: Compositor.CleanFrame is not null && ReadsPreviousFrame(list.Commands));
        int missing = 0;
        foreach (ref readonly DrawCmd cmd in list.Commands)
        {
            switch (cmd.Kind)
            {
                case DrawKind.TexturedQuad:
                    if (!Resolve(cmd.Texture, out uint tex, out TransitionParams? tp))
                    {
                        missing++;
                        break;
                    }

                    Draw(cmd, tex, tp);
                    break;
                case DrawKind.FillQuad:
                    Draw(cmd, 0, null);
                    break;
                case DrawKind.Capture:
                    Compositor.Capture(cmd.Target, cmd.Texture.A);
                    break;
                case DrawKind.Clear:
                {
                    uint fill = cmd.V0.Rgba;
                    Compositor.Clear(cmd.Target, (byte)fill, (byte)(fill >> 8), (byte)(fill >> 16), (byte)(fill >> 24));
                    break;
                }

                case DrawKind.OverlaysBegin:
                    Compositor.KeepCleanFrame();
                    break;
                default:
                    throw new InvalidOperationException($"unknown display-list command kind {cmd.Kind}");
            }
        }

        CompositorCounters c = Compositor.Counters;
        return new ExecuteStats(list.Count, c.Draws, c.Culled, c.Captures, missing, c.Blits, c.BlitPixels);
    }

    /// <summary>
    /// True when <paramref name="commands"/> copy target 0 into a capture surface before they draw a picture into it or clear it: the list reads the frame the
    /// previous list left (the transition start of the stage: the alpha fill of effect 56, then the captures into surfaces 0 and 15). Fills before the capture
    /// do not end the head (that alpha fill is one); commands on the other targets are not looked at.
    /// </summary>
    public static bool ReadsPreviousFrame(ReadOnlySpan<DrawCmd> commands)
    {
        foreach (ref readonly DrawCmd c in commands)
        {
            if (c.Target != 0)
            {
                continue;
            }

            switch (c.Kind)
            {
                case DrawKind.Capture:
                    return true;
                case DrawKind.FillQuad:
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private bool Resolve(in TextureRef key, out uint texture, out TransitionParams? transition)
    {
        if (key.Kind == TextureKind.Capture)
        {
            texture = Compositor.CaptureSurface(key.A).Texture;
            transition = null;
            return true;
        }

        return Textures.TryGetTexture(key, out texture, out transition) && texture != 0;
    }

    private void Draw(in DrawCmd cmd, uint texture, TransitionParams? transition)
    {
        _quad[0] = cmd.V0;
        _quad[1] = cmd.V1;
        _quad[2] = cmd.V2;
        _quad[3] = cmd.V3;
        Compositor.DrawQuad(cmd.Target, texture, _quad, cmd.EffectWord, cmd.BlendState, transition, cmd.Clip, cmd.Fx);
    }
}
