using UnityEditor;

// Makes Resources/Ride textures readable (CPU path), point-filtered (pixel art), uncompressed.
// Layers mirror horizontally so steering/panning never reveals a hard edge.
public class RideTextureImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        string ap = assetPath.Replace('\\', '/');
        if (ap.Contains("/Ride/Generated/"))
        {
            // baked procedural textures (fog, glow, puffs, road snapshot...) - pixel art, fog sheets tile horizontally
            var gi = (TextureImporter)assetImporter;
            gi.textureType = TextureImporterType.Default;
            gi.alphaIsTransparency = true; gi.isReadable = false; gi.mipmapEnabled = false;
            gi.filterMode = UnityEngine.FilterMode.Point; gi.npotScale = TextureImporterNPOTScale.None;
            gi.textureCompression = TextureImporterCompression.Uncompressed; gi.maxTextureSize = 4096;
            bool tile = ap.Contains("Gen_Fog_") && !ap.Contains("Fog_Right");
            gi.wrapMode = tile ? UnityEngine.TextureWrapMode.Repeat : UnityEngine.TextureWrapMode.Clamp;
            return;
        }
        if (!ap.Contains("/Resources/Ride/")) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Default;
        ti.alphaIsTransparency = true;
        ti.isReadable = true;
        ti.filterMode = UnityEngine.FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.maxTextureSize = 4096;
        bool isPathOrHorse = assetPath.Contains("T_Ride_Path") || assetPath.Contains("T_Ride_Horse") || assetPath.Contains("T_Ride_Pine") || assetPath.Contains("T_Ride_Tree");
        ti.wrapModeU = isPathOrHorse ? UnityEngine.TextureWrapMode.Clamp : UnityEngine.TextureWrapMode.Mirror;
        ti.wrapModeV = UnityEngine.TextureWrapMode.Clamp;
    }
}
