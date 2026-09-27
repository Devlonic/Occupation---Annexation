using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    /// <summary>
    /// Holds the surrendered defenders. Its duty is threat-disabled and keeps them lying down,
    /// even if the mental state is lost for some reason.
    /// </summary>
    public class LordJob_Capitulated : LordJob
    {
        public override bool AddFleeToil => false;

        public override bool CanBlockHostileVisitors => false;

        /// <summary>
        /// The town's doors now belong to the Protectorate, which is at war with them. Medics still have to get
        /// through their own town to carry the wounded to its beds.
        /// </summary>
        public override bool CanOpenAnyDoor(Pawn p) => true;

        /// <summary>
        /// Vanilla drops pawns from their lord when they go down. A capitulated pawn who bleeds out and later
        /// gets back up would then have neither the surrender nor the threat-disabled duty: an enemy again.
        /// </summary>
        public override bool ShouldRemovePawn(Pawn p, PawnLostCondition reason)
        {
            return reason != PawnLostCondition.Incapped;
        }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            graph.StartingToil = new LordToil_Capitulated();
            return graph;
        }
    }

    public class LordToil_Capitulated : LordToil
    {
        public override bool AllowSatisfyLongNeeds => false;

        public override void UpdateAllDuties()
        {
            foreach (Pawn pawn in lord.ownedPawns)
            {
                pawn.mindState.duty = new PawnDuty(OA_DefOf.OA_Capitulated);
            }
        }
    }
}
