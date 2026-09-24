using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// One permanently allied "Protectorate" faction owns every occupied or annexed town.
    /// </summary>
    public static class ProtectorateUtility
    {
        public static Faction Protectorate => Find.FactionManager.FirstFactionOfDef(OA_DefOf.OA_Protectorate);

        public static bool IsProtectorate(Faction faction)
        {
            return faction != null && faction.def == OA_DefOf.OA_Protectorate;
        }

        public static Faction GetOrCreate()
        {
            Faction existing = Protectorate;
            if (existing != null)
            {
                return existing;
            }

            Faction faction = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(OA_DefOf.OA_Protectorate));
            // NewGeneratedFaction places a random settlement for every visible faction; the Protectorate only owns conquered towns.
            foreach (Settlement settlement in Find.WorldObjects.Settlements.Where(s => s.Faction == faction).ToList())
            {
                settlement.Destroy();
            }
            faction.Name = "OA_ProtectorateName".Translate(Faction.OfPlayer.Name);
            Find.FactionManager.Add(faction);

            SetRelation(faction, Faction.OfPlayer, FactionRelationKind.Ally, 100);
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (other == faction || other.IsPlayer)
                {
                    continue;
                }
                // The Protectorate shares the colony's friends and enemies.
                FactionRelationKind playerKind = other.RelationKindWith(Faction.OfPlayer);
                if (playerKind == FactionRelationKind.Hostile)
                {
                    SetRelation(faction, other, FactionRelationKind.Hostile, -100);
                }
                else
                {
                    SetRelation(faction, other, FactionRelationKind.Neutral, 0);
                }
            }
            Log.Message("[Occupation & Annexation] Created faction " + faction.Name);
            return faction;
        }

        /// <summary>
        /// Writes both sides of a relation directly. Goodwill with the Protectorate is locked by a patch,
        /// so the usual goodwill-driven relation changes never override this.
        /// </summary>
        public static void SetRelation(Faction a, Faction b, FactionRelationKind kind, int goodwill)
        {
            FactionRelation ab = a.RelationWith(b, allowNull: true);
            if (ab == null)
            {
                a.TryMakeInitialRelationsWith(b);
                ab = a.RelationWith(b);
            }
            FactionRelation ba = b.RelationWith(a);
            ab.kind = kind;
            ab.baseGoodwill = goodwill;
            ba.kind = kind;
            ba.baseGoodwill = goodwill;
        }
    }
}
