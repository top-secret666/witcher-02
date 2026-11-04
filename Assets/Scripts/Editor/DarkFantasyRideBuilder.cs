// Tools > Build Dark Fantasy Ride : sets up Assets/Scenes/HorseRide.unity for the dark-fantasy horse ride.
// All layers are real GameObjects saved in the scene (Hierarchy: Camera_PostFX, HorseRide_Rig > RideCanvas > Frame > World >
// 01_Sky, 02_Background, 03_Midground, 04_Trees_Left, 05_Road, 06_Fog, 07_Particles, 08_Horse). Procedural textures and the
// particle material are saved to Assets/Ride/Generated. At Play the HorseRideRig component drives these objects.
// Reproducible: run it again any time. The scene is backed up to <project>/Backup before it is changed.
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class DarkFantasyRideBuilder
{
    const string ScenePath = "Assets/Scenes/HorseRide.unity";
    const string ProfilePath = "Assets/Settings/DarkFantasyRideProfile.asset";
    const string GeneratedDir = "Assets/Ride/Generated";

    [MenuItem("Tools/Build Dark Fantasy Ride")]
    public static void BuildMenu()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        bool rebuild = true;
        if (SceneHasBakedRig())
        {
            int c = EditorUtility.DisplayDialogComplex("Dark Fantasy Ride",
                "HorseRide already has its scene objects.\n\nKeep them (your manual edits stay; only camera, post FX and build settings are updated), or rebuild them from the rig settings?",
                "Keep objects", "Cancel", "Rebuild objects");
            if (c == 1) return;
            rebuild = c == 2;
        }
        Build(rebuild, false);
        EditorUtility.DisplayDialog("Dark Fantasy Ride", "Scene HorseRide is ready (all layers are in the Hierarchy).\nPress Play: W = ride forward, D = turn right (A = left, S = back).", "OK");
    }

    [MenuItem("Tools/Dark Fantasy Ride/Reset Rig Settings And Rebuild Objects")]
    public static void ResetMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (!EditorUtility.DisplayDialog("Dark Fantasy Ride", "Reset every HorseRideRig setting to the defaults and rebuild all scene objects?\n(The scene is backed up to Backup/ first.)", "Reset", "Cancel")) return;
        Build(true, true);
    }

    static bool SceneHasBakedRig()
    {
        if (!File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, ScenePath))) return false;
        var active = SceneManager.GetActiveScene();
        if (active.path != ScenePath) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var rig = Object.FindAnyObjectByType<HorseRideRig>();
        return rig && rig.IsBuilt;
    }

    public static void Build(bool rebuildObjects, bool resetSettings)
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

        // Camera + post FX under one tidy parent
        var camGroup = GameObject.Find("Camera_PostFX");
        if (!camGroup) camGroup = new GameObject("Camera_PostFX");
        cam.transform.SetParent(camGroup.transform, true);
        vol.gameObject.name = "PostFX_GlobalVolume";
        vol.transform.SetParent(camGroup.transform, true);
        camGroup.transform.SetSiblingIndex(0);

        // Rig
        var rig = Object.FindAnyObjectByType<HorseRideRig>();
        var rigGo = rig ? rig.gameObject : new GameObject("HorseRide_Rig");
        rigGo.name = "HorseRide_Rig";
        if (rig && resetSettings)
        {
            rig.ClearHierarchy();
            Object.DestroyImmediate(rig);        // re-create the component so every setting gets the tuned defaults from HorseRideRig.cs
            rig = null;
        }
        if (!rig) { rig = rigGo.AddComponent<HorseRideRig>(); rebuildObjects = true; }
        rig.postProfile = profile;
        rig.forceAspect = 0f;

        if (rebuildObjects || !rig.IsBuilt)
        {
            rig.ClearHierarchy();
            rig.particleMaterial = null;
            rig.Build();
            BakeGeneratedAssets(rig);
        }
        rigGo.transform.SetSiblingIndex(1);
        EditorUtility.SetDirty(rig);

        RenderSettings.fog = false;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // Put HorseRide first in Build Settings (keeps the others)
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        list.RemoveAll(s => s.path == ScenePath);
        list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log("DarkFantasyRideBuilder: HorseRide scene built. Press Play (W forward, D right).");
    }

    // Saves every runtime-generated texture (fog, glow, rays, crows, puffs, road snapshot, particle dot) and the particle
    // material as assets so the scene objects keep them in Edit mode and after reload.
    static void BakeGeneratedAssets(HorseRideRig rig)
    {
        Directory.CreateDirectory(Path.Combine(Directory.GetParent(Application.dataPath).FullName, GeneratedDir));
        AssetDatabase.Refresh();
        var map = new Dictionary<Texture2D, Texture2D>();
        var used = new HashSet<string>();
        System.Func<Texture2D, Texture2D> save = null;
        save = t =>
        {
            if (!t || AssetDatabase.Contains(t)) return t;
            if (map.TryGetValue(t, out var done)) return done;
            string baseName = string.IsNullOrEmpty(t.name) ? "Gen_Texture" : t.name;
            string n = baseName; int k = 1;
            while (used.Contains(n)) n = baseName + "_" + (k++);
            used.Add(n);
            string path = GeneratedDir + "/" + n + ".png";
            byte[] png;
            try { png = t.EncodeToPNG(); } catch { map[t] = t; return t; }
            File.WriteAllBytes(Path.Combine(Directory.GetParent(Application.dataPath).FullName, path), png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            map[t] = asset ? asset : t;
            return map[t];
        };
        rig.RemapTextures(save);

        var mat = rig.particleMaterial;
        if (mat && !AssetDatabase.Contains(mat))
        {
            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
                if (mat.HasProperty(prop) && mat.GetTexture(prop) is Texture2D mt) mat.SetTexture(prop, save(mt));
            string mpath = GeneratedDir + "/M_RideParticles.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(mpath)) AssetDatabase.DeleteAsset(mpath);
            AssetDatabase.CreateAsset(mat, mpath);
        }
        AssetDatabase.SaveAssets();
    }

    // ---- Preview: opens the saved scene (tests the baked objects) and renders frames at 9:16 and 16:9
    //      to <project>/Backup/preview_<aspect>_*.png (works in batch mode too)
    [MenuItem("Tools/Dark Fantasy Ride/Render Preview Frames")]
    public static void RenderPreview()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        string prev = SceneManager.GetActiveScene().path;
        RenderPreview(9f / 16f, "9x16");
        RenderPreview(16f / 9f, "16x9");
        EditorSceneManager.OpenScene(string.IsNullOrEmpty(prev) ? ScenePath : prev, OpenSceneMode.Single);   // discard preview changes
    }

    public static void RenderPreview(float aspect, string tag)
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        string outDir = Path.Combine(root, "Backup");
        Directory.CreateDirectory(outDir);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var rig = Object.FindAnyObjectByType<HorseRideRig>();
        if (!rig) { Debug.LogError("DarkFantasyRideBuilder: no HorseRideRig in " + ScenePath); return; }
        Debug.Log("DarkFantasyRideBuilder: preview " + tag + " uses baked scene objects: " + rig.IsBuilt + ", children under rig: " + rig.GetComponentsInChildren<Transform>(true).Length);
        rig.forceAspect = aspect;
        if (rig.pixelHeight <= 0) rig.pixelHeight = 1080;   // preview = what a 1080p screen shows
        try
        {
            rig.BeginRuntime();
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
                    if (tmp) { rt = new RenderTexture(Mathf.RoundToInt(1080 * aspect), 1080, 24); cam.targetTexture = rt; }
                    cam.Render();
                    var prevRT = RenderTexture.active; RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
                    RenderTexture.active = prevRT;
                    File.WriteAllBytes(Path.Combine(outDir, "preview_" + tag + "_" + f + ".png"), tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    if (tmp) { cam.targetTexture = null; rt.Release(); }
                    idx++;
                }
            }
        }
        finally
        {
            rig.EndRuntime();
            foreach (var v in Object.FindObjectsByType<Volume>()) if (v.name.Contains("(runtime)")) Object.DestroyImmediate(v.gameObject);
        }
        Debug.Log("DarkFantasyRideBuilder: " + tag + " preview frames written to " + outDir);
    }

    static void DumpHierarchy(Transform t, System.Text.StringBuilder sb, int depth)
    {
        if (depth > 5) return;
        var extra = "";
        var ri = t.GetComponent<UnityEngine.UI.RawImage>(); if (ri) extra += " [RawImage " + (ri.texture ? ri.texture.name : "none") + "]";
        if (t.GetComponent<ParticleSystem>()) extra += " [ParticleSystem]";
        if (t.GetComponent<Canvas>()) extra += " [Canvas]";
        if (t.GetComponent<Camera>()) extra += " [Camera]";
        if (t.GetComponent<Volume>()) extra += " [Volume]";
        sb.Append(new string(' ', depth * 2)).Append(t.name).Append(extra).Append('\n');
        int shown = 0;
        foreach (Transform c in t) { if (shown++ > 14) { sb.Append(new string(' ', depth * 2 + 2)).Append("... (" + t.childCount + " children)\n"); break; } DumpHierarchy(c, sb, depth + 1); }
    }

    // Batch entry: Unity -batchmode -projectPath <p> -executeMethod DarkFantasyRideBuilder.BatchBuildAndPreview -quit
    public static void BatchBuildAndPreview()
    {
        Build(true, true);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var sb = new System.Text.StringBuilder();
        foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects()) DumpHierarchy(go.transform, sb, 0);
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Backup", "hierarchy.txt"), sb.ToString());
        RenderPreview();
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);   // discard preview objects
    }
}
