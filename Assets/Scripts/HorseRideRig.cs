// HorseRideRig - layered 2.5D parallax first-person horse ride, "dark fantasy nostalgia" (Witcher vibe).
// Builds a Screen Space - Camera canvas at Play from textures in Resources/Ride/ so URP post-processing applies.
// Layers back-to-front: Sky(+moon glow), Mountains, Castle, Valley, CliffsRight, Spires, CliffLeft, Path (CPU perspective road),
// fog sheets between depths, moon rays, crows, fireflies, dust, Horse.
// Controls: W / Up = ride forward, D / Right = steer right (A / Left = steer left), S / Down = back up.
// Previous version: Backup/2026-11-04_DarkFantasy/HorseRideRig.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public class HorseRideRig : MonoBehaviour
{
    public enum Motion { Loop, Approach }

    [System.Serializable]
    public class ZoomLayer
    {
        public string textureName = "T_Ride_Castle";
        public bool enabled = true;
        [Tooltip("Loop = endless zoom with two crossfading copies (near scenery). Approach = slowly grows with total distance ridden (far landmarks).")]
        public Motion motion = Motion.Loop;
        [Tooltip("Scale at the start of a cycle")] public float startScale = 1f;
        [Tooltip("Scale at the end of a cycle / max scale for Approach")] public float endScale = 1.1f;
        [Tooltip("Cycles per loop (Loop mode)")] public int cyclesPerLoop = 1;
        [Tooltip("Pixel drift over one cycle (reference 1080x1920)")] public Vector2 drift = Vector2.zero;
        [Tooltip("Two copies half a cycle apart, crossfaded (seamless). Off = single copy.")] public bool crossfade = true;
        [Range(0f, 0.5f)] public float fadeFraction = 0.25f;
        [Tooltip("How much of the camera bob this layer receives (0 far .. 1 near)")] public float bobAmount = 0.3f;
        [Tooltip("How much this layer pans when steering (0 far .. 1 near)")] public float steerParallax = 0.3f;
        [Tooltip("Colour multiplier (atmospheric perspective)")] public Color tint = Color.white;
        [HideInInspector] public RawImage a, b;
        [HideInInspector] public RectTransform group;
    }

    [Header("Control (W forward, D right; A left / S back also work)")]
    public bool requireInput = true;
    [Tooltip("Max forward playback speed (1 = reference video speed)")] public float forwardSpeed = 1f;
    [Tooltip("Max backward playback speed")] public float backwardSpeed = 0.5f;
    public float acceleration = 1.0f, deceleration = 1.4f;
    [Tooltip("Holding D (or A) alone makes the horse walk while turning")] public bool steerWalks = true;
    [Range(0, 1)] public float steerWalkSpeed = 0.4f;
    [Tooltip("Max sideways pan in reference px for a layer with steerParallax = 1")] public float maxSteerPx = 260f;
    [Tooltip("How fast the steer value follows the key (per second)")] public float steerResponse = 0.9f;
    [Tooltip("When D is released the view slowly drifts back to centre")] public bool steerReturnsToCenter = true;
    public float steerReturnSpeed = 0.35f;
    [Tooltip("Road bend added at full steer")] public float pathSteerCurve = 0.45f;
    public float steerBankDegrees = 1.2f;

    [Header("Rendering")]
    [Tooltip("Pixel-perfect low-res render height (9:16). 0 = render at screen resolution.")] public int pixelHeight = 640;
    public FilterMode filter = FilterMode.Point;
    [Tooltip("URP post-processing (bloom, grading, vignette, grain, CA). Uses postProfile or builds one at runtime.")] public bool setupPostFX = true;
    public VolumeProfile postProfile;
    [Tooltip("Texture suffix for the recoloured dark-fantasy set (falls back to the original texture)")] public string textureSuffix = "_DF";

    [Header("Loop")]
    public float loopDuration = 4f;
    [Tooltip("Vanishing point in normalized screen coords (0..1)")] public Vector2 vanishingPoint = new Vector2(0.46f, 0.42f);
    public Vector2 referenceResolution = new Vector2(1080, 1920);
    public Color grade = Color.white;
    [Tooltip("Seconds of riding to get ~63% of the way to Approach layers' max scale")] public float approachSeconds = 45f;
    [Tooltip("Horizontal mirrored overscan on each side (fraction of width) so panning never shows an edge")] public float overscan = 0.35f;

    [Header("Sky")]
    public float skyBobAmount = 0.05f;
    public float skySteer = 0.03f;
    public Color skyTint = Color.white;

    [Header("Moon")]
    public bool buildMoonGlow = true;
    [Tooltip("Moon centre in sky texture (normalized, from bottom-left)")] public Vector2 moonPos = new Vector2(0.717f, 0.798f);
    public float moonGlowSize = 520f;
    public Color moonGlowColor = new Color(0.85f, 0.75f, 1f, 0.32f);
    public bool buildMoonRays = true;
    [Range(0, 1)] public float moonRayAlpha = 0.07f;

    [Header("Layers (back to front, excluding sky/path/horse)")]
    public List<ZoomLayer> layers = new List<ZoomLayer>
    {
        new ZoomLayer{ textureName="T_Ride_Mountains",   motion=Motion.Approach, startScale=1.00f, endScale=1.05f, crossfade=false, bobAmount=0.10f, steerParallax=0.06f, tint=new Color(0.95f,0.93f,1f) },
        new ZoomLayer{ textureName="T_Ride_Castle",      motion=Motion.Approach, startScale=1.00f, endScale=1.22f, crossfade=false, bobAmount=0.15f, steerParallax=0.09f },
        new ZoomLayer{ textureName="T_Ride_Valley",      startScale=1.00f, endScale=1.18f, drift=new Vector2(  0, -30), bobAmount=0.25f, steerParallax=0.16f },
        new ZoomLayer{ textureName="T_Ride_CliffsRight", startScale=1.00f, endScale=1.30f, drift=new Vector2( 70, -40), bobAmount=0.35f, steerParallax=0.30f },
        new ZoomLayer{ textureName="T_Ride_Spires",      startScale=1.00f, endScale=1.45f, drift=new Vector2(-70, -40), bobAmount=0.55f, steerParallax=0.40f },
        new ZoomLayer{ textureName="T_Ride_CliffLeft",   startScale=1.00f, endScale=1.90f, drift=new Vector2(-200,-30), cyclesPerLoop=2, bobAmount=0.8f, steerParallax=0.65f, tint=new Color(0.92f,0.92f,0.98f) },
    };

    [Header("Fog (procedural dithered pixel fog)")]
    public bool buildFog = true;
    public Color fogColor = new Color(0.55f, 0.47f, 0.74f, 1f);
    public Color groundMistColor = new Color(0.50f, 0.55f, 0.70f, 1f);
    [Tooltip("Fog pixel grid (texture width in pixels)")] public int fogTexWidth = 240;
    [Tooltip("Haze sheet IN FRONT of each layer (same order as Layers)")] public float[] layerFogAlpha = { 0.20f, 0.22f, 0.20f, 0.12f, 0.08f, 0f };
    [Tooltip("Vertical centre of each haze band (0 bottom .. 1 top)")] public float[] layerFogCenter = { 0.47f, 0.385f, 0.31f, 0.26f, 0.22f, 0.2f };
    [Tooltip("Horizontal drift speed of the fog (uv per second)")] public float fogDrift = 0.008f;
    [Range(0, 1)] public float groundMistAlpha = 0.22f;
    [Range(0, 1)] public float foregroundWispAlpha = 0.10f;

    [Header("Path (CPU perspective road)")]
    public bool buildPath = true;
    public int pathTexWidth = 240, pathTexHeight = 180;
    public float pathScreenFraction = 0.42f;
    public float pathHorizon = 0.98f;
    public float cameraHeight = 1.0f;
    public float pathWorldWidth = 2.4f;
    public float pathTileLength = 6f;
    [Tooltip("Tiles travelled per loop (integer = seamless)")] public int pathTilesPerLoop = 2;
    public float pathVanishX = 0f;
    public float pathFadeNear = 0.0f, pathFadeFar = 0.35f;
    public float pathCurve = -0.32f;
    public float pathCurvePower = 2.2f;
    public Color pathTint = new Color(0.86f, 0.84f, 0.95f, 1f);
    [Tooltip("Path colour blends to this near the horizon (fog)")] public Color pathFarColor = new Color(0.30f, 0.26f, 0.45f, 1f);
    public float pathBobAmount = 0.9f;

    [Header("Horse (foreground)")]
    public bool buildHorse = true;
    [Tooltip("Horse height as fraction of screen height")] public float horseHeight = 0.42f;
    [Tooltip("Pivot y: lower = more of the horse pushed below the frame")] public float horsePivotY = 0.08f;
    public Color horseTint = Color.white;
    public float horseSteerPx = 34f, horseSteerDegrees = 3f;

    [Header("Gait (walk -> 3-beat canter, driven by speed)")]
    public float walkStrideHz = 0.95f, canterStrideHz = 1.75f;
    public Vector2 canterBlend = new Vector2(0.35f, 0.75f);
    public float walkBobPx = 6f, canterBobPx = 26f;
    public float pitchPx = 10f;
    public float gaitSwayPx = 9f;
    public float gaitRollDegrees = 0.8f;
    public float neckPumpPx = 16f;
    [Range(0, 1)] public float neckPhase = 0.55f;
    public float neckNodDegrees = 1.4f;
    public float earTwitchDegrees = 1.2f;
    public Vector2 earTwitchInterval = new Vector2(1.5f, 4.5f);

    [Header("Smoke (procedural pixel puffs)")]
    public bool buildSmoke = true;
    [Tooltip("x,y = position inside the layer texture (0..1 from bottom-left), z = index into Layers, w = strength")]
    public Vector4[] smokeEmitters = { new Vector4(0.86f, 0.52f, 1, 1.2f), new Vector4(0.47f, 0.56f, 1, 0.7f), new Vector4(0.50f, 0.215f, 2, 0.6f), new Vector4(0.56f, 0.205f, 2, 0.5f), new Vector4(0.22f, 0.41f, 4, 0.6f), new Vector4(0.31f, 0.40f, 4, 0.5f) };
    public int puffsPerEmitter = 14;
    public Vector2 wind = new Vector2(0.9f, 1f);
    public float smokeRise = 70f, smokeLife = 5f, smokeSize = 10f;
    public Color smokeColor = new Color(0.56f, 0.50f, 0.66f, 0.6f);

    [Header("Particles")]
    public bool buildFireflies = true;
    public int fireflyCount = 46;
    public Color fireflyColor = new Color(1f, 0.86f, 0.45f, 1f);
    public float fireflySize = 9f;
    public bool buildDust = true;
    public int dustCount = 70;
    public Color dustColor = new Color(0.80f, 0.74f, 0.95f, 0.55f);
    public float dustSize = 5f;
    public bool buildSnow = false;
    public int snowCount = 70;
    public bool buildCrows = true;
    public int crowCount = 4;

    // ------------------------------------------------------------------ state
    RectTransform _root, _world, _skyGroup;
    RawImage _sky, _path, _horse, _rays;
    Texture2D _pathOut, _pathSrc;
    Color32[] _src, _dst;
    int _sw, _sh;
    float _t, _speed, _bobAmp, _gaitW, _stride, _earTimer = 2f, _earT = 1f, _steer, _distance;
    Camera _cam, _displayCam;
    RenderTexture _rt;
    readonly List<(RawImage img, float speed, float steer)> _fogSheets = new List<(RawImage, float, float)>();

    class Mote { public RawImage img; public Vector3 p; public float seed, size, speed; public bool firefly; }
    readonly List<Mote> _motes = new List<Mote>();
    class Crow { public RawImage img; public float x, y, speed, phase, scale; }
    readonly List<Crow> _crows = new List<Crow>();
    Texture2D _crowUp, _crowDown;

    public RenderTexture PixelTarget => _rt;
    public Camera RenderCamera => _cam;

    void Start() { Build(); }

    void OnDestroy()
    {
        if (_cam && _cam.targetTexture == _rt) _cam.targetTexture = null;
        if (_rt) { _rt.Release(); if (Application.isPlaying) Destroy(_rt); else DestroyImmediate(_rt); }
    }

    Texture2D Load(string n)
    {
        Texture2D t = null;
        if (!string.IsNullOrEmpty(textureSuffix)) t = Resources.Load<Texture2D>("Ride/" + n + textureSuffix);
        return t ? t : Resources.Load<Texture2D>("Ride/" + n);
    }

    RawImage MakeImage(string name, Texture tex, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        go.layer = 5;
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.GetComponent<RawImage>(); img.texture = tex; img.raycastTarget = false; img.color = grade;
        if (tex) tex.filterMode = filter;
        return img;
    }

    RectTransform MakeGroup(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.layer = 5;
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    // Full-frame rect widened by mirrored overscan; pivot at the vanishing point so scaling zooms toward it.
    void SetupLayerRect(RawImage img)
    {
        var rt = img.rectTransform;
        float ov = Mathf.Max(0f, overscan);
        float w = referenceResolution.x;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-ov * w, 0); rt.offsetMax = new Vector2(ov * w, 0);
        rt.pivot = new Vector2((vanishingPoint.x + ov) / (1f + 2f * ov), vanishingPoint.y);
        img.uvRect = new Rect(-ov, 0f, 1f + 2f * ov, 1f);
        if (img.texture is Texture2D t) { t.wrapModeU = TextureWrapMode.Mirror; t.wrapModeV = TextureWrapMode.Clamp; }
    }

    // ------------------------------------------------------------------ build
    void SetupCamera(Canvas canvas)
    {
        _cam = Camera.main;
        if (!_cam)
        {
            var cgo = new GameObject("Main Camera", typeof(Camera)); cgo.tag = "MainCamera";
            _cam = cgo.GetComponent<Camera>(); cgo.transform.position = new Vector3(0, 1, -10);
        }
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = Color.black;
        _cam.cullingMask |= 1 << 5;
        var data = _cam.GetUniversalAdditionalCameraData();
        if (data != null) { data.renderPostProcessing = setupPostFX; data.antialiasing = AntialiasingMode.None; }

        if (pixelHeight > 0)
        {
            int h = pixelHeight, w = Mathf.RoundToInt(h * referenceResolution.x / referenceResolution.y);
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "RidePixelRT", filterMode = FilterMode.Point, antiAliasing = 1 };
            _rt.Create();
            _cam.targetTexture = _rt;

            // A display camera + overlay canvas shows the low-res frame, upscaled with hard pixels and letterboxed.
            var dgo = new GameObject("RideDisplayCamera", typeof(Camera));
            dgo.transform.SetParent(transform, false);
            _displayCam = dgo.GetComponent<Camera>();
            _displayCam.clearFlags = CameraClearFlags.SolidColor; _displayCam.backgroundColor = Color.black;
            _displayCam.cullingMask = 0; _displayCam.depth = _cam.depth + 10;
            var dd = _displayCam.GetUniversalAdditionalCameraData();
            if (dd != null) dd.renderPostProcessing = false;

            var ogo = new GameObject("RideDisplayUI", typeof(Canvas), typeof(CanvasScaler)); ogo.layer = 5;
            ogo.transform.SetParent(transform, false);
            var oc = ogo.GetComponent<Canvas>(); oc.renderMode = RenderMode.ScreenSpaceOverlay; oc.sortingOrder = 100;
            var img = MakeImage("Frame", _rt, (RectTransform)ogo.transform); img.color = Color.white;
            var fit = img.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent; fit.aspectRatio = referenceResolution.x / referenceResolution.y;
        }

        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = _cam;
        canvas.planeDistance = Mathf.Clamp(5f, _cam.nearClipPlane + 0.1f, _cam.farClipPlane - 0.1f);
    }

    void SetupVolume()
    {
        if (!setupPostFX) return;
        if (!postProfile) postProfile = DarkFantasyPost.CreateRuntimeProfile();
        foreach (var v in FindObjectsByType<Volume>())
            if (v.sharedProfile == postProfile) return;              // scene already uses it (built by the editor menu)
        var go = new GameObject("DarkFantasyVolume (runtime)", typeof(Volume));
        go.transform.SetParent(transform, false);
        var vol = go.GetComponent<Volume>(); vol.isGlobal = true; vol.priority = 50; vol.sharedProfile = postProfile;
    }

    public void Build()
    {
        var canvasGo = new GameObject("RideUI", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.layer = 5;
        canvasGo.transform.SetParent(transform, false);
        var c = canvasGo.GetComponent<Canvas>();
        SetupCamera(c);
        SetupVolume();
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; cs.referenceResolution = referenceResolution; cs.matchWidthOrHeight = 1f;

        var bg = MakeImage("Bg", Texture2D.whiteTexture, (RectTransform)canvasGo.transform); bg.color = Color.black;
        var frame = new GameObject("Frame9x16", typeof(RectTransform), typeof(RectMask2D)); frame.layer = 5;
        _root = (RectTransform)frame.transform; _root.SetParent(canvasGo.transform, false);
        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f); _root.sizeDelta = referenceResolution;
        _world = MakeGroup("World", _root);
        _world.offsetMin = new Vector2(-40, -40); _world.offsetMax = new Vector2(40, 40); // overscan for bob/roll

        // Sky + moon glow
        _skyGroup = MakeGroup("L1_SkyGroup", _world);
        _sky = MakeImage("Sky", Load("T_Ride_Sky"), _skyGroup); SetupLayerRect(_sky); _sky.color = skyTint;
        if (buildMoonGlow) BuildMoonGlow();
        if (buildCrows) BuildCrows();

        int li = 2;
        for (int i = 0; i < layers.Count; i++)
        {
            var L = layers[i];
            L.a = L.b = null; L.group = null;
            if (!L.enabled) continue;
            var tex = Load(L.textureName);
            if (!tex) { Debug.LogWarning("HorseRideRig: missing Resources/Ride/" + L.textureName); continue; }
            L.group = MakeGroup("L" + li + "_" + L.textureName, _world);
            L.a = MakeImage(L.textureName + "_A", tex, L.group); SetupLayerRect(L.a);
            if (L.motion == Motion.Loop && L.crossfade) { L.b = MakeImage(L.textureName + "_B", tex, L.group); SetupLayerRect(L.b); }
            if (buildMoonRays && L.textureName.Contains("Castle")) BuildMoonRays();
            if (buildFog && i < layerFogAlpha.Length && layerFogAlpha[i] > 0f)
            {
                float cy = i < layerFogCenter.Length ? layerFogCenter[i] : 0.3f;
                AddFogSheet("Haze_" + L.textureName, cy, 0.055f + i * 0.012f, layerFogAlpha[i], fogDrift * (1f + i * 0.6f), i * 37 + 3, fogColor, 0.05f + L.steerParallax);
            }
            li++;
        }

        if (buildSmoke) BuildSmoke();
        if (buildPath) BuildPath();
        if (buildFog && groundMistAlpha > 0f) AddFogSheet("GroundMist", 0.30f, 0.07f, groundMistAlpha, fogDrift * 3f, 911, groundMistColor, 0.8f);
        BuildMotes();
        if (buildFog && foregroundWispAlpha > 0f) AddFogSheet("ForegroundWisps", 0.03f, 0.06f, foregroundWispAlpha, fogDrift * 5f, 517, groundMistColor, 1f);

        if (buildHorse)
        {
            var htex = Load("T_Ride_Horse");
            if (htex)
            {
                _horse = MakeImage("L9_Horse", htex, _root); _horse.color = horseTint;
                var rt = _horse.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, horsePivotY);
                float h = referenceResolution.y * horseHeight;
                rt.sizeDelta = new Vector2(h * htex.width / (float)htex.height, h);
            }
        }
        Tick(0f);
    }

    static readonly int[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
    static float Bayer(int x, int y) { return (Bayer4[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f; }

    void BuildMoonGlow()
    {
        int n = 64; var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float r = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
            float d = Mathf.Clamp01(Mathf.Exp(-r * r * 4f) * 1.05f - 0.03f);
            float q = d * 6f + Bayer(x, y) - 0.5f; int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 6);
            px[y * n + x] = new Color32(255, 255, 255, (byte)(lvl * 255 / 6));
        }
        t.SetPixels32(px); t.Apply(false);
        var img = MakeImage("MoonGlow", t, _sky.rectTransform); img.color = moonGlowColor;
        var rt = img.rectTransform;
        // moonPos is in texture space; the sky rect has mirrored overscan, convert to rect space
        float ov = Mathf.Max(0f, overscan);
        var p = new Vector2((moonPos.x + ov) / (1f + 2f * ov), moonPos.y);
        rt.anchorMin = rt.anchorMax = p; rt.sizeDelta = Vector2.one * moonGlowSize; rt.pivot = new Vector2(0.5f, 0.5f);
    }

    void BuildMoonRays()
    {
        int W = 135, H = 240;
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[W * H];
        var src = new Vector2(moonPos.x * W, moonPos.y * H);
        float[] beams = { -2.05f, -1.85f, -1.62f, -1.35f, -1.12f };
        float[] widths = { 0.02f, 0.04f, 0.018f, 0.03f, 0.02f };
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            var d = new Vector2(x + 0.5f, y + 0.5f) - src;
            float ang = Mathf.Atan2(d.y, d.x), dist = d.magnitude / H;
            float v = 0f;
            for (int i = 0; i < beams.Length; i++) { float da = Mathf.DeltaAngle(ang * Mathf.Rad2Deg, beams[i] * Mathf.Rad2Deg) * Mathf.Deg2Rad; v += Mathf.Exp(-(da * da) / (widths[i] * widths[i])); }
            v *= Mathf.Clamp01(dist * 6f) * Mathf.Clamp01(1.1f - dist * 1.4f);
            float q = Mathf.Clamp01(v) * 3f + Bayer(x, y) - 0.5f; int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 3);
            px[y * W + x] = new Color32(235, 220, 255, (byte)(lvl * 255 / 3));
        }
        t.SetPixels32(px); t.Apply(false);
        _rays = MakeImage("MoonRays", t, _world); _rays.color = new Color(1, 1, 1, moonRayAlpha);
    }

    void BuildCrows()
    {
        _crowUp = CrowTex(true); _crowDown = CrowTex(false);
        for (int i = 0; i < crowCount; i++)
        {
            var img = MakeImage("Crow" + i, _crowUp, _skyGroup); img.color = new Color(0.05f, 0.03f, 0.09f, 1f);
            var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0.5f);
            var c = new Crow { img = img, phase = Random.value, scale = Random.Range(0.6f, 1.1f) };
            RespawnCrow(c, true); _crows.Add(c);
        }
    }

    void RespawnCrow(Crow c, bool anywhere)
    {
        c.x = anywhere ? Random.Range(0f, 1f) : Random.Range(-0.35f, -0.1f);
        c.y = Random.Range(0.66f, 0.86f); c.speed = Random.Range(0.025f, 0.045f);
    }

    Texture2D CrowTex(bool up)
    {
        string[] a = up
            ? new[] { "X.......X", ".X.....X.", "..XX.XX..", "....X...." }
            : new[] { ".........", "....X....", ".XXXXXXX.", "X.......X" };
        int w = 9, h = 4; var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) t.SetPixel(x, h - 1 - y, a[y][x] == 'X' ? Color.white : Color.clear);
        t.Apply(false); return t;
    }

    // Full-screen dithered pixel fog band: vertical centre/height in screen fractions (from bottom).
    void AddFogSheet(string name, float centerY, float height, float alpha, float speed, int seed, Color col, float steer)
    {
        int W = Mathf.Max(32, fogTexWidth), H = Mathf.RoundToInt(W * referenceResolution.y / referenceResolution.x);
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = filter };
        var px = new Color32[W * H];
        Color32 fc = col;
        for (int y = 0; y < H; y++)
        {
            float fy = (float)y / H;
            float band = Mathf.Exp(-Mathf.Pow((fy - centerY) / Mathf.Max(0.01f, height), 2f));
            for (int x = 0; x < W; x++)
            {
                float a = (float)x / W * Mathf.PI * 2f;
                float n = 0f, amp = 0.55f, f = 1f;
                for (int o = 0; o < 4; o++)
                {
                    n += amp * Mathf.PerlinNoise(seed + Mathf.Cos(a) * 3f * f, seed * 0.37f + Mathf.Sin(a) * 3f * f + fy * 9f * f);
                    amp *= 0.5f; f *= 2f;
                }
                float d = Mathf.Clamp01(band * (0.35f + n * 1.1f));
                float q = d * 4f + Bayer(x, y) - 0.5f;
                int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 4);
                px[y * W + x] = new Color32(fc.r, fc.g, fc.b, (byte)(lvl * 255 / 4));
            }
        }
        t.SetPixels32(px); t.Apply(false);
        var img = MakeImage(name, t, _world); img.color = new Color(1, 1, 1, alpha);
        _fogSheets.Add((img, speed, steer));
    }

    class Puff { public RawImage img; public float phase, seed, strength; public ZoomLayer owner; public bool isB; }
    readonly List<Puff> _puffs = new List<Puff>();

    Texture2D MakePuffTex()
    {
        int n = 8; var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float r = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
            float d = Mathf.Clamp01(1.15f - r);
            float q = d * 2f + Bayer(x, y) - 0.5f;
            int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 2);
            t.SetPixel(x, y, new Color(1, 1, 1, lvl / 2f));
        }
        t.Apply(); return t;
    }

    void BuildSmoke()
    {
        var tex = MakePuffTex();
        float ov = Mathf.Max(0f, overscan);
        foreach (var e in smokeEmitters)
        {
            int li = (int)e.z; if (li < 0 || li >= layers.Count) continue;
            var L = layers[li]; if (!L.enabled || !L.a) continue;
            var anchor = new Vector2((e.x + ov) / (1f + 2f * ov), e.y);
            foreach (var host in new[] { L.a, L.b })
            {
                if (!host) continue;
                for (int i = 0; i < puffsPerEmitter; i++)
                {
                    var img = MakeImage("Smoke", tex, host.rectTransform);
                    var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
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
            float a = Mathf.Repeat(Time.time / life + p.phase, 1f);
            float wob = Mathf.Sin((Time.time + p.seed) * 1.3f) * 4f * a;
            var rt = p.img.rectTransform;
            rt.anchoredPosition = new Vector2((wind.x * smokeRise * a * a * 1.6f + wob), (wind.y * smokeRise * a)) * p.strength;
            float sz = smokeSize * p.strength * (0.6f + a * 2.4f);
            sz = Mathf.Round(sz / 4f) * 4f;
            rt.sizeDelta = new Vector2(sz, sz);
            var host = p.isB ? p.owner.b : p.owner.a;
            float hostA = host ? host.color.a : 1f;
            var c = smokeColor; c.a *= Smooth(0f, 0.12f, a) * (1f - Smooth(0.45f, 1f, a)) * hostA;
            p.img.color = c;
        }
    }

    Texture2D MakeGlowDot(int n, float core)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float r = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
            float d = r < core ? 1f : Mathf.Clamp01(1f - (r - core) / (1f - core)) * 0.45f;
            float q = d * 3f + Bayer(x, y) - 0.5f; int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, 3);
            t.SetPixel(x, y, new Color(1, 1, 1, lvl / 3f));
        }
        t.Apply(false); return t;
    }

    void BuildMotes()
    {
        var ff = MakeGlowDot(7, 0.3f);
        var dust = MakeGlowDot(2, 1f);
        int total = (buildFireflies ? fireflyCount : 0) + (buildDust ? dustCount : 0) + (buildSnow ? snowCount : 0);
        for (int i = 0; i < total; i++)
        {
            bool isFF = buildFireflies && i < fireflyCount;
            bool isSnow = !isFF && buildSnow && i >= total - snowCount;
            var img = MakeImage(isFF ? "Firefly" : isSnow ? "Snow" : "Dust", isFF ? ff : dust, _world);
            var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = vanishingPoint; rt.pivot = new Vector2(0.5f, 0.5f);
            var m = new Mote { img = img, firefly = isFF, seed = Random.value * 100f };
            m.size = isFF ? fireflySize * Random.Range(0.7f, 1.3f) : (isSnow ? 7f : dustSize) * Random.Range(0.6f, 1.4f);
            m.speed = isFF ? Random.Range(0.25f, 0.5f) : Random.Range(0.6f, 1.1f);
            img.color = isFF ? fireflyColor : (isSnow ? new Color(0.92f, 0.92f, 1f, 0.8f) : dustColor);
            RespawnMote(m, true); _motes.Add(m);
        }
    }

    void RespawnMote(Mote m, bool anyDepth)
    {
        // p = (x, y) spread around the vanishing point in "world" units, z = depth (1 far .. 0.12 near)
        float side = Random.value < 0.5f ? -1f : 1f;
        float x = m.firefly ? side * Random.Range(0.15f, 1.3f) : Random.Range(-1.4f, 1.4f);
        float y = m.firefly ? Random.Range(-0.75f, 0.25f) : Random.Range(-0.9f, 0.9f);
        m.p = new Vector3(x, y, anyDepth ? Random.Range(0.15f, 1f) : Random.Range(0.85f, 1.1f));
    }

    void UpdateMotes(float travel, Vector2 bob, float steerPx)
    {
        var size = referenceResolution;
        foreach (var m in _motes)
        {
            m.p.z -= travel * 0.22f * m.speed;
            if (m.firefly)
            {
                m.p.x += Mathf.Sin(Time.time * 0.7f + m.seed) * 0.0009f;
                m.p.y += Mathf.Cos(Time.time * 0.9f + m.seed * 1.7f) * 0.0007f;
            }
            else m.p.y -= Time.deltaTime * 0.02f * m.speed;               // slow falling ash / dust
            if (m.p.z < 0.12f) RespawnMote(m, false);
            if (m.p.z > 1.15f) m.p.z = 0.12f;
            float inv = 1f / Mathf.Max(0.05f, m.p.z);
            float nearness = Mathf.Clamp01(1f - m.p.z);
            var rt = m.img.rectTransform;
            Vector2 pos = new Vector2(m.p.x * size.x * 0.32f, m.p.y * size.y * 0.22f) * inv;
            pos += bob * (0.3f + 0.6f * nearness) - new Vector2(steerPx * (0.2f + 0.8f * nearness), 0);
            rt.anchoredPosition = new Vector2(Mathf.Round(pos.x / 3f) * 3f, Mathf.Round(pos.y / 3f) * 3f);
            float s = Mathf.Max(3f, Mathf.Round(m.size * (0.5f + inv * 0.35f) / 3f) * 3f);
            rt.sizeDelta = new Vector2(s, s);
            var c = m.img.color;
            float fadeIn = Smooth(1.1f, 0.85f, m.p.z), fadeOut = Smooth(0.12f, 0.22f, m.p.z);
            float blink = m.firefly ? Mathf.Clamp01(0.55f + 0.45f * Mathf.Sin(Time.time * (1.3f + m.seed % 1.5f) + m.seed)) : 1f;
            c.a = (m.firefly ? fireflyColor.a : (m.img.name == "Snow" ? 0.8f : dustColor.a)) * fadeIn * fadeOut * blink;
            m.img.color = c;
        }
    }

    void UpdateCrows(float dt)
    {
        foreach (var c in _crows)
        {
            c.x += c.speed * dt; c.phase += dt * 2.2f;
            if (c.x > 1.2f) RespawnCrow(c, false);
            var rt = c.img.rectTransform;
            rt.anchoredPosition = new Vector2(c.x * referenceResolution.x, (c.y + Mathf.Sin(c.phase * 0.7f) * 0.01f) * referenceResolution.y);
            rt.sizeDelta = new Vector2(9f, 4f) * 4f * c.scale;
            c.img.texture = Mathf.Repeat(c.phase, 1f) < 0.5f ? _crowUp : _crowDown;
        }
    }

    void BuildPath()
    {
        _pathSrc = Load("T_Ride_Path");
        if (!_pathSrc) { Debug.LogWarning("HorseRideRig: missing T_Ride_Path"); return; }
        try { _src = _pathSrc.GetPixels32(); }
        catch { Debug.LogError("HorseRideRig: T_Ride_Path must be Read/Write enabled (RideTextureImporter does this automatically)"); return; }
        _sw = _pathSrc.width; _sh = _pathSrc.height;
        _pathOut = new Texture2D(pathTexWidth, pathTexHeight, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = filter };
        _dst = new Color32[pathTexWidth * pathTexHeight];
        _path = MakeImage("L8_Path", _pathOut, _world);
        var rt = _path.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(1f, pathScreenFraction);
        rt.offsetMin = rt.offsetMax = Vector2.zero; rt.pivot = new Vector2(vanishingPoint.x, 0f);
        _path.color = Color.white;
    }

    Color32 Sample(float u, float v)
    {
        float x = Mathf.Clamp(u * _sw - 0.5f, 0, _sw - 1.001f);
        float y = (v - Mathf.Floor(v)) * _sh - 0.5f; if (y < 0) y += _sh;
        int x0 = (int)x, y0 = (int)y; int x1 = x0 + 1, y1 = (y0 + 1) % _sh;
        float fx = x - x0, fy = y - y0;
        Color32 a = _src[y0 * _sw + x0], b = _src[y0 * _sw + x1], c = _src[y1 * _sw + x0], d = _src[y1 * _sw + x1];
        return Color32.Lerp(Color32.Lerp(a, b, fx), Color32.Lerp(c, d, fx), fy);
    }

    void DrawPath(float travel, float curve)
    {
        int W = pathTexWidth, H = pathTexHeight;
        float hz = pathHorizon * H;
        Color32 farC = pathFarColor;
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            float dy = (hz - y) / H;
            if (dy <= 0.002f) { for (int x = 0; x < W; x++) _dst[row + x] = new Color32(0, 0, 0, 0); continue; }
            float z = cameraHeight / dy;
            float halfW = pathWorldWidth * 0.5f / z;
            float vanish = Mathf.Lerp(pathVanishX, 0f, Mathf.Clamp01(dy));
            vanish += curve * Mathf.Pow(1f - Mathf.Clamp01(dy), pathCurvePower);
            float v = (z + travel) / pathTileLength;
            float far = Mathf.Clamp01(dy / Mathf.Max(0.001f, pathFadeFar));
            float fogK = 1f - Mathf.Clamp01(dy / 0.6f); fogK *= fogK;
            float aspect = (float)W / H;
            for (int x = 0; x < W; x++)
            {
                float sx = ((x + 0.5f) / W - vanishingPoint.x) * aspect - vanish * aspect;
                float u = sx / (halfW * 2f) + 0.5f;
                if (u < -0.02f || u > 1.02f) { _dst[row + x] = new Color32(0, 0, 0, 0); continue; }
                var col = Sample(Mathf.Clamp01(u), v);
                float edge = Mathf.Clamp01(Mathf.Min(u + 0.02f, 1.02f - u) * 25f);
                byte a = (byte)(col.a * far * edge);
                col = Color32.Lerp(new Color32((byte)(col.r * pathTint.r), (byte)(col.g * pathTint.g), (byte)(col.b * pathTint.b), 255), farC, fogK);
                col.a = a;
                _dst[row + x] = col;
            }
        }
        _pathOut.SetPixels32(_dst); _pathOut.Apply(false);
    }

    static float Smooth(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3 - 2 * x); }

    void ApplyZoom(RawImage img, float p, ZoomLayer L, bool fade, Vector2 bob, float steerPx)
    {
        if (!img) return;
        float s = Mathf.Lerp(L.startScale, L.endScale, p * p * 0.35f + p * 0.65f);
        var rt = img.rectTransform; rt.localScale = new Vector3(s, s, 1);
        rt.anchoredPosition = L.drift * p + bob * L.bobAmount - new Vector2(steerPx * L.steerParallax, 0);
        var c = grade * L.tint;
        if (fade) c.a = Smooth(0, L.fadeFraction, p) * (1f - Smooth(1f - L.fadeFraction, 1f, p));
        img.color = c;
    }

    void Update() { Tick(Time.deltaTime); }

    /// <summary>Advances the ride by dt (also used by the editor preview renderer).</summary>
    public void Tick(float dt, float forcedForward = -1f, float forcedSteer = float.NaN)
    {
        Vector2 input = forcedForward >= 0f ? new Vector2(float.IsNaN(forcedSteer) ? 0f : forcedSteer, forcedForward) : (requireInput ? ReadInput() : new Vector2(0f, 1f));
        if (!float.IsNaN(forcedSteer)) input.x = forcedSteer;
        float target = input.y > 0 ? input.y * forwardSpeed : input.y * backwardSpeed;
        if (steerWalks && Mathf.Abs(input.x) > 0.01f && Mathf.Abs(input.y) < 0.01f) target = steerWalkSpeed * forwardSpeed;
        float rate = Mathf.Abs(target) > Mathf.Abs(_speed) && Mathf.Sign(target) == Mathf.Sign(_speed == 0 ? target : _speed) ? acceleration : deceleration;
        _speed = Mathf.MoveTowards(_speed, target, rate * dt);
        _t += dt * _speed;
        _distance = Mathf.Max(0f, _distance + dt * _speed);
        float aspd = Mathf.Abs(_speed);
        _bobAmp = Mathf.MoveTowards(_bobAmp, Mathf.Clamp01(3f * aspd / Mathf.Max(0.01f, forwardSpeed)), dt * 2f);
        float loopT = Mathf.Repeat(_t, loopDuration) / loopDuration;

        // steering (D right / A left)
        float steerTarget = Mathf.Abs(input.x) > 0.01f ? Mathf.Sign(input.x) : (steerReturnsToCenter ? 0f : _steer);
        float sRate = Mathf.Abs(input.x) > 0.01f ? steerResponse : steerReturnSpeed;
        _steer = Mathf.MoveTowards(_steer, Mathf.Clamp(steerTarget, -1f, 1f), sRate * dt * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(aspd * 2f)));
        float steerEase = _steer * (2f - Mathf.Abs(_steer));        // ease-out curve
        float steerPx = steerEase * maxSteerPx;

        // ---- gait ----
        float sNorm = Mathf.Clamp01(aspd / Mathf.Max(0.01f, forwardSpeed));
        float canterT = _speed < 0f ? 0f : Smooth(canterBlend.x, canterBlend.y, sNorm);
        _gaitW = Mathf.MoveTowards(_gaitW, canterT, dt * 1.5f);
        float strideHz = Mathf.Lerp(walkStrideHz, canterStrideHz, _gaitW) * Mathf.Lerp(0.75f, 1f, sNorm);
        _stride += dt * strideHz * (aspd > 0.0005f || _bobAmp > 0.01f ? 1f : 0f);
        float ph = Mathf.Repeat(_stride, 1f);
        float vert = Mathf.Lerp(WalkCurve(ph) * walkBobPx, CanterCurve(ph) * canterBobPx, _gaitW);
        float pitch = Mathf.Lerp(0.3f, 1f, _gaitW) * pitchPx * CanterPitch(ph);
        float sway = Mathf.Sin(_stride * Mathf.PI) * gaitSwayPx * Mathf.Lerp(1f, 0.7f, _gaitW);
        Vector2 bob = new Vector2(sway, vert + pitch) * _bobAmp;
        if (_world)
        {
            float roll = Mathf.Sin(_stride * Mathf.PI + 0.6f) * gaitRollDegrees * _bobAmp - steerEase * steerBankDegrees;
            _world.localEulerAngles = new Vector3(0, 0, roll);
            float sc = 1f + 0.004f * pitch / Mathf.Max(1f, pitchPx) * _bobAmp;
            _world.localScale = new Vector3(sc, sc, 1f);
        }

        if (_skyGroup) _skyGroup.anchoredPosition = bob * skyBobAmount - new Vector2(steerPx * skySteer, 0);
        if (_sky && buildMoonGlow && _sky.transform.childCount > 0)
        {
            var g = _sky.transform.GetChild(0).GetComponent<RawImage>();
            if (g) { var c = moonGlowColor; c.a *= 0.85f + 0.15f * Mathf.Sin(Time.time * 0.8f); g.color = c; }
        }

        float approach = 1f - Mathf.Exp(-_distance / Mathf.Max(1f, approachSeconds));
        foreach (var L in layers)
        {
            if (!L.a) continue;
            if (L.motion == Motion.Approach)
            {
                float s = Mathf.Lerp(L.startScale, L.endScale, approach);
                var rt = L.a.rectTransform; rt.localScale = new Vector3(s, s, 1);
                rt.anchoredPosition = L.drift * approach + bob * L.bobAmount - new Vector2(steerPx * L.steerParallax, 0);
                L.a.color = grade * L.tint;
                continue;
            }
            float p = Mathf.Repeat(loopT * Mathf.Max(1, L.cyclesPerLoop), 1f);
            if (L.crossfade && L.b)
            {
                float pb = (p + 0.5f) % 1f;
                ApplyZoom(L.a, p, L, true, bob, steerPx); ApplyZoom(L.b, pb, L, true, bob, steerPx);
                // the nearer (bigger, outgoing) copy must draw on top of the incoming one
                (p > pb ? L.a : L.b).transform.SetAsLastSibling();
            }
            else ApplyZoom(L.a, p, L, false, bob, steerPx);
        }

        if (_rays)
        {
            _rays.rectTransform.anchoredPosition = bob * 0.12f - new Vector2(steerPx * 0.08f, 0);
            _rays.color = new Color(1, 1, 1, moonRayAlpha * (0.8f + 0.2f * Mathf.Sin(Time.time * 0.5f)));
        }

        float tNow = Application.isPlaying ? Time.time : _t;
        for (int i = 0; i < _fogSheets.Count; i++)
        {
            var f = _fogSheets[i];
            var r = f.img.uvRect;
            r.x = Mathf.Repeat(tNow * f.speed + _t * 0.01f * (i + 1) + steerPx * f.steer / referenceResolution.x, 1f);
            f.img.uvRect = r;
            f.img.rectTransform.anchoredPosition = bob * (0.15f + 0.2f * f.steer);
        }

        UpdateSmoke(); UpdateCrows(dt);
        UpdateMotes(dt * _speed, bob, steerPx);

        if (_path)
        {
            DrawPath(loopT * pathTilesPerLoop * pathTileLength, pathCurve + steerEase * pathSteerCurve);
            _path.rectTransform.anchoredPosition = bob * pathBobAmount;
        }

        if (_horse)
        {
            float nph = Mathf.Repeat(ph - neckPhase, 1f);
            float pump = Mathf.Lerp(Mathf.Sin(nph * Mathf.PI * 4f) * 0.35f, Mathf.Cos(nph * Mathf.PI * 2f), _gaitW);
            var rt = _horse.rectTransform;
            rt.anchoredPosition = new Vector2(bob.x * 0.55f + steerEase * horseSteerPx, -bob.y * 0.35f + pump * neckPumpPx * _bobAmp);
            _earTimer -= dt;
            if (_earTimer <= 0f) { _earTimer = Random.Range(earTwitchInterval.x, earTwitchInterval.y); _earT = 0f; }
            _earT += dt;
            float twitch = _earT < 0.25f ? Mathf.Sin(_earT / 0.25f * Mathf.PI * 2f) * earTwitchDegrees : 0f;
            rt.localEulerAngles = new Vector3(0, 0, Mathf.Sin(nph * Mathf.PI * 2f) * neckNodDegrees * _bobAmp + twitch - steerEase * horseSteerDegrees);
            float sy = 1f + 0.012f * twitch / Mathf.Max(0.01f, earTwitchDegrees);
            rt.localScale = new Vector3(1f, sy, 1f);
        }
    }

    static float Dip(float x, float wDown, float wUp)
    {
        x = x - Mathf.Round(x);
        float w = x < 0f ? wDown : wUp;
        return Mathf.Exp(-(x / w) * (x / w));
    }
    static float CanterCurve(float p)
    {
        return -0.55f * Dip(p - 0.00f, 0.035f, 0.09f) - 1.0f * Dip(p - 0.22f, 0.03f, 0.10f) - 0.75f * Dip(p - 0.40f, 0.03f, 0.12f)
               + 0.55f * Dip(p - 0.72f, 0.16f, 0.16f);
    }
    static float CanterPitch(float p) { return -Dip(p - 0.45f, 0.06f, 0.14f) + 0.4f * Dip(p - 0.05f, 0.08f, 0.12f); }
    static float WalkCurve(float p)
    {
        return -(Dip(p, 0.06f, 0.1f) + Dip(p - 0.25f, 0.06f, 0.1f) + Dip(p - 0.5f, 0.06f, 0.1f) + Dip(p - 0.75f, 0.06f, 0.1f)) * 0.5f + 0.35f;
    }

    // x = steer (D/Right +1, A/Left -1), y = forward (W/Up +1, S/Down -1)
    static Vector2 ReadInput()
    {
        Vector2 v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
        }
        var gp = Gamepad.current;
        if (gp != null && v == Vector2.zero) { var s = gp.leftStick.ReadValue(); if (s.sqrMagnitude > 0.04f) v = s; }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (v == Vector2.zero)
        {
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v.y += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v.y -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) v.x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) v.x -= 1f;
        }
#endif
        return new Vector2(Mathf.Clamp(v.x, -1f, 1f), Mathf.Clamp(v.y, -1f, 1f));
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
