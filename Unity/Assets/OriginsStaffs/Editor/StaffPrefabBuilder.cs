using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OriginsStaffs.EditorTools
{
    /// <summary>
    /// Builds visual-only prefabs (no H3VR scripts yet) from Ripped/staff_assets.json:
    /// each staff with its tip, head crystal and reload crystal snapped to BO2's own tags,
    /// plus the loose reload crystal (the future magazine) and the dig-site crystal.
    /// </summary>
    public static class StaffPrefabBuilder
    {
        private const string ManifestPath = RippedAssetPostprocessor.RippedRoot + "staff_assets.json";
        private const string MaterialDir = RippedAssetPostprocessor.RippedRoot + "Materials/";
        private const string PrefabDir = "Assets/OriginsStaffs/Prefabs/Visual/";

        // tools/export_unity.py file stems per element (tips say "lightning", the rest "bolt").
        private static readonly string[][] Elements =
        {
            new[] { "Fire", "fire", "fire", "fire" },
            new[] { "Ice", "water", "water", "water" },
            new[] { "Lightning", "bolt", "lightning", "bolt" },
            new[] { "Wind", "air", "air", "air" },
        };

#pragma warning disable 0649 // filled in by JsonUtility
        [Serializable]
        private class Manifest { public ModelEntry[] models; }

        [Serializable]
        private class ModelEntry
        {
            public string name;
            public string obj;
            public float[] boundsMin;
            public float[] boundsMax;
            public MaterialEntry[] materials;
            public TagEntry[] tags;
        }

        [Serializable]
        private class MaterialEntry { public string name; public string albedo; public string normal; public string emissive; }

        [Serializable]
        private class TagEntry { public string name; public float[] pos; public float[] rot; }
#pragma warning restore 0649

        private static Dictionary<string, ModelEntry> _models;
        private static Dictionary<string, Material> _materials;

        [MenuItem("Origins Staffs/Rebuild Ripped Prefabs")]
        public static void Rebuild()
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (json == null)
            {
                EditorUtility.DisplayDialog("Origins Staffs", "Missing " + ManifestPath + ". Run tools/export_unity.py first.", "OK");
                return;
            }

            _models = new Dictionary<string, ModelEntry>();
            foreach (var m in JsonUtility.FromJson<Manifest>(json.text).models) _models[m.name] = m;
            _materials = new Dictionary<string, Material>();
            Directory.CreateDirectory(MaterialDir);
            Directory.CreateDirectory(PrefabDir);
            AssetDatabase.Refresh();

            int built = 0;
            foreach (var el in Elements)
            {
                string label = el[0], key = el[1], tip = el[2], upgTip = el[3];
                if (!_models.ContainsKey("t6_wpn_zmb_staff_crystal_" + key + "_world")) continue;

                built += SavePrefab("Staff_" + label, BuildStaff("t6_wpn_zmb_staff_world",
                    "t6_wpn_zmb_staff_tip_" + tip + "_world", key, "t6_wpn_zmb_staff_reload_" + key + "_world"));
                built += SavePrefab("Staff_" + label + "_Upgraded", BuildStaff("t6_wpn_zmb_staff_upg_world",
                    "t6_wpn_zmb_staff_tip_upg_" + upgTip + "_world", key, "t6_wpn_zmb_staff_reload_upg_" + key + "_world"));
                built += SavePrefab("Crystal_" + label, Instantiate("t6_wpn_zmb_staff_reload_" + key + "_world"));
                built += SavePrefab("Crystal_" + label + "_Upgraded", Instantiate("t6_wpn_zmb_staff_reload_upg_" + key + "_world"));
                built += SavePrefab("CrystalPickup_" + label, Instantiate("t6_wpn_zmb_staff_crystal_" + key + "_part"));
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[OriginsStaffs] Built " + built + " prefabs in " + PrefabDir);
        }

        private static GameObject BuildStaff(string body, string tipModel, string key, string reloadModel)
        {
            var root = Instantiate(body);
            if (root == null) return null;
            Attach(root, "tag_tip", tipModel);
            Attach(root, "tag_crystal", "t6_wpn_zmb_staff_crystal_" + key + "_world");
            Attach(root, "tag_reload", reloadModel);
            return root;
        }

        private static void Attach(GameObject staff, string tagName, string model)
        {
            var tag = staff.transform.Find(tagName);
            var part = Instantiate(model);
            if (tag == null || part == null)
            {
                Debug.LogWarning("[OriginsStaffs] Could not attach " + model + " at " + tagName);
                if (part != null) UnityEngine.Object.DestroyImmediate(part);
                return;
            }
            part.transform.SetParent(tag, false);
        }

        /// <summary>Instantiates a ripped model with materials applied and its tags as child transforms.</summary>
        private static GameObject Instantiate(string name)
        {
            ModelEntry entry;
            if (!_models.TryGetValue(name, out entry))
            {
                Debug.LogWarning("[OriginsStaffs] " + name + " is not in " + ManifestPath);
                return null;
            }
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(RippedAssetPostprocessor.RippedRoot + "Models/" + entry.obj);
            if (asset == null)
            {
                Debug.LogWarning("[OriginsStaffs] Missing model asset " + entry.obj);
                return null;
            }

            var go = (GameObject)UnityEngine.Object.Instantiate(asset);
            go.name = name;

            // The importer made one placeholder material per OBJ "usemtl", named after it
            // (export_unity.py writes the matching .mtl); swap each for the real material.
            foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = renderer.sharedMaterials;
                for (int s = 0; s < mats.Length; s++)
                {
                    var match = FindMaterial(entry, mats[s] != null ? mats[s].name : null);
                    if (match == null && s < entry.materials.Length) match = entry.materials[s];
                    if (match != null) mats[s] = GetMaterial(match);
                }
                renderer.sharedMaterials = mats;
            }

            Vector3 scale, mirror;
            MeasureImport(go, entry, out scale, out mirror);
            foreach (var t in entry.tags)
            {
                var tag = new GameObject(t.name).transform;
                tag.SetParent(go.transform, false);
                tag.localPosition = Vector3.Scale(new Vector3(t.pos[0], t.pos[1], t.pos[2]), Vector3.Scale(scale, mirror));
                // Reflecting across one axis keeps that quaternion component and negates the other two.
                var q = new Quaternion(t.rot[0], t.rot[1], t.rot[2], t.rot[3]);
                if (mirror.x < 0) q = new Quaternion(q.x, -q.y, -q.z, q.w);
                tag.localRotation = q;
            }
            return go;
        }

        private static MaterialEntry FindMaterial(ModelEntry entry, string placeholderName)
        {
            if (string.IsNullOrEmpty(placeholderName)) return null;
            foreach (var m in entry.materials)
            {
                if (m.name == placeholderName) return m;
            }
            return null;
        }

        /// <summary>
        /// Compares the imported mesh bounds with the raw OBJ bounds, so tag placement follows
        /// whatever scale and handedness flip Unity's OBJ importer actually applied.
        /// </summary>
        private static void MeasureImport(GameObject go, ModelEntry entry, out Vector3 scale, out Vector3 mirror)
        {
            var rawMin = new Vector3(entry.boundsMin[0], entry.boundsMin[1], entry.boundsMin[2]);
            var rawMax = new Vector3(entry.boundsMax[0], entry.boundsMax[1], entry.boundsMax[2]);
            var bounds = new Bounds();
            bool first = true;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                var local = new Bounds(go.transform.InverseTransformPoint(mf.transform.TransformPoint(b.center)),
                    Vector3.Scale(b.size, mf.transform.lossyScale));
                if (first) { bounds = local; first = false; }
                else bounds.Encapsulate(local);
            }

            float s = (rawMax - rawMin).magnitude > 0 ? bounds.size.magnitude / (rawMax - rawMin).magnitude : RippedAssetPostprocessor.InchesToMeters;
            scale = Vector3.one * s;

            float rawCenterX = (rawMin.x + rawMax.x) * 0.5f * s;
            mirror = Vector3.one;
            if (Mathf.Abs(rawCenterX) > 1e-4f && Mathf.Sign(bounds.center.x) != Mathf.Sign(rawCenterX)) mirror.x = -1;
        }

        private static Material GetMaterial(MaterialEntry m)
        {
            Material mat;
            if (_materials.TryGetValue(m.name, out mat)) return mat;

            string path = MaterialDir + m.name + ".mat";
            mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetFloat("_Glossiness", 0.35f);
            mat.SetTexture("_MainTex", LoadTexture(m.albedo));
            var normal = LoadTexture(m.normal);
            mat.SetTexture("_BumpMap", normal);
            if (normal != null) mat.EnableKeyword("_NORMALMAP");
            var emissive = LoadTexture(m.emissive);
            if (emissive != null)
            {
                mat.SetTexture("_EmissionMap", emissive);
                mat.SetColor("_EmissionColor", Color.white * 1.5f);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            EditorUtility.SetDirty(mat);
            _materials[m.name] = mat;
            return mat;
        }

        private static Texture2D LoadTexture(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(RippedAssetPostprocessor.RippedRoot + "Textures/" + file);
        }

        private static int SavePrefab(string name, GameObject go)
        {
            if (go == null) return 0;
            go.name = name;
            string path = PrefabDir + name + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) PrefabUtility.ReplacePrefab(go, existing, ReplacePrefabOptions.ReplaceNameBased);
            else PrefabUtility.CreatePrefab(path, go);
            UnityEngine.Object.DestroyImmediate(go);
            return 1;
        }
    }
}
