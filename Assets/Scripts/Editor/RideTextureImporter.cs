using UnityEditor;

// Makes Resources/Ride textures readable (CPU path), point-filtered (pixel art), uncompressed.
// Layers mirror horizontally so steering/panning never reveals a hard edge.
public class RideTextureImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Replace('\\', '/').Contains("/Resources/Ride/")) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Default;
        ti.alphaIsTransparency = true;
        ti.isReadable = true;
        ti.filterMode = UnityEngine.FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.maxTextureSize = 4096;
        bool isPathOrHorse = assetPath.Contains("T_Ride_Path") || assetPath.Contains("T_Ride_Horse");
        ti.wrapModeU = isPathOrHorse ? UnityEngine.TextureWrapMode.Clamp : UnityEngine.TextureWrapMode.Mirror;
        ti.wrapModeV = UnityEngine.TextureWrapMode.Clamp;
    }
}
