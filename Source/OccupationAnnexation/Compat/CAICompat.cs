using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Soft integration with CAI 5000 (Krkr.rule56). Nothing here references CAI at compile time.
    /// </summary>
    public static class CAICompat
    {
        public static readonly Type ThingCompCombatAIType = AccessTools.TypeByName("CombatAI.Comps.ThingComp_CombatAI");

        private static readonly FieldInfo dutiesField;

        public static bool Active => ThingCompCombatAIType != null;

        static CAICompat()
        {
            if (ThingCompCombatAIType != null)
            {
                dutiesField = AccessTools.Field(ThingCompCombatAIType, "duties");
            }
        }

        /// <summary>
        /// CAI keeps its own queue of custom duties (assault point, escort, hunt down) that it re-applies
        /// to pawn.mindState.duty. Surrendered pawns must lose them.
        /// </summary>
        public static void ClearCustomDuties(Pawn pawn)
        {
            if (!Active || dutiesField == null)
            {
                return;
            }
            try
            {
                var comps = pawn.AllComps;
                for (int i = 0; i < comps.Count; i++)
                {
                    if (!ThingCompCombatAIType.IsInstanceOfType(comps[i]))
                    {
                        continue;
                    }
                    object duties = dutiesField.GetValue(comps[i]);
                    if (duties == null)
                    {
                        return;
                    }
                    Type dutiesType = duties.GetType();
                    AccessTools.Field(dutiesType, "curCustomDuty")?.SetValue(duties, null);
                    if (AccessTools.Field(dutiesType, "queue")?.GetValue(duties) is IList queue)
                    {
                        queue.Clear();
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Occupation & Annexation] Failed to clear CAI duties: " + e, 0x4F41_0002);
            }
        }
    }
}
