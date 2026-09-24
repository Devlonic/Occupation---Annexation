using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// A pawn that laid down arms. Any mental state blocks the constant think tree (CE TakeAndEquip,
    /// opportunistic weapon pickup) and lord duties; the threat part is handled by the Pawn.ThreatDisabled patch.
    /// </summary>
    public class MentalState_Surrendered : MentalState
    {
        public override bool AllowRestingInBed => false;

        protected override bool CanEndBeforeMaxDurationNow => false;

        public override void PostStart(string reason)
        {
            base.PostStart(reason);
            SurrenderUtility.DropWeapons(pawn);
            CAICompat.ClearCustomDuties(pawn);
            pawn.mindState.enemyTarget = null;
            if (pawn.Drafted)
            {
                pawn.drafter.Drafted = false;
            }
        }

        public override void MentalStateTick()
        {
            // Never recovers on its own: ends when captured, when the town is set up, or by force.
            if (pawn.IsHashIntervalTick(30))
            {
                age += 30;
            }
        }

        public override RandomSocialMode SocialModeMax()
        {
            return RandomSocialMode.Off;
        }
    }
}
