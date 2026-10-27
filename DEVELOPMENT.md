# Development

## Layout

| Path | What |
| --- | --- |
| `Unity/` | MeatKit project (Unity **5.6.7f1**, MeatKit **3.0.7**, see below). `Assets/MeatKit` is MeatKit's; ours is `Assets/OriginsStaffs`. |
| `Unity/Assets/OriginsStaffs/Ripped/` | Meshes and textures from BO2. Gitignored except `.meta` files; regenerate below. |
| `Unity/Assets/OriginsStaffs/Prefabs/Visual/` | Visual-only staff/crystal prefabs made by **Origins Staffs > Rebuild Ripped Prefabs**. |
| `tools/` | Extraction scripts, the browser model viewer, and the OtherLoader `.unitypackage`. |
| `extracted/` | Curated rip from `collect_assets.py`. Gitignored. |

Game assets are never committed. Everything under `Ripped/` and `extracted/` is rebuilt from
your own Black Ops II install.

## Extracting from Black Ops II

Tools live outside the repo, in `X:\Tools\OpenAssetTools` (OAT v0.34.0).

Origins' streamed textures are in `dlczm4.ipak`, but the zone asks for `zm_tomb.ipak`. Without
that file every texture fails with `Could not find data for image`. A hardlink under the
expected name fixes it without touching the game folder:

```powershell
New-Item -ItemType HardLink -Path X:\Tools\OpenAssetTools\ipak_alias\zm_tomb.ipak -Target "X:\SteamLibrary\steamapps\common\Call of Duty Black Ops II\zone\all\dlczm4.ipak"
```

Then, from `X:\Tools\OpenAssetTools`:

```bash
ZONE="X:/SteamLibrary/steamapps/common/Call of Duty Black Ops II/zone/all/zm_tomb.ff"
./Unlinker.exe --model-format GLTF -o "zone_dump/?zone?" "$ZONE"
./Unlinker.exe --search-path X:/Tools/OpenAssetTools/ipak_alias --include-assets image -o "zone_dump_img/?zone?" "$ZONE"
./Unlinker.exe --include-assets xmodel --model-format OBJ -o "zone_dump_obj/?zone?" "$ZONE"
```

Then, from the repo root (Pillow is in the OAT folder's venv):

```bash
X:/Tools/OpenAssetTools/.venv/Scripts/python tools/collect_assets.py X:/Tools/OpenAssetTools/zone_dump/zm_tomb X:/Tools/OpenAssetTools/zone_dump_img/zm_tomb extracted
python tools/export_unity.py X:/Tools/OpenAssetTools/zone_dump_obj/zm_tomb extracted Unity/Assets/OriginsStaffs/Ripped fire water bolt air
```

`export_unity.py` stages OBJ meshes (Unity 5.6 cannot read GLTF), PNG textures, and
`staff_assets.json`. That file holds what OBJ loses: the BO2 attachment tags (`tag_tip`,
`tag_crystal`, `tag_reload`, `tag_flash`) and which texture each material uses.

To look at the rip without Unity, serve the repo root and open `tools/viewer.html`. The
`origins-staffs-viewer` launch config in the H3VR folder's `.claude/launch.json` does this on
port 8765.

## First-time Unity setup

1. Install Unity **5.6.7f1** and open `Unity/` directly, not through Unity Hub (Hub can hang
   on the splash screen with 5.6).
2. Run the extraction above so `Ripped/` is populated.
3. **Origins Staffs > First-Time Setup**. This runs MeatKit's **Import Game** (it finds H3VR
   through Steam), waits for the recompile, imports `tools/OtherloaderPackage-1.1.1.unitypackage`,
   and runs **Origins Staffs > Rebuild Ripped Prefabs**.

If the MeatKit menu is missing or there are compile errors about the game assembly, clear
**Player Settings > Other Settings > Scripting Define Symbols** and run **MeatKit > Scripts >
Import Game** again.

### MeatKit is pinned to 3.0.7: do not run "Check for updates"

MeatKit 3.1.x (the GitHub `main` branch as of 2026-10) crashes the editor on most script
reloads here: `System.ExecutionEngineException: SIGILL` in
`MeatKit.NativeHookManager.OnShutdownManaged`. Its 2026 "stability" rewrite detours a lot of
Unity's native code, and the shutdown detour fails on this machine (Windows 11, Meta Horizon
runtime installed). Disabling just its `TerminateMonoIOWorkers()` call did not help.

3.0.7 still uses the small 2023-era hook and survived repeated compile/reload cycles. Going
from 3.0.7 to 3.1.1 only changes `Assets/MeatKit/Editor` (3.1.x adds `PlayHelper.cs`,
`ManagedPluginDomainFix.cs` and `SafeAssemblyStripper.cs`) and `ProjectSettings/MeatKitVersion.txt`.
To move between versions, close Unity and copy the `Assets/MeatKit` folder from the release zip.

## Model notes

- BO2 units are inches. Ripped models import at `globalScale = 0.0254`, so 1 unit = 1 m.
- Every element shares one staff body (`t6_wpn_zmb_staff_world`, about 1.5 m). The element is
  only the tip and crystal on its tags.
- `t6_wpn_zmb_staff_view` is far more detailed but only covers the front half of the shaft,
  because that is all the player saw in BO2.
- `t6_wpn_zmb_staff_reload_<el>_world` sits at `tag_reload`, by the grip. It is the crystal
  housing BO2 swaps during its reload animation, and the planned H3VR magazine.
- Clip sizes (base/upgraded): fire 9/12, ice 6/9, lightning 12/18, wind 3/6. `upgraded2` and
  `upgraded3` are the charge-shot levels. Most damage is applied by GSC scripts, so the
  weapon files' damage values are not usable as-is.
- Effects (`fx` assets) cannot be exported by OAT and have to be rebuilt in Unity.

## Packaging

MeatKit writes bundles to `Unity/AssetBundles/<PackageName>/<Version>/`. For testing, set the
build profile to **Copy To Profile** with the r2modman H3VR profile. To publish, copy the
bundles (and any `OriginsStaffs.dll`) into `package/`, then run `tcli build`. `thunderstore.toml`
adds the repo-root README, CHANGELOG and icon.

The build profile's README/CHANGELOG/icon fields point at `Assets/OriginsStaffs/Package/`.
`PackageDocsSync` copies the repo-root files there whenever the editor loads.
