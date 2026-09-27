using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using HarmonyLib;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using UnityEngine;

namespace OccupationAnnexation
{
    /// <summary>
    /// End-to-end self test. Only runs when the game is started with "-quicktest -oa_autotest"
    /// (optionally "-oa_report=C:\path\report.txt"); otherwise it does nothing.
    /// Assault → capitulation → prisoner → occupation → leave → save/load → annex → deliveries → visit → leave → events.
    /// </summary>
    public class GameComponent_OAAutoTest : GameComponent
    {
        private static readonly bool Enabled = GenCommandLine.CommandLineArgPassed("oa_autotest");
        private const string SaveName = "OA_AutoTest";

        private int step = -1;
        private int stepStartTick = -1;
        private int lastActionTick;
        private int failures;
        private int checks;
        private Settlement target;
        private OccupiedSettlement town;
        private Caravan caravan;
        private Pawn prisonerTarget;
        private Pawn captor;
        private int populationBeforeSave;
        private float stockBeforeSave;
        private int homeItemsBefore;
        private int townTile = -1;
        private List<string> log = new List<string>();
        private Dictionary<string, int> jobHistogram = new Dictionary<string, int>();
        private bool visitChecked;
        private List<Pawn> visitLocals = new List<Pawn>();
        private int damagedAtStart;
        private int filthAtStart;
        private int uiFrames;
        private int uiStage;
        private int uiErrorsAtStart;
        private Window uiWindow;
        private static int errorsSeen;
        private static readonly List<string> errorSamples = new List<string>();
        private static bool logHooked;
        private DateTime heartbeatTime = DateTime.MinValue;
        private int heartbeatTick;
        private int medicPhase;
        private int medicMark;
        private int ceasefireStart;
        private int tendedAtStart;
        private int medicineGiven;
        private int mapMedicine;
        private bool earlyTendReported;
        private bool shotWarmupSeen;
        private Pawn medicPatient;
        private Pawn shooter;
        private List<Pawn> medics = new List<Pawn>();

        public GameComponent_OAAutoTest(Game game)
        {
            if (Enabled && !logHooked)
            {
                logHooked = true;
                Application.logMessageReceivedThreaded += (condition, stackTrace, type) =>
                {
                    if (type == LogType.Error || type == LogType.Exception)
                    {
                        errorsSeen++;
                        if (errorSamples.Count < 15)
                        {
                            errorSamples.Add(condition.Length > 300 ? condition.Substring(0, 300) : condition);
                        }
                    }
                };
            }
        }

        public override void GameComponentOnGUI()
        {
            if (Enabled)
            {
                uiFrames++;
            }
        }

        public override void ExposeData()
        {
            if (!Enabled)
            {
                return;
            }
            Scribe_Values.Look(ref step, "oaTestStep", -1);
            Scribe_Values.Look(ref failures, "oaTestFailures", 0);
            Scribe_Values.Look(ref checks, "oaTestChecks", 0);
            Scribe_References.Look(ref town, "oaTestTown");
            Scribe_References.Look(ref caravan, "oaTestCaravan");
            Scribe_Values.Look(ref populationBeforeSave, "oaTestPop", 0);
            Scribe_Values.Look(ref stockBeforeSave, "oaTestStock", 0f);
            Scribe_Values.Look(ref townTile, "oaTestTownTile", -1);
            Scribe_Collections.Look(ref log, "oaTestLog", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                log ??= new List<string>();
                stepStartTick = -1;
            }
        }

        public override void GameComponentUpdate()
        {
            // Entering a hostile map pauses the game; the test keeps time running (runs every frame, even when paused).
            if (!Enabled || step >= 100 || LongEventHandler.ShouldWaitForEvent)
            {
                return;
            }
            Prefs.DevMode = true;
            if (step == 20)
            {
                try
                {
                    UiSmokeTest();
                }
                catch (Exception e)
                {
                    Fail("Exception in the UI smoke test: " + e);
                    Next(7);
                }
                return;
            }
            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Ultrafast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            if (Find.WindowStack.WindowsForcePause)
            {
                foreach (Window window in Find.WindowStack.Windows.ToList())
                {
                    if (window.forcePause)
                    {
                        Note("Closing dialog that pauses the game: " + window.GetType().Name);
                        window.Close(doCloseSound: false);
                    }
                }
            }
        }

        public override void GameComponentTick()
        {
            if (!Enabled || step >= 100)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now % 250 == 0)
            {
                DateTime real = DateTime.Now;
                if (heartbeatTime != DateTime.MinValue)
                {
                    double seconds = (real - heartbeatTime).TotalSeconds;
                    Note($"heartbeat: {(now - heartbeatTick) / Math.Max(0.001, seconds):F0} TPS, maps {Find.Maps.Count}, current {Find.CurrentMap}");
                }
                heartbeatTime = real;
                heartbeatTick = now;
            }
            if (stepStartTick < 0)
            {
                stepStartTick = now;
            }
            try
            {
                RunStep(now);
            }
            catch (Exception e)
            {
                Fail($"Exception in step {step}: {e}");
                Finish();
            }
        }

        private int StepTicks => Find.TickManager.TicksGame - stepStartTick;

        private void Next(int nextStep)
        {
            Note($"--- step {step} -> {nextStep} after {StepTicks} ticks");
            step = nextStep;
            stepStartTick = Find.TickManager.TicksGame;
        }

        private void RunStep(int now)
        {
            switch (step)
            {
                case -1:
                    if (StepTicks > 1300)
                    {
                        Next(0);
                    }
                    break;
                case 0:
                    Setup();
                    break;
                case 1:
                    if (target != null && target.HasMap && target.Map.mapPawns.FreeColonistsSpawnedCount > 0)
                    {
                        MapComponent_SiegeMorale morale = target.Map.GetComponent<MapComponent_SiegeMorale>();
                        if (morale.initialized)
                        {
                            TrySpawnPlayerVehicle(target.Map);
                            Note($"Settlement map ready: {morale.baselineDefenders} defenders, power {morale.baselinePower:F0}, turrets {morale.baselineTurrets}");
                            Check(morale.baselineDefenders > 0, "settlement has defenders");
                            Next(2);
                        }
                    }
                    else if (StepTicks > 5000)
                    {
                        Fail("Settlement map was not generated");
                        Finish();
                    }
                    break;
                case 2:
                    BreakDefense(now);
                    break;
                case 3:
                    VerifyCapitulation();
                    break;
                case 4:
                    TakePrisoner();
                    break;
                case 5:
                    LeaveMap(6);
                    break;
                case 6:
                    VerifyCollected();
                    break;
                case 7:
                    SaveAndReload();
                    break;
                case 8:
                    VerifyAfterLoadAndAnnex();
                    break;
                case 9:
                    StartDeliveries();
                    break;
                case 10:
                    VerifyDeliveries();
                    break;
                case 11:
                    StartVisit();
                    break;
                case 12:
                    ObserveVisit();
                    break;
                case 13:
                    LeaveMap(14);
                    break;
                case 14:
                    VerifySecondCollection();
                    break;
                case 15:
                    TestEvents();
                    break;
                case 16:
                    Finish();
                    break;
                case 17:
                    TestGarrisonAndGifts();
                    break;
                case 18:
                    TestGettingUp();
                    break;
                case 19:
                    TestMedics(now);
                    break;
                case 20:
                    // Driven from GameComponentUpdate.
                    break;
            }
        }

