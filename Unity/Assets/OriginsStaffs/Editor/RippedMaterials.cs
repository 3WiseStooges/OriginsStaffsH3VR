using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OriginsStaffs.EditorTools
{
    /// <summary>Standard-shader materials for ripped BO2 textures, shared by every builder.</summary>
    public static class RippedMaterials
    {
        public const string MaterialDir = RippedAssetPostprocessor.RippedRoot + "Materials/";
        public const string TextureDir = RippedAssetPostprocessor.RippedRoot + "Textures/";

        private static readonly Dictionary<string, Material> Cache = new Dictionary<string, Material>();

        public static void ClearCache()
        {
            Cache.Clear();
        }

        /// <summary>
        /// Gets (or creates) the material named after BO2's, e.g. "mtl_t6_wpn_zmb_staff".
        /// Crystal and power-source materials glow in BO2, so they get their colour map as emission.
        /// </summary>
        public static Material Get(string bo2Name, string albedo, string normal, string emissive)
        {
            string name = bo2Name.Substring(bo2Name.LastIndexOf('/') + 1);
            Material mat;
            if (Cache.TryGetValue(name, out mat) && mat != null) return mat;

            Directory.CreateDirectory(MaterialDir);
            string path = MaterialDir + name + ".mat";
            mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetFloat("_Glossiness", 0.35f);
            mat.SetTexture("_MainTex", LoadTexture(albedo));
            Texture2D normalMap = LoadTexture(normal);
            mat.SetTexture("_BumpMap", normalMap);
            if (normalMap != null) mat.EnableKeyword("_NORMALMAP");
            else mat.DisableKeyword("_NORMALMAP");

            Texture2D glow = LoadTexture(emissive);
            if (glow != null)
            {
                mat.SetTexture("_EmissionMap", glow);
                mat.SetColor("_EmissionColor", Color.white * 1.5f);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                mat.DisableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(mat);
            Cache[name] = mat;
            return mat;
        }

        public static bool IsEmissive(Material mat)
        {
            return mat != null && mat.IsKeywordEnabled("_EMISSION");
        }

        private static Texture2D LoadTexture(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TextureDir + file);
        }
    }
}
