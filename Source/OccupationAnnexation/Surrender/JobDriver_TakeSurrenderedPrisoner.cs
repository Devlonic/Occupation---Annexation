using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// Walks to a surrendered pawn, binds them and makes them a prisoner of the colony on the spot.
    /// On a non-home map prisoners wait instead of escaping, so they can be taken into a caravan.
    /// </summary>
    public class JobDriver_TakeSurrenderedPrisoner : JobDriver
    {
        private const int BindTicks = 120;

        private Pawn Victim => (Pawn)job.GetTarget(TargetIndex.A).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Victim, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !SurrenderUtility.IsSurrendered(Victim) && !Victim.Downed);
            this.FailOn(() => Victim.IsPrisoner);

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);

            yield return Toils_General.Wait(BindTicks, TargetIndex.A).WithProgressBarToilDelay(TargetIndex.A);

            Toil bind = ToilMaker.MakeToil("OA_BindPrisoner");
            bind.initAction = () =>
            {
                Pawn victim = Victim;
                victim.guest.CapturedBy(Faction.OfPlayer, pawn);
                victim.guest.WaitInsteadOfEscapingForDefaultTicks();
                MapComponent_SiegeMorale morale = victim.Map?.GetComponent<MapComponent_SiegeMorale>();
                if (morale != null)
                {
                    morale.prisonersTaken++;
                }
                Messages.Message("OA_MessageTookPrisoner".Translate(pawn.LabelShort, victim.LabelShort, pawn.Named("CAPTOR"), victim.Named("PRISONER")), victim, MessageTypeDefOf.NeutralEvent);
            };
            bind.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return bind;
        }
    }
}
