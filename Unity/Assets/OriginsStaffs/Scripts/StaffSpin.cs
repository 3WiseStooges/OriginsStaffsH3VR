using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>Turns its transform about the world up axis; particles simulated in its local space
    /// swirl with it (the blizzard and the whirlwind).</summary>
    public class StaffSpin : MonoBehaviour
    {
        public float DegreesPerSecond = 180f;

        private void Update()
        {
            transform.Rotate(Vector3.up, DegreesPerSecond * Time.deltaTime, Space.World);
        }
    }
}
