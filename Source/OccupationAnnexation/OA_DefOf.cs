using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    [DefOf]
    public static class OA_DefOf
    {
        public static MentalStateDef OA_Surrendered;

        public static JobDef OA_Surrender;
        public static JobDef OA_TakeSurrenderedPrisoner;

        public static DutyDef OA_Capitulated;

        public static FactionDef OA_Protectorate;

        public static WorldObjectDef OA_OccupiedSettlement;

        public static MapGeneratorDef OA_OccupiedTown;

        static OA_DefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OA_DefOf));
        }
    }
}
