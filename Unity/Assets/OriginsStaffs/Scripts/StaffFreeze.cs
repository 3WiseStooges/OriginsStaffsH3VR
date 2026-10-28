using System.Collections;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// BO2's ice_affect_zombie on a sosig: an icicle lodges in its chest, it freezes and slows
    /// (H3VR's own freeze), and after 1.8-2.3 s it either shatters (if the staff's damage would
    /// kill it) or takes the damage and thaws half a second later. The charged shot's blizzard
    /// calls it with always_kill: 0.5-0.7 s, then it always shatters. While this is on a sosig it
    /// is "on ice", and further ice shots pick other targets, as in BO2.
    /// </summary>
    public class StaffFreeze : MonoBehaviour
    {
        public static bool IsOnIce(Sosig s)
        {
            return s.GetComponent<StaffFreeze>() != null;
        }

        /// <summary>Starts the freeze; does nothing if the sosig is already on ice.</summary>
        public static void Apply(Sosig s, float bo2Damage, int iff, Vector3 source, GameObject icicle, GameObject frost, GameObject shatter)
        {
            Apply(s, bo2Damage, iff, source, icicle, frost, shatter, new Vector2(1.8f, 2.3f), false);
        }

        public static void Apply(Sosig s, float bo2Damage, int iff, Vector3 source, GameObject icicle, GameObject frost, GameObject shatter,
                                 Vector2 delay, bool alwaysKill)
        {
            if (s == null || IsOnIce(s) || s.BodyState == Sosig.SosigBodyState.Dead) return;
            StaffFreeze f = s.gameObject.AddComponent<StaffFreeze>();
            f.StartCoroutine(f.Run(s, bo2Damage, iff, source, icicle, frost, shatter, delay, alwaysKill));
        }

        private IEnumerator Run(Sosig s, float bo2Damage, int iff, Vector3 source, GameObject icicle, GameObject frost, GameObject shatter,
                                Vector2 delay, bool alwaysKill)
        {
            SosigLink torso = StaffDamage.Torso(s);
            Vector3 dir = torso != null ? (torso.transform.position - source).normalized : Vector3.forward;
            GameObject stuck = null;
            if (torso != null)
            {
                // The icicle sits half buried in the chest, pointing back along the shot.
                stuck = StaffDamage.SpawnOn(icicle, torso.transform, torso.transform.position - dir * 0.08f);
                if (stuck != null) stuck.transform.rotation = Quaternion.LookRotation(dir);
                StaffDamage.SpawnOn(frost, torso.transform, torso.transform.position);
            }
            Debug.Log("[OriginsStaffs] Ice: froze " + s.name);
            // H3VR's freeze (frozen material, crawling, slow turns); thawed by hand below.
            s.ActivatePower(PowerupType.Unfreeze, PowerUpIntensity.High, PowerUpDuration.VeryShort, false, true);

            yield return new WaitForSeconds(Random.Range(delay.x, delay.y));
            if (s == null || s.BodyState == Sosig.SosigBodyState.Dead)
            {
                Finish(stuck);
                yield break;
            }

            if (alwaysKill || StaffDamage.WouldKill(s, bo2Damage))
            {
                // frozen_zombie_shatter: the body bursts into ice and gibs.
                Vector3 at = torso != null ? torso.transform.position : s.transform.position;
                StaffDamage.SpawnOn(shatter, null, at);
                Debug.Log("[OriginsStaffs] Ice: shattered " + s.name);
                StaffDamage.Gib(s, iff, Damage.DamageClass.Projectile);
                Finish(stuck);
                yield break;
            }

            // Too tough to shatter: thaw first (H3VR would triple the blow on ice), then hit.
            s.ActivatePower(PowerupType.Unfreeze, PowerUpIntensity.High, PowerUpDuration.VeryShort, false, false);
            StaffDamage.Hurt(s, bo2Damage, dir, iff, source);
            Debug.Log("[OriginsStaffs] Ice: " + s.name + " too tough to shatter, took the hit and thawed");
            if (stuck != null) Destroy(stuck);
            yield return new WaitForSeconds(0.5f);
            Finish(null);
        }

        private void Finish(GameObject stuck)
        {
            if (stuck != null) Destroy(stuck, 1f);
            Destroy(this);
        }
    }
}
