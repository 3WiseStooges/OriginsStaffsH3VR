using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// The ice and wind staffs' shot, ported from BO2's scripts (_zm_weap_staff_water.gsc and
    /// _zm_weap_staff_air.gsc). In BO2 the projectile itself does no damage; on firing, the script
    /// picks targets in a cone in front of the player. Here the visible bolts carry that out: each
    /// target is hit as its bolt reaches it.
    ///
    ///   Ice (_icicle_locate_target, run three times): each search takes the closest sosig within
    ///   600 in and 25 degrees of the aim that is not already on ice and is in line of sight, and
    ///   freezes it (<see cref="StaffFreeze"/>). One icicle flies at each target; the rest of the
    ///   spray scatters.
    ///   Wind (wind_damage_cone): of the 12 sosigs closest within 400 in, every one within 45
    ///   degrees (upgraded 60) of the aim takes 2050 (upgraded 3300). The dead are flung away like
    ///   BO2's launched ragdolls; the rest are stunned. BO2 does no line-of-sight check here.
    ///
    /// Beyond BO2: a sosig a bolt actually touches is hit too, even outside the cone (in VR the bolt
    /// is plainly seen to hit it). Ice still freezes at most <see cref="MaxTargets"/> per shot, and no
    /// sosig is hit twice by one shot. Play-testing: only Boreas' Fury keeps the wind cone
    /// (<see cref="UsesCone"/>), and the ball of wind blows away every sosig within 2 m of its path
    /// (<see cref="StaffBolt.NearRadius"/>): the ones it kills are flung, the ones it does not are
    /// knocked off their feet and pushed away as well as stunned.
    /// Spawned at the muzzle facing the aim; removes itself once every bolt is done.
    /// </summary>
    public class StaffConeShot : MonoBehaviour
    {
        public StaffElement Element = StaffElement.Ice;

        [Header("BO2 targeting")]
        [Tooltip("Metres (ice 600 in, wind 400 in).")]
        public float Range = 15.24f;
        [Tooltip("Degrees either side of the aim (ice 25, wind 45, upgraded wind 60).")]
        public float HalfAngle = 25f;
        [Tooltip("Ice: sosigs frozen per shot (BO2: 3 searches). Wind: closest sosigs considered (BO2: 12).")]
        public int MaxTargets = 3;
        [Tooltip("BO2 damage per target (2050 base, 3300 upgraded).")]
        public float Bo2Damage = 2050f;
        public bool NeedsLineOfSight = true;
        public LayerMask BlockerMask;
        [Tooltip("Off: no target search, only what the bolts touch is hit.")]
        public bool UsesCone = true;

        [Header("Bolts")]
        public StaffBolt BoltPrefab;
        [Tooltip("Metres per second (BO2 projectileSpeed: ice 2000 in/s, wind 1500 in/s).")]
        public float BoltSpeed = 50.8f;
        [Tooltip("Ice: shards in the spray, aimed ones included.")]
        public int SprayCount = 1;
        public float SprayDegrees = 0f;

        [Header("Ice")]
        public GameObject IciclePrefab;
        public GameObject FrostPrefab;
        public GameObject ShatterPrefab;

        [Header("Wind")]
        [Tooltip("BO2 launches killed zombies at 300 in/s away from the player, lifted 5-35% of that.")]
        public float LaunchSpeed = 7.62f;
        public Vector2 LaunchLift = new Vector2(0.05f, 0.35f);
        [Tooltip("Survivors play BO2's electric stun (zm_electric_stun) for about this long.")]
        public float StunTime = 1.5f;
        [Tooltip("Sosigs the ball of wind blows on but does not kill are knocked down and pushed away this fast (not BO2).")]
        public float SurvivorPush = 4.5f;
        public GameObject GustPrefab;

        private readonly HashSet<Sosig> m_hit = new HashSet<Sosig>();
        // Captured at Fire: a bolt can outlive this object and still report a hit.
        private Vector3 m_origin;
        private Vector3 m_forward;
        private string m_name;
        private int m_iff;

        public void Fire(int iff)
        {
            m_origin = transform.position;
            m_forward = transform.forward;
            m_name = name;
            m_iff = iff;
            Vector3 forward = m_forward;
            List<KeyValuePair<Sosig, SosigLink>> near = UsesCone
                ? StaffDamage.SosigsNear(m_origin, Range).OrderBy(p => (p.Value.transform.position - m_origin).sqrMagnitude).ToList()
                : new List<KeyValuePair<Sosig, SosigLink>>();
            int targeted = Element == StaffElement.Ice ? FireIce(forward, near) : FireWind(forward, near);
            if (UsesCone) Debug.Log("[OriginsStaffs] " + m_name + ": " + near.Count + " sosig(s) within " + Range.ToString("0.0") + " m, " + targeted + " in the cone");
        }

        private bool InCone(Vector3 forward, Vector3 point)
        {
            Vector3 to = point - m_origin;
            return to.sqrMagnitude < 0.0001f || Vector3.Angle(forward, to) <= HalfAngle;
        }

        // ------------------------------------------------------------------ ice

        private int FireIce(Vector3 forward, List<KeyValuePair<Sosig, SosigLink>> near)
        {
            int picked = 0;
            for (int search = 0; search < MaxTargets; search++)
            {
                foreach (KeyValuePair<Sosig, SosigLink> pair in near)
                {
                    Sosig s = pair.Key;
                    if (m_hit.Contains(s) || StaffFreeze.IsOnIce(s)) continue;
                    SosigLink torso = StaffDamage.Torso(s) ?? pair.Value;
                    if (!InCone(forward, torso.transform.position)) continue;
                    // BO2 traces to a random body tag; a sosig half behind cover may be skipped.
                    SosigLink tag = s.Links[Random.Range(0, s.Links.Count)];
                    if (tag == null) tag = torso;
                    if (NeedsLineOfSight && StaffDamage.Blocked(m_origin, tag.transform.position, BlockerMask)) continue;
                    m_hit.Add(s);
                    picked++;
                    Vector3 aim = torso.transform.position;
                    SpawnBolt(aim - m_origin, aim);
                    StartCoroutine(FreezeOnArrival(s, Vector3.Distance(m_origin, aim) / BoltSpeed));
                    break;
                }
            }
            for (int i = picked; i < SprayCount; i++)
            {
                Vector2 spread = Random.insideUnitCircle * SprayDegrees;
                SpawnBolt(Quaternion.LookRotation(forward) * Quaternion.Euler(spread.x, spread.y, 0f) * Vector3.forward, null);
            }
            return picked;
        }

        private IEnumerator FreezeOnArrival(Sosig s, float delay)
        {
            yield return new WaitForSeconds(delay);
            Freeze(s); // already frozen if its icicle got there first
        }

        private void Freeze(Sosig s)
        {
            if (s != null) StaffFreeze.Apply(s, Bo2Damage, m_iff, m_origin, IciclePrefab, FrostPrefab, ShatterPrefab);
        }

        private void OnShardHit(SosigLink link)
        {
            Sosig s = link.S;
            if (m_hit.Contains(s))
            {
                Freeze(s); // an aimed icicle arriving
                return;
            }
            if (m_hit.Count >= MaxTargets || StaffFreeze.IsOnIce(s)) return;
            m_hit.Add(s);
            Freeze(s);
        }

        // ------------------------------------------------------------------ wind

        private int FireWind(Vector3 forward, List<KeyValuePair<Sosig, SosigLink>> near)
        {
            SpawnBolt(forward, null);
            int targeted = 0;
            foreach (KeyValuePair<Sosig, SosigLink> pair in near.Take(MaxTargets))
            {
                SosigLink torso = StaffDamage.Torso(pair.Key) ?? pair.Value;
                Vector3 at = torso.transform.position;
                if (!InCone(forward, at)) continue;
                if (NeedsLineOfSight && StaffDamage.Blocked(m_origin, at, BlockerMask)) continue;
                targeted++;
                StartCoroutine(GustOnArrival(pair.Key, Vector3.Distance(m_origin, at) / BoltSpeed));
            }
            return targeted;
        }

        private IEnumerator GustOnArrival(Sosig s, float delay)
        {
            yield return new WaitForSeconds(delay);
            Gust(s, false);
        }

        /// <summary>The ball of wind touching a sosig, or passing within 2 m of it.</summary>
        private void OnBall(SosigLink link)
        {
            Gust(link.S, true);
        }

        /// <summary>BO2's wind hit. <paramref name="fromBall"/>: blown on by the ball itself rather than
        /// struck by BO2's cone, so a survivor is blown over too instead of only stunned.</summary>
        private void Gust(Sosig s, bool fromBall)
        {
            if (s == null || s.BodyState == Sosig.SosigBodyState.Dead || !m_hit.Add(s)) return;
            SosigLink torso = StaffDamage.Torso(s);
            if (torso == null) return;
            Vector3 at = torso.transform.position;
            StaffDamage.SpawnOn(GustPrefab, null, at);

            Vector3 away = at - m_origin;
            away.y = 0f;
            away = away.sqrMagnitude > 0.0001f ? away.normalized : m_forward;
            bool kills = StaffDamage.WouldKill(s, Bo2Damage);
            if (kills)
            {
                StaffDamage.Kill(s, m_iff);
                StaffDamage.Launch(s, (away + Vector3.up * Random.Range(LaunchLift.x, LaunchLift.y)) * LaunchSpeed);
            }
            else
            {
                StaffDamage.Hurt(s, Bo2Damage, away, m_iff, m_origin);
                if (fromBall)
                {
                    s.Shudder(1f);
                    StaffDamage.Launch(s, (away + Vector3.up * 0.3f) * SurvivorPush);
                }
                s.Stun(StunTime);
            }
            Debug.Log("[OriginsStaffs] " + m_name + ": " + (kills ? "killed and flung " : "stunned ") + s.name + " at " + Vector3.Distance(m_origin, at).ToString("0.0") + " m");
        }

        // ------------------------------------------------------------------ bolts

        private void SpawnBolt(Vector3 dir, Vector3? target)
        {
            if (BoltPrefab == null || dir.sqrMagnitude < 0.0001f) return;
            GameObject go = (GameObject)Instantiate(BoltPrefab.gameObject, m_origin, Quaternion.LookRotation(dir));
            StaffBolt bolt = go.GetComponent<StaffBolt>();
            if (Element == StaffElement.Ice)
            {
                bolt.OnSosigHit = OnShardHit;
            }
            else
            {
                bolt.OnSosigHit = OnBall;
                bolt.OnSosigNear = OnBall;
            }
            if (target.HasValue) bolt.LaunchAt(target.Value, BoltSpeed);
            else bolt.Launch(dir.normalized * BoltSpeed);
        }
    }
}
