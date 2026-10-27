using System.IO;
using UnityEditor;

namespace OriginsStaffs.EditorTools
{
    /// <summary>
    /// MeatKit's build profile can only reference README/CHANGELOG/icon assets inside Assets/,
    /// but the repo-root copies are the ones tcli and GitHub use. Mirror them in on editor load.
    /// </summary>
    [InitializeOnLoad]
    public static class PackageDocsSync
    {
        private const string Target = "Assets/OriginsStaffs/Package/";
        private static readonly string[] Files = { "README.md", "CHANGELOG.md", "icon.png" };

        static PackageDocsSync()
        {
            Sync();
        }

        [MenuItem("Origins Staffs/Sync Package Docs")]
        public static void Sync()
        {
            Directory.CreateDirectory(Target);
            bool changed = false;
            foreach (var file in Files)
            {
                var source = Path.Combine("..", file);
                var dest = Target + file;
                if (!File.Exists(source)) continue;
                if (File.Exists(dest) && File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(source)) continue;
                File.Copy(source, dest, true);
                changed = true;
            }
            if (changed) AssetDatabase.Refresh();
        }
    }
}
