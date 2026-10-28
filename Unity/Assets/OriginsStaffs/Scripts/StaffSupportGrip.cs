using FistVR;

namespace OriginsStaffs
{
    /// <summary>
    /// The black wrapped handle: the support hand. It steadies and aims the staff (see
    /// <see cref="OriginsStaff.GetRotTarget"/>) and anchors it while the trigger hand pumps the
    /// lever, but it has no trigger. The staff is always carried by the blue rings, so grabbing
    /// the handle of a loose staff just picks the staff up there.
    /// </summary>
    public class StaffSupportGrip : FVRInteractiveObject
    {
        public OriginsStaff Staff;

        public override bool IsInteractable()
        {
            return Staff != null && Staff.IsInteractable();
        }

        public override void BeginInteraction(FVRViveHand hand)
        {
            if (Staff != null && !Staff.IsHeld)
            {
                // Same hand-off H3VR uses for magazine grab points.
                hand.ForceSetInteractable(Staff);
                Staff.BeginInteraction(hand);
                return;
            }
            base.BeginInteraction(hand);
            if (Staff != null) Staff.PlayHandleSound(transform.position);
            hand.Buzz(hand.Buzzer.Buzz_BeginInteraction);
        }
    }
}
