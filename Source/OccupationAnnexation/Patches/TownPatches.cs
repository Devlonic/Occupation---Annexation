using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace OccupationAnnexation
{
    /// <summary>
    /// Locals on their own town map live on a schedule: their needs do not decay while the player visits.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_NeedsTracker), nameof(Pawn_NeedsTracker.NeedsTrackerTick))]
    public static class Patch_Pawn_NeedsTracker_NeedsTrackerTick
    {
        public static bool Prefix(Pawn ___pawn)
        {
            return ___pawn?.Faction?.def != OA_DefOf.OA_Protectorate || !TownUtility.IsTownsfolk(___pawn);
        }
    }

    [HarmonyPatch(typeof(MentalBreaker), nameof(MentalBreaker.CanDoRandomMentalBreaks), MethodType.Getter)]
    public static class Patch_MentalBreaker_CanDoRandomMentalBreaks
    {
        public static void Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result && TownUtility.IsTownsfolk(___pawn))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Locals do not arm themselves from the town's stockpile (the player's tribute).
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_PickUpOpportunisticWeapon), "TryGiveJob")]
    public static class Patch_JobGiver_PickUpOpportunisticWeapon
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (TownUtility.IsTownsfolk(pawn) || SurrenderUtility.IsSurrendered(pawn))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_CE_JobGiver_TakeAndEquip
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            target = CECompat.Active ? AccessTools.Method(AccessTools.TypeByName("CombatExtended.JobGiver_TakeAndEquip"), "TryGiveJob") : null;
            return target != null;
        }

        public static MethodBase TargetMethod() => target;

        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (TownUtility.IsTownsfolk(pawn))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Killing locals of a town costs its loyalty.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill_Townsfolk
    {
        private const float LoyaltyLossPerLocalKilled = 10f;

        public static void Prefix(Pawn __instance, DamageInfo? dinfo)
        {
            if (dinfo?.Instigator?.Faction != Faction.OfPlayer || !TownUtility.IsTownsfolk(__instance))
            {
                return;
            }
            if (__instance.Map.Parent is OccupiedSettlement town)
            {
                town.loyalty = UnityEngine.Mathf.Max(0f, town.loyalty - LoyaltyLossPerLocalKilled);
                Messages.Message("OA_MessageLocalKilled".Translate(town.Label, LoyaltyLossPerLocalKilled.ToString("F0")), __instance, MessageTypeDefOf.NegativeEvent);
            }
        }
    }
}
