using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace OccupationAnnexation
{
    /// <summary>
    /// Tracks the morale of a hostile settlement's defenders while the player assaults it,
    /// and makes the survivors capitulate once the main defense is broken.
    /// </summary>
    public class MapComponent_SiegeMorale : MapComponent
    {
        private const int EvaluateInterval = 250;

        public bool initialized;
        public bool capitulated;
        public int capitulationTick = -1;
        public int engagementStartTick = -1;

        public int baselineDefenders;
        public float baselinePower;
        public int baselineTurrets;
        public Pawn commander;

        public float lastMorale = 1f;

        // Consequences that carry over into the occupation's loyalty.
        public int surrenderedKilled;
        public int prisonersTaken;

        private readonly List<Pawn> tmpDefenders = new List<Pawn>();

        public MapComponent_SiegeMorale(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref initialized, "initialized", false);
            Scribe_Values.Look(ref capitulated, "capitulated", false);
            Scribe_Values.Look(ref capitulationTick, "capitulationTick", -1);
            Scribe_Values.Look(ref engagementStartTick, "engagementStartTick", -1);
            Scribe_Values.Look(ref baselineDefenders, "baselineDefenders", 0);
            Scribe_Values.Look(ref baselinePower, "baselinePower", 0f);
            Scribe_Values.Look(ref baselineTurrets, "baselineTurrets", 0);
            Scribe_References.Look(ref commander, "commander");
            Scribe_Values.Look(ref lastMorale, "lastMorale", 1f);
            Scribe_Values.Look(ref surrenderedKilled, "surrenderedKilled", 0);
            Scribe_Values.Look(ref prisonersTaken, "prisonersTaken", 0);
        }

        public override void MapComponentTick()
        {
            if (capitulated || !OAMod.Settings.enableCapitulation)
            {
                return;
            }
            if ((Find.TickManager.TicksGame + map.uniqueID) % EvaluateInterval != 0)
            {
                return;
            }
            if (!SurrenderUtility.IsHostileSettlementMap(map, out Settlement settlement))
            {
                return;
            }
            Faction faction = settlement.Faction;
            if (!initialized)
            {
                InitBaseline(faction);
                return;
            }
            if (ShouldCapitulate(faction, out _))
            {
                SurrenderUtility.Capitulate(map, faction);
            }
        }

        private void InitBaseline(Faction faction)
        {
            List<Pawn> defenders = ActiveDefenders(faction);
            baselineDefenders = defenders.Count;
            baselinePower = TotalPower(defenders);
            baselineTurrets = CountTurrets(faction);
            commander = null;
            if (faction.leader != null && defenders.Contains(faction.leader))
            {
                commander = faction.leader;
            }
            else
            {
                float best = -1f;
                foreach (Pawn pawn in defenders)
                {
                    if (pawn.kindDef.combatPower > best)
                    {
                        best = pawn.kindDef.combatPower;
                        commander = pawn;
                    }
                }
            }
            initialized = true;
            OAMod.DebugLog($"Siege baseline on {map}: {baselineDefenders} defenders, power {baselinePower:F0}, {baselineTurrets} turrets, commander {commander?.LabelShort ?? "none"}.");
        }

        public bool ShouldCapitulate(Faction faction, out string report)
        {
            report = null;
            List<Pawn> active = ActiveDefenders(faction);
            float activePower = TotalPower(active);
            int turrets = CountTurrets(faction);

            float lostFraction = baselinePower > 0f ? Mathf.Clamp01(1f - activePower / baselinePower) : 0f;
            float turretLoss = baselineTurrets > 0 ? Mathf.Clamp01(1f - turrets / (float)baselineTurrets) : 0f;

            int now = Find.TickManager.TicksGame;
            if (engagementStartTick < 0)
            {
                if (lostFraction <= 0.001f && turretLoss <= 0.001f)
                {
                    report = "no engagement yet";
                    return false;
                }
                engagementStartTick = now;
            }

            bool commanderDown = commander != null && (commander.Dead || commander.Downed || commander.IsPrisoner || !commander.Spawned || commander.Map != map);

            float suppressedShare = 0f;
            if (CECompat.Active && active.Count > 0)
            {
                int suppressed = 0;
                foreach (Pawn pawn in active)
                {
                    if (CECompat.IsSuppressedOrHunkering(pawn))
                    {
                        suppressed++;
                    }
                }
                suppressedShare = suppressed / (float)active.Count;
            }

            float playerPower = PlayerPower();
            float ratio = playerPower / Mathf.Max(1f, activePower);

            float morale = 1f
                - lostFraction * 0.9f
                - turretLoss * 0.2f
                - (commanderDown ? 0.15f : 0f)
                - suppressedShare * 0.25f
                - Mathf.Clamp((ratio - 1f) * 0.1f, 0f, 0.25f);
            lastMorale = morale;

            OASettings settings = OAMod.Settings;
            bool enoughTime = now - engagementStartTick >= settings.minCombatTicks;
            bool brokenMorale = lostFraction >= settings.minDefenseLossForCapitulation && morale <= settings.capitulationMoraleThreshold;
            bool almostNobodyLeft = baselineDefenders >= 3 && active.Count <= 1 && lostFraction > 0.5f;

            report = $"morale {morale:F2} (threshold {settings.capitulationMoraleThreshold:F2}), lost {lostFraction:P0}, turrets lost {turretLoss:P0}, "
                + $"commander down {commanderDown}, suppressed {suppressedShare:P0}, strength ratio {ratio:F2}, active {active.Count}/{baselineDefenders}, "
                + $"combat time {(now - engagementStartTick).ToStringTicksToPeriod()}";

            return (enoughTime && brokenMorale) || almostNobodyLeft;
        }

        public List<Pawn> ActiveDefenders(Faction faction)
        {
            tmpDefenders.Clear();
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(faction);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (!SurrenderUtility.IsDefenderOf(pawn, faction) || pawn.Downed || SurrenderUtility.IsSurrendered(pawn))
                {
                    continue;
                }
                if (pawn.MentalStateDef == MentalStateDefOf.PanicFlee)
                {
                    continue;
                }
                tmpDefenders.Add(pawn);
            }
            return new List<Pawn>(tmpDefenders);
        }

        private static float Power(Pawn pawn)
        {
            return Mathf.Max(pawn.kindDef.combatPower, 20f);
        }

        private static float TotalPower(List<Pawn> pawns)
        {
            float sum = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                sum += Power(pawns[i]);
            }
            return sum;
        }

        private float PlayerPower()
        {
            float sum = 0f;
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn.Downed || pawn.IsPrisoner)
                {
                    continue;
                }
                if (pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid || pawn.training?.HasLearned(TrainableDefOf.Release) == true)
                {
                    sum += Power(pawn);
                }
            }
            return sum;
        }

        private int CountTurrets(Faction faction)
        {
            int count = 0;
            List<Building> buildings = map.listerBuildings.allBuildingsNonColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (buildings[i] is Building_Turret && buildings[i].Faction == faction)
                {
                    count++;
                }
            }
            return count;
        }
    }
}
