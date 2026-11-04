// Tools > Build Dark Fantasy Ride : sets up Assets/Scenes/HorseRide.unity for the dark-fantasy horse ride.
// Reproducible: run it again any time. The scene is backed up to <project>/Backup before it is changed.
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class DarkFantasyRideBuilder
{
    const string ScenePath = "Assets/Scenes/HorseRide.unity";
    const string ProfilePath = "Assets/Settings/DarkFantasyRideProfile.asset";

    [MenuItem("Tools/Build Dark Fantasy Ride")]
    public static void BuildMenu()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Build();
        if (!Application.isBatchMode)
            EditorUtility.DisplayDialog("Dark Fantasy Ride", "Scene HorseRide is ready.\nPress Play: W = ride forward, D = turn right (A = left, S = back).\nGame view: set 1080x1920 (portrait) for TikTok.", "OK");
    }

    public static void Build()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string backupDir = Path.Combine(root, "Backup");
        Directory.CreateDirectory(backupDir);
        if (File.Exists(Path.Combine(root, ScenePath)))
            File.Copy(Path.Combine(root, ScenePath), Path.Combine(backupDir, "HorseRide_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity"), true);

        AssetDatabase.Refresh();
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if (!profile)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
        }
        DarkFantasyPost.Configure(profile);
        foreach (var comp in profile.components)
        {
            if (!AssetDatabase.Contains(comp)) { comp.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy; AssetDatabase.AddObjectToAsset(comp, profile); }
            EditorUtility.SetDirty(comp);
        }
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        var scene = File.Exists(Path.Combine(root, ScenePath))
            ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single)
            : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // Camera: black background, post-processing on, no AA (pixel art)
        var cam = Camera.main;
        if (!cam) { var cgo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)); cgo.tag = "MainCamera"; cam = cgo.GetComponent<Camera>(); }
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        var cd = cam.GetUniversalAdditionalCameraData();
        cd.renderPostProcessing = true; cd.antialiasing = AntialiasingMode.None;
        EditorUtility.SetDirty(cam); EditorUtility.SetDirty(cd);

        // Global volume -> dark fantasy profile
        Volume vol = null;
        foreach (var v in Object.FindObjectsByType<Volume>()) if (v.isGlobal) { vol = v; break; }
        if (!vol) vol = new GameObject("Global Volume", typeof(Volume)).GetComponent<Volume>();
        vol.isGlobal = true; vol.priority = 10; vol.sharedProfile = profile;
        EditorUtility.SetDirty(vol);

        // Rig
        // Re-create the component so every setting gets the tuned defaults from HorseRideRig.cs
        var old = Object.FindAnyObjectByType<HorseRideRig>();
        var rigGo = old ? old.gameObject : new GameObject("HorseRideRig");
        if (old) Object.DestroyImmediate(old);
        var rig = rigGo.AddComponent<HorseRideRig>();
        rig.postProfile = profile;
        EditorUtility.SetDirty(rig);

        RenderSettings.fog = false;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Put HorseRide first in Build Settings (keeps the others)
        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        list.RemoveAll(s => s.path == ScenePath);
        list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log("DarkFantasyRideBuilder: HorseRide scene built. Press Play (W forward, D right).");
    }

    // ---- Preview: renders a few frames of the ride at 9:16 and 16:9 to <project>/Backup/preview_<aspect>_*.png (works in batch mode too)
    [MenuItem("Tools/Dark Fantasy Ride/Render Preview Frames")]
    public static void RenderPreview()
    {
        RenderPreview(9f / 16f, "9x16");
        RenderPreview(16f / 9f, "16x9");
    }

    public static void RenderPreview(float aspect, string tag)
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string outDir = Path.Combine(root, "Backup");
        Directory.CreateDirectory(outDir);
        var rig = new GameObject("__PreviewRig").AddComponent<HorseRideRig>();
        rig.postProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        rig.forceAspect = aspect;
        try
        {
            rig.Build();
            float[] times = { 1.0f, 3.0f, 4.6f, 4.75f, 5.2f };
            float t = 0; int idx = 0;
            for (int f = 0; f < 420; f++)
            {
                float steer = f > 330 ? 1f : 0f;
                rig.Tick(1f / 60f, 1f, steer);
                t += 1f / 60f;
                if (idx < times.Length && t >= times[idx] || f == 419)
                {
                    Canvas.ForceUpdateCanvases();
                    var cam = rig.RenderCamera;
                    var rt = rig.PixelTarget;
                    bool tmp = rt == null;
                    if (tmp) { rt = new RenderTexture(Mathf.RoundToInt(960 * aspect), 960, 24); cam.targetTexture = rt; }
                    cam.Render();
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
                    RenderTexture.active = prev;
                    File.WriteAllBytes(Path.Combine(outDir, "preview_" + tag + "_" + f + ".png"), tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    if (tmp) { cam.targetTexture = null; rt.Release(); }
                    idx++;
                }
            }
        }
        finally
        {
            var cam = rig.RenderCamera;
            if (cam) cam.targetTexture = null;
            Object.DestroyImmediate(rig.gameObject);
            foreach (var v in Object.FindObjectsByType<Volume>()) if (v.name.Contains("(runtime)")) Object.DestroyImmediate(v.gameObject);
        }
        Debug.Log("DarkFantasyRideBuilder: " + tag + " preview frames written to " + outDir);
    }

    // Batch entry: Unity -batchmode -projectPath <p> -executeMethod DarkFantasyRideBuilder.BatchBuildAndPreview -quit
    public static void BatchBuildAndPreview()
    {
        Build();
        RenderPreview();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);   // discard preview objects
    }
}
