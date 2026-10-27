using UnityEditor;
using UnityEngine;

namespace OriginsStaffs.EditorTools
{
    /// <summary>
    /// One click for a fresh clone: import the game's scripts (MeatKit finds H3VR via Steam),
    /// then, after the script reload that causes, the OtherLoader package and our prefabs.
    /// </summary>
    [InitializeOnLoad]
    public static class FirstTimeSetup
    {
        private const string PendingKey = "OriginsStaffs.FirstTimeSetup.Pending";
        private const string OtherLoaderPackage = "../tools/OtherloaderPackage-1.1.1.unitypackage";

        static FirstTimeSetup()
        {
            if (!EditorPrefs.GetBool(PendingKey, false)) return;
            EditorApplication.delayCall += Continue;
        }

        [MenuItem("Origins Staffs/First-Time Setup", priority = 0)]
        public static void Run()
        {
            EditorPrefs.SetBool(PendingKey, true);
            global::MeatKit.MeatKit.ImportAssemblies();
            // Importing the game switches on H3VR_IMPORTED and recompiles, which reloads this
            // class; Continue() picks up from there. If nothing recompiled, carry on now.
            EditorApplication.delayCall += Continue;
        }

        private static void Continue()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Continue;
                return;
            }
            if (!EditorPrefs.GetBool(PendingKey, false)) return;
            EditorPrefs.DeleteKey(PendingKey);

            if (!AssetDatabase.IsValidFolder("Assets/Packages/Otherloader"))
            {
                Debug.Log("[OriginsStaffs] Importing " + OtherLoaderPackage);
                AssetDatabase.ImportPackage(OtherLoaderPackage, false);
            }
            StaffPrefabBuilder.Rebuild();
        }
    }
}
