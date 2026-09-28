using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    /// <summary>
    /// A surrendered medic carries a downed comrade into a bed of the town. Unlike vanilla rescuing it never
    /// makes anyone a guest or a prisoner of the player.
    /// </summary>
    public class JobDriver_CarryWoundedToBed : JobDriver
    {
        /// <summary>Standing on one cell this long while on the way means the way is blocked.</summary>
        private const int StuckTicks = 600;

        private IntVec3 lastCell = IntVec3.Invalid;
        private int lastMoveTick;
        private bool stuck;

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
                if (stuck && Takee != null)
                {
                    pawn.MapHeld?.GetComponent<MapComponent_SiegeMorale>()?.Notify_CarryFailed(Takee);
                }
            });

            // Nobody picks up someone who is burning: the fire is put out first.
            yield return WatchedGoto(Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedOrNull(TargetIndex.A)
                .FailOn(() => SurrenderFireUtility.FireOnDowned(Takee) != null));
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return WatchedGoto(Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch).FailOn(() => !pawn.IsCarryingPawn(Takee)));
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

        /// <summary>
        /// A medic must not stand in front of a blocked way forever (vanilla would have it attack whatever blocks it,
        /// which the surrender forbids): it gives up, and the wounded stays on the ground for a while.
        /// </summary>
        private Toil WatchedGoto(Toil toil)
        {
            toil.AddPreInitAction(() =>
            {
                lastCell = pawn.Position;
                lastMoveTick = Find.TickManager.TicksGame;
            });
            toil.FailOn(() =>
            {
                int now = Find.TickManager.TicksGame;
                if (pawn.Position != lastCell)
                {
                    lastCell = pawn.Position;
                    lastMoveTick = now;
                    return false;
                }
                if (now - lastMoveTick < StuckTicks)
                {
                    return false;
                }
                stuck = true;
                OAMod.DebugLog($"{pawn} carrying {Takee} is stuck at {pawn.Position}: {DescribeBlock()}.");
                return true;
            });
            return toil;
        }

        private string DescribeBlock()
        {
            IntVec3 next = pawn.pather.nextCell;
            string things = next.IsValid && next.InBounds(pawn.Map) ? next.GetThingList(pawn.Map)
                .Select(t => $"{t} ({t.Faction?.Name}{(t is Building_Door door ? $", can open {door.PawnCanOpen(pawn)}" : "")}{(t is Pawn other ? $", hostile {other.HostileTo(pawn)}" : "")})").ToCommaList() : "";
            return $"next cell {next} [{things}], destination {pawn.pather.Destination}, moving {pawn.pather.Moving}, lord {pawn.GetLord()?.LordJob?.GetType().Name}";
        }
    }
}
