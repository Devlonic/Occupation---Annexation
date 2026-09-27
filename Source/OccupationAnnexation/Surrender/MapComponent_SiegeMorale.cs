using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse.AI;
using Verse.AI.Group;
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
        private const int MedicInterval = 30;

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

        /// <summary>
        /// Everyone who laid down arms here. Vanilla clears mental states when a downed pawn gets back up
        /// (and in a few other cases), so the surrender is re-applied from this list.
        /// </summary>
        public HashSet<Pawn> capitulatedPawns = new HashSet<Pawn>();

        /// <summary>Saves from before the list existed get it rebuilt once.</summary>
        private bool capitulatedListBuilt;

        /// <summary>The last time the player shot at or near those who capitulated, or hurt one of them; -1 if never.</summary>
        public int lastAttackTick = -1;

        /// <summary>The ceasefire whose medics were already announced, so the message shows once per ceasefire.</summary>
        private int medicsAnnouncedFor = -1;

        /// <summary>Set by an attack; the medics are sent back down on the next tick, outside the shot or damage code.</summary>
        private bool medicsInterruptPending;

        private readonly List<Pawn> tmpDefenders = new List<Pawn>();
        private readonly List<Pawn> tmpPawns = new List<Pawn>();

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
            Scribe_Collections.Look(ref capitulatedPawns, "capitulatedPawns", LookMode.Reference);
            Scribe_Values.Look(ref capitulatedListBuilt, "capitulatedListBuilt", false);
            Scribe_Values.Look(ref lastAttackTick, "lastAttackTick", -1);
            Scribe_Values.Look(ref medicsAnnouncedFor, "medicsAnnouncedFor", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                capitulatedPawns ??= new HashSet<Pawn>();
                capitulatedPawns.RemoveWhere(p => p == null);
            }
        }

        public override void MapComponentTick()
        {
            int tick = Find.TickManager.TicksGame + map.uniqueID;
            if (capitulated)
            {
                if (medicsInterruptPending)
                {
                    SendMedicsDown();
                }
                if (tick % MedicInterval == 0)
                {
                    UpdateMedics();
                }
                if (tick % EvaluateInterval == 0)
                {
                    MaintainSurrender();
                }
                return;
            }
            if (tick % EvaluateInterval != 0)
            {
                return;
            }
            if (!OAMod.Settings.enableCapitulation)
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

        /// <summary>
        /// Puts back into surrender anyone who got up or otherwise lost the surrendered state.
        /// </summary>
        public void MaintainSurrender()
        {
            if (!capitulatedListBuilt)
            {
                RebuildCapitulatedList();
            }
            if (capitulatedPawns.Count == 0)
            {
                return;
            }
            foreach (Pawn pawn in capitulatedPawns.ToList())
            {
                if (!StillCapitulated(pawn))
                {
                    capitulatedPawns.Remove(pawn);
                    continue;
                }
                if (!pawn.Downed && (!SurrenderUtility.IsSurrendered(pawn) || !(pawn.GetLord()?.LordJob is LordJob_Capitulated)))
                {
                    SurrenderUtility.Resurrender(pawn);
                }
            }
        }

        /// <summary>
        /// The ceasefire runs from the capitulation or from the player's last attack on those who capitulated.
        /// </summary>
        public int CeasefireStartTick => Mathf.Max(capitulationTick, lastAttackTick);

        /// <summary>
        /// When this pawn may get up to tend the wounded: its own 15 to 40 seconds into the current ceasefire.
        /// </summary>
        public int TendAllowedTick(Pawn pawn)
        {
            int start = CeasefireStartTick;
            int seed = Gen.HashCombineInt(pawn.thingIDNumber, start);
            return start + Rand.RangeInclusiveSeeded(SurrenderMedicUtility.MinCeasefireTicks, SurrenderMedicUtility.MaxCeasefireTicks, seed);
        }

        /// <summary>
        /// The player shot at or near those who capitulated, or hurt one of them: the ceasefire starts over.
        /// </summary>
        public void Notify_CeasefireBroken()
        {
            int now = Find.TickManager.TicksGame;
            if (lastAttackTick == now)
            {
                return;
            }
            lastAttackTick = now;
            medicsInterruptPending = true;
        }

        /// <summary>
        /// Everyone up tending the wounded drops their patients and lies face down again.
        /// </summary>
        private void SendMedicsDown()
        {
            medicsInterruptPending = false;
            var medics = new List<Pawn>();
            foreach (Pawn pawn in capitulatedPawns)
            {
                if (StillCapitulated(pawn) && !pawn.Downed && SurrenderUtility.IsSurrendered(pawn) && pawn.CurJobDef == JobDefOf.TendPatient)
                {
                    medics.Add(pawn);
                }
            }
            if (medics.Count == 0)
            {
                return;
            }
            foreach (Pawn pawn in medics)
            {
                SurrenderMedicUtility.StowCarriedMedicine(pawn);
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            Messages.Message("OA_MessageSurrenderedLieDown".Translate(map.Parent?.LabelCap ?? map.ToString()), new LookTargets(medics), MessageTypeDefOf.NeutralEvent);
            OAMod.DebugLog($"Attack on the surrendered at {map}: {medics.Count} medics lie down again.");
        }

        /// <summary>
        /// Gets up the medics whose time has come while someone needs tending, and stops those whose patient
        /// was taken prisoner or is gone.
        /// </summary>
        private void UpdateMedics()
        {
            if (capitulatedPawns.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpPawns.Clear();
            tmpPawns.AddRange(capitulatedPawns);
            List<Pawn> started = null;
            foreach (Pawn pawn in tmpPawns)
            {
                if (!StillCapitulated(pawn) || pawn.Downed || !SurrenderUtility.IsSurrendered(pawn))
                {
                    continue;
                }
                Job job = pawn.CurJob;
                if (job?.def == JobDefOf.TendPatient)
                {
                    if (!SurrenderMedicUtility.IsPatientFor(pawn, job.targetA.Pawn, needsTend: false))
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
                    }
                    continue;
                }
                if (job?.def != OA_DefOf.OA_Surrender || now < TendAllowedTick(pawn) || !SurrenderMedicUtility.CanDoctor(pawn))
                {
                    continue;
                }
                if (SurrenderMedicUtility.FindPatient(pawn) == null)
                {
                    continue;
                }
                pawn.jobs.CheckForJobOverride();
                if (pawn.CurJobDef == JobDefOf.TendPatient)
                {
                    started ??= new List<Pawn>();
                    started.Add(pawn);
                }
            }
            tmpPawns.Clear();
            if (started != null && medicsAnnouncedFor != CeasefireStartTick)
            {
                medicsAnnouncedFor = CeasefireStartTick;
                Messages.Message("OA_MessageSurrenderedTending".Translate(map.Parent?.LabelCap ?? map.ToString()), new LookTargets(started), MessageTypeDefOf.NeutralEvent);
                OAMod.DebugLog($"Ceasefire at {map}: {started.Count} surrendered medics get up to tend the wounded.");
            }
        }

        /// <summary>
        /// A town occupied with an older version has no list: every free person of the defeated faction
        /// on its map took part in the capitulation.
        /// </summary>
        public void MarkCapitulatedListBuilt()
        {
            capitulatedListBuilt = true;
        }

        private void RebuildCapitulatedList()
        {
            capitulatedListBuilt = true;
            if (capitulatedPawns.Count > 0 || !(map.Parent is OccupiedSettlement town) || town.originalFaction == null)
            {
                return;
            }
            foreach (Pawn pawn in map.mapPawns.SpawnedPawnsInFaction(town.originalFaction))
            {
                if (pawn.RaceProps.Humanlike && StillCapitulated(pawn))
                {
                    capitulatedPawns.Add(pawn);
                }
            }
            if (capitulatedPawns.Count > 0)
            {
                OAMod.DebugLog($"Rebuilt the capitulation list of {map}: {capitulatedPawns.Count} pawns.");
            }
        }

        public bool StillCapitulated(Pawn pawn)
        {
            return pawn != null && !pawn.Dead && !pawn.Destroyed && pawn.Spawned && pawn.Map == map
                && !pawn.IsPrisoner && !pawn.IsSlave && pawn.Faction != null && !pawn.Faction.IsPlayer
                && pawn.Faction.HostileTo(Faction.OfPlayer);
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
