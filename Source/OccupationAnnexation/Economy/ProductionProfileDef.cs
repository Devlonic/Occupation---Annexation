using System.Collections.Generic;
using RimWorld;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// What an occupied town produces. A town's mix of profiles is inferred from the buildings and
    /// fields it had when it was captured.
    /// </summary>
    public class ProductionProfileDef : Def
    {
        /// <summary>Score every town gets regardless of buildings (e.g. taxes).</summary>
        public float baseWeight;

        /// <summary>ThingDef names of buildings that indicate this trade. Strings, so modded buildings can be listed safely.</summary>
        public List<string> indicatorBuildings = new List<string>();

        public float weightPerBuilding = 1f;

        /// <summary>Whether sown crops in the town count towards this profile.</summary>
        public bool indicatorFields;

        public float weightPerCrop = 0.04f;

        public List<ProductionOutput> outputs = new List<ProductionOutput>();
    }

    public class ProductionOutput
    {
        public ThingDef thing;

        public float perWorkerPerDay = 1f;

        public TechLevel minTechLevel = TechLevel.Undefined;

        public TechLevel maxTechLevel = TechLevel.Archotech;

        public bool AllowedFor(TechLevel techLevel)
        {
            return thing != null && techLevel >= minTechLevel && techLevel <= maxTechLevel;
        }
    }

    public class ProductionShare : IExposable
    {
        public ProductionProfileDef def;
        public float share;

        public ProductionShare()
        {
        }

        public ProductionShare(ProductionProfileDef def, float share)
        {
            this.def = def;
            this.share = share;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref share, "share", 0f);
        }
    }
}
