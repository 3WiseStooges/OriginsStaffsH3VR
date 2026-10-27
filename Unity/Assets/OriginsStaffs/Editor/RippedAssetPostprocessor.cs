using UnityEditor;

namespace OriginsStaffs.EditorTools
{
    /// <summary>
    /// Import settings for everything tools/export_unity.py drops into Ripped/.
    /// BO2 models are in inches; real materials are built by StaffPrefabBuilder.
    /// </summary>
    public class RippedAssetPostprocessor : AssetPostprocessor
    {
        public const string RippedRoot = "Assets/OriginsStaffs/Ripped/";
        public const float InchesToMeters = 0.0254f;

        // Bump when the settings below change so Unity reimports existing ripped assets.
        public override uint GetVersion()
        {
            return 2;
        }

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(RippedRoot)) return;

            var importer = (ModelImporter)assetImporter;
            importer.globalScale = InchesToMeters;
            // Needed so each OBJ "usemtl" stays its own submesh; StaffPrefabBuilder then
            // replaces these placeholder materials with real ones.
            importer.importMaterials = true;
            importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
            importer.materialSearch = ModelImporterMaterialSearch.Local;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.isReadable = false;
        }

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(RippedRoot)) return;

            var importer = (TextureImporter)assetImporter;
            if (assetPath.EndsWith("_nml.png") || assetPath.EndsWith("_n.png"))
            {
                importer.textureType = TextureImporterType.NormalMap;
            }
            importer.maxTextureSize = 2048;
        }
    }
}
