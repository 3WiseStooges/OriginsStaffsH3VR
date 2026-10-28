using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FistVR;
using OtherLoader;
using UnityEditor;
using UnityEngine;

namespace OriginsStaffs.EditorTools
{
    /// <summary>
    /// Builds the playable staffs from Ripped/Assembly/*.json (tools/export_assembly.py):
    /// meshes, skeleton and BO2 reload tracks, then the H3VR item around them — grips, physics,
    /// effects, sounds, item data, spawner entry and the MeatKit/OtherLoader build profile.
    ///
    /// Layout (root at the rubber grip, +Z towards the tip, as H3VR guns point down +Z):
    ///   Staff_Fire            OriginsStaff, Rigidbody, grab triggers (layer Interactable)
    ///     StaffRoot           the BO2 assembly (meshes, bones, StaffReloadRig)
    ///     Phys                collision shapes (layer Default)
    ///     RecoilHolder/Pose   main grip pose on the rubber handle
    ///     Lever/Pose          StaffLever on the blue rings (layer Interactable)
    ///     Muzzle, QBPose
    /// </summary>
    public static class StaffAssemblyBuilder
    {
        private const string AssemblyDir = RippedAssetPostprocessor.RippedRoot + "Assembly/";
        private const string GeneratedDir = RippedAssetPostprocessor.RippedRoot + "Generated/";
        private const string FxDir = RippedAssetPostprocessor.RippedRoot + "FX/";
        private const string SoundDir = RippedAssetPostprocessor.RippedRoot + "Sounds/";
        private const string PropsDir = RippedAssetPostprocessor.RippedRoot + "Props/";
        private const string PrefabDir = "Assets/OriginsStaffs/Prefabs/";
        private const string DataDir = "Assets/OriginsStaffs/Data/";
        private const string BuildDir = "Assets/OriginsStaffs/Data/Build/";
        private const string PackageDir = "Assets/OriginsStaffs/Package/";
        private const string FolderIcon = "Assets/OriginsStaffs/Art/OriginsStaffsFolder.png";
        private const string SpawnerFolder = "Firearms/Exotic/OriginsStaffs";

        private const int LayerDefault = 0;
        private const int LayerInteractable = 10;
        // Default, NoSelfCol, AgentMeleeWeapon, Environment, AgentBody (H3VR's layer table).
        private const int ProjectileHitMask = (1 << 0) | (1 << 11) | (1 << 13) | (1 << 19) | (1 << 20);
        private const int BlockerMask = 1 << 19;
        // Play-testing: BO2's mix was too loud next to H3VR's guns. Applied to every staff sound.
        private const float SoundVolume = 0.25f;

        private static readonly Dictionary<string, string> DisplayNames = new Dictionary<string, string>
        {
            { "Staff_Fire", "Staff of Fire" },
            { "Staff_Fire_Upgraded", "Kagutsuchi's Blood" },
            { "Staff_Ice", "Staff of Ice" },
            { "Staff_Ice_Upgraded", "Ullr's Arrow" },
            { "Staff_Lightning", "Staff of Lightning" },
            { "Staff_Lightning_Upgraded", "Kimat's Bite" },
            { "Staff_Wind", "Staff of Wind" },
            { "Staff_Wind_Upgraded", "Boreas' Fury" },
        };

        /// <summary>What differs per element besides BO2's weapon-file numbers.</summary>
        private class ElementStyle
        {
            public StaffElement Element;
            public Color Glow;      // crystal, projectile and flash light
            public string[] Blades; // upgraded tip bones that flip out as blades
        }

        private static readonly Dictionary<string, ElementStyle> Styles = new Dictionary<string, ElementStyle>
        {
            { "fire", new ElementStyle { Element = StaffElement.Fire, Glow = new Color(1f, 0.5f, 0.15f),
                Blades = new[] { "tag_tip_fire5", "tag_tip_fire6" } } },
            { "ice", new ElementStyle { Element = StaffElement.Ice, Glow = new Color(0.45f, 0.8f, 1f),
                Blades = new[] { "tag_blade_1", "tag_blade_2", "tag_blade_3", "tag_blade_4" } } },
            { "lightning", new ElementStyle { Element = StaffElement.Lightning, Glow = new Color(0.65f, 0.45f, 1f),
                Blades = new[] { "tag_tip_lightning1", "tag_tip_lightning2", "tag_tip_lightning3", "tag_tip_lightning4" } } },
            { "wind", new ElementStyle { Element = StaffElement.Wind, Glow = new Color(1f, 0.85f, 0.4f),
                Blades = new[] { "tag_tip_air_le_blade1", "tag_tip_air_le_blade2", "tag_tip_air_le_blade3",
                                 "tag_tip_air_ri_blade1", "tag_tip_air_ri_blade2", "tag_tip_air_ri_blade3" } } },
        };

#pragma warning disable 0649 // filled in by JsonUtility
        [Serializable] private class AssemblyData
        {
            public string name;
            public BoneData[] bones;
            public MeshData[] meshes;
            public TrackData[] tracks;
            public PointData[] points;
            public int frames;
            public float fps;
            public StatsData stats;
            public string element;
            public bool upgraded;
            public string icon;
            public FxData[] fx;
            public SoundData[] sounds;
            public float modelScale = 1f;
        }
        [Serializable] private class BoneData
        {
            public string name;
            public int parent;
            public float[] pos;
            public float[] rot;
            public float[] restPos; // BO2 idle pose for this variant, applied after skinning
            public float[] restRot;
            public float scale;     // local scale (the halved tip and head crystal); 0 = unscaled
        }
        [Serializable] private class MaterialData { public string name; public string albedo; public string normal; public string emissive; }
        [Serializable] private class SubmeshData { public MaterialData material; public string triangles; }
        [Serializable] private class MeshData
        {
            public string name;
            public int bone;
            public int vertexCount;
            public string vertices;
            public string normals;
            public string uv;
            public SubmeshData[] submeshes;
            public bool skinned;
            public string boneIndices;
            public string boneWeights;
        }
        [Serializable] private class TrackData { public int bone; public string posOffset; public string rot; }
        [Serializable] private class PointData { public string name; public float[] pos; }
        [Serializable] private class StatsData
        {
            public float clipSize;
            public float maxAmmo;
            public float fireTime;
            public float projectileSpeed;
            public float explosionRadius;
            public float explosionInnerDamage;
            public float explosionOuterDamage;
            public float damage;
            public float projectileLifetime;
        }
        [Serializable] private class FxData { public string name; public string file; public int columns; public int rows; }
        [Serializable] private class SoundData { public string key; public string alias; public string[] files; public float[] volume; public float[] pitch; }
#pragma warning restore 0649

        private class BuiltStaff
        {
            public GameObject Prefab;
            public FVRObject Item;
            public ItemSpawnerEntry Entry;
        }

        [MenuItem("Origins Staffs/Build Staffs", priority = 20)]
        public static void BuildAll()
        {
            RippedMaterials.ClearCache();
            EnsureFolder(PrefabDir + "FX");
            EnsureFolder(BuildDir);
            EnsureFolder(GeneratedDir);

            var built = new List<BuiltStaff>();
            foreach (string file in Directory.GetFiles(AssemblyDir, "*.json").OrderBy(f => f))
            {
                built.Add(BuildStaff(file.Replace('\\', '/')));
            }
            BuildPackage(built);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[OriginsStaffs] Built " + built.Count + " staffs: " + string.Join(", ", built.Select(b => b.Prefab.name).ToArray()));
        }

        [MenuItem("Origins Staffs/Build Staffs + Copy Mod To r2modman", priority = 21)]
        public static void BuildAndCopy()
        {
            BuildAll();
            var profile = AssetDatabase.LoadAssetAtPath<MeatKit.BuildProfile>(BuildDir + "OriginsStaffs_Profile.asset");
            MeatKit.MeatKitCache.LastSelectedProfile = profile;
            MeatKit.MeatKit.DoBuild();
        }

        // ------------------------------------------------------------------ staff

        private static BuiltStaff BuildStaff(string jsonPath)
        {
            var data = JsonUtility.FromJson<AssemblyData>(File.ReadAllText(jsonPath));
            ElementStyle style = Styles[data.element];
            string generated = GeneratedDir + data.name + "/";
            EnsureFolder(generated);
            string prefabPath = PrefabDir + data.name + ".prefab";

            // The staff is carried by the blue rings (trigger hand), so the root sits there: the
            // hand ranks grab candidates by transform position, and this is also the holster point.
            Vector3 anchor = Point(data, "blueRings");
            var root = new GameObject(data.name);
            root.layer = LayerInteractable;

            // --- BO2 assembly: bones, meshes, reload rig
            var bones = new Transform[data.bones.Length];
            for (int i = 0; i < bones.Length; i++) bones[i] = new GameObject(data.bones[i].name).transform;
            for (int i = 0; i < bones.Length; i++)
            {
                BoneData b = data.bones[i];
                bones[i].SetParent(b.parent >= 0 ? bones[b.parent] : root.transform, false);
                bones[i].localPosition = V3(b.pos);
                bones[i].localRotation = Q(b.rot);
                if (b.scale > 0f) bones[i].localScale = Vector3.one * b.scale; // halved tip and head crystal
            }
            Transform visual = bones[0];
            visual.localPosition = -anchor;

            var glowRenderers = new List<Renderer>();
            var headGlowRenderers = new List<Renderer>();
            foreach (MeshData m in data.meshes)
            {
                var go = new GameObject(m.name);
                go.transform.SetParent(bones[m.bone], false);
                Mesh mesh = BuildMesh(m, generated + m.name + ".asset");
                Material[] mats = m.submeshes.Select(s => RippedMaterials.Get(s.material.name, s.material.albedo, s.material.normal, s.material.emissive)).ToArray();
                Renderer r;
                if (m.skinned)
                {
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * go.transform.localToWorldMatrix).ToArray();
                    EditorUtility.SetDirty(mesh);
                    smr.bones = bones;
                    smr.rootBone = bones[m.bone];
                    smr.sharedMesh = mesh;
                    Bounds bounds = mesh.bounds;
                    bounds.Expand(0.3f); // the reload mechanism travels ~15 cm
                    smr.localBounds = bounds;
                    r = smr;
                }
                else
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    r = go.AddComponent<MeshRenderer>();
                }
                r.sharedMaterials = mats;
                if (mats.Any(RippedMaterials.IsEmissive))
                {
                    // The crystal in the tip read as dull in game; it gets its own, stronger glow.
                    if (bones[m.bone].name == "Mount_crystal") headGlowRenderers.Add(r);
                    else glowRenderers.Add(r);
                }
            }

            StaffReloadRig rig = BuildRig(data, visual.gameObject, bones);

            // Bind matrices and the rig's bind positions are taken; now pose the variant.
            for (int i = 0; i < bones.Length; i++)
            {
                BoneData b = data.bones[i];
                if (b.restPos != null && b.restPos.Length == 3) bones[i].localPosition = V3(b.restPos);
                if (b.restRot != null && b.restRot.Length == 4) bones[i].localRotation = Q(b.restRot);
            }

            // --- positions along the staff, relative to the rings
            float butt = Point(data, "buttEnd").z - anchor.z;
            float muzzleZ = Point(data, "muzzle").z - anchor.z;
            float reloadMount = BoneZ(bones, "Mount_reload");
            float tipMount = BoneZ(bones, "Mount_tip");
            float top = muzzleZ + 0.12f;

            // --- lever: carries the trigger hand's pose, so pumping slides the hand down the shaft
            var leverGo = new GameObject("Lever");
            leverGo.layer = LayerInteractable;
            leverGo.transform.SetParent(root.transform, false);
            Transform recoilHolder = Child(leverGo.transform, "RecoilHolder", Vector3.zero);
            Transform pose = Child(recoilHolder, "Pose", Vector3.zero);

            // Holstered upright along the body, locked at the rings: the staff's +Z (tip) maps to
            // the slot's up.
            Transform qbPose = Child(root.transform, "QBPose", Vector3.zero);
            qbPose.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Transform muzzle = Child(root.transform, "Muzzle", new Vector3(0f, 0f, muzzleZ));

            // Grab zones split the staff: the rings and everything above carry the staff (trigger
            // hand); everything below the rings is the support grip, so a second hand anywhere on
            // the lower half steadies the staff instead of taking it from the trigger hand.
            float split = Point(data, "blueRingsStart").z - anchor.z - 0.03f;
            AddBox(root, true, new Vector3(0f, 0f, (split + tipMount) * 0.5f), new Vector3(0.09f, 0.09f, tipMount - split));
            AddBox(root, true, new Vector3(0f, 0f, (tipMount + top) * 0.5f), new Vector3(0.14f, 0.14f, top - tipMount));

