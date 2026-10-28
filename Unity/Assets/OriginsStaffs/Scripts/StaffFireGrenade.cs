using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Kagutsuchi's Blood's charged shot: BO2's staff_fire_upgraded2/3_zm is a grenade, lobbed at
    /// 1300 in/s forward and 200 in/s up under 800 in/s^2 gravity. It sticks to whatever it hits except
    /// zombies (it glances off them), and goes off once it has been in flight half a second and come to
    /// rest (fire_staff_update_grenade_fuse), or after its 10 s fuse. Where it goes off, a fire pool
    /// burns (<see cref="StaffFirePool"/>).
    /// </summary>
    public class StaffFireGrenade : MonoBehaviour
    {
        [Tooltip("BO2 g_gravity: 800 in/s^2.")]
        public float Gravity = 20.32f;
        public float ArmTime = 0.5f;
        public float Fuse = 10f;
        public float CastRadius = 0.08f;
        public LayerMask BlockerMask;
        public GameObject ExplosionPrefab;
        public StaffFirePool PoolPrefab;
        public ParticleSystem[] DetachOnEnd;

        private Vector3 m_velocity;
        private float m_age;
        private bool m_stuck;
        private Vector3 m_normal = Vector3.up;
        private bool m_done;

        public void Launch(Vector3 velocity)
        {
            m_velocity = velocity;
        }

        private void Update()
        {
            if (m_done) return;
            float dt = Time.deltaTime;
            m_age += dt;
            if ((m_stuck && m_age >= ArmTime) || m_age >= Fuse)
            {
                Explode();
                return;
            }
            if (m_stuck) return;

            m_velocity += Vector3.down * Gravity * dt;
            Vector3 step = m_velocity * dt;
            float dist = step.magnitude;
            if (dist <= 0f) return;

            int mask = BlockerMask | StaffDamage.AgentBodyMask;
            RaycastHit[] hits = Physics.SphereCastAll(transform.position, CastRadius, step / dist, dist, mask, QueryTriggerInteraction.Ignore);
            RaycastHit best = new RaycastHit();
            bool found = false, onSosig = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (found && hits[i].distance >= best.distance) continue;
                bool level = ((1 << hits[i].collider.gameObject.layer) & BlockerMask) != 0;
                if (!level)
                {
                    SosigLink link = StaffDamage.FindLink(hits[i].collider);
                    if (link == null || link.S == null || link.S.BodyState == Sosig.SosigBodyState.Dead) continue;
                }
                best = hits[i];
                found = true;
                onSosig = !level;
            }
            if (!found)
            {
                transform.position += step;
                if (m_velocity.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(m_velocity);
                return;
            }

            Vector3 point = best.point == Vector3.zero ? transform.position + step / dist * best.distance : best.point;
            Vector3 normal = best.normal == Vector3.zero ? -step / dist : best.normal;
            if (onSosig)
            {
                // "Stick to all, except ai": it glances off and keeps falling.
                transform.position = point + normal * (CastRadius + 0.02f);
                m_velocity = Vector3.Reflect(m_velocity, normal) * 0.3f;
                return;
            }
            transform.position = point + normal * 0.05f;
            m_normal = normal;
            m_stuck = true;
            m_velocity = Vector3.zero;
        }

        private void Explode()
        {
            m_done = true;
            Vector3 at = transform.position;
            Quaternion up = Quaternion.LookRotation(m_normal);
            if (ExplosionPrefab != null) Instantiate(ExplosionPrefab, at, up);
            if (PoolPrefab != null) Instantiate(PoolPrefab.gameObject, at, Quaternion.identity);
            GM.CurrentSceneSettings.PingReceivers(at);
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
