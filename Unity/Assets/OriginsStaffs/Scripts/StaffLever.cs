using FistVR;
using UnityEngine;

namespace OriginsStaffs
{
    /// <summary>
    /// The reload mechanism under the blue rings, worked like a pump.
    ///
    /// The trigger hand holds the staff at the rings; this transform carries that hand's pose, so
    /// moving it slides the hand along the shaft. The release button (A/X in Streamlined controls,
    /// touchpad up in Classic) unlocks it.
    /// With the other hand on the black handle, closing the gap between the hands pulls the lever:
    /// past <see cref="PullSnap"/> of the stroke it snaps to the stop and BO2's reload plays on its
    /// own (tube back, crystal out, petals open, crown turns, crystal spins). Opening the gap again
    /// past <see cref="PushSnap"/> slams it home, re-seats the crystal (recharging the staff) and
    /// settles. Pushing early just fast-forwards the spin.
    /// </summary>
    public class StaffLever : MonoBehaviour
    {
        public enum Phase
        {
            Closed,
            Unlocked,
            Pulling,
            Cycling,
            Out,
            Seating
        }

        public OriginsStaff Staff;
        public StaffReloadRig Rig;
        [Tooltip("Full pump stroke (m).")]
        public float Travel = 0.08f;
        [Tooltip("Fraction of the stroke after which the pull completes itself.")]
        [Range(0.2f, 1f)] public float PullSnap = 0.6f;
        [Tooltip("Fraction of the stroke back towards home after which the push completes itself.")]
        [Range(0.2f, 1f)] public float PushSnap = 0.5f;
        [Tooltip("Speed (m/s) the lever springs to the stop or home once a pull or push snaps.")]
        public float SnapSpeed = 2.5f;
        [Tooltip("Speed (m/s) the lever springs home when the handle hand lets go mid-pull.")]
        public float ReturnSpeed = 1.5f;
        [Tooltip("How fast (m/s) the resting hand gap may shrink without counting as a pull, so slow drift is ignored.")]
        public float RestDrift = 0.05f;
        [Tooltip("Playback speed of BO2's automatic reload segments (1 = BO2's 30 fps).")]
        public float AnimSpeed = 1f;
        [Tooltip("Playback speed of the rest of the spin when the lever is pushed home early.")]
        public float RushSpeed = 4f;
        [Tooltip("A haptic tick every this many metres of pull.")]
        public float TickSpacing = 0.015f;
        [Tooltip("Unlock the lever by itself when the crystal runs dry. Off: play-testing wanted the release press every time.")]
        public bool AutoUnlockWhenEmpty = false;

        [Header("BO2 reload frames (30 fps)")]
        public float FrameUnlocked = 12.6f;
        public float FramePullStart = 12f;
        public float FrameTubeBack = 16f;
        public float FrameSpinEnd = 45f;
        public float FrameSeated = 50f;
        public float FrameHome = 57f;
        public float FrameCue1 = 14f;
        public float FrameCue2 = 29f;
        public float FrameCue3 = 43f;

        [Header("Audio")]
        public AudioEvent AudEvent_Unlock;
        public AudioEvent AudEvent_Lock;
        public AudioEvent AudEvent_Cue1;
        public AudioEvent AudEvent_Cue2;
        public AudioEvent AudEvent_Cue3;
        public AudioEvent AudEvent_Cue4;

        private Phase m_phase = Phase.Closed;
        private float m_homeZ;
        private float m_offset;
        private float m_frame;
        private float m_restGap;
        private bool m_hasRest;
        private float m_pushBase;
        private float m_lastTick;
        private bool m_cue1;
        private bool m_cue2;
        private bool m_cue3;
        private bool m_recharged;
        private bool m_pushQueued;
        private bool m_atStop;
        private bool m_autoUnlocked;

        public Phase CurrentPhase
        {
            get { return m_phase; }
        }

        /// <summary>True when the mechanism is shut and locked, i.e. the staff may fire.</summary>
        public bool IsClosed
        {
            get { return m_phase == Phase.Closed; }
        }

        /// <summary>How much brighter the crystal should glow right now (it flares while spinning).</summary>
        public float GlowMultiplier
        {
            get
            {
                if (m_phase == Phase.Cycling) return 1.6f + 0.4f * Mathf.Sin(Time.time * 25f);
                if (m_phase == Phase.Out) return 1.3f;
                return 1f;
            }
        }

        private void Awake()
        {
            m_homeZ = transform.localPosition.z;
        }

        /// <summary>The release button on the trigger hand: unlock, or lock again if unused.</summary>
        public void PressRelease()
        {
            if (m_phase == Phase.Closed) Unlock();
            else if (m_phase == Phase.Unlocked) Lock(true);
        }

        /// <summary>The trigger hand let go: an unused unlock re-locks unless the crystal is empty.</summary>
        public void OnTriggerHandReleased()
        {
            if (m_phase == Phase.Unlocked && Staff.Charges > 0) Lock(false);
        }

