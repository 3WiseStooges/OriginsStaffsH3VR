using System.Collections;
using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Ullr's Arrow's charged shot (BO2's staff_water_position_source): a blizzard 33 in above where
    /// the ice bolt ended. After 1 s it rages for 6 s (level 3: 9 s); every 0.1 s, every zombie within
    /// 175 in that is not already on ice is frozen and, 0.5-0.7 s later, shattered
    /// (ice_affect_zombie with always_kill). Only one blizzard at a time: a new one ends the old.
    /// </summary>
    public class StaffBlizzard : MonoBehaviour
    {
        [Tooltip("Metres (BO2: 175 in).")]
        public float Radius = 4.445f;
        [Tooltip("Seconds of storm (BO2: 6, level 3: 9).")]
        public float Duration = 6f;
        public float StartDelay = 1f;
        public float Tick = 0.1f;
        [Tooltip("BO2 raises the storm 33 in above the impact.")]
        public float Rise = 0.84f;
        public float Bo2Damage = 3300f;

        [Header("Effects")]
        public GameObject IciclePrefab;
        public GameObject FrostPrefab;
        public GameObject ShatterPrefab;
        public AudioSource Loop;
        public Light StormLight;
        public ParticleSystem[] StopOnEnd;

        private static StaffBlizzard s_active;
        private bool m_ended;

        private IEnumerator Start()
        {
            if (s_active != null) s_active.End();
            s_active = this;
            transform.position += Vector3.up * Rise;
            transform.rotation = Quaternion.identity;
            int iff = GM.CurrentPlayerBody.GetPlayerIFF();

            yield return new WaitForSeconds(StartDelay);
            for (float t = 0f; t < Duration && !m_ended; t += Tick)
            {
                foreach (KeyValuePair<Sosig, SosigLink> pair in StaffDamage.SosigsNear(transform.position, Radius))
                {
                    if (StaffFreeze.IsOnIce(pair.Key)) continue;
                    StaffFreeze.Apply(pair.Key, Bo2Damage, iff, transform.position, IciclePrefab, FrostPrefab, ShatterPrefab,
                                      new Vector2(0.5f, 0.7f), true);
                }
                yield return new WaitForSeconds(Tick);
            }
            End();
        }

        private void End()
        {
            if (m_ended) return;
            m_ended = true;
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
            float light = StormLight != null ? StormLight.intensity : 0f;
            for (float t = 0f; t < 1.5f; t += Time.deltaTime)
            {
                float k = 1f - t / 1.5f;
                if (Loop != null) Loop.volume = volume * k;
                if (StormLight != null) StormLight.intensity = light * k;
                yield return null;
            }
            Destroy(gameObject, 1f);
        }
    }
}
