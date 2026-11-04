// HorseRideRig - layered 2.5D parallax first-person horse ride, "dark fantasy nostalgia" (Witcher vibe).
// The scene objects (Sky, Background, Midground, Trees_Left, Road, Fog, Particles, Horse) are real GameObjects saved in
// Assets/Scenes/HorseRide.unity by Tools > Build Dark Fantasy Ride; at Play this rig only drives them (serialized references).
// If the scene has no baked objects (e.g. another scene) the rig builds the same hierarchy at runtime.
// Screen Space - Camera canvas so URP post-processing applies; a low-res pixel RT is created at Play.
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
    public enum Motion { Loop, Approach, Tunnel, Static }

    [System.Serializable]
    public class ZoomLayer
    {
        public string textureName = "T_Ride_Castle";
        public bool enabled = true;
        [Tooltip("Loop = endless zoom with two crossfading copies (near scenery). Approach = slowly grows with total distance ridden (far landmarks). Tunnel = 3 staggered copies that grow from the vanishing point and slide out past the screen edge (endless, gap-free).")]
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
        [Tooltip("Hierarchy group: Background, Midground or Trees_Left")] public string hierarchyGroup = "Midground";
        [Tooltip("Tunnel: number of staggered copies (2..3)")] [Range(2, 3)] public int tunnelCopies = 3;
        [HideInInspector] public RawImage a, b, c;
        [HideInInspector] public RectTransform group;
    }

    [Header("Control (W forward, D right; A left / S back also work)")]
    public bool requireInput = true;
    [Tooltip("Max forward playback speed (1 = reference video speed). Full speed = gallop.")] public float forwardSpeed = 1.3f;
    [Tooltip("Max backward playback speed")] public float backwardSpeed = 0.5f;
    [Tooltip("Holding D (or A) alone makes the horse walk while turning")] public bool steerWalks = true;
    [Range(0, 1)] public float steerWalkSpeed = 0.4f;
    [Tooltip("Max sideways pan in reference px for a layer with steerParallax = 1")] public float maxSteerPx = 260f;
    [Tooltip("How fast the steer value follows the key (per second)")] public float steerResponse = 0.9f;
    [Tooltip("When D is released the view slowly drifts back to centre")] public bool steerReturnsToCenter = true;
    public float steerReturnSpeed = 0.35f;
    [Tooltip("Road bend added at full steer")] public float pathSteerCurve = 0.32f;
    public float steerBankDegrees = 1.2f;

    [Header("Rendering")]
    [Tooltip("Low-res pixel render height. 0 = render straight at screen resolution (sharpest; point-filtered pixel art).")] public int pixelHeight = 0;
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
    [Tooltip("0 = fill whatever the Game view / screen is (any aspect, no black bars). >0 forces an aspect (width/height), e.g. 0.5625 = 9:16, 1.7778 = 16:9.")] public float forceAspect = 0f;

    [Tooltip("Only the road, the roadside pines and the horse move; sky, mountains, castle, cliffs and tall trees stay still")] public bool staticScenery = true;

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
    [Range(0, 1)] public float moonRayAlpha = 0.06f;

    [Header("Layers (back to front, excluding sky/path/horse)")]
    public List<ZoomLayer> layers = new List<ZoomLayer>
    {
        new ZoomLayer{ textureName="T_Ride_Mountains",   hierarchyGroup="Background", motion=Motion.Static, crossfade=false, bobAmount=0f, steerParallax=0f, tint=new Color(0.95f,0.93f,1f) },
        new ZoomLayer{ textureName="T_Ride_CastleHD",    hierarchyGroup="Background", motion=Motion.Static, crossfade=false, bobAmount=0f, steerParallax=0f },
        new ZoomLayer{ textureName="T_Ride_Valley",      hierarchyGroup="Midground",  motion=Motion.Static, crossfade=false, bobAmount=0f, steerParallax=0f },
        new ZoomLayer{ textureName="T_Ride_CliffsRight", hierarchyGroup="Midground",  motion=Motion.Static, crossfade=false, bobAmount=0f, steerParallax=0f },
        new ZoomLayer{ textureName="T_Ride_Spires",      hierarchyGroup="Midground",  motion=Motion.Static, crossfade=false, bobAmount=0f, steerParallax=0f },
        new ZoomLayer{ textureName="T_Ride_CliffLeftWide", hierarchyGroup="Trees_Left", motion=Motion.Static, crossfade=false, bobAmount=0f, steerParallax=0f, tint=new Color(0.92f,0.92f,0.98f) },
    };

    [Header("Fog (procedural dithered pixel fog)")]
    public bool buildFog = true;
    public Color fogColor = new Color(0.55f, 0.47f, 0.74f, 1f);
    public Color groundMistColor = new Color(0.50f, 0.55f, 0.70f, 1f);
    [Tooltip("Fog pixel grid (texture width in pixels)")] public int fogTexWidth = 240;
    [Tooltip("Haze sheet IN FRONT of each layer (same order as Layers)")] public float[] layerFogAlpha = { 0f, 0f, 0f, 0f, 0f, 0f };
    [Tooltip("Vertical centre of each haze band (0 bottom .. 1 top)")] public float[] layerFogCenter = { 0.47f, 0.385f, 0.31f, 0.26f, 0.22f, 0.2f };
    [Tooltip("Horizontal drift speed of the fog (uv per second)")] public float fogDrift = 0.008f;
    [Range(0, 1)] public float groundMistAlpha = 0f;
    [Range(0, 1)] public float foregroundWispAlpha = 0f;
    [Tooltip("Fog bank on the right side only: fills the bottom-right and rolls up to the foot of the right mountain")] public bool buildRightFog = true;
    [Range(0, 1)] public float rightFogAlpha = 0.9f;
    [Range(0, 1)] public float rightFogBackAlpha = 0.9f;
    [Tooltip("Fog is solid up to about here (screen fraction from bottom) = hides the foot of the right mountain")] public float rightFogTop = 0.44f;
    [Tooltip("Above the horizon the right fog starts at this design-x (keeps the castle clear and crisp)")] public float castleClearX = 0.84f;
    [Tooltip("Fog bank on the left side (over the bottom of the pines and the tall trees)")] public bool buildLeftFog = true;
    [Range(0, 1)] public float leftFogAlpha = 0.9f;
    [Tooltip("Left fog: solid up to fullTop, then fades smoothly up to top (screen fractions)")] public float leftFogFullTop = 0.20f, leftFogTop = 0.52f;
    public Color rightFogColor = new Color(0.47f, 0.41f, 0.67f, 1f);
    public Color leftFogColor = new Color(0.25f, 0.17f, 0.38f, 1f);
    [Tooltip("Slow sideways breathing of the fog bank (ref px); 0 = completely still")] public float rightFogSway = 0f;

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
    public float pathCurve = -0.10f;
    public float pathCurvePower = 2.2f;
    public Color pathTint = new Color(0.86f, 0.84f, 0.95f, 1f);
    [Tooltip("Path colour blends to this near the horizon (fog)")] public Color pathFarColor = new Color(0.30f, 0.26f, 0.45f, 1f);
    public float pathBobAmount = 0.9f;

    [Header("Pine rows along the road (perspective, endless)")]
    public bool buildPineRows = true;
    public bool pinesLeft = true, pinesRight = false;
    [Tooltip("Sprites Resources/Ride/<prefix>0.._DF (falls back to T_Ride_Pine)")] public string pineTexturePrefix = "T_Ride_Tree";
    [Tooltip("Clusters per side")] public int pinesPerSide = 9;
    [Tooltip("Overlapping trees per cluster (varied size and depth)")] [Range(1, 6)] public int pinesPerCluster = 4;
    [Tooltip("World distance between trees along the road (road width = pathWorldWidth)")] public float pineSpacing = 4.2f;
    [Tooltip("Distance of tree trunks from the road centre (world units)")] public Vector2 pineLateral = new Vector2(1.55f, 2.4f);
    [Tooltip("Tree height in world units (camera/rider eye height = cameraHeight = 1)")] public Vector2 pineHeight = new Vector2(3.4f, 6.5f);
    public float pineNear = 0.9f;
    [Tooltip("Pushes the trees' feet down into the ground (strip fraction) so they stand at road level")] public float pineSink = 0.04f;
    [Tooltip("Trees fade in from the fog beyond this distance")] public float pineFogStart = 6f, pineFogEnd = 34f;
    public Color pineNearTint = new Color(0.86f, 0.86f, 0.94f, 1f);
    public Color pineFogTint = new Color(0.36f, 0.31f, 0.55f, 1f);

    [Header("Horse (foreground)")]
    public bool buildHorse = true;
    [Tooltip("Horse height as fraction of screen height")] public float horseHeight = 0.42f;
    [Tooltip("Pivot y: lower = more of the horse pushed below the frame")] public float horsePivotY = 0.08f;
    public Color horseTint = Color.white;
    public float horseSteerPx = 34f, horseSteerDegrees = 3f;

    [Header("Gait (walk -> 3-beat canter -> 4-beat gallop, driven by speed)")]
    [Tooltip("Strides per second")] public float walkStrideHz = 0.95f, canterStrideHz = 1.7f, gallopStrideHz = 2.25f;
    [Tooltip("Speed fraction where walk blends into canter")] public Vector2 canterBlend = new Vector2(0.28f, 0.55f);
    [Tooltip("Speed fraction where canter blends into gallop")] public Vector2 gallopBlend = new Vector2(0.72f, 0.95f);
    [Tooltip("Seconds to ease up to / down from full speed")] public float speedUpTime = 1.4f, slowDownTime = 0.9f;
    [Tooltip("Vertical camera travel in px: walk / canter / gallop")] public float walkBobPx = 6f, canterBobPx = 26f, gallopBobPx = 34f;
    [Tooltip("Camera spring (Hz, damping) - smooths the gait curve, slight overshoot after impacts")] public float camSpringHz = 6f, camSpringDamping = 0.55f;
    public float pitchPx = 12f;
    public float gaitSwayPx = 9f;
    public float gaitRollDegrees = 0.9f;
    [Tooltip("Rider leans back / view lifts while accelerating (px per speed/s)")] public float surgePx = 14f;
    public float neckPumpPx = 20f;
    [Range(0, 1)] public float neckPhase = 0.5f;
    [Tooltip("Horse head spring (Hz, damping): lower = more lag behind the body")] public float headSpringHz = 3.2f, headSpringDamping = 0.45f;
    public float neckNodDegrees = 2.2f;
    [Header("Hoof impacts")]
    [Tooltip("Screen shake on each hoof strike (ref px at full gallop)")] public float impactShakePx = 7f;
    public float impactShakeDecay = 16f;
    public bool buildHoofDust = true;
    public Color hoofDustColor = new Color(0.62f, 0.55f, 0.62f, 0.55f);
    public float earTwitchDegrees = 1.2f;
    public Vector2 earTwitchInterval = new Vector2(1.5f, 4.5f);

    [Header("Smoke (procedural pixel puffs)")]
    public bool buildSmoke = false;
    [Tooltip("x,y = position inside the layer texture (0..1 from bottom-left), z = index into Layers, w = strength")]
    public Vector4[] smokeEmitters = { new Vector4(0.86f, 0.52f, 1, 1.2f), new Vector4(0.47f, 0.56f, 1, 0.7f), new Vector4(0.50f, 0.215f, 2, 0.6f), new Vector4(0.56f, 0.205f, 2, 0.5f), new Vector4(0.22f, 0.41f, 4, 0.6f), new Vector4(0.31f, 0.40f, 4, 0.5f) };
    public int puffsPerEmitter = 14;
    public Vector2 wind = new Vector2(0.9f, 1f);
    public float smokeRise = 70f, smokeLife = 5f, smokeSize = 10f;
    public Color smokeColor = new Color(0.56f, 0.50f, 0.66f, 0.6f);

    [Header("Particles")]
    [Tooltip("Fireflies and ash/dust as ParticleSystem components (off = legacy UI sprites)")] public bool useParticleSystems = true;
    [Tooltip("Particle material (the builder saves one to Assets/Ride/Generated)")] public Material particleMaterial;
    [Range(0, 50)] public int particleSortingOrder = 20;
    public bool buildFireflies = false;
    public int fireflyCount = 46;
    public Color fireflyColor = new Color(1f, 0.86f, 0.45f, 1f);
    public float fireflySize = 9f;
    public bool buildDust = false;
    public int dustCount = 70;
    public Color dustColor = new Color(0.80f, 0.74f, 0.95f, 0.55f);
    public float dustSize = 5f;
    public bool buildSnow = false;
    public int snowCount = 70;
    public bool buildCrows = false;
    public int crowCount = 4;

    // ------------------------------------------------------------------ state
    // Scene objects are serialized (baked by the editor builder); simulation state is runtime only.
    [System.Serializable] public class Pine { public RawImage img; public int side, slot, sub; [System.NonSerialized] public float depth; }
    [System.Serializable] public class Dust { public RawImage img; public float age = 99f, life, side; public Vector2 vel, pos; }
    [System.Serializable] public class Mote { public RawImage img; public Vector3 p; public float seed, size, speed; public bool firefly; }
    [System.Serializable] public class Crow { public RawImage img; public float x, y, speed, phase, scale; }
    [System.Serializable] public class Puff { public RawImage img; public float phase, seed, strength; public int layer, copy; public Vector2 uv; }
    [System.Serializable] public class FogSheet { public RawImage img; public float speed, steer, sway, seed; public bool scroll; }

    [SerializeField, HideInInspector] bool _built;
    [SerializeField, HideInInspector] Canvas _canvas;
    [SerializeField, HideInInspector] RectTransform _root, _world, _skyGroup, _pineGroup;
    [SerializeField, HideInInspector] RawImage _sky, _path, _horse, _rays, _moonGlow;
    [SerializeField, HideInInspector] List<Pine> _pines = new List<Pine>();
    [SerializeField, HideInInspector] List<Texture2D> _pineTex = new List<Texture2D>();
    [SerializeField, HideInInspector] List<Dust> _dust = new List<Dust>();
    [SerializeField, HideInInspector] List<FogSheet> _fogSheets = new List<FogSheet>();
    [SerializeField, HideInInspector] List<Mote> _motes = new List<Mote>();
    [SerializeField, HideInInspector] List<Crow> _crows = new List<Crow>();
    [SerializeField, HideInInspector] List<Puff> _puffs = new List<Puff>();
    [SerializeField, HideInInspector] List<ParticleSystem> _particles = new List<ParticleSystem>();
    [SerializeField, HideInInspector] Texture2D _crowUp, _crowDown;

    Texture2D _pathOut, _pathSrc;
    Color32[] _src, _dst;
    int _sw, _sh;
    float _t, _speed, _speedVel, _accel, _bobAmp, _gaitW, _gallopW, _stride, _prevPh, _earTimer = 2f, _earT = 1f, _steer, _distance;
    float _camY, _camV, _headY, _headV, _shake, _pathCurveNow;
    Camera _cam, _displayCam;
    GameObject _displayRoot;
    RenderTexture _rt;

    // aspect handling: the 9:16 design area (world) stays centred; everything that must reach the screen edges extends sideways
    float _aspect = 0.5625f, _extraPx;     // extra canvas px on each side beyond the 1080-wide design area
    const float WorldW = 1160f;            // design width + 2*40 bob margin
    const float MaxAspect = 2.4f;          // fog / rays are generated wide enough for 21:9
    float LayerExt => (Mathf.Max(0f, overscan) * referenceResolution.x + _extraPx) / WorldW;   // layer rect extension per side, in world widths
    float EdgeExt => (_extraPx + 60f) / WorldW;                                                 // path extension per side
    float MaxExt => (Mathf.Max(0f, (referenceResolution.y * MaxAspect - referenceResolution.x) * 0.5f) + 60f) / WorldW;
    readonly Dictionary<Texture2D, bool> _edgeOpaque = new Dictionary<Texture2D, bool>();

    public bool IsBuilt => _built && _canvas && _world && _sky;
    public RenderTexture PixelTarget => _rt;
    public Camera RenderCamera => _cam;
    public RawImage PathImage => _path;
    public IReadOnlyList<ParticleSystem> Particles => _particles;

    void Start() { if (Application.isPlaying) BeginRuntime(); }

    /// <summary>Takes over the baked scene objects (or builds them if missing), fits them to the screen aspect,
    /// creates the pixel render target and starts driving everything. Also used by the editor preview renderer.</summary>
    public void BeginRuntime()
    {
        if (!IsBuilt) { ClearHierarchy(); Build(); }
        ConfigureCanvasCamera();
        SetupVolume();
        Relayout();
        SetupPixelDisplay();
        foreach (var ps in _particles) if (ps) { ps.Clear(true); ps.Play(true); }
        Tick(0f);
    }

    /// <summary>Removes the runtime-only display (pixel RT, display camera); scene objects stay.</summary>
    public void EndRuntime()
    {
        if (_cam && _cam.targetTexture == _rt) _cam.targetTexture = null;
        if (_rt) { _rt.Release(); DestroyObj(_rt); _rt = null; }
        if (_displayRoot) DestroyObj(_displayRoot);
        _displayRoot = null; _displayCam = null;
    }

    static void DestroyObj(Object o) { if (!o) return; if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }

    /// <summary>Deletes every object the rig made (scene objects included) and clears the references.</summary>
    public void ClearHierarchy()
    {
        EndRuntime();
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var go = transform.GetChild(i).gameObject;
            go.SetActive(false);
            DestroyObj(go);
        }
        _fogSheets.Clear(); _motes.Clear(); _crows.Clear(); _puffs.Clear(); _pines.Clear(); _pineTex.Clear(); _dust.Clear(); _particles.Clear();
        _sky = _path = _horse = _rays = _moonGlow = null; _pineGroup = null; _canvas = null; _root = _world = _skyGroup = null;
        foreach (var L in layers) { L.a = L.b = L.c = null; L.group = null; }
        _pathOut = null; _built = false;
    }

    /// <summary>Destroys everything and builds again.</summary>
    public void Rebuild() { ClearHierarchy(); Build(); if (Application.isPlaying) BeginRuntime(); }

    void OnDestroy() { EndRuntime(); }

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

    // Layer rect = world rect widened by overscan + the extra screen width; pivot at the vanishing point so scaling zooms toward it.
    // Textures may be wider than 9:16 (e.g. the 3x sky): the centre 9:16 part is the design area, the rest fills wide screens.
    // Beyond the texture: transparent-edged sprites clamp to nothing, opaque edges mirror (no stretching, no hard edge).
    void SetupLayerRect(RawImage img)
    {
        var rt = img.rectTransform;
        float e = LayerExt;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-e * WorldW, 0); rt.offsetMax = new Vector2(e * WorldW, 0);
        rt.pivot = new Vector2((vanishingPoint.x + e) / (1f + 2f * e), vanishingPoint.y);
        float k = 1f;
        if (img.texture is Texture2D t)
        {
            float designW = t.height * referenceResolution.x / referenceResolution.y;
            k = Mathf.Min(1f, designW / t.width);
            t.wrapModeU = EdgesOpaque(t) ? TextureWrapMode.Mirror : TextureWrapMode.Clamp; t.wrapModeV = TextureWrapMode.Clamp;
        }
        img.uvRect = new Rect(0.5f - (0.5f + e) * k, 0f, (1f + 2f * e) * k, 1f);
    }

    // design-space u (0..1 across the world rect) -> anchor inside a layer rect made by SetupLayerRect
    float LayerAnchorX(float u) { float e = LayerExt; return (u + e) / (1f + 2f * e); }

    bool EdgesOpaque(Texture2D t)
    {
        if (_edgeOpaque.TryGetValue(t, out bool v)) return v;
        v = true;
        try
        {
            int n = 0, H = t.height;
            for (int y = 0; y < H; y += 2) { if (t.GetPixel(0, y).a > 0.1f) n++; if (t.GetPixel(t.width - 1, y).a > 0.1f) n++; }
            v = n > 4;
        }
        catch { }
        _edgeOpaque[t] = v; return v;
    }

    // ------------------------------------------------------------------ build
    void ConfigureCanvasCamera()
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
        if (_canvas)
        {
            _canvas.renderMode = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera = _cam;
            _canvas.planeDistance = Mathf.Clamp(5f, _cam.nearClipPlane + 0.1f, _cam.farClipPlane - 0.1f);
        }
    }

    // Runtime only: the main camera renders into a low-res RT with the screen's aspect; a display camera + overlay canvas
    // shows it upscaled with hard pixels, filling the whole screen.
    void SetupPixelDisplay()
    {
        EndRuntime();
        if (pixelHeight <= 0 || !_cam) return;
        int h = pixelHeight, w = Mathf.Max(16, Mathf.RoundToInt(h * _aspect));
        _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "RidePixelRT", filterMode = FilterMode.Point, antiAliasing = 1 };
        _rt.Create();
        _cam.targetTexture = _rt;

        _displayRoot = new GameObject("RideDisplay (runtime)");
        _displayRoot.hideFlags = HideFlags.DontSave;
        _displayRoot.transform.SetParent(transform, false);
        var dgo = new GameObject("RideDisplayCamera", typeof(Camera));
        dgo.transform.SetParent(_displayRoot.transform, false);
        _displayCam = dgo.GetComponent<Camera>();
        _displayCam.clearFlags = CameraClearFlags.SolidColor; _displayCam.backgroundColor = Color.black;
        _displayCam.cullingMask = 0; _displayCam.depth = _cam.depth + 10;
        var dd = _displayCam.GetUniversalAdditionalCameraData();
        if (dd != null) dd.renderPostProcessing = false;

        var ogo = new GameObject("RideDisplayUI", typeof(Canvas), typeof(CanvasScaler)); ogo.layer = 5;
        ogo.transform.SetParent(_displayRoot.transform, false);
        var oc = ogo.GetComponent<Canvas>(); oc.renderMode = RenderMode.ScreenSpaceOverlay; oc.sortingOrder = 100;
        var img = MakeImage("Frame", _rt, (RectTransform)ogo.transform); img.color = Color.white;
        var fit = img.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; fit.aspectRatio = _aspect;
    }

    float ComputeAspect()
    {
        if (forceAspect > 0f) return forceAspect;
        return Screen.height > 0 ? (float)Screen.width / Screen.height : 0.5625f;
    }

    /// <summary>Fits the (baked) objects to the current aspect: layer widths, anchors, road strip.</summary>
    public void Relayout()
    {
        _aspect = ComputeAspect();
        _extraPx = Mathf.Max(0f, (referenceResolution.y * _aspect - referenceResolution.x) * 0.5f);
        if (_sky) SetupLayerRect(_sky);
        foreach (var L in layers) { if (L.a) SetupLayerRect(L.a); if (L.b) SetupLayerRect(L.b); if (L.c) SetupLayerRect(L.c); }
        if (_moonGlow) _moonGlow.rectTransform.anchorMin = _moonGlow.rectTransform.anchorMax = new Vector2(LayerAnchorX(moonPos.x), moonPos.y);
        foreach (var p in _puffs) if (p.img) p.img.rectTransform.anchorMin = p.img.rectTransform.anchorMax = new Vector2(LayerAnchorX(p.uv.x), p.uv.y);
        SetupPathStrip();
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

    readonly Dictionary<string, RectTransform> _groups = new Dictionary<string, RectTransform>();
    RectTransform Group(string key) { return _groups.TryGetValue(key, out var g) && g ? g : _world; }

    /// <summary>Creates the whole scene hierarchy (works in Edit mode: the builder saves it into the scene).</summary>
    public void Build()
    {
        if (IsBuilt) return;
        _aspect = ComputeAspect();
        _extraPx = Mathf.Max(0f, (referenceResolution.y * _aspect - referenceResolution.x) * 0.5f);
        var canvasGo = new GameObject("RideCanvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.layer = 5;
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.GetComponent<Canvas>();
        ConfigureCanvasCamera();
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; cs.referenceResolution = referenceResolution; cs.matchWidthOrHeight = 1f;

        var bg = MakeImage("00_BlackBackdrop", null, (RectTransform)canvasGo.transform); bg.color = Color.black;
        var frame = new GameObject("Frame", typeof(RectTransform), typeof(RectMask2D)); frame.layer = 5;
        _root = (RectTransform)frame.transform; _root.SetParent(canvasGo.transform, false);
        _root.anchorMin = Vector2.zero; _root.anchorMax = Vector2.one; _root.offsetMin = _root.offsetMax = Vector2.zero;   // whole screen
        // 9:16 design area (+40 px bob margin), centred; wider screens are filled by the layers' sideways extension
        _world = MakeGroup("World", _root);
        _world.anchorMin = _world.anchorMax = new Vector2(0.5f, 0.5f); _world.sizeDelta = referenceResolution + new Vector2(80f, 80f);

        // draw order = sibling order (back to front)
        _groups.Clear();
        string[] order = { "Sky", "Background", "Midground", "Trees_Left", "Road", "Fog", "Particles" };
        for (int i = 0; i < order.Length; i++) _groups[order[i]] = MakeGroup((i + 1).ToString("00") + "_" + order[i], _world);

        // Sky + moon glow + crows
        _skyGroup = Group("Sky");
        _sky = MakeImage("Sky", Load("T_Ride_Sky"), _skyGroup); SetupLayerRect(_sky); _sky.color = skyTint;
        if (buildMoonGlow) BuildMoonGlow();
        if (buildCrows) BuildCrows();

        for (int i = 0; i < layers.Count; i++)
        {
            var L = layers[i];
            L.a = L.b = L.c = null; L.group = null;
            if (!L.enabled) continue;
            var tex = Load(L.textureName);
            if (!tex) { Debug.LogWarning("HorseRideRig: missing Resources/Ride/" + L.textureName); continue; }
            var parent = Group(string.IsNullOrEmpty(L.hierarchyGroup) ? "Midground" : L.hierarchyGroup);
            string nice = L.textureName.Replace("T_Ride_", "");
            if (nice.StartsWith("CliffLeft")) nice = "TallTrees_Left";
            if (nice == "CastleHD") nice = "Castle";
            L.group = MakeGroup(nice, parent);
            L.a = MakeImage(nice + "_A", tex, L.group); SetupLayerRect(L.a);
            bool second = (L.motion == Motion.Loop && L.crossfade) || L.motion == Motion.Tunnel;
            if (second) { L.b = MakeImage(nice + "_B", tex, L.group); SetupLayerRect(L.b); }
            if (L.motion == Motion.Tunnel && L.tunnelCopies >= 3) { L.c = MakeImage(nice + "_C", tex, L.group); SetupLayerRect(L.c); }
            if (buildMoonRays && L.textureName.Contains("Castle")) { BuildMoonRays(parent); _rays.transform.SetSiblingIndex(L.group.GetSiblingIndex()); }   // rays behind the castle: castle stays crisp
            if (buildFog && buildRightFog && rightFogBackAlpha > 0f && L.textureName.Contains("Valley"))
                AddSideFog("Fog_Right_Back", parent, 1, rightFogTop, rightFogTop + 0.10f, rightFogBackAlpha, 1.3f, 401, rightFogColor, 0f);
            if (buildFog && i < layerFogAlpha.Length && layerFogAlpha[i] > 0f)
            {
                float cy = i < layerFogCenter.Length ? layerFogCenter[i] : 0.3f;
                AddFogSheet("Haze_" + nice, parent, cy, 0.055f + i * 0.012f, layerFogAlpha[i], fogDrift * (1f + i * 0.6f), i * 37 + 3, fogColor, 0.05f + L.steerParallax);
            }
        }

        if (buildSmoke) BuildSmoke();
        if (buildPineRows) BuildPines();
        if (buildPath) BuildPath();
        if (buildFog && buildRightFog && rightFogAlpha > 0f) AddSideFog("Fog_Right_Front", Group("Fog"), 1, rightFogTop - 0.16f, rightFogTop + 0.12f, rightFogAlpha, 1f, 977, rightFogColor);
        if (buildFog && buildLeftFog && leftFogAlpha > 0f) AddSideFog("Fog_Left_Front", Group("Fog"), -1, leftFogFullTop, leftFogTop, leftFogAlpha, 1f, 613, leftFogColor);
        if (buildFog && groundMistAlpha > 0f) AddFogSheet("GroundMist", Group("Fog"), 0.30f, 0.07f, groundMistAlpha, fogDrift * 3f, 911, groundMistColor, 0.8f);
        if (buildHoofDust) BuildHoofDust();
        if (useParticleSystems) BuildParticleSystems(); else BuildMotes();
        if (buildFog && foregroundWispAlpha > 0f) AddFogSheet("ForegroundWisps", Group("Fog"), 0.03f, 0.06f, foregroundWispAlpha, fogDrift * 5f, 517, groundMistColor, 1f);

        if (buildHorse)
        {
            var htex = Load("T_Ride_Horse");
            if (htex)
            {
                _horse = MakeImage("08_Horse", htex, _root); _horse.color = horseTint;
                var rt = _horse.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, horsePivotY);
                float h = referenceResolution.y * horseHeight;
                rt.sizeDelta = new Vector2(h * htex.width / (float)htex.height, h);
            }
        }
        _built = true;
        Relayout();
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
        t.SetPixels32(px); t.Apply(false); t.name = "Gen_MoonGlow";
        var img = MakeImage("MoonGlow", t, _sky.rectTransform); img.color = moonGlowColor; _moonGlow = img;
        var rt = img.rectTransform;
        // moonPos is in texture space; the sky rect has mirrored overscan, convert to rect space
        var p = new Vector2(LayerAnchorX(moonPos.x), moonPos.y);
        rt.anchorMin = rt.anchorMax = p; rt.sizeDelta = Vector2.one * moonGlowSize; rt.pivot = new Vector2(0.5f, 0.5f);
    }

    void BuildMoonRays(RectTransform parent)
    {
        float ex = MaxExt;
        int H = 240, W = Mathf.RoundToInt(135 * (1f + 2f * ex));
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[W * H];
        var src = new Vector2((moonPos.x + ex) / (1f + 2f * ex) * W, moonPos.y * H);
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
        t.SetPixels32(px); t.Apply(false); t.name = "Gen_MoonRays";
        _rays = MakeImage("MoonRays", t, parent); _rays.color = new Color(1, 1, 1, moonRayAlpha);
        _rays.rectTransform.offsetMin = new Vector2(-ex * WorldW, 0); _rays.rectTransform.offsetMax = new Vector2(ex * WorldW, 0);
    }

    void BuildCrows()
    {
        _crowUp = CrowTex(true); _crowDown = CrowTex(false); _crowUp.name = "Gen_CrowUp"; _crowDown.name = "Gen_CrowDown";
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
    void AddFogSheet(string name, RectTransform parent, float centerY, float height, float alpha, float speed, int seed, Color col, float steer)
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
        t.SetPixels32(px); t.Apply(false); t.name = "Gen_Fog_" + name;
        var img = MakeImage(name, t, parent); img.color = new Color(1, 1, 1, alpha);
        float ex = MaxExt;
        img.rectTransform.offsetMin = new Vector2(-ex * WorldW, 0); img.rectTransform.offsetMax = new Vector2(ex * WorldW, 0);
        img.uvRect = new Rect(0f, 0f, 1f + 2f * ex, 1f);
        _fogSheets.Add(new FogSheet { img = img, speed = speed, steer = steer, scroll = true });
    }

    // Fog bank beside the road (side +1 right, -1 left). It touches the road edge (no gap), follows the road perspective,
    // is densest near the camera and thins with distance, and above fullTop breaks up into wisps that fade smoothly upward.
    void AddSideFog(string name, RectTransform parent, int side, float fullTop, float top, float alpha, float density, int seed, Color col, float falloff = 1f)
    {
        float ex = MaxExt, span = 1f + 2f * ex;
        int H = Mathf.Max(32, fogTexWidth) * 16 / 9 / 2, W = Mathf.RoundToInt(H * 9f / 16f * 2f * span);   // ~4 ref px per fog pixel
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = filter };
        var px = new Color32[W * H];
        Color32 fc = col;
        float aspect = (float)pathTexWidth / pathTexHeight;
        float hz = pathHorizon * pathScreenFraction;
        const int Levels = 10;
        for (int y = 0; y < H; y++)
        {
            float fy = (y + 0.5f) / H;                                   // 0 bottom .. 1 top of the world rect
            float dyRaw = pathHorizon - fy / Mathf.Max(0.05f, pathScreenFraction);
            float dy = Mathf.Clamp(dyRaw, 0f, 1.5f);                     // 0 at the horizon .. 1 at the bottom (near)
            float curve = pathCurve * Mathf.Pow(1f - Mathf.Clamp01(dy), pathCurvePower);
            float half = (pathWorldWidth * 0.5f * dy) / aspect;
            // inner edge sits a little INSIDE the road's grass verge so fog and road meet without a dark gap
            float roadSide = vanishingPoint.x + curve + side * half * 0.86f;
            float above = Smooth(hz - 0.07f, hz + 0.10f, fy);
            float edge = Mathf.Lerp(roadSide, side > 0 ? castleClearX : vanishingPoint.x - 0.16f, above);
            float soft = Mathf.Lerp(0.02f + 0.05f * dy, 0.26f, above);
            float nearK = Mathf.Lerp(1f, Mathf.Lerp(0.5f, 1f, Smooth(0f, 0.9f, dy)) * (dyRaw < 0f ? 0.5f : 1f), falloff);   // densest near the camera
            for (int x = 0; x < W; x++)
            {
                float xd = (x + 0.5f) / W * span - ex;                       // design-space x
                float eN = edge + side * (Mathf.PerlinNoise(seed * 0.17f + fy * 6f, xd * 2f) - 0.5f) * soft * 0.8f;   // ragged, not ruler-straight
                float horiz = side > 0 ? Smooth(eN, eN + soft, xd) : 1f - Smooth(eN - soft, eN, xd);
                float n = 0f, amp = 0.55f, f = 1f;
                for (int o = 0; o < 4; o++) { n += amp * Mathf.PerlinNoise(seed + xd * 5f * f, seed * 0.41f + fy * 7f * f); amp *= 0.5f; f *= 2f; }
                float w2 = Mathf.PerlinNoise(seed * 0.7f + xd * 2.2f, fy * 3.5f + seed);                  // big wisps
                float ftN = fullTop + (w2 - 0.5f) * 0.08f;
                float u = Mathf.Clamp01((fy - ftN) / Mathf.Max(0.02f, top - ftN));
                float vert = 1f - u * u * u * (u * (u * 6f - 15f) + 10f);    // smootherstep: very soft upward fade
                vert *= Mathf.Lerp(1f, Mathf.Clamp01(w2 * 1.7f - 0.25f), Smooth(0f, 0.7f, u));        // higher up it disperses into wisps
                float d = Mathf.Clamp01(horiz * vert * nearK * density * (0.8f + (n - 0.5f) * 0.9f));
                float q = d * Levels + Bayer(x, y) - 0.5f;
                int lvl = Mathf.Clamp(Mathf.FloorToInt(q + 0.5f), 0, Levels);
                px[y * W + x] = new Color32(fc.r, fc.g, fc.b, (byte)(lvl * 255 / Levels));
            }
        }
        t.SetPixels32(px); t.Apply(false); t.name = "Gen_Fog_" + name;
        var img = MakeImage(name, t, parent); img.color = new Color(1, 1, 1, alpha);
        img.rectTransform.offsetMin = new Vector2(-ex * WorldW, 0); img.rectTransform.offsetMax = new Vector2(ex * WorldW, 0);
        _fogSheets.Add(new FogSheet { img = img, speed = 0f, steer = 0f, sway = rightFogSway, seed = seed, scroll = false });
    }

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
        var tex = MakePuffTex(); tex.name = "Gen_SmokePuff";
        foreach (var e in smokeEmitters)
        {
            int li = (int)e.z; if (li < 0 || li >= layers.Count) continue;
            var L = layers[li]; if (!L.enabled || !L.a) continue;
            var anchor = new Vector2(LayerAnchorX(e.x), e.y);
            var hosts = new[] { L.a, L.b, L.c };
            for (int hi = 0; hi < hosts.Length; hi++)
            {
                var host = hosts[hi];
                if (!host) continue;
                for (int i = 0; i < puffsPerEmitter; i++)
                {
                    var img = MakeImage("Smoke", tex, host.rectTransform);
                    var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
                    _puffs.Add(new Puff { img = img, phase = (float)i / puffsPerEmitter, seed = Random.value * 100f, strength = e.w, layer = li, copy = hi, uv = new Vector2(e.x, e.y) });
                }
            }
        }
    }

    void UpdateSmoke()
    {
        float life = Mathf.Max(0.5f, smokeLife);
        float tNow = Application.isPlaying ? Time.time : _t;
        foreach (var p in _puffs)
        {
            if (!p.img) continue;
            float a = Mathf.Repeat(tNow / life + p.phase, 1f);
            float wob = Mathf.Sin((tNow + p.seed) * 1.3f) * 4f * a;
            var rt = p.img.rectTransform;
            rt.anchoredPosition = new Vector2((wind.x * smokeRise * a * a * 1.6f + wob), (wind.y * smokeRise * a)) * p.strength;
            float sz = smokeSize * p.strength * (0.6f + a * 2.4f);
            sz = Mathf.Round(sz / 4f) * 4f;
            rt.sizeDelta = new Vector2(sz, sz);
            if (!p.img || p.layer < 0 || p.layer >= layers.Count) continue;
            var owner = layers[p.layer];
            var host = p.copy == 2 ? owner.c : p.copy == 1 ? owner.b : owner.a;
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
        var ff = MakeGlowDot(7, 0.3f); ff.name = "Gen_FireflyDot";
        var dust = MakeGlowDot(2, 1f); dust.name = "Gen_DustDot";
        int total = (buildFireflies ? fireflyCount : 0) + (buildDust ? dustCount : 0) + (buildSnow ? snowCount : 0);
        for (int i = 0; i < total; i++)
        {
            bool isFF = buildFireflies && i < fireflyCount;
            bool isSnow = !isFF && buildSnow && i >= total - snowCount;
            var img = MakeImage(isFF ? "Firefly" : isSnow ? "Snow" : "Dust", isFF ? ff : dust, Group("Particles"));
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
        float widen = (referenceResolution.x + 2f * _extraPx) / referenceResolution.x;
        foreach (var m in _motes)
        {
            if (!m.img) continue;
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
            Vector2 pos = new Vector2(m.p.x * size.x * 0.32f * widen, m.p.y * size.y * 0.22f) * inv;
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
            if (!c.img) continue;
            c.x += c.speed * dt; c.phase += dt * 2.2f;
            if (c.x > 1.2f) RespawnCrow(c, false);
            var rt = c.img.rectTransform;
            rt.anchoredPosition = new Vector2(c.x * (WorldW + 2f * _extraPx) - _extraPx, (c.y + Mathf.Sin(c.phase * 0.7f) * 0.01f) * referenceResolution.y);
            rt.sizeDelta = new Vector2(9f, 4f) * 4f * c.scale;
            c.img.texture = Mathf.Repeat(c.phase, 1f) < 0.5f ? _crowUp : _crowDown;
        }
    }

    // ---------------------------------------------------------------- pine rows
    void BuildPines()
    {
        for (int v = 0; v < 8; v++) { var t = Load(pineTexturePrefix + v); if (t) { t.wrapMode = TextureWrapMode.Clamp; t.filterMode = filter; _pineTex.Add(t); } }
        if (_pineTex.Count == 0) for (int v = 0; v < 8; v++) { var t = Load("T_Ride_Pine" + v); if (t) { t.wrapMode = TextureWrapMode.Clamp; t.filterMode = filter; _pineTex.Add(t); } }
        if (_pineTex.Count == 0) { Debug.LogWarning("HorseRideRig: no Resources/Ride/" + pineTexturePrefix + "*_DF textures"); return; }
        // same rect as the road strip so trees share the road's perspective mapping
        _pineGroup = MakeGroup("Pines_Left", Group("Trees_Left"));
        _pineGroup.anchorMin = Vector2.zero; _pineGroup.anchorMax = new Vector2(1f, pathScreenFraction);
        _pineGroup.offsetMin = _pineGroup.offsetMax = Vector2.zero; _pineGroup.pivot = new Vector2(vanishingPoint.x, 0f);
        for (int side = -1; side <= 1; side += 2)
        {
            if (side < 0 && !pinesLeft || side > 0 && !pinesRight) continue;
            for (int i = 0; i < pinesPerSide; i++)
            {
                var cluster = MakeGroup((side < 0 ? "Cluster_L" : "Cluster_R") + i.ToString("00"), _pineGroup);
                for (int k = 0; k < Mathf.Max(1, pinesPerCluster); k++)
                {
                    var img = MakeImage("Pine" + k, _pineTex[(i + k) % _pineTex.Count], _pineGroup);
                    img.transform.SetParent(cluster, false);
                    var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = new Vector2(0.5f, 0f);
                    _pines.Add(new Pine { img = img, side = side, slot = i, sub = k });
                }
            }
        }
    }

    static float Hash(int n, int side) { uint h = (uint)(n * 374761393 + side * 668265263); h = (h ^ (h >> 13)) * 1274126177u; return ((h ^ (h >> 16)) & 0xFFFF) / 65535f; }

    void UpdatePines(float travel, Vector2 bob)
    {
        if (_pineGroup == null || _pines.Count == 0) return;
        _pineGroup.anchoredPosition = bob * pathBobAmount;
        float stripPx = (referenceResolution.y + 80f) * pathScreenFraction;
        float stripW = referenceResolution.x + 80f;
        float aspect = (float)pathTexWidth / pathTexHeight;
        float baseIdx = Mathf.Floor(travel / pineSpacing);
        float frac = travel / pineSpacing - baseIdx;
        foreach (var p in _pines)
        {
            if (!p.img) continue;
            int worldIdx = (int)baseIdx + p.slot;                          // identity of the cluster along the endless road
            int id = worldIdx * 8 + p.sub;                                 // identity of the tree inside the cluster
            float stagger = p.side > 0 ? 0.5f : 0f;
            float r1 = Hash(id, p.side), r2 = Hash(id * 7 + 3, p.side), r3 = Hash(id * 13 + 5, p.side), r4 = Hash(id * 29 + 11, p.side);
            // cluster: the first tree stands at the road edge, the others behind / outward, overlapping
            float z = pineNear + (p.slot + stagger - frac) * pineSpacing + (p.sub == 0 ? 0f : (0.25f + r4 * 0.9f) * pineSpacing * 0.45f);
            p.depth = z;
            if (z <= 0.05f) { p.img.enabled = false; continue; }
            float dy = cameraHeight / z;                                   // fraction of strip height below the horizon
            float yStrip = pathHorizon - dy - pineSink;                    // 0..1 inside the strip (can go below 0 = off screen); planted on the ground
            float vanish = Mathf.Lerp(pathVanishX, 0f, Mathf.Clamp01(dy)) + _pathCurveNow * Mathf.Pow(1f - Mathf.Clamp01(dy), pathCurvePower);
            float lateral = Mathf.Lerp(pineLateral.x, pineLateral.y, r1 * 0.4f) + p.sub * 0.42f + r4 * 0.35f;
            float xf = vanishingPoint.x + vanish + p.side * (lateral / z) / aspect;
            float h = Mathf.Lerp(pineHeight.x, pineHeight.y, r2) * (p.sub == 0 ? 1f : Mathf.Lerp(0.6f, 1.15f, r4)) / z * stripPx;
            var tex = _pineTex[(int)(r3 * _pineTex.Count) % _pineTex.Count];
            float w = h * tex.width / tex.height;
            bool visible = yStrip > -0.05f - h / stripPx && xf * stripW + w > -40f - _extraPx && xf * stripW - w < stripW + 40f + _extraPx;
            p.img.enabled = visible;
            if (!visible) continue;
            p.img.texture = tex;
            var rt = p.img.rectTransform;
            rt.anchoredPosition = new Vector2(Mathf.Round(xf * stripW / 3f) * 3f, Mathf.Round(yStrip * stripPx / 3f) * 3f);
            rt.sizeDelta = new Vector2(Mathf.Round(w / 3f) * 3f, Mathf.Round(h / 3f) * 3f);
            rt.localScale = new Vector3(r1 > 0.5f ? -1f : 1f, 1f, 1f) ;   // mirror half of them for variety
            float fog = Smooth(pineFogStart, pineFogEnd, z);
            var c = Color.Lerp(pineNearTint, pineFogTint, fog);
            c.a = 1f - Smooth(pineFogEnd - 4f, pineFogEnd + 2f, z);
            p.img.color = c;
        }
        // far trees first, near trees on top
        _pines.RemoveAll(q => q == null || !q.img);
        // clusters back to front, and inside a cluster far trees first (near ones overlap them)
        foreach (var p in _pines) if (p.img && p.img.transform.parent != _pineGroup)
            p.img.transform.parent.SetSiblingIndex(Mathf.Clamp(pinesPerSide - 1 - p.slot + (p.side > 0 ? pinesPerSide : 0), 0, _pineGroup.childCount - 1));
        _pines.Sort((a, b) => b.depth.CompareTo(a.depth));
        for (int i = 0; i < _pines.Count; i++) _pines[i].img.transform.SetAsLastSibling();
    }

    // ---------------------------------------------------------------- particle systems (fireflies, ash)
    Material MakeParticleMaterial()
    {
        if (particleMaterial) return particleMaterial;
        var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (!sh) sh = Shader.Find("Sprites/Default");
        var dot = MakeGlowDot(8, 0.35f); dot.name = "Gen_ParticleDot";
        var m = new Material(sh) { name = "M_RideParticles" };
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", dot);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", dot);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Surface"))
        {
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        m.renderQueue = 3000;
        particleMaterial = m;
        return m;
    }

    ParticleSystem MakeParticles(string name, Color col, Vector2 size, Vector2 life, float rate, int max, float radial, float down, float noise, Vector3 box, Vector3 boxOffset, float grow)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(ParticleSystem)); go.layer = 5;
        var rt = (RectTransform)go.transform; rt.SetParent(Group("Particles"), false);
        rt.anchorMin = rt.anchorMax = vanishingPoint; rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
        var ps = go.GetComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = true; main.prewarm = true; main.playOnAwake = true;
        main.duration = 5f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
        main.startColor = col;
        main.maxParticles = max;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        var em = ps.emission; em.rateOverTime = rate;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = box; sh.position = boxOffset;
        var vel = ps.velocityOverLifetime; vel.enabled = true; vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(0f); vel.y = new ParticleSystem.MinMaxCurve(-down); vel.z = new ParticleSystem.MinMaxCurve(0f);
        vel.radial = new ParticleSystem.MinMaxCurve(radial);
        if (noise > 0f) { var nz = ps.noise; nz.enabled = true; nz.strength = noise; nz.frequency = 0.35f; nz.scrollSpeed = 0.15f; nz.quality = ParticleSystemNoiseQuality.Medium; }
        var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, grow)));
        var col2 = ps.colorOverLifetime; col2.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
        col2.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard; r.sharedMaterial = MakeParticleMaterial(); r.sortingOrder = particleSortingOrder;
        r.minParticleSize = 0f; r.maxParticleSize = 1f;
        _particles.Add(ps);
        ps.Play(true);
        return ps;
    }

    void BuildParticleSystems()
    {
        float w = WorldW * 1.6f;
        if (buildFireflies) MakeParticles("Fireflies", fireflyColor, new Vector2(fireflySize * 0.7f, fireflySize * 1.3f), new Vector2(5f, 8f), fireflyCount / 6f, fireflyCount + 20, 28f, 0f, 30f, new Vector3(w, 900f, 1f), new Vector3(0f, -350f, 0f), 1.6f);
        if (buildDust) MakeParticles("Ash_Dust", dustColor, new Vector2(dustSize * 0.6f, dustSize * 1.4f), new Vector2(3f, 4.5f), dustCount / 3.5f, dustCount + 30, 150f, 18f, 8f, new Vector3(w, 1900f, 1f), Vector3.zero, 1.8f);
        if (buildSnow) MakeParticles("Snow", new Color(0.92f, 0.92f, 1f, 0.8f), new Vector2(5f, 9f), new Vector2(4f, 6f), snowCount / 4f, snowCount + 30, 90f, 80f, 10f, new Vector3(w, 1900f, 1f), Vector3.zero, 1.5f);
    }

    // ---------------------------------------------------------------- hoof dust
    void BuildHoofDust()
    {
        var dustTex = MakePuffTex(); dustTex.name = "Gen_HoofDust";
        var parent = MakeGroup("HoofDust", Group("Particles"));
        for (int i = 0; i < 28; i++)
        {
            var img = MakeImage("HoofDust" + i.ToString("00"), dustTex, parent);
            var rt = img.rectTransform; rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0.5f);
            img.color = Color.clear; _dust.Add(new Dust { img = img });
        }
    }

    void SpawnHoofDust(float strength)
    {
        if (_dust.Count == 0 || strength < 0.15f) return;
        for (int k = 0; k < 2; k++)
        {
            Dust d = null; float oldest = -1f;
            foreach (var q in _dust) if (q.img && q.age > oldest) { oldest = q.age; d = q; }
            if (d == null) return;
            d.side = Random.value < 0.5f ? -1f : 1f;
            d.age = 0f; d.life = Random.Range(0.7f, 1.2f);
            d.pos = new Vector2(d.side * Random.Range(170f, 300f), Random.Range(10f, 70f));
            d.vel = new Vector2(d.side * Random.Range(60f, 160f), Random.Range(30f, 80f)) * Mathf.Lerp(0.6f, 1.2f, strength);
        }
    }

    void UpdateHoofDust(float dt, float sNorm)
    {
        foreach (var d in _dust)
        {
            if (!d.img) continue;
            if (d.age > d.life) { if (d.img.color.a > 0f) d.img.color = Color.clear; continue; }
            d.age += dt;
            float a = d.age / d.life;
            d.pos += d.vel * dt; d.vel *= Mathf.Exp(-2.5f * dt);
            d.pos.y -= dt * 160f * sNorm;                                   // left behind as the horse moves on
            var rt = d.img.rectTransform;
            rt.anchoredPosition = new Vector2(Mathf.Round(d.pos.x / 3f) * 3f, Mathf.Round(d.pos.y / 3f) * 3f);
            float sz = Mathf.Round((40f + a * 110f) / 3f) * 3f; rt.sizeDelta = new Vector2(sz, sz);
            var c = hoofDustColor; c.a *= Smooth(0f, 0.1f, a) * (1f - Smooth(0.35f, 1f, a)); d.img.color = c;
        }
    }

    void BuildPath()
    {
        _path = MakeImage("Road_Path", null, Group("Road"));
        _path.color = Color.white;
        SetupPathStrip();
    }

    // (Re)creates the CPU road texture for the current aspect; the strip is widened sideways for wide screens.
    void SetupPathStrip()
    {
        if (!_path) return;
        if (_src == null)
        {
            _pathSrc = Load("T_Ride_Path");
            if (!_pathSrc) { Debug.LogWarning("HorseRideRig: missing T_Ride_Path"); return; }
            try { _src = _pathSrc.GetPixels32(); }
            catch { Debug.LogError("HorseRideRig: T_Ride_Path must be Read/Write enabled (RideTextureImporter does this automatically)"); return; }
            _sw = _pathSrc.width; _sh = _pathSrc.height;
        }
        _pathExt = EdgeExt;
        int W = Mathf.RoundToInt(pathTexWidth * (1f + 2f * _pathExt));
        if (!_pathOut || _pathW != W || _pathOut.height != pathTexHeight)
        {
            if (_pathOut) DestroyObj(_pathOut);
            _pathW = W;
            _pathOut = new Texture2D(_pathW, pathTexHeight, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = filter, name = "Gen_RoadSnapshot" };
            _dst = new Color32[_pathW * pathTexHeight];
        }
        _path.texture = _pathOut;
        var rt = _path.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(1f, pathScreenFraction);
        rt.offsetMin = new Vector2(-_pathExt * WorldW, 0); rt.offsetMax = new Vector2(_pathExt * WorldW, 0);
        rt.pivot = new Vector2((vanishingPoint.x + _pathExt) / (1f + 2f * _pathExt), 0f);
    }

    float _pathExt; int _pathW;

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
        if (_src == null || !_pathOut || _dst == null) return;
        int W = _pathW, H = pathTexHeight;
        float hz = pathHorizon * H;
        float span = 1f + 2f * _pathExt;
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
            float aspect = (float)pathTexWidth / H;            // design aspect (unchanged look)
            for (int x = 0; x < W; x++)
            {
                float xd = (x + 0.5f) / W * span - _pathExt;     // design-space x (0..1 = the 9:16 area)
                float sx = (xd - vanishingPoint.x) * aspect - vanish * aspect;
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

    // Tunnel: copies staggered evenly in phase; each grows exponentially from the vanishing point (pivot) and slides out
    // past the screen edge, fading in while small (behind the others) and out at the very end. Nearest copy draws on top.
    readonly List<(RawImage img, float p)> _tun = new List<(RawImage, float)>();
    void ApplyTunnel(ZoomLayer L, float p, Vector2 bob, float steerPx)
    {
        _tun.Clear();
        var imgs = new[] { L.a, L.b, L.c };
        int n = 0; foreach (var im in imgs) if (im) n++;
        int k = 0;
        foreach (var im in imgs)
        {
            if (!im) continue;
            float pk = Mathf.Repeat(p + (float)k / n, 1f); k++;
            float s0 = Mathf.Max(0.05f, L.startScale), s = s0 * Mathf.Pow(Mathf.Max(s0, L.endScale) / s0, pk);
            var rt = im.rectTransform; rt.localScale = new Vector3(s, s, 1);
            float near = Mathf.Clamp01((s - s0) / Mathf.Max(0.01f, L.endScale - s0));
            rt.anchoredPosition = L.drift * pk + bob * L.bobAmount * Mathf.Lerp(0.5f, 1.2f, near) - new Vector2(steerPx * L.steerParallax * Mathf.Lerp(0.5f, 1.3f, near), 0);
            var c = Color.Lerp(grade * pineFogTint, grade * L.tint, Smooth(0f, 0.45f, pk));
            c.a = Smooth(0f, L.fadeFraction, pk) * (1f - Smooth(0.82f, 1f, pk));
            im.color = c;
            _tun.Add((im, pk));
        }
        _tun.Sort((x, y) => x.p.CompareTo(y.p));
        for (int i = 0; i < _tun.Count; i++) _tun[i].img.transform.SetSiblingIndex(i);
    }

    float _aspectCheck;
    void Update()
    {
        if (!IsBuilt) return;
        _aspectCheck -= Time.unscaledDeltaTime;
        if (_aspectCheck <= 0f)
        {
            _aspectCheck = 0.25f;
            float a = forceAspect > 0f ? forceAspect : (Screen.height > 0 ? (float)Screen.width / Screen.height : _aspect);
            if (Mathf.Abs(a - _aspect) > 0.01f) { Relayout(); SetupPixelDisplay(); }   // Game view / window resized -> refit to the new aspect
        }
        Tick(Time.deltaTime);
    }

    /// <summary>Advances the ride by dt (also used by the editor preview renderer).</summary>
    public void Tick(float dt, float forcedForward = -1f, float forcedSteer = float.NaN)
    {
        Vector2 input = forcedForward >= 0f ? new Vector2(float.IsNaN(forcedSteer) ? 0f : forcedSteer, forcedForward) : (requireInput ? ReadInput() : new Vector2(0f, 1f));
        if (!float.IsNaN(forcedSteer)) input.x = forcedSteer;
        float target = input.y > 0 ? input.y * forwardSpeed : input.y * backwardSpeed;
        if (steerWalks && Mathf.Abs(input.x) > 0.01f && Mathf.Abs(input.y) < 0.01f) target = steerWalkSpeed * forwardSpeed;
        // eased speed (SmoothDamp = acceleration ease-in/out)
        float prevSpeed = _speed;
        _speed = Mathf.SmoothDamp(_speed, target, ref _speedVel, Mathf.Abs(target) > Mathf.Abs(_speed) ? speedUpTime : slowDownTime, Mathf.Infinity, Mathf.Max(dt, 1e-4f));
        if (dt > 0f) _accel = Mathf.Lerp(_accel, (_speed - prevSpeed) / dt, 1f - Mathf.Exp(-dt * 4f));
        _t += dt * _speed;
        _distance = Mathf.Max(0f, _distance + dt * _speed);
        float aspd = Mathf.Abs(_speed);
        _bobAmp = Mathf.MoveTowards(_bobAmp, Mathf.Clamp01(4f * aspd / Mathf.Max(0.01f, forwardSpeed)), dt * 2f);
        float loopT = Mathf.Repeat(_t, loopDuration) / loopDuration;

        // steering (D right / A left)
        float steerTarget = Mathf.Abs(input.x) > 0.01f ? Mathf.Sign(input.x) : (steerReturnsToCenter ? 0f : _steer);
        float sRate = Mathf.Abs(input.x) > 0.01f ? steerResponse : steerReturnSpeed;
        _steer = Mathf.MoveTowards(_steer, Mathf.Clamp(steerTarget, -1f, 1f), sRate * dt * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(aspd * 2f)));
        float steerEase = _steer * (2f - Mathf.Abs(_steer));        // ease-out curve
        float steerPx = steerEase * maxSteerPx;

        // ---- gait: walk (4-beat) -> canter (3-beat) -> gallop (4-beat + long suspension) ----
        float sNorm = Mathf.Clamp01(aspd / Mathf.Max(0.01f, forwardSpeed));
        bool fwd = _speed >= 0f;
        _gaitW = Mathf.MoveTowards(_gaitW, fwd ? Smooth(canterBlend.x, canterBlend.y, sNorm) : 0f, dt * 1.5f);
        _gallopW = Mathf.MoveTowards(_gallopW, fwd ? Smooth(gallopBlend.x, gallopBlend.y, sNorm) : 0f, dt * 1.2f);
        float strideHz = Mathf.Lerp(Mathf.Lerp(walkStrideHz, canterStrideHz, _gaitW), gallopStrideHz, _gallopW) * Mathf.Lerp(0.8f, 1f, sNorm);
        _stride += dt * strideHz * (aspd > 0.0005f || _bobAmp > 0.01f ? 1f : 0f);
        float ph = Mathf.Repeat(_stride, 1f);
        float vCanter = Mathf.Lerp(CanterCurve(ph) * canterBobPx, GallopCurve(ph) * gallopBobPx, _gallopW);
        float vertTarget = Mathf.Lerp(WalkCurve(ph) * walkBobPx, vCanter, _gaitW) * _bobAmp;
        float pitch = Mathf.Lerp(0.3f, 1f, _gaitW) * pitchPx * Mathf.Lerp(CanterPitch(ph), GallopPitch(ph), _gallopW) * _bobAmp;
        // camera rides on a damped spring: eases the gait curve and overshoots slightly after impacts
        Spring(ref _camY, ref _camV, vertTarget + pitch, camSpringHz, camSpringDamping, dt);

        // hoof strikes -> screen shake + dust
        if (dt > 0f && aspd > 0.02f) FireImpacts(_prevPh, ph, sNorm);
        _prevPh = ph;
        _shake *= Mathf.Exp(-impactShakeDecay * dt);
        float tn = (Application.isPlaying ? Time.time : _t) * 37f;
        Vector2 shake = new Vector2(Mathf.PerlinNoise(tn, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, tn) - 0.5f) * 2f * _shake * impactShakePx;

        float sway = Mathf.Sin(_stride * Mathf.PI) * gaitSwayPx * Mathf.Lerp(1f, 0.7f, _gaitW) * _bobAmp;
        float surge = Mathf.Clamp(_accel, -1.5f, 1.5f) * surgePx;
        Vector2 bob = new Vector2(sway, _camY - surge) + shake;
        if (_world)
        {
            float roll = Mathf.Sin(_stride * Mathf.PI + 0.6f) * gaitRollDegrees * Mathf.Lerp(1f, 1.3f, _gallopW) * _bobAmp - steerEase * steerBankDegrees;
            if (staticScenery) roll = 0f;
            _world.localEulerAngles = new Vector3(0, 0, roll);
            float sc = 1f + 0.004f * pitch / Mathf.Max(1f, pitchPx);
            if (staticScenery) sc = 1f;
            _world.localScale = new Vector3(sc, sc, 1f);
        }

        if (_skyGroup) _skyGroup.anchoredPosition = staticScenery ? Vector2.zero : bob * skyBobAmount - new Vector2(steerPx * skySteer, 0);
        float tNow = Application.isPlaying ? Time.time : _t;
        if (_moonGlow) { var c = moonGlowColor; c.a *= 0.85f + 0.15f * Mathf.Sin(tNow * 0.8f); _moonGlow.color = c; }

        float approach = 1f - Mathf.Exp(-_distance / Mathf.Max(1f, approachSeconds));
        foreach (var L in layers)
        {
            if (!L.a) continue;
            if (L.motion == Motion.Static)
            {
                var srt = L.a.rectTransform; srt.localScale = Vector3.one; srt.anchoredPosition = Vector2.zero;
                L.a.color = grade * L.tint;
                foreach (var extra in new[] { L.b, L.c }) if (extra) extra.color = Color.clear;
                continue;
            }
            if (L.motion == Motion.Approach)
            {
                float s = Mathf.Lerp(L.startScale, L.endScale, approach);
                var rt = L.a.rectTransform; rt.localScale = new Vector3(s, s, 1);
                rt.anchoredPosition = L.drift * approach + bob * L.bobAmount - new Vector2(steerPx * L.steerParallax, 0);
                L.a.color = grade * L.tint;
                continue;
            }
            float p = Mathf.Repeat(loopT * Mathf.Max(1, L.cyclesPerLoop), 1f);
            if (L.motion == Motion.Tunnel)
            {
                ApplyTunnel(L, p, bob, steerPx);
                continue;
            }
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
            _rays.rectTransform.anchoredPosition = staticScenery ? Vector2.zero : bob * 0.12f - new Vector2(steerPx * 0.08f, 0);
            _rays.color = new Color(1, 1, 1, moonRayAlpha * (0.8f + 0.2f * Mathf.Sin(tNow * 0.5f)));
        }

        for (int i = 0; i < _fogSheets.Count; i++)
        {
            var f = _fogSheets[i];
            if (f == null || !f.img) continue;
            if (f.scroll)
            {
                var r = f.img.uvRect;
                r.x = Mathf.Repeat(tNow * f.speed + _t * 0.01f * (i + 1) + steerPx * f.steer / referenceResolution.x, 1f);
                f.img.uvRect = r;
                f.img.rectTransform.anchoredPosition = bob * (0.15f + 0.2f * f.steer);
            }
            else
            {
                // fog bank: slow breathing sway, pans with steering like the scenery at its depth
                float fsw = Mathf.Sin(tNow * 0.21f + f.seed) * f.sway + Mathf.Sin(tNow * 0.53f + f.seed * 1.7f) * f.sway * 0.35f;
                f.img.rectTransform.anchoredPosition = new Vector2(fsw - steerPx * f.steer, Mathf.Sin(tNow * 0.17f + f.seed) * f.sway * 0.25f) + (staticScenery ? Vector2.zero : bob * (0.2f + 0.5f * f.steer));
            }
        }

        UpdateSmoke(); UpdateCrows(dt);
        UpdateMotes(dt * _speed, bob, steerPx);
        foreach (var ps in _particles)
        {
            if (!ps) continue;
            var vel = ps.velocityOverLifetime; vel.speedModifier = Mathf.Lerp(0.12f, 1.15f, sNorm) * (fwd ? 1f : -0.4f);
            ((RectTransform)ps.transform).anchoredPosition = new Vector2(bob.x * 0.6f - steerPx * 0.5f, bob.y * 0.6f);
            if (!Application.isPlaying && dt > 0f) ps.Simulate(dt, true, false, false);
        }

        if (_path)
        {
            DrawPath(loopT * pathTilesPerLoop * pathTileLength, pathCurve + steerEase * pathSteerCurve);
            _path.rectTransform.anchoredPosition = bob * pathBobAmount;
        }
        _pathCurveNow = pathCurve + steerEase * pathSteerCurve;
        UpdatePines(_t / loopDuration * pathTilesPerLoop * pathTileLength, bob);
        UpdateHoofDust(dt, sNorm);

        if (_horse)
        {
            // head/neck: driven by the stride, follows on a softer spring so it lags the body (nod drop on fore impacts, lift in suspension)
            float nph = Mathf.Repeat(ph - neckPhase, 1f);
            float headTarget = Mathf.Lerp(Mathf.Sin(nph * Mathf.PI * 4f) * 0.35f, Mathf.Cos(nph * Mathf.PI * 2f), _gaitW) * _bobAmp;
            headTarget -= _camY / Mathf.Max(1f, canterBobPx) * 0.35f;        // inertia: body drops, head keeps going
            Spring(ref _headY, ref _headV, headTarget, headSpringHz, headSpringDamping, dt);
            var rt = _horse.rectTransform;
            rt.anchoredPosition = new Vector2(bob.x * 0.55f + steerEase * horseSteerPx, -_camY * 0.3f + _headY * neckPumpPx + surge * 0.6f) + shake * 0.5f;
            _earTimer -= dt;
            if (_earTimer <= 0f) { _earTimer = Random.Range(earTwitchInterval.x, earTwitchInterval.y); _earT = 0f; }
            _earT += dt;
            float twitch = _earT < 0.25f ? Mathf.Sin(_earT / 0.25f * Mathf.PI * 2f) * earTwitchDegrees : 0f;
            float nod = Mathf.Clamp(_headV * 0.25f, -1f, 1f) * neckNodDegrees;
            rt.localEulerAngles = new Vector3(0, 0, nod + twitch - steerEase * horseSteerDegrees);
            float sy = 1f + 0.012f * twitch / Mathf.Max(0.01f, earTwitchDegrees) + 0.01f * _headY;
            rt.localScale = new Vector3(1f, sy, 1f);
        }
    }

    static void Spring(ref float x, ref float v, float target, float hz, float zeta, float dt)
    {
        if (dt <= 0f) { return; }
        float w = 2f * Mathf.PI * Mathf.Max(0.1f, hz);
        int steps = Mathf.CeilToInt(dt / (1f / 120f));
        float h = dt / steps;
        for (int i = 0; i < steps; i++) { v += (w * w * (target - x) - 2f * zeta * w * v) * h; x += v * h; }
    }

    static readonly Vector2[] WalkHits = { new Vector2(0f, 0.15f), new Vector2(0.25f, 0.12f), new Vector2(0.5f, 0.15f), new Vector2(0.75f, 0.12f) };
    static readonly Vector2[] CanterHits = { new Vector2(0f, 0.5f), new Vector2(0.22f, 1f), new Vector2(0.40f, 0.75f) };
    static readonly Vector2[] GallopHits = { new Vector2(0f, 0.5f), new Vector2(0.09f, 0.55f), new Vector2(0.30f, 0.9f), new Vector2(0.40f, 1f) };

    void FireImpacts(float from, float to, float sNorm)
    {
        var hits = _gallopW > 0.5f ? GallopHits : _gaitW > 0.5f ? CanterHits : WalkHits;
        foreach (var h in hits)
        {
            bool crossed = from <= to ? (h.x > from && h.x <= to) : (h.x > from || h.x <= to);
            if (!crossed) continue;
            _shake = Mathf.Max(_shake, h.y * Mathf.Lerp(0.15f, 1f, sNorm) * _bobAmp);
            if (_gaitW > 0.3f) SpawnHoofDust(h.y * sNorm);
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
    // transverse gallop: hind pair (0, 0.09), fore pair (0.30, 0.40 strongest), long suspension ~0.72
    static float GallopCurve(float p)
    {
        return -0.5f * Dip(p - 0.00f, 0.03f, 0.07f) - 0.55f * Dip(p - 0.09f, 0.03f, 0.07f) - 0.9f * Dip(p - 0.30f, 0.03f, 0.08f)
               - 1.0f * Dip(p - 0.40f, 0.03f, 0.10f) + 0.8f * Dip(p - 0.72f, 0.15f, 0.15f);
    }
    static float GallopPitch(float p) { return -Dip(p - 0.44f, 0.05f, 0.12f) + 0.5f * Dip(p - 0.10f, 0.07f, 0.10f); }
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

#if UNITY_EDITOR
    /// <summary>Editor bake: lets the builder swap every runtime-generated texture for a saved asset.</summary>
    public void RemapTextures(System.Func<Texture2D, Texture2D> remap)
    {
        foreach (var img in GetComponentsInChildren<RawImage>(true))
            if (img.texture is Texture2D t) { var n = remap(t); if (n != t) img.texture = n; }
        if (_crowUp) _crowUp = remap(_crowUp);
        if (_crowDown) _crowDown = remap(_crowDown);
        for (int i = 0; i < _pineTex.Count; i++) if (_pineTex[i]) _pineTex[i] = remap(_pineTex[i]);
    }
#endif

    // Auto-spawn so the scene works even without manual setup.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoSpawn()
    {
        if (FindAnyObjectByType<HorseRideRig>()) return;
        if (!Resources.Load<Texture2D>("Ride/T_Ride_Sky")) return;
        new GameObject("HorseRideRig").AddComponent<HorseRideRig>();
    }
}
