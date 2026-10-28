using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Plays one of BO2's impact sounds from a real 3D source that falls off with distance.
    /// H3VR's explosion sound pool is tuned for grenades that should be heard across a map, which
    /// made every fireball impact sound like it landed next to the player.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class StaffImpactSound : MonoBehaviour
    {
        public AudioClip[] Clips;
        public Vector2 VolumeRange = new Vector2(0.9f, 0.9f);
        public Vector2 PitchRange = new Vector2(1f, 1f);

        private void Start()
        {
            if (Clips == null || Clips.Length == 0) return;
            AudioSource source = GetComponent<AudioSource>();
            source.clip = Clips[Random.Range(0, Clips.Length)];
            source.volume = Random.Range(VolumeRange.x, VolumeRange.y);
            source.pitch = Random.Range(PitchRange.x, PitchRange.y);
            source.Play();
        }
    }
}
