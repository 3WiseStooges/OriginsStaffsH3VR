using System.Collections;
using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Kimat's Bite's charged shot (BO2's staff_lightning_position_source): a ball of lightning drifting
    /// along the aim for 8 s, then lingering 3 s before it bursts. Every 0.5 s it reaches for each
    /// zombie within 150 in (level 3: 250 in) in line of sight, 0.2 s apart. A zombie it holds is
    /// stunned, and every second after the first half second takes 2500 (level 3: 3500), or dies if
    /// that would kill it; it is let go when the ball moves out of range. One ball at a time.
    /// </summary>
    public class StaffLightningOrb : MonoBehaviour
    {
        [Tooltip("Metres (BO2 get_lightning_blast_range: 150 in, level 3: 250 in).")]
        public float Range = 3.81f;
        [Tooltip("BO2 get_lightning_ball_damage_per_sec: 2500 (level 3: 3500), once a second.")]
        public float Bo2DamagePerPulse = 2500f;
        public float ActiveTime = 8f;
        public float LingerTime = 3f;
        public float StunTime = 1.1f;
        public LayerMask BlockerMask;

        [Header("Effects")]
        [Tooltip("On each zombie the ball grabs (BO2 lightning_impact_fx and wpn_imp_lightningstaff).")]
        public GameObject ZapPrefab;
        public GameObject EndPrefab;
        public Material ArcMaterial;
        public float ArcWidth = 0.08f;
        public AudioSource Loop;
        public ParticleSystem[] StopOnEnd;

        private static StaffLightningOrb s_active;
        private Vector3 m_target;
        private float m_speed;
        private bool m_killing = true;
        private bool m_ended;
        private int m_iff;
        private readonly HashSet<Sosig> m_zapped = new HashSet<Sosig>();
        private readonly Dictionary<Sosig, LineRenderer> m_arcs = new Dictionary<Sosig, LineRenderer>();

        public void Launch(Vector3 target, float speed)
        {
            m_target = target;
            m_speed = speed;
        }

        private IEnumerator Start()
        {
            if (s_active != null) s_active.Burst(); // BO2: a new ball ends the old one at once
            s_active = this;
            m_iff = GM.CurrentPlayerBody.GetPlayerIFF();
            StartCoroutine(KillZombies());
            yield return new WaitForSeconds(ActiveTime);
            m_killing = false;
            yield return new WaitForSeconds(LingerTime);
            Burst();
        }

        private void Update()
        {
            if (m_ended) return;
            transform.position = Vector3.MoveTowards(transform.position, m_target, m_speed * Time.deltaTime);
            foreach (KeyValuePair<Sosig, LineRenderer> arc in m_arcs) UpdateArc(arc.Key, arc.Value);
        }

        private IEnumerator KillZombies()
        {
            while (m_killing)
            {
                foreach (KeyValuePair<Sosig, SosigLink> pair in StaffDamage.SosigsNearSorted(transform.position, Range))
                {
                    if (!m_killing) yield break;
                    Sosig s = pair.Key;
                    if (s == null || m_zapped.Contains(s) || s.BodyState == Sosig.SosigBodyState.Dead) continue;
                    SosigLink torso = StaffDamage.Torso(s) ?? pair.Value;
                    if (StaffDamage.Blocked(transform.position, torso.transform.position, BlockerMask)) continue;
                    StartCoroutine(Zap(s));
                    yield return new WaitForSeconds(0.2f);
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        /// <summary>BO2's staff_lightning_ball_damage_over_time.</summary>
        private IEnumerator Zap(Sosig s)
        {
            m_zapped.Add(s);
            SosigLink torso = StaffDamage.Torso(s);
            if (torso != null) StaffDamage.SpawnOn(ZapPrefab, torso.transform, torso.transform.position);
            m_arcs[s] = NewArc();
            yield return new WaitForSeconds(0.5f);
            while (m_killing && s != null && s.BodyState != Sosig.SosigBodyState.Dead)
            {
                s.Stun(StunTime);
                yield return new WaitForSeconds(1f);
                if (!m_killing || s == null || s.BodyState == Sosig.SosigBodyState.Dead) break;
                torso = StaffDamage.Torso(s);
                if (torso == null || Vector3.Distance(transform.position, torso.transform.position) > Range) break;
                if (StaffDamage.WouldKill(s, Bo2DamagePerPulse))
                {
                    StaffDamage.SpawnOn(ZapPrefab, torso.transform, torso.transform.position);
                    StaffDamage.Kill(s, m_iff);
                    break;
                }
                StaffDamage.Hurt(s, Bo2DamagePerPulse, (torso.transform.position - transform.position).normalized, m_iff, transform.position);
            }
            LineRenderer line;
            if (m_arcs.TryGetValue(s, out line))
            {
                if (line != null) Destroy(line.gameObject);
                m_arcs.Remove(s);
            }
            m_zapped.Remove(s);
        }

        private LineRenderer NewArc()
        {
            var go = new GameObject("Arc");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = ArcMaterial;
            line.useWorldSpace = true;
            line.positionCount = 8;
            line.startWidth = ArcWidth;
            line.endWidth = ArcWidth * 0.6f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        /// <summary>A jagged bolt from the ball to the zombie, re-drawn every frame; like BO2's arc it
        /// flashes across twice a second.</summary>
        private void UpdateArc(Sosig s, LineRenderer line)
        {
            if (line == null) return;
            SosigLink torso = s != null ? StaffDamage.Torso(s) : null;
            if (torso == null)
            {
                line.enabled = false;
                return;
            }
            line.enabled = Mathf.Repeat(Time.time * 2f + line.GetInstanceID() * 0.37f, 1f) < 0.6f;
            Vector3 a = transform.position, b = torso.transform.position;
            Vector3 dir = b - a;
            float length = dir.magnitude;
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(side, dir).normalized;
            int n = line.positionCount;
            for (int i = 0; i < n; i++)
            {
                float k = i / (float)(n - 1);
                float jitter = (i == 0 || i == n - 1) ? 0f : length * 0.06f;
                line.SetPosition(i, a + dir * k + side * Random.Range(-jitter, jitter) + up * Random.Range(-jitter, jitter));
            }
        }

        private void Burst()
        {
            if (m_ended) return;
            m_ended = true;
            m_killing = false;
            if (s_active == this) s_active = null;
            StopAllCoroutines();
            foreach (KeyValuePair<Sosig, LineRenderer> arc in m_arcs)
            {
                if (arc.Value != null) Destroy(arc.Value.gameObject);
            }
            m_arcs.Clear();
            m_zapped.Clear();
            if (EndPrefab != null) Instantiate(EndPrefab, transform.position, Quaternion.identity);
            if (Loop != null) Loop.Stop();
            if (StopOnEnd != null)
            {
                for (int i = 0; i < StopOnEnd.Length; i++)
                {
                    if (StopOnEnd[i] != null) StopOnEnd[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            Light light = GetComponent<Light>();
            if (light != null) light.enabled = false;
            Destroy(gameObject, 1.5f);
        }
    }
}