        // ------------------------------------------------------------------ steps

        private void Setup()
        {
            OAMod.Settings.minCombatTicks = 0;
            OAMod.Settings.debugLogging = true;
            Map home = Find.AnyPlayerHomeMap;
            target = Find.WorldObjects.Settlements
                .Where(s => s.Faction != null && !s.Faction.IsPlayer && s.Faction.HostileTo(Faction.OfPlayer) && s.Faction.def.humanlikeFaction)
                .OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(s.Tile, home.Tile))
                .FirstOrDefault();
            if (target == null)
            {
                Fail("No hostile settlement in the world");
                Finish();
                return;
            }
            townTile = target.Tile;
            Note($"Target: {target.Label} of {target.Faction.Name} ({target.Faction.def.defName}, {target.Faction.def.techLevel}) at tile {target.Tile}");

            var squad = new List<Pawn>();
            for (int i = 0; i < 6; i++)
            {
                Pawn pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                squad.Add(pawn);
            }
            caravan = CaravanMaker.MakeCaravan(squad, Faction.OfPlayer, target.Tile, addToWorldPawnsIfNotAlready: true);
            SettlementUtility.Attack(caravan, target);
            Next(1);
        }

        private void BreakDefense(int now)
        {
            Map map = target.HasMap ? target.Map : null;
            if (map == null || target.Destroyed)
            {
                // The settlement object is replaced on occupation.
                Next(3);
                return;
            }
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            if (morale.capitulated)
            {
                Check(true, "capitulation triggered by morale");
                Next(3);
                return;
            }
            int fleeing = map.mapPawns.SpawnedPawnsInFaction(target.Faction).Count(p => p.MentalStateDef == MentalStateDefOf.PanicFlee);
            if (fleeing > 0)
            {
                Fail($"{fleeing} defenders are panic-fleeing although fleeing is disabled");
            }
            if (now - lastActionTick < 60)
            {
                return;
            }
            lastActionTick = now;
            List<Pawn> active = morale.ActiveDefenders(target.Faction);
            if (active.Count == 0)
            {
                Note("No active defenders left before capitulation");
                Next(3);
                return;
            }
            Pawn victim = active.RandomElement();
            if (Rand.Bool)
            {
                victim.Kill(null);
            }
            else
            {
                HealthUtility.DamageUntilDowned(victim, allowBleedingWounds: false);
            }
            morale.ShouldCapitulate(target.Faction, out string report);
            Note("Morale: " + report);
            if (StepTicks > 30000)
            {
                Fail("Defense never broke");
                Finish();
            }
        }

        private void VerifyCapitulation()
        {
            town = Find.WorldObjects.AllWorldObjects.OfType<OccupiedSettlement>().FirstOrDefault(t => t.Tile == townTile);
            if (town == null)
            {
                if (StepTicks > 2000)
                {
                    Fail("Settlement was not occupied after capitulation");
                    Finish();
                }
                return;
            }
            Map map = town.Map;
            Check(map != null && map.Parent == town, "map reparented to the occupied town");
            Check(target.Destroyed, "original settlement object removed");
            Faction protectorate = ProtectorateUtility.Protectorate;
            Check(protectorate != null, "protectorate faction created");
            Check(protectorate != null && protectorate.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Ally, "protectorate is allied");
            Faction original = town.originalFaction;
            List<Pawn> locals = map.mapPawns.SpawnedPawnsInFaction(original).Where(p => p.RaceProps.Humanlike).ToList();
            Note($"Locals after capitulation: {locals.Count} (surrendered {locals.Count(SurrenderUtility.IsSurrendered)}, downed {locals.Count(p => p.Downed)})");
            Check(locals.Count > 0, "survivors exist");
            Check(locals.All(p => SurrenderUtility.IsSurrendered(p) || p.Downed), "all survivors surrendered or downed");
            Check(locals.All(p => p.ThreatDisabled(null)), "survivors are not threats");
            Check(locals.Where(SurrenderUtility.IsSurrendered).All(p => p.equipment?.Primary == null), "surrendered pawns are unarmed");
            Check(locals.Where(p => SurrenderUtility.IsSurrendered(p) && !p.Downed).All(p => p.CurJobDef == OA_DefOf.OA_Surrender || p.CurJobDef == null), "surrendered pawns lie down");
            int foreignTurrets = map.listerBuildings.allBuildingsNonColonist.Count(b => b is Building_Turret && b.Faction == original);
            Check(foreignTurrets == 0, "no turrets left for the defeated faction");
            Note($"Profile: {town.profile.Select(p => p.def.defName + " " + p.share.ToString("F2")).ToCommaList()}; rect {town.townRect}");
            CheckReformGizmos(map, "after occupation");
            TestShowEnemyGizmo(map);
            Next(4);
        }

        private void TakePrisoner()
        {
            Map map = town.Map;
            if (prisonerTarget == null)
            {
                prisonerTarget = map.mapPawns.SpawnedPawnsInFaction(town.originalFaction)
                    .Where(SurrenderUtility.IsSurrendered)
                    .OrderBy(p => p.Downed ? 1 : 0)
                    .FirstOrDefault();
                captor = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Downed && !p.InMentalState)
                    .OrderByDescending(p => p.health.summaryHealth.SummaryHealthPercent)
                    .FirstOrDefault();
                if (prisonerTarget == null || captor == null)
                {
                    Note("Nobody to take prisoner (or no capable colonist); skipping");
                    Next(18);
                    return;
                }
                Note($"Captor {captor.LabelShort} (health {captor.health.summaryHealth.SummaryHealthPercent:P0}, drafted {captor.Drafted}, job {captor.CurJobDef?.defName}) -> {prisonerTarget.LabelShort} at distance {captor.Position.DistanceTo(prisonerTarget.Position):F0}, reachable {captor.CanReach(prisonerTarget, PathEndMode.Touch, Danger.Deadly)}, reservable {captor.CanReserve(prisonerTarget)}");
                string expectedLabel = "OA_TakePrisoner".Translate(prisonerTarget.LabelShort, prisonerTarget);
                // Float menus are only built for pawns on the map being looked at.
                Current.Game.CurrentMap = map;
                List<FloatMenuOption> options = FloatMenuMakerMap.ChoicesAtFor(prisonerTarget.DrawPos, captor);
                if (!options.Any(o => o.Label.StartsWith(expectedLabel)))
                {
                    Note("Float menu options: " + options.Select(o => o.Label).ToCommaList());
                }
                Check(options.Any(o => o.Label.StartsWith(expectedLabel)), "float menu offers taking the surrendered pawn prisoner");
                Job job = JobMaker.MakeJob(OA_DefOf.OA_TakeSurrenderedPrisoner, prisonerTarget);
                job.count = 1;
                Check(captor.jobs.TryTakeOrderedJob(job, JobTag.Misc), "prisoner job accepted");
                return;
            }
            if (StepTicks % 250 == 0)
            {
                Note($"  captor job {captor.CurJobDef?.defName} toil {captor.jobs.curDriver?.CurToilString}, pos {captor.Position}, downed {captor.Downed}, mental {captor.MentalStateDef?.defName}; victim surrendered {SurrenderUtility.IsSurrendered(prisonerTarget)}, downed {prisonerTarget.Downed}, prisoner {prisonerTarget.IsPrisoner}, job {prisonerTarget.CurJobDef?.defName}, spawned {prisonerTarget.Spawned}");
            }
            if (prisonerTarget.IsPrisonerOfColony)
            {
                CheckReformGizmos(map, "after taking a prisoner");
                Check(true, "surrendered pawn became a prisoner");
                Check(!SurrenderUtility.IsSurrendered(prisonerTarget), "prisoner no longer in the surrendered state");
                Next(18);
            }
            else if (StepTicks > 12000)
            {
                Fail("Taking a prisoner did not complete");
                Next(18);
            }
        }

        private void LeaveMap(int nextStep)
        {
            Map map = town.Map;
            if (map == null)
            {
                Next(nextStep);
                return;
            }
            List<Pawn> leaving = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony).ToList();
            caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(leaving, Faction.OfPlayer, town.Tile, town.Tile, -1, sendMessage: false);
            Note($"Left the town with {leaving.Count} pawns (caravan {caravan?.Label})");
            Next(nextStep);
        }

        private void VerifyCollected()
        {
            if (town.HasMap)
            {
                if (StepTicks > 2000)
                {
                    Fail("Town map was not removed after leaving");
                    Finish();
                }
                return;
            }
            Note($"Town after leaving: population {town.PopulationCount}, workers {town.WorkerCount}, stock {town.Stock.Count()} stacks worth {town.StockValue:F0}, loyalty {town.loyalty:F0}");
            Check(!town.Destroyed, "town still exists");
            Check(town.PopulationCount > 0, "population stored");
            Check(town.snapshot != null && town.snapshot.buildings.Count > 0, $"snapshot taken ({town.snapshot?.buildings.Count ?? 0} buildings, {town.snapshot?.plants.Count ?? 0} plants)");
            Check(town.Population.All(p => !Find.WorldPawns.Contains(p)), "population is not duplicated in world pawns");
            Check(town.Population.All(p => ProtectorateUtility.IsProtectorate(p.Faction)), "population belongs to the protectorate");
            Check(town.Population.All(p => !p.InMentalState), "population no longer surrendered");
            if (town.snapshot != null)
            {
                var defs = town.snapshot.buildings.GroupBy(b => b.def).OrderByDescending(g => g.Count()).Take(40).Select(g => g.Key + "=" + g.Count());
                Note("Snapshot buildings: " + defs.ToCommaList());
            }
            Next(20);
        }

        private void SaveAndReload()
        {
            populationBeforeSave = town.PopulationCount;
            stockBeforeSave = town.StockValue;
            step = 8;
            stepStartTick = -1;
            Note("Saving and reloading");
            GameDataSaveLoader.SaveGame(SaveName);
            LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(SaveName));
            step = 99;
        }

        private void VerifyAfterLoadAndAnnex()
        {
            if (StepTicks < 60)
            {
                return;
            }
            Check(town != null && !town.Destroyed, "town survived save/load");
            Check(town.PopulationCount == populationBeforeSave, $"population survived save/load ({town.PopulationCount}/{populationBeforeSave})");
            Check(Math.Abs(town.StockValue - stockBeforeSave) < 1f, $"stock survived save/load ({town.StockValue:F0}/{stockBeforeSave:F0})");

            town.loyalty = 90f;
            town.occupiedTick -= GenDate.TicksPerDay * 10;
            EconomyUtility.DailyTick(town);
            Check(town.CanAnnexNow(out string reason), "annexation available: " + reason);
            town.Annex();
            Check(town.state == OccupationState.Annexed, "town annexed");
            float before = town.StockValue;
            float cap = OAMod.Settings.stockpileCapPerPawn;
            OAMod.Settings.stockpileCapPerPawn = 100000f;
            EconomyUtility.Produce(town, 5f);
            OAMod.Settings.stockpileCapPerPawn = cap;
            Note($"Produced 5 days: stock {before:F0} -> {town.StockValue:F0} ({town.WorkerCount} workers, efficiency {EconomyUtility.ProductionEfficiency(town):F2})");
            Check(town.StockValue > before, "production adds to the stock");
            Next(9);
        }

        private void StartDeliveries()
        {
            Map home = Find.AnyPlayerHomeMap;
            List<Thing> stock = town.Stock.ToList();
            if (stock.Count < 2)
            {
                EconomyUtility.Produce(town, 10f);
                stock = town.Stock.ToList();
            }
            homeItemsBefore = home.listerThings.AllThings.Count(t => t.def.category == ThingCategory.Item);
            List<Thing> byCaravan = stock.Take(Math.Max(1, stock.Count / 2)).ToList();
            List<Thing> byPods = stock.Skip(byCaravan.Count).Take(3).ToList();
            DeliveryUtility.RequestDelivery(town, home, byCaravan, byPods: false);
            if (byPods.Count > 0)
            {
                DeliveryUtility.RequestDelivery(town, home, byPods, byPods: true);
            }
            foreach (DeliveryOrder order in GameComponent_Occupation.Instance.deliveries)
            {
                order.arrivalTick = Find.TickManager.TicksGame + 300;
            }
            Note($"Requested deliveries: {byCaravan.Count} stacks by caravan, {byPods.Count} by pods");
            Next(10);
        }

        private void VerifyDeliveries()
        {
            Map home = Find.AnyPlayerHomeMap;
            if (GameComponent_Occupation.Instance.deliveries.Count > 0)
            {
                return;
            }
            bool carriersPresent = home.mapPawns.AllPawnsSpawned.Any(p => ProtectorateUtility.IsProtectorate(p.Faction));
            if (StepTicks < 12000 && carriersPresent)
            {
                return;
            }
            int itemsNow = home.listerThings.AllThings.Count(t => t.def.category == ThingCategory.Item);
            Note($"Home items: {homeItemsBefore} -> {itemsNow}; carriers still on map: {carriersPresent}");
            Check(itemsNow > homeItemsBefore || home.listerThings.AllThings.Any(t => t is ActiveDropPod), "delivered goods reached the colony");
            Next(11);
        }

        private void StartVisit()
        {
            if (caravan == null || caravan.Destroyed)
            {
                caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer && c.Tile == town.Tile);
            }
            if (caravan == null)
            {
                Fail("No caravan at the town to visit it");
                Next(15);
                return;
            }
            populationBeforeSave = town.PopulationCount;
            CaravanArrivalAction_VisitTown.Enter(caravan, town);
            Next(12);
        }

        private void ObserveVisit()
        {
            if (!town.HasMap)
            {
                if (StepTicks > 3000)
                {
                    Fail("Visit map was not generated");
                    Next(15);
                }
                return;
            }
            Map map = town.Map;
            List<Pawn> locals = map.mapPawns.SpawnedPawnsInFaction(town.Faction).Where(p => p.RaceProps.Humanlike).ToList();
            if (!visitChecked)
            {
                visitChecked = true;
                visitLocals = locals.ToList();
                int buildings = map.listerBuildings.allBuildingsNonColonist.Count(b => b.Faction == town.Faction);
                int expected = 0, restored = 0;
                var missing = new Dictionary<string, int>();
                foreach (BuildingRecord record in town.snapshot.buildings)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(record.def);
                    if (def == null)
                    {
                        continue;
                    }
                    expected++;
                    if (record.pos.InBounds(map) && record.pos.GetThingList(map).Any(t => t.def == def))
                    {
                        restored++;
                    }
                    else
                    {
                        missing.TryGetValue(record.def, out int n);
                        missing[record.def] = n + 1;
                    }
                }
                Note($"Visit map: {buildings} town buildings, snapshot records restored {restored}/{expected}, {locals.Count} locals, stock left in object {town.Stock.Count()}");
                if (missing.Count > 0)
                {
                    Note("Not restored: " + missing.OrderByDescending(kv => kv.Value).Take(20).Select(kv => kv.Key + "=" + kv.Value).ToCommaList());
                }
                Check(restored >= expected * 0.97f, $"snapshot restored ({restored}/{expected})");
                damagedAtStart = MissingHitPointsInTown(map);
                filthAtStart = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count;
                Note("Filth on the fresh visit map: " + map.listerThings.ThingsInGroup(ThingRequestGroup.Filth)
                    .GroupBy(f => f.def.defName).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key + "=" + g.Count()).ToCommaList()
                    + $"; in town area: {map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count(f => town.townRect.Contains(f.Position))}");
                Check(locals.Count == populationBeforeSave, $"all locals spawned ({locals.Count}/{populationBeforeSave})");
                Check(locals.Any(p => p.GetLord()?.LordJob is LordJob_TownLife), "locals follow the town routine");
                Check(!town.Stock.Any(), "stockpile laid out on the map");
            }
            if (StepTicks % 500 == 0)
            {
                foreach (Pawn pawn in locals)
                {
                    string key = pawn.CurJobDef?.defName ?? "none";
                    jobHistogram.TryGetValue(key, out int count);
                    jobHistogram[key] = count + 1;
                }
                jobHistogram["_samples"] = jobHistogram.TryGetValue("_samples", out int samples) ? samples + 1 : 1;
            }
            if (StepTicks > GenDate.TicksPerDay)
            {
                int damagedNow = MissingHitPointsInTown(map);
                int filthNow = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count;
                Note($"Missing hit points {damagedAtStart} -> {damagedNow}; filth {filthAtStart} -> {filthNow}");
                Note("Town jobs seen: " + jobHistogram.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value).ToCommaList());
                Check(damagedAtStart == 0 || damagedNow < damagedAtStart, "locals repair battle damage");
                Check(jobHistogram.ContainsKey("LayDown"), "locals sleep at night");
                Check(jobHistogram.Keys.Any(k => k == "OA_FakeWork" || k == "OA_FakeFarm" || k == "HaulToCell" || k == "OA_Patrol" || k == "LayDown"), "locals do town jobs");
                Next(13);
            }
        }

        private int MissingHitPointsInTown(Map map)
        {
            return map.listerBuildings.allBuildingsNonColonist
                .Where(b => b.Faction == town.Faction && b.def.useHitPoints && town.townRect.Contains(b.Position))
                .Sum(b => b.MaxHitPoints - b.HitPoints);
        }

        private void VerifySecondCollection()
        {
            if (town.HasMap)
            {
                if (StepTicks > 2000)
                {
                    Fail("Town map was not removed after the visit");
                    Next(15);
                }
                return;
            }
            List<Pawn> missing = visitLocals.Where(p => !town.Population.Contains(p)).ToList();
            foreach (Pawn pawn in missing)
            {
                Note($"  missing local {pawn.LabelShort}: dead {pawn.Dead}, destroyed {pawn.Destroyed}, spawned {pawn.Spawned}, world pawn {pawn.IsWorldPawn()}, holder {pawn.ParentHolder?.GetType().Name}, faction {pawn.Faction?.Name}, carried by caravan {pawn.GetCaravan()?.Label}");
            }
            int died = missing.Count(p => p.Dead);
            Check(town.PopulationCount + died == populationBeforeSave, $"population back in the town after the visit ({town.PopulationCount}/{populationBeforeSave}, died {died})");
            Check(town.visits == 1, "visit counted");
            Next(17);
        }

        /// <summary>
        /// A leftover enemy (like Real Ruins' hostile ruin animals) is listed by the town's "enemies left" gizmo.
        /// </summary>
        private void TestShowEnemyGizmo(Map map)
        {
            string label = "OA_CommandShowEnemy".Translate(1);
            Check(!town.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == label), "no 'enemies left' gizmo without enemies");
            Pawn fox = PawnGenerator.GeneratePawn(PawnKindDef.Named("Fox_Red"), Faction.OfAncientsHostile);
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 20);
            GenSpawn.Spawn(fox, cell, map);
            List<Thing> enemies = OccupiedSettlement.RemainingEnemies(map);
            Check(enemies.Contains(fox), "a hostile ruin animal counts as a remaining enemy");
            Check(town.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == label), "'enemies left' gizmo appears");
            fox.Destroy();
            Check(OccupiedSettlement.RemainingEnemies(map).Count == 0 && !town.GetGizmos().OfType<Command>().Any(c => c.defaultLabel == label), "'enemies left' gizmo disappears");
        }

        /// <summary>
        /// Vanilla clears the mind of a pawn that stops being downed. A capitulated pawn must stay surrendered.
        /// </summary>
        private void TestGettingUp()
        {
            Map map = town.Map;
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            Pawn pawn = morale.capitulatedPawns.FirstOrDefault(p => morale.StillCapitulated(p) && p.Downed)
                ?? morale.capitulatedPawns.FirstOrDefault(p => morale.StillCapitulated(p));
            if (pawn == null)
            {
                Note("No capitulated pawn left to test getting up; skipping");
                Next(19);
                return;
            }
            if (!pawn.Downed)
            {
                HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);
            }
            Check(pawn.Downed, $"test pawn {pawn.LabelShort} is downed");
            pawn.health.RemoveAllHediffs();
            Check(!pawn.Downed, "downed capitulated pawn got back up");
            Check(SurrenderUtility.IsSurrendered(pawn), "the pawn that got up surrendered again");
            Check(pawn.ThreatDisabled(null), "the pawn that got up is not a threat");
            Check(pawn.GetLord()?.LordJob is LordJob_Capitulated, "the pawn that got up is back in the capitulated lord");

            // Other ways the surrendered state can be lost are caught by the periodic check.
            pawn.mindState.mentalStateHandler.Reset();
            morale.MaintainSurrender();
            Check(SurrenderUtility.IsSurrendered(pawn), "a lost surrender is restored by the periodic check");

            // The real chain behind "there are still enemies here": a capitulated pawn goes down later (bleeding out),
            // vanilla drops it from its lord, then it gets back up and vanilla clears its mind.
            Pawn other = morale.capitulatedPawns.FirstOrDefault(p => p != pawn && morale.StillCapitulated(p));
            if (other != null)
            {
                if (other.Downed)
                {
                    other.health.RemoveAllHediffs();
                }
                HealthUtility.DamageUntilDowned(other, allowBleedingWounds: false);
                Check(other.Downed && other.GetLord()?.LordJob is LordJob_Capitulated, "a pawn that goes down after capitulating stays in the capitulated lord");

                // Control: the old behaviour (dropped from the lord, no MakeUndowned patch) makes it an enemy again.
                MethodInfo makeUndowned = AccessTools.Method(typeof(Pawn_HealthTracker), "MakeUndowned");
                MethodInfo postfix = AccessTools.Method(typeof(Patch_Pawn_HealthTracker_MakeUndowned), "Postfix");
                var harmony = new Harmony(OAMod.HarmonyId);
                harmony.Unpatch(makeUndowned, postfix);
                try
                {
                    other.GetLord()?.RemovePawn(other);
                    other.health.RemoveAllHediffs();
                    bool threat = GenHostility.IsActiveThreatToPlayer(other);
                    Note($"Control without the patch: {other.LabelShort} downed {other.Downed}, surrendered {SurrenderUtility.IsSurrendered(other)}, active threat {threat}");
                    Check(!SurrenderUtility.IsSurrendered(other) && threat, "control: vanilla makes a recovered pawn an enemy again");
                    morale.MaintainSurrender();
                    Check(SurrenderUtility.IsSurrendered(other) && !GenHostility.IsActiveThreatToPlayer(other) && other.GetLord()?.LordJob is LordJob_Capitulated,
                        "control: the periodic check restores the surrender and the capitulated lord");
                }
                finally
                {
                    harmony.Patch(makeUndowned, postfix: new HarmonyMethod(postfix));
                }
            }

            // Saves made before the capitulation list existed: the list is rebuilt from the defeated faction's people.
            int listed = morale.capitulatedPawns.Count;
            morale.capitulatedPawns.Clear();
            Traverse.Create(morale).Field("capitulatedListBuilt").SetValue(false);
            pawn.mindState.mentalStateHandler.Reset();
            morale.MaintainSurrender();
            Check(morale.capitulatedPawns.Count > 0 && morale.capitulatedPawns.Contains(pawn), $"old saves: capitulation list rebuilt ({morale.capitulatedPawns.Count}, was {listed})");
            Check(SurrenderUtility.IsSurrendered(pawn), "old saves: pawns that lost the surrender lie down again");

            CheckReformGizmos(map, "after a downed pawn got up");
            Next(19);
        }

        /// <summary>
        /// Once the shooting has stopped for 15 to 40 seconds, those who surrendered and can doctor tend their wounded.
        /// A real shot next to them sends them back down and restarts the wait; a shot elsewhere on the map does not.
        /// </summary>
        private void TestMedics(int now)
        {
            Map map = town.Map;
            MapComponent_SiegeMorale morale = map.GetComponent<MapComponent_SiegeMorale>();
            Pawn tending = medics.FirstOrDefault(p => p.CurJobDef == JobDefOf.TendPatient);
            if (medicPhase == 3 || medicPhase == 4 || medicPhase == 9)
            {
                if (tending != null && now - ceasefireStart < SurrenderMedicUtility.MinCeasefireTicks && !earlyTendReported)
                {
                    earlyTendReported = true;
                    Fail($"{tending.LabelShort} got up to tend only {now - ceasefireStart} ticks into the ceasefire");
                }
            }
            switch (medicPhase)
            {
                case 0:
                    SetUpMedicTest(map, morale, now);
                    break;
                case 1:
                    // The shot next to a medic.
                    if (morale.lastAttackTick >= medicMark)
                    {
                        Check(true, $"a shot next to those who surrendered breaks the ceasefire ({morale.lastAttackTick - medicMark} ticks after the order)");
                        ceasefireStart = morale.lastAttackTick;
                        medicPhase = 2;
                    }
                    else if (now - medicMark > 900)
                    {
                        Fail($"the shot next to those who surrendered was not noticed (shooter job {shooter.CurJobDef?.defName}, stance {shooter.stances.curStance?.GetType().Name})");
                        ceasefireStart = morale.CeasefireStartTick;
                        medicPhase = 2;
                    }
                    break;
                case 2:
                    Check(tending == null, "nobody tends right after the shot");
                    if (!FireFarFromCapitulated(map, morale))
                    {
                        Fail("could not fire far from those who surrendered");
                        medicPhase = 4;
                        break;
                    }
                    medicMark = now;
                    medicPhase = 3;
                    break;
                case 3:
                    // The shot far away: wait until it has actually been fired.
                    if (shooter.stances.curStance is Stance_Warmup)
                    {
                        shotWarmupSeen = true;
                    }
                    if (shotWarmupSeen && shooter.stances.curStance is Stance_Cooldown)
                    {
                        Check(morale.lastAttackTick == ceasefireStart, "a shot far from those who surrendered does not break the ceasefire");
                        medicPhase = 4;
                    }
                    else if (now - medicMark > 600)
                    {
                        Fail($"the shot far away was never fired (shooter job {shooter.CurJobDef?.defName}, stance {shooter.stances.curStance?.GetType().Name})");
                        medicPhase = 4;
                    }
                    break;
                case 4:
                    if (tending != null)
                    {
                        int delay = now - ceasefireStart;
                        Note($"{tending.LabelShort} tends {tending.CurJob.targetA.Thing?.LabelShort} {delay} ticks ({delay / 60f:F1} s) into the ceasefire, medicine {tending.CurJob.targetB.Thing?.LabelShort ?? "none"}");
                        Check(delay >= SurrenderMedicUtility.MinCeasefireTicks && delay <= SurrenderMedicUtility.MaxCeasefireTicks + 31, $"the first medic gets up 15-40 s after the shooting stops ({delay / 60f:F1} s)");
                        Check(SurrenderUtility.IsSurrendered(tending) && tending.ThreatDisabled(null) && !GenHostility.IsActiveThreatToPlayer(tending), "a medic stays surrendered and is no threat");
                        Check(tending.equipment?.Primary == null, "a medic is unarmed");
                        Check(tending.MentalState?.InspectLine == "OA_SurrenderedTendingInspect".Translate(), "a medic's inspect line says it tends the wounded");
                        Thing medicine = tending.CurJob.targetB.Thing;
                        Check(medicine != null && medicine.def.IsMedicine && (tending.inventory.Contains(medicine) || tending.carryTracker.CarriedThing == medicine), "a medic uses the medicine it carries");
                        CheckReformGizmos(map, "while those who surrendered tend their wounded");
                        tendedAtStart = TendedWounds(morale);
                        medicMark = now;
                        medicPhase = 5;
                    }
                    else if (now - ceasefireStart > SurrenderMedicUtility.MaxCeasefireTicks + 600)
                    {
                        Fail("no surrendered medic tended the wounded");
                        foreach (Pawn medic in medics)
                        {
                            Note($"  {medic.LabelShort}: may tend {SurrenderMedicUtility.MayTendNow(medic)}, patient {SurrenderMedicUtility.FindPatient(medic)?.LabelShort}, job {medic.CurJobDef?.defName}, downed {medic.Downed}, surrendered {SurrenderUtility.IsSurrendered(medic)}");
                        }
                        FinishMedicTest(map);
                    }
                    break;
                case 5:
                    if (TendedWounds(morale) > tendedAtStart)
                    {
                        Check(true, $"the wounded got tended ({now - medicMark} ticks, {TendedWounds(morale) - tendedAtStart} wounds; test patient still needs tending {medicPatient.health.HasHediffsNeedingTend()})");
                        Check(MedicineWithMedics() < medicineGiven, $"the medics' own medicine gets used ({MedicineWithMedics()}/{medicineGiven} left)");
                        Check(SpawnedMedicine(map) == mapMedicine, $"the town's own medicine is left alone ({SpawnedMedicine(map)}/{mapMedicine})");
                        if (tending == null && !medicPatient.Dead)
                        {
                            // Someone must be at work for the next shot: a fresh wound (not from the player).
                            medicPatient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 3f));
                        }
                        medicMark = now;
                        medicPhase = 6;
                    }
                    else if (now - medicMark > 5000)
                    {
                        Fail($"the wounded were not tended (medic job {tending?.CurJobDef?.defName} {tending?.jobs.curDriver?.CurToilString}, test patient needs tending {medicPatient.health.HasHediffsNeedingTend()})");
                        FinishMedicTest(map);
                    }
                    break;
                case 6:
                    if (tending != null)
                    {
                        if (!FireNear(map, tending.Position))
                        {
                            Fail("could not fire next to a medic at work");
                            FinishMedicTest(map);
                            break;
                        }
                        medicPatient = tending.CurJob.targetA.Pawn ?? medicPatient;
                        mapMedicine = SpawnedMedicine(map);
                        Note($"Second shot while {tending.LabelShort} is at '{tending.jobs.curDriver?.CurToilString}', carrying {tending.carryTracker.CarriedThing?.LabelShort ?? "nothing"}");
                        medicMark = now;
                        medicPhase = 7;
                    }
                    else if (now - medicMark > 600)
                    {
                        Fail("no medic went back to work for the second shot");
                        FinishMedicTest(map);
                    }
                    break;
                case 7:
                    if (morale.lastAttackTick >= medicMark)
                    {
                        ceasefireStart = morale.lastAttackTick;
                        earlyTendReported = false;
                        medicPhase = 8;
                    }
                    else if (now - medicMark > 900)
                    {
                        Fail("the shot next to a medic at work was not noticed");
                        FinishMedicTest(map);
                    }
                    break;
                case 8:
                    Check(tending == null, "medics abandon the wounded when shots resume");
                    Check(medics.Where(p => !p.Dead && !p.Downed).All(p => p.CurJobDef == OA_DefOf.OA_Surrender), "medics lie face down again");
                    Check(medics.All(p => p.carryTracker?.CarriedThing == null), "medics are not left holding medicine");
                    Check(SpawnedMedicine(map) == mapMedicine, $"medics pocket their medicine instead of dropping it ({SpawnedMedicine(map)}/{mapMedicine} on the ground)");
                    medicPhase = 9;
                    break;
                case 9:
                    if (tending != null && now - ceasefireStart >= SurrenderMedicUtility.MinCeasefireTicks)
                    {
                        Check(now - ceasefireStart <= SurrenderMedicUtility.MaxCeasefireTicks + 31, $"medics get up again 15-40 s into the new ceasefire ({(now - ceasefireStart) / 60f:F1} s)");
                        medicPhase = 10;
                    }
                    else if (now - ceasefireStart > SurrenderMedicUtility.MaxCeasefireTicks + 600)
                    {
                        Fail("medics never got up again after the new ceasefire");
                        FinishMedicTest(map);
                    }
                    break;
                case 10:
                    // Hurting one of them in any way counts like a shot.
                    Pawn victim = medicPatient.Dead ? medics.First(p => !p.Dead) : medicPatient;
                    victim.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 1f, instigator: shooter));
                    Check(morale.lastAttackTick == now, "hurting one of those who surrendered breaks the ceasefire");
                    medicPhase = 11;
                    break;
                case 11:
                    Check(tending == null, "medics lie down after one of them is hurt");
                    FinishMedicTest(map);
                    break;
            }
        }

        private void SetUpMedicTest(Map map, MapComponent_SiegeMorale morale, int now)
        {
            List<Pawn> alive = morale.capitulatedPawns.Where(morale.StillCapitulated).ToList();
            medics = alive.Where(p => !p.Downed && SurrenderUtility.IsSurrendered(p) && SurrenderMedicUtility.CanDoctor(p)).ToList();
            if (medics.Count == 0)
            {
                Pawn candidate = alive.FirstOrDefault(p => p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor));
                candidate?.health.RemoveAllHediffs();
                medics = alive.Where(p => !p.Downed && SurrenderUtility.IsSurrendered(p) && SurrenderMedicUtility.CanDoctor(p)).ToList();
            }
            medicPatient = alive.Where(p => !medics.Contains(p)).OrderBy(p => p.Downed ? 0 : 1).FirstOrDefault();
            if (medicPatient == null && medics.Count > 1)
            {
                medicPatient = medics.Last();
                medics.Remove(medicPatient);
            }
            shooter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.InMentalState && !p.WorkTagIsDisabled(WorkTags.Violent));
            if (medics.Count == 0 || medicPatient == null || shooter == null)
            {
                Note($"Cannot test the surrendered medics (medics {medics.Count}, patient {medicPatient?.LabelShort}, shooter {shooter?.LabelShort}); skipping");
                Next(5);
                return;
            }
            if (!medicPatient.Downed)
            {
                HealthUtility.DamageUntilDowned(medicPatient, allowBleedingWounds: true);
            }
            if (!medicPatient.Dead && !medicPatient.health.HasHediffsNeedingTend())
            {
                medicPatient.TakeDamage(new DamageInfo(DamageDefOf.Cut, 4f));
            }
            Check(!medicPatient.Dead && medicPatient.Downed && medicPatient.health.HasHediffsNeedingTend(), $"wounded comrade {medicPatient.LabelShort} needs tending");
            // Settlement defenders rarely carry medicine; each medic gets some, the town's stock must stay untouched.
            foreach (Pawn medic in medics)
            {
                Thing herbal = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                herbal.stackCount = 3;
                medic.inventory.innerContainer.TryAdd(herbal);
            }
            medicineGiven = MedicineWithMedics();
            mapMedicine = SpawnedMedicine(map);
            Note($"Medics: {medics.Select(p => p.LabelShort).ToCommaList()}; ceasefire so far {now - morale.CeasefireStartTick} ticks, tending already {medics.Count(p => p.CurJobDef == JobDefOf.TendPatient)}");

            ThingDef gunDef = ThingDef.Named("Gun_Revolver");
            var gun = (ThingWithComps)ThingMaker.MakeThing(gunDef, GenStuff.DefaultStuffFor(gunDef));
            shooter.equipment.DestroyAllEquipment();
            shooter.equipment.AddEquipment(gun);
            Type ammoUser = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
            ThingComp ammo = ammoUser == null ? null : gun.AllComps.FirstOrDefault(c => ammoUser.IsInstanceOfType(c));
            if (ammo != null)
            {
                AccessTools.Method(ammoUser, "ResetAmmoCount").Invoke(ammo, new object[] { null });
            }
            shooter.drafter.Drafted = true;

            medicMark = now;
            if (!medics.Any(m => FireNear(map, m.Position)))
            {
                Fail("could not fire next to those who surrendered");
                FinishMedicTest(map);
                return;
            }
            medicPhase = 1;
        }

        private static int TendedWounds(MapComponent_SiegeMorale morale)
        {
            return morale.capitulatedPawns.Where(p => !p.Dead).Sum(p => p.health.hediffSet.hediffs.Count(h => h.IsTended()));
        }

        private int MedicineWithMedics()
        {
            return medics.Sum(p => p.inventory.innerContainer.Where(t => t.def.IsMedicine).Sum(t => t.stackCount)
                + (p.carryTracker.CarriedThing?.def.IsMedicine == true ? p.carryTracker.CarriedThing.stackCount : 0));
        }

        private static int SpawnedMedicine(Map map)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine).Sum(t => t.stackCount);
        }

        private bool FireNear(Map map, IntVec3 around)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(around, 3f, useCenter: false))
            {
                if (cell.InBounds(map) && cell.Standable(map) && !cell.Fogged(map) && cell.GetFirstPawn(map) == null && FireAt(map, cell))
                {
                    return true;
                }
            }
            return false;
        }

        private bool FireFarFromCapitulated(Map map, MapComponent_SiegeMorale morale)
        {
            List<Pawn> capitulated = morale.capitulatedPawns.Where(p => p.Spawned).ToList();
            IEnumerable<IntVec3> cells = map.AllCells
                .Where(c => c.Standable(map) && !c.Fogged(map) && c.GetFirstPawn(map) == null && capitulated.All(p => !p.Position.InHorDistOf(c, 25f)))
                .InRandomOrder()
                .Take(50);
            foreach (IntVec3 cell in cells)
            {
                if (FireAt(map, cell))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Puts the armed test colonist within range and orders a single burst at the cell, as the player does with "Attack".
        /// </summary>
        private bool FireAt(Map map, IntVec3 target)
        {
            Verb verb = shooter.equipment?.PrimaryEq?.PrimaryVerb;
            if (verb == null)
            {
                return false;
            }
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(target, 12f, useCenter: false))
            {
                if (cell.DistanceTo(target) < 5f || !cell.InBounds(map) || !cell.Standable(map) || cell.Fogged(map) || (cell.GetFirstPawn(map) != null && cell != shooter.Position))
                {
                    continue;
                }
                if (!verb.CanHitTargetFrom(cell, target))
                {
                    continue;
                }
                shooter.jobs.StopAll();
                shooter.stances.CancelBusyStanceHard();
                shooter.Position = cell;
                shooter.Notify_Teleported();
                shotWarmupSeen = false;
                Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                job.verbToUse = verb;
                job.maxNumStaticAttacks = 1;
                job.playerForced = true;
                bool ordered = shooter.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                Note($"{shooter.LabelShort} fires at {target} from {cell}: ordered {ordered}");
                return ordered;
            }
            return false;
        }

        private void FinishMedicTest(Map map)
        {
            if (shooter != null)
            {
                shooter.jobs.StopAll();
                shooter.drafter.Drafted = false;
                shooter.equipment.DestroyAllEquipment();
            }
            CheckReformGizmos(map, "after the medic test");
            Next(5);
        }

        private void TestGarrisonAndGifts()
        {
            if (caravan == null || caravan.Destroyed)
            {
                caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.Faction == Faction.OfPlayer && c.Tile == town.Tile);
            }
            Pawn guard = caravan?.PawnsListForReading.FirstOrDefault(p => p.IsFreeColonist && !p.Downed);
            if (guard == null || caravan.PawnsListForReading.Count(p => p.IsFreeColonist && !p.Downed) < 2)
            {
                Note("Not enough colonists in the caravan to test the garrison; skipping");
                Next(15);
                return;
            }
            float loyaltyBefore = town.loyalty;
            town.StationGarrison(guard, caravan);
            Check(town.GarrisonCount == 1 && !caravan.PawnsListForReading.Contains(guard), "colonist stationed as garrison");
            Check(!Find.WorldPawns.Contains(guard), "garrison is held by the town, not by world pawns");
            EconomyUtility.UpdateLoyalty(town);
            Check(town.loyalty > loyaltyBefore || town.loyalty >= 100f, $"garrison raises loyalty ({loyaltyBefore:F1} -> {town.loyalty:F1})");

            Thing gift = CaravanInventoryUtility.AllInventoryItems(caravan).FirstOrDefault();
            if (gift != null)
            {
                int stacksBefore = town.Stock.Count();
                gift.holdingOwner?.Remove(gift);
                town.Store(gift);
                Check(town.Stock.Contains(gift) || town.Stock.Count() >= stacksBefore, "gift stored in the stockpile");
            }

            town.WithdrawGarrison();
            Check(town.GarrisonCount == 0, "garrison withdrawn");
            Check(Find.WorldObjects.Caravans.Any(c => c.PawnsListForReading.Contains(guard)), "withdrawn garrison forms a caravan");
            Check(Find.WorldPawns.Contains(guard), "withdrawn colonist is a world pawn again");
            Next(15);
        }

        private void TestEvents()
        {
            int tile = town.Tile;
            TownEventsUtility.RetakeAttempt(town);
            Check(town.retakeAttackTick > 0, "retake attempt scheduled");
            TownEventsUtility.ResolveRetake(town);
            if (town.Destroyed)
            {
                Check(Find.WorldObjects.Settlements.Any(s => s.Tile == tile), "lost town became a settlement again");
            }
            else
            {
                town.loyalty = 0f;
                TownEventsUtility.Uprising(town);
                Check(town.Destroyed && Find.WorldObjects.Settlements.Any(s => s.Tile == tile), "uprising turned the town into a hostile settlement");
            }
            Next(16);
        }

        /// <summary>
        /// Opens every window and tab of the mod for a few frames; errors while drawing are caught by the log hook.
        /// </summary>
        private void UiSmokeTest()
        {
            const int FramesPerStage = 30;
            if (uiStage > 0)
            {
                if (uiFrames < FramesPerStage)
                {
                    return;
                }
                uiWindow?.Close(doCloseSound: false);
                uiWindow = null;
            }
            else
            {
                uiErrorsAtStart = errorsSeen;
            }
            uiFrames = 0;
            switch (uiStage++)
            {
                case 0:
                    Note("UI: mod settings");
                    uiWindow = new Dialog_ModSettings(LoadedModManager.GetMod<OAMod>());
                    Find.WindowStack.Add(uiWindow);
                    break;
                case 1:
                    Note("UI: town on the world map (inspect string, gizmos, town tab)");
                    CameraJumper.TryShowWorld();
                    Find.WorldSelector.ClearSelection();
                    Find.WorldSelector.Select(town, playSound: false);
                    Find.World.UI.inspectPane.OpenTabType = typeof(WITab_Town);
                    break;
                case 2:
                    Note("UI: caravan at the town (caravan gizmos)");
                    Find.WorldSelector.ClearSelection();
                    Find.WorldSelector.Select(caravan, playSound: false);
                    Check(town.GetFloatMenuOptions(caravan).Any(), "town offers caravan float menu options");
                    Check(town.GetCaravanGizmos(caravan).Any(), "town offers caravan gizmos");
                    Check(town.GetGizmos().Any(), "town offers gizmos");
                    break;
                case 3:
                    Note("UI: take from stockpile dialog");
                    uiWindow = new Dialog_StockpileTransfer(town, caravan, gift: false);
                    Find.WindowStack.Add(uiWindow);
                    break;
                case 4:
                    Note("UI: gift dialog");
                    uiWindow = new Dialog_StockpileTransfer(town, caravan, gift: true);
                    Find.WindowStack.Add(uiWindow);
                    break;
                case 5:
                    Note("UI: delivery dialog");
                    uiWindow = new Dialog_StockpileTransfer(town, Find.AnyPlayerHomeMap);
                    Find.WindowStack.Add(uiWindow);
                    break;
                default:
                    Find.WorldSelector.ClearSelection();
                    CameraJumper.TryHideWorld();
                    Check(errorsSeen == uiErrorsAtStart, $"UI drew without errors ({errorsSeen - uiErrorsAtStart} errors)");
                    uiStage = 0;
                    Next(7);
                    return;
            }
        }

        // ------------------------------------------------------------------ Vehicle Framework

        private static readonly Type VehiclePawnType = AccessTools.TypeByName("Vehicles.VehiclePawn");

        private void TrySpawnPlayerVehicle(Map map)
        {
            if (VehiclePawnType == null)
            {
                return;
            }
            try
            {
                Type defType = AccessTools.TypeByName("Vehicles.VehicleDef");
                Def def = GenDefDatabase.GetDefSilentFail(defType, "VVE_BangBus") ?? GenDefDatabase.GetAllDefsInDatabaseForDef(defType).FirstOrDefault();
                MethodInfo generate = AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleSpawner"), "GenerateVehicle", new[] { defType, typeof(Faction) });
                var vehicle = (Pawn)generate.Invoke(null, new object[] { def, Faction.OfPlayer });
                Pawn colonist = map.mapPawns.FreeColonistsSpawned.First();
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(colonist.Position, map, 8, c => c.Standable(map));
                GenSpawn.Spawn(vehicle, cell, map, Rot4.North);
                Note($"Spawned player vehicle {vehicle.LabelShort} ({def.defName}) at {cell}");
            }
            catch (Exception e)
            {
                Fail("Could not spawn a player vehicle: " + e);
            }
        }

        /// <summary>
        /// Vanilla "Reform caravan" and Vehicle Framework's "Reform vehicle caravan" must both be usable once the town is taken.
        /// </summary>
        private void CheckReformGizmos(Map map, string when)
        {
            bool vanillaThreat = GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: true);
            bool defaultThreat = GenHostility.AnyHostileActiveThreatTo(map, Faction.OfPlayer, out IAttackTarget threat);
            Note($"Threats {when}: vanilla check {vanillaThreat}, default check {defaultThreat}, first threat {threat?.Thing?.ToString() ?? "none"} ({threat?.Thing?.Faction?.Name})");
            int listed = 0;
            foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToFaction(Faction.OfPlayer))
            {
                if (listed++ >= 25)
                {
                    break;
                }
                Thing thing = target.Thing;
                Note($"  hostile target {thing} [{thing.GetType().Name}] faction {thing.Faction?.Name}, active threat {GenHostility.IsActiveThreatTo(target, Faction.OfPlayer)}, threat disabled {target.ThreatDisabled(null)}, surrendered {thing is Pawn p && SurrenderUtility.IsSurrendered(p)}, downed {(thing as Pawn)?.Downed}");
            }
            FormCaravanComp comp = map.Parent.GetComponent<FormCaravanComp>();
            if (comp == null)
            {
                Fail("the town has no FormCaravanComp");
                return;
            }
            foreach (Gizmo gizmo in comp.GetGizmos())
            {
                if (gizmo is Command command)
                {
                    Note($"  caravan gizmo '{command.defaultLabel}' disabled {command.Disabled} {command.disabledReason}");
                    if (VehiclePawnType != null && command.defaultLabel == "VF_CommandReformVehicleCaravan".Translate())
                    {
                        Check(!command.Disabled, $"Reform vehicle caravan is available {when}");
                    }
                }
            }
            Check(!vanillaThreat && !defaultThreat, $"no active threats {when}");
        }

        // ------------------------------------------------------------------ reporting

        private void Check(bool condition, string what)
        {
            checks++;
            if (condition)
            {
                Note("PASS " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private void Fail(string what)
        {
            failures++;
            Note("FAIL " + what);
        }

        private void Note(string text)
        {
            string line = $"[{Find.TickManager?.TicksGame}] {text}";
            log.Add(line);
            Log.Message("[OA AutoTest] " + line);
        }

        private void Finish()
        {
            Note($"Errors logged during the whole run: {errorsSeen}");
            foreach (string sample in errorSamples)
            {
                Note("  error: " + sample);
            }
            Note($"DONE: {checks} checks, {failures} failures");
            step = 100;
            try
            {
                if (GenCommandLine.TryGetCommandLineArg("oa_report", out string path))
                {
                    File.WriteAllLines(path, log);
                }
            }
            catch (Exception e)
            {
                Log.Error("[OA AutoTest] Could not write report: " + e);
            }
            if (GenCommandLine.CommandLineArgPassed("oa_autotest_quit"))
            {
                Root.Shutdown();
            }
        }
    }
}
