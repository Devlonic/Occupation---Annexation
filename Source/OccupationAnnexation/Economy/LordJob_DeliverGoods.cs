using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    /// <summary>
    /// Townsfolk bring tribute: walk to the drop spot, unload everything, walk away.
    /// </summary>
    public class LordJob_DeliverGoods : LordJob
    {
        private IntVec3 dropSpot;

        public LordJob_DeliverGoods()
        {
        }

        public LordJob_DeliverGoods(IntVec3 dropSpot)
        {
            this.dropSpot = dropSpot;
        }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var travel = new LordToil_Travel(dropSpot) { useAvoidGrid = true };
            graph.StartingToil = travel;
            var unload = new LordToil_UnloadGoods(dropSpot);
            graph.AddToil(unload);
            var leave = new LordToil_ExitMap(LocomotionUrgency.Walk, canDig: false, interruptCurrentJob: true);
            graph.AddToil(leave);

            var arrived = new Transition(travel, unload);
            arrived.AddTrigger(new Trigger_Memo("TravelArrived"));
            arrived.AddTrigger(new Trigger_TicksPassed(GenDate.TicksPerDay / 2));
            graph.AddTransition(arrived);

            var done = new Transition(unload, leave);
            done.AddTrigger(new Trigger_TicksPassed(900));
            graph.AddTransition(done);
            return graph;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref dropSpot, "dropSpot");
        }
    }

    public class LordToil_UnloadGoods : LordToil
    {
        private readonly IntVec3 spot;

        public LordToil_UnloadGoods(IntVec3 spot)
        {
            this.spot = spot;
        }

        public override void Init()
        {
            base.Init();
            foreach (Pawn pawn in lord.ownedPawns)
            {
                pawn.inventory?.DropAllNearPawn(pawn.Position, forbid: false, unforbid: true);
            }
        }

        public override void UpdateAllDuties()
        {
            foreach (Pawn pawn in lord.ownedPawns)
            {
                pawn.mindState.duty = new PawnDuty(DutyDefOf.WanderClose, spot);
            }
        }
    }
}
