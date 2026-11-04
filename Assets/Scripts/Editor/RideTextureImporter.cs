using UnityEditor;

// Makes Resources/Ride textures readable (CPU path), smooth-filtered, uncompressed, no pixel snapping.
public class RideTextureImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Replace('\\', '/').Contains("/Resources/Ride/")) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Default;
        ti.alphaIsTransparency = true;
        ti.isReadable = true;
        ti.filterMode = UnityEngine.FilterMode.Bilinear;
        ti.mipmapEnabled = false;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.maxTextureSize = 4096;
        ti.wrapMode = UnityEngine.TextureWrapMode.Clamp;
    }
}
