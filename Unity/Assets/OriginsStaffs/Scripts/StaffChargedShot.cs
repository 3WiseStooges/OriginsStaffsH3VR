using System.Collections;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// An upgraded staff's charged shot (BO2's staff_&lt;element&gt;_upgraded2_zm / upgraded3_zm,
    /// charge levels 2 and 3). Spawned at the muzzle facing the aim; launches the element's projectile
    /// and removes itself.
    ///   Fire: lobbed fire grenades that stick where they land and burn (<see cref="StaffFireGrenade"/>);
    ///   level 2 throws one more 0.35 s later, level 3 two more (fire_additional_shots).
    ///   Ice: an ice bolt; where it ends, a blizzard (<see cref="StaffBlizzard"/>).
    ///   Lightning: a slow ball of lightning that drains everything near it (<see cref="StaffLightningOrb"/>).
    ///   Wind: a ball of wind; where it lands, a whirlwind (<see cref="StaffWhirlwind"/>).
    /// </summary>
    public class StaffChargedShot : MonoBehaviour
    {
        public StaffElement Element = StaffElement.Fire;
        [Tooltip("BO2 charge level: 2 or 3.")]
        public int Level = 2;
        public LayerMask BlockerMask;

        [Header("Ice and wind")]
        public StaffBolt BoltPrefab;
        [Tooltip("Metres per second (BO2 projectileSpeed: ice 2000 in/s, wind 1500 in/s).")]
        public float BoltSpeed = 38.1f;

        [Header("Fire")]
        public StaffFireGrenade GrenadePrefab;
        [Tooltip("BO2 projectileSpeed / projectileSpeedUp: 1300 in/s forward, 200 in/s up.")]
        public float GrenadeSpeed = 33.02f;
        public float GrenadeLift = 5.08f;
        [Tooltip("BO2 fire_additional_shots: 1 more at level 2, 2 more at level 3.")]
        public int ExtraGrenades = 1;
        public float ExtraDelay = 0.35f;

        [Header("Lightning")]
        public StaffLightningOrb OrbPrefab;
        [Tooltip("BO2 spawns the ball 100 in in front of the muzzle...")]
        public float OrbStartDistance = 2.54f;
        [Tooltip("...and sends it 800 in (level 3: 1200 in) along the aim, stopping at walls.")]
        public float OrbRange = 20.32f;
        [Tooltip("BO2: the full range takes 8 s.")]
        public float OrbTravelTime = 8f;

        public void Fire(OriginsStaff staff)
        {
            Transform muzzle = transform;
            switch (Element)
            {
                case StaffElement.Fire:
                    Lob(muzzle.position, muzzle.rotation);
                    if (ExtraGrenades > 0) StartCoroutine(ExtraShots(staff));
                    break;
                case StaffElement.Ice:
                case StaffElement.Wind:
                    if (BoltPrefab != null)
                    {
                        GameObject go = (GameObject)Instantiate(BoltPrefab.gameObject, muzzle.position, muzzle.rotation);
                        StaffBolt bolt = go.GetComponent<StaffBolt>();
                        bolt.OnSosigHit = link => { }; // BO2's projectile bursts on zombies too
                        bolt.Launch(muzzle.forward * BoltSpeed);
                    }
                    break;
                case StaffElement.Lightning:
                    if (OrbPrefab != null)
                    {
                        Vector3 start = muzzle.position + muzzle.forward * OrbStartDistance;
                        Vector3 end = muzzle.position + muzzle.forward * OrbRange;
                        RaycastHit hit;
                        if (Physics.Raycast(muzzle.position, muzzle.forward, out hit, OrbRange, BlockerMask, QueryTriggerInteraction.Ignore))
                        {
                            end = hit.point - muzzle.forward * 0.3f;
                        }
                        GameObject go = (GameObject)Instantiate(OrbPrefab.gameObject, start, muzzle.rotation);
                        go.GetComponent<StaffLightningOrb>().Launch(end, OrbRange / OrbTravelTime);
                    }
                    break;
            }
        }

        private void Lob(Vector3 from, Quaternion aim)
        {
            if (GrenadePrefab == null) return;
            GameObject go = (GameObject)Instantiate(GrenadePrefab.gameObject, from, aim);
            go.GetComponent<StaffFireGrenade>().Launch(aim * Vector3.forward * GrenadeSpeed + Vector3.up * GrenadeLift);
        }

        /// <summary>BO2's extra fire shots: each 0.35 s apart from wherever the staff now aims, 5 degrees
        /// lower per shot and up to 15 degrees to either side, while the staff is still held.</summary>
        private IEnumerator ExtraShots(OriginsStaff staff)
        {
            for (int i = 1; i <= ExtraGrenades; i++)
            {
                yield return new WaitForSeconds(ExtraDelay);
                if (staff == null || !staff.IsHeld) yield break;
                Transform muzzle = staff.GetMuzzle();
                Lob(muzzle.position, muzzle.rotation * Quaternion.Euler(5f * i, Random.Range(-15f, 15f), 0f));
                staff.ExtraShotEffects();
            }
        }
    }
}
