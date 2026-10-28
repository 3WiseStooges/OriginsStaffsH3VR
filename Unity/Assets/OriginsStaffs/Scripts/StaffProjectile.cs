using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// The fire and lightning staffs' projectile: flies straight (BO2 staff shots ignore gravity)
    /// and bursts on impact.
    ///   Fire: damages what it hits and everything within the blast radius, the way H3VR's own
    ///   Explosion does, and sets things alight.
    ///   Lightning: BO2's own rocket damage (staff_lightning_zm, no script involved). The sosig hit
    ///   takes the direct damage, and every sosig in the radius with a clear line to the burst takes
    ///   radius damage falling from the inner to the outer value at the edge. Those it kills drop
    ///   (BO2's tesla death); the rest are stunned (zm_electric_stun). Both get sparks and a zap.
    /// </summary>
    public class StaffProjectile : MonoBehaviour
    {
        public StaffElement Element = StaffElement.Fire;

        [Header("Flight")]
        public float Lifetime = 5f;
        public float Gravity = 0f;
        public float CastRadius = 0.05f;
        public LayerMask HitMask;
        public LayerMask BlockerMask;

        [Header("Damage")]
        [Tooltip("BO2 explosionRadius, in metres.")]
        public float SplashRadius = 2.54f;
        public float DirectBlunt = 1500f;
        public float DirectThermal = 4000f;
        public float DirectEMP = 0f;
        public float SplashBlunt = 350f;
        public float SplashThermal = 2500f;
        public float SplashEMP = 0f;
        [Tooltip("Seconds of stun on a direct hit (scaled down across the splash); above 1 the sosig also shudders.")]
        public float Stunning = 0f;
        [Range(0f, 1f)] public float IgniteChance = 0.85f;
        public float PushSpeed = 2.5f;

        [Header("Lightning (BO2 damage, see StaffDamage)")]
        [Tooltip("BO2 damage: to the sosig hit (2050, upgraded 3300).")]
        public float Bo2Direct = 2050f;
        [Tooltip("BO2 explosionInnerDamage / explosionOuterDamage (2050 / 1750, upgraded 3300 / 3000).")]
        public float Bo2Inner = 2050f;
        public float Bo2Outer = 1750f;
        public float StunTime = 1.5f;

        [Header("Effects")]
        public GameObject ImpactPrefab;
        [Tooltip("Spawned on each sosig caught by the blast and carried along with it (lightning: sparks and a zap).")]
        public GameObject TargetEffectPrefab;
        public AudioEvent AudEvent_Impact;
        [Tooltip("Particle systems that keep fading out after the ball is gone (trail, embers).")]
        public ParticleSystem[] DetachOnImpact;

        private Vector3 m_velocity;
        private float m_age;
        private int m_iff = -1;
        private Rigidbody m_ignore;
        private bool m_done;

        private static readonly Collider[] s_overlap = new Collider[64];

        public void Launch(Vector3 velocity, int iff, Rigidbody ignore)
        {
            m_velocity = velocity;
            m_iff = iff;
            m_ignore = ignore;
            transform.rotation = Quaternion.LookRotation(velocity);
        }

        private void Update()
        {
            if (m_done) return;
            float dt = Time.deltaTime;
            m_age += dt;
            if (m_age >= Lifetime)
            {
                Explode(transform.position, -m_velocity.normalized, null);
                return;
            }

            m_velocity += Vector3.down * Gravity * dt;
            Vector3 step = m_velocity * dt;
            float dist = step.magnitude;
            if (dist <= 0f) return;

            RaycastHit[] hits = Physics.SphereCastAll(transform.position, CastRadius, step / dist, dist, HitMask, QueryTriggerInteraction.Ignore);
            RaycastHit best = new RaycastHit();
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (m_ignore != null && hits[i].collider.attachedRigidbody == m_ignore) continue;
                if (!found || hits[i].distance < best.distance)
                {
                    best = hits[i];
                    found = true;
                }
            }
            if (found)
            {
                Vector3 point = best.point == Vector3.zero ? transform.position + step.normalized * best.distance : best.point;
                Explode(point, best.normal, best.collider);
                return;
            }

            transform.position += step;
            transform.rotation = Quaternion.LookRotation(m_velocity);
        }

        private void Explode(Vector3 point, Vector3 normal, Collider direct)
        {
            m_done = true;
            Vector3 centre = point + normal * 0.05f;
            if (Element == StaffElement.Lightning) ShockSosigs(centre, direct);
            HashSet<IFVRDamageable> done = new HashSet<IFVRDamageable>();
            HashSet<Sosig> marked = new HashSet<Sosig>();

            if (direct != null && !(Element == StaffElement.Lightning && StaffDamage.FindLink(direct) != null))
            {
                IFVRDamageable target = StaffDamage.FindDamageable(direct);
                if (target != null)
                {
                    Damage d = MakeDamage(point, -normal, 1f, DirectBlunt, DirectThermal, DirectEMP, Damage.DamageClass.Projectile);
                    d.hitNormal = normal;
                    target.Damage(d);
                    done.Add(target);
                }
                OnCaught(direct, 1f, point, marked);
            }

            int count = Physics.OverlapSphereNonAlloc(centre, SplashRadius, s_overlap, HitMask, QueryTriggerInteraction.Ignore);
            HashSet<Rigidbody> pushed = new HashSet<Rigidbody>();
            for (int i = 0; i < count; i++)
            {
                Collider col = s_overlap[i];
                if (col == null) continue;
                if (m_ignore != null && col.attachedRigidbody == m_ignore) continue;
                if (Element == StaffElement.Lightning && StaffDamage.FindLink(col) != null) continue; // handled by ShockSosigs

                Vector3 toTarget = col.transform.position - centre;
                float distance = toTarget.magnitude;
                float falloff = Mathf.Clamp01(1f - distance / SplashRadius);
                if (falloff <= 0f) continue;
                if (distance > 0.01f && Physics.Raycast(centre, toTarget / distance, distance, BlockerMask, QueryTriggerInteraction.Ignore))
                {
                    falloff *= 0.05f; // behind cover
                }

                IFVRDamageable target = StaffDamage.FindDamageable(col);
                if (target != null && done.Add(target))
                {
                    Damage d = MakeDamage(col.transform.position, toTarget.normalized, falloff, SplashBlunt, SplashThermal, SplashEMP, Damage.DamageClass.Explosive);
                    target.Damage(d);
                }
                OnCaught(col, falloff, col.transform.position, marked);

                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && !rb.isKinematic && pushed.Add(rb))
                {
                    rb.AddForce(toTarget.normalized * PushSpeed * falloff + Vector3.up * PushSpeed * 0.3f * falloff, ForceMode.VelocityChange);
                }
            }

            GM.CurrentSceneSettings.PingReceivers(centre);
            if (AudEvent_Impact != null && AudEvent_Impact.Clips != null && AudEvent_Impact.Clips.Count > 0)
            {
                SM.PlayCoreSound(FVRPooledAudioType.Explosion, AudEvent_Impact, centre);
            }
            if (ImpactPrefab != null)
            {
                Instantiate(ImpactPrefab, centre, Quaternion.LookRotation(normal));
            }

            if (DetachOnImpact != null)
            {
                for (int i = 0; i < DetachOnImpact.Length; i++)
                {
                    ParticleSystem ps = DetachOnImpact[i];
                    if (ps == null) continue;
                    ps.transform.SetParent(null, true);
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    Destroy(ps.gameObject, 2f);
                }
            }
            Destroy(gameObject);
        }

        private Damage MakeDamage(Vector3 point, Vector3 dir, float scale, float blunt, float thermal, float emp, Damage.DamageClass cls)
        {
            Damage d = StaffDamage.Make(cls, point, dir, m_iff, transform.position);
            d.Dam_Blunt = blunt * scale;
            d.Dam_Thermal = thermal * scale;
            d.Dam_EMP = emp * scale;
            d.Dam_Stunning = Stunning * scale;
            StaffDamage.Totals(d);
            return d;
        }

        private void ShockSosigs(Vector3 centre, Collider direct)
        {
            SosigLink directLink = direct != null ? StaffDamage.FindLink(direct) : null;
            Sosig directSosig = directLink != null ? directLink.S : null;
            Dictionary<Sosig, SosigLink> near = StaffDamage.SosigsNear(centre, SplashRadius);
            if (directSosig != null && directSosig.BodyState != Sosig.SosigBodyState.Dead) near[directSosig] = directLink;

            foreach (KeyValuePair<Sosig, SosigLink> pair in near)
            {
                Sosig s = pair.Key;
                bool isDirect = s == directSosig;
                Vector3 at = pair.Value.transform.position;
                if (!isDirect && StaffDamage.Blocked(centre, at, BlockerMask)) continue;
                float bo2 = Mathf.Lerp(Bo2Inner, Bo2Outer, Mathf.Clamp01(Vector3.Distance(centre, at) / SplashRadius));
                if (isDirect) bo2 += Bo2Direct;

                SosigLink torso = StaffDamage.Torso(s) ?? pair.Value;
                StaffDamage.SpawnOn(TargetEffectPrefab, torso.transform, torso.transform.position);
                if (StaffDamage.WouldKill(s, bo2))
                {
                    StaffDamage.Kill(s, m_iff);
                }
                else
                {
                    StaffDamage.Hurt(s, bo2, (at - centre).normalized, m_iff, centre);
                    s.Stun(StunTime);
                }
            }
        }

        /// <summary>Element side effects on anything the burst reached: fire ignites.</summary>
        private void OnCaught(Collider col, float strength, Vector3 point, HashSet<Sosig> marked)
        {
            if (Element == StaffElement.Fire)
            {
                FVRIgnitable ignitable = col.gameObject.GetComponent<FVRIgnitable>();
                if (ignitable == null && col.attachedRigidbody != null)
                {
                    ignitable = col.attachedRigidbody.gameObject.GetComponent<FVRIgnitable>();
                }
                if (ignitable != null && Random.value < IgniteChance * strength)
                {
                    FXM.Ignite(ignitable, 1f);
                }
            }
        }
    }
}
