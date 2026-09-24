using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Uprisings of unhappy towns and attempts of the former owners to take their town back.
    /// </summary>
    public static class TownEventsUtility
    {
        private const float UprisingLoyalty = 20f;
        private const int RetakeWarningTicks = GenDate.TicksPerDay * 2;
        private const float RetakeMtbDaysOccupied = 30f;
        private const float RetakeMtbDaysAnnexed = 60f;

        public static void DailyChecks(OccupiedSettlement town)
        {
            OASettings settings = OAMod.Settings;

            if (town.loyalty < UprisingLoyalty)
            {
                town.lowLoyaltyDays++;
            }
            else
            {
                town.lowLoyaltyDays = 0;
            }
            if (settings.enableUprisings && town.lowLoyaltyDays >= 2)
            {
                float garrisonDeterrence = Mathf.Min(0.8f, town.GarrisonCount * 0.2f);
                float chance = (UprisingLoyalty - town.loyalty) / UprisingLoyalty * 0.15f * (1f - garrisonDeterrence);
                if (Rand.Chance(chance))
                {
                    Uprising(town);
                    return;
                }
            }

            if (settings.enableRetakeAttempts && town.retakeAttackTick < 0 && FormerOwnerCanRetake(town))
            {
                float mtb = town.state == OccupationState.Occupied ? RetakeMtbDaysOccupied : RetakeMtbDaysAnnexed;
                if (Rand.MTBEventOccurs(mtb, 1f, 1f))
                {
                    RetakeAttempt(town);
                }
            }
        }

        private static bool FormerOwnerCanRetake(OccupiedSettlement town)
        {
            Faction former = town.originalFaction;
            return former != null && !former.defeated && former.HostileTo(Faction.OfPlayer);
        }

        // ------------------------------------------------------------------ uprising

        public static void Uprising(OccupiedSettlement town)
        {
            if (town.HasMap)
            {
                // Abstract events only happen while nobody is in the town.
                return;
            }
            int garrison = town.GarrisonCount;
            if (garrison > 0 && Rand.Chance(Mathf.Min(0.9f, 0.4f + 0.15f * garrison)))
            {
                int killed = Mathf.Min(town.PopulationCount - 1, Rand.RangeInclusive(1, 3));
                foreach (Pawn rebel in town.Population.InRandomOrder().Take(Mathf.Max(0, killed)).ToList())
                {
                    town.contents.Remove(rebel);
                    rebel.Kill(null);
                    Find.WorldPawns.PassToWorld(rebel, PawnDiscardDecideMode.Decide);
                }
                town.loyalty = Mathf.Max(0f, town.loyalty - 5f);
                town.lowLoyaltyDays = 0;
                Find.LetterStack.ReceiveLetter("OA_LetterUprisingSuppressedLabel".Translate(town.Label), "OA_LetterUprisingSuppressed".Translate(town.Label, killed), LetterDefOf.NeutralEvent, town);
                return;
            }
            Faction owner = town.originalFaction != null && town.originalFaction.HostileTo(Faction.OfPlayer)
                ? town.originalFaction
                : Find.FactionManager.RandomEnemyFaction(allowHidden: false, allowDefeated: true, allowNonHumanlike: false);
            if (owner == null)
            {
                town.loyalty = Mathf.Max(0f, town.loyalty - 5f);
                return;
            }
            LoseTown(town, owner, "OA_LetterUprisingLabel".Translate(town.Label), "OA_LetterUprising".Translate(town.Label, owner.Name));
        }

        // ------------------------------------------------------------------ retake attempts

        /// <summary>
        /// Announces an attack of the former owners in two days. The player can reinforce the garrison or be there to fight.
        /// </summary>
        public static void RetakeAttempt(OccupiedSettlement town)
        {
            if (town.originalFaction == null)
            {
                return;
            }
            town.retakeAttackTick = Find.TickManager.TicksGame + RetakeWarningTicks;
            Find.LetterStack.ReceiveLetter(
                "OA_LetterRetakeWarningLabel".Translate(town.Label),
                "OA_LetterRetakeWarning".Translate(town.Label, town.originalFaction.Name, RetakeWarningTicks.ToStringTicksToPeriod()),
                LetterDefOf.ThreatBig,
                town,
                town.originalFaction);
        }

        public static void ResolveRetake(OccupiedSettlement town)
        {
            town.retakeAttackTick = -1;
            Faction attacker = town.originalFaction;
            if (attacker == null || !attacker.HostileTo(Faction.OfPlayer))
            {
                return;
            }

            if (town.HasMap)
            {
                // The player is there: a real raid on the town map.
                Map map = town.Map;
                var parms = new IncidentParms
                {
                    target = map,
                    faction = attacker,
                    points = Mathf.Max(StorytellerUtility.DefaultThreatPointsNow(map), 300f),
                    raidStrategy = RaidStrategyDefOf.ImmediateAttack,
                    raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn,
                    forced = true
                };
                if (!IncidentDefOf.RaidEnemy.Worker.TryExecute(parms))
                {
                    Log.Warning("[Occupation & Annexation] Could not start the retake raid on " + town.Label);
                }
                return;
            }

            float defense = town.GarrisonCount * 1.5f + town.WorkerCount * 0.15f * (town.loyalty / 100f);
            float attack = Rand.Range(1f, 4f);
            if (defense >= attack || Rand.Chance(defense / (defense + attack)))
            {
                town.loyalty = Mathf.Min(100f, town.loyalty + 10f);
                Find.LetterStack.ReceiveLetter("OA_LetterRetakeRepelledLabel".Translate(town.Label), "OA_LetterRetakeRepelled".Translate(town.Label, attacker.Name), LetterDefOf.PositiveEvent, town, attacker);
                return;
            }
            LoseTown(town, attacker, "OA_LetterRetakeLostLabel".Translate(town.Label), "OA_LetterRetakeLost".Translate(town.Label, attacker.Name));
        }

        // ------------------------------------------------------------------ losing a town

        /// <summary>
        /// The town becomes a regular settlement of <paramref name="owner"/> again. The garrison escapes as a caravan,
        /// the population joins the new owners and the stockpile is lost.
        /// </summary>
        public static void LoseTown(OccupiedSettlement town, Faction owner, string label, string text)
        {
            int tile = town.Tile;
            string name = town.Name;
            town.WithdrawGarrison();
            foreach (Pawn pawn in town.Residents.ToList())
            {
                town.contents.Remove(pawn);
                pawn.SetFaction(owner);
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Decide);
            }
            town.Destroy();

            if (owner.defeated)
            {
                owner.defeated = false;
            }
            var settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            settlement.SetFaction(owner);
            settlement.Tile = tile;
            settlement.Name = name ?? SettlementNameGenerator.GenerateSettlementName(settlement);
            Find.WorldObjects.Add(settlement);

            Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NegativeEvent, settlement, owner);
        }
    }
}
