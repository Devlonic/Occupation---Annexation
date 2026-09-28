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
            Job fire = SurrenderFireUtility.TryGiveFireJob(pawn);
            if (fire != null)
            {
                return fire;
            }
            Job medic = SurrenderMedicUtility.TryGiveMedicJob(pawn);
            if (medic != null)
            {
                return medic;
            }
            Job job = JobMaker.MakeJob(OA_DefOf.OA_Surrender, pawn.Position);
            job.expiryInterval = -1;
            return job;
        }
    }
}
