using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// The visible part of the ice and wind shots: an ice shard or a ball of wind that flies
    /// straight until it hits the level or a sosig. BO2's own targeting (<see cref="StaffConeShot"/>)
    /// does most of the work; a sosig the bolt touches is reported back to the shot, so a direct hit
    /// always counts. On a wall or a sosig it bursts (BO2's projectile impact effect and sound); at
    /// the end of its life it fades. An aimed bolt stops where its target is.
    /// </summary>
    public class StaffBolt : MonoBehaviour
    {
        public float Lifetime = 0.3f;
        public float CastRadius = 0.03f;
        public LayerMask BlockerMask;
        [Tooltip("Spawned where the bolt hits the level or a sosig.")]
        public GameObject ImpactPrefab;
        [Tooltip("Spawned where the bolt runs out of life or reaches its target.")]
        public GameObject EndPrefab;
        [Tooltip("Particle systems that keep fading out after the bolt is gone (trails).")]
        public ParticleSystem[] DetachOnEnd;
        [Tooltip("Spins the bolt about its flight direction (degrees per second; an ice shard tumbling, the wind ball turning).")]
        public float Spin = 0f;

        [Tooltip("Living sosigs within this many metres of the bolt as it flies, with nothing solid in between, are reported to OnSosigNear (the wind ball's gust). 0: off.")]
        public float NearRadius = 0f;

        /// <summary>Called when the bolt runs into a living sosig (set by the shot that fired it).</summary>
        public System.Action<SosigLink> OnSosigHit;
        /// <summary>Called for each living sosig link within <see cref="NearRadius"/> of the bolt's path, every frame
        /// (set by the shot that fired it, which hits each sosig once).</summary>
        public System.Action<SosigLink> OnSosigNear;

        private Vector3 m_velocity;
        private float m_age;
        private float m_maxDistance = float.MaxValue;
        private float m_travelled;
        private bool m_done;

        public void Launch(Vector3 velocity)
        {
            m_velocity = velocity;
            transform.rotation = Quaternion.LookRotation(velocity);
        }

        /// <summary>Flies to <paramref name="target"/> and stops there.</summary>
        public void LaunchAt(Vector3 target, float speed)
        {
            Vector3 to = target - transform.position;
            m_maxDistance = to.magnitude;
            Launch(m_maxDistance > 0.001f ? to / m_maxDistance * speed : transform.forward * speed);
        }

        private void Update()
        {
            if (m_done) return;
            float dt = Time.deltaTime;
            m_age += dt;
            Vector3 step = m_velocity * dt;
            float dist = step.magnitude;
            if (m_age >= Lifetime || dist <= 0f)
            {
                End(EndPrefab, transform.position, Quaternion.LookRotation(-m_velocity));
                return;
            }
            if (m_travelled + dist >= m_maxDistance)
            {
                dist = m_maxDistance - m_travelled;
                step = step.normalized * dist;
            }

            if (NearRadius > 0f && OnSosigNear != null) Sweep(transform.position, transform.position + step);

            // The level, or a living sosig; anything else on the sosig layer (the player's own
            // hitboxes, dead sosigs) is flown through.
            int mask = BlockerMask | (OnSosigHit != null ? StaffDamage.AgentBodyMask : 0);
            RaycastHit[] hits = Physics.SphereCastAll(transform.position, CastRadius, step / dist, dist, mask, QueryTriggerInteraction.Ignore);
            RaycastHit best = new RaycastHit();
            SosigLink bestLink = null;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (found && hits[i].distance >= best.distance) continue;
                SosigLink link = null;
                if (((1 << hits[i].collider.gameObject.layer) & BlockerMask) == 0)
                {
                    link = StaffDamage.FindLink(hits[i].collider);
                    if (link == null || link.S == null || link.S.BodyState == Sosig.SosigBodyState.Dead) continue;
                }
                best = hits[i];
                bestLink = link;
                found = true;
            }
            if (found)
            {
                Vector3 point = best.point == Vector3.zero ? transform.position + step / dist * best.distance : best.point;
                if (bestLink != null) OnSosigHit(bestLink);
                End(ImpactPrefab, point + best.normal * 0.03f, Quaternion.LookRotation(best.normal == Vector3.zero ? -m_velocity : best.normal));
                return;
            }

            transform.position += step;
            m_travelled += dist;
            transform.rotation = Quaternion.LookRotation(m_velocity) * Quaternion.Euler(0f, 0f, Spin * m_age);
            if (m_travelled >= m_maxDistance - 0.001f) End(EndPrefab, transform.position, Quaternion.LookRotation(-m_velocity));
        }

        private void Sweep(Vector3 from, Vector3 to)
        {
            Collider[] cols = Physics.OverlapCapsule(from, to, NearRadius, StaffDamage.AgentBodyMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < cols.Length; i++)
            {
                SosigLink link = StaffDamage.FindLink(cols[i]);
                if (link == null || link.S == null || link.S.BodyState == Sosig.SosigBodyState.Dead) continue;
                if (StaffDamage.Blocked(from, link.transform.position, BlockerMask)) continue;
                OnSosigNear(link);
            }
        }

        private void End(GameObject prefab, Vector3 point, Quaternion rotation)
        {
            m_done = true;
            if (prefab != null) Instantiate(prefab, point, rotation);
            if (DetachOnEnd != null)
            {
                for (int i = 0; i < DetachOnEnd.Length; i++)
                {
                    ParticleSystem ps = DetachOnEnd[i];
                    if (ps == null) continue;
                    ps.transform.SetParent(null, true);
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    Destroy(ps.gameObject, 2f);
                }
            }
            Destroy(gameObject);
        }
    }
}
