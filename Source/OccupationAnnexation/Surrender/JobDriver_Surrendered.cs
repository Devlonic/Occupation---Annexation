using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// Lies face down on the spot, indefinitely.
    /// </summary>
    public class JobDriver_Surrendered : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil lie = ToilMaker.MakeToil("OA_Surrendered");
            lie.initAction = () =>
            {
                pawn.pather.StopDead();
                pawn.jobs.posture = PawnPosture.LayingOnGroundNormal;
            };
            lie.tickAction = () =>
            {
                if (pawn.jobs.posture == PawnPosture.Standing)
                {
                    pawn.jobs.posture = PawnPosture.LayingOnGroundNormal;
                }
            };
            lie.AddFinishAction(() => pawn.jobs.posture = PawnPosture.Standing);
            lie.defaultCompleteMode = ToilCompleteMode.Never;
            yield return lie;
        }
    }
}
