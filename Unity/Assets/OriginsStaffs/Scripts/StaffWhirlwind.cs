using System.Collections;
using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Boreas' Fury's charged shot (BO2's staff_air_find_source / staff_air_position_source).
    /// Where the ball of wind lands: if a zombie is within 100 in of the spot, the shot hit it. That
    /// zombie is launched, and every zombie within 250 in of it is flung away or torn apart. Either
    /// way a whirlwind touches down on the ground there for 3.5 s per charge level (7 s, level 3:
    /// 10.5 s). From half a second in, it grabs the zombies within 250 in, one every 0.5 s, and drags
    /// them in at 50 in/s (level 3: 62.5 in/s). Any that reach its centre (30 in) are torn apart.
    /// One whirlwind at a time.
    /// </summary>
    public class StaffWhirlwind : MonoBehaviour
    {
        [Tooltip("Seconds (BO2: 3.5 per charge level).")]
        public float Duration = 7f;
        [Tooltip("Metres (BO2 get_air_blast_range: 250 in).")]
        public float Range = 6.35f;
        [Tooltip("A zombie this close (2D) to where the shot landed was hit (BO2: 100 in).")]
        public float SourceRadius = 2.54f;
        [Tooltip("Metres per second (BO2: 50 in a second, level 3: 62.5).")]
        public float DragSpeed = 1.27f;
        [Tooltip("Metres (BO2: 30 in, 2D).")]
        public float KillDistance = 0.76f;
        public float GrabInterval = 0.5f;
        public float StartDelay = 0.5f;
        [Tooltip("BO2 launches at 300 in/s away from the player, lifted 5-35% of that.")]
        public float LaunchSpeed = 7.62f;
        public LayerMask BlockerMask;

        [Header("Effects")]
        public GameObject GustPrefab;
        public AudioSource Loop;
        public ParticleSystem[] StopOnEnd;

        private static StaffWhirlwind s_active;
        private readonly HashSet<Sosig> m_grabbed = new HashSet<Sosig>();
        private bool m_active;
        private bool m_ended;
        private int m_iff;
        private Vector3 m_player;

        private IEnumerator Start()
        {
            m_iff = GM.CurrentPlayerBody.GetPlayerIFF();
            m_player = GM.CurrentPlayerBody.transform.position;
            Vector3 at = transform.position;

            // staff_air_find_source: the closest living zombie decides whether the shot hit one.
            List<KeyValuePair<Sosig, SosigLink>> near = StaffDamage.SosigsNearSorted(at, Range * 2f);
            if (near.Count > 0)
            {
                Vector3 p = near[0].Value.transform.position;
                if (new Vector2(p.x - at.x, p.z - at.z).magnitude <= SourceRadius)
                {
                    Sosig source = near[0].Key;
                    SosigLink torso = StaffDamage.Torso(source) ?? near[0].Value;
                    at = torso.transform.position;
                    Fling(source, true);
                    StartCoroutine(ProximityKill(at));
                }
            }

            // Touch down: BO2 spawns 100 in above the spot and drops it to the ground.
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up * 2.54f, Vector3.down, out hit, 30f, BlockerMask, QueryTriggerInteraction.Ignore))
            {
                at = hit.point;
            }
            transform.position = at;
            transform.rotation = Quaternion.identity;

            if (s_active != null) s_active.End();
            s_active = this;
            m_active = true;
            StartCoroutine(Timeout());

            yield return new WaitForSeconds(StartDelay);
            while (m_active)
            {
                foreach (KeyValuePair<Sosig, SosigLink> pair in StaffDamage.SosigsNearSorted(transform.position, Range))
                {
                    if (!m_active) break;
                    Sosig s = pair.Key;
                    if (m_grabbed.Contains(s)) continue;
                    // BO2 traces twice from the whirlwind to the zombie, at chest and head height.
                    Vector3 from = transform.position, to = s.transform.position;
                    if (StaffDamage.Blocked(from + Vector3.up * 0.8f, to + Vector3.up * 0.8f, BlockerMask)) continue;
                    if (StaffDamage.Blocked(from + Vector3.up * 1.6f, to + Vector3.up * 1.6f, BlockerMask)) continue;
                    StartCoroutine(Drag(s));
                    yield return new WaitForSeconds(GrabInterval);
                }
                yield return null;
            }
        }

        private IEnumerator Timeout()
        {
            yield return new WaitForSeconds(Duration);
            End();
        }

        /// <summary>BO2's staff_air_proximity_kill around the zombie the shot hit.</summary>
        private IEnumerator ProximityKill(Vector3 centre)
        {
            foreach (KeyValuePair<Sosig, SosigLink> pair in StaffDamage.SosigsNearSorted(centre, Range))
            {
                if (pair.Key == null || pair.Key.BodyState == Sosig.SosigBodyState.Dead) continue;
                Fling(pair.Key, false);
                yield return new WaitForSeconds(0.05f);
            }
        }

        /// <summary>BO2's staff_air_fling_zombie: the source is always launched; the rest are launched or
        /// torn apart, a coin toss each.</summary>
        private void Fling(Sosig s, bool isSource)
        {
            SosigLink torso = StaffDamage.Torso(s);
            if (torso == null) return;
            StaffDamage.SpawnOn(GustPrefab, null, torso.transform.position);
            if (isSource || Random.value < 0.5f)
            {
                StaffDamage.Kill(s, m_iff);
                Vector3 away = StaffDamage.Away(m_player, torso.transform.position, Vector3.forward);
                StaffDamage.Launch(s, (away + Vector3.up * Random.Range(0.05f, 0.35f)) * LaunchSpeed);
            }
            else
            {
                StaffDamage.Gib(s, m_iff, Damage.DamageClass.Explosive);
            }
        }

        /// <summary>BO2's whirlwind_drag_zombie: pulled in along the ground until it reaches the centre.</summary>
        private IEnumerator Drag(Sosig s)
        {
            m_grabbed.Add(s);
            float shudder = 0f;
            while (m_active && s != null && s.BodyState != Sosig.SosigBodyState.Dead)
            {
                SosigLink torso = StaffDamage.Torso(s);
                if (torso == null) break;
                Vector3 centre = transform.position;
                Vector3 to = centre - torso.transform.position;
                to.y = 0f;
                if (to.magnitude <= KillDistance)
                {
                    StaffDamage.SpawnOn(GustPrefab, null, torso.transform.position);
                    StaffDamage.Gib(s, m_iff, Damage.DamageClass.Explosive); // whirlwind kills always gib (staff_air_gib)
                    break;
                }
                // Keep it off its feet while it is pulled in, spinning round as it goes.
                shudder -= Time.deltaTime;
                if (shudder <= 0f)
                {
                    s.Shudder(1f);
                    shudder = 0.5f;
                }
                Vector3 dir = to.normalized;
                Vector3 swirl = Vector3.Cross(Vector3.up, dir);
                float height = torso.transform.position.y - centre.y;
                Vector3 v = dir * DragSpeed + swirl * DragSpeed * 0.6f + Vector3.up * (height < 1.2f ? 1.2f : 0.2f);
                for (int i = 0; i < s.Links.Count; i++)
                {
                    SosigLink l = s.Links[i];
                    if (l != null && l.R != null) l.R.velocity = Vector3.Lerp(l.R.velocity, v, 0.25f);
                }
                yield return new WaitForFixedUpdate();
            }
            m_grabbed.Remove(s);
        }

        private void End()
        {
            if (m_ended) return;
            m_ended = true;
            m_active = false;
            if (s_active == this) s_active = null;
            StartCoroutine(FadeOut());
        }

        private IEnumerator FadeOut()
        {
            if (StopOnEnd != null)
            {
                for (int i = 0; i < StopOnEnd.Length; i++)
                {
                    if (StopOnEnd[i] != null) StopOnEnd[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            float volume = Loop != null ? Loop.volume : 0f;
            for (float t = 0f; t < 1.5f; t += Time.deltaTime)
            {
                if (Loop != null) Loop.volume = volume * (1f - t / 1.5f);
                yield return null;
            }
            Destroy(gameObject, 1f);
        }
    }
}
