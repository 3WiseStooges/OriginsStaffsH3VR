using System.Collections;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// An Origins elemental staff: a firearm with no chamber or magazine. Its charges live in the
    /// crystal and refill when the reload lever is cycled, like BO2.
    ///
    /// Hands: the blue rings are the main hold: trigger, release button and reload lever (the
    /// rings' pose rides on <see cref="StaffLever"/>). The black handle (<see cref="StaffSupportGrip"/>)
    /// is a support hand with no trigger. Two-handed aim is computed here rather than by H3VR's
    /// front-grip code, which needs the front hand ahead of the trigger hand and turns unstable as
    /// the hands close in (as they do while pumping).
    /// </summary>
    public class OriginsStaff : FVRFireArm
    {
        [Header("Origins Staff")]
        public StaffLever Lever;
        public StaffSupportGrip Support;
        [Tooltip("Below this hand separation (m) two-handed aim blends back to one-handed.")]
        public float MinTwoHandSpan = 0.12f;

        [Header("Blades (upgraded staffs)")]
        [Tooltip("One collider per blade on the upgraded tip; also in MP.StabColliders and MP.HighDamageColliders.")]
        public Collider[] KnifeColliders = new Collider[0];
        [Tooltip("Per blade, a transform whose forward runs along the blade towards its point.")]
        public Transform[] KnifeStabDirections = new Transform[0];
        public int Capacity = 9;
        [Tooltip("Seconds between shots while the trigger is held (BO2 fireTime).")]
        public float RefireDelay = 0.75f;
        public bool FullAuto = true;
        public float StaffTriggerPull = 0.75f;
        public float StaffTriggerReset = 0.35f;

        [Header("Shot")]
        public StaffElement Element = StaffElement.Fire;
        [Tooltip("Fire and lightning: a ball fired from the muzzle.")]
        public StaffProjectile ProjectilePrefab;
        [Tooltip("Ice and wind: BO2's scripted cone shot, carried by icicles or a ball of wind.")]
        public StaffConeShot ConeShotPrefab;
        [Tooltip("Metres per second (BO2 projectileSpeed, e.g. fire 1200 in/s).")]
        public float ProjectileSpeed = 30.48f;
        public float SpreadDegrees = 0.5f;
        [Tooltip("Fire: BO2's fire_spread_shots, extra fireballs after each shot (2, base and upgraded). They cost no charge.")]
        public int SideShots = 0;
        [Tooltip("Seconds before each side shot (BO2: two network frames, 0.2 s).")]
        public float SideShotDelay = 0.2f;
        [Tooltip("Degrees off the aim, turning about the world's up axis: first one way, then the other (BO2: 15).")]
        public float SideShotYaw = 15f;
        [Tooltip("Side shots start this far ahead of the muzzle (BO2: 50 in).")]
        public float SideShotStart = 1.27f;
        [Tooltip("Side shots head for a point this far out, turned by SideShotYaw (BO2: 100 in). With a wall closer than this along the aim, there are none.")]
        public float SideShotReach = 2.54f;
        public LayerMask SideShotBlockers;
        public GameObject MuzzleFlashPrefab;

        [Header("Charge shot (upgraded staffs)")]
        [Tooltip("BO2 fireType 'Charge Shot': the shot goes on release; holding charges it up.")]
        public bool ChargeShot;
        [Tooltip("Held this long, the charge begins to show (sound, glow, rumble); a quicker tap is a plain shot.")]
        public float ChargeCueDelay = 0.25f;
        [Tooltip("Seconds held to reach charge level 2, then 3.")]
        public float[] ChargeLevelTimes = { 1f, 2f };
        [Tooltip("The charged shots for levels 2 and 3. Each spends one charge per level.")]
        public StaffChargedShot[] ChargedShots = new StaffChargedShot[0];
        public AudioSource ChargeLoop;
        public ParticleSystem ChargeParticles;
        public float ChargeParticleRate = 20f;
        public float ChargeGlowPerLevel = 0.6f;
        public AudioEvent AudEvent_Charge1;
        public AudioEvent AudEvent_Charge2;
        public AudioEvent AudEvent_Charge3;

        [Header("Crystal glow")]
        public Renderer[] GlowRenderers;
        public Light CrystalLight;
        [Tooltip("The crystal held in the tip: glows brighter than the reload crystal, with its own light.")]
        public Renderer[] HeadGlowRenderers = new Renderer[0];
        public Light HeadLight;
        public float HeadGlowBoost = 1.5f;
        public float HeadLightFull = 1.6f;
        public float GlowFull = 1.6f;
        public float GlowEmpty = 0.06f;
        public float LightFull = 1.2f;
        public float ShotFlash = 1.8f;

        [Header("Audio")]
        public AudioEvent AudEvent_Shot;
        public AudioEvent AudEvent_ShotRear;
        public AudioEvent AudEvent_Flux;
        public AudioEvent AudEvent_DryFire;
        public AudioEvent AudEvent_Recharged;
        public AudioEvent AudEvent_Handle;

        private int m_charges;
        private float m_refire;
        private float m_trigger;
        private int m_triggerFrame = -10;
        private bool m_triggerWasReset = true;
        private bool m_dryFired;
        private float m_flash;
        private MaterialPropertyBlock m_block;

        private bool m_charging;
        private bool m_chargeCued;
        private float m_chargeTime;
        private int m_chargeLevel;
        private float m_chargeBuzz;
        private float m_chargeGlow;

        // Carried by the lower hand alone after the trigger hand let go: held where it was
        // grabbed (no settling into the rings pose) and unable to fire.
        private bool m_supportCarry;
        private float m_savedPosInterp;
        private float m_savedRotInterp;

        public int Charges
        {
            get { return m_charges; }
        }

        public override void Awake()
        {
            base.Awake();
            m_charges = Capacity;
            m_block = new MaterialPropertyBlock();
            UpdateGlow();
            Debug.Log("[OriginsStaffs] " + name + " ready: " + Capacity + " charges, " + Element
                      + (ConeShotPrefab != null ? " cone shot" : " shot" + (SideShots > 0 ? " + " + SideShots + " side shots" : "")) + " per pull"
                      + (ChargeShot ? ", charges up to level " + (1 + ChargedShots.Length) : ""));
        }

        private bool SupportHeld
        {
            get { return Support != null && Support.IsHeld && Support.m_hand != null; }
        }

        public override void UpdateInteraction(FVRViveHand hand)
        {
            base.UpdateInteraction(hand);
            if (m_supportCarry) return; // the lower hand has no trigger
            m_trigger = hand.Input.TriggerFloat;
            m_triggerFrame = Time.frameCount;
            if (Lever != null && ReleasePressed(hand)) Lever.PressRelease();
        }

        private static bool ReleasePressed(FVRViveHand hand)
        {
            if (hand.IsInStreamlinedMode) return hand.Input.AXButtonDown;
            Vector2 axes = hand.Input.TouchpadAxes;
            return hand.Input.TouchpadDown && axes.magnitude > 0.2f && Vector2.Angle(axes, Vector2.up) <= 45f;
        }

        /// <summary>
        /// Two-handed aim: the staff runs from the handle hand up through the trigger hand, rolled
        /// to the trigger hand's up. Blends to the trigger hand alone as the hands meet, so the
        /// aim never comes from a vanishingly short line.
        /// </summary>
        public override Quaternion GetRotTarget()
        {
            Quaternion oneHand = base.GetRotTarget();
            if (!IsHeld || m_hand == null || QuickbeltSlot != null || !SupportHeld || m_supportCarry) return oneHand;

            Vector3 span = m_hand.Input.Pos - Support.m_hand.Input.Pos;
            float length = span.magnitude;
            if (length < 0.001f) return oneHand;
            Vector3 dir = span / length;
            Vector3 up = Vector3.ProjectOnPlane(oneHand * Vector3.up, dir);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(oneHand * Vector3.forward, dir);
            Quaternion twoHand = Quaternion.LookRotation(dir, up) * PoseOverride.localRotation;
            return Quaternion.Slerp(oneHand, twoHand, Mathf.InverseLerp(MinTwoHandSpan * 0.5f, MinTwoHandSpan, length));
        }

        public override bool IsForegripStabilized()
        {
            return SupportHeld;
        }

        public override bool IsTwoHandStabilized()
        {
            return SupportHeld;
        }

        public override void BeginInteraction(FVRViveHand hand)
        {
            // Lower hand carrying the staff and the other hand takes the rings again: that hand
            // becomes the trigger hand and the lower hand goes back to steadying.
            FVRViveHand carrier = m_supportCarry && m_hand != hand ? m_hand : null;
            if (m_supportCarry && m_hand != hand) EndSupportCarry();
            base.BeginInteraction(hand);
            if (carrier != null && Support != null)
            {
                carrier.ForceSetInteractable(Support);
                Support.BeginInteraction(carrier);
            }
        }

        public override void EndInteraction(FVRViveHand hand)
        {
            FVRViveHand handleHand = SupportHeld && !m_supportCarry ? Support.m_hand : null;
            base.EndInteraction(hand);
            if (Lever != null) Lever.OnTriggerHandReleased();
            if (m_supportCarry) EndSupportCarry();
            if (handleHand != null && QuickbeltSlot == null)
            {
                // Let go of the rings while still holding the lower half: the staff stays exactly
                // where it is in that hand instead of jumping to the rings pose.
                m_savedPosInterp = PositionInterpSpeed;
                m_savedRotInterp = RotationInterpSpeed;
                PositionInterpSpeed = 0f;
                RotationInterpSpeed = 0f;
                handleHand.ForceSetInteractable(this);
                BeginInteraction(handleHand);
                m_supportCarry = true;
            }
        }

        private void EndSupportCarry()
        {
            m_supportCarry = false;
            PositionInterpSpeed = m_savedPosInterp;
            RotationInterpSpeed = m_savedRotInterp;
        }

        /// <summary>
        /// H3VR's stab logic takes one stab direction; the upgraded tips have two to six blades.
        /// Point it at whichever blade made contact before H3VR handles the hit.
        /// </summary>
        public override void OnCollisionEnter(Collision col)
        {
            if (MP != null && MP.IsMeleeWeapon && KnifeColliders.Length > 0 && col.contacts.Length > 0)
            {
                int knife = System.Array.IndexOf(KnifeColliders, col.contacts[0].thisCollider);
                if (knife >= 0 && knife < KnifeStabDirections.Length) MP.StabDirection = KnifeStabDirections[knife];
            }
            base.OnCollisionEnter(col);
        }

        public override void EndInteractionIntoInventorySlot(FVRViveHand hand, FVRQuickBeltSlot slot)
        {
            if (SupportHeld)
            {
                FVRViveHand handleHand = Support.m_hand;
                handleHand.EndInteractionIfHeld(Support);
                Support.EndInteraction(handleHand);
            }
            base.EndInteractionIntoInventorySlot(hand, slot);
        }

        public void BuzzTriggerHand(bool strong)
        {
            if (m_hand == null) return;
            m_hand.Buzz(strong ? m_hand.Buzzer.Buzz_BeginInteraction : m_hand.Buzzer.Buzz_OnHoverInteractive);
        }

        public void PlayHandleSound(Vector3 pos)
        {
            PlaySoundAt(AudEvent_Handle, FVRPooledAudioType.GunHand, pos);
        }

        public void Recharge()
        {
            Debug.Log("[OriginsStaffs] " + name + " recharged: " + m_charges + " -> " + Capacity);
            m_charges = Capacity;
            m_flash = 1f;
            PlaySound(AudEvent_Recharged, FVRPooledAudioType.GunMech);
        }

        public override void FVRUpdate()
        {
            base.FVRUpdate();

            // No hand on the rings this frame (dropped or holstered): release the trigger.
            if (Time.frameCount - m_triggerFrame > 1) m_trigger = 0f;

            if (Lever != null)
            {
                bool pumping = IsHeld && m_hand != null && QuickbeltSlot == null && SupportHeld;
                float gap = pumping ? Vector3.Dot(m_hand.Input.Pos - Support.m_hand.Input.Pos, transform.forward) : 0f;
                Lever.Tick(pumping, gap, Time.deltaTime);
            }

            if (m_refire > 0f) m_refire -= Time.deltaTime;
            if (m_trigger <= StaffTriggerReset)
            {
                m_triggerWasReset = true;
                m_dryFired = false;
            }

            bool pulled = m_trigger >= StaffTriggerPull;
            bool mechanismClosed = Lever == null || Lever.IsClosed;
            if (ChargeShot)
            {
                UpdateCharge(pulled, mechanismClosed);
            }
            else if (pulled && m_refire <= 0f && (m_triggerWasReset || FullAuto))
            {
                if (m_charges > 0 && mechanismClosed)
                {
                    FireStaff();
                    m_refire = RefireDelay;
                    m_triggerWasReset = false;
                }
                else if (m_charges == 0 && !m_dryFired)
                {
                    // Empty: click, even with the lever already unlocked for the reload.
                    PlaySound(AudEvent_DryFire, FVRPooledAudioType.GunMech);
                    m_dryFired = true;
                    m_triggerWasReset = false;
                }
            }

            m_flash = Mathf.MoveTowards(m_flash, 0f, Time.deltaTime * 5f);
            UpdateGlow();
        }

        private void FireStaff()
        {
            m_charges--;
            Transform muzzle = GetMuzzle();
            if (ProjectilePrefab != null)
            {
                int iff = GM.CurrentPlayerBody.GetPlayerIFF();
                Vector2 spread = Random.insideUnitCircle * SpreadDegrees;
                LaunchProjectile(muzzle.position, muzzle.rotation * Quaternion.Euler(spread.x, spread.y, 0f) * Vector3.forward, iff);
                if (SideShots > 0) StartCoroutine(FireSideShots(iff));
            }
            if (ConeShotPrefab != null)
            {
                GameObject go = (GameObject)Instantiate(ConeShotPrefab.gameObject, muzzle.position, muzzle.rotation);
                go.GetComponent<StaffConeShot>().Fire(GM.CurrentPlayerBody.GetPlayerIFF());
            }
            ShotEffects();
        }

        private void LaunchProjectile(Vector3 from, Vector3 dir, int iff)
        {
            Quaternion aim = Quaternion.LookRotation(dir);
            GameObject go = (GameObject)Instantiate(ProjectilePrefab.gameObject, from, aim);
            go.GetComponent<StaffProjectile>().Launch(aim * Vector3.forward * ProjectileSpeed, iff, RootRigidbody);
        }

        /// <summary>
        /// BO2's fire_spread_shots, for the base staff and Kagutsuchi's Blood alike: 0.2 s after the
        /// shot, unless a wall is within 100 in along the aim, a fireball goes off to one side, and 0.2 s
        /// later another to the other side. Each starts 50 in ahead of the muzzle and heads for the
        /// point 100 in out at 15 degrees off the aim at that moment, which sends it about 29 degrees
        /// off the aim. Only while the staff is still held.
        /// </summary>
        private IEnumerator FireSideShots(int iff)
        {
            for (int i = 0; i < SideShots; i++)
            {
                yield return new WaitForSeconds(SideShotDelay);
                if (!IsHeld || QuickbeltSlot != null || ProjectilePrefab == null) yield break;
                Transform muzzle = GetMuzzle();
                Vector3 origin = muzzle.position;
                Vector3 forward = muzzle.forward;
                if (i == 0 && Physics.Raycast(origin, forward, SideShotReach, SideShotBlockers, QueryTriggerInteraction.Ignore)) yield break;
                // BO2 turns the yaw by -15 first, which in its axes is to the right, then +15.
                Vector3 side = Quaternion.AngleAxis(i % 2 == 0 ? SideShotYaw : -SideShotYaw, Vector3.up) * forward;
                Vector3 start = origin + forward * SideShotStart;
                LaunchProjectile(start, origin + side * SideShotReach - start, iff);
            }
        }

        /// <summary>
        /// BO2's charge shot. The pull starts a charge; letting go fires it. Held past
        /// <see cref="ChargeLevelTimes"/> it reaches level 2, then 3, each with BO2's charge sound and a
        /// harder rumble, as far as the charges left allow. A level-1 release is the normal shot.
        /// Dropping the staff, holstering it or opening the lever loses the charge.
        /// </summary>
        private void UpdateCharge(bool pulled, bool mechanismClosed)
        {
            float dt = Time.deltaTime;
            if (!m_charging)
            {
                m_chargeGlow = Mathf.MoveTowards(m_chargeGlow, 0f, dt * 4f);
                if (pulled && m_triggerWasReset && m_refire <= 0f)
                {
                    m_triggerWasReset = false;
                    if (m_charges > 0 && mechanismClosed)
                    {
                        m_charging = true;
                        m_chargeCued = false;
                        m_chargeTime = 0f;
                        m_chargeLevel = 1;
                    }
                    else if (m_charges == 0 && !m_dryFired)
                    {
                        PlaySound(AudEvent_DryFire, FVRPooledAudioType.GunMech);
                        m_dryFired = true;
                    }
                }
                return;
            }

            if (!IsHeld || m_hand == null || QuickbeltSlot != null || m_supportCarry || !mechanismClosed)
            {
                EndCharge();
                return;
            }

            m_chargeTime += dt;
            if (!m_chargeCued && m_chargeTime >= ChargeCueDelay)
            {
                m_chargeCued = true;
                PlaySound(AudEvent_Charge1, FVRPooledAudioType.GunMech);
                if (ChargeLoop != null) ChargeLoop.Play();
                if (ChargeParticles != null) ChargeParticles.Play();
            }
            int level = 1;
            for (int i = 0; i < ChargeLevelTimes.Length; i++)
            {
                if (m_chargeTime >= ChargeLevelTimes[i]) level = i + 2;
            }
            level = Mathf.Min(level, m_charges, 1 + ChargedShots.Length);
            if (level > m_chargeLevel)
            {
                m_chargeLevel = level;
                PlaySound(level == 2 ? AudEvent_Charge2 : AudEvent_Charge3, FVRPooledAudioType.GunMech);
                if (m_hand != null) m_hand.Buzz(m_hand.Buzzer.Buzz_GunShot);
            }
            if (m_chargeCued)
            {
                // BO2 rumbles every 0.1 s while charged, harder at each level.
                m_chargeBuzz -= dt;
                if (m_chargeBuzz <= 0f && m_hand != null)
                {
                    m_chargeBuzz = 0.15f;
                    m_hand.Buzz(m_chargeLevel >= 3 ? m_hand.Buzzer.Buzz_BeginInteraction : m_hand.Buzzer.Buzz_OnHoverInteractive);
                }
                if (ChargeParticles != null)
                {
                    var emission = ChargeParticles.emission;
                    emission.rateOverTime = ChargeParticleRate * m_chargeLevel;
                }
                m_chargeGlow = Mathf.MoveTowards(m_chargeGlow, ChargeGlowPerLevel * m_chargeLevel, dt * 3f);
            }

            if (m_trigger <= StaffTriggerReset)
            {
                int fired = m_chargeLevel;
                EndCharge();
                m_triggerWasReset = true;
                if (fired >= 2) FireCharged(fired);
                else FireStaff();
                m_refire = RefireDelay;
            }
        }

        private void EndCharge()
        {
            m_charging = false;
            m_chargeCued = false;
            m_chargeTime = 0f;
            m_chargeLevel = 0;
            if (ChargeLoop != null) ChargeLoop.Stop();
            if (ChargeParticles != null) ChargeParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        private void FireCharged(int level)
        {
            StaffChargedShot shot = level - 2 < ChargedShots.Length ? ChargedShots[level - 2] : null;
            if (shot == null)
            {
                FireStaff();
                return;
            }
            m_charges = Mathf.Max(0, m_charges - level);
            Transform muzzle = GetMuzzle();
            GameObject go = (GameObject)Instantiate(shot.gameObject, muzzle.position, muzzle.rotation);
            go.GetComponent<StaffChargedShot>().Fire(this);
            Debug.Log("[OriginsStaffs] " + name + ": charged shot, level " + level + " (" + m_charges + " charges left)");
            ShotEffects();
        }

        /// <summary>Muzzle flash, rumble and sound for the extra grenades of a charged fire shot.</summary>
        public void ExtraShotEffects()
        {
            ShotEffects();
        }

        private void ShotEffects()
        {
            Transform muzzle = GetMuzzle();
            if (MuzzleFlashPrefab != null)
            {
                Instantiate(MuzzleFlashPrefab, muzzle.position, muzzle.rotation);
            }

            Recoil(IsTwoHandStabilized(), IsForegripStabilized(), IsShoulderStabilized());
            if (m_hand != null) m_hand.Buzz(m_hand.Buzzer.Buzz_GunShot);
            if (SupportHeld) Support.m_hand.Buzz(Support.m_hand.Buzzer.Buzz_GunShot);

            GM.CurrentSceneSettings.OnShotFired(this);
            GM.CurrentPlayerBody.VisibleEvent(2f);
            PlaySound(AudEvent_Shot, FVRPooledAudioType.GunShot);
            PlaySound(AudEvent_ShotRear, FVRPooledAudioType.GunShot);
            PlaySound(AudEvent_Flux, FVRPooledAudioType.GunMech);
            m_flash = 1f;
        }

        private void UpdateGlow()
        {
            float fill = Capacity > 0 ? (float)m_charges / Capacity : 0f;
            float level = Mathf.Lerp(GlowEmpty, GlowFull, fill);
            if (m_charges == 0) level *= 0.5f + 0.5f * Mathf.PerlinNoise(Time.time * 6f, 0.5f);
            level += m_flash * ShotFlash;
            level += m_chargeGlow * (0.85f + 0.15f * Mathf.Sin(Time.time * 18f));
            if (Lever != null) level *= Lever.GlowMultiplier;

            SetEmission(GlowRenderers, level);
            SetEmission(HeadGlowRenderers, level * HeadGlowBoost);
            SetLight(CrystalLight, LightFull * level / GlowFull);
            SetLight(HeadLight, HeadLightFull * level / GlowFull);
        }

        private void SetEmission(Renderer[] renderers, float level)
        {
            if (renderers == null) return;
            Color emission = Color.white * level;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(m_block);
                m_block.SetColor("_EmissionColor", emission);
                r.SetPropertyBlock(m_block);
            }
        }

        private static void SetLight(Light light, float intensity)
        {
            if (light == null) return;
            light.intensity = intensity;
            light.enabled = intensity > 0.02f;
        }

        /// <summary>
        /// Plays through this gun's own audio pools. SM.PlayCoreSound ignores the gun categories
        /// (GunShot, GunMech, GunHand) and returns silently, since guns are meant to use these.
        /// </summary>
        public void PlaySound(AudioEvent ev, FVRPooledAudioType type)
        {
            PlaySoundAt(ev, type, GetMuzzle().position);
        }

        public void PlaySoundAt(AudioEvent ev, FVRPooledAudioType type, Vector3 pos)
        {
            if (ev == null || ev.Clips == null || ev.Clips.Count == 0) return;
            switch (type)
            {
                case FVRPooledAudioType.GunShot:
                    if (m_pool_shot != null) m_pool_shot.PlayClip(ev, pos);
                    break;
                case FVRPooledAudioType.GunMech:
                    if (m_pool_mechanics != null) m_pool_mechanics.PlayClip(ev, pos);
                    break;
                case FVRPooledAudioType.GunHand:
                    if (m_pool_handling != null) m_pool_handling.PlayClip(ev, pos);
                    break;
                default:
                    SM.PlayCoreSound(type, ev, pos);
                    break;
            }
        }
    }
}
