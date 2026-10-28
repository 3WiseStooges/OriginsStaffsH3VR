using System.Collections.Generic;
using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    public enum StaffElement
    {
        Fire,
        Ice,
        Lightning,
        Wind,
    }

    /// <summary>
    /// Shared lookups and damage for the staffs' shots, including BO2's damage rules in H3VR terms.
    ///
    /// BO2's staff scripts work in zombie health: a hit either kills (health below the damage) or
    /// takes that much off. H3VR sosigs have no health pool; a sosig dies when its torso link runs
    /// out of integrity (100, minus 0.032 per point of blunt damage). One BO2 point counts as
    /// <see cref="Bo2ToBlunt"/> blunt, so BO2's 2050 just destroys a fresh, standard torso:
    /// the staffs kill ordinary sosigs outright, as they kill early-round zombies, and tougher
    /// sosigs survive like late-round ones.
    /// </summary>
    public static class StaffDamage
    {
        public const float Bo2ToBlunt = 1.6f;

        private static int s_agentMask;

        /// <summary>
        /// Layers sosig bodies can be on: AgentBody, plus Default in case a sosig's links sit there.
        /// Searches use an unbounded OverlapSphere: a fixed-size buffer over a 15 m sphere fills up with
        /// level colliders first (why the first ice and wind blasts never hit anything).
        /// </summary>
        public static int AgentMask
        {
            get
            {
                if (s_agentMask == 0)
                {
                    int layer = LayerMask.NameToLayer("AgentBody");
                    s_agentMask = (layer >= 0 ? 1 << layer : 1 << 20) | 1;
                }
                return s_agentMask;
            }
        }

        /// <summary>The AgentBody layer alone: what a flying bolt may run into besides the level (never the staff itself).</summary>
        public static int AgentBodyMask
        {
            get { return AgentMask & ~1; }
        }

        public static IFVRDamageable FindDamageable(Collider col)
        {
            IFVRDamageable d = col.gameObject.GetComponent(typeof(IFVRDamageable)) as IFVRDamageable;
            if (d == null && col.attachedRigidbody != null)
            {
                d = col.attachedRigidbody.gameObject.GetComponent(typeof(IFVRDamageable)) as IFVRDamageable;
            }
            return d;
        }

        public static SosigLink FindLink(Collider col)
        {
            SosigLink link = col.GetComponent<SosigLink>();
            if (link == null && col.attachedRigidbody != null) link = col.attachedRigidbody.GetComponent<SosigLink>();
            return link;
        }

        /// <summary>Every living sosig with a body link within range, and its link nearest the centre.</summary>
        public static Dictionary<Sosig, SosigLink> SosigsNear(Vector3 centre, float range)
        {
            var found = new Dictionary<Sosig, SosigLink>();
            var best = new Dictionary<Sosig, float>();
            Collider[] cols = Physics.OverlapSphere(centre, range, AgentMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < cols.Length; i++)
            {
                SosigLink link = FindLink(cols[i]);
                if (link == null || link.S == null || link.S.BodyState == Sosig.SosigBodyState.Dead) continue;
                float d = (link.transform.position - centre).sqrMagnitude;
                float prev;
                if (!best.TryGetValue(link.S, out prev) || d < prev)
                {
                    best[link.S] = d;
                    found[link.S] = link;
                }
            }
            return found;
        }

        public static SosigLink Torso(Sosig s)
        {
            SosigLink fallback = null;
            for (int i = 0; i < s.Links.Count; i++)
            {
                SosigLink l = s.Links[i];
                if (l == null) continue;
                if (l.BodyPart == SosigLink.SosigBodyPart.Torso) return l;
                if (fallback == null) fallback = l;
            }
            return fallback;
        }

        /// <summary>BO2's "health below the damage" test: would this much BO2 damage destroy the torso?</summary>
        public static bool WouldKill(Sosig s, float bo2Damage)
        {
            if (s.IsInvuln) return false;
            SosigLink torso = Torso(s);
            if (torso == null) return true;
            float blunt = bo2Damage * Bo2ToBlunt * s.DamMult_Projectile;
            if (s.AppliesDamageResistToIntegrityLoss) blunt *= s.DamMult_Blunt;
            float loss = Mathf.Clamp(blunt * 0.032f * torso.DamMult, 0f, 100f);
            return loss >= torso.GetIntegrityRatio() * 100f;
        }

        /// <summary>Kills the sosig the way BO2's scripted deaths do: no gore, it just drops (ragdoll).</summary>
        public static void Kill(Sosig s, int iff)
        {
            s.SetLastIFFDamageSource(iff);
            s.KillSosig();
        }

        /// <summary>BO2 damage that does not kill: blunt on the torso, so it still staggers.</summary>
        public static void Hurt(Sosig s, float bo2Damage, Vector3 dir, int iff, Vector3 source)
        {
            SosigLink torso = Torso(s);
            if (torso == null) return;
            Damage d = Make(Damage.DamageClass.Projectile, torso.transform.position, dir, iff, source);
            d.Dam_Blunt = bo2Damage * Bo2ToBlunt;
            Totals(d);
            torso.Damage(d);
        }

        /// <summary>BO2's zombie_gib_all: the sosig dies and bursts apart.</summary>
        public static void Gib(Sosig s, int iff, Damage.DamageClass cls)
        {
            Kill(s, iff);
            for (int i = 0; i < s.Links.Count; i++)
            {
                if (s.Links[i] != null) s.Links[i].LinkExplodes(cls);
            }
        }

        /// <summary><see cref="SosigsNear"/>, closest first (BO2's get_array_of_closest).</summary>
        public static List<KeyValuePair<Sosig, SosigLink>> SosigsNearSorted(Vector3 centre, float range)
        {
            var list = new List<KeyValuePair<Sosig, SosigLink>>(SosigsNear(centre, range));
            list.Sort((a, b) => (a.Value.transform.position - centre).sqrMagnitude.CompareTo((b.Value.transform.position - centre).sqrMagnitude));
            return list;
        }

        /// <summary>Horizontal direction from <paramref name="from"/> to <paramref name="at"/>.</summary>
        public static Vector3 Away(Vector3 from, Vector3 at, Vector3 fallback)
        {
            Vector3 d = at - from;
            d.y = 0f;
            return d.sqrMagnitude > 0.0001f ? d.normalized : fallback;
        }

        /// <summary>Throws every link of a (dead) sosig, as BO2 launches a ragdoll.</summary>
        public static void Launch(Sosig s, Vector3 velocity)
        {
            for (int i = 0; i < s.Links.Count; i++)
            {
                SosigLink l = s.Links[i];
                if (l != null && l.R != null) l.R.AddForce(velocity, ForceMode.VelocityChange);
            }
        }

        public static bool Blocked(Vector3 from, Vector3 to, int blockerMask)
        {
            Vector3 d = to - from;
            float dist = d.magnitude;
            return dist > 0.01f && Physics.Raycast(from, d / dist, dist, blockerMask, QueryTriggerInteraction.Ignore);
        }

        public static Damage Make(Damage.DamageClass cls, Vector3 point, Vector3 dir, int iff, Vector3 source)
        {
            Damage d = new Damage();
            d.Class = cls;
            d.point = point;
            d.hitNormal = -dir;
            d.strikeDir = dir;
            d.damageSize = 0.1f;
            d.Source_IFF = iff;
            d.Source_Point = source;
            return d;
        }

        public static void Totals(Damage d)
        {
            d.Dam_TotalKinetic = d.Dam_Blunt + d.Dam_Piercing + d.Dam_Cutting;
            d.Dam_TotalEnergetic = d.Dam_Thermal + d.Dam_Chilling + d.Dam_EMP;
        }

        /// <summary>A one-shot effect (sparks, frost) that rides on whatever it was spawned on.</summary>
        public static GameObject SpawnOn(GameObject prefab, Transform target, Vector3 point)
        {
            if (prefab == null) return null;
            GameObject go = (GameObject)Object.Instantiate(prefab, point, target != null ? target.rotation : Quaternion.identity);
            if (target != null) go.transform.SetParent(target, true);
            return go;
        }
    }
}
