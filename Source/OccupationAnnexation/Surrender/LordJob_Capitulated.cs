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
