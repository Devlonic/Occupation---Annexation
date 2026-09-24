using System.Collections.Generic;
using RimWorld.Planet;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Goods on their way from a town to a colony map. Holds the items while they travel.
    /// </summary>
    public class DeliveryOrder : IExposable, IThingHolder
    {
        public ThingOwner<Thing> items;
        public OccupiedSettlement source;
        public int sourceTile = -1;
        public Map destination;
        public int arrivalTick;
        public bool byPods;
        public string sourceLabel;

        public DeliveryOrder()
        {
            items = new ThingOwner<Thing>(this, oneStackOnly: false);
        }

        public IThingHolder ParentHolder => null;

        public ThingOwner GetDirectlyHeldThings() => items;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, items);
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref items, "items", this);
            Scribe_References.Look(ref source, "source");
            Scribe_Values.Look(ref sourceTile, "sourceTile", -1);
            Scribe_References.Look(ref destination, "destination");
            Scribe_Values.Look(ref arrivalTick, "arrivalTick", 0);
            Scribe_Values.Look(ref byPods, "byPods", false);
            Scribe_Values.Look(ref sourceLabel, "sourceLabel");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                items ??= new ThingOwner<Thing>(this, oneStackOnly: false);
            }
        }
    }
}
