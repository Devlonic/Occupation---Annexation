using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// A surrendered medic carries a downed comrade into a bed of the town. Unlike vanilla rescuing it never
    /// makes anyone a guest or a prisoner of the player.
    /// </summary>
    public class JobDriver_CarryWoundedToBed : JobDriver
    {
        private Pawn Takee => (Pawn)job.GetTarget(TargetIndex.A).Thing;

        private Building_Bed Bed => (Building_Bed)job.GetTarget(TargetIndex.B).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Takee, job, 1, -1, null, errorOnFailed)
                && pawn.Reserve(Bed, job, Bed.SleepingSlotsCount, 0, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            this.FailOn(() => !Takee.Downed || Takee.IsPrisoner);
            AddFinishAction(condition =>
            {
                if (pawn.carryTracker.CarriedThing != null)
                {
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                }
            });

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch).FailOn(() => !pawn.IsCarryingPawn(Takee));
            // The wounded reserves the bed for themselves when they lie down in it.
            yield return Toils_Reserve.Release(TargetIndex.B);

            Toil tuck = ToilMaker.MakeToil("OA_TuckIntoBed");
            tuck.initAction = () =>
            {
                Pawn takee = Takee;
                Building_Bed bed = Bed;
                pawn.carryTracker.TryDropCarriedThing(bed.Position, ThingPlaceMode.Direct, out _);
                if (takee.Spawned && RestUtility.CanUseBedNow(bed, takee, checkSocialProperness: false, allowMedBedEvenIfSetToNoCare: true))
                {
                    takee.jobs.Notify_TuckedIntoBed(bed);
                    takee.mindState.Notify_TuckedIntoBed();
                }
                if (!takee.InBed())
                {
                    // Whatever went wrong, nobody carries this one around in circles.
                    pawn.Map.GetComponent<MapComponent_SiegeMorale>()?.Notify_CarryFailed(takee);
                }
            };
            tuck.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return tuck;
        }
    }
}
