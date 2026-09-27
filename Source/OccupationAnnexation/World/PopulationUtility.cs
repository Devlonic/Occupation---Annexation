using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// People as a resource: loyal towns attract newcomers until their beds are full, and annexed towns send
    /// volunteers to the colony.
    /// </summary>
    public static class PopulationUtility
    {
        public const float GrowthMinLoyalty = 50f;
        public const float RecruitMinLoyalty = 60f;
        public const float RecruitLoyaltyCost = 8f;
        public const int RecruitCooldownTicks = GenDate.TicksPerDay * 5;

        /// <summary>A town without a snapshot, or with hardly any beds, still houses this many.</summary>
        private const int MinHousing = 3;

        /// <summary>
        /// Sleeping places in the town as it was last seen. Beds built during a visit make room for more people.
        /// </summary>
        public static int Housing(OccupiedSettlement town)
        {
            if (town.housing < 0)
            {
                town.housing = CountHousing(town.snapshot);
            }
            return Mathf.Max(MinHousing, town.housing);
        }

        public static int CountHousing(MapSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }
            int slots = 0;
            foreach (BuildingRecord record in snapshot.buildings)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(record.def);
                if (def != null && def.IsBed && def.building.bed_humanlike)
                {
                    slots += BedUtility.GetSleepingSlotsCount(def.size);
                }
            }
            return slots;
        }

        public static float GrowthChancePerDay(OccupiedSettlement town)
        {
            if (!OAMod.Settings.enablePopulationGrowth || town.loyalty < GrowthMinLoyalty || town.PopulationCount == 0 || town.PopulationCount >= Housing(town))
            {
                return 0f;
            }
            float chance = Mathf.Lerp(0.04f, 0.12f, Mathf.InverseLerp(GrowthMinLoyalty, 100f, town.loyalty));
            return town.state == OccupationState.Annexed ? chance * 1.5f : chance;
        }

        public static void DailyGrowth(OccupiedSettlement town)
        {
            if (Rand.Chance(GrowthChancePerDay(town)))
            {
                Pawn newcomer = AddNewcomer(town);
                if (newcomer != null)
                {
                    Messages.Message("OA_MessageNewcomer".Translate(town.Label, newcomer.LabelShort, newcomer.Named("PAWN")), town, MessageTypeDefOf.PositiveEvent);
                }
            }
        }

        /// <summary>
        /// Someone of the town's old people moves in, unarmed and with empty pockets.
        /// </summary>
        public static Pawn AddNewcomer(OccupiedSettlement town)
        {
            PawnKindDef kind = town.originalFactionDef?.basicMemberKind ?? PawnKindDefOf.Villager;
            Pawn pawn;
            try
            {
                pawn = Generate(kind, town);
            }
            catch (System.Exception e)
            {
                OAMod.DebugLog($"Could not generate a {kind.defName} for {town.Label} ({e.Message}); using a villager.");
                pawn = Generate(PawnKindDefOf.Villager, town);
            }
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            town.Store(pawn);
            return pawn;
        }

        private static Pawn Generate(PawnKindDef kind, OccupiedSettlement town)
        {
            var request = new PawnGenerationRequest(kind, town.Faction, PawnGenerationContext.NonPlayer, town.Tile, forceGenerateNewPawn: true, dontGiveWeapon: true);
            return PawnGenerator.GeneratePawn(request);
        }

        // ------------------------------------------------------------------ volunteers

        public static IEnumerable<Pawn> RecruitCandidates(OccupiedSettlement town)
        {
            return town.Population.Where(p => !p.Dead && !p.Downed && p.DevelopmentalStage.Adult());
        }

        public static bool CanRecruitNow(OccupiedSettlement town, out string reason)
        {
            reason = null;
            int now = Find.TickManager.TicksGame;
            if (town.state != OccupationState.Annexed)
            {
                reason = "OA_RecruitNeedsAnnexed".Translate();
            }
            else if (town.HasMap)
            {
                reason = "OA_CannotWhileVisiting".Translate();
            }
            else if (town.loyalty < RecruitMinLoyalty)
            {
                reason = "OA_RecruitNeedsLoyalty".Translate(RecruitMinLoyalty.ToString("F0"), town.loyalty.ToString("F0"));
            }
            else if (town.lastRecruitTick >= 0 && now < town.lastRecruitTick + RecruitCooldownTicks)
            {
                reason = "OA_RecruitCooldown".Translate((town.lastRecruitTick + RecruitCooldownTicks - now).ToStringTicksToPeriod());
            }
            else if (!RecruitCandidates(town).Any())
            {
                reason = "OA_RecruitNobody".Translate();
            }
            return reason == null;
        }

        /// <summary>
        /// A townsperson leaves with the caravan as a new colonist. The town resents losing its people a little.
        /// </summary>
        public static void Recruit(OccupiedSettlement town, Pawn pawn, Caravan caravan)
        {
            town.contents.Remove(pawn);
            RecruitUtility.Recruit(pawn, Faction.OfPlayer);
            caravan.AddPawn(pawn, addCarriedPawnToWorldPawnsIfAny: true);
            if (!pawn.IsWorldPawn())
            {
                Find.WorldPawns.PassToWorld(pawn);
            }
            town.loyalty = Mathf.Max(0f, town.loyalty - RecruitLoyaltyCost);
            town.lastRecruitTick = Find.TickManager.TicksGame;
            Messages.Message("OA_MessageRecruited".Translate(town.Label, pawn.LabelShort, pawn.Named("PAWN")), caravan, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>"Name, age — best skill level", as in the town tab.</summary>
        public static string Describe(Pawn pawn)
        {
            SkillRecord best = pawn.skills?.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).FirstOrDefault();
            string line = pawn.LabelCap + ", " + pawn.ageTracker.AgeBiologicalYears;
            if (best != null)
            {
                line += " — " + best.def.skillLabel.CapitalizeFirst() + " " + best.Level;
            }
            return line;
        }
    }
}
