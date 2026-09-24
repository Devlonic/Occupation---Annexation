using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// World inspector tab: who lives in the town, who guards it, and what lies in its stockpile.
    /// </summary>
    public class WITab_Town : WITab
    {
        private const float RowHeight = 24f;

        private Vector2 scrollPosition;
        private float viewHeight = 1000f;

        public WITab_Town()
        {
            size = new Vector2(440f, 500f);
            labelKey = "OA_TabTown";
        }

        private OccupiedSettlement Town => SelObject as OccupiedSettlement;

        public override bool IsVisible => Town != null;

        protected override void FillTab()
        {
            OccupiedSettlement town = Town;
            if (town == null)
            {
                return;
            }
            Rect outRect = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
            outRect.yMin += 20f;
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float y = 0f;

            if (town.HasMap)
            {
                Widgets.Label(new Rect(0f, y, viewRect.width, RowHeight * 2f), "OA_TabTownVisiting".Translate());
                y += RowHeight * 2f;
            }

            Header(ref y, viewRect.width, "OA_TabPopulation".Translate(town.PopulationCount));
            foreach (Pawn pawn in town.Population.OrderByDescending(p => p.ageTracker.AgeBiologicalYears))
            {
                SkillRecord best = pawn.skills?.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).FirstOrDefault();
                string line = pawn.LabelCap + ", " + pawn.ageTracker.AgeBiologicalYears;
                if (best != null)
                {
                    line += " — " + best.def.skillLabel.CapitalizeFirst() + " " + best.Level;
                }
                if (pawn.Downed)
                {
                    line += " (" + "Downed".Translate() + ")";
                }
                Row(ref y, viewRect.width, line);
            }

            if (town.GarrisonCount > 0)
            {
                Header(ref y, viewRect.width, "OA_TabGarrison".Translate(town.GarrisonCount));
                foreach (Pawn pawn in town.Garrison)
                {
                    Row(ref y, viewRect.width, pawn.LabelCap);
                }
            }

            Header(ref y, viewRect.width, "OA_TabStockpile".Translate(town.StockValue.ToStringMoney(), town.StockCap.ToStringMoney()));
            foreach (var group in town.Stock.GroupBy(t => t.def).OrderByDescending(g => g.Sum(t => t.MarketValue * t.stackCount)))
            {
                int count = group.Sum(t => t.stackCount);
                float value = group.Sum(t => t.MarketValue * t.stackCount);
                Row(ref y, viewRect.width, group.Key.LabelCap + " x" + count + " (" + value.ToStringMoney() + ")");
            }

            viewHeight = y + 10f;
            Widgets.EndScrollView();
        }

        private static void Header(ref float y, float width, string text)
        {
            y += 6f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, width, 30f), text);
            Text.Font = GameFont.Small;
            y += 32f;
        }

        private static void Row(ref float y, float width, string text)
        {
            Widgets.Label(new Rect(8f, y, width - 8f, RowHeight), text);
            y += RowHeight;
        }
    }
}
