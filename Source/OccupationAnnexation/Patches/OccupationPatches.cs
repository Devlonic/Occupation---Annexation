using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// When a hostile settlement falls and anyone is left alive, it is occupied instead of destroyed.
    /// Defenders who are all down (none able to surrender on their feet) capitulate at this point too.
    /// </summary>
    [HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
    public static class Patch_SettlementDefeatUtility_CheckDefeated
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Settlement factionBase)
        {
            if (!OAMod.Settings.enableCapitulation)
            {
                return true;
            }
            Faction faction = factionBase.Faction;
            Map map = factionBase.Map;
            if (faction == null || faction.IsPlayer || map == null || !faction.HostileTo(Faction.OfPlayer))
            {
                return true;
            }
            if (!SurrenderUtility.IsDefeated(map, faction))
            {
                return true;
            }
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (morale == null)
            {
                return true;
            }
            if (!morale.capitulated)
            {
                if (!SurrenderUtility.AnyLivingDefenders(map, faction))
                {
                    // Nobody left to surrender: the vanilla outcome (ruins).
                    return true;
                }
                SurrenderUtility.Capitulate(map, faction);
            }
            OccupationUtility.Occupy(factionBase);
            return false;
        }
    }

    /// <summary>
    /// The Protectorate is a permanent ally: goodwill with it never changes.
    /// </summary>
    [HarmonyPatch(typeof(Faction), nameof(Faction.CanChangeGoodwillFor))]
    public static class Patch_Faction_CanChangeGoodwillFor
    {
        public static void Postfix(Faction __instance, Faction other, ref bool __result)
        {
            if (__result && (ProtectorateUtility.IsProtectorate(__instance) || ProtectorateUtility.IsProtectorate(other)))
            {
                __result = false;
            }
        }
    }
}
