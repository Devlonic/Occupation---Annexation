using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    public class JobGiver_Surrendered : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.Downed)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(OA_DefOf.OA_Surrender, pawn.Position);
            job.expiryInterval = -1;
            return job;
        }
    }
}
