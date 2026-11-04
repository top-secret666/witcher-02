// HorseRideRig — layered 2D parallax "first-person horse ride" (Witcher / dark fantasy nostalgia).
// Builds a Screen Space Overlay canvas at Play from textures in Resources/Ride/.
// Layers back-to-front: Sky, Castle, CliffsRight, Spires, CliffLeft, Path (CPU perspective road), Horse.
// Optional: T_Ride_Fog (drifting fog sheets between depths) + snow particles.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public class HorseRideRig : MonoBehaviour
{
    [System.Serializable]
    public class ZoomLayer
    {
        public string textureName = "T_Ride_Castle";
        public bool enabled = true;
        [Tooltip("Scale at the start of a cycle")] public float startScale = 1f;
        [Tooltip("Scale at the end of a cycle (forward motion)")] public float endScale = 1.1f;
        [Tooltip("Cycles per loop (1 = one zoom per loop)")] public int cyclesPerLoop = 1;
        [Tooltip("Pixel drift over one cycle (reference 1080x1920)")] public Vector2 drift = Vector2.zero;
        [Tooltip("Use two copies half a cycle apart and crossfade (seamless). Off = single copy, hard reset.")] public bool crossfade = true;
        [Range(0f, 0.5f)] public float fadeFraction = 0.25f;
        [Tooltip("How much of the camera bob this layer receives (0 far .. 1 near)")] public float bobAmount = 0.3f;
        [HideInInspector] public RawImage a, b;
    }

    [Header("Control (W forward / S backward)")]
    public bool requireInput = true;
    [Tooltip("Max forward playback speed (1 = reference video speed)")] public float forwardSpeed = 1f;
    [Tooltip("Max backward playback speed")] public float backwardSpeed = 0.6f;
    [Tooltip("Speed change per second when accelerating")] public float acceleration = 1.2f;
    [Tooltip("Speed change per second when stopping")] public float deceleration = 1.6f;
    [Header("Pixel look")]
    public FilterMode filter = FilterMode.Point;

    [Header("Loop")]
    public float loopDuration = 4f;
    [Tooltip("Vanishing point in normalized screen coords (0..1)")] public Vector2 vanishingPoint = new Vector2(0.46f, 0.42f);
    public Vector2 referenceResolution = new Vector2(1080, 1920);
    public Color grade = Color.white;

    [Header("Layers (back to front, excluding sky/path/horse)")]
    public List<ZoomLayer> layers = new List<ZoomLayer>
    {
        new ZoomLayer{ textureName="T_Ride_Castle",      startScale=1.00f, endScale=1.06f, drift=new Vector2(  6,  -4), crossfade=false, bobAmount=0.15f },
        new ZoomLayer{ textureName="T_Ride_CliffsRight", startScale=1.00f, endScale=1.25f, drift=new Vector2( 60, -40), bobAmount=0.35f },
        new ZoomLayer{ textureName="T_Ride_Spires",      startScale=1.00f, endScale=1.45f, drift=new Vector2(-70, -40), bobAmount=0.55f },
        new ZoomLayer{ textureName="T_Ride_CliffLeft",   startScale=1.00f, endScale=1.90f, drift=new Vector2(-200,-30), cyclesPerLoop=2, bobAmount=0.8f },
    };

    [Header("Sky")] public float skyBobAmount = 0.05f;

    [Header("Fog (procedural dithered pixel fog)")]
    public bool buildFog = true;
    public Color fogColor = new Color(0.78f, 0.72f, 0.68f, 1f);
    [Tooltip("Fog pixel grid (texture width in pixels)")] public int fogTexWidth = 240;
    [Tooltip("Aerial perspective: haze in front of castle / right cliffs / spires")] public float[] layerFogAlpha = { 0.55f, 0.38f, 0.26f, 0f };
    [Tooltip("Horizontal drift speed of the fog (uv per second)")] public float fogDrift = 0.008f;
    [Range(0, 1)] public float groundMistAlpha = 0.5f;
    [Tooltip("Old T_Ride_Fog texture sheets (legacy)")] public bool useLegacyFogTexture = false;
    [Range(0, 1)] public float fogAlpha = 0.35f;
    public float fogEndScale = 1.6f;

    [Header("Path (CPU perspective road)")]
    public bool buildPath = true;
    public int pathTexWidth = 240, pathTexHeight = 180;
    [Tooltip("Fraction of screen height the path strip covers from the bottom")] public float pathScreenFraction = 0.42f;
    [Tooltip("Horizon inside the strip (0 bottom .. 1 top)")] public float pathHorizon = 0.98f;
    public float cameraHeight = 1.0f;
    public float pathWorldWidth = 2.4f;
    [Tooltip("Path texture repeats every N world units")] public float pathTileLength = 6f;
    [Tooltip("Tiles travelled per loop (integer = seamless)")] public int pathTilesPerLoop = 2;
    [Tooltip("Path centre x offset toward the vanishing point (−0.5..0.5 of strip width at horizon)")] public float pathVanishX = 0f;
    public float pathFadeNear = 0.0f, pathFadeFar = 0.35f;
    [Tooltip("How far the road bends sideways at the horizon (fraction of strip width). Negative = bends left")] public float pathCurve = -0.32f;
    [Tooltip("Curve shape: 1 = linear, 2 = smooth arc, 3 = bends late")] public float pathCurvePower = 2.2f;
    public Color pathTint = new Color(0.85f, 0.85f, 0.9f, 1f);
    public float pathBobAmount = 0.9f;

    [Header("Horse (foreground)")]
    public bool buildHorse = true;
    [Tooltip("Horse height as fraction of screen height")] public float horseHeight = 0.44f;
    public float horseBobPx = 22f;
    [Range(0, 1)] public float horseBobPhase = 0.35f;
    public float horseNodDegrees = 1.6f;
    [Tooltip("Subtle ear twitch: brief extra wobble every N seconds")] public float earTwitchEvery = 2f;
    public float earTwitchDegrees = 1.2f;

    [Header("Camera bob (gallop)")]
    public float bobBeatsPerSecond = 2.5f;
    public float bobPx = 18f;
    [Tooltip(">1 = sharper downward impact")] public float impactSharpness = 2.2f;
    public float swayPx = 8f;
    public float rollDegrees = 0.6f;

    [Header("Smoke (procedural pixel puffs)")]
    public bool buildSmoke = true;
    [Tooltip("Emitters: x,y = position inside the layer (0..1), z = layer index (0 castle, 1 cliffs right, 2 spires/village, 3 cliff left), w = strength")]
    public Vector4[] smokeEmitters = { new Vector4(0.86f, 0.52f, 0, 1.2f), new Vector4(0.47f, 0.56f, 0, 0.7f), new Vector4(0.22f, 0.41f, 2, 0.6f), new Vector4(0.31f, 0.40f, 2, 0.6f), new Vector4(0.36f, 0.415f, 2, 0.5f) };
    public int puffsPerEmitter = 14;
    public Vector2 wind = new Vector2(0.9f, 1f);
    public float smokeRise = 70f, smokeLife = 5f, smokeSize = 10f;
    public Color smokeColor = new Color(0.62f, 0.58f, 0.56f, 0.75f);

    [Header("Snow")]
    public bool buildSnow = true;
    public int snowCount = 70;
    public float snowSpeed = 0.5f;
    public float snowSize = 6f;

    RectTransform _root, _world;
    RawImage _sky, _path, _horse;
    Texture2D _pathOut, _pathSrc;
    Color32[] _src, _dst;
    int _sw, _sh;
    readonly List<(RawImage img, Vector2 dir, float phase, float speed)> _snow = new List<(RawImage, Vector2, float, float)>();
    readonly List<(RawImage img, float phase)> _fog = new List<(RawImage, float)>();
    float _t, _bobT, _speed, _bobAmp;

    void Start() { Build(); }

    Texture2D Load(string n) { return Resources.Load<Texture2D>("Ride/" + n); }

    RawImage MakeImage(string name, Texture tex, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.GetComponent<RawImage>(); img.texture = tex; img.raycastTarget = false; img.color = grade;
        if (tex) tex.filterMode = filter;
        return img;
    }

    void SetPivotToVP(RectTransform rt)
    {
        // full-screen rect, pivot at vanishing point so scaling zooms toward it
        rt.pivot = vanishingPoint;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    public void Build()
    {
        var canvasGo = new GameObject("RideUI", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var c = canvasGo.GetComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 50;
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; cs.referenceResolution = referenceResolution; cs.matchWidthOrHeight = 1f;

        // black letterbox background
        var bg = MakeImage("Bg", Texture2D.whiteTexture, (RectTransform)canvasGo.transform); bg.color = Color.black;
        // 9:16 frame
        var frame = new GameObject("Frame9x16", typeof(RectTransform), typeof(RectMask2D));
        _root = (RectTransform)frame.transform; _root.SetParent(canvasGo.transform, false);
        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f); _root.sizeDelta = referenceResolution;
        var w = new GameObject("World", typeof(RectTransform)); _world = (RectTransform)w.transform; _world.SetParent(_root, false);
        _world.anchorMin = Vector2.zero; _world.anchorMax = Vector2.one; _world.offsetMin = _world.offsetMax = new Vector2(-40, -40) * -1f;
        _world.offsetMin = new Vector2(-40, -40); _world.offsetMax = new Vector2(40, 40); // overscan for bob

        var sky = Load("T_Ride_Sky");
        _sky = MakeImage("L1_Sky", sky, _world); SetPivotToVP(_sky.rectTransform);

        Texture2D fog = buildFog ? Load("T_Ride_Fog") : null;
        int li = 2;
        foreach (var L in layers)
        {
            if (!L.enabled) continue;
            var tex = Load(L.textureName);
            if (!tex) { Debug.LogWarning("HorseRideRig: missing Resources/Ride/" + L.textureName); continue; }
            L.a = MakeImage("L" + li + "_" + L.textureName + "_A", tex, _world); SetPivotToVP(L.a.rectTransform);
            if (L.crossfade) { L.b = MakeImage("L" + li + "_" + L.textureName + "_B", tex, _world); SetPivotToVP(L.b.rectTransform); }
            if (useLegacyFogTexture && fog && li == 3) AddFog(fog, 0f);
            if (useLegacyFogTexture && fog && li == 4) AddFog(fog, 0.5f);
            int fi = li - 2;
            if (buildFog && fi < layerFogAlpha.Length && layerFogAlpha[fi] > 0f)
                AddFogSheet("Haze_" + L.textureName, 0.60f - fi * 0.05f, 0.16f + fi * 0.03f, layerFogAlpha[fi], fogDrift * (1f + fi * 0.6f), fi * 37);
            li++;
        }

        if (buildSmoke) BuildSmoke();
        if (buildPath) BuildPath();
        if (buildFog && groundMistAlpha > 0f) AddFogSheet("GroundMist", 0.30f, 0.12f, groundMistAlpha, fogDrift * 3f, 911);

        if (buildSnow)
        {
            var dot = MakeDot();
            for (int i = 0; i < snowCount; i++)
            {
                var img = MakeImage("Snow" + i, dot, _world);
                var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = vanishingPoint; rt.sizeDelta = Vector2.one * snowSize;
                float ang = Random.value * Mathf.PI * 2f;
                _snow.Add((img, new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) * 1.4f), Random.value, Random.Range(0.6f, 1.4f)));
            }
        }

        if (buildHorse)
        {
            var htex = Load("T_Ride_Horse");
            if (htex)
            {
                _horse = MakeImage("L7_Horse", htex, _root); _horse.color = Color.white * grade.r + new Color(0, 0, 0, 1);
                var rt = _horse.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0.05f);
                float h = referenceResolution.y * horseHeight;
                rt.sizeDelta = new Vector2(h * htex.width / (float)htex.height, h);
            }
        }
    }

    readonly List<(RawImage img, float speed, float alpha)> _fogSheets = new List<(RawImage, float, float)>();
    static readonly int[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    // Builds a full-screen dithered pixel fog band: vertical centre/height in screen fractions (from bottom).
    void AddFogSheet(string name, float centerY, float height, float alpha, float speed, int seed)
    {
        int W = Mathf.Max(32, fogTexWidth), H = Mathf.RoundToInt(W * referenceResolution.y / referenceResolution.x);
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = filter };
        var px = new Color32[W * H];
        Color32 fc = fogColor;
        for (int y = 0; y < H; y++)
        {
            float fy = (float)y / H;
            float band = Mathf.Exp(-Mathf.Pow((fy - centerY) / Mathf.Max(0.01f, height), 2f));
            for (int x = 0; x < W; x++)
            {
                // tileable fbm along x (wraps every W)
                float a = (float)x / W * Mathf.PI * 2f;
                float n = 0f, amp = 0.55f, f = 1f;
                for (int o = 0; o < 4; o++)
                {
                    n += amp * Mathf.PerlinNoise(seed + Mathf.Cos(a) * 3f * f, seed * 0.37f + Mathf.Sin(a) * 3f * f + fy * 9f * f);
                    amp *= 0.5f; f *= 2f;
                }
                float d = Mathf.Clamp01(band * (0.35f + n * 1.1f));
                // 4 alpha levels, ordered (Bayer) dither between them => pixel-art fog
                float q = d * 4f + (Bayer4[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f - 0.5f;
                int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 4);
                px[y * W + x] = new Color32(fc.r, fc.g, fc.b, (byte)(lvl * 255 / 4));
            }
        }
        t.SetPixels32(px); t.Apply(false);
        var img = MakeImage(name, t, _world); img.color = new Color(1, 1, 1, alpha);
        _fogSheets.Add((img, speed, alpha));
    }

    class Puff { public RawImage img; public float phase, seed; public float strength; public ZoomLayer owner; public bool isB; }
    readonly List<Puff> _puffs = new List<Puff>();

    Texture2D MakePuffTex()
    {
        int n = 8; var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float r = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
            float d = Mathf.Clamp01(1.15f - r);
            float q = d * 2f + (Bayer4[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f - 0.5f;
            int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 2);
            t.SetPixel(x, y, new Color(1, 1, 1, lvl / 2f));
        }
        t.Apply(); return t;
    }

    void BuildSmoke()
    {
        var tex = MakePuffTex();
        var active = layers.FindAll(l => l.enabled && l.a);
        foreach (var e in smokeEmitters)
        {
            int li = Mathf.Clamp((int)e.z, 0, active.Count - 1); if (active.Count == 0) return;
            var L = active[li];
            foreach (var host in new[] { L.a, L.b })
            {
                if (!host) continue;
                for (int i = 0; i < puffsPerEmitter; i++)
                {
                    var img = MakeImage("Smoke", tex, host.rectTransform);
                    var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = new Vector2(e.x, e.y); rt.pivot = new Vector2(0.5f, 0.5f);
                    _puffs.Add(new Puff { img = img, phase = (float)i / puffsPerEmitter, seed = Random.value * 100f, strength = e.w, owner = L, isB = host == L.b });
                }
            }
        }
    }

    void UpdateSmoke()
    {
        float life = Mathf.Max(0.5f, smokeLife);
        foreach (var p in _puffs)
        {
            float a = Mathf.Repeat(Time.time / life + p.phase, 1f);          // 0 born .. 1 dissolved
            float wob = Mathf.Sin((Time.time + p.seed) * 1.3f) * 4f * a;
            var rt = p.img.rectTransform;
            rt.anchoredPosition = new Vector2((wind.x * smokeRise * a * a * 1.6f + wob), (wind.y * smokeRise * a)) * p.strength;
            float sz = smokeSize * p.strength * (0.6f + a * 2.4f);
            sz = Mathf.Round(sz / 4f) * 4f;                                    // snap growth to the pixel grid
            rt.sizeDelta = new Vector2(sz, sz);
            var host = p.isB ? p.owner.b : p.owner.a;
            float hostA = host ? host.color.a : 1f;
            var c = smokeColor; c.a *= Smooth(0f, 0.12f, a) * (1f - Smooth(0.45f, 1f, a)) * hostA;
            p.img.color = c;
        }
    }

    void AddFog(Texture2D fog, float phase)
    {
        var img = MakeImage("Fog", fog, _world); SetPivotToVP(img.rectTransform);
        _fog.Add((img, phase));
    }

    Texture2D MakeDot()
    {
        int n = 16; var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
            t.SetPixel(x, y, new Color(0.9f, 0.92f, 0.95f, Mathf.Clamp01(1f - d) * 0.9f));
        }
        t.Apply(); t.filterMode = filter; return t;
    }

    void BuildPath()
    {
        _pathSrc = Load("T_Ride_Path");
        if (!_pathSrc) { Debug.LogWarning("HorseRideRig: missing T_Ride_Path"); return; }
        try { _src = _pathSrc.GetPixels32(); }
        catch { Debug.LogError("HorseRideRig: T_Ride_Path must be Read/Write enabled (RideTextureImporter does this automatically)"); return; }
        _sw = _pathSrc.width; _sh = _pathSrc.height;
        _pathOut = new Texture2D(pathTexWidth, pathTexHeight, TextureFormat.RGBA32, false);
        _pathOut.wrapMode = TextureWrapMode.Clamp; _pathOut.filterMode = filter;
        _dst = new Color32[pathTexWidth * pathTexHeight];
        _path = MakeImage("L6_Path", _pathOut, _world);
        var rt = _path.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(1f, pathScreenFraction);
        rt.offsetMin = rt.offsetMax = Vector2.zero; rt.pivot = new Vector2(vanishingPoint.x, 0f);
        _path.color = Color.white;
        Debug.Log("HorseRideRig: CPU path active " + _sw + "x" + _sh);
    }

    Color32 Sample(float u, float v)
    {
        // bilinear, u clamped, v wrapped
        float x = Mathf.Clamp(u * _sw - 0.5f, 0, _sw - 1.001f);
        float y = (v - Mathf.Floor(v)) * _sh - 0.5f; if (y < 0) y += _sh;
        int x0 = (int)x, y0 = (int)y; int x1 = x0 + 1, y1 = (y0 + 1) % _sh;
        float fx = x - x0, fy = y - y0;
        Color32 a = _src[y0 * _sw + x0], b = _src[y0 * _sw + x1], c = _src[y1 * _sw + x0], d = _src[y1 * _sw + x1];
        return Color32.Lerp(Color32.Lerp(a, b, fx), Color32.Lerp(c, d, fx), fy);
    }

    void DrawPath(float travel)
    {
        int W = pathTexWidth, H = pathTexHeight;
        float hz = pathHorizon * H;
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            float dy = (hz - y) / H; // 0 at horizon, ~1 at bottom
            if (dy <= 0.002f) { for (int x = 0; x < W; x++) _dst[row + x] = new Color32(0, 0, 0, 0); continue; }
            float z = cameraHeight / dy;                 // world distance of this row
            float halfW = pathWorldWidth * 0.5f / z;     // half road width in strip-height units
            float vanish = Mathf.Lerp(pathVanishX, 0f, Mathf.Clamp01(dy)); // road bends toward VP at distance
            vanish += pathCurve * Mathf.Pow(1f - Mathf.Clamp01(dy), pathCurvePower); // curve: lateral offset grows with distance (depth-only => seamless both ways)
            float v = (z + travel) / pathTileLength;
            float far = Mathf.Clamp01(dy / Mathf.Max(0.001f, pathFadeFar));    // fade out toward horizon
            float aspect = (float)W / H;
            for (int x = 0; x < W; x++)
            {
                float sx = ((x + 0.5f) / W - vanishingPoint.x) * aspect - vanish * aspect;
                float u = sx / (halfW * 2f) + 0.5f;
                if (u < -0.02f || u > 1.02f) { _dst[row + x] = new Color32(0, 0, 0, 0); continue; }
                var col = Sample(Mathf.Clamp01(u), v);
                float edge = Mathf.Clamp01(Mathf.Min(u + 0.02f, 1.02f - u) * 25f);
                col.r = (byte)(col.r * pathTint.r); col.g = (byte)(col.g * pathTint.g); col.b = (byte)(col.b * pathTint.b);
                col.a = (byte)(col.a * far * edge);
                _dst[row + x] = col;
            }
        }
        _pathOut.SetPixels32(_dst); _pathOut.Apply(false);
    }

    static float Smooth(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3 - 2 * x); }

    void ApplyZoom(RawImage img, float p, ZoomLayer L, bool fade, Vector2 bob)
    {
        if (!img) return;
        float s = Mathf.Lerp(L.startScale, L.endScale, p * p * 0.35f + p * 0.65f); // accelerate slightly as it nears
        var rt = img.rectTransform; rt.localScale = new Vector3(s, s, 1);
        rt.anchoredPosition = L.drift * p + bob * L.bobAmount;
        var c = grade;
        if (fade) c.a = Smooth(0, L.fadeFraction, p) * (1f - Smooth(1f - L.fadeFraction, 1f, p));
        img.color = c;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        float target = requireInput ? ReadAxis() : 1f;
        target = target > 0 ? target * forwardSpeed : target * backwardSpeed;
        float rate = Mathf.Abs(target) > Mathf.Abs(_speed) && Mathf.Sign(target) == Mathf.Sign(_speed == 0 ? target : _speed) ? acceleration : deceleration;
        _speed = Mathf.MoveTowards(_speed, target, rate * dt);
        _t += dt * _speed;                                   // scene time: reverses with S, stops with no input
        float aspd = Mathf.Abs(_speed);
        _bobT += dt * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(aspd));
        _bobAmp = Mathf.MoveTowards(_bobAmp, Mathf.Clamp01(aspd / Mathf.Max(0.01f, forwardSpeed)), dt * 2.5f);
        float loopT = Mathf.Repeat(_t, loopDuration) / loopDuration;

        // gallop bob: sharp downward impact, slow rise; amplitude follows speed so it settles when stopped
        float beat = Mathf.Repeat(_bobT * bobBeatsPerSecond, 1f);
        float down = Mathf.Pow(Mathf.Sin(beat * Mathf.PI), impactSharpness);
        Vector2 bob = new Vector2(Mathf.Sin(_bobT * bobBeatsPerSecond * Mathf.PI) * swayPx, -down * bobPx + bobPx * 0.5f) * _bobAmp;
        if (_world) _world.localEulerAngles = new Vector3(0, 0, Mathf.Sin(_bobT * bobBeatsPerSecond * Mathf.PI) * rollDegrees * _bobAmp);

        if (_sky) _sky.rectTransform.anchoredPosition = bob * skyBobAmount;

        foreach (var L in layers)
        {
            if (!L.a) continue;
            float p = Mathf.Repeat(loopT * Mathf.Max(1, L.cyclesPerLoop), 1f);
            if (L.crossfade && L.b) { ApplyZoom(L.a, p, L, true, bob); ApplyZoom(L.b, (p + 0.5f) % 1f, L, true, bob); }
            else ApplyZoom(L.a, p, L, false, bob);
        }

        foreach (var f in _fog)
        {
            float p = (loopT + f.phase) % 1f;
            var rt = f.img.rectTransform; float s = Mathf.Lerp(1f, fogEndScale, p);
            rt.localScale = new Vector3(s, s, 1); rt.anchoredPosition = bob * 0.4f;
            var c = grade; c.a = fogAlpha * Mathf.Sin(p * Mathf.PI); f.img.color = c;
        }

        for (int i = 0; i < _fogSheets.Count; i++)
        {
            var f = _fogSheets[i];
            var r = f.img.uvRect; r.x = Mathf.Repeat(Time.time * f.speed + _t * 0.01f * (i + 1), 1f); f.img.uvRect = r;
            f.img.rectTransform.anchoredPosition = bob * (0.15f + 0.2f * i);
        }

        UpdateSmoke();

        if (_path)
        {
            DrawPath(loopT * pathTilesPerLoop * pathTileLength);
            _path.rectTransform.anchoredPosition = bob * pathBobAmount;
        }

        foreach (var s in _snow)
        {
            float p = Mathf.Repeat(loopT * Mathf.Round(s.speed * snowSpeed * 4f) + s.phase, 1f);
            float r = p * p * 1400f;
            var rt = s.img.rectTransform; rt.anchoredPosition = s.dir * r + bob * 0.7f + new Vector2(0, -p * 120f);
            float sz = snowSize * (0.3f + p * 2.2f); rt.sizeDelta = new Vector2(sz, sz);
            var c = s.img.color; c.a = Smooth(0, 0.15f, p) * (1 - Smooth(0.85f, 1, p)); s.img.color = c;
        }

        if (_horse)
        {
            float hb = (beat + horseBobPhase) % 1f;
            float hd = Mathf.Pow(Mathf.Sin(hb * Mathf.PI), impactSharpness) * _bobAmp;
            var rt = _horse.rectTransform;
            rt.anchoredPosition = new Vector2(bob.x * 0.6f, -hd * horseBobPx + horseBobPx * 0.3f);
            float twitch = 0f;
            if (earTwitchEvery > 0f)
            {
                float tp = Mathf.Repeat(Time.time, earTwitchEvery) / earTwitchEvery;
                if (tp < 0.08f) twitch = Mathf.Sin(tp / 0.08f * Mathf.PI * 2f) * earTwitchDegrees;
            }
            rt.localEulerAngles = new Vector3(0, 0, Mathf.Sin(hb * Mathf.PI * 2f) * horseNodDegrees * _bobAmp + twitch);
        }
    }

    static float ReadAxis()
    {
        float v = 0f;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null) { if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f; if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f; }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (v == 0f) { if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v += 1f; if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v -= 1f; }
#endif
        return Mathf.Clamp(v, -1f, 1f);
    }

    // Auto-spawn so the scene works even without manual setup.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoSpawn()
    {
        if (FindAnyObjectByType<HorseRideRig>()) return;
        if (!Resources.Load<Texture2D>("Ride/T_Ride_Sky")) return;
        new GameObject("HorseRideRig").AddComponent<HorseRideRig>();
    }
}
