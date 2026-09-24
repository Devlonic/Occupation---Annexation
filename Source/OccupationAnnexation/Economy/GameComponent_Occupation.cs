using System.Collections.Generic;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Game-wide state: deliveries in transit.
    /// </summary>
    public class GameComponent_Occupation : GameComponent
    {
        public List<DeliveryOrder> deliveries = new List<DeliveryOrder>();

        public static GameComponent_Occupation Instance => Current.Game.GetComponent<GameComponent_Occupation>();

        public GameComponent_Occupation(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (deliveries.Count == 0 || Find.TickManager.TicksGame % 250 != 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = deliveries.Count - 1; i >= 0; i--)
            {
                if (deliveries[i].arrivalTick <= now)
                {
                    DeliveryOrder order = deliveries[i];
                    deliveries.RemoveAt(i);
                    DeliveryUtility.Arrive(order);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref deliveries, "deliveries", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                deliveries ??= new List<DeliveryOrder>();
                deliveries.RemoveAll(d => d == null);
            }
        }
    }
}
