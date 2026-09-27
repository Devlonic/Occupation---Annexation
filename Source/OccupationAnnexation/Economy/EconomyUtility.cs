using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// The abstract daily life of a town while nobody is visiting: healing, loyalty, production.
    /// </summary>
    public static class EconomyUtility
    {
        private const float HealPerDay = 10f;

        public static void DailyTick(OccupiedSettlement town)
        {
            if (town.HasMap)
            {
                // While someone is visiting, the town is simulated on its map instead.
                return;
            }
            HealPawns(town);
            UpdateLoyalty(town);
            Produce(town, 1f);
            PopulationUtility.DailyGrowth(town);
            TownMilitiaUtility.DailyCheck(town);
            TownEventsUtility.DailyChecks(town);
        }

        public static void UpdateLoyalty(OccupiedSettlement town)
        {
            float delta = town.state == OccupationState.Occupied ? 1.5f : 0.5f;
            delta += Mathf.Min(3f, town.GarrisonCount * 0.75f);
            delta += TaxLoyaltyPerDay(town.tax);
            if (town.state == OccupationState.Annexed)
            {
                // Integrated towns drift towards content.
                delta += (70f - town.loyalty) * 0.02f;
            }
            town.loyalty = Mathf.Clamp(town.loyalty + delta, 0f, 100f);

            if (town.state == OccupationState.Occupied && !town.annexReadyNotified && town.CanAnnexNow(out _))
            {
                town.annexReadyNotified = true;
                Find.LetterStack.ReceiveLetter("OA_LetterAnnexReadyLabel".Translate(town.Label), "OA_LetterAnnexReady".Translate(town.Label), LetterDefOf.PositiveEvent, town);
            }
        }

        public static void Produce(OccupiedSettlement town, float days)
        {
            int workers = town.WorkerCount;
            if (workers <= 0 || town.profile.Count == 0)
            {
                return;
            }
            if (town.StockValue >= town.StockCap)
            {
                if (!town.stockpileFullNotified)
                {
                    town.stockpileFullNotified = true;
                    Messages.Message("OA_MessageStockpileFull".Translate(town.Label), town, MessageTypeDefOf.NeutralEvent);
                }
                return;
            }
            town.stockpileFullNotified = false;

            float efficiency = ProductionEfficiency(town);
            TechLevel techLevel = town.TechLevel;
            foreach (ProductionShare share in town.profile)
            {
                List<ProductionOutput> outputs = share.def.outputs.Where(o => o.AllowedFor(techLevel)).ToList();
                if (outputs.Count == 0)
                {
                    continue;
                }
                foreach (ProductionOutput output in outputs)
                {
                    float amount = output.perWorkerPerDay * workers * efficiency * share.share * days;
                    town.productionProgress.TryGetValue(output.thing, out float progress);
                    progress += amount;
                    int whole = Mathf.FloorToInt(progress);
                    if (whole > 0)
                    {
                        town.AddStock(output.thing, whole);
                        progress -= whole;
                    }
                    town.productionProgress[output.thing] = progress;
                }
            }
        }

        public static float ProductionEfficiency(OccupiedSettlement town)
        {
            float loyaltyFactor = Mathf.Lerp(0.3f, 1.2f, town.loyalty / 100f);
            float stateFactor = town.state == OccupationState.Occupied ? 0.5f : 1f;
            float militiaFactor = town.militia ? TownMilitiaUtility.ProductionFactor : 1f;
            return OAMod.Settings.productionMultiplier * loyaltyFactor * stateFactor * militiaFactor * TaxOutputFactor(town.tax);
        }

        /// <summary>
        /// Nobody ticks stored pawns, so wounds are tended and healed here, once a day.
        /// </summary>
        public static void HealPawns(OccupiedSettlement town)
        {
            var injuries = new List<Hediff_Injury>();
            foreach (Thing thing in town.contents.ToList())
            {
                if (!(thing is Pawn pawn) || pawn.Dead || pawn.health?.hediffSet == null)
                {
                    continue;
                }
                injuries.Clear();
                pawn.health.hediffSet.GetHediffs(ref injuries, h => !h.IsPermanent());
                foreach (Hediff_Injury injury in injuries)
                {
                    if (!injury.IsTended())
                    {
                        injury.Tended(Rand.Range(0.3f, 0.7f), 1f);
                    }
                    injury.Heal(HealPerDay);
                    if (injury.ShouldRemove)
                    {
                        pawn.health.RemoveHediff(injury);
                    }
                }
                Hediff bloodLoss = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
                if (bloodLoss != null)
                {
                    bloodLoss.Severity -= 0.5f;
                    if (bloodLoss.ShouldRemove)
                    {
                        pawn.health.RemoveHediff(bloodLoss);
                    }
                }
            }
        }

        public static float TaxLoyaltyPerDay(TaxLevel tax)
        {
            switch (tax)
            {
                case TaxLevel.Low:
                    return 1.5f;
                case TaxLevel.High:
                    return -2f;
                default:
                    return 0f;
            }
        }

        public static float TaxOutputFactor(TaxLevel tax)
        {
            switch (tax)
            {
                case TaxLevel.Low:
                    return 0.6f;
                case TaxLevel.High:
                    return 1.4f;
                default:
                    return 1f;
            }
        }

        public static string TaxLabel(TaxLevel tax)
        {
            return ("OA_Tax_" + tax).Translate();
        }

        public static string TaxDescription(TaxLevel tax)
        {
            return "OA_TaxDescription".Translate(TaxOutputFactor(tax).ToStringPercent(), TaxLoyaltyPerDay(tax).ToStringWithSign("0.#"));
        }

        public static string LoyaltyLabel(float loyalty)
        {
            if (loyalty < 15f)
            {
                return "OA_Loyalty_Rebellious".Translate();
            }
            if (loyalty < 35f)
            {
                return "OA_Loyalty_Resentful".Translate();
            }
            if (loyalty < 60f)
            {
                return "OA_Loyalty_Compliant".Translate();
            }
            if (loyalty < 85f)
            {
                return "OA_Loyalty_Loyal".Translate();
            }
            return "OA_Loyalty_Devoted".Translate();
        }
    }
}
