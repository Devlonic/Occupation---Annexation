using RimWorld;
using RimWorld.BaseGen;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Rebuilds the town from its snapshot. Without a snapshot (e.g. it failed to save), a fresh settlement
    /// layout without inhabitants is generated in its place.
    /// </summary>
    public class GenStep_RestoreTown : GenStep
    {
        public override int SeedPart => 1330790212;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!(map.Parent is OccupiedSettlement town))
            {
                return;
            }
            if (town.snapshot != null)
            {
                town.snapshot.Restore(map, town.Faction);
                return;
            }

            CellRect rect = (town.townRect.IsEmpty ? CellRect.CenteredOn(map.Center, 36) : town.townRect).ClipInsideMap(map);
            var resolveParams = new ResolveParams
            {
                rect = rect,
                faction = town.Faction,
                settlementDontGeneratePawns = true
            };
            BaseGen.globalSettings.map = map;
            BaseGen.globalSettings.minBuildings = 1;
            BaseGen.globalSettings.minBarracks = 1;
            BaseGen.symbolStack.Push("settlement", resolveParams);
            BaseGen.Generate();
            if (town.townRect.IsEmpty)
            {
                town.townRect = rect;
            }
        }
    }
}
