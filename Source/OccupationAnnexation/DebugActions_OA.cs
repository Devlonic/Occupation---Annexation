using LudeonTK;
using RimWorld.Planet;
using Verse;

namespace OccupationAnnexation
{
    public static partial class DebugActions_OA
    {
        private const string Category = "Occupation & Annexation";

        [DebugAction(Category, "Force capitulation", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceCapitulation()
        {
            Map map = Find.CurrentMap;
            if (!SurrenderUtility.IsHostileSettlementMap(map, out Settlement settlement))
            {
                Messages.Message("Current map is not a hostile settlement.", RimWorld.MessageTypeDefOf.RejectInput, false);
                return;
            }
            SurrenderUtility.Capitulate(map, settlement.Faction);
        }

        [DebugAction(Category, "Log siege morale", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogMorale()
        {
            Map map = Find.CurrentMap;
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (!SurrenderUtility.IsHostileSettlementMap(map, out Settlement settlement) || morale == null)
            {
                Log.Message("[Occupation & Annexation] Current map is not a hostile settlement.");
                return;
            }
            if (!morale.initialized)
            {
                Log.Message("[Occupation & Annexation] Morale baseline is not initialized yet.");
                return;
            }
            bool wouldCapitulate = morale.ShouldCapitulate(settlement.Faction, out string report);
            Log.Message($"[Occupation & Annexation] {settlement.LabelCap}: {report}. Capitulate now: {wouldCapitulate}. Capitulated: {morale.capitulated}.");
        }
    }
}