            var phys = new GameObject("Phys");
            phys.layer = LayerDefault;
            phys.transform.SetParent(root.transform, false);
            AddCapsule(phys, false, butt, reloadMount, 0.022f);
            AddCapsule(phys, false, reloadMount, tipMount, 0.05f);
            AddBox(phys, false, new Vector3(0f, 0f, tipMount + 0.1f), new Vector3(0.25f, 0.05f, 0.21f));

            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 2.5f;
            rb.drag = 0.5f;
            rb.angularDrag = 0.05f;

            // --- support grip: the whole lower half, from the butt up to just below the rings.
            // The support hand is not snapped anywhere; aim uses wherever it actually holds.
            var supportGo = new GameObject("SupportGrip");
            supportGo.layer = LayerInteractable;
            supportGo.transform.SetParent(root.transform, false);
            supportGo.transform.localPosition = new Vector3(0f, 0f, (butt + split) * 0.5f);
            var supportTrigger = supportGo.AddComponent<CapsuleCollider>();
            supportTrigger.isTrigger = true;
            supportTrigger.direction = 2;
            supportTrigger.radius = 0.06f;
            supportTrigger.height = split - butt;
            Transform supportPose = Child(supportGo.transform, "Pose", Vector3.zero);

            // --- crystal light rides on the power-source bone so it follows the reload
            Transform crystalBone = bones.FirstOrDefault(b => b.name.EndsWith("_source_base1")) ?? muzzle;
            var lightGo = new GameObject("CrystalLight");
            lightGo.transform.SetParent(crystalBone, false);
            var crystalLight = lightGo.AddComponent<Light>();
            crystalLight.type = LightType.Point;
            crystalLight.color = style.Glow;
            crystalLight.range = 0.7f;
            crystalLight.intensity = 1.2f;
            crystalLight.shadows = LightShadows.None;

            // --- data assets
            Dictionary<string, AudioEvent> sounds = BuildSounds(data);
            FVRObject item = BuildItem(data, prefabPath);
            ItemSpawnerEntry entry = BuildSpawnerEntry(data, item);
            FVRFirearmAudioSet audioSet = BuildAudioSet(data, sounds);
            FVRFireArmRecoilProfile recoil = BuildRecoil();
            StaffProjectile projectile = null;
            StaffConeShot cone = null;
            switch (style.Element)
            {
                case StaffElement.Fire: projectile = BuildFireball(data, style, sounds); break;
                case StaffElement.Lightning: projectile = BuildLightningBall(data, style, sounds); break;
                case StaffElement.Ice: cone = BuildIceShot(data, style, sounds); break;
                case StaffElement.Wind: cone = BuildWindShot(data, style, sounds); break;
            }
            GameObject flash = BuildMuzzleFlash(data, style);

            // --- the staff
            var staff = root.AddComponent<OriginsStaff>();
            staff.ControlType = FVRInteractionControlType.GrabToggle;
            staff.HandlingGrabSound = HandlingGrabType.RifleWood;
            staff.PoseOverride = pose;
            staff.QBPoseOverride = qbPose;
            staff.UseGrabPointChild = true; // grabbed anywhere, it settles into the hand at the rings
            staff.UseGripRotInterp = true;
            staff.PositionInterpSpeed = 4f;
            staff.RotationInterpSpeed = 4f;
            staff.EndInteractionIfDistant = true;
            staff.EndInteractionDistance = 0.35f;
            staff.ObjectWrapper = item;
            staff.Harnessable = true;
            staff.Size = FVRPhysicalObject.FVRPhysicalObjectSize.Large; // large side/back slots only
            staff.DoesReleaseOverrideVelocity = true;
            staff.ThrowVelMultiplier = 1f;
            staff.ThrowAngMultiplier = 1f;
            staff.MoveIntensity = 1f;
            staff.RotIntensity = 1f;
            staff.AttachedRotationMultiplier = 60f;
            staff.AttachedPositionMultiplier = 9000f;
            staff.AttachedRotationFudge = 10000f;
            staff.AttachedPositionFudge = 10000f;
            staff.UsesGravity = true;
            staff.DistantGrabbable = true;
            staff.MuzzlePos = muzzle;
            staff.Foregrip = supportGo;
            staff.AudioClipSet = audioSet;
            staff.RecoilingPoseHolder = recoilHolder;
            staff.RecoilProfile = recoil;
            staff.MuzzleEffects = new MuzzleEffect[0];
            staff.GasOutEffects = new FVRFireArm.GasOutEffect[0];

            staff.Capacity = Mathf.RoundToInt(data.stats.clipSize);
            staff.RefireDelay = data.stats.fireTime > 0f ? data.stats.fireTime : 0.75f;
            staff.FullAuto = true;
            staff.Element = style.Element;
            staff.ProjectilePrefab = projectile;
            staff.ConeShotPrefab = cone;
            staff.ProjectileSpeed = data.stats.projectileSpeed * 0.0254f;
            // BO2's fire_spread_shots: two staggered side fireballs, for the base staff and Kagutsuchi's Blood alike.
            staff.SideShots = style.Element == StaffElement.Fire ? 2 : 0;
            staff.SideShotDelay = 0.2f;              // two network frames
            staff.SideShotYaw = 15f;
            staff.SideShotStart = 50f * 0.0254f;
            staff.SideShotReach = 100f * 0.0254f;
            staff.SideShotBlockers = BlockerMask;
            staff.MuzzleFlashPrefab = flash;
            staff.GlowRenderers = glowRenderers.ToArray();
            staff.CrystalLight = crystalLight;
            staff.HeadGlowRenderers = headGlowRenderers.ToArray();
            Transform headMount = bones.FirstOrDefault(b => b.name == "Mount_crystal");
            if (headMount != null)
            {
                var headLightGo = new GameObject("HeadLight");
                headLightGo.transform.SetParent(headMount, false);
                var headLight = headLightGo.AddComponent<Light>();
                headLight.type = LightType.Point;
                headLight.color = style.Glow;
                headLight.range = 1.2f;
                headLight.intensity = 1.6f;
                headLight.shadows = LightShadows.None;
                staff.HeadLight = headLight;
            }
            staff.AudEvent_Shot = Sound(sounds, "fire");
            staff.AudEvent_ShotRear = Sound(sounds, "fire_rear");
            staff.AudEvent_Flux = Sound(sounds, "flux");
            staff.AudEvent_DryFire = Sound(sounds, "dryfire");
            staff.AudEvent_Recharged = Sound(sounds, "crystal");
            staff.AudEvent_Handle = Sound(sounds, "handling");

            var supportGrip = supportGo.AddComponent<StaffSupportGrip>();
            supportGrip.ControlType = FVRInteractionControlType.GrabToggle;
            supportGrip.PoseOverride = supportPose;
            supportGrip.EndInteractionIfDistant = true;
            supportGrip.EndInteractionDistance = (split - butt) * 0.5f + 0.25f; // anywhere along the zone
            supportGrip.Staff = staff;
            staff.Support = supportGrip;

            var lever = leverGo.AddComponent<StaffLever>();
            lever.Staff = staff;
            lever.Rig = rig;
            lever.AudEvent_Unlock = Sound(sounds, "pickup");
            lever.AudEvent_Lock = Sound(sounds, "pickup");
            lever.AudEvent_Cue1 = Sound(sounds, "reload_1");
            lever.AudEvent_Cue2 = Sound(sounds, "reload_2");
            lever.AudEvent_Cue3 = Sound(sounds, "reload_3");
            lever.AudEvent_Cue4 = Sound(sounds, "reload_4");
            staff.Lever = lever;

            if (data.upgraded) SetupBlades(staff, style.Blades, bones, root.GetComponentsInChildren<SkinnedMeshRenderer>(true), pose, muzzle);
            if (data.upgraded) SetupChargeShot(staff, data, style, sounds, muzzle);

