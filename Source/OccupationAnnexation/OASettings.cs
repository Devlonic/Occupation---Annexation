using RimWorld;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    public class OASettings : ModSettings
    {
        // Capitulation
        public bool enableCapitulation = true;
        public bool disableSettlementFlee = true;
        public float capitulationMoraleThreshold = 0.35f;
        public float minDefenseLossForCapitulation = 0.3f;
        public int minCombatTicks = 1250;

        // Occupation and annexation
        public float startingLoyalty = 20f;
        public float annexLoyaltyThreshold = 60f;
        public int minOccupationDays = 5;
        public float loyaltyLossPerKilledSurrendered = 8f;

        // Economy
        public float productionMultiplier = 1f;
        public float stockpileCapPerPawn = 400f;
        public bool allowDropPodDelivery = true;

        // Events
        public bool enableUprisings = true;
        public bool enableRetakeAttempts = true;

        public bool debugLogging;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enableCapitulation, "enableCapitulation", true);
            Scribe_Values.Look(ref disableSettlementFlee, "disableSettlementFlee", true);
            Scribe_Values.Look(ref capitulationMoraleThreshold, "capitulationMoraleThreshold", 0.35f);
            Scribe_Values.Look(ref minDefenseLossForCapitulation, "minDefenseLossForCapitulation", 0.3f);
            Scribe_Values.Look(ref minCombatTicks, "minCombatTicks", 1250);
            Scribe_Values.Look(ref startingLoyalty, "startingLoyalty", 20f);
            Scribe_Values.Look(ref annexLoyaltyThreshold, "annexLoyaltyThreshold", 60f);
            Scribe_Values.Look(ref minOccupationDays, "minOccupationDays", 5);
            Scribe_Values.Look(ref loyaltyLossPerKilledSurrendered, "loyaltyLossPerKilledSurrendered", 8f);
            Scribe_Values.Look(ref productionMultiplier, "productionMultiplier", 1f);
            Scribe_Values.Look(ref stockpileCapPerPawn, "stockpileCapPerPawn", 400f);
            Scribe_Values.Look(ref allowDropPodDelivery, "allowDropPodDelivery", true);
            Scribe_Values.Look(ref enableUprisings, "enableUprisings", true);
            Scribe_Values.Look(ref enableRetakeAttempts, "enableRetakeAttempts", true);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
        }

        private Vector2 scrollPosition;
        private float viewHeight = 900f;

        public void DoWindowContents(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);
            var list = new Listing_Standard();
            list.Begin(viewRect);

            list.Label("OA_Settings_HeaderCapitulation".Translate());
            list.CheckboxLabeled("OA_Settings_EnableCapitulation".Translate(), ref enableCapitulation, "OA_Settings_EnableCapitulation_Tip".Translate());
            list.CheckboxLabeled("OA_Settings_DisableFlee".Translate(), ref disableSettlementFlee, "OA_Settings_DisableFlee_Tip".Translate());
            capitulationMoraleThreshold = list.SliderLabeled("OA_Settings_MoraleThreshold".Translate(capitulationMoraleThreshold.ToStringPercent()), capitulationMoraleThreshold, 0.05f, 0.8f, 0.6f, "OA_Settings_MoraleThreshold_Tip".Translate());
            minDefenseLossForCapitulation = list.SliderLabeled("OA_Settings_MinDefenseLoss".Translate(minDefenseLossForCapitulation.ToStringPercent()), minDefenseLossForCapitulation, 0f, 0.9f, 0.6f, "OA_Settings_MinDefenseLoss_Tip".Translate());
            minCombatTicks = Mathf.RoundToInt(list.SliderLabeled("OA_Settings_MinCombatTime".Translate(minCombatTicks.ToStringTicksToPeriod()), minCombatTicks, 0f, 10000f, 0.6f));

            list.GapLine();
            list.Label("OA_Settings_HeaderOccupation".Translate());
            startingLoyalty = Mathf.Round(list.SliderLabeled("OA_Settings_StartingLoyalty".Translate(startingLoyalty.ToString("F0")), startingLoyalty, 0f, 60f, 0.6f));
            annexLoyaltyThreshold = Mathf.Round(list.SliderLabeled("OA_Settings_AnnexThreshold".Translate(annexLoyaltyThreshold.ToString("F0")), annexLoyaltyThreshold, 20f, 100f, 0.6f));
            minOccupationDays = Mathf.RoundToInt(list.SliderLabeled("OA_Settings_MinOccupationDays".Translate(minOccupationDays), minOccupationDays, 0f, 30f, 0.6f));

            list.GapLine();
            list.Label("OA_Settings_HeaderEconomy".Translate());
            productionMultiplier = list.SliderLabeled("OA_Settings_ProductionMultiplier".Translate(productionMultiplier.ToStringPercent()), productionMultiplier, 0.1f, 5f, 0.6f);
            stockpileCapPerPawn = Mathf.Round(list.SliderLabeled("OA_Settings_StockpileCap".Translate(stockpileCapPerPawn.ToString("F0")), stockpileCapPerPawn, 50f, 3000f, 0.6f) / 10f) * 10f;
            list.CheckboxLabeled("OA_Settings_AllowPods".Translate(), ref allowDropPodDelivery, "OA_Settings_AllowPods_Tip".Translate());

            list.GapLine();
            list.Label("OA_Settings_HeaderEvents".Translate());
            list.CheckboxLabeled("OA_Settings_EnableUprisings".Translate(), ref enableUprisings);
            list.CheckboxLabeled("OA_Settings_EnableRetake".Translate(), ref enableRetakeAttempts);

            list.GapLine();
            list.CheckboxLabeled("OA_Settings_DebugLogging".Translate(), ref debugLogging);

            viewHeight = list.CurHeight + 10f;
            list.End();
            Widgets.EndScrollView();
        }
    }
}
