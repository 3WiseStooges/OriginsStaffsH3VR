using System.Collections;
using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Where a charged fire grenade goes off (BO2's fire_staff_area_of_effect): for 5 s, every 0.2 s,
    /// every zombie within 80 in (level 3: 100 in) is hit by flame_damage_fx. On the last step the
    /// radius doubles. With 11500 (level 3: 20000) damage per hit, BO2 kills almost anything in it:
    /// half the time the body bursts apart, otherwise it burns to death. Anything that survives is set
    /// alight and burns for 300 (450) a second for 8 s.
    /// </summary>
    public class StaffFirePool : MonoBehaviour
    {
        [Tooltip("Metres (BO2: 80 in, level 3: 100 in).")]
        public float Radius = 2.03f;
        public float Duration = 5f;
        public float Step = 0.2f;
        [Tooltip("BO2 get_impact_damage: 11500 (level 3: 20000).")]
        public float Bo2Damage = 11500f;
        [Tooltip("BO2 get_damage_per_second: 300 (level 3: 450), for 8 s.")]
        public float Bo2BurnPerSecond = 300f;
        public float BurnTime = 8f;
        public LayerMask BlockerMask;

        [Header("Effects")]
        public GameObject BodyFlamesPrefab;
        public GameObject FinalBurstPrefab;
        public Light PoolLight;
        public AudioSource Loop;
        public ParticleSystem[] StopOnEnd;

        private float m_lightBase;

        private IEnumerator Start()
        {
            int iff = GM.CurrentPlayerBody.GetPlayerIFF();
            if (PoolLight != null) m_lightBase = PoolLight.intensity;
            Vector3 centre = transform.position;
            float left = Duration;
            while (left > 0f)
            {
                float radius = left - Step <= 0f ? Radius * 2f : Radius;
                List<KeyValuePair<Sosig, SosigLink>> near = StaffDamage.SosigsNearSorted(centre, radius);
                yield return new WaitForSeconds(Step);
                left -= Step;
                foreach (KeyValuePair<Sosig, SosigLink> pair in near)
                {
                    Sosig s = pair.Key;
                    if (s == null || s.BodyState == Sosig.SosigBodyState.Dead) continue;
                    if (StaffDamage.Blocked(centre + Vector3.up * 0.3f, pair.Value.transform.position, BlockerMask)) continue;
                    Flame(s, centre, iff);
                }
            }

            if (FinalBurstPrefab != null) Instantiate(FinalBurstPrefab, centre, Quaternion.identity);
            if (Loop != null) Loop.Stop();
            if (StopOnEnd != null)
            {
                for (int i = 0; i < StopOnEnd.Length; i++)
                {
                    if (StopOnEnd[i] != null) StopOnEnd[i].Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            if (PoolLight != null) PoolLight.enabled = false;
            Destroy(gameObject, 2.5f);
        }

        private void Update()
        {
            if (PoolLight != null && PoolLight.enabled) PoolLight.intensity = m_lightBase * (0.75f + 0.5f * Mathf.PerlinNoise(Time.time * 7f, 0.3f));
        }

        /// <summary>BO2's flame_damage_fx on an upgraded staff's fire.</summary>
        private void Flame(Sosig s, Vector3 centre, int iff)
        {
            SosigLink torso = StaffDamage.Torso(s);
            if (torso == null) return;
            if (StaffDamage.WouldKill(s, Bo2Damage))
            {
                if (Random.value < 0.5f)
                {
                    StaffDamage.Gib(s, iff, Damage.DamageClass.Explosive);
                }
                else
                {
                    StaffDamage.SpawnOn(BodyFlamesPrefab, torso.transform, torso.transform.position);
                    StaffDamage.Kill(s, iff);
                }
                return;
            }
            StaffBurn.Apply(s, Bo2BurnPerSecond, BurnTime, iff, BodyFlamesPrefab);
            StaffDamage.Hurt(s, Bo2Damage, StaffDamage.Away(centre, torso.transform.position, Vector3.forward), iff, centre);
        }
    }
}