            GameObject prefab = SavePrefab(root, prefabPath);
            return new BuiltStaff { Prefab = prefab, Item = item, Entry = entry };
        }

        /// <summary>
        /// Every upgraded staff flips blades out of its tip (fire: two knives, ice: four claws,
        /// lightning: four spikes, wind: six fan blades); make them H3VR melee blades. Each blade
        /// gets a box collider fitted to its own vertices (in its bone's space, so it follows the
        /// pose) and a stab direction along the blade to its point. Swings with a blade cut; a
        /// thrust along a blade stabs and sticks like H3VR's knives.
        /// </summary>
        private static void SetupBlades(OriginsStaff staff, string[] blades, Transform[] bones, SkinnedMeshRenderer[] renderers, Transform handPoint, Transform endPoint)
        {
            SkinnedMeshRenderer tip = renderers.FirstOrDefault(r => r.name.Contains("tip"));
            if (tip == null)
            {
                Debug.LogWarning("[OriginsStaffs] No tip mesh found; blades not set up");
                return;
            }
            Mesh mesh = tip.sharedMesh;
            BoneWeight[] weights = mesh.boneWeights;
            Vector3[] verts = mesh.vertices;
            Matrix4x4[] bind = mesh.bindposes;

            var colliders = new List<Collider>();
            var directions = new List<Transform>();
            foreach (string knife in blades)
            {
                int bi = Array.FindIndex(bones, b => b.name == knife);
                if (bi < 0) continue;
                var points = new List<Vector3>();
                for (int v = 0; v < verts.Length; v++)
                {
                    if (weights[v].boneIndex0 == bi && weights[v].weight0 > 0.5f) points.Add(bind[bi].MultiplyPoint3x4(verts[v]));
                }
                if (points.Count == 0) continue;

                var bounds = new Bounds(points[0], Vector3.zero);
                foreach (Vector3 p in points) bounds.Encapsulate(p);
                int axis = bounds.size.x >= bounds.size.y && bounds.size.x >= bounds.size.z ? 0 : (bounds.size.y >= bounds.size.z ? 1 : 2);
                Vector3 along = Vector3.zero;
                along[axis] = 1f;
                // The point is the end of the blade farther from the tip's mount (the bone's origin).
                Vector3 near = bounds.center - along * bounds.extents[axis];
                Vector3 far = bounds.center + along * bounds.extents[axis];
                if (near.magnitude > far.magnitude)
                {
                    along = -along;
                    far = near;
                }

                var bladeGo = new GameObject("Blade_" + knife);
                bladeGo.layer = LayerDefault;
                bladeGo.transform.SetParent(bones[bi], false);
                var box = bladeGo.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = bounds.size;
                colliders.Add(box);

                var dir = new GameObject("StabDir_" + knife).transform;
                dir.SetParent(bones[bi], false);
                dir.localPosition = far;
                dir.localRotation = Quaternion.LookRotation(along, axis == 1 ? Vector3.forward : Vector3.up);
                directions.Add(dir);
                Debug.Log("[OriginsStaffs] " + staff.name + " " + knife + ": blade " + (bones[bi].TransformVector(bounds.size)).magnitude.ToString("0.00")
                          + " m, points " + bones[bi].TransformDirection(along).ToString("0.00") + " in staff space");
            }

            FVRPhysicalObject.MeleeParams mp = staff.MP ?? new FVRPhysicalObject.MeleeParams();
            mp.IsMeleeWeapon = true;
            mp.HandPoint = handPoint;
            mp.EndPoint = endPoint;
            mp.IgnoreRBs = new List<Rigidbody>();
            mp.BaseDamageBCP = new Vector3(250f, 0f, 0f);       // staff body: a heavy club
            mp.HighDamageBCP = new Vector3(50f, 1800f, 300f);   // knife edge: cuts and can sever
            mp.StabDamageBCP = new Vector3(0f, 600f, 2600f);    // knife point
            mp.TearDamageBCP = new Vector3(0f, 900f, 400f);
            mp.HighDamageColliders = new List<Collider>(colliders);
            mp.HighDamageVectors = new List<Transform>(directions);
            mp.DoesCyclePosePoints = false;
            mp.PosePoints = new Transform[0];
            mp.CanNewStab = colliders.Count > 0;
            mp.StabColliders = new List<Collider>(colliders);
            mp.StabDirection = directions.Count > 0 ? directions[0] : endPoint;
            mp.StabAngularThreshold = 30f;
            mp.StabVelocityRequirement = 1.5f;
            mp.BladeLength = colliders.Count > 0 ? colliders[0].transform.TransformVector(((BoxCollider)colliders[0]).size).magnitude * 0.8f : 0.15f;
            mp.MassWhileStabbed = 10f;
            mp.CanTearOut = true;
            mp.TearOutVelThreshold = 3f;
            mp.CanNewLodge = false;
            mp.LodgeDirections = new Transform[0];
            mp.LodgeColliders = new List<Collider>();
            mp.UsesSweepTesting = false;
            mp.TestCols = new List<Transform>();
            staff.MP = mp;
            staff.KnifeColliders = colliders.ToArray();
            staff.KnifeStabDirections = directions.ToArray();
        }

        private static StaffReloadRig BuildRig(AssemblyData data, GameObject host, Transform[] bones)
        {
            var rig = host.AddComponent<StaffReloadRig>();
            int n = data.tracks.Length;
            int stride = data.frames + 1;
            rig.LastFrame = data.frames;
            rig.FramesPerSecond = data.fps;
            rig.Bones = new Transform[n];
            rig.BindPositions = new Vector3[n];
            rig.HasPosition = new bool[n];
            rig.HasRotation = new bool[n];
            rig.PositionOffsets = new Vector3[n * stride];
            rig.Rotations = new Quaternion[n * stride];
            for (int t = 0; t < n; t++)
            {
                TrackData tr = data.tracks[t];
                Transform bone = bones[tr.bone];
                rig.Bones[t] = bone;
                rig.BindPositions[t] = bone.localPosition;
                if (!string.IsNullOrEmpty(tr.posOffset))
                {
                    float[] p = Floats(tr.posOffset);
                    for (int f = 0; f < stride; f++) rig.PositionOffsets[t * stride + f] = new Vector3(p[f * 3], p[f * 3 + 1], p[f * 3 + 2]);
                    rig.HasPosition[t] = true;
                }
                if (!string.IsNullOrEmpty(tr.rot))
                {
                    float[] q = Floats(tr.rot);
                    for (int f = 0; f < stride; f++) rig.Rotations[t * stride + f] = new Quaternion(q[f * 4], q[f * 4 + 1], q[f * 4 + 2], q[f * 4 + 3]);
                    rig.HasRotation[t] = true;
                }
                else
                {
                    for (int f = 0; f < stride; f++) rig.Rotations[t * stride + f] = bone.localRotation;
                }
            }
            return rig;
        }

        private static Mesh BuildMesh(MeshData m, string path)
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = mesh == null;
            if (created) mesh = new Mesh();
            mesh.Clear();
            mesh.name = m.name;

            float[] v = Floats(m.vertices), n = Floats(m.normals), uv = Floats(m.uv);
            int count = m.vertexCount;
            var verts = new Vector3[count];
            var norms = new Vector3[count];
            var uvs = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                verts[i] = new Vector3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
                norms[i] = new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]);
                uvs[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
            }
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.subMeshCount = m.submeshes.Length;
            for (int s = 0; s < m.submeshes.Length; s++) mesh.SetTriangles(Ints(m.submeshes[s].triangles), s);

            if (m.skinned)
            {
                int[] bi = Ints(m.boneIndices);
                float[] bw = Floats(m.boneWeights);
                var weights = new BoneWeight[count];
                for (int i = 0; i < count; i++)
                {
                    float total = bw[i * 4] + bw[i * 4 + 1] + bw[i * 4 + 2] + bw[i * 4 + 3];
                    if (total <= 0f) total = 1f;
                    weights[i] = new BoneWeight
                    {
                        boneIndex0 = bi[i * 4], weight0 = bw[i * 4] / total,
                        boneIndex1 = bi[i * 4 + 1], weight1 = bw[i * 4 + 1] / total,
                        boneIndex2 = bi[i * 4 + 2], weight2 = bw[i * 4 + 2] / total,
                        boneIndex3 = bi[i * 4 + 3], weight3 = bw[i * 4 + 3] / total,
                    };
                }
                mesh.boneWeights = weights;
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            if (created) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // ------------------------------------------------------------------ effects

        private static StaffProjectile BuildFireball(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds)
        {
            string name = "Fireball_" + data.name;
            var root = new GameObject(name);
            AddLight(root, style.Glow, 3.5f, 2.5f);
            AddLoop(root, Sound(sounds, "proj_loop"));

            float scale = data.upgraded ? 1.25f : 1f;
            ParticleSystem core = Particles(root.transform, "Core", Fx(data, "fxt_fire_flame_base_01"), true, false);
            {
                var main = core.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.16f, 0.28f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.22f * scale, 0.34f * scale);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.maxParticles = 60;
                var emission = core.emission;
                emission.rateOverTime = 70f;
                var shape = core.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.03f;
                Fade(core, new Color(1f, 0.85f, 0.6f), new Color(1f, 0.45f, 0.1f));
            }

            ParticleSystem trail = Particles(root.transform, "Trail", Fx(data, "fxt_fire_anim_flicker_1"), true, false);
            {
                var main = trail.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.14f * scale, 0.22f * scale);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 300;
                var emission = trail.emission;
                emission.rateOverTime = 0f;
                emission.rateOverDistance = 40f;
                var shape = trail.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.04f;
                Fade(trail, new Color(1f, 0.6f, 0.2f), new Color(0.6f, 0.1f, 0.02f));
                Shrink(trail);
            }

            ParticleSystem embers = Particles(root.transform, "Embers", Fx(data, "fxt_debris_fire_ember_cloud_01"), true, false);
            {
                var main = embers.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.0f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
                main.gravityModifier = -0.15f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 200;
                var emission = embers.emission;
                emission.rateOverTime = 35f;
                var shape = embers.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.05f;
                Fade(embers, new Color(1f, 0.7f, 0.3f), new Color(1f, 0.3f, 0.05f));
            }

            var ball = root.AddComponent<StaffProjectile>();
            ball.Element = StaffElement.Fire;
            ball.Lifetime = data.stats.projectileLifetime > 0f ? data.stats.projectileLifetime : 5f;
            ball.HitMask = ProjectileHitMask;
            ball.BlockerMask = BlockerMask;
            ball.SplashRadius = (data.stats.explosionRadius > 0f ? data.stats.explosionRadius : 100f) * 0.0254f;
            if (data.upgraded)
            {
                ball.DirectThermal *= 1.4f;
                ball.SplashThermal *= 1.4f;
            }
            ball.ImpactPrefab = BuildImpact(data, style, Sound(sounds, data.upgraded ? "impact_upgraded" : "impact") ?? Sound(sounds, "impact"));
            ball.AudEvent_Impact = null; // the impact prefab plays it from a 3D source that fades with distance
            ball.DetachOnImpact = new[] { trail, embers };

            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab").GetComponent<StaffProjectile>();
        }

        private static GameObject BuildImpact(AssemblyData data, ElementStyle style, AudioEvent impactSound)
        {
            string name = "Impact_" + data.name;
            float scale = data.upgraded ? 1.25f : 1f;
            GameObject root = OneShot(name, style.Glow, 6f * scale, 6f, 0.45f, 3f, impactSound);

            ParticleSystem burst = Particles(root.transform, "Burst", Fx(data, "fxt_fire_flame_base_01"), false, false);
            {
                var main = burst.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.75f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f * scale, 4f * scale);
                main.startSize = new ParticleSystem.MinMaxCurve(0.6f * scale, 1.2f * scale);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 40;
                var emission = burst.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)18) });
                var shape = burst.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.15f;
                Fade(burst, new Color(1f, 0.9f, 0.7f), new Color(0.5f, 0.12f, 0.03f));
                Grow(burst);
            }

            ParticleSystem flames = Particles(root.transform, "Flames", Fx(data, "fxt_fire_anim_flicker_1"), false, false);
            {
                var main = flames.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.45f * scale, 0.9f * scale);
                main.gravityModifier = -0.15f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 30;
                var emission = flames.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)10) });
                var shape = flames.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.5f * scale;
                Fade(flames, new Color(1f, 0.75f, 0.4f), new Color(0.6f, 0.15f, 0.02f));
            }

            ParticleSystem embers = Particles(root.transform, "Embers", Fx(data, "fxt_debris_fire_ember_cloud_01"), false, false);
            {
                var main = embers.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
                main.gravityModifier = 0.5f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 80;
                var emission = embers.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)45) });
                var shape = embers.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.1f;
                Fade(embers, new Color(1f, 0.8f, 0.4f), new Color(1f, 0.3f, 0.05f));
            }

            ParticleSystem smoke = Particles(root.transform, "Smoke", Fx(data, "fxt_smoke_tile"), false, true);
            {
                var main = smoke.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.6f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.9f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.9f * scale, 1.6f * scale);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                main.gravityModifier = -0.05f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 12;
                var emission = smoke.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0.05f, (short)6) });
                var shape = smoke.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.3f;
                var col = smoke.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(Gradient(new Color(0.15f, 0.13f, 0.12f), new Color(0.1f, 0.1f, 0.1f), 0.35f));
                Grow(smoke);
            }

            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab");
        }

        private static GameObject BuildMuzzleFlash(AssemblyData data, ElementStyle style)
        {
            string name = "MuzzleFlash_" + data.name;
            GameObject root = OneShot(name, style.Glow, 3f, 3f, 0.15f, 0.8f, null);
            if (style.Element != StaffElement.Fire)
            {
                // The crystal's flare. Ice adds a burst of frost and glints out of the tip: enough to read
                // as an ice blast, but thin and short-lived (the first, heavy version blinded the player).
                bool ice = style.Element == StaffElement.Ice;
                string tex = ice ? "fxt_light_flare_cool" : "fxt_light_flare";
                Emit(root.transform, "Flare", Fx(data, tex), false, V(0.08f, 0.12f), V(0f, 0f), ice ? V(0.35f, 0.5f) : V(0.25f, 0.35f), 2, 0f,
                     ParticleSystemShapeType.Sphere, 0.01f, 0f, Color.white, style.Glow, 0f, false);
                if (ice)
                {
                    Color frostColor = new Color(0.85f, 0.95f, 1f);
                    ParticleSystem frost = Emit(root.transform, "Frost", Fx(data, "fxt_smk_def_0"), true, V(0.45f, 0.7f), V(2f, 6f), V(0.12f, 0.2f), 10, 0f,
                         ParticleSystemShapeType.Cone, 0.03f, 18f, frostColor, style.Glow, 0f, true, false, 0.5f, 0.35f);
                    Grow(frost, 3f);
                    Drag(frost, 0.4f);
                    Emit(root.transform, "Glints", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.25f, 0.5f), V(4f, 12f), V(0.02f, 0.05f), 30, 0f,
                         ParticleSystemShapeType.Cone, 0.02f, 15f, Color.white, frostColor, 0.1f);
                    ParticleSystem ring = Emit(root.transform, "Ring", Fx(data, "fxt_fx_emp_ring_wave"), false, V(0.2f, 0.2f), V(0f, 0f), V(0.25f, 0.25f), 1, 0f,
                         ParticleSystemShapeType.Sphere, 0.01f, 0f, Color.white, style.Glow, 0f, false, false, 0.5f, 0.6f);
                    Grow(ring, 2.5f);
                }
                return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab");
            }

            ParticleSystem jet = Particles(root.transform, "Jet", Fx(data, "fxt_fire_flame_vert_c"), false, false);
            {
                var main = jet.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.08f, 0.14f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.4f);
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.maxParticles = 6;
                var emission = jet.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)3) });
                var shape = jet.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 8f;
                shape.radius = 0.02f;
                Fade(jet, new Color(1f, 0.85f, 0.6f), new Color(1f, 0.4f, 0.1f));
            }
            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab");
        }

        // --- lightning: a slow crackling ball that bursts and shocks everything nearby

        private static StaffProjectile BuildLightningBall(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds)
        {
            string name = "LightningBall_" + data.name;
            var root = new GameObject(name);
            float scale = data.upgraded ? 1.2f : 1f;
            AddLight(root, style.Glow, 3f, 3f);
            AddLoop(root, Sound(sounds, "proj_loop"));

            Color white = new Color(0.95f, 0.9f, 1f);
            Emit(root.transform, "Core", Fx(data, "fxt_electric_tile"), false, V(0.08f, 0.16f), V(0f, 0f), V(0.22f * scale, 0.32f * scale), 0, 45f,
                 ParticleSystemShapeType.Sphere, 0.02f, 0f, white, style.Glow, 0f, false, true);
            Emit(root.transform, "Glow", Fx(data, "fxt_light_flare_cool"), false, V(0.08f, 0.12f), V(0f, 0f), V(0.45f * scale, 0.6f * scale), 0, 20f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, white, style.Glow, 0f, false, true);
            Emit(root.transform, "Bolts", Fx(data, "fxt_env_lighting_bolt_air"), false, V(0.06f, 0.12f), V(0f, 0f), V(0.3f * scale, 0.55f * scale), 0, 30f,
                 ParticleSystemShapeType.Sphere, 0.05f, 0f, white, style.Glow, 0f, false, true);
            ParticleSystem trail = Emit(root.transform, "Trail", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.2f, 0.45f), V(0.2f, 0.8f), V(0.04f, 0.09f), 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.06f, 0f, white, style.Glow, 0.2f, true, true);
            var trailEmission = trail.emission;
            trailEmission.rateOverDistance = 35f;

            // BO2's weapon file: 2050 direct, 2050 inner / 1750 outer over 96 in (upgraded 3300, 3300 / 3000
            // over 128 in). Sosigs take that as BO2 damage (StaffProjectile.ShockSosigs); props and other
            // targets take the ordinary H3VR damage below, scaled the same way.
            float inner = data.stats.explosionInnerDamage > 0f ? data.stats.explosionInnerDamage : 2050f;
            float k = inner / 2050f;
            var ball = root.AddComponent<StaffProjectile>();
            ball.Element = StaffElement.Lightning;
            ball.Lifetime = data.stats.projectileLifetime > 0f ? data.stats.projectileLifetime : 5f;
            ball.HitMask = ProjectileHitMask;
            ball.BlockerMask = BlockerMask;
            ball.CastRadius = 0.08f;
            ball.SplashRadius = (data.stats.explosionRadius > 0f ? data.stats.explosionRadius : 96f) * 0.0254f;
            ball.DirectBlunt = 1500f * k;
            ball.DirectThermal = 0f;
            ball.DirectEMP = 1500f * k;
            ball.SplashBlunt = 700f * k;
            ball.SplashThermal = 0f;
            ball.SplashEMP = 900f * k;
            ball.Stunning = 0f;
            ball.Bo2Direct = data.stats.damage > 0f ? data.stats.damage : inner;
            ball.Bo2Inner = inner;
            ball.Bo2Outer = data.stats.explosionOuterDamage > 0f ? data.stats.explosionOuterDamage : inner * 0.85f;
            ball.StunTime = 1.5f;
            ball.IgniteChance = 0f;
            ball.PushSpeed = 1.5f;
            ball.ImpactPrefab = BuildLightningImpact(data, style, Sound(sounds, "impact"));
            ball.TargetEffectPrefab = BuildShock(data, style, Sound(sounds, "zap"));
            ball.DetachOnImpact = new[] { trail };
            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab").GetComponent<StaffProjectile>();
        }

        private static GameObject BuildLightningImpact(AssemblyData data, ElementStyle style, AudioEvent sound, string name = null, float scaleOverride = 0f)
        {
            float scale = scaleOverride > 0f ? scaleOverride : (data.upgraded ? 1.3f : 1f);
            GameObject root = OneShot(name ?? ("Impact_" + data.name), style.Glow, 7f * scale, 7f, 0.3f, 2f, sound);
            Color white = new Color(0.95f, 0.9f, 1f);
            ParticleSystem ring = Emit(root.transform, "Ring", Fx(data, "fxt_fx_emp_ring_wave"), false, V(0.3f, 0.3f), V(0f, 0f), V(1.6f * scale, 1.6f * scale), 1, 0f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, white, style.Glow, 0f);
            Grow(ring);
            Emit(root.transform, "Flash", Fx(data, "fxt_light_flare_cool"), false, V(0.12f, 0.2f), V(0f, 0f), V(2f * scale, 2.6f * scale), 1, 0f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, white, style.Glow, 0f);
            Emit(root.transform, "Bolts", Fx(data, "fxt_env_lighting_bolt_air"), false, V(0.08f, 0.2f), V(1f, 3f), V(0.6f * scale, 1.2f * scale), 12, 0f,
                 ParticleSystemShapeType.Sphere, 0.3f * scale, 0f, white, style.Glow, 0f);
            Emit(root.transform, "Sparks", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.3f, 0.8f), V(2f, 6f), V(0.03f, 0.07f), 40, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.1f, 0f, white, style.Glow, 0.6f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        /// <summary>Sparks crawling over a shocked sosig, with BO2's zombie-shock zap.</summary>
        private static GameObject BuildShock(AssemblyData data, ElementStyle style, AudioEvent sound, string name = null)
        {
            GameObject root = OneShot(name ?? ("Shock_" + data.name), style.Glow, 2f, 2.5f, 1f, 1.5f, sound);
            Color white = new Color(0.95f, 0.9f, 1f);
            Emit(root.transform, "Web", Fx(data, "fxt_electric_tile"), false, V(0.06f, 0.12f), V(0f, 0f), V(0.3f, 0.6f), 0, 40f,
                 ParticleSystemShapeType.Sphere, 0.25f, 0f, white, style.Glow, 0f, false, false, 1f);
            Emit(root.transform, "Bolts", Fx(data, "fxt_env_lighting_bolt_air"), false, V(0.05f, 0.1f), V(0f, 0f), V(0.25f, 0.5f), 0, 25f,
                 ParticleSystemShapeType.Sphere, 0.3f, 0f, white, style.Glow, 0f, false, false, 1f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        // --- ice: BO2's icicle spray; up to three sosigs freeze and then shatter

        private static StaffConeShot BuildIceShot(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds)
        {
            var root = new GameObject("IceShot_" + data.name);
            root.AddComponent<StaffTimedEffect>().Lifetime = 2.5f; // outlives its shards, which report hits to it
            var shot = root.AddComponent<StaffConeShot>();
            shot.Element = StaffElement.Ice;
            // _icicle_locate_target, three times per shot: the closest zombie within 600 in and
            // 25 degrees of the aim, not already on ice, with a clear trace to a random body tag.
            shot.Range = 600f * 0.0254f;
            shot.HalfAngle = 25f;
            // Play-tested: Ullr's Arrow's 12 icicles freeze up to 5 (BO2: 3 for both), in proportion to the spray.
            shot.MaxTargets = data.upgraded ? 5 : 3;
            shot.Bo2Damage = data.upgraded ? 3300f : 2050f; // ice_affect_zombie
            shot.NeedsLineOfSight = true;
            shot.BlockerMask = BlockerMask;
            shot.BoltSpeed = Speed(data, 2000f);
            shot.SprayCount = data.upgraded ? 12 : 7; // play-tested: Ullr's Arrow throws a denser spray
            shot.SprayDegrees = 6f;
            shot.BoltPrefab = BuildIceShard(data, style);
            shot.IciclePrefab = BuildIcicle(data, style);
            shot.FrostPrefab = BuildFrost(data, style, Sound(sounds, "freeze"));
            shot.ShatterPrefab = BuildShatter(data, style, Sound(sounds, "shatter"));
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffConeShot>();
        }

        /// <summary>One icicle of the spray: BO2's ice projectile model, small, with a cold glint.</summary>
        private static StaffBolt BuildIceShard(AssemblyData data, ElementStyle style)
        {
            var root = new GameObject("IceShard_" + data.name);
            AddProp(root.transform, "IceShard", 0.22f);
            Color frost = new Color(0.85f, 0.95f, 1f);
            Emit(root.transform, "Glint", Fx(data, "fxt_light_flare_cool"), false, V(0.05f, 0.08f), V(0f, 0f), V(0.12f, 0.16f), 0, 30f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, Color.white, style.Glow, 0f, false, true, 1f, 0.6f);
            ParticleSystem trail = Emit(root.transform, "Trail", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.12f, 0.25f), V(0f, 0.2f), V(0.02f, 0.04f), 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, Color.white, frost, 0f, true, true, 1f, 0.8f);
            var trailEmission = trail.emission;
            trailEmission.rateOverDistance = 12f;

            var bolt = root.AddComponent<StaffBolt>();
            // BO2's projectile lives 0.3 s (600 in). Play-testing: shards vanishing in mid-air looked
            // broken, so they fly on until they hit something; BO2's 600 in still bounds the targeting.
            bolt.Lifetime = 2f;
            bolt.CastRadius = 0.02f;
            bolt.BlockerMask = BlockerMask;
            bolt.Spin = 540f;
            GameObject puff = BuildIcePuff(data, style);
            bolt.ImpactPrefab = puff;
            bolt.EndPrefab = puff;
            bolt.DetachOnEnd = new[] { trail };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffBolt>();
        }

        /// <summary>The icicle left in a frozen sosig's chest (BO2's attach_bullet_model).</summary>
        private static GameObject BuildIcicle(AssemblyData data, ElementStyle style)
        {
            var root = new GameObject("Icicle_" + data.name);
            AddProp(root.transform, "IceShard", 0.3f);
            Emit(root.transform, "Cold", Fx(data, "fxt_light_flare_cool"), false, V(0.2f, 0.3f), V(0f, 0f), V(0.15f, 0.25f), 0, 8f,
                 ParticleSystemShapeType.Sphere, 0.05f, 0f, Color.white, style.Glow, 0f, false, true, 1f, 0.35f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        /// <summary>Where a shard breaks on the level or runs out: a few chips and a wisp, nothing to see through.</summary>
        private static GameObject BuildIcePuff(AssemblyData data, ElementStyle style)
        {
            var root = new GameObject("IcePuff_" + data.name);
            root.AddComponent<StaffTimedEffect>().Lifetime = 1.2f;
            Color frost = new Color(0.85f, 0.95f, 1f);
            Emit(root.transform, "Chips", Fx(data, "fxt_debris_clump_rocks"), false, V(0.3f, 0.6f), V(1f, 2.5f), V(0.02f, 0.05f), 6, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.05f, 0f, Color.white, frost, 0.8f);
            ParticleSystem wisp = Emit(root.transform, "Wisp", Fx(data, "fxt_smk_def_0"), true, V(0.4f, 0.7f), V(0.1f, 0.3f), V(0.1f, 0.16f), 2, 0f,
                 ParticleSystemShapeType.Sphere, 0.05f, 0f, frost, style.Glow, 0f, true, false, 0.5f, 0.2f);
            Grow(wisp, 2f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        /// <summary>A sosig freezing over, with BO2's freeze sound (wpn_waterstaff_freeze_zombie).</summary>
        private static GameObject BuildFrost(AssemblyData data, ElementStyle style, AudioEvent sound)
        {
            GameObject root = OneShot("Frost_" + data.name, style.Glow, 2f, 1.5f, 0.6f, 2.5f, sound);
            Color frost = new Color(0.85f, 0.95f, 1f);
            ParticleSystem mist = Emit(root.transform, "Mist", Fx(data, "fxt_smk_def_0"), true, V(0.8f, 1.4f), V(0.05f, 0.2f), V(0.3f, 0.5f), 5, 0f,
                 ParticleSystemShapeType.Sphere, 0.2f, 0f, frost, style.Glow, -0.03f, true, false, 0.5f, 0.35f);
            Grow(mist, 1.6f);
            Emit(root.transform, "Glints", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.5f, 1.2f), V(0.1f, 0.5f), V(0.02f, 0.05f), 0, 20f,
                 ParticleSystemShapeType.Sphere, 0.3f, 0f, Color.white, frost, 0.05f, true, false, 2f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        /// <summary>BO2's frozen_zombie_shatter: a burst of ice where the body was.</summary>
        private static GameObject BuildShatter(AssemblyData data, ElementStyle style, AudioEvent sound)
        {
            GameObject root = OneShot("Shatter_" + data.name, style.Glow, 3f, 3f, 0.3f, 2.5f, sound);
            Color frost = new Color(0.85f, 0.95f, 1f);
            Emit(root.transform, "Shards", Fx(data, "fxt_debris_clump_rocks"), false, V(0.8f, 1.6f), V(2f, 5f), V(0.06f, 0.16f), 50, 0f,
                 ParticleSystemShapeType.Sphere, 0.3f, 0f, Color.white, frost, 1f);
            Emit(root.transform, "Flash", Fx(data, "fxt_light_flare_cool"), false, V(0.12f, 0.18f), V(0f, 0f), V(1f, 1.4f), 1, 0f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, Color.white, style.Glow, 0f);
            ParticleSystem mist = Emit(root.transform, "Mist", Fx(data, "fxt_smk_def_0"), true, V(0.8f, 1.4f), V(0.4f, 1.2f), V(0.4f, 0.7f), 6, 0f,
                 ParticleSystemShapeType.Sphere, 0.3f, 0f, frost, style.Glow, 0f, true, false, 0.5f, 0.4f);
            Grow(mist, 1.8f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        // --- wind: a fast ball of wind; everything in the cone in front is struck as it passes

        private static StaffConeShot BuildWindShot(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds)
        {
            var root = new GameObject("WindShot_" + data.name);
            root.AddComponent<StaffTimedEffect>().Lifetime = 1.5f; // outlives the ball, which reports hits to it
            var shot = root.AddComponent<StaffConeShot>();
            shot.Element = StaffElement.Wind;
            // wind_damage_cone: the 12 zombies closest within 400 in, within 45 degrees of the aim
            // (upgraded 60), take 2050 (upgraded 3300). No trace in BO2. Play-tested: only Boreas'
            // Fury keeps the cone; the base staff hits only what its ball touches.
            shot.UsesCone = data.upgraded;
            shot.Range = 400f * 0.0254f;
            shot.HalfAngle = data.upgraded ? 60f : 45f;
            shot.MaxTargets = 12;
            shot.Bo2Damage = data.upgraded ? 3300f : 2050f;
            shot.NeedsLineOfSight = false;
            shot.BlockerMask = BlockerMask;
            shot.BoltSpeed = Speed(data, 1500f);
            // Play-tested: the ball blows away every sosig within 2 m of its path.
            shot.BoltPrefab = BuildWindBall(data, style, "WindBall_" + data.name, data.upgraded ? 1.25f : 1f, 0.75f, 0.2f, 2f,
                                            BuildDustPuff(data, "Impact_" + data.name, Sound(sounds, "impact"), 0.5f), null);
            shot.GustPrefab = BuildDustPuff(data, "Gust_" + data.name, null, 0.3f);
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffConeShot>();
        }

        /// <summary>
        /// The ball of wind at 1500 in/s. The normal shot's is removed after 0.75 s by BO2's
        /// air_projectile_delete and only fades; the charged shot's flies on and leaves a whirlwind.
        /// </summary>
        private static StaffBolt BuildWindBall(AssemblyData data, ElementStyle style, string name, float scale, float lifetime, float castRadius,
                                               float nearRadius, GameObject impactPrefab, GameObject endPrefab)
        {
            var root = new GameObject(name);
            AddLight(root, style.Glow, 2f, 0.8f);
            Color air = new Color(1f, 0.97f, 0.85f);
            ParticleSystem swirl = Emit(root.transform, "Swirl", Fx(data, "fxt_debris_heli_rotor_swirl"), false, V(0.15f, 0.25f), V(0f, 0f), V(0.45f * scale, 0.65f * scale), 0, 30f,
                 ParticleSystemShapeType.Sphere, 0.03f, 0f, air, style.Glow, 0f, false, true, 1f, 0.55f);
            Spin(swirl, 14f);
            ParticleSystem rings = Emit(root.transform, "Rings", Fx(data, "fxt_fx_ring_spiral"), false, V(0.12f, 0.2f), V(0f, 0f), V(0.3f * scale, 0.45f * scale), 0, 14f,
                 ParticleSystemShapeType.Sphere, 0.02f, 0f, air, style.Glow, 0f, false, true, 1f, 0.6f);
            Spin(rings, 20f);
            ParticleSystem trail = Emit(root.transform, "Trail", Fx(data, "fxt_smk_def_0"), true, V(0.35f, 0.6f), V(0f, 0.3f), V(0.2f * scale, 0.3f * scale), 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.1f, 0f, new Color(0.85f, 0.8f, 0.68f), new Color(0.6f, 0.55f, 0.46f), 0f, true, true, 1f, 0.25f);
            Grow(trail, 2.5f);
            var trailEmission = trail.emission;
            trailEmission.rateOverDistance = 5f;

            var bolt = root.AddComponent<StaffBolt>();
            bolt.Lifetime = lifetime;
            bolt.CastRadius = castRadius; // about the swirl's size, so what looks like a hit is one
            bolt.NearRadius = nearRadius;
            bolt.BlockerMask = BlockerMask;
            bolt.ImpactPrefab = impactPrefab;
            bolt.EndPrefab = endPrefab;
            bolt.DetachOnEnd = new[] { swirl, rings, trail };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffBolt>();
        }

        private static GameObject BuildDustPuff(AssemblyData data, string name, AudioEvent sound, float opacity)
        {
            var root = new GameObject(name);
            AddImpactSound(root, sound);
            root.AddComponent<StaffTimedEffect>().Lifetime = 2.5f;
            ParticleSystem dust = Emit(root.transform, "Dust", Fx(data, "fxt_smk_def_0"), true, V(0.8f, 1.4f), V(0.5f, 2f), V(0.3f, 0.6f), 8, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.3f, 0f, new Color(0.7f, 0.63f, 0.5f), new Color(0.45f, 0.4f, 0.33f), 0f, true, false, 0.5f, opacity);
            Grow(dust, 2f);
            Emit(root.transform, "Debris", Fx(data, "fxt_debris_clump_rocks"), false, V(0.5f, 1.1f), V(2f, 5f), V(0.04f, 0.09f), 16, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.2f, 0f, new Color(0.6f, 0.55f, 0.45f), new Color(0.4f, 0.36f, 0.3f), 1f, true, false, 0.5f, 0.9f);
            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab");
        }

        // --- charge shot (upgraded staffs): BO2's staff_<element>_upgraded2_zm / upgraded3_zm

        private static void SetupChargeShot(OriginsStaff staff, AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, Transform muzzle)
        {
            staff.ChargeShot = true; // BO2 fireType "Charge Shot"
            staff.FullAuto = false;
            staff.ChargedShots = new[] { BuildChargedShot(data, style, sounds, 2), BuildChargedShot(data, style, sounds, 3) };
            staff.AudEvent_Charge1 = Sound(sounds, "charge_1");
            staff.AudEvent_Charge2 = Sound(sounds, "charge_2");
            staff.AudEvent_Charge3 = Sound(sounds, "charge_3");

            AudioEvent loop = Sound(sounds, "charge_loop");
            if (loop != null && loop.Clips.Count > 0)
            {
                var source = muzzle.gameObject.AddComponent<AudioSource>();
                source.clip = loop.Clips[0];
                source.loop = true;
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.volume = loop.VolumeRange.x;
                source.minDistance = 0.5f;
                source.maxDistance = 20f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.dopplerLevel = 0f;
                staff.ChargeLoop = source;
            }

            // Energy gathering at the tip while charging; the staff raises its rate per level.
            string tex;
            Vector2 size;
            switch (style.Element)
            {
                case StaffElement.Fire: tex = "fxt_fire_anim_flicker_1"; size = V(0.08f, 0.14f); break;
                case StaffElement.Ice: tex = "fxt_spark_pcloud_blue_1"; size = V(0.03f, 0.06f); break;
                case StaffElement.Lightning: tex = "fxt_electric_tile"; size = V(0.08f, 0.14f); break;
                default: tex = "fxt_debris_heli_rotor_swirl"; size = V(0.1f, 0.18f); break;
            }
            ParticleSystem ps = Emit(muzzle, "ChargeFx", Fx(data, tex), false, V(0.25f, 0.4f), V(-0.6f, -0.35f), size, 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.16f, 0f, Color.white, style.Glow, 0f, false, true, 1f, 0.9f);
            var main = ps.main;
            main.playOnAwake = false;
            staff.ChargeParticles = ps;
            staff.ChargeParticleRate = 20f;
        }

        private static StaffChargedShot BuildChargedShot(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level)
        {
            string tag = "_L" + level + "_" + data.name;
            var root = new GameObject(style.Element + "Charge" + tag);
            root.AddComponent<StaffTimedEffect>().Lifetime = 1.5f; // outlives the fire staff's extra grenades
            var shot = root.AddComponent<StaffChargedShot>();
            shot.Element = style.Element;
            shot.Level = level;
            shot.BlockerMask = BlockerMask;
            switch (style.Element)
            {
                case StaffElement.Fire:
                    // staff_fire_upgraded2/3_zm: a grenade, 1300 in/s forward and 200 in/s up.

                    shot.GrenadeSpeed = 1300f * 0.0254f;
                    shot.GrenadeLift = 200f * 0.0254f;
                    shot.ExtraGrenades = level == 3 ? 2 : 1;
                    shot.GrenadePrefab = BuildFireGrenade(data, style, sounds, level, tag);
                    break;
                case StaffElement.Ice:
                    shot.BoltSpeed = Speed(data, 2000f);
                    shot.BoltPrefab = BuildIceBolt(data, style, sounds, level, tag);
                    break;
                case StaffElement.Lightning:
                    shot.OrbStartDistance = 100f * 0.0254f;
                    shot.OrbRange = (level == 3 ? 1200f : 800f) * 0.0254f; // staff_lightning_get_shot_range
                    shot.OrbTravelTime = 8f;
                    shot.OrbPrefab = BuildLightningOrb(data, style, sounds, level, tag);
                    break;
                case StaffElement.Wind:
                    shot.BoltSpeed = Speed(data, 1500f);
                    StaffWhirlwind whirlwind = BuildWhirlwind(data, style, sounds, level, tag);
                    // Charged, the ball is not cut short: it flies its full 5 s until it lands.
                    shot.BoltPrefab = BuildWindBall(data, style, "WindChargeBall" + tag, 1.6f, data.stats.projectileLifetime > 0f ? data.stats.projectileLifetime : 5f,
                                                    0.25f, 0f, whirlwind.gameObject, whirlwind.gameObject);
                    break;
            }
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffChargedShot>();
        }

        // --- fire: lobbed grenades that burn where they land

        private static StaffFireGrenade BuildFireGrenade(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level, string tag)
        {
            var root = new GameObject("FireGrenade" + tag);
            float scale = level == 3 ? 1.8f : 1.5f;
            AddLight(root, style.Glow, 4f, 3f);
            AddLoop(root, Sound(sounds, "proj_loop"));
            Emit(root.transform, "Core", Fx(data, "fxt_fire_flame_base_01"), false, V(0.16f, 0.28f), V(0f, 0f), V(0.22f * scale, 0.34f * scale), 0, 70f,
                 ParticleSystemShapeType.Sphere, 0.04f, 0f, new Color(1f, 0.85f, 0.6f), new Color(1f, 0.45f, 0.1f), 0f, false, true, 1f);
            ParticleSystem trail = Emit(root.transform, "Trail", Fx(data, "fxt_fire_anim_flicker_1"), false, V(0.3f, 0.55f), V(0f, 0f), V(0.14f * scale, 0.24f * scale), 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.05f, 0f, new Color(1f, 0.6f, 0.2f), new Color(0.6f, 0.1f, 0.02f), 0f, true, true, 1f);
            var trailEmission = trail.emission;
            trailEmission.rateOverDistance = 40f;
            Shrink(trail);
            ParticleSystem embers = Emit(root.transform, "Embers", Fx(data, "fxt_debris_fire_ember_cloud_01"), false, V(0.5f, 1.1f), V(0.2f, 0.9f), V(0.02f, 0.05f), 0, 45f,
                 ParticleSystemShapeType.Sphere, 0.06f, 0f, new Color(1f, 0.7f, 0.3f), new Color(1f, 0.3f, 0.05f), -0.15f, true, true, 1f);

            var grenade = root.AddComponent<StaffFireGrenade>();
            grenade.Gravity = 800f * 0.0254f;
            grenade.ArmTime = 0.5f;
            grenade.Fuse = 10f; // fuseTime
            grenade.CastRadius = 0.08f;
            grenade.BlockerMask = BlockerMask;
            // projExplosionEffect fx_zmb_staff_fire_ug_impact_exp_1, projExplosionSound ..._proj_impact_upgraded
            grenade.ExplosionPrefab = BuildFireBurst(data, style, "FireGrenadeBurst" + tag, scale, Sound(sounds, "impact_upgraded") ?? Sound(sounds, "impact"));
            grenade.PoolPrefab = BuildFirePool(data, style, sounds, level, tag);
            grenade.DetachOnEnd = new[] { trail, embers };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffFireGrenade>();
        }

        private static StaffFirePool BuildFirePool(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level, string tag)
        {
            var root = new GameObject("FirePool" + tag);
            float radius = (level == 3 ? 100f : 80f) * 0.0254f; // fire_staff_area_of_effect aoe_radius
            var pool = root.AddComponent<StaffFirePool>();
            pool.Radius = radius;
            pool.Duration = 5f;
            pool.Step = 0.2f;
            pool.Bo2Damage = level == 3 ? 20000f : 11500f;     // get_impact_damage
            pool.Bo2BurnPerSecond = level == 3 ? 450f : 300f;  // get_damage_per_second
            pool.BurnTime = 8f;                                // get_damage_duration
            pool.BlockerMask = BlockerMask;
            pool.PoolLight = AddLight(root, style.Glow, radius * 3.5f, 3f);

            AudioEvent loopSound = Sound(sounds, "grenade_loop");
            AddLoop(root, loopSound);
            pool.Loop = root.GetComponent<AudioSource>();

            // Ground fire across the radius: the particle discs lie flat.
            var flat = new GameObject("Ground").transform;
            flat.SetParent(root.transform, false);
            flat.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ParticleSystem flames = Emit(flat, "Flames", Fx(data, "fxt_fire_anim_flicker_1"), false, V(0.6f, 1.0f), V(0f, 0.2f), V(0.35f, 0.8f), 0, 55f * radius,
                 ParticleSystemShapeType.Circle, radius, 0f, new Color(1f, 0.75f, 0.4f), new Color(0.7f, 0.15f, 0.02f), -0.25f, true, true, 1f);
            ParticleSystem puffs = Emit(flat, "Puffs", Fx(data, "fxt_fire_flame_base_01"), false, V(0.5f, 0.9f), V(0f, 0.2f), V(0.5f, 1.1f), 0, 12f * radius,
                 ParticleSystemShapeType.Circle, radius * 0.8f, 0f, new Color(1f, 0.85f, 0.6f), new Color(0.6f, 0.15f, 0.03f), -0.15f, true, true, 1f, 0.8f);
            ParticleSystem embers = Emit(flat, "Embers", Fx(data, "fxt_debris_fire_ember_cloud_01"), false, V(0.8f, 1.6f), V(0.3f, 1.2f), V(0.02f, 0.05f), 0, 20f * radius,
                 ParticleSystemShapeType.Circle, radius, 0f, new Color(1f, 0.75f, 0.35f), new Color(1f, 0.3f, 0.05f), -0.3f, true, true, 1f);
            ParticleSystem smoke = Emit(flat, "Smoke", Fx(data, "fxt_smoke_tile"), true, V(1.8f, 2.8f), V(0.2f, 0.5f), V(0.8f, 1.4f), 0, 2.5f * radius,
                 ParticleSystemShapeType.Circle, radius * 0.7f, 0f, new Color(0.15f, 0.13f, 0.12f), new Color(0.1f, 0.1f, 0.1f), -0.08f, true, true, 1f, 0.35f);
            Grow(smoke, 2f);
            pool.StopOnEnd = new[] { flames, puffs, embers, smoke };

            pool.BodyFlamesPrefab = BuildBodyFlames(data, style, "BodyFlames" + tag);
            // The last step doubles the radius; it ends with wpn_firestaff_proj_impact.
            pool.FinalBurstPrefab = BuildFireBurst(data, style, "FirePoolBurst" + tag, radius * 2f / 1.2f, Sound(sounds, "impact"));
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffFirePool>();
        }

        /// <summary>A burst of flame (the grenade going off, the pool's last flare).</summary>
        private static GameObject BuildFireBurst(AssemblyData data, ElementStyle style, string name, float scale, AudioEvent sound)
        {
            GameObject root = OneShot(name, style.Glow, 6f * scale, 6f, 0.45f, 3f, sound);
            ParticleSystem burst = Emit(root.transform, "Burst", Fx(data, "fxt_fire_flame_base_01"), false, V(0.35f, 0.75f), V(1.5f * scale, 4f * scale), V(0.5f * scale, 1f * scale), 18, 0f,
                 ParticleSystemShapeType.Sphere, 0.15f, 0f, new Color(1f, 0.9f, 0.7f), new Color(0.5f, 0.12f, 0.03f), 0f);
            Grow(burst);
            Emit(root.transform, "Flames", Fx(data, "fxt_fire_anim_flicker_1"), false, V(0.8f, 1.4f), V(0.2f, 0.8f), V(0.4f * scale, 0.8f * scale), 10, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.4f * scale, 0f, new Color(1f, 0.75f, 0.4f), new Color(0.6f, 0.15f, 0.02f), -0.15f);
            Emit(root.transform, "Embers", Fx(data, "fxt_debris_fire_ember_cloud_01"), false, V(0.8f, 1.8f), V(2f, 6f), V(0.02f, 0.06f), 45, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.1f, 0f, new Color(1f, 0.8f, 0.4f), new Color(1f, 0.3f, 0.05f), 0.5f);
            ParticleSystem smoke = Emit(root.transform, "Smoke", Fx(data, "fxt_smoke_tile"), true, V(1.5f, 2.6f), V(0.3f, 0.9f), V(0.8f * scale, 1.4f * scale), 6, 0f,
                 ParticleSystemShapeType.Hemisphere, 0.3f, 0f, new Color(0.15f, 0.13f, 0.12f), new Color(0.1f, 0.1f, 0.1f), -0.05f, true, false, 0.5f, 0.35f);
            Grow(smoke);
            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab");
        }

        /// <summary>Flames on a burning sosig (BO2's fire_char_fx), for as long as it burns.</summary>
        private static GameObject BuildBodyFlames(AssemblyData data, ElementStyle style, string name)
        {
            var root = new GameObject(name);
            root.AddComponent<StaffTimedEffect>().Lifetime = 9f;
            AddLight(root, style.Glow, 2.5f, 1.5f);
            Emit(root.transform, "Flames", Fx(data, "fxt_fire_anim_flicker_1"), false, V(0.4f, 0.7f), V(0.1f, 0.4f), V(0.25f, 0.4f), 0, 28f,
                 ParticleSystemShapeType.Sphere, 0.25f, 0f, new Color(1f, 0.75f, 0.4f), new Color(0.7f, 0.15f, 0.02f), -0.3f, true, true, 1f);
            Emit(root.transform, "Embers", Fx(data, "fxt_debris_fire_ember_cloud_01"), false, V(0.6f, 1.2f), V(0.2f, 0.8f), V(0.02f, 0.04f), 0, 12f,
                 ParticleSystemShapeType.Sphere, 0.25f, 0f, new Color(1f, 0.75f, 0.35f), new Color(1f, 0.3f, 0.05f), -0.3f, true, true, 1f);
            return SavePrefab(root, PrefabDir + "FX/" + name + ".prefab");
        }

        // --- ice: a bolt that leaves a blizzard where it ends

        private static StaffBolt BuildIceBolt(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level, string tag)
        {
            var root = new GameObject("IceBolt" + tag);
            AddProp(root.transform, "IceShard", 0.5f);
            AddLight(root, style.Glow, 2f, 1.5f);
            Color frost = new Color(0.85f, 0.95f, 1f);
            Emit(root.transform, "Glint", Fx(data, "fxt_light_flare_cool"), false, V(0.05f, 0.08f), V(0f, 0f), V(0.3f, 0.4f), 0, 30f,
                 ParticleSystemShapeType.Sphere, 0.01f, 0f, Color.white, style.Glow, 0f, false, true, 1f, 0.7f);
            // fx_zmb_staff_ice_ug_trail_bolt
            ParticleSystem trail = Emit(root.transform, "Trail", Fx(data, "fxt_smk_def_0"), true, V(0.4f, 0.7f), V(0f, 0.2f), V(0.12f, 0.2f), 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.04f, 0f, frost, style.Glow, 0f, true, true, 1f, 0.3f);
            Grow(trail, 2.5f);
            var trailEmission = trail.emission;
            trailEmission.rateOverDistance = 6f;
            ParticleSystem glints = Emit(root.transform, "Glints", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.2f, 0.4f), V(0f, 0.3f), V(0.02f, 0.05f), 0, 0f,
                 ParticleSystemShapeType.Sphere, 0.03f, 0f, Color.white, frost, 0f, true, true, 1f, 0.9f);
            var glintEmission = glints.emission;
            glintEmission.rateOverDistance = 20f;

            var bolt = root.AddComponent<StaffBolt>();
            bolt.Lifetime = data.stats.projectileLifetime > 0f ? data.stats.projectileLifetime : 0.3f; // the blizzard forms where it ends
            bolt.CastRadius = 0.05f;
            bolt.BlockerMask = BlockerMask;
            GameObject blizzard = BuildBlizzard(data, style, sounds, level, tag);
            bolt.ImpactPrefab = blizzard;
            bolt.EndPrefab = blizzard;
            bolt.DetachOnEnd = new[] { trail, glints };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffBolt>();
        }

        private static GameObject BuildBlizzard(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level, string tag)
        {
            var root = new GameObject("Blizzard" + tag);
            var storm = root.AddComponent<StaffBlizzard>();
            storm.Radius = 175f * 0.0254f;       // ice_staff_blizzard_do_kills: 30625 = 175^2
            storm.Duration = level == 3 ? 9f : 6f;
            storm.StartDelay = 1f;
            storm.Tick = 0.1f;
            storm.Rise = 33f * 0.0254f;
            storm.Bo2Damage = 3300f;
            storm.IciclePrefab = BuildIcicle(data, style);
            storm.FrostPrefab = BuildFrost(data, style, Sound(sounds, "freeze"));
            storm.ShatterPrefab = BuildShatter(data, style, Sound(sounds, "shatter"));
            storm.StormLight = AddLight(root, style.Glow, 9f, 2.5f);
            AddImpactSound(root, Sound(sounds, "storm_imp"));
            var loopGo = new GameObject("Loop");
            loopGo.transform.SetParent(root.transform, false);
            AddLoop(loopGo, Sound(sounds, "storm"));
            storm.Loop = loopGo.GetComponent<AudioSource>();

            float r = storm.Radius;
            Color frost = new Color(0.8f, 0.92f, 1f);
            var swirl = new GameObject("Swirl").transform;
            swirl.SetParent(root.transform, false);
            swirl.gameObject.AddComponent<StaffSpin>().DegreesPerSecond = 80f;
            var flat = new GameObject("Flat").transform;
            flat.SetParent(swirl, false);
            flat.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ParticleSystem mist = Emit(flat, "Mist", Fx(data, "fxt_smk_def_0"), true, V(2f, 3f), V(0.2f, 0.6f), V(1.2f, 2f), 0, 3f * r,
                 ParticleSystemShapeType.Circle, r * 0.85f, 0f, frost, style.Glow, 0f, false, true, 1f, 0.3f);
            Grow(mist, 1.8f);
            var high = new GameObject("High").transform;
            high.SetParent(swirl, false);
            high.localPosition = new Vector3(0f, 2.2f, 0f);
            high.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ParticleSystem snow = Emit(high, "Snow", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(1.5f, 2.5f), V(0.2f, 0.8f), V(0.03f, 0.07f), 0, 28f * r,
                 ParticleSystemShapeType.Circle, r, 0f, Color.white, frost, 0.15f, false, true, 1f);
            ParticleSystem shards = Emit(high, "Shards", Fx(data, "fxt_debris_clump_rocks"), false, V(1f, 1.6f), V(0.5f, 1.5f), V(0.04f, 0.1f), 0, 6f * r,
                 ParticleSystemShapeType.Circle, r * 0.9f, 0f, Color.white, frost, 0.3f, false, true, 1f);
            ParticleSystem core = Emit(root.transform, "Core", Fx(data, "fxt_light_flare_cool"), false, V(0.8f, 1.2f), V(0f, 0f), V(1.4f, 2f), 0, 4f,
                 ParticleSystemShapeType.Sphere, 0.2f, 0f, Color.white, style.Glow, 0f, false, true, 1f, 0.35f);
            storm.StopOnEnd = new[] { mist, snow, shards, core };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab");
        }

        // --- lightning: a slow ball that drains everything around it

        private static StaffLightningOrb BuildLightningOrb(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level, string tag)
        {
            var root = new GameObject("LightningOrb" + tag);
            float scale = level == 3 ? 1.3f : 1f;
            AddLight(root, style.Glow, 6f * scale, 3f);
            AddLoop(root, Sound(sounds, "ball")); // wpn_lightningstaff_ball
            Color white = new Color(0.95f, 0.9f, 1f);
            ParticleSystem core = Emit(root.transform, "Core", Fx(data, "fxt_electric_tile"), false, V(0.08f, 0.16f), V(0f, 0f), V(0.5f * scale, 0.8f * scale), 0, 50f,
                 ParticleSystemShapeType.Sphere, 0.05f, 0f, white, style.Glow, 0f, false, true, 1f);
            ParticleSystem glow = Emit(root.transform, "Glow", Fx(data, "fxt_light_flare_cool"), false, V(0.1f, 0.16f), V(0f, 0f), V(1.0f * scale, 1.4f * scale), 0, 20f,
                 ParticleSystemShapeType.Sphere, 0.02f, 0f, white, style.Glow, 0f, false, true, 1f, 0.8f);
            ParticleSystem bolts = Emit(root.transform, "Bolts", Fx(data, "fxt_env_lighting_bolt_air"), false, V(0.06f, 0.14f), V(0f, 0f), V(0.6f * scale, 1.1f * scale), 0, 40f,
                 ParticleSystemShapeType.Sphere, 0.35f * scale, 0f, white, style.Glow, 0f, false, true, 1f);
            ParticleSystem sparks = Emit(root.transform, "Sparks", Fx(data, "fxt_spark_pcloud_blue_1"), false, V(0.3f, 0.7f), V(1f, 3f), V(0.03f, 0.06f), 0, 30f,
                 ParticleSystemShapeType.Sphere, 0.3f * scale, 0f, white, style.Glow, 0.3f, true, true, 1f);

            var orb = root.AddComponent<StaffLightningOrb>();
            orb.Range = (level == 3 ? 250f : 150f) * 0.0254f;    // get_lightning_blast_range
            orb.Bo2DamagePerPulse = level == 3 ? 3500f : 2500f;  // get_lightning_ball_damage_per_sec
            orb.ActiveTime = 8f;
            orb.LingerTime = 3f;
            orb.StunTime = 1.1f;
            orb.BlockerMask = BlockerMask;
            orb.ZapPrefab = BuildShock(data, style, Sound(sounds, "arc") ?? Sound(sounds, "zap"), "OrbZap" + tag);
            orb.EndPrefab = BuildLightningImpact(data, style, Sound(sounds, "ball_explo"), "OrbBurst" + tag, 1.6f * scale);
            orb.ArcMaterial = ArcMaterial(data, style);
            orb.ArcWidth = 0.1f;
            orb.Loop = root.GetComponent<AudioSource>();
            orb.StopOnEnd = new[] { core, glow, bolts, sparks };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffLightningOrb>();
        }

        /// <summary>The charged ball's arcs: BO2's bolt strip texture, stretched along a line.</summary>
        private static Material ArcMaterial(AssemblyData data, ElementStyle style)
        {
            string dir = FxDir + "Materials/";
            EnsureFolder(dir);
            string path = dir + "lightning_arc.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Particles/Additive"));
                AssetDatabase.CreateAsset(mat, path);
            }
            FxData fx = Fx(data, "fxt_env_lightning_bolt_trail2");
            if (fx != null) mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(FxDir + fx.file);
            mat.mainTextureScale = new Vector2(1f, 0.5f); // one of the two strips
            mat.SetColor("_TintColor", new Color(style.Glow.r, style.Glow.g, style.Glow.b, 0.8f));
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // --- wind: a whirlwind where the charged ball lands

        private static StaffWhirlwind BuildWhirlwind(AssemblyData data, ElementStyle style, Dictionary<string, AudioEvent> sounds, int level, string tag)
        {
            var root = new GameObject("Whirlwind" + tag);
            var wind = root.AddComponent<StaffWhirlwind>();
            wind.Duration = level * 3.5f;                     // chargeshotlevel * 3.5
            wind.Range = 250f * 0.0254f;                      // get_air_blast_range
            wind.SourceRadius = 100f * 0.0254f;               // staff_air_find_source: 10000 = 100^2
            wind.DragSpeed = (level == 3 ? 62.5f : 50f) * 0.0254f;
            wind.KillDistance = 30f * 0.0254f;                // n_fling_range_sq 900
            wind.GrabInterval = 0.5f;
            wind.StartDelay = 0.5f;
            wind.LaunchSpeed = 300f * 0.0254f;
            wind.BlockerMask = BlockerMask;
            wind.GustPrefab = BuildDustPuff(data, "WhirlGust" + tag, null, 0.4f);
            AddLight(root, style.Glow, 6f, 1.2f);
            AddImpactSound(root, Sound(sounds, "tornado_imp"));
            var loopGo = new GameObject("Loop");
            loopGo.transform.SetParent(root.transform, false);
            AddLoop(loopGo, Sound(sounds, "tornado"));
            wind.Loop = loopGo.GetComponent<AudioSource>();

            Color air = new Color(1f, 0.97f, 0.85f);
            Color dustColor = new Color(0.75f, 0.68f, 0.55f);
            var funnel = new GameObject("Funnel").transform;
            funnel.SetParent(root.transform, false);
            funnel.gameObject.AddComponent<StaffSpin>().DegreesPerSecond = 360f;
            var upward = new GameObject("Up").transform;
            upward.SetParent(funnel, false);
            upward.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ParticleSystem column = Emit(upward, "Column", Fx(data, "fxt_debris_heli_rotor_swirl"), false, V(1f, 1.3f), V(2.5f, 3.5f), V(0.7f, 1.1f), 0, 30f,
                 ParticleSystemShapeType.Cone, 0.4f, 18f, air, style.Glow, 0f, false, true, 1f, 0.5f);
            Grow(column, 3f);
            Spin(column, 6f);
            ParticleSystem rings = Emit(upward, "Rings", Fx(data, "fxt_fx_ring_spiral"), false, V(1f, 1.4f), V(2f, 3f), V(0.8f, 1.2f), 0, 8f,
                 ParticleSystemShapeType.Cone, 0.2f, 5f, air, style.Glow, 0f, false, true, 1f, 0.5f);
            Grow(rings, 2.5f);
            Spin(rings, 10f);
            ParticleSystem debris = Emit(upward, "Debris", Fx(data, "fxt_debris_clump_rocks"), false, V(1f, 2f), V(2f, 5f), V(0.04f, 0.1f), 0, 20f,
                 ParticleSystemShapeType.Cone, 0.8f, 25f, dustColor, new Color(0.5f, 0.45f, 0.38f), 0.5f, false, true, 1f, 0.9f);
            var ground = new GameObject("Ground").transform;
            ground.SetParent(funnel, false);
            ground.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            ParticleSystem dust = Emit(ground, "Dust", Fx(data, "fxt_smk_def_0"), true, V(1.2f, 2f), V(0.5f, 1.5f), V(0.8f, 1.4f), 0, 14f,
                 ParticleSystemShapeType.Circle, 1.5f, 0f, dustColor, new Color(0.5f, 0.45f, 0.38f), 0f, false, true, 1f, 0.35f);
            Grow(dust, 2f);
            wind.StopOnEnd = new[] { column, rings, debris, dust };
            return SavePrefab(root, PrefabDir + "FX/" + root.name + ".prefab").GetComponent<StaffWhirlwind>();
        }

        // --- static models (Ripped/Props)

        /// <summary>Adds a ripped static model under <paramref name="parent"/>, scaled to <paramref name="length"/> metres along Z.</summary>
        private static void AddProp(Transform parent, string prop, float length)
        {
            var data = JsonUtility.FromJson<AssemblyData>(File.ReadAllText(PropsDir + prop + ".json"));
            MeshData m = data.meshes[0];
            EnsureFolder(GeneratedDir + "Props/");
            Mesh mesh = BuildMesh(m, GeneratedDir + "Props/" + m.name + ".asset");
            var go = new GameObject(prop);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = m.submeshes.Select(s => RippedMaterials.Get(s.material.name, s.material.albedo, s.material.normal, s.material.emissive)).ToArray();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.localScale = Vector3.one * (length / Mathf.Max(mesh.bounds.size.z, 0.001f));
        }

        private static float Speed(AssemblyData data, float fallbackInchesPerSecond)
        {
            return (data.stats.projectileSpeed > 0f ? data.stats.projectileSpeed : fallbackInchesPerSecond) * 0.0254f;
        }

        // --- effect helpers

        private static Vector2 V(float min, float max)
        {
            return new Vector2(min, max);
        }

        private static Light AddLight(GameObject go, Color color, float range, float intensity)
        {
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            return light;
        }

        private static void AddLoop(GameObject go, AudioEvent loop)
        {
            if (loop == null || loop.Clips.Count == 0) return;
            var source = go.AddComponent<AudioSource>();
            source.clip = loop.Clips[0];
            source.loop = true;
            source.playOnAwake = true;
            source.spatialBlend = 1f;
            source.volume = loop.VolumeRange.x;
            source.minDistance = 1f;
            source.maxDistance = 60f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.dopplerLevel = 0f;
        }

        /// <summary>A 3D source that fades with distance (H3VR's explosion pool is heard map-wide).</summary>
        private static void AddImpactSound(GameObject go, AudioEvent sound)
        {
            if (sound == null || sound.Clips.Count == 0) return;
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic; // halves roughly every doubling of distance
            source.minDistance = 2.5f;
            source.maxDistance = 150f;
            source.dopplerLevel = 0f;
            var impact = go.AddComponent<StaffImpactSound>();
            impact.Clips = sound.Clips.ToArray();
            impact.VolumeRange = sound.VolumeRange;
            impact.PitchRange = sound.PitchRange;
        }

        /// <summary>Root of a one-shot effect: a flash of light that fades, an optional sound, and removal after a while.</summary>
        private static GameObject OneShot(string name, Color color, float range, float intensity, float lightFade, float lifetime, AudioEvent sound)
        {
            var root = new GameObject(name);
            AddImpactSound(root, sound);
            Light light = AddLight(root, color, range, intensity);
            var timed = root.AddComponent<StaffTimedEffect>();
            timed.Lifetime = lifetime;
            timed.FlashLight = light;
            timed.LightFadeTime = lightFade;
            return root;
        }

        /// <summary>One particle layer: a burst (and/or a rate) of textured billboards fading from start to end colour.</summary>
        private static ParticleSystem Emit(Transform parent, string name, FxData fx, bool alphaBlended, Vector2 life, Vector2 speed, Vector2 size,
            int burst, float rate, ParticleSystemShapeType shape, float radius, float angle, Color start, Color end, float gravity,
            bool world = true, bool loop = false, float duration = 0.5f, float peakAlpha = 1f)
        {
            ParticleSystem ps = Particles(parent, name, fx, loop, alphaBlended);
            var main = ps.main;
            main.duration = duration;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = gravity;
            main.simulationSpace = world ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            main.maxParticles = Mathf.Max(burst, 1) + Mathf.CeilToInt(rate * 1.5f) + 10;
            var emission = ps.emission;
            emission.rateOverTime = rate;
            if (burst > 0) emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });
            var sh = ps.shape;
            sh.shapeType = shape;
            sh.radius = radius;
            sh.angle = angle;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(Gradient(start, end, peakAlpha));
            return ps;
        }

        private static void Grow(ParticleSystem ps, float to)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(to, new AnimationCurve(new Keyframe(0f, 1f / to), new Keyframe(1f, 1f)));
        }

        private static void Drag(ParticleSystem ps, float dampen)
        {
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(0f);
            limit.dampen = dampen;
        }

        private static void Spin(ParticleSystem ps, float radiansPerSecond)
        {
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-radiansPerSecond, radiansPerSecond);
        }

        private static ParticleSystem Particles(Transform parent, string name, FxData fx, bool loop, bool alphaBlended)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = loop;
            main.duration = loop ? 1f : 0.5f;
            main.playOnAwake = true;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.material = ParticleMaterial(fx, alphaBlended);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            if (fx != null && (fx.columns > 1 || fx.rows > 1))
            {
                var sheet = ps.textureSheetAnimation;
                sheet.enabled = true;
                sheet.numTilesX = fx.columns;
                sheet.numTilesY = fx.rows;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
                sheet.cycleCount = 1;
            }
            return ps;
        }

        private static Material ParticleMaterial(FxData fx, bool alphaBlended)
        {
            string dir = FxDir + "Materials/";
            EnsureFolder(dir);
            string name = (fx != null ? fx.name : "none") + (alphaBlended ? "_alpha" : "_add");
            string path = dir + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find(alphaBlended ? "Particles/Alpha Blended" : "Particles/Additive"));
                AssetDatabase.CreateAsset(mat, path);
            }
            if (fx != null) mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(FxDir + fx.file);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void Fade(ParticleSystem ps, Color start, Color end)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(Gradient(start, end, 1f));
        }

        private static void Shrink(ParticleSystem ps)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));
        }

        private static void Grow(ParticleSystem ps)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.3f)));
        }

        private static Gradient Gradient(Color start, Color end, float peakAlpha)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(peakAlpha, 0.1f), new GradientAlphaKey(peakAlpha * 0.6f, 0.6f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        // ------------------------------------------------------------------ data assets

        private static Dictionary<string, AudioEvent> BuildSounds(AssemblyData data)
        {
            var events = new Dictionary<string, AudioEvent>();
            foreach (SoundData s in data.sounds)
            {
                var ev = new AudioEvent();
                ev.Clips = s.files.Select(f => AssetDatabase.LoadAssetAtPath<AudioClip>(SoundDir + f)).Where(c => c != null).ToList();
                ev.VolumeRange = new Vector2(s.volume[0], s.volume[1]) * SoundVolume;
                ev.PitchRange = new Vector2(s.pitch[0], s.pitch[1]);
                if (ev.Clips.Count == 0) Debug.LogWarning("[OriginsStaffs] No clips loaded for sound " + s.key);
                events[s.key] = ev;
            }
            return events;
        }

        private static AudioEvent Sound(Dictionary<string, AudioEvent> sounds, string key)
        {
            AudioEvent ev;
            return sounds.TryGetValue(key, out ev) ? ev : null;
        }

        private static FVRObject BuildItem(AssemblyData data, string prefabPath)
        {
            var item = LoadOrCreate<FVRObject>(DataDir + data.name + "_Item.asset");
            item.ItemID = "OriginsStaffs." + data.name;
            item.DisplayName = DisplayName(data);
            item.Category = FVRObject.ObjectCategory.Firearm;
            item.SpawnedFromId = item.ItemID;
            item.TagEra = FVRObject.OTagEra.WW1; // Origins is set in 1918
            item.TagSet = FVRObject.OTagSet.SciFiFictional;
            item.TagFirearmSize = FVRObject.OTagFirearmSize.Oversize;
            item.TagFirearmAction = FVRObject.OTagFirearmAction.Automatic;
            item.TagFirearmRoundPower = data.element == "fire" ? FVRObject.OTagFirearmRoundPower.Fire : FVRObject.OTagFirearmRoundPower.Exotic;
            item.TagFirearmCountryOfOrigin = FVRObject.OTagFirearmCountryOfOrigin.Fictional;
            item.TagFirearmFirstYear = 1918;
            item.TagFirearmFiringModes = new List<FVRObject.OTagFirearmFiringMode> { FVRObject.OTagFirearmFiringMode.FullAuto };
            item.TagFirearmFeedOption = new List<FVRObject.OTagFirearmFeedOption> { FVRObject.OTagFirearmFeedOption.InternalMag };
            item.MagazineCapacity = Mathf.RoundToInt(data.stats.clipSize);
            EditorUtility.SetDirty(item);

            // OtherLoader resolves the prefab from this path at load time (see the OtherLoader sample).
            var so = new SerializedObject(item);
            so.FindProperty("m_anvilPrefab.AssetName").stringValue = prefabPath;
            so.ApplyModifiedPropertiesWithoutUndo();
            return item;
        }

        private static ItemSpawnerEntry BuildSpawnerEntry(AssemblyData data, FVRObject item)
        {
            var entry = LoadOrCreate<ItemSpawnerEntry>(DataDir + data.name + "_SpawnerEntry.asset");
            entry.MainObjectID = item.ItemID;
            entry.SpawnWithIDs = new List<string>();
            entry.SecondaryObjectIDs = new List<string>();
            entry.SpawnWithObjs = new List<FVRObject>();
            entry.SecondaryObjs = new List<FVRObject>();
            entry.EntryPath = SpawnerFolder + "/" + item.ItemID;
            entry.Page = ItemSpawnerV2.PageMode.Firearms;
            entry.SubCategory = ItemSpawnerID.ESubCategory.Exotic;
            entry.EntryIcon = AssetDatabase.LoadAssetAtPath<Sprite>(FxDir + data.icon);
            entry.DisplayName = item.DisplayName;
            entry.IsDisplayedInMainEntry = true;
            entry.ModTags = new List<string> { "Origins Staffs" };
            entry.TutorialBlockIDs = new List<string>();
            entry.IsModded = true;
            EditorUtility.SetDirty(entry);
            return entry;
        }

        /// <summary>
        /// OtherLoader shows a folder for every path segment; without an entry of its own (no
        /// MainObjectID) the folder is an unlabeled white tile.
        /// </summary>
        private static ItemSpawnerEntry BuildFolderEntry()
        {
            var entry = LoadOrCreate<ItemSpawnerEntry>(DataDir + "OriginsStaffs_Folder.asset");
            entry.MainObjectID = "";
            entry.SpawnWithIDs = new List<string>();
            entry.SecondaryObjectIDs = new List<string>();
            entry.SpawnWithObjs = new List<FVRObject>();
            entry.SecondaryObjs = new List<FVRObject>();
            entry.EntryPath = SpawnerFolder;
            entry.Page = ItemSpawnerV2.PageMode.Firearms;
            entry.SubCategory = ItemSpawnerID.ESubCategory.Exotic;
            entry.EntryIcon = AssetDatabase.LoadAssetAtPath<Sprite>(FolderIcon);
            entry.DisplayName = "Origins Staffs";
            entry.IsDisplayedInMainEntry = true;
            entry.ModTags = new List<string> { "Origins Staffs" };
            entry.TutorialBlockIDs = new List<string>();
            entry.IsModded = true;
            if (entry.EntryIcon == null) Debug.LogWarning("[OriginsStaffs] Folder icon missing: " + FolderIcon);
            EditorUtility.SetDirty(entry);
            return entry;
        }

        private static FVRFirearmAudioSet BuildAudioSet(AssemblyData data, Dictionary<string, AudioEvent> sounds)
        {
            var set = LoadOrCreate<FVRFirearmAudioSet>(DataDir + data.name + "_Audio.asset");
            set.FTP = new FVRFirearmAudioSet.ForceTypeProperties();
            set.Shots_Main = Sound(sounds, "fire") ?? new AudioEvent();
            set.UsesTail_Main = false;
            set.UsesTail_Suppressed = false;
            AudioEvent click = Sound(sounds, "pickup") ?? new AudioEvent();
            set.HandleBack = Sound(sounds, "reload_1") ?? click;
            set.HandleForward = Sound(sounds, "reload_4") ?? click;
            set.FireSelector = click;
            EditorUtility.SetDirty(set);
            return set;
        }

        private static FVRFireArmRecoilProfile BuildRecoil()
        {
            // A staff throwing a fireball: a firm push and some muzzle rise, nothing like a rifle.
            var r = LoadOrCreate<FVRFireArmRecoilProfile>(DataDir + "StaffRecoil.asset");
            r.VerticalRotPerShot = 4f;
            r.HorizontalRotPerShot = 1.2f;
            r.MaxVerticalRot = 14f;
            r.MaxHorizontalRot = 6f;
            r.VerticalRotRecovery = 9f;
            r.HorizontalRotRecovery = 9f;
            r.ZLinearPerShot = 0.018f;
            r.ZLinearMax = 0.05f;
            r.ZLinearRecovery = 7f;
            r.XYLinearPerShot = 0.004f;
            r.XYLinearMax = 0.01f;
            r.XYLinearRecovery = 7f;
            r.MassDriftIntensity = 0.4f;
            r.MaxMassDriftMagnitude = 4f;
            r.MaxMassMaxRotation = 4f;
            r.MassDriftRecoveryFactor = 5f;
            EditorUtility.SetDirty(r);
            return r;
        }

        private static void BuildPackage(List<BuiltStaff> staffs)
        {
            var item = LoadOrCreate<MeatKit.OtherLoaderBuildItem>(BuildDir + "OriginsStaffs_Items.asset");
            item.BundleName = "origins_staffs";
            item.Prefabs = staffs.Select(s => s.Prefab).ToList();
            item.SpawnerEntries = staffs.Select(s => s.Entry).ToList();
            item.SpawnerEntries.Insert(0, BuildFolderEntry());
            item.FVRObjects = staffs.Select(s => s.Item).ToList();
            item.AccuracyCharts = new List<FVRFireArmMechanicalAccuracyChart>();
            item.RoundData = new List<FVRFireArmRoundDisplayData>();
            item.HandlingGrabSets = new List<HandlingGrabSet>();
            item.HandlingReleaseSets = new List<HandlingReleaseSet>();
            item.HandlingReleaseSlotSets = new List<HandlingReleaseIntoSlotSet>();
            item.AudioBulletImpactSets = new List<AudioBulletImpactSet>();
            item.AudioImpactSets = new List<AudioImpactSet>();
            item.TutorialBlocks = new List<TutorialBlock>();
            item.TutorialVideos = new List<UnityEngine.Object>();
            item.OnDemand = true;
            EditorUtility.SetDirty(item);

            var root = LoadOrCreate<MeatKit.OtherLoaderBuildRoot>(BuildDir + "OriginsStaffs_Root.asset");
            root.BuildItemsFirst = new List<MeatKit.OtherLoaderBuildItem>();
            root.BuildItemsAny = new List<MeatKit.OtherLoaderBuildItem> { item };
            root.BuildItemsLast = new List<MeatKit.OtherLoaderBuildItem>();
            root.SelfLoading = true;
            EditorUtility.SetDirty(root);

            var profile = LoadOrCreate<MeatKit.BuildProfile>(BuildDir + "OriginsStaffs_Profile.asset");
            profile.PackageName = "OriginsStaffs";
            profile.Author = "LJIndustries";
            profile.Version = ManifestVersion();
            profile.Description = "The four elemental staffs from Black Ops 2 Zombies' Origins, reloaded by hand with glowing crystals.";
            profile.WebsiteURL = "https://github.com/3WiseStooges/OriginsStaffs";
            profile.Icon = AssetDatabase.LoadAssetAtPath<Texture2D>(PackageDir + "icon.png");
            profile.ReadMe = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(PackageDir + "README.md");
            profile.Changelog = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(PackageDir + "CHANGELOG.md");
            profile.AdditionalNamespaces = new[] { "OriginsStaffs" };
            profile.BuildItems = new MeatKit.BuildItem[] { root };
            profile.BuildAction = MeatKit.BuildAction.CopyToProfile;
            if (string.IsNullOrEmpty(profile.OutputProfile))
            {
                profile.OutputProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "r2modmanPlus-local/H3VR/profiles/Default").Replace('\\', '/');
            }
            EditorUtility.SetDirty(profile);
        }

        // ------------------------------------------------------------------ helpers

        private static string DisplayName(AssemblyData data)
        {
            string name;
            return DisplayNames.TryGetValue(data.name, out name) ? name : data.name.Replace('_', ' ');
        }

        private static string ManifestVersion()
        {
            string path = Path.Combine("..", "manifest.json");
            if (!File.Exists(path)) return "0.1.0";
            string text = File.ReadAllText(path);
            int i = text.IndexOf("\"version_number\"", StringComparison.Ordinal);
            if (i < 0) return "0.1.0";
            int start = text.IndexOf('"', text.IndexOf(':', i) + 1) + 1;
            return text.Substring(start, text.IndexOf('"', start) - start);
        }

        private static FxData Fx(AssemblyData data, string name)
        {
            return data.fx.FirstOrDefault(f => f.name == name);
        }

        private static Vector3 Point(AssemblyData data, string name)
        {
            PointData p = data.points.FirstOrDefault(x => x.name == name);
            if (p == null) throw new Exception("[OriginsStaffs] Assembly " + data.name + " has no point '" + name + "'");
            return V3(p.pos);
        }

        private static float BoneZ(Transform[] bones, string name)
        {
            Transform t = bones.First(b => b.name == name);
            return t.position.z; // root sits at the world origin while building
        }

        private static Transform Child(Transform parent, string name, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            t.gameObject.layer = parent.gameObject.layer;
            return t;
        }

        private static void AddBox(GameObject go, bool trigger, Vector3 center, Vector3 size)
        {
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = trigger;
            box.center = center;
            box.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        }

        private static void AddCapsule(GameObject go, bool trigger, float z0, float z1, float radius)
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.isTrigger = trigger;
            cap.direction = 2;
            cap.radius = radius;
            cap.center = new Vector3(0f, 0f, (z0 + z1) * 0.5f);
            cap.height = Mathf.Abs(z1 - z0) + radius * 2f;
        }

        private static GameObject SavePrefab(GameObject go, string path)
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            GameObject prefab = existing != null
                ? PrefabUtility.ReplacePrefab(go, existing, ReplacePrefabOptions.ReplaceNameBased)
                : PrefabUtility.CreatePrefab(path, go);
            UnityEngine.Object.DestroyImmediate(go);
            return prefab;
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/') + "/");
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void EnsureFolder(string dir)
        {
            dir = dir.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(dir)) return;
            string parent = Path.GetDirectoryName(dir).Replace('\\', '/');
            EnsureFolder(parent + "/");
            AssetDatabase.CreateFolder(parent, Path.GetFileName(dir));
        }

        private static Vector3 V3(float[] a)
        {
            return new Vector3(a[0], a[1], a[2]);
        }

        private static Quaternion Q(float[] a)
        {
            return new Quaternion(a[0], a[1], a[2], a[3]);
        }

        private static float[] Floats(string b64)
        {
            byte[] bytes = Convert.FromBase64String(b64);
            var result = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        private static int[] Ints(string b64)
        {
            byte[] bytes = Convert.FromBase64String(b64);
            var result = new int[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }
    }
}
