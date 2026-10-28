using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// Poses the staff's reload mechanism at any frame of BO2's reload animation, so the lever
    /// can scrub it with the player's hand instead of playing it on a timer.
    /// Track data comes from tools/export_assembly.py (via StaffAssemblyBuilder). BO2 stores these
    /// bones' translations as offsets from the bind pose and their rotations as absolute local
    /// rotations; both are already converted to Unity space.
    /// </summary>
    public class StaffReloadRig : MonoBehaviour
    {
        public int LastFrame = 75;
        public float FramesPerSecond = 30f;

        public Transform[] Bones;
        public Vector3[] BindPositions;
        public bool[] HasPosition;
        public bool[] HasRotation;

        // Flattened [track * (LastFrame + 1) + frame].
        public Vector3[] PositionOffsets;
        public Quaternion[] Rotations;

        private float m_frame = -1f;

        public float CurrentFrame
        {
            get { return m_frame; }
        }

        private void Awake()
        {
            Sample(0f);
        }

        public void Sample(float frame)
        {
            frame = Mathf.Clamp(frame, 0f, LastFrame);
            if (Mathf.Abs(frame - m_frame) < 0.0005f) return;
            m_frame = frame;

            int f0 = Mathf.FloorToInt(frame);
            int f1 = Mathf.Min(f0 + 1, LastFrame);
            float t = frame - f0;
            int stride = LastFrame + 1;
            for (int i = 0; i < Bones.Length; i++)
            {
                Transform bone = Bones[i];
                if (bone == null) continue;
                int row = i * stride;
                if (HasPosition[i])
                {
                    bone.localPosition = BindPositions[i] + Vector3.Lerp(PositionOffsets[row + f0], PositionOffsets[row + f1], t);
                }
                if (HasRotation[i])
                {
                    bone.localRotation = Quaternion.Slerp(Rotations[row + f0], Rotations[row + f1], t);
                }
            }
        }
    }
}
