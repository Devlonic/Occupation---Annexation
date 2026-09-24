using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OccupationAnnexation
{
    /// <summary>
    /// Daily routine of the locals while the player visits: work, fields, hauling, guard duty, leisure, sleep.
    /// An occupied (not yet annexed) town is sullen: most locals just stand around.
    /// </summary>
    public class LordJob_TownLife : LordJob
    {
        public CellRect townRect;
        public bool annexed;

        public LordJob_TownLife()
        {
        }

        public LordJob_TownLife(CellRect townRect, bool annexed)
        {
            this.townRect = townRect;
            this.annexed = annexed;
        }

        public override bool AddFleeToil => false;

        public override bool CanBlockHostileVisitors => false;

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            graph.StartingToil = new LordToil_TownLife();
            return graph;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref townRect, "townRect");
            Scribe_Values.Look(ref annexed, "annexed", false);
        }
    }

    public class LordToil_TownLife : LordToil
    {
        private const int ReassignInterval = GenDate.TicksPerHour;

        private LordJob_TownLife Job => (LordJob_TownLife)lord.LordJob;

        public override bool AllowSatisfyLongNeeds => false;

        public override void LordToilTick()
        {
            base.LordToilTick();
            if (lord.ticksInToil % ReassignInterval == 0)
            {
                UpdateAllDuties();
            }
        }

        public override void UpdateAllDuties()
        {
            Map map = lord.Map;
            CellRect rect = Job.townRect;
            IntVec3 center = rect.IsEmpty ? map.Center : rect.CenterCell;
            float radius = rect.IsEmpty ? 15f : UnityEngine.Mathf.Max(rect.Width, rect.Height) / 2f;
            int hour = GenLocalDate.HourOfDay(map);
            int day = GenLocalDate.DayOfYear(map);
            bool night = hour >= 22 || hour < 6;
            bool evening = hour >= 18 && hour < 22;
            bool crops = TownUtility.AnyCrops(map, rect);

            foreach (Pawn pawn in lord.ownedPawns)
            {
                float roll = Rand.ValueSeeded(pawn.thingIDNumber * 397 + day * 31 + (night ? 7 : 0));
                DutyDef duty = PickDuty(pawn, roll, night, evening, crops);
                pawn.mindState.duty = new PawnDuty(duty, center, radius);
            }
        }

        private DutyDef PickDuty(Pawn pawn, float roll, bool night, bool evening, bool crops)
        {
            if (night)
            {
                return roll < 0.12f && IsAdult(pawn) ? OA_TownDefOf.OA_TownGuard : OA_TownDefOf.OA_TownSleep;
            }
            if (!IsAdult(pawn))
            {
                return OA_TownDefOf.OA_TownLeisure;
            }
            if (!Job.annexed)
            {
                return roll < 0.35f ? OA_TownDefOf.OA_TownWork : OA_TownDefOf.OA_TownIdle;
            }
            if (evening)
            {
                return roll < 0.6f ? OA_TownDefOf.OA_TownLeisure : roll < 0.7f ? OA_TownDefOf.OA_TownGuard : OA_TownDefOf.OA_TownWork;
            }
            if (roll < 0.12f)
            {
                return OA_TownDefOf.OA_TownGuard;
            }
            if (roll < 0.6f)
            {
                return OA_TownDefOf.OA_TownWork;
            }
            if (roll < 0.78f)
            {
                return crops ? OA_TownDefOf.OA_TownFarm : OA_TownDefOf.OA_TownWork;
            }
            if (roll < 0.9f)
            {
                return OA_TownDefOf.OA_TownHaul;
            }
            return OA_TownDefOf.OA_TownLeisure;
        }

        private static bool IsAdult(Pawn pawn)
        {
            return pawn.ageTracker.AgeBiologicalYears >= 13;
        }
    }

    [DefOf]
    public static class OA_TownDefOf
    {
        public static DutyDef OA_TownWork;
        public static DutyDef OA_TownFarm;
        public static DutyDef OA_TownHaul;
        public static DutyDef OA_TownGuard;
        public static DutyDef OA_TownLeisure;
        public static DutyDef OA_TownIdle;
        public static DutyDef OA_TownSleep;

        public static JobDef OA_FakeWork;
        public static JobDef OA_FakeFarm;
        public static JobDef OA_Patrol;
        public static JobDef OA_TownRepair;
        public static JobDef OA_TownClean;

        static OA_TownDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(OA_TownDefOf));
        }
    }
}
