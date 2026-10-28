using System.Collections;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// A sosig set alight by Kagutsuchi's Blood that survived the flames (BO2's
    /// flame_damage_over_time): it burns for a while, taking BO2 damage every second, and dies when a
    /// tick would kill it. One per sosig; being set alight again while burning changes nothing, as in BO2.
    /// </summary>
    public class StaffBurn : MonoBehaviour
    {
        public static void Apply(Sosig s, float bo2PerSecond, float duration, int iff, GameObject flames)
        {
            if (s == null || s.BodyState == Sosig.SosigBodyState.Dead || s.GetComponent<StaffBurn>() != null) return;
            StaffBurn b = s.gameObject.AddComponent<StaffBurn>();
            b.StartCoroutine(b.Run(s, bo2PerSecond, duration, iff, flames));
        }

        private IEnumerator Run(Sosig s, float bo2PerSecond, float duration, int iff, GameObject flames)
        {
            SosigLink torso = StaffDamage.Torso(s);
            GameObject fx = torso != null ? StaffDamage.SpawnOn(flames, torso.transform, torso.transform.position) : null;
            yield return new WaitForSeconds(0.5f);
            for (float t = 0f; t < duration; t += 1f)
            {
                if (s == null || s.BodyState == Sosig.SosigBodyState.Dead) break;
                if (StaffDamage.WouldKill(s, bo2PerSecond))
                {
                    StaffDamage.Kill(s, iff);
                    break;
                }
                StaffDamage.Hurt(s, bo2PerSecond, Vector3.down, iff, s.transform.position);
                yield return new WaitForSeconds(1f);
            }
            if (fx != null) Destroy(fx, 1f);
            Destroy(this);
        }
    }
}