        /// <summary>
        /// Advances the mechanism. <paramref name="gap"/> is the distance between the two hands
        /// along the staff; it only means something when <paramref name="pumping"/> (both hands on).
        /// </summary>
        public void Tick(bool pumping, float gap, float dt)
        {
            float fps = Rig != null ? Rig.FramesPerSecond : 30f;
            if (Staff.Charges > 0) m_autoUnlocked = false;

            switch (m_phase)
            {
                case Phase.Closed:
                    m_offset = 0f;
                    m_frame = 0f;
                    m_hasRest = false;
                    if (AutoUnlockWhenEmpty && Staff.Charges == 0 && !m_autoUnlocked)
                    {
                        m_autoUnlocked = true;
                        Unlock();
                    }
                    break;

                case Phase.Unlocked:
                    m_frame = FrameUnlocked;
                    FollowPull(pumping, gap, dt);
                    if (m_offset > 0.003f)
                    {
                        m_phase = Phase.Pulling;
                        m_cue1 = m_cue2 = m_cue3 = m_recharged = m_pushQueued = m_atStop = false;
                        m_lastTick = m_offset;
                    }
                    break;

                case Phase.Pulling:
                {
                    FollowPull(pumping, gap, dt);
                    float pull = Mathf.Clamp01(m_offset / (Travel * PullSnap));
                    m_frame = Mathf.Lerp(FramePullStart, FrameTubeBack, pull);
                    CueAt(ref m_cue1, FrameCue1, AudEvent_Cue1, true);
                    if (Mathf.Abs(m_offset - m_lastTick) >= TickSpacing)
                    {
                        m_lastTick = m_offset;
                        Staff.BuzzTriggerHand(false);
                    }
                    if (pull >= 1f)
                    {
                        // The pull completes itself: the lever springs to the stop and BO2's reload takes over.
                        m_phase = Phase.Cycling;
                        m_frame = FrameTubeBack;
                        m_atStop = false;
                        CueAt(ref m_cue1, 0f, AudEvent_Cue1, true);
                        Staff.BuzzTriggerHand(true);
                    }
                    else if (m_offset <= 0.0015f)
                    {
                        m_phase = Phase.Unlocked; // pushed back before the pull completed: still unlocked
                        m_offset = 0f;
                    }
                    break;
                }

                case Phase.Cycling:
                case Phase.Out:
                {
                    if (m_pushQueued || !pumping)
                    {
                        m_pushQueued = true;
                        m_offset = Mathf.MoveTowards(m_offset, 0f, SnapSpeed * dt);
                    }
                    else if (!m_atStop)
                    {
                        m_offset = Mathf.MoveTowards(m_offset, Travel, SnapSpeed * dt);
                        if (m_offset >= Travel)
                        {
                            m_atStop = true;
                            m_pushBase = gap; // the push is measured from where the hands are now
                        }
                    }
                    else
                    {
                        m_pushBase = Mathf.Min(m_pushBase, gap); // hands still closing: push starts from the closest point
                        float target = Mathf.Clamp(Travel - (gap - m_pushBase), 0f, Travel);
                        m_offset = Mathf.MoveTowards(m_offset, target, 6f * dt);
                        if (m_offset <= Travel * (1f - PushSnap))
                        {
                            m_pushQueued = true;
                            Staff.BuzzTriggerHand(true);
                        }
                    }

                    if (m_phase == Phase.Cycling)
                    {
                        m_frame += dt * fps * (m_pushQueued ? RushSpeed : AnimSpeed);
                        CueAt(ref m_cue2, FrameCue2, AudEvent_Cue2, false);
                        CueAt(ref m_cue3, FrameCue3, AudEvent_Cue3, false);
                        if (m_frame >= FrameSpinEnd)
                        {
                            m_frame = FrameSpinEnd;
                            m_phase = m_pushQueued ? Phase.Seating : Phase.Out;
                            if (!m_pushQueued) Staff.BuzzTriggerHand(false);
                        }
                    }
                    else if (m_pushQueued)
                    {
                        m_phase = Phase.Seating;
                    }
                    break;
                }

                case Phase.Seating:
                    m_offset = Mathf.MoveTowards(m_offset, 0f, SnapSpeed * dt);
                    m_frame += dt * fps * AnimSpeed;
                    if (!m_recharged && m_frame >= FrameSeated)
                    {
                        m_recharged = true;
                        Play(AudEvent_Cue4);
                        Staff.BuzzTriggerHand(true);
                        Staff.Recharge();
                    }
                    if (m_frame >= FrameHome && m_offset <= 0f)
                    {
                        m_phase = Phase.Closed;
                        m_frame = 0f;
                    }
                    break;
            }

            Vector3 p = transform.localPosition;
            p.z = m_homeZ - m_offset;
            transform.localPosition = p;
            if (Rig != null) Rig.Sample(Mathf.Min(m_frame, FrameHome));
        }

        /// <summary>Lever follows the hands closing in; springs home if the handle hand lets go.</summary>
        private void FollowPull(bool pumping, float gap, float dt)
        {
            if (!pumping)
            {
                m_hasRest = false;
                m_offset = Mathf.MoveTowards(m_offset, 0f, ReturnSpeed * dt);
                return;
            }
            if (!m_hasRest)
            {
                m_restGap = gap;
                m_hasRest = true;
            }
            // The resting gap follows the hands apart instantly and together only slowly, so a quick
            // closing motion is a pull while drift is not.
            m_restGap = Mathf.Max(gap, m_restGap - RestDrift * dt);
            float target = Mathf.Clamp(m_restGap - gap, 0f, Travel);
            m_offset = Mathf.MoveTowards(m_offset, target, 6f * dt);
        }

        private void Unlock()
        {
            m_phase = Phase.Unlocked;
            m_hasRest = false;
            Play(AudEvent_Unlock);
            Staff.BuzzTriggerHand(false);
        }

        private void Lock(bool audible)
        {
            m_phase = Phase.Closed;
            m_offset = 0f;
            m_frame = 0f;
            if (audible)
            {
                Play(AudEvent_Lock);
                Staff.BuzzTriggerHand(false);
            }
        }

        private void CueAt(ref bool played, float frame, AudioEvent ev, bool strong)
        {
            if (played || m_frame < frame) return;
            played = true;
            Play(ev);
            if (strong) Staff.BuzzTriggerHand(true);
        }

        private void Play(AudioEvent ev)
        {
            Staff.PlaySoundAt(ev, FVRPooledAudioType.GunMech, transform.position);
        }
    }
}
