// DarkFantasyPost - URP post-processing preset for the "dark fantasy nostalgia" horse ride.
// Used at runtime (auto-created profile) and by Tools/Build Dark Fantasy Ride (saved profile asset).
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class DarkFantasyPost
{
    static T Get<T>(VolumeProfile p) where T : VolumeComponent
    {
        if (!p.TryGet(out T c)) c = p.Add<T>(true);
        c.active = true;
        return c;
    }

    /// <summary>Fills (or updates) a profile with the purple/teal-shadow, warm-highlight, grainy look.</summary>
    public static void Configure(VolumeProfile p)
    {
        var tm = Get<Tonemapping>(p);
        tm.mode.Override(TonemappingMode.None);                 // keep pixel-art colours as painted

        var bloom = Get<Bloom>(p);
        bloom.threshold.Override(0.72f);
        bloom.intensity.Override(0.9f);
        bloom.scatter.Override(0.72f);
        bloom.tint.Override(new Color(1f, 0.86f, 0.96f));
        bloom.highQualityFiltering.Override(false);

        var ca = Get<ColorAdjustments>(p);
        ca.postExposure.Override(0.12f);
        ca.contrast.Override(14f);
        ca.saturation.Override(0f);
        ca.colorFilter.Override(new Color(1f, 0.97f, 0.98f));

        var st = Get<SplitToning>(p);
        st.shadows.Override(new Color(0.40f, 0.42f, 0.58f));     // violet/teal-ish shadows
        st.highlights.Override(new Color(0.95f, 0.72f, 0.50f));  // warm highlights
        st.balance.Override(10f);

        var smh = Get<ShadowsMidtonesHighlights>(p);
        smh.shadows.Override(new Vector4(0.95f, 1.02f, 1.04f, 0f));   // teal-blue lift in the darks
        smh.midtones.Override(new Vector4(1.0f, 1.0f, 1.0f, 0f));
        smh.highlights.Override(new Vector4(1.08f, 1.0f, 0.92f, 0f));

        var vig = Get<Vignette>(p);
        vig.color.Override(new Color(0.03f, 0.0f, 0.07f));
        vig.intensity.Override(0.38f);
        vig.smoothness.Override(0.45f);
        vig.rounded.Override(false);

        var fg = Get<FilmGrain>(p);
        fg.type.Override(FilmGrainLookup.Medium3);
        fg.intensity.Override(0.18f);
        fg.response.Override(0.75f);

        var chr = Get<ChromaticAberration>(p);
        chr.intensity.Override(0f);              // no colour fringing: keep pixels crisp

        var mb = Get<MotionBlur>(p);
        mb.intensity.Override(0f);
    }

    public static VolumeProfile CreateRuntimeProfile()
    {
        var p = ScriptableObject.CreateInstance<VolumeProfile>();
        p.name = "DarkFantasyRideProfile (runtime)";
        Configure(p);
        return p;
    }
}
