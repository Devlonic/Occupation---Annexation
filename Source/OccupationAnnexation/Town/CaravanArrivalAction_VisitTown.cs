using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Enters a town, generating its map from the snapshot if nobody is there yet.
    /// </summary>
    public class CaravanArrivalAction_VisitTown : CaravanArrivalAction
    {
        private OccupiedSettlement town;

        public CaravanArrivalAction_VisitTown()
        {
        }

        public CaravanArrivalAction_VisitTown(OccupiedSettlement town)
        {
            this.town = town;
        }

        public override string Label => "OA_VisitTown".Translate(town.Label);

        public override string ReportString => "CaravanVisiting".Translate(town.Label);

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, int destinationTile)
        {
            FloatMenuAcceptanceReport report = base.StillValid(caravan, destinationTile);
            if (!report)
            {
                return report;
            }
            if (town == null || town.Tile != destinationTile)
            {
                return false;
            }
            return CanVisit(town);
        }

        public override void Arrived(Caravan caravan)
        {
            Enter(caravan, town);
        }

        public static void Enter(Caravan caravan, OccupiedSettlement town)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                Map map = GetOrGenerateMapUtility.GetOrGenerateMap(town.Tile, null);
                CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, CaravanDropInventoryMode.DoNotDrop);
                Messages.Message("OA_MessageEnteredTown".Translate(caravan.Label, town.Label), new LookTargets(caravan.PawnsListForReading), MessageTypeDefOf.NeutralEvent);
            }, "GeneratingMapForNewEncounter", doAsynchronously: false, null);
        }

        public static FloatMenuAcceptanceReport CanVisit(OccupiedSettlement town)
        {
            return town != null && town.Spawned;
        }

        public static IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan, OccupiedSettlement town)
        {
            return CaravanArrivalActionUtility.GetFloatMenuOptions(() => CanVisit(town), () => new CaravanArrivalAction_VisitTown(town), "OA_VisitTown".Translate(town.Label), caravan, town.Tile, town);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref town, "town");
        }
    }
}
