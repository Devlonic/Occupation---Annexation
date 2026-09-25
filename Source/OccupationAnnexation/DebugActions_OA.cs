using LudeonTK;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

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

        /// <summary>
        /// What keeps "Reform caravan" disabled: every hostile target on the current map and whether it counts as a threat.
        /// </summary>
        [DebugAction(Category, "Log hostile threats", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LogHostileThreats()
        {
            Map map = Find.CurrentMap;
            var text = new System.Text.StringBuilder();
            text.AppendLine($"[Occupation & Annexation] Hostile targets on {map} ({map.Parent?.LabelCap}): any active threat = {RimWorld.GenHostility.AnyHostileActiveThreatToPlayer(map)}");
            foreach (Verse.AI.IAttackTarget target in map.attackTargetsCache.TargetsHostileToFaction(RimWorld.Faction.OfPlayer))
            {
                Thing thing = target.Thing;
                var pawn = thing as Pawn;
                text.AppendLine($"  {thing} at {thing.Position}, faction {thing.Faction?.Name}, active threat {RimWorld.GenHostility.IsActiveThreatToPlayer(target)}, "
                    + $"surrendered {SurrenderUtility.IsSurrendered(pawn)}, capitulated {pawn != null && SurrenderUtility.HasCapitulated(pawn)}, downed {pawn?.Downed}, "
                    + $"mental state {pawn?.MentalStateDef?.defName}, lord {pawn?.GetLord()?.LordJob?.GetType().Name}");
            }
            Log.Message(text.ToString());
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
